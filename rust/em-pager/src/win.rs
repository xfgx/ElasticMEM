//! Привязка em-pager к Windows/WHP: C ABI для прокси WinHvPlatform.dll, VEH для обращений самого VMM,
//! фоновый компактор с бюджетом проб по доле возвратов и журнал с реальными счётчиками памяти процесса.
//! Переменные окружения: EM_PAGER_CLUSTER (страниц в кластере, 16), EM_PAGER_IDLE_MS (2000),
//! EM_PAGER_MIN_MIB (64), EM_PAGER_GHOST (0/1), EM_PAGER_GHOST_MIB (64), EM_PAGER_CODEC (xpress|lz4),
//! EM_PAGER_DEDUP (1), EM_PAGER_DELTA (0), EM_PAGER_SKETCH (1), EM_PAGER_PREFETCH (8 — co-access prefetch),
//! EM_PAGER_COACCESS_MS (50), EM_PAGER_START_DELAY_S (60), EM_PAGER_TARGET_HITS (20),
//! EM_PAGER_LOG (em-pager.log).

use crate::{Cfg, Cl, Core, Ops};
use em_cas::{CasStore, Config};
use std::io::Write;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Mutex, OnceLock};

const MEM_COMMIT: u32 = 0x1000;
const MEM_DECOMMIT: u32 = 0x4000;
const PAGE_READONLY: u32 = 0x02;
const PAGE_READWRITE: u32 = 0x04;
const WHV_READ: u32 = 1;
const WHV_WRITE: u32 = 2;
const WHV_EXEC: u32 = 4;

extern "system" {
    fn VirtualAlloc(a: *mut u8, s: usize, t: u32, p: u32) -> *mut u8;
    fn VirtualFree(a: *mut u8, s: usize, t: u32) -> i32;
    fn VirtualProtect(a: *mut u8, s: usize, p: u32, old: *mut u32) -> i32;
    fn GetTickCount64() -> u64;
    fn QueryPerformanceCounter(c: *mut i64) -> i32;
    fn QueryPerformanceFrequency(f: *mut i64) -> i32;
    fn AddVectoredExceptionHandler(first: u32, h: usize) -> *mut core::ffi::c_void;
    fn GetCurrentProcess() -> isize;
    fn K32GetProcessMemoryInfo(h: isize, p: *mut Pmc, cb: u32) -> i32;
    fn GetCurrentThreadId() -> u32;
    fn GetThreadId(h: isize) -> u32;
    fn SuspendThread(h: isize) -> u32;
    fn ResumeThread(h: isize) -> u32;
    fn GetThreadContext(h: isize, ctx: *mut Ctx) -> i32;
    fn CloseHandle(h: isize) -> i32;
}

#[link(name = "ntdll")]
extern "system" {
    fn NtGetNextThread(process: isize, thread: isize, access: u32, attrs: u32, flags: u32, out: *mut isize) -> i32;
}

#[repr(C, align(16))]
struct Ctx([u8; 1232]); // CONTEXT x64

/// Остановленные на время восстановления потоки. Ёмкость выделена заранее: в окне остановки нельзя
/// выделять память (остановленный поток может держать замок кучи).
struct Frozen {
    handles: Vec<isize>,
}

