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
    fn SetUnhandledExceptionFilter(f: usize) -> usize;
    fn GetModuleHandleExW(flags: u32, addr: *const u16, out: *mut usize) -> i32;
    fn GetModuleFileNameW(m: usize, buf: *mut u16, n: u32) -> u32;
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

// ---------- vCPU в госте не останавливаются при восстановлении ----------
// Раньше freeze() останавливал ВСЕ потоки, включая vCPU внутри WHvRunVirtualProcessor: каждая остановка выбивала
// их из гостя, и при ~100 восстановлениях в секунду гость целиком стоял ~20 % времени (Edge «зависал»).
// vCPU, который сейчас исполняет гостя, к памяти хоста не обращается: кластер, который восстанавливается, у гостя
// не отображён. Опасен только его выход в код VMM — поэтому на выходе из WHvRunVirtualProcessor поток ждёт, пока
// идёт восстановление (протокол Деккера: восстановитель пишет RESTORING и читает in_guest, vCPU пишет in_guest и
// читает RESTORING; при SeqCst хотя бы один видит запись другого).
const VCPU_SLOTS: usize = 64;
static VCPU_TID: [AtomicU64; VCPU_SLOTS] = [const { AtomicU64::new(0) }; VCPU_SLOTS];
static VCPU_IN_GUEST: [AtomicBool; VCPU_SLOTS] = [const { AtomicBool::new(false) }; VCPU_SLOTS];
static RESTORING: AtomicBool = AtomicBool::new(false);
thread_local! { static VCPU_SLOT: std::cell::Cell<usize> = const { std::cell::Cell::new(usize::MAX) }; }

fn vcpu_slot() -> Option<usize> {
    let s = VCPU_SLOT.with(|s| s.get());
    if s != usize::MAX {
        return (s < VCPU_SLOTS).then_some(s);
    }
    let tid = unsafe { GetCurrentThreadId() } as u64;
    for i in 0..VCPU_SLOTS {
        if VCPU_TID[i].compare_exchange(0, tid, Ordering::SeqCst, Ordering::SeqCst).is_ok() {
            VCPU_SLOT.with(|s| s.set(i));
            return Some(i);
        }
    }
    VCPU_SLOT.with(|s| s.set(VCPU_SLOTS)); // мест нет: этот поток останавливается как обычный
    None
}

/// Перед WHvRunVirtualProcessor: поток исполняет гостя, останавливать его при восстановлении не нужно.
pub extern "C" fn em_pager_vcpu_enter() {
    if let Some(i) = vcpu_slot() {
        VCPU_IN_GUEST[i].store(true, Ordering::SeqCst);
    }
}

/// После WHvRunVirtualProcessor, до любого кода VMM: дождаться конца идущего восстановления.
pub extern "C" fn em_pager_vcpu_leave() {
    if let Some(i) = vcpu_slot() {
        VCPU_IN_GUEST[i].store(false, Ordering::SeqCst);
        let mut n = 0u32;
        while RESTORING.load(Ordering::SeqCst) {
            n += 1;
            if n < 256 { std::hint::spin_loop() } else { std::thread::yield_now() }
        }
    }
}

fn in_guest(tid: u32) -> bool {
    (0..VCPU_SLOTS).any(|i| VCPU_TID[i].load(Ordering::SeqCst) == tid as u64 && VCPU_IN_GUEST[i].load(Ordering::SeqCst))
}

