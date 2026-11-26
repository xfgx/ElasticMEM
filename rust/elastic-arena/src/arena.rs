//! Windows-часть: прозрачно-сжатая арена. Резервируем диапазон адресов, холодные страницы держим
//! декоммиченными (PAGE_NOACCESS) и сжатыми. Обращение к такой странице бросает access violation; наш
//! векторный обработчик (VEH) коммитит страницу, распаковывает ровно 4 КиБ и возобновляет инструкцию.
//! Фоновый компактор возвращает холодные страницы в сжатое состояние и освобождает физическую память.
//!
//! Память арены — раздел ОС (pagefile-backed section) с двумя видами: «вид приложения» (его адреса видит
//! приложение) и «вид заполнения» (только для арены). Холодная страница в виде приложения стоит PAGE_NOACCESS,
//! её физическая память отдана ОС (MEM_RESET через вид заполнения). Восстановление пишет байты через вид
//! заполнения и лишь затем возвращает доступ виду приложения — поэтому нет окна, в котором чужой поток видит
//! пустую или недописанную страницу: до последнего шага он получает access violation и ждёт замок в VEH.
//! (Раньше арена коммитила страницу через VirtualAlloc, и свежезакоммиченная страница была доступна всем до
//! заполнения — та же гонка, что поймал pager-sim в em-pager.) Способ освобождения проверен view-probe на
//! Windows Server 2022/2025: MEM_RESET через второй вид возвращает ОС всю физическую память страниц.
//!
//! Инварианты без потерь (глобальный замок на арену держится на всём отказе и всём вытеснении):
//!  * вытеснение сперва ставит PAGE_READONLY, затем читает байты — поэтому запись приложения не теряется:
//!    она отказывает, встаёт на тот же замок, а после распаковки инструкция записи выполняется заново;
//!  * слот хранилища освобождается только после успешной распаковки в страницу.

use crate::codec::PageCodec;
use crate::core::{Fault, State, Table};
use std::sync::atomic::{AtomicPtr, AtomicUsize, Ordering};
use std::sync::Mutex;

const PAGE: usize = 4096;
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
    fn CreateFileMappingW(file: isize, sa: *const u8, prot: u32, hi: u32, lo: u32, name: *const u16) -> isize;
    fn MapViewOfFile(h: isize, access: u32, hi: u32, lo: u32, n: usize) -> *mut u8;
    fn UnmapViewOfFile(a: *const u8) -> i32;
    fn CloseHandle(h: isize) -> i32;
    fn VirtualUnlock(a: *mut u8, n: usize) -> i32;
}

const MEM_RESET: u32 = 0x80000;
const FILE_MAP_ALL: u32 = 0x0002 | 0x0004;

/// Физическую память страницы — ОС (содержимое больше не нужно, вид приложения уже PAGE_NOACCESS).
unsafe fn release_phys(fill: *mut u8) {
    VirtualAlloc(fill, PAGE, MEM_RESET, PAGE_READWRITE);
}

/// Записать страницу через вид заполнения и убрать её из рабочего набора по этому виду (иначе одна физическая
/// страница считалась бы в рабочем наборе дважды). VirtualUnlock на незаблокированной странице так и делает.
unsafe fn fill_page(fill: *mut u8, src: &[u8]) {
    std::ptr::copy_nonoverlapping(src.as_ptr(), fill, PAGE);
    VirtualUnlock(fill, PAGE);
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
    fill: *mut u8,
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
    fill: *mut u8,
    section: isize,
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
    pub cas: Option<em_cas::CasStats>,
}

impl Arena {
    /// Создать арену на `pages` страниц 4 КиБ, хранилище сжатия ограничено `quota_bytes`.
    pub fn new(
        codec: Box<dyn PageCodec>,
        pages: usize,
        quota_bytes: usize,
    ) -> std::io::Result<Arena> {
        Self::with_table(Table::new(codec, pages, quota_bytes), pages)
    }

    /// Арена поверх content-addressed хранилища em-cas (zero / dedup / XOR-delta / временна́я дельта).
    /// `ghost_pages` — сколько прошлых версий страниц держать базами временно́й дельты (0 — выкл).
    pub fn new_cas(store: em_cas::CasStore, pages: usize, ghost_pages: usize) -> std::io::Result<Arena> {
        Self::with_table(Table::new_cas(store, pages, ghost_pages), pages)
    }

    fn with_table(table: Table, pages: usize) -> std::io::Result<Arena> {
        let bytes = pages * PAGE;
        let section = unsafe { CreateFileMappingW(-1, std::ptr::null(), PAGE_READWRITE, (bytes as u64 >> 32) as u32, bytes as u32, std::ptr::null()) };
        if section == 0 {
            return Err(std::io::Error::last_os_error());
        }
        let base = unsafe { MapViewOfFile(section, FILE_MAP_ALL, 0, 0, bytes) };
        let fill = unsafe { MapViewOfFile(section, FILE_MAP_ALL, 0, 0, bytes) };
        if base.is_null() || fill.is_null() {
            let e = std::io::Error::last_os_error();
            unsafe {
                if !base.is_null() { UnmapViewOfFile(base); }
                if !fill.is_null() { UnmapViewOfFile(fill); }
                CloseHandle(section);
            }
            return Err(e);
        }
        // все страницы стартуют в состоянии Zero: вид приложения недоступен, первое обращение заполнит страницу
        let mut old = 0u32;
        unsafe { VirtualProtect(base, bytes, PAGE_NOACCESS, &mut old) };
        let inner = Box::into_raw(Box::new(Mutex::new(Inner {
            table,
            base,
            fill,
            pages,
        })));
        let slot = match register(base as usize, base as usize + bytes, inner) {
            Some(s) => s,
            None => {
                unsafe {
                    drop(Box::from_raw(inner));
                    UnmapViewOfFile(base);
                    UnmapViewOfFile(fill);
                    CloseHandle(section);
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
            fill,
            section,
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
                unsafe {
                    VirtualProtect(va, PAGE, PAGE_NOACCESS, &mut old);
                    release_phys(inner.fill.add(p * PAGE));
                }
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
        // хранилище: перенести payload'ы из полупустых сегментов, пустые вернуть ОС
        self.inner().lock().unwrap().table.maintain(2048);
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
            cas: t.cas_stats(),
        }
    }
}

impl Drop for Arena {
    fn drop(&mut self) {
        REG_PTR[self.slot].store(std::ptr::null_mut(), Ordering::SeqCst);
        REG_BASE[self.slot].store(0, Ordering::SeqCst);
        REG_END[self.slot].store(0, Ordering::SeqCst);
        unsafe {
            UnmapViewOfFile(self.base);
            UnmapViewOfFile(self.fill);
            CloseHandle(self.section);
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
    // Сжата/нулевая: распаковать в буфер, записать через вид заполнения (вид приложения всё ещё PAGE_NOACCESS —
    // другие потоки ждут в VEH), и только потом открыть доступ виду приложения. Окна с пустой страницей нет.
    let mut tmp = [0u8; PAGE];
    if let Fault::Corrupt = inner.table.on_fault(p, &mut tmp, now) {
        // Fail-closed: страницу нельзя восстановить без потерь — исключение уходит дальше, процесс падает,
        // а не продолжает работу с неверными байтами.
        return EXCEPTION_CONTINUE_SEARCH;
    }
    fill_page(inner.fill.add(p * PAGE), &tmp);
    let mut old = 0u32;
    if VirtualProtect(va, PAGE, PAGE_READWRITE, &mut old) == 0 {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    EXCEPTION_CONTINUE_EXECUTION
}