impl Frozen {
    fn freeze(&mut self) {
        const ACCESS: u32 = 0x0002 | 0x0008 | 0x0800; // SUSPEND_RESUME | GET_CONTEXT | QUERY_LIMITED_INFORMATION
        let me = unsafe { GetCurrentThreadId() };
        let mut cur: isize = 0;
        loop {
            let mut next: isize = 0;
            let st = unsafe { NtGetNextThread(GetCurrentProcess(), cur, ACCESS, 0, 0, &mut next) };
            if cur != 0 && !self.handles.contains(&cur) {
                unsafe { CloseHandle(cur) };
            }
            if st < 0 {
                break;
            }
            cur = next;
            if unsafe { GetThreadId(next) } == me || self.handles.len() == self.handles.capacity() {
                continue;
            }
            if unsafe { SuspendThread(next) } != u32::MAX {
                // SuspendThread асинхронна: GetThreadContext возвращается, только когда поток действительно стоит
                let mut c = Ctx([0; 1232]);
                c.0[48..52].copy_from_slice(&0x0010_0001u32.to_le_bytes()); // ContextFlags = CONTEXT_CONTROL
                unsafe { GetThreadContext(next, &mut c) };
                self.handles.push(next);
            }
        }
    }
    fn thaw(&mut self) {
        for &h in &self.handles {
            unsafe {
                ResumeThread(h);
                CloseHandle(h);
            }
        }
        self.handles.clear();
    }
}

#[repr(C)]
#[derive(Default)]
struct Pmc { cb: u32, faults: u32, peak_ws: usize, ws: usize, qpp: usize, qp: usize, qnpp: usize, qnp: usize, pagefile: usize, peak_pagefile: usize, private: usize }

/// (приватная память МиБ, рабочий набор МиБ) этого процесса по счётчикам Windows.
pub fn proc_mem() -> (f64, f64) {
    let mut p = Pmc { cb: std::mem::size_of::<Pmc>() as u32, ..Default::default() };
    unsafe { K32GetProcessMemoryInfo(GetCurrentProcess(), &mut p, p.cb) };
    (p.private as f64 / 1048576.0, p.ws as f64 / 1048576.0)
}

fn now_ms() -> u64 {
    unsafe { GetTickCount64() }
}

type MapFn = unsafe extern "system" fn(part: usize, src: *mut u8, gpa: u64, size: u64, flags: u32) -> i32;
type UnmapFn = unsafe extern "system" fn(part: usize, gpa: u64, size: u64) -> i32;

enum Hv {
    Whp { part: usize, map: MapFn, unmap: UnmapFn },
    /// Модель гипервизора для стенда без WHP: битовая карта отображённых гостевых страниц.
    Sim { mapped: Vec<AtomicU64>, gpa0: u64 },
}

pub struct WinOps {
    hv: Hv,
    frozen: Mutex<Frozen>,
}

impl WinOps {
    fn sim_bit(&self, gpa: u64) -> Option<(usize, u64)> {
        if let Hv::Sim { mapped, gpa0 } = &self.hv {
            if gpa < *gpa0 {
                return None;
            }
            let p = ((gpa - gpa0) / 4096) as usize;
            return (p / 64 < mapped.len()).then_some((p / 64, 1u64 << (p % 64)));
        }
        None
    }
    fn sim_set(&self, gpa: u64, len: usize, on: bool) {
        if let Hv::Sim { mapped, .. } = &self.hv {
            for k in 0..len / 4096 {
                if let Some((w, b)) = self.sim_bit(gpa + (k * 4096) as u64) {
                    if on { mapped[w].fetch_or(b, Ordering::SeqCst); } else { mapped[w].fetch_and(!b, Ordering::SeqCst); }
                }
            }
        }
    }
}

