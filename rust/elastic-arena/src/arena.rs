//! Windows-часть: прозрачно-сжатая арена. Резервируем диапазон адресов, холодные страницы держим
//! декоммиченными (PAGE_NOACCESS) и сжатыми. Обращение к такой странице бросает access violation; наш
//! векторный обработчик (VEH) коммитит страницу, распаковывает ровно 4 КиБ и возобновляет инструкцию.
//! Фоновый компактор возвращает холодные страницы в сжатое состояние и освобождает физическую память.
//!
//! Инварианты без потерь (глобальный замок на арену держится на всём отказе и всём вытеснении):
//!  * вытеснение сперва ставит PAGE_READONLY, затем читает байты — поэтому запись приложения не теряется:
//!    она отказывает, встаёт на тот же замок, а после распаковки инструкция записи выполняется заново;
//!  * слот хранилища освобождается только после успешной распаковки в страницу.

use crate::codec::PageCodec;
use crate::core::{State, Table};
use std::sync::atomic::{AtomicPtr, AtomicUsize, Ordering};
use std::sync::Mutex;

const PAGE: usize = 4096;
const MEM_COMMIT: u32 = 0x1000;
const MEM_RESERVE: u32 = 0x2000;
const MEM_DECOMMIT: u32 = 0x4000;
const MEM_RELEASE: u32 = 0x8000;
const PAGE_NOACCESS: u32 = 0x01;
const PAGE_READONLY: u32 = 0x02;
const PAGE_READWRITE: u32 = 0x04;
const EXCEPTION_CONTINUE_EXECUTION: i32 = -1;
const EXCEPTION_CONTINUE_SEARCH: i32 = 0;
const STATUS_ACCESS_VIOLATION: u32 = 0xC000_0005;

#[allow(dead_code)]
extern "system" {
    fn VirtualAlloc(addr: *mut u8, size: usize, typ: u32, prot: u32) -> *mut u8;
    fn VirtualFree(addr: *mut u8, size: usize, typ: u32) -> i32;
    fn VirtualProtect(addr: *mut u8, size: usize, prot: u32, old: *mut u32) -> i32;
    fn GetTickCount64() -> u64;
    fn AddVectoredExceptionHandler(first: u32, handler: usize) -> *mut core::ffi::c_void;
    fn RemoveVectoredExceptionHandler(handle: *mut core::ffi::c_void) -> u32;
}

#[repr(C)]
struct ExceptionRecord {
    code: u32,
    flags: u32,
    record: *mut core::ffi::c_void,
    address: *mut core::ffi::c_void,
    number_parameters: u32,
    // далее padding + information[15]; читаем information[0..1] по смещению.
    information: [usize; 15],
}

#[repr(C)]
struct ExceptionPointers {
    exception_record: *mut ExceptionRecord,
    context_record: *mut core::ffi::c_void,
}

struct Inner {
    table: Table,
    base: *mut u8,
    pages: usize,
}
unsafe impl Send for Inner {}

// Глобальный реестр арен для VEH: найти арену по адресу отказа без аллокаций и без замка реестра.
const MAX_ARENAS: usize = 16;
static REG_BASE: [AtomicUsize; MAX_ARENAS] = [const { AtomicUsize::new(0) }; MAX_ARENAS];
static REG_END: [AtomicUsize; MAX_ARENAS] = [const { AtomicUsize::new(0) }; MAX_ARENAS];
static REG_PTR: [AtomicPtr<Mutex<Inner>>; MAX_ARENAS] =
    [const { AtomicPtr::new(std::ptr::null_mut()) }; MAX_ARENAS];
static VEH_INSTALLED: AtomicUsize = AtomicUsize::new(0);
static VEH_HANDLE: AtomicPtr<core::ffi::c_void> = AtomicPtr::new(std::ptr::null_mut());

pub struct Arena {
    inner: *mut Mutex<Inner>,
    base: *mut u8,
    bytes: usize,
    slot: usize,
}
unsafe impl Send for Arena {}
unsafe impl Sync for Arena {}

