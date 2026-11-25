//! pager-sim: пейджер гостевой памяти на модели гипервизора, но с настоящей памятью Windows.
//! «Гостевая RAM» — VirtualAlloc, как у QEMU. Два потока-vCPU обращаются к ней; если кластер снят с
//! отображения (модель WHvUnmapGpaRange), поток делает «exit» -> em_pager_mem_exit. Поток-«устройство»
//! пишет в память напрямую, как эмуляция дисков/DMA в QEMU: обращения к вытесненным кластерам ловит VEH.
//! В конце вся память сверяется с эталоном байт в байт.
//!
//! pager-sim.exe [guest_mib=512] [hot_mib=48] [secs=60] [idle_ms=1000]; EM_SIM_SCATTER=1 — горячие страницы
//! вразброс по одной (худший случай для кластеров 64 КиБ).

#[cfg(not(windows))]
fn main() {
    eprintln!("pager-sim рассчитан на Windows (VEH + VirtualAlloc).");
}

#[cfg(windows)]
fn main() {
    use em_pager::win;
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
    use std::sync::Arc;
    use std::time::{Duration, Instant};
    const PAGE: usize = 4096;
    extern "system" {
        fn VirtualAlloc(a: *mut u8, s: usize, t: u32, p: u32) -> *mut u8;
    }
    let a: Vec<u64> = std::env::args().skip(1).filter_map(|s| s.parse().ok()).collect();
    let guest_mib = *a.first().unwrap_or(&512) as usize;
    let hot_mib = *a.get(1).unwrap_or(&48) as usize;
    let secs = *a.get(2).unwrap_or(&60);
    let idle_ms = *a.get(3).unwrap_or(&1000);
    let scatter = std::env::var("EM_SIM_SCATTER").as_deref() == Ok("1");
    for (k, v) in [("EM_PAGER_START_DELAY_S", "0".to_string()), ("EM_PAGER_IDLE_MS", idle_ms.to_string()), ("EM_PAGER_MIN_MIB", "1".to_string())] {
        if std::env::var(k).is_err() {
            std::env::set_var(k, v);
        }
    }
    let pages = guest_mib * 256;
    let bytes = pages * PAGE;
    let ram = unsafe { VirtualAlloc(std::ptr::null_mut(), bytes, 0x3000, 0x04) };
    assert!(!ram.is_null());
    let base = ram as usize;
    let mut x: u64 = 0x9E37_79B9_7F4A_7C15;
    let mut rng = move || {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        x
    };
    // Содержимое, похожее на память гостя: свободные (нулевые) страницы, копии, почти-копии, куча.
    let mem = unsafe { std::slice::from_raw_parts_mut(ram, bytes) };
    for p in 0..pages {
        let kind = rng() % 10;
        if kind < 3 {
            continue;
        }
        if kind < 5 && p > 64 {
            let src = (rng() as usize) % p;
            mem.copy_within(src * PAGE..src * PAGE + PAGE, p * PAGE);
            if kind == 4 {
                for _ in 0..4 {
                    let o = p * PAGE + (rng() as usize % 512) * 8;
                    mem[o] ^= 0x5A;
                }
            }
            continue;
        }
        for i in 0..512 {
            let v: u64 = match i % 4 { 0 => 0x7ff6_1000_0000 + (rng() & 0xffff) * 16, 1 => rng() & 0x3ff, 2 => 0x0065_0074_0061_0064, _ => 0 };
            mem[p * PAGE + i * 8..p * PAGE + i * 8 + 8].copy_from_slice(&v.to_le_bytes());
        }
    }
    let init: Arc<Vec<u8>> = Arc::new(mem.to_vec());
    let gpa0: u64 = 0x1_0000_0000;
    let clp = std::env::var("EM_PAGER_CLUSTER").ok().and_then(|v| v.parse().ok()).unwrap_or(16usize);
    println!("SIM guest_mib={guest_mib} hot_mib={hot_mib} secs={secs} idle_ms={idle_ms} scatter={scatter} cluster_pages={clp}");
    assert_eq!(win::sim_init(gpa0, pages), 0);
    win::em_pager_add_ram(gpa0, ram, bytes as u64, 1);
    let (priv0, _) = win::proc_mem();

    let hot_pages = hot_mib * 256;
    let hot: Arc<Vec<usize>> = Arc::new(if scatter {
        (0..hot_pages).map(|_| rng() as usize % pages).collect()
    } else {
        let mut v = Vec::new();
        while v.len() < hot_pages {
            let s = (rng() as usize % (pages / 16)) * 16;
            v.extend(s..s + 16);
        }
        v
    });
    let stop = Arc::new(AtomicBool::new(false));
    let exits = Arc::new(AtomicU64::new(0));
    let bad_reads = Arc::new(AtomicU64::new(0));
    // слова, которые пишут потоки: vCPU k — смещение 256 + 8k, устройство — смещение 64
    let shadows: Vec<Arc<Vec<AtomicU64>>> = (0..3)
        .map(|t| {
            let off = if t == 2 { 64 } else { 256 + 8 * t };
            Arc::new((0..pages).map(|p| AtomicU64::new(u64::from_le_bytes(init[p * PAGE + off..p * PAGE + off + 8].try_into().unwrap()))).collect())
        })
        .collect();
    let mut threads = Vec::new();
    for t in 0..3usize {
        let (stop, exits, bad, hot, sh) = (stop.clone(), exits.clone(), bad_reads.clone(), hot.clone(), shadows[t].clone());
        threads.push(std::thread::spawn(move || {
            let mut r = 0x1234_5678u64 + t as u64 * 977;
            let mut nx = move || {
                r ^= r << 13;
                r ^= r >> 7;
                r ^= r << 17;
                r
            };
            let off = if t == 2 { 64 } else { 256 + 8 * t };
            let mut n = 0u64;
            while !stop.load(Ordering::Relaxed) {
                // vCPU: горячий набор, изредка случайная страница; «устройство» (DMA-буферы) — тоже в основном горячий набор
                let cold = if t == 2 { nx() % 64 == 0 } else { nx() % 8192 == 0 };
                let p = if cold { nx() as usize % pages } else { hot[nx() as usize % hot.len()] };
                if t < 2 {
                    let gpa = gpa0 + (p * PAGE + off) as u64;
                    if !win::sim_is_mapped(gpa) {
                        exits.fetch_add(1, Ordering::Relaxed);
                        assert_eq!(win::em_pager_mem_exit(gpa, 1), 1, "exit по RAM пейджера должен обрабатываться");
                    }
                }
                let va = (base + p * PAGE + off) as *mut u64;
                let v = unsafe { std::ptr::read_volatile(va) };
                if v != sh[p].load(Ordering::Relaxed) {
                    bad.fetch_add(1, Ordering::Relaxed);
                }
                unsafe { std::ptr::write_volatile(va, v.wrapping_add(1)) };
                sh[p].store(v.wrapping_add(1), Ordering::Relaxed);
                n += 1;
                if t == 2 {
                    std::thread::sleep(Duration::from_millis(5));
                } else if n % 64 == 0 {
                    std::thread::sleep(Duration::from_micros(200));
                }
            }
        }));
    }
    let t0 = Instant::now();
    let mut min_priv = f64::MAX;
    let mut last = String::new();
    while t0.elapsed() < Duration::from_secs(secs) {
        std::thread::sleep(Duration::from_secs(5));
        let (pv, _) = win::proc_mem();
        min_priv = min_priv.min(pv);
        last = win::stats_line();
        println!("STAT {last}");
    }
    stop.store(true, Ordering::SeqCst);
    for t in threads {
        t.join().unwrap();
    }
    win::stop();
    std::thread::sleep(Duration::from_millis(300));
    let final_line = win::stats_line();
    // Проверка без потерь: вся гостевая память против эталона (чтение вытесненного восстановит его через VEH).
    let mut mism = 0u64;
    for p in 0..pages {
        let pg = unsafe { std::slice::from_raw_parts((base + p * PAGE) as *const u8, PAGE) };
        let mut want = init[p * PAGE..p * PAGE + PAGE].to_vec();
        for (t, sh) in shadows.iter().enumerate() {
            let off = if t == 2 { 64 } else { 256 + 8 * t };
            want[off..off + 8].copy_from_slice(&sh[p].load(Ordering::Relaxed).to_le_bytes());
        }
        if pg != &want[..] {
            if mism < 3 {
                let diff: Vec<usize> = (0..PAGE).filter(|&i| pg[i] != want[i]).collect();
                let o = diff[0] & !7;
                println!("MISMATCH page={p} cluster={} diff_bytes={} first_offsets={:?} have={:#x} want={:#x}", p / clp, diff.len(), &diff[..diff.len().min(12)],
                    u64::from_le_bytes(pg[o..o + 8].try_into().unwrap()), u64::from_le_bytes(want[o..o + 8].try_into().unwrap()));
            }
            mism += 1;
        }
    }
    let br = bad_reads.load(Ordering::Relaxed);
    println!("FINAL {final_line}");
    println!("PROC private_mib_start={priv0:.1} private_mib_min={min_priv:.1} guest_mib={guest_mib} (эталон в куче — ещё {guest_mib} МиБ, учтено в start)");
    println!("SIM exits={} last={}", exits.load(Ordering::Relaxed), !last.is_empty());
    let ok = mism == 0 && br == 0;
    println!("LOSSLESS mismatched_pages={mism} wrong_reads={br} verdict={}", if ok { "PASS" } else { "FAIL" });
    std::process::exit(if ok { 0 } else { 1 });
}