impl Ops for WinOps {
    fn map(&self, gpa: u64, hva: *mut u8, len: usize, write: bool) -> bool {
        match &self.hv {
            Hv::Whp { part, map, .. } => {
                let fl = WHV_READ | WHV_EXEC | if write { WHV_WRITE } else { 0 };
                unsafe { map(*part, hva, gpa, len as u64, fl) >= 0 }
            }
            Hv::Sim { mapped, .. } => {
                // как настоящий WHP: отображение уже отображённого диапазона — ошибка
                for k in 0..len / 4096 {
                    if let Some((wi, b)) = self.sim_bit(gpa + (k * 4096) as u64) {
                        if mapped[wi].load(Ordering::SeqCst) & b != 0 {
                            return false;
                        }
                    }
                }
                self.sim_set(gpa, len, true);
                true
            }
        }
    }
    fn unmap(&self, gpa: u64, len: usize) -> bool {
        match &self.hv {
            Hv::Whp { part, unmap, .. } => unsafe { unmap(*part, gpa, len as u64) >= 0 },
            Hv::Sim { .. } => {
                self.sim_set(gpa, len, false);
                true
            }
        }
    }
    fn commit(&self, hva: *mut u8, len: usize) -> bool {
        unsafe { !VirtualAlloc(hva, len, MEM_COMMIT, PAGE_READWRITE).is_null() }
    }
    fn decommit(&self, hva: *mut u8, len: usize) {
        unsafe { VirtualFree(hva, len, MEM_DECOMMIT) };
    }
    fn protect_ro(&self, hva: *mut u8, len: usize) {
        let mut o = 0;
        unsafe { VirtualProtect(hva, len, PAGE_READONLY, &mut o) };
    }
    fn protect_rw(&self, hva: *mut u8, len: usize) {
        let mut o = 0;
        unsafe { VirtualProtect(hva, len, PAGE_READWRITE, &mut o) };
    }
    fn freeze(&self) {
        self.frozen.lock().unwrap().freeze();
    }
    fn thaw(&self) {
        self.frozen.lock().unwrap().thaw();
    }
}

pub struct Pager {
    core: Mutex<Core>,
    ops: WinOps,
    // фильтр для VEH без замка: hva управляемых диапазонов
    hva_lo: [AtomicUsize; 16],
    hva_hi: [AtomicUsize; 16],
    stop: AtomicBool,
    start_ms: u64,
    log: Mutex<Option<std::fs::File>>,
    qpf: i64,
}

static PAGER: OnceLock<Pager> = OnceLock::new();

/// Сколько vCPU/VEH-потоков ждут замок пейджера. Замок не честный: компактор, который берёт его тысячи раз
/// подряд на короткие операции, иначе «перехватывает» его у ждущего vCPU на десятки миллисекунд.
static WAITERS: AtomicUsize = AtomicUsize::new(0);

fn lock_urgent(p: &Pager) -> std::sync::MutexGuard<'_, Core> {
    WAITERS.fetch_add(1, Ordering::SeqCst);
    let c = p.core.lock().unwrap();
    WAITERS.fetch_sub(1, Ordering::SeqCst);
    c
}

fn env_u64(k: &str, d: u64) -> u64 {
    std::env::var(k).ok().and_then(|v| v.trim().parse().ok()).unwrap_or(d)
}

fn make_core() -> Core {
    let cfg = Cfg {
        cluster_pages: env_u64("EM_PAGER_CLUSTER", 16).clamp(1, 256) as usize,
        idle_ms: env_u64("EM_PAGER_IDLE_MS", 2000),
        min_range_bytes: env_u64("EM_PAGER_MIN_MIB", 64) << 20,
        keep_ghost: env_u64("EM_PAGER_GHOST", 0) == 1,
        prefetch_depth: env_u64("EM_PAGER_PREFETCH", 8).min(64) as usize,
        coaccess_ms: env_u64("EM_PAGER_COACCESS_MS", 50),
    };
    let name = std::env::var("EM_PAGER_CODEC").unwrap_or_else(|_| "xpress".into());
    let cc = Config {
        dedup: env_u64("EM_PAGER_DEDUP", 1) == 1,
        // Gate 0 на настоящей памяти: XOR-delta ни разу не окупила свой индекс — по умолчанию выключена
        delta: env_u64("EM_PAGER_DELTA", 0) == 1,
        sketch_features: env_u64("EM_PAGER_SKETCH", 1).clamp(1, 3) as usize,
        ..Config::default()
    };
    // auto: холодные кластеры — XPRESS (плотнее), тёплые (уже возвращавшиеся) — LZ4 (быстрее распаковка)
    let cas = if name == "auto" {
        CasStore::with_alt(em_cas::by_name("xpress"), em_cas::by_name("lz4"), cc)
    } else {
        CasStore::new(em_cas::by_name(&name), cc)
    };
    Core::new(cfg, cas)
}