#[derive(Clone, Copy, Debug, Default)]
pub struct Stats {
    pub pages: usize,
    pub resident_pages: usize,
    pub compressed_pages: usize,
    pub zero_pages: usize,
    pub pinned_pages: usize,
    pub probing_pages: usize,
    pub probe_hits: u64,
    pub store_bytes: u64,
    pub payload_bytes: u64,
    pub faults: u64,
    pub decompressed: u64,
    pub evictions: u64,
}

impl Arena {
    /// Создать арену на `pages` страниц 4 КиБ, хранилище сжатия ограничено `quota_bytes`.
    pub fn new(
        codec: Box<dyn PageCodec>,
        pages: usize,
        quota_bytes: usize,
    ) -> std::io::Result<Arena> {
        let bytes = pages * PAGE;
        let base = unsafe { VirtualAlloc(std::ptr::null_mut(), bytes, MEM_RESERVE, PAGE_NOACCESS) };
        if base.is_null() {
            return Err(std::io::Error::last_os_error());
        }
        let inner = Box::into_raw(Box::new(Mutex::new(Inner {
            table: Table::new(codec, pages, quota_bytes),
            base,
            pages,
        })));
        let slot = match register(base as usize, base as usize + bytes, inner) {
            Some(s) => s,
            None => {
                unsafe {
                    drop(Box::from_raw(inner));
                    VirtualFree(base, 0, MEM_RELEASE);
                }
                return Err(std::io::Error::new(
                    std::io::ErrorKind::Other,
                    "too many arenas registered",
                ));
            }
        };
        install_veh();
        Ok(Arena {
            inner,
            base,
            bytes,
            slot,
        })
    }

    pub fn base(&self) -> *mut u8 {
        self.base
    }
    pub fn len(&self) -> usize {
        self.bytes
    }
    pub fn is_empty(&self) -> bool {
        self.bytes == 0
    }

    fn inner(&self) -> &Mutex<Inner> {
        unsafe { &*self.inner }
    }

    /// Один проход компактора. Две стадии, как accessed-бит в ядре:
    ///  1. проба: до `max_probe` резидентных страниц (чей срок подошёл) ставим PAGE_NOACCESS, байты остаются на месте —
    ///     если приложение их тронет, отказ стоит лишь VirtualProtect (без распаковки), и страница помечается горячей;
    ///  2. сжатие: страницы, которые пробыли в пробе дольше `idle_ms` нетронутыми, сжимаем и освобождаем физику.
    /// Замок арены берётся на окно сканирования и на одну страницу, никогда на всю порцию — отказы приложения
    /// не ждут компактор дольше одной операции над страницей.
    pub fn compact(&self, max_probe: usize, idle_ms: u64) -> (usize, usize) {
        const WINDOW: usize = 8192;
        let now = unsafe { GetTickCount64() };
        let pages = self.bytes / PAGE;
        let (mut probe, mut idle) = (Vec::new(), Vec::new());
        let mut from = 0;
        while from < pages {
            let inner = self.inner().lock().unwrap();
            inner.table.candidates(from, from + WINDOW, now, idle_ms, &mut probe, max_probe, &mut idle);
            drop(inner);
            from += WINDOW;
        }
        let mut scratch = [0u8; PAGE];
        let mut old = 0u32;
        let mut compressed = 0;
        for &p in &idle {
            let mut inner = self.inner().lock().unwrap();
            if inner.table.state(p) != State::Probing {
                continue;
            }
            let va = unsafe { inner.base.add(p * PAGE) };
            unsafe { VirtualProtect(va, PAGE, PAGE_READONLY, &mut old) };
            let src = unsafe { std::slice::from_raw_parts(va, PAGE) };
            if inner.table.evict(p, src, &mut scratch) {
                unsafe { VirtualFree(va, PAGE, MEM_DECOMMIT) };
                compressed += 1;
            } else {
                unsafe { VirtualProtect(va, PAGE, PAGE_READWRITE, &mut old) };
            }
        }
        let mut probed = 0;
        for &p in &probe {
            let mut inner = self.inner().lock().unwrap();
            if inner.table.begin_probe(p, now) {
                let va = unsafe { inner.base.add(p * PAGE) };
                unsafe { VirtualProtect(va, PAGE, PAGE_NOACCESS, &mut old) };
                probed += 1;
            }
        }
        (probed, compressed)
    }

