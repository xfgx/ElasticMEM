//! view-probe: можно ли освобождать физическую память страниц, если арена держит данные в разделе
//! (pagefile-backed section) с двумя видами — «вид приложения» и «вид заполнения».
//!
//! Зачем: сейчас арена восстанавливает страницу так: VirtualAlloc(MEM_COMMIT) и копирование. Свежезакоммиченная
//! страница сразу доступна всем потокам, и в окне до конца копирования чужой поток может прочитать нули.
//! С двумя видами окна нет: вид приложения остаётся PAGE_NOACCESS (чужие потоки уходят в VEH и ждут), данные
//! пишутся через вид заполнения, и только потом виду приложения возвращается доступ.
//! Вопрос — чем тогда отдавать физическую память холодных страниц (декоммит у видов раздела невозможен).
//! Проверяем DiscardVirtualMemory, MEM_RESET и OfferVirtualMemory по счётчикам ОС и проверяем согласованность видов.

#[cfg(not(windows))]
fn main() {
    eprintln!("view-probe рассчитан на Windows.");
}

#[cfg(windows)]
fn main() {
    const PAGE: usize = 4096;
    #[repr(C)]
    #[derive(Default)]
    struct Pmc { cb: u32, faults: u32, peak_ws: usize, ws: usize, a: usize, b: usize, c: usize, d: usize, pf: usize, peak_pf: usize, private: usize }
    #[repr(C)]
    #[derive(Default)]
    struct Perf { cb: u32, commit_total: usize, commit_limit: usize, commit_peak: usize, phys_total: usize, phys_avail: usize, sys_cache: usize, kernel_total: usize, kernel_paged: usize, kernel_nonpaged: usize, page_size: usize, handles: u32, procs: u32, threads: u32 }
    extern "system" {
        fn CreateFileMappingW(f: isize, sa: *const u8, prot: u32, hi: u32, lo: u32, name: *const u16) -> isize;
        fn MapViewOfFile(h: isize, access: u32, hi: u32, lo: u32, n: usize) -> *mut u8;
        fn VirtualProtect(a: *mut u8, n: usize, p: u32, old: *mut u32) -> i32;
        fn VirtualAlloc(a: *mut u8, n: usize, t: u32, p: u32) -> *mut u8;
        fn DiscardVirtualMemory(a: *mut u8, n: usize) -> u32;
        fn OfferVirtualMemory(a: *mut u8, n: usize, prio: u32) -> u32;
        fn ReclaimVirtualMemory(a: *const u8, n: usize) -> u32;
        fn VirtualUnlock(a: *mut u8, n: usize) -> i32;
        fn GetCurrentProcess() -> isize;
        fn K32GetProcessMemoryInfo(h: isize, p: *mut Pmc, cb: u32) -> i32;
        fn K32GetPerformanceInfo(p: *mut Perf, cb: u32) -> i32;
        fn GetLastError() -> u32;
        fn Sleep(ms: u32);
    }
    let mib: usize = std::env::args().nth(1).and_then(|s| s.parse().ok()).unwrap_or(512);
    let n = mib << 20;
    let snap = |tag: &str| {
        let mut p = Pmc { cb: std::mem::size_of::<Pmc>() as u32, ..Default::default() };
        let mut q = Perf { cb: std::mem::size_of::<Perf>() as u32, ..Default::default() };
        unsafe {
            K32GetProcessMemoryInfo(GetCurrentProcess(), &mut p, p.cb);
            K32GetPerformanceInfo(&mut q, q.cb);
        }
        let (ws, private, avail, commit) = (p.ws >> 20, p.private >> 20, q.phys_avail * q.page_size >> 20, q.commit_total * q.page_size >> 20);
        println!("SNAP {tag:<28} ws_mib={ws} private_mib={private} sys_avail_mib={avail} sys_commit_mib={commit}");
        (ws as i64, avail as i64, commit as i64)
    };
    let fill = |v: *mut u8, seed: u64| {
        for p in 0..n / PAGE {
            for k in 0..8 {
                unsafe { *(v.add(p * PAGE + k * 512) as *mut u64) = seed ^ (p as u64 * 8 + k as u64) };
            }
        }
    };
    let check = |v: *const u8, seed: u64| -> usize {
        (0..n / PAGE).filter(|&p| (0..8).any(|k| unsafe { *(v.add(p * PAGE + k * 512) as *const u64) } != seed ^ (p as u64 * 8 + k as u64))).count()
    };
    let methods = ["discard", "mem_reset", "offer", "unlock_only"];
    let mut verdict_ok = true;
    for m in methods {
        let h = unsafe { CreateFileMappingW(-1, std::ptr::null(), 0x04, (n >> 32) as u32, n as u32, std::ptr::null()) };
        assert!(h != 0);
        let app = unsafe { MapViewOfFile(h, 0x0002 | 0x0004, 0, 0, n) };
        let fil = unsafe { MapViewOfFile(h, 0x0002 | 0x0004, 0, 0, n) };
        assert!(!app.is_null() && !fil.is_null());
        println!("--- method={m} mib={mib}");
        fill(app, 0x1111);
        let mis_alias = check(fil, 0x1111);
        let (w0, a0, c0) = snap("written via app view");
        let mut old = 0;
        unsafe { VirtualProtect(app, n, 0x01, &mut old) }; // app view: PAGE_NOACCESS
        let rc = unsafe {
            match m {
                "discard" => DiscardVirtualMemory(fil, n),
                "mem_reset" => { if VirtualAlloc(fil, n, 0x80000, 0x04).is_null() { GetLastError() } else { 0 } }
                "offer" => OfferVirtualMemory(fil, n, 1),
                _ => 0,
            }
        };
        unsafe {
            VirtualUnlock(fil, n); // убрать из рабочего набора (обычно возвращает ошибку 158 — так и задумано)
            VirtualUnlock(app, n);
            Sleep(500);
        }
        let (w1, a1, c1) = snap("after release");
        println!("RELEASE method={m} rc={rc} ws_freed_mib={} sys_avail_gain_mib={} sys_commit_delta_mib={}", w0 - w1, a1 - a0, c1 - c0);
        if m == "offer" {
            unsafe { ReclaimVirtualMemory(fil, n) };
        }
        // восстановление: пишем через вид заполнения, приложению возвращаем доступ, читаем через вид приложения
        fill(fil, 0x2222);
        unsafe { VirtualProtect(app, n, 0x04, &mut old) };
        let mis = check(app, 0x2222);
        let (_w2, a2, _c2) = snap("refilled via filler view");
        println!("COHERENT method={m} alias_mismatch={mis_alias} after_refill_mismatch={mis} refill_cost_avail_mib={}", a1 - a2);
        if mis != 0 || mis_alias != 0 {
            verdict_ok = false;
        }
        extern "system" {
            fn UnmapViewOfFile(a: *const u8) -> i32;
            fn CloseHandle(h: isize) -> i32;
        }
        unsafe {
            UnmapViewOfFile(app);
            UnmapViewOfFile(fil);
            CloseHandle(h);
        }
    }
    println!("VIEWS coherent={}", if verdict_ok { "PASS" } else { "FAIL" });
    std::process::exit(if verdict_ok { 0 } else { 1 });
}