fn init(hv: Hv) -> i32 {
    let mut f = 0i64;
    unsafe { QueryPerformanceFrequency(&mut f) };
    let log = std::env::var("EM_PAGER_LOG").unwrap_or_else(|_| "em-pager.log".into());
    let file = std::fs::OpenOptions::new().create(true).append(true).open(log).ok();
    let p = Pager {
        core: Mutex::new(make_core()),
        ops: WinOps { hv, frozen: Mutex::new(Frozen { handles: Vec::with_capacity(4096) }) },
        hva_lo: [const { AtomicUsize::new(0) }; 16],
        hva_hi: [const { AtomicUsize::new(0) }; 16],
        stop: AtomicBool::new(false),
        start_ms: now_ms(),
        log: Mutex::new(file),
        qpf: f.max(1),
    };
    if PAGER.set(p).is_err() {
        return -1;
    }
    unsafe { AddVectoredExceptionHandler(1, veh as *const () as usize) };
    let pg = PAGER.get().unwrap();
    let (cfg, codec) = {
        let c = pg.core.lock().unwrap();
        (c.cfg, c.cas.codec_name())
    };
    pg.logln(&format!("EM_PAGER start cfg={cfg:?} codec={codec}"));
    std::thread::Builder::new().name("em-pager-compactor".into()).spawn(compactor).ok();
    0
}

impl Pager {
    fn qpc(&self) -> i64 {
        let mut t = 0i64;
        unsafe { QueryPerformanceCounter(&mut t) };
        t
    }
    /// Операция под замком пейджера с учётом времени удержания (вид op из HOLD_NAMES).
    fn with_core<R>(&self, op: usize, f: impl FnOnce(&mut Core) -> R) -> R {
        // фоновая работа уступает замок срочным (exit vCPU, VEH): не больше ~2 мс ожидания на операцию
        for i in 0..64 {
            if WAITERS.load(Ordering::SeqCst) == 0 {
                break;
            }
            if i < 32 {
                std::hint::spin_loop();
            } else {
                std::thread::sleep(std::time::Duration::from_micros(50));
            }
        }
        let mut c = self.core.lock().unwrap();
        let t0 = self.qpc();
        let r = f(&mut c);
        let us = self.us_since(t0);
        c.record_hold_us(op, us);
        r
    }
    fn logln(&self, s: &str) {
        if let Some(f) = self.log.lock().unwrap().as_mut() {
            let _ = writeln!(f, "{s}");
            let _ = f.flush();
        }
    }
    fn us_since(&self, t0: i64) -> u64 {
        let mut t = 0i64;
        unsafe { QueryPerformanceCounter(&mut t) };
        ((t - t0) as u128 * 1_000_000 / self.qpf as u128) as u64
    }
    fn refresh_filter(&self, core: &Core) {
        let mut it = core.ranges.iter().filter(|r| r.managed);
        for i in 0..16 {
            let (lo, hi) = it.next().map(|r| (r.hva, r.hva + r.bytes as usize)).unwrap_or((0, 0));
            self.hva_lo[i].store(lo, Ordering::SeqCst);
            self.hva_hi[i].store(hi, Ordering::SeqCst);
        }
    }
    fn fail_closed(&self, what: &str) -> ! {
        self.logln(&format!("EM_PAGER FATAL {what}: страницу нельзя восстановить без потерь — процесс остановлен, чтобы не испортить память гостя"));
        std::process::abort();
    }
}

// ------------------------------ C ABI ------------------------------

/// partition — WHV_PARTITION_HANDLE; map/unmap — настоящие WHvMapGpaRange/WHvUnmapGpaRange.
pub extern "C" fn em_pager_init(partition: usize, map: usize, unmap: usize) -> i32 {
    if partition == 0 || map == 0 || unmap == 0 {
        return -1;
    }
    let hv = Hv::Whp { part: partition, map: unsafe { std::mem::transmute::<usize, MapFn>(map) }, unmap: unsafe { std::mem::transmute::<usize, UnmapFn>(unmap) } };
    init(hv)
}

