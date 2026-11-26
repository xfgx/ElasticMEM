//! Источники страниц: живые процессы (Windows / Linux, только резидентные страницы), полный минидамп,
//! сырой образ (например, память гостя), синтетика. Всё читается локально; наружу уходят только агрегаты.

use std::io::{Read, Seek, SeekFrom};

pub const PAGE: usize = 4096;

/// Получатель страниц: (источник, адрес, байты).
pub type Sink<'a> = dyn FnMut(u32, u64, &[u8]) + 'a;

pub struct Opts {
    pub private_only: bool,
    pub max_bytes: u64,
    /// Для --raw: диапазоны смещений [a, b), которых нет в образе (например, дыры физической памяти,
    /// забитые нулями при снятии дампа). Такие страницы не считаются.
    pub skip: Vec<(u64, u64)>,
}

// ------------------------------ сырой образ / минидамп ------------------------------

pub fn raw(path: &str, o: &Opts, sink: &mut Sink) -> std::io::Result<u64> {
    let mut f = std::fs::File::open(path)?;
    let mut buf = vec![0u8; 1 << 20];
    let (mut addr, mut n) = (0u64, 0u64);
    loop {
        let got = read_full(&mut f, &mut buf)?;
        for p in buf[..got - got % PAGE].chunks_exact(PAGE) {
            if n * PAGE as u64 >= o.max_bytes {
                return Ok(n);
            }
            if !o.skip.iter().any(|&(a, b)| addr >= a && addr < b) {
                sink(0, addr, p);
                n += 1;
            }
            addr += PAGE as u64;
        }
        if got < buf.len() {
            return Ok(n);
        }
    }
}

fn read_full(f: &mut std::fs::File, b: &mut [u8]) -> std::io::Result<usize> {
    let mut got = 0;
    while got < b.len() {
        let n = f.read(&mut b[got..])?;
        if n == 0 {
            break;
        }
        got += n;
    }
    Ok(got)
}

/// Полный минидамп (MiniDumpWithFullMemory): поток Memory64ListStream (тип 9).
pub fn minidump(path: &str, o: &Opts, sink: &mut Sink) -> std::io::Result<u64> {
    let mut f = std::fs::File::open(path)?;
    let mut h = [0u8; 32];
    f.read_exact(&mut h)?;
    if &h[0..4] != b"MDMP" {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "not a minidump"));
    }
    let nstreams = u32::from_le_bytes(h[8..12].try_into().unwrap());
    let dir = u32::from_le_bytes(h[12..16].try_into().unwrap());
    f.seek(SeekFrom::Start(dir as u64))?;
    let mut d = vec![0u8; nstreams as usize * 12];
    f.read_exact(&mut d)?;
    let mem64 = d.chunks_exact(12).find(|e| u32::from_le_bytes(e[0..4].try_into().unwrap()) == 9);
    let Some(e) = mem64 else {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "no Memory64ListStream: нужен MiniDumpWithFullMemory"));
    };
    let rva = u32::from_le_bytes(e[8..12].try_into().unwrap());
    f.seek(SeekFrom::Start(rva as u64))?;
    let mut hh = [0u8; 16];
    f.read_exact(&mut hh)?;
    let nr = u64::from_le_bytes(hh[0..8].try_into().unwrap());
    let mut data_at = u64::from_le_bytes(hh[8..16].try_into().unwrap());
    let mut ranges = vec![0u8; nr as usize * 16];
    f.read_exact(&mut ranges)?;
    let mut n = 0u64;
    let mut buf = vec![0u8; PAGE];
    for r in ranges.chunks_exact(16) {
        let start = u64::from_le_bytes(r[0..8].try_into().unwrap());
        let size = u64::from_le_bytes(r[8..16].try_into().unwrap());
        f.seek(SeekFrom::Start(data_at))?;
        for k in 0..size / PAGE as u64 {
            if n * PAGE as u64 >= o.max_bytes {
                return Ok(n);
            }
            f.read_exact(&mut buf)?;
            sink(0, start + k * PAGE as u64, &buf);
            n += 1;
        }
        data_at += size;
    }
    Ok(n)
}

