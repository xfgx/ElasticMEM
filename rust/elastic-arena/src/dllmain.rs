//! Точка входа внедрённой DLL. При загрузке в чужой процесс (через лаунчер) читает переменные окружения,
//! ставит перехват аллокатора и запускает фоновый компактор. Управляется переменными:
//!   EM_ARENA_HOOK=1        — включить перехват;
//!   EM_ARENA_MIN_MB=8      — минимальный размер выделения для обслуживания ареной;
//!   EM_ARENA_IDLE_MS=1000  — сколько страница должна простаивать до сжатия;
//!   EM_ARENA_REPORT=путь   — куда писать отчёт (строки key=value).
//! В DllMain делаем минимум (патч IAT + CreateThread): тяжёлую работу ведёт отдельный поток, вне loader lock.

use crate::hook;
use std::ffi::c_void;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

const DLL_PROCESS_ATTACH: u32 = 1;

extern "system" {
    fn CreateThread(
        attrs: *mut c_void,
        stack: usize,
        start: unsafe extern "system" fn(*mut c_void) -> u32,
        param: *mut c_void,
        flags: u32,
        tid: *mut u32,
    ) -> *mut c_void;
}

static PATCHED: AtomicUsize = AtomicUsize::new(0);
static PEAK_COMPRESSED: AtomicU64 = AtomicU64::new(0); // МиБ×10, пик за всё время
static PEAK_SAVED: AtomicU64 = AtomicU64::new(0);      // % ×10 при пике

unsafe extern "system" fn worker(_: *mut c_void) -> u32 {
    let idle_ms: u64 = std::env::var("EM_ARENA_IDLE_MS")
        .ok()
        .and_then(|s| s.parse().ok())
        .unwrap_or(1000);
    let report = std::env::var("EM_ARENA_REPORT").ok();
    loop {
        let compressed = hook::compact_all(1024, idle_ms);
        if let Some(ref path) = report {
            let (regions, logical, resident, comp, store, served, passed) = hook::summary();
            let physical = resident + store;
            let saved = if logical > 0.0 { 100.0 * (1.0 - physical / logical) } else { 0.0 };
            // Пик сжатия за весь прогон: финальная сверка в приложении распаковывает всё обратно,
            // поэтому мгновенный снимок в конце занижает экономию. Пик отражает установившийся режим.
            if (comp * 10.0) as u64 > PEAK_COMPRESSED.load(Ordering::Relaxed) {
                PEAK_COMPRESSED.store((comp * 10.0) as u64, Ordering::Relaxed);
                PEAK_SAVED.store((saved.max(0.0) * 10.0) as u64, Ordering::Relaxed);
            }
            let text = format!(
                "hook_installed=1\npatched_iat_entries={}\nserved_allocs={}\npassed_allocs={}\nregions={}\nlogical_mib={:.1}\nresident_mib={:.1}\ncompressed_mib={:.1}\nstore_mib={:.1}\nphysical_mib={:.1}\nsaved_pct={:.1}\npeak_compressed_mib={:.1}\npeak_saved_pct={:.1}\nlast_compacted_pages={}\n",
                PATCHED.load(Ordering::Relaxed),
                served,
                passed,
                regions,
                logical,
                resident,
                comp,
                store,
                physical,
                saved,
                PEAK_COMPRESSED.load(Ordering::Relaxed) as f64 / 10.0,
                PEAK_SAVED.load(Ordering::Relaxed) as f64 / 10.0,
                compressed
            );
            let _ = std::fs::write(path, text);
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
}

#[no_mangle]
pub unsafe extern "system" fn DllMain(_module: *mut c_void, reason: u32, _reserved: *mut c_void) -> i32 {
    if reason == DLL_PROCESS_ATTACH {
        if std::env::var("EM_ARENA_HOOK").as_deref() == Ok("1") {
            let min_mb: usize = std::env::var("EM_ARENA_MIN_MB")
                .ok()
                .and_then(|s| s.parse().ok())
                .unwrap_or(8);
            let patched = hook::install(min_mb * 1024 * 1024);
            PATCHED.store(patched, Ordering::Relaxed);
            CreateThread(
                std::ptr::null_mut(),
                0,
                worker,
                std::ptr::null_mut(),
                0,
                std::ptr::null_mut(),
            );
        }
    }
    1
}