pub extern "C" fn em_pager_add_ram(gpa: u64, hva: *mut u8, size: u64, writable: i32) {
    let Some(p) = PAGER.get() else { return };
    let mut c = p.core.lock().unwrap();
    let ok = c.add_range(gpa, hva, size, writable != 0);
    p.refresh_filter(&c);
    drop(c);
    p.logln(&format!("EM_PAGER add_ram gpa={gpa:#x} size_mib={} managed={ok}", size >> 20));
}

pub extern "C" fn em_pager_del_ram(gpa: u64, size: u64) {
    let Some(p) = PAGER.get() else { return };
    let mut c = p.core.lock().unwrap();
    if c.del_range(gpa, size, &p.ops).is_err() {
        p.fail_closed("del_ram restore");
    }
    p.refresh_filter(&c);
    drop(c);
    p.logln(&format!("EM_PAGER del_ram gpa={gpa:#x} size_mib={}", size >> 20));
}

thread_local! { static LAST_EXIT: std::cell::Cell<(u64, u32)> = const { std::cell::Cell::new((u64::MAX, 0)) }; }

/// Memory-access exit vCPU. 1 — наш кластер обработан (повторить инструкцию), 0 — не наш (MMIO для VMM).
pub extern "C" fn em_pager_mem_exit(gpa: u64, _access: u32) -> i32 {
    let Some(p) = PAGER.get() else { return 0 };
    let mut t0 = 0i64;
    unsafe { QueryPerformanceCounter(&mut t0) };
    let mut c = lock_urgent(p);
    let Some((ri, cl)) = c.find_gpa(gpa) else { return 0 };
    let was_evicted = c.ranges[ri].st[cl] == Cl::Evicted;
    if was_evicted {
        let wait = p.us_since(t0);
        c.record_wait_us(wait);
    }
    let th = p.qpc();
    // Все vCPU — один поток обучения co-access (гость один, его обращения связаны между vCPU),
    // обращения самого VMM через VEH — отдельные потоки по id (это эмуляция устройств, у неё свой порядок).
    let res = c.on_exit(ri, cl, now_ms(), 0, &p.ops);
    let hold = p.us_since(th);
    c.record_hold_us(0, hold);
    match res {
        Ok(true) => {
            if was_evicted {
                let us = p.us_since(t0);
                c.record_restore_us(us);
            }
            drop(c);
            // страховка от бесконечного цикла exit'ов по одному адресу
            let looping = LAST_EXIT.with(|l| {
                let (g, n) = l.get();
                let n = if g == gpa >> 12 { n + 1 } else { 0 };
                l.set((gpa >> 12, n));
                n > 100_000
            });
            if looping {
                p.fail_closed("exit loop on one gpa");
            }
            1
        }
        Ok(false) => 0,
        Err(_) => p.fail_closed("exit restore"),
    }
}

// ------------------------------ стенд без WHP ------------------------------

/// Пейджер с моделью гипервизора (pager-sim): gpa0 — начало модели, pages — её размер.
pub fn sim_init(gpa0: u64, pages: usize) -> i32 {
    let mapped = (0..(pages + 63) / 64).map(|_| AtomicU64::new(u64::MAX)).collect();
    init(Hv::Sim { mapped, gpa0 })
}

pub fn sim_is_mapped(gpa: u64) -> bool {
    let Some(p) = PAGER.get() else { return true };
    match (p.ops.sim_bit(gpa), &p.ops.hv) {
        (Some((w, b)), Hv::Sim { mapped, .. }) => mapped[w].load(Ordering::SeqCst) & b != 0,
        _ => true,
    }
}