/// Синтетика, похожая на кучу (для самопроверки): нули, копии, почти-копии, указатели, текст, шум.
pub fn synthetic(mib: u64, seed: u64, sink: &mut Sink) -> u64 {
    let mut x = seed | 1;
    let mut rng = move || {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        x
    };
    let pages = mib * 256;
    let mut prev: Vec<Vec<u8>> = Vec::new();
    for i in 0..pages {
        let kind = rng() % 10;
        let mut p = vec![0u8; PAGE];
        match kind {
            0..=2 => {}
            3 | 4 if !prev.is_empty() => {
                p = prev[rng() as usize % prev.len()].clone();
                if kind == 4 {
                    for _ in 0..4 {
                        let o = (rng() as usize % 512) * 8;
                        p[o] ^= 0x5A;
                    }
                }
            }
            _ => {
                for (k, w) in p.chunks_exact_mut(8).enumerate() {
                    let v = match (kind + k as u64) % 4 { 0 => 0x7ff6_1000_0000 + (rng() & 0xffff) * 16, 1 => rng() & 0x3ff, 2 => 0x0065_0074_0061_0064 ^ (rng() & 0x1f), _ => 0 };
                    w.copy_from_slice(&v.to_le_bytes());
                }
            }
        }
        sink(0, i * PAGE as u64, &p);
        if prev.len() < 4096 {
            prev.push(p);
        } else {
            let k = rng() as usize % prev.len();
            prev[k] = p;
        }
    }
    pages
}

// ------------------------------ живые процессы ------------------------------

#[cfg(windows)]
pub mod live {
    use super::*;

    #[repr(C)]
    struct Mbi { base: usize, alloc_base: usize, alloc_protect: u32, partition: u16, size: usize, state: u32, protect: u32, typ: u32 }
    #[repr(C)]
    #[derive(Clone, Copy)]
    struct WsEx { va: usize, attr: usize }
    #[repr(C)]
    struct Pe32 { size: u32, usage: u32, pid: u32, heap: usize, module: u32, threads: u32, parent: u32, prio: i32, flags: u32, exe: [u16; 260] }

    extern "system" {
        fn OpenProcess(access: u32, inherit: i32, pid: u32) -> isize;
        fn CloseHandle(h: isize) -> i32;
        fn VirtualQueryEx(h: isize, a: usize, m: *mut Mbi, n: usize) -> usize;
        fn ReadProcessMemory(h: isize, a: usize, b: *mut u8, n: usize, got: *mut usize) -> i32;
        fn K32QueryWorkingSetEx(h: isize, b: *mut WsEx, n: u32) -> i32;
        fn CreateToolhelp32Snapshot(flags: u32, pid: u32) -> isize;
        fn Process32FirstW(h: isize, e: *mut Pe32) -> i32;
        fn Process32NextW(h: isize, e: *mut Pe32) -> i32;
    }

    pub fn pids_by_name(name: &str) -> Vec<u32> {
        let want = name.to_lowercase().trim_end_matches(".exe").to_string();
        let mut out = Vec::new();
        unsafe {
            let s = CreateToolhelp32Snapshot(2, 0);
            if s == -1 {
                return out;
            }
            let mut e = Pe32 { size: std::mem::size_of::<Pe32>() as u32, usage: 0, pid: 0, heap: 0, module: 0, threads: 0, parent: 0, prio: 0, flags: 0, exe: [0; 260] };
            let mut ok = Process32FirstW(s, &mut e);
            while ok != 0 {
                let n = e.exe.iter().position(|&c| c == 0).unwrap_or(260);
                let exe = String::from_utf16_lossy(&e.exe[..n]).to_lowercase();
                if exe.trim_end_matches(".exe") == want {
                    out.push(e.pid);
                }
                ok = Process32NextW(s, &mut e);
            }
            CloseHandle(s);
        }
        out
    }

