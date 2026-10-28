//! Синтетическое «чужое приложение» для сквозной проверки перехвата. Ждёт появления elastic_arena.dll,
//! затем выделяет большой буфер ЧЕРЕЗ VirtualAlloc (как движок игры под свой пул), записывает узнаваемый
//! узор и в буфер, и в теневую копию (обычная куча, не перехватывается), крутит 60 Гц цикл по горячему
//! подмножеству и в конце сверяет буфер с тенью байт в байт. Приложение НЕ знает об арене.

#[cfg(not(windows))]
fn main() {
    eprintln!("hooktarget работает только на Windows.");
}

#[cfg(windows)]
fn main() {
    use std::ffi::c_void;
    use std::time::{Duration, Instant};

    extern "system" {
        fn VirtualAlloc(addr: *mut c_void, size: usize, typ: u32, prot: u32) -> *mut c_void;
        fn GetModuleHandleW(name: *const u16) -> *mut c_void;
    }
    const MEM_COMMIT_RESERVE: u32 = 0x3000;
    const PAGE_READWRITE: u32 = 0x04;

    let args: Vec<String> = std::env::args().collect();
    let size_mb: usize = args.get(1).and_then(|s| s.parse().ok()).unwrap_or(256);
    let hot_mb: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(32);
    let secs: u64 = args.get(3).and_then(|s| s.parse().ok()).unwrap_or(20);
    let size = size_mb * 1024 * 1024;
    let pages = size / 4096;
    let hot_pages = hot_mb * 1024 * 1024 / 4096;

    // Дождаться, пока лаунчер внедрит DLL и поставит перехват (детерминированно, без гонки по времени).
    let dllw: Vec<u16> = "elastic_arena.dll".encode_utf16().chain(std::iter::once(0)).collect();
    let mut waited = 0;
    let injected = loop {
        if unsafe { !GetModuleHandleW(dllw.as_ptr()).is_null() } {
            break true;
        }
        if waited > 8000 {
            break false;
        }
        std::thread::sleep(Duration::from_millis(50));
        waited += 50;
    };
    println!("dll_injected={}", injected as u8);

    let buf = unsafe { VirtualAlloc(std::ptr::null_mut(), size, MEM_COMMIT_RESERVE, PAGE_READWRITE) };
    if buf.is_null() {
        println!("alloc_failed=1");
        std::process::exit(2);
    }
    let buf = buf as *mut u8;
    println!("alloc_base={:p} size_mib={}", buf, size_mb);

    let mut shadow = vec![0u8; size];
    let mut x: u64 = 0x1234_5678_9abc_def1;
    let mut rng = || {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        x
    };
    // Узор «похожий на кучу» в буфер и тень.
    for p in 0..pages {
        let kind = p % 4;
        for i in 0..512 {
            let w: u64 = match kind {
                0 => (i as u64 * 7 + p as u64) & 1023,
                1 => if i & 1 == 0 { 0x7ff6_0000 } else { 0x1000_0000 + ((p as u64 * 512 + i as u64) & 0xffff) },
                2 => 0x2020_2020_2020_2020 ^ (rng() & 0x1f1f_1f1f),
                _ => if i % 16 == 0 { rng() } else { 0 },
            };
            let off = p * 4096 + i * 8;
            unsafe { *(buf.add(off) as *mut u64) = w };
            shadow[off..off + 8].copy_from_slice(&w.to_le_bytes());
        }
    }

    // Горячее подмножество разбросано по буферу.
    let mut perm: Vec<u32> = (0..pages as u32).collect();
    for k in (1..pages).rev() {
        let j = (rng() as usize) % (k + 1);
        perm.swap(k, j);
    }
    let hot: Vec<usize> = perm[..hot_pages].iter().map(|&v| v as usize).collect();

    let mut frames: Vec<f64> = Vec::new();
    let start = Instant::now();
    let mut next = Instant::now();
    let mut k: u64 = 0;
    let mut sink: u64 = 0;
    while start.elapsed() < Duration::from_secs(secs) {
        let t0 = Instant::now();
        for (idx, &p) in hot.iter().enumerate() {
            let off = p * 4096 + (((k as usize + idx) * 8) & 4088);
            unsafe {
                let v = (buf.add(off) as *mut u64).read();
                sink = sink.wrapping_add(v);
                (buf.add(off) as *mut u64).write(v.wrapping_add(1));
            }
            let cur = u64::from_le_bytes(shadow[off..off + 8].try_into().unwrap()).wrapping_add(1);
            shadow[off..off + 8].copy_from_slice(&cur.to_le_bytes());
        }
        frames.push(t0.elapsed().as_secs_f64() * 1000.0);
        k += 1;
        next += Duration::from_micros(16_667);
        let now = Instant::now();
        if next > now { std::thread::sleep(next - now); } else { next = now; }
    }
    std::hint::black_box(sink);

    // Сверка без потерь.
    let mut mism = 0u64;
    for off in 0..size {
        if unsafe { *buf.add(off) } != shadow[off] {
            mism += 1;
        }
    }
    frames.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let q = |f: f64| frames[((frames.len() as f64 * f) as usize).min(frames.len() - 1)];
    println!(
        "FRAMES n={} p50_ms={:.3} p99_ms={:.3} max_ms={:.3} over16ms={}",
        frames.len(),
        q(0.50),
        q(0.99),
        frames[frames.len() - 1],
        frames.iter().filter(|&&d| d > 16.667).count()
    );
    println!("LOSSLESS mismatched_bytes={} verdict={}", mism, if mism == 0 { "PASS" } else { "FAIL" });
    if mism != 0 {
        std::process::exit(1);
    }
}