pub fn stop() {
    if let Some(p) = PAGER.get() {
        p.stop.store(true, Ordering::SeqCst);
    }
}

pub fn stats_line() -> String {
    let Some(p) = PAGER.get() else { return String::new() };
    line(p)
}

/// Строка журнала. Под замком только копируются счётчики; квантили и форматирование — после.
fn line(p: &Pager) -> String {
    let (s, cs, clb, meta_b, (rs, wt), holds) = {
        let c = p.core.lock().unwrap();
        let meta_b = c.ranges.iter().map(|r| r.st.len() as u64 * 24 + (r.st.len() * c.cfg.cluster_pages) as u64 * 12).sum::<u64>();
        (c.stats(), c.cas.stats(), (c.cfg.cluster_pages * 4096) as u64, meta_b, c.latency_samples(), c.hold_samples())
    };
    let hold_s: Vec<String> = crate::HOLD_NAMES.iter().zip(&holds).map(|(n, v)| format!("{n}:{}/{}", crate::quantile(v, 0.99), v.iter().max().copied().unwrap_or(0))).collect();
    let evicted_b = s.evicted_cl * clb;
    let store_b = cs.total_bytes();
    let (pv, ws) = proc_mem();
    let q = |f: f64| crate::quantile(&rs, f);
    let qw = |f: f64| crate::quantile(&wt, f);
    let mib = |b: u64| b as f64 / 1048576.0;
    format!(
        "t_s={} guest_mib={} resident_cl={} probing_cl={} evicted_cl={} evicted_mib={:.1} store_mib={:.1} payload_mib={:.1} tables_mib={:.1} saved_mib={:.1} zero_pages={} dedup={} delta={} temporal={} full={} incompressible={} ghost_pages={} probes={} probe_hits={} evictions={} rollbacks={} exit_restores={} host_restores={} prefetched={} prefetch_used={} pf_checks={} pf_check_hits={} pf_active={} pf_vcpu_acc_pct={} restore_p50_us={} restore_p99_us={} lock_wait_p50_us={} lock_wait_p99_us={} crc_failures={} segments_released={} proc_private_mib={:.1} proc_ws_mib={:.1} hold_p99/max_us={}",
        (now_ms() - p.start_ms) / 1000,
        s.guest_bytes >> 20, s.resident_cl, s.probing_cl, s.evicted_cl,
        mib(evicted_b), mib(store_b), mib(cs.payload_bytes), mib(meta_b),
        (evicted_b as f64 - store_b as f64 - meta_b as f64) / 1048576.0,
        s.zero_pages_evicted, cs.put_dedup, cs.put_delta, cs.put_temporal, cs.put_full, cs.put_incompressible, s.ghost_pages,
        s.probes, s.probe_hits, s.evictions, s.evict_rollbacks, s.exit_restores, s.host_restores, s.prefetched, s.prefetch_used, s.pf_checks, s.pf_check_hits, s.pf_active, s.pf_vcpu_acc_pct, q(0.5), q(0.99), qw(0.5), qw(0.99),
        cs.crc_failures, cs.segments_released, pv, ws, hold_s.join(",")
    )
}