impl Frozen {
    fn freeze(&mut self) {
        RESTORING.store(true, Ordering::SeqCst);
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
            let tid = unsafe { GetThreadId(next) };
            if tid == me || self.handles.len() == self.handles.capacity() || in_guest(tid) {
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
        RESTORING.store(false, Ordering::SeqCst);
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
                let hr = unsafe { map(*part, hva, gpa, len as u64, fl) };
                if hr < 0 {
                    MAP_FAILS.fetch_add(1, Ordering::Relaxed);
                    LAST_MAP_HR.store(hr as u32, Ordering::Relaxed);
                }
                hr >= 0
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
            Hv::Whp { part, unmap, .. } => {
                let hr = unsafe { unmap(*part, gpa, len as u64) };
                if hr < 0 {
                    UNMAP_FAILS.fetch_add(1, Ordering::Relaxed);
                    LAST_UNMAP_HR.store(hr as u32, Ordering::Relaxed);
                }
                hr >= 0
            }
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

/// Ошибки WHvMapGpaRange/WHvUnmapGpaRange пейджера (в строку журнала) и сколько подробных записей уже сделано.
static MAP_FAILS: AtomicU64 = AtomicU64::new(0);
static LAST_MAP_HR: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
static UNMAP_FAILS: AtomicU64 = AtomicU64::new(0);
static LAST_UNMAP_HR: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
static EXIT_NOTES: AtomicU64 = AtomicU64::new(0);

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
    // Падение процесса VMM (необработанное исключение, panic пейджера) — строка в журнал: иначе после краша QEMU
    // в журнале пейджера нет ничего, кроме последней строки статистики.
    unsafe { PREV_FILTER.store(SetUnhandledExceptionFilter(crash_filter as *const () as usize), Ordering::SeqCst) };
    let prev_hook = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        if let Some(p) = PAGER.get() {
            p.logln_try(&format!("EM_PAGER PANIC {info}"));
        }
        prev_hook(info);
    }));
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
    /// Для пути падения: не ждать замок журнала (его может держать упавший поток).
    fn logln_try(&self, s: &str) {
        if let Ok(mut g) = self.log.try_lock() {
            if let Some(f) = g.as_mut() {
                let _ = writeln!(f, "{s}");
                let _ = f.flush();
            }
        }
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

/// Буфер ввода-вывода VMM в памяти гостя: вернуть и закрепить кластеры до ReadFile/WriteFile (ядро не вызывает VEH).
pub extern "C" fn em_pager_prepare_io(addr: usize, len: usize) {
    let Some(p) = PAGER.get() else { return };
    if len == 0 || !(0..16).any(|i| addr < p.hva_hi[i].load(Ordering::Acquire) && addr + len > p.hva_lo[i].load(Ordering::Acquire)) {
        return;
    }
    let mut since = None;
    loop {
        let mut c = lock_urgent(p);
        match c.prepare_io(addr, len, now_ms(), &p.ops) {
            Ok(_) => return,
            Err(em_cas::CasError::NoMemory) => {
                drop(c);
                if !wait_for_memory(p, "io", &mut since) {
                    p.fail_closed("io buffer restore NoMemory");
                }
            }
            Err(e) => p.fail_closed(&format!("io buffer restore {e:?}")),
        }
    }
}

thread_local! { static NOMEM_SINCE: std::cell::Cell<Option<u64>> = const { std::cell::Cell::new(None) }; }
thread_local! { static LAST_EXIT: std::cell::Cell<(u64, u32)> = const { std::cell::Cell::new((u64::MAX, 0)) }; }

/// Memory-access exit vCPU. 1 — наш кластер обработан (повторить инструкцию), 0 — не наш (MMIO для VMM).
/// `access_info` — WHV_MEMORY_ACCESS_INFO целиком (биты 0-1 — тип доступа, бит 2 — GpaUnmapped).
pub extern "C" fn em_pager_mem_exit(gpa: u64, access_info: u32) -> i32 {
    let Some(p) = PAGER.get() else { return 0 };
    let mut t0 = 0i64;
    unsafe { QueryPerformanceCounter(&mut t0) };
    let mut c = lock_urgent(p);
    let Some((ri, cl)) = c.find_gpa(gpa) else { return 0 };
    if access_info & 4 == 0 {
        // exit по RAM пейджера, хотя отображение есть (GpaUnmapped=0): обновить отображение кластера и повторить
        let st = c.ranges[ri].st[cl];
        if EXIT_NOTES.fetch_add(1, Ordering::Relaxed) < 64 {
            p.logln(&format!("EM_PAGER exit_mapped gpa={gpa:#x} access_info={access_info:#x} state={st:?}"));
        }
        if st == Cl::Resident {
            let remapped = c.remap_cl(ri, cl, &p.ops);
            drop(c);
            return if remapped { exit_retry(p, gpa) } else { p.fail_closed("remap after mapped-gpa exit") };
        }
    }
    let was_evicted = c.ranges[ri].st[cl] == Cl::Evicted;
    if was_evicted {
        let wait = p.us_since(t0);
        c.record_wait_us(wait);
    }
    let th = p.qpc();
    // Все vCPU — один поток обучения co-access (гость один, его обращения связаны между vCPU),
    // обращения самого VMM через VEH — отдельные потоки по id (это эмуляция устройств, у неё свой порядок).
    let (mut ri, mut cl) = (ri, cl);
    let mut since = None;
    let res = loop {
        match c.on_exit(ri, cl, now_ms(), 0, &p.ops) {
            Err(em_cas::CasError::NoMemory) => {
                drop(c);
                if !wait_for_memory(p, "exit", &mut since) {
                    p.fail_closed("exit restore NoMemory");
                }
                c = lock_urgent(p);
                // пока замок был отпущен, диапазоны могли поменяться
                let Some(f) = c.find_gpa(gpa) else { return exit_retry(p, gpa) };
                (ri, cl) = f;
            }
            r => break r,
        }
    };
    let hold = p.us_since(th);
    c.record_hold_us(0, hold);
    match res {
        Ok(true) => {
            if was_evicted {
                let us = p.us_since(t0);
                c.record_restore_us(us);
            }
            drop(c);
            exit_retry(p, gpa)
        }
        Ok(false) => {
            // отобразить не удалось даже после повторов: в VMM этот exit отдавать нельзя (принял бы за MMIO).
            // Повторяем инструкцию гостя — следующий exit попробует снова; бесконечный цикл ловит exit_retry.
            let st = c.ranges[ri].st[cl];
            drop(c);
            if EXIT_NOTES.fetch_add(1, Ordering::Relaxed) < 64 {
                p.logln(&format!(
                    "EM_PAGER map_failed gpa={gpa:#x} state={st:?} map_fails={} last_map_hr={:#010x} unmap_fails={} last_unmap_hr={:#010x}",
                    MAP_FAILS.load(Ordering::Relaxed), LAST_MAP_HR.load(Ordering::Relaxed), UNMAP_FAILS.load(Ordering::Relaxed), LAST_UNMAP_HR.load(Ordering::Relaxed)
                ));
            }
            exit_retry(p, gpa)
        }
        Err(e) => p.fail_closed(&format!("exit restore {e:?}")),
    }
}

/// Нехватка памяти ОС при восстановлении (VirtualAlloc MEM_COMMIT отказал: исчерпан лимит выделения системы —
/// например, параллельно другой процесс занял много памяти). Это временно и без потерь: байты кластера остались в
/// хранилище. Поток ждёт (замок пейджера отпущен) и повторяет до 120 с; FATAL — только если память так и не нашлась.
const NOMEM_WAIT_MS: u64 = 120_000;
static NOMEM_WAITS: AtomicU64 = AtomicU64::new(0);

fn wait_for_memory(p: &Pager, what: &str, since: &mut Option<u64>) -> bool {
    let now = now_ms();
    let t0 = *since.get_or_insert(now);
    if now - t0 > NOMEM_WAIT_MS {
        return false;
    }
    if now == t0 && NOMEM_WAITS.fetch_add(1, Ordering::Relaxed) < 32 {
        p.logln(&format!("EM_PAGER nomem {what}: ОС не дала память под восстановление, жду и повторяю (до {} с)", NOMEM_WAIT_MS / 1000));
    }
    std::thread::sleep(std::time::Duration::from_millis(20));
    true
}

/// Повторить инструкцию гостя (1). Страховка от бесконечного цикла exit'ов по одному адресу.
fn exit_retry(p: &Pager, gpa: u64) -> i32 {
    let looping = LAST_EXIT.with(|l| {
        let (g, n) = l.get();
        let n = if g == gpa >> 12 { n + 1 } else { 0 };
        l.set((gpa >> 12, n));
        n > 100_000
    });
    if looping {
        p.logln(&format!(
            "EM_PAGER loop gpa={gpa:#x} map_fails={} last_map_hr={:#010x} unmap_fails={} last_unmap_hr={:#010x}",
            MAP_FAILS.load(Ordering::Relaxed), LAST_MAP_HR.load(Ordering::Relaxed), UNMAP_FAILS.load(Ordering::Relaxed), LAST_UNMAP_HR.load(Ordering::Relaxed)
        ));
        p.fail_closed("exit loop on one gpa");
    }
    1
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
        "t_s={} guest_mib={} resident_cl={} probing_cl={} evicted_cl={} evicted_mib={:.1} store_mib={:.1} payload_mib={:.1} tables_mib={:.1} saved_mib={:.1} zero_pages={} dedup={} delta={} temporal={} full={} incompressible={} ghost_pages={} probes={} probe_hits={} evictions={} rollbacks={} exit_restores={} host_restores={} io_restores={} prefetched={} prefetch_used={} pf_checks={} pf_check_hits={} pf_active={} pf_vcpu_acc_pct={} restore_p50_us={} restore_p99_us={} lock_wait_p50_us={} lock_wait_p99_us={} crc_failures={} segments_released={} proc_private_mib={:.1} proc_ws_mib={:.1} hold_p99/max_us={}",
        (now_ms() - p.start_ms) / 1000,
        s.guest_bytes >> 20, s.resident_cl, s.probing_cl, s.evicted_cl,
        mib(evicted_b), mib(store_b), mib(cs.payload_bytes), mib(meta_b),
        (evicted_b as f64 - store_b as f64 - meta_b as f64) / 1048576.0,
        s.zero_pages_evicted, cs.put_dedup, cs.put_delta, cs.put_temporal, cs.put_full, cs.put_incompressible, s.ghost_pages,
        s.probes, s.probe_hits, s.evictions, s.evict_rollbacks, s.exit_restores, s.host_restores, s.io_restores, s.prefetched, s.prefetch_used, s.pf_checks, s.pf_check_hits, s.pf_active, s.pf_vcpu_acc_pct, q(0.5), q(0.99), qw(0.5), qw(0.99),
        cs.crc_failures, cs.segments_released, pv, ws, hold_s.join(",")
    ) + &format!(" map_fails={} unmap_fails={}", MAP_FAILS.load(Ordering::Relaxed), UNMAP_FAILS.load(Ordering::Relaxed))
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
            // нижняя граница 8 проб за проход: при 64 горячий гость (Edge) получал сотни лишних exit'ов в секунду
            budget = (0.5 * budget + 0.5 * (target / ratio)).clamp(8.0, 65536.0);
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

// ------------------------------ падение процесса ------------------------------

static PREV_FILTER: AtomicUsize = AtomicUsize::new(0);

/// Последний шанс перед завершением процесса: код исключения, модуль и смещение, адрес обращения и чем он был
/// для пейджера (память гостя: состояние кластера). Дальше — прежний фильтр (WER, отчёт Windows).
unsafe extern "system" fn crash_filter(info: *mut ExceptionPointers) -> i32 {
    let rec = if info.is_null() { std::ptr::null_mut() } else { (*info).rec };
    if let (Some(p), false) = (PAGER.get(), rec.is_null()) {
        let pc = (*rec).address as usize;
        let (op, a) = if (*rec).nparams >= 2 { ((*rec).info[0], (*rec).info[1]) } else { (usize::MAX, 0) };
        let mut m = 0usize;
        // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | UNCHANGED_REFCOUNT
        GetModuleHandleExW(4 | 2, pc as *const u16, &mut m);
        let mut buf = [0u16; 260];
        let n = if m != 0 { GetModuleFileNameW(m, buf.as_mut_ptr(), 260) as usize } else { 0 };
        let path = String::from_utf16_lossy(&buf[..n.min(260)]);
        let module = path.rsplit('\\').next().unwrap_or("?").to_string();
        let in_ram = (0..16).any(|i| a >= p.hva_lo[i].load(Ordering::Acquire) && a < p.hva_hi[i].load(Ordering::Acquire));
        let state = if !in_ram {
            "not-guest-ram".to_string()
        } else {
            match p.core.try_lock() {
                Ok(c) => match c.find_hva(a) {
                    Some((ri, cl)) => format!("{:?}", c.ranges[ri].st[cl]),
                    None => "unmanaged".to_string(),
                },
                Err(_) => "core-locked".to_string(),
            }
        };
        p.logln_try(&format!(
            "EM_PAGER CRASH code={:#010x} module={} offset={:#x} access={} addr={:#x} guest_ram={} cluster={} thread={}",
            (*rec).code, if module.is_empty() { "?" } else { &module }, pc.wrapping_sub(m), op as isize, a, in_ram, state, GetCurrentThreadId()
        ));
    }
    let prev = PREV_FILTER.load(Ordering::SeqCst);
    if prev != 0 {
        return std::mem::transmute::<usize, unsafe extern "system" fn(*mut ExceptionPointers) -> i32>(prev)(info);
    }
    0 // EXCEPTION_CONTINUE_SEARCH
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
            NOMEM_SINCE.with(|s| s.set(None));
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
        Err(em_cas::CasError::NoMemory) => {
            // временная нехватка памяти ОС: подождать и повторить ту же инструкцию VMM (снова отказ -> снова сюда)
            drop(c);
            let mut since = NOMEM_SINCE.with(|s| s.get());
            let waited = wait_for_memory(p, "host", &mut since);
            NOMEM_SINCE.with(|s| s.set(since));
            if waited { CONTINUE_EXECUTION } else { p.fail_closed("host-side restore NoMemory") }
        }
        Err(e) => p.fail_closed(&format!("host-side restore {e:?}")),
    }
}