    pub fn stats(&self) -> Stats {
        let inner = self.inner().lock().unwrap();
        let t = &inner.table;
        Stats {
            pages: t.pages(),
            resident_pages: t.resident,
            compressed_pages: t.compressed,
            zero_pages: t.zero,
            pinned_pages: t.pinned,
            probing_pages: t.probing,
            probe_hits: t.probe_hits,
            store_bytes: t.store_bytes(),
            payload_bytes: t.payload_bytes(),
            faults: t.faults,
            decompressed: t.decompressed,
            evictions: t.evictions,
        }
    }
}

impl Drop for Arena {
    fn drop(&mut self) {
        REG_PTR[self.slot].store(std::ptr::null_mut(), Ordering::SeqCst);
        REG_BASE[self.slot].store(0, Ordering::SeqCst);
        REG_END[self.slot].store(0, Ordering::SeqCst);
        unsafe {
            VirtualFree(self.base, 0, MEM_RELEASE);
            drop(Box::from_raw(self.inner));
        }
    }
}

fn register(base: usize, end: usize, ptr: *mut Mutex<Inner>) -> Option<usize> {
    for i in 0..MAX_ARENAS {
        if REG_PTR[i]
            .compare_exchange(
                std::ptr::null_mut(),
                ptr,
                Ordering::SeqCst,
                Ordering::SeqCst,
            )
            .is_ok()
        {
            REG_BASE[i].store(base, Ordering::SeqCst);
            REG_END[i].store(end, Ordering::SeqCst);
            return Some(i);
        }
    }
    None
}

fn install_veh() {
    if VEH_INSTALLED
        .compare_exchange(0, 1, Ordering::SeqCst, Ordering::SeqCst)
        .is_ok()
    {
        let h = unsafe { AddVectoredExceptionHandler(1, veh_handler as *const () as usize) };
        VEH_HANDLE.store(h, Ordering::SeqCst);
    }
}

/// Векторный обработчик отказов. Без аллокаций и без паник: находит арену по адресу и распаковывает страницу.
unsafe extern "system" fn veh_handler(info: *mut ExceptionPointers) -> i32 {
    let rec = (*info).exception_record;
    if rec.is_null() || (*rec).code != STATUS_ACCESS_VIOLATION {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    if (*rec).number_parameters < 2 {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    let addr = (*rec).information[1]; // адрес обращения
                                      // Найти арену.
    let mut inner_ptr: *mut Mutex<Inner> = std::ptr::null_mut();
    for i in 0..MAX_ARENAS {
        let b = REG_BASE[i].load(Ordering::Acquire);
        let e = REG_END[i].load(Ordering::Acquire);
        if b != 0 && addr >= b && addr < e {
            inner_ptr = REG_PTR[i].load(Ordering::Acquire);
            break;
        }
    }
    if inner_ptr.is_null() {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    let mutex = &*inner_ptr;
    let mut inner = match mutex.lock() {
        Ok(g) => g,
        Err(_) => return EXCEPTION_CONTINUE_SEARCH,
    };
    let base = inner.base as usize;
    let p = (addr - base) / PAGE;
    if p >= inner.pages {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    let va = inner.base.add(p * PAGE);
    let now = GetTickCount64();
    match inner.table.state(p) {
        State::Resident | State::Pinned => return EXCEPTION_CONTINUE_EXECUTION, // уже вернули
        State::Probing => {
            // Попадание в пробу: байты на месте, просто вернуть доступ.
            let mut old = 0u32;
            if VirtualProtect(va, PAGE, PAGE_READWRITE, &mut old) == 0 {
                return EXCEPTION_CONTINUE_SEARCH;
            }
            let dst = std::slice::from_raw_parts_mut(va, PAGE);
            inner.table.on_fault(p, dst, now);
            return EXCEPTION_CONTINUE_EXECUTION;
        }
        _ => {}
    }
    // Сжата/нулевая: закоммитить физическую страницу под запись, затем заполнить.
    if VirtualAlloc(va, PAGE, MEM_COMMIT, PAGE_READWRITE).is_null() {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    let dst = std::slice::from_raw_parts_mut(va, PAGE);
    inner.table.on_fault(p, dst, now);
    EXCEPTION_CONTINUE_EXECUTION
}