    /// Резидентные страницы процесса (QueryWorkingSetEx: только Valid), без подкачки остальных.
    pub fn process(pid: u32, o: &Opts, budget: &mut u64, sink: &mut Sink) -> std::io::Result<u64> {
        const MEM_COMMIT: u32 = 0x1000;
        const MEM_PRIVATE: u32 = 0x20000;
        let h = unsafe { OpenProcess(0x0400 | 0x0010, 0, pid) }; // QUERY_INFORMATION | VM_READ
        if h == 0 {
            return Err(std::io::Error::last_os_error());
        }
        let mut a = 0usize;
        let mut n = 0u64;
        let mut buf = vec![0u8; PAGE];
        let mut ws: Vec<WsEx> = Vec::new();
        let (mut regions, mut ws_fail, mut not_resident) = (0u64, 0u64, 0u64);
        loop {
            let mut m = Mbi { base: 0, alloc_base: 0, alloc_protect: 0, partition: 0, size: 0, state: 0, protect: 0, typ: 0 };
            if unsafe { VirtualQueryEx(h, a, &mut m, std::mem::size_of::<Mbi>()) } == 0 {
                break;
            }
            let readable = m.protect & 0x01 == 0 && m.protect & 0x100 == 0 && m.protect != 0; // не NOACCESS / GUARD
            if m.state == MEM_COMMIT && readable && (!o.private_only || m.typ == MEM_PRIVATE) {
                regions += 1;
                let pages = m.size / PAGE;
                let mut i = 0;
                while i < pages {
                    let chunk = (pages - i).min(4096);
                    ws.clear();
                    ws.extend((0..chunk).map(|k| WsEx { va: m.base + (i + k) * PAGE, attr: 0 }));
                    let filt = unsafe { K32QueryWorkingSetEx(h, ws.as_mut_ptr(), (chunk * std::mem::size_of::<WsEx>()) as u32) } != 0;
                    if !filt {
                        ws_fail += 1; // фильтр недоступен (например, Wine) — читаем все закоммиченные страницы
                    }
                    {
                        for w in &ws {
                            if filt && w.attr & 1 == 0 {
                                not_resident += 1;
                                continue; // не в рабочем наборе — не трогаем, чтобы не подкачивать
                            }
                            if *budget < PAGE as u64 {
                                unsafe { CloseHandle(h) };
                                return Ok(n);
                            }
                            let mut got = 0;
                            if unsafe { ReadProcessMemory(h, w.va, buf.as_mut_ptr(), PAGE, &mut got) } != 0 && got == PAGE {
                                sink(pid, w.va as u64, &buf);
                                *budget -= PAGE as u64;
                                n += 1;
                            }
                        }
                    }
                    i += chunk;
                }
            }
            a = m.base + m.size;
            if a == 0 {
                break;
            }
        }
        unsafe { CloseHandle(h) };
        if std::env::var("EM_PAGESTAT_DEBUG").is_ok() {
            eprintln!("pid {pid}: regions={regions} ws_query_failures={ws_fail} not_resident={not_resident} read={n}");
        }
        Ok(n)
    }
}

#[cfg(target_os = "linux")]
pub mod live {
    use super::*;

    pub fn pids_by_name(name: &str) -> Vec<u32> {
        let mut out = Vec::new();
        if let Ok(rd) = std::fs::read_dir("/proc") {
            for e in rd.flatten() {
                if let Ok(pid) = e.file_name().to_string_lossy().parse::<u32>() {
                    if let Ok(c) = std::fs::read_to_string(format!("/proc/{pid}/comm")) {
                        if c.trim() == name {
                            out.push(pid);
                        }
                    }
                }
            }
        }
        out
    }

    /// Резидентные страницы (бит present в /proc/pid/pagemap); private_only — анонимные отображения.
    pub fn process(pid: u32, o: &Opts, budget: &mut u64, sink: &mut Sink) -> std::io::Result<u64> {
        let maps = std::fs::read_to_string(format!("/proc/{pid}/maps"))?;
        let mut mem = std::fs::File::open(format!("/proc/{pid}/mem"))?;
        let mut pm = std::fs::File::open(format!("/proc/{pid}/pagemap")).ok();
        let mut n = 0u64;
        let mut buf = vec![0u8; PAGE];
        for l in maps.lines() {
            let mut it = l.split_whitespace();
            let (Some(range), Some(perms)) = (it.next(), it.next()) else { continue };
            let path = it.nth(3).unwrap_or("");
            if !perms.starts_with('r') || path == "[vvar]" || path == "[vsyscall]" {
                continue;
            }
            if o.private_only && !(path.is_empty() || path == "[heap]" || path == "[stack]") {
                continue;
            }
            let (a, b) = range.split_once('-').unwrap();
            let (a, b) = (u64::from_str_radix(a, 16).unwrap(), u64::from_str_radix(b, 16).unwrap());
            let mut va = a;
            while va < b {
                if let Some(p) = pm.as_mut() {
                    let mut e = [0u8; 8];
                    if p.seek(SeekFrom::Start(va / PAGE as u64 * 8)).is_ok() && p.read_exact(&mut e).is_ok() && u64::from_le_bytes(e) >> 63 == 0 {
                        va += PAGE as u64;
                        continue;
                    }
                }
                if *budget < PAGE as u64 {
                    return Ok(n);
                }
                if mem.seek(SeekFrom::Start(va)).is_ok() && mem.read_exact(&mut buf).is_ok() {
                    sink(pid, va, &buf);
                    *budget -= PAGE as u64;
                    n += 1;
                }
                va += PAGE as u64;
            }
        }
        Ok(n)
    }
}