fn compactor() {
    let p = PAGER.get().unwrap();
    let delay = env_u64("EM_PAGER_START_DELAY_S", 60);
    let target = env_u64("EM_PAGER_TARGET_HITS", 20) as f64; // возвратов горячих кластеров на проход 100 мс
    let ghost_budget = env_u64("EM_PAGER_GHOST_MIB", 64) << 20;
    let t_start = now_ms();
    while now_ms() < t_start + delay * 1000 {
        if p.stop.load(Ordering::Relaxed) {
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(200));
    }
    p.logln("EM_PAGER compactor active");
    let mut budget = 512f64;
    let (mut last_log, mut last_maint) = (0u64, 0u64);
    let (mut probe, mut idle) = (Vec::new(), Vec::new());
    let (mut probed_prev, mut hits_prev) = (0u64, 0u64);
    while !p.stop.load(Ordering::Relaxed) {
        let now = now_ms();
        probe.clear();
        idle.clear();
        // сканирование кусками по 4096 кластеров: каждый кусок — отдельный короткий захват замка
        let total = p.core.lock().unwrap().clusters();
        let mut scanned = 0;
        while scanned < total && probe.len() < budget as usize {
            p.with_core(4, |c| c.candidates(now, budget as usize, 4096, &mut probe, &mut idle));
            scanned += 4096;
        }
        // по одному кластеру под замком: exit'ы vCPU ждут не дольше одной операции над кластером
        for &(ri, cl) in &idle {
            p.with_core(2, |c| c.evict(ri, cl, now, &p.ops));
        }
        let mut n = 0u64;
        for &(ri, cl) in &probe {
            if p.with_core(3, |c| c.begin_probe(ri, cl, now, &p.ops)) {
                n += 1;
            }
        }
        // бюджет по доле возвратов (как TMO / active-trim): пробуем столько, чтобы возвращалось ~target за проход
        let hits = p.core.lock().unwrap().st.probe_hits;
        let dh = (hits - hits_prev) as f64;
        hits_prev = hits;
        if probed_prev > 0 {
            let ratio = (dh / probed_prev as f64).max(0.005);
            budget = (0.5 * budget + 0.5 * (target / ratio)).clamp(64.0, 65536.0);
        }
        probed_prev = n;
        if now >= last_maint + 1000 {
            for _ in 0..16 {
                if p.with_core(5, |c| c.maintain(256, ghost_budget)) < 256 {
                    break;
                }
            }
            last_maint = now;
        }
        if now >= last_log + 5000 {
            let l = line(p);
            p.logln(&format!("EM_PAGER {l} budget={}", budget as u64));
            last_log = now;
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
}

// ------------------------------ VEH: обращения самого VMM ------------------------------

#[repr(C)]
struct ExceptionRecord { code: u32, flags: u32, record: *mut core::ffi::c_void, address: *mut core::ffi::c_void, nparams: u32, info: [usize; 15] }
#[repr(C)]
struct ExceptionPointers { rec: *mut ExceptionRecord, ctx: *mut core::ffi::c_void }

unsafe extern "system" fn veh(info: *mut ExceptionPointers) -> i32 {
    const CONTINUE_EXECUTION: i32 = -1;
    const CONTINUE_SEARCH: i32 = 0;
    let rec = (*info).rec;
    if rec.is_null() || (*rec).code != 0xC000_0005 || (*rec).nparams < 2 {
        return CONTINUE_SEARCH;
    }
    let a = (*rec).info[1];
    let Some(p) = PAGER.get() else { return CONTINUE_SEARCH };
    if !(0..16).any(|i| a >= p.hva_lo[i].load(Ordering::Acquire) && a < p.hva_hi[i].load(Ordering::Acquire)) {
        return CONTINUE_SEARCH;
    }
    let mut c = lock_urgent(p);
    let Some((ri, cl)) = c.find_hva(a) else { return CONTINUE_SEARCH };
    let th = p.qpc();
    let res = c.on_host_fault(ri, cl, now_ms(), GetCurrentThreadId() | 0x8000_0000, &p.ops);
    let hold = p.us_since(th);
    c.record_hold_us(1, hold);
    match res {
        Ok(true) => {
            // отказ по закоммиченной RW-памяти повторяется без конца — значит, он не наш
            let again = LAST_EXIT.with(|l| {
                let (g, n) = l.get();
                let n = if g == (a as u64 >> 12) | 1 << 63 { n + 1 } else { 0 };
                l.set(((a as u64 >> 12) | 1 << 63, n));
                n > 10_000
            });
            if again { CONTINUE_SEARCH } else { CONTINUE_EXECUTION }
        }
        Ok(false) => CONTINUE_SEARCH,
        Err(_) => p.fail_closed("host-side restore"),
    }
}
