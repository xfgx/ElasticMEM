//! Стенд реального времени: «приложение» целиком работает в сжатой арене. Главный вопрос —
//! можно ли сжимать работающее приложение БЕЗ ПОТЕРЬ и без рывков, пока фоновый компактор
//! параллельно вытесняет холодные страницы, а поток приложения в это же время пишет в память.
//!
//! Проверяем три вещи:
//!   1. Корректность: теневая копия в обычной памяти; после прогона арена == тень байт в байт.
//!   2. Реальное время: время каждого «кадра» (60 Гц) — p50/p99/max, число кадров дольше 16.6 мс.
//!   3. Экономия: резидентные страницы против сжатого хранилища.

#[cfg(not(windows))]
fn main() {
    eprintln!("arena-bench рассчитан на Windows (VEH + VirtualAlloc).");
}

#[cfg(windows)]
fn main() {
    use elastic_arena::{Arena, PageCodec, Rle, Xpress, PAGE};
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
    use std::sync::Arc;
    use std::time::{Duration, Instant};

    // --- параметры «приложения» ---
    let args: Vec<String> = std::env::args().collect();
    let total_mib: usize = args.get(1).and_then(|s| s.parse().ok()).unwrap_or(512);
    let hot_mib: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(48);
    let warm_mib: usize = args.get(3).and_then(|s| s.parse().ok()).unwrap_or(32);
    let secs: u64 = args.get(4).and_then(|s| s.parse().ok()).unwrap_or(40);

    let pages = total_mib * 1024 * 1024 / PAGE;
    let hot_pages = hot_mib * 1024 * 1024 / PAGE;
    let warm_pages = warm_mib * 1024 * 1024 / PAGE;
    let quota = total_mib * 1024 * 1024; // хранилище не больше логического размера

    let codec_name = std::env::var("EM_CODEC").unwrap_or_else(|_| "xpress".into());
    println!(
        "OS windows arena total_mib={total_mib} hot_mib={hot_mib} warm_mib={warm_mib} secs={secs} codec={codec_name} pages={pages}"
    );

    let codec: Box<dyn PageCodec> = if codec_name == "rle" {
        Box::new(Rle)
    } else {
        Box::new(Xpress)
    };
    let arena = Arc::new(Arena::new(codec, pages, quota).expect("arena"));
    let base = arena.base() as usize;

    // Теневая копия: эталон содержимого для проверки без потерь.
    let mut shadow = vec![0u8; pages * PAGE];

    // Детерминированный xorshift.
    let mut x: u64 = 0x9E37_79B9_7F4A_7C15;
    let mut rng = || {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        x
    };

    // Инициализация: записать «похожие на кучу» байты и в арену, и в тень.
    for p in 0..pages {
        let va = (base + p * PAGE) as *mut u8;
        let page = unsafe { std::slice::from_raw_parts_mut(va, PAGE) };
        let sh = &mut shadow[p * PAGE..p * PAGE + PAGE];
        let kind = p % 4;
        for i in 0..PAGE / 8 {
            let w: u64 = match kind {
                0 => (i as u64 * 7 + p as u64) & 1023,
                1 => {
                    if i & 1 == 0 {
                        0x0000_7ff6_0000_0000
                    } else {
                        0x1000_0000 + ((p as u64 * 1024 + i as u64) * 16 & 0xfffff)
                    }
                }
                2 => 0x2020_2020_2020_2020 ^ (rng() & 0x1f1f_1f1f_1f1f_1f1f),
                _ => {
                    if i % 16 == 0 {
                        rng()
                    } else {
                        0
                    }
                }
            };
            let b = w.to_le_bytes();
            page[i * 8..i * 8 + 8].copy_from_slice(&b);
            sh[i * 8..i * 8 + 8].copy_from_slice(&b);
        }
    }

    // Горячие и тёплые страницы разбросаны по арене.
    let mut perm: Vec<u32> = (0..pages as u32).collect();
    for i in (1..pages).rev() {
        let j = (rng() as usize) % (i + 1);
        perm.swap(i, j);
    }
    let hot: Vec<usize> = perm[..hot_pages].iter().map(|&v| v as usize).collect();
    let warm: Vec<usize> = perm[hot_pages..hot_pages + warm_pages]
        .iter()
        .map(|&v| v as usize)
        .collect();

    // Фоновый компактор: вытесняет холодные страницы небольшими порциями, как реальный агент.
    let stop = Arc::new(AtomicBool::new(false));
    let evicted = Arc::new(AtomicU64::new(0));
    let compactor = {
        let arena = arena.clone();
        let stop = stop.clone();
        let evicted = evicted.clone();
        std::thread::spawn(move || {
            while !stop.load(Ordering::Relaxed) {
                let n = arena.compact(2048);
                evicted.fetch_add(n as u64, Ordering::Relaxed);
                std::thread::sleep(Duration::from_millis(50));
            }
        })
    };

    // Поток приложения: 60 Гц. Каждый кадр трогает горячие страницы (чтение+запись), раз в 4 с — тёплые.
    let mut frames: Vec<f64> = Vec::with_capacity((secs * 70) as usize);
    let start = Instant::now();
    let mut next = Instant::now();
    let mut last_warm = Instant::now();
    let mut k: u64 = 0;
    let mut sink: u64 = 0;
    while start.elapsed() < Duration::from_secs(secs) {
        let t0 = Instant::now();
        for (idx, &p) in hot.iter().enumerate() {
            let off = ((k as usize + idx) * 8) & (PAGE - 8);
            let va = (base + p * PAGE + off) as *mut u64;
            unsafe {
                sink = sink.wrapping_add(*va); // чтение (может вызвать распаковку)
                let nv = (*va).wrapping_add(1);
                *va = nv; // запись (может прийтись на страницу в момент вытеснения)
            }
            // отразить запись в тень
            let s = &mut shadow[p * PAGE + off..p * PAGE + off + 8];
            let cur = u64::from_le_bytes(s.try_into().unwrap()).wrapping_add(1);
            s.copy_from_slice(&cur.to_le_bytes());
        }
        if last_warm.elapsed() > Duration::from_secs(4) {
            for &p in &warm {
                let va = (base + p * PAGE + 64) as *mut u64;
                unsafe {
                    sink = sink.wrapping_add(*va);
                }
            }
            last_warm = Instant::now();
        }
        let dt = t0.elapsed().as_secs_f64() * 1000.0;
        frames.push(dt);
        k += 1;
        next += Duration::from_micros(16_667);
        let now = Instant::now();
        if next > now {
            std::thread::sleep(next - now);
        } else {
            next = now;
        }
    }

    stop.store(true, Ordering::Relaxed);
    compactor.join().ok();
    std::hint::black_box(sink);

    // Снимок состояния ДО проверки: сама проверка читает все страницы и распакует их обратно в резидент.
    let st = arena.stats();

    // --- проверка без потерь: вся арена против тени ---
    let mut mism = 0u64;
    let mut first_bad = usize::MAX;
    for p in 0..pages {
        let va = (base + p * PAGE) as *const u8;
        let page = unsafe { std::slice::from_raw_parts(va, PAGE) };
        let sh = &shadow[p * PAGE..p * PAGE + PAGE];
        if page != sh {
            mism += 1;
            if first_bad == usize::MAX {
                first_bad = p;
            }
        }
    }

    frames.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let q = |f: f64| frames[((frames.len() as f64 * f) as usize).min(frames.len() - 1)];
    let over16 = frames.iter().filter(|&&d| d > 16.667).count();
    let over50 = frames.iter().filter(|&&d| d > 50.0).count();

    println!(
        "FRAMES n={} p50_ms={:.3} p99_ms={:.3} p999_ms={:.3} max_ms={:.3} over16ms={} over50ms={}",
        frames.len(),
        q(0.50),
        q(0.99),
        q(0.999),
        frames[frames.len() - 1],
        over16,
        over50
    );
    println!(
        "MEM resident_mib={:.1} compressed_mib={:.1} zero_mib={:.1} pinned_mib={:.1} store_mib={:.1} payload_mib={:.1} ratio={:.3}",
        (st.resident_pages * PAGE) as f64 / 1048576.0,
        (st.compressed_pages * PAGE) as f64 / 1048576.0,
        (st.zero_pages * PAGE) as f64 / 1048576.0,
        (st.pinned_pages * PAGE) as f64 / 1048576.0,
        st.store_bytes as f64 / 1048576.0,
        st.payload_bytes as f64 / 1048576.0,
        if st.payload_bytes > 0 {
            st.store_bytes as f64 / (st.compressed_pages.max(1) * PAGE) as f64
        } else {
            0.0
        }
    );
    println!(
        "ENGINE faults={} decompressed={} evictions={} evicted_by_compactor={}",
        st.faults,
        st.decompressed,
        st.evictions,
        evicted.load(Ordering::Relaxed)
    );
    println!(
        "LOSSLESS mismatched_pages={} first_bad_page={} verdict={}",
        mism,
        if first_bad == usize::MAX {
            -1i64
        } else {
            first_bad as i64
        },
        if mism == 0 { "PASS" } else { "FAIL" }
    );
    if mism != 0 {
        std::process::exit(1);
    }
}
