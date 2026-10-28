//! Перехват аллокатора чужого процесса через таблицу импорта (IAT). Наша DLL, будучи внедрённой,
//! подменяет в главном модуле процесса указатели на kernel32!VirtualAlloc / VirtualFree. Крупные
//! анонимные COMMIT+READWRITE выделения обслуживаются сжатой ареной (`Arena`): приложение получает
//! обычный с виду буфер, а холодные страницы в нём прозрачно сжимаются.
//!
//! Почему IAT, а не inline-hook ntdll: IAT-патч не трогает защищённые страницы кода и не ломает CFG/CIG,
//! он надёжнее и безопаснее. Ограничение: ловит только тех, кто зовёт VirtualAlloc через импорт
//! (движки игр, свои аллокаторы, буферы ресурсов). Рост кучи CRT идёт мимо (через NtAllocateVirtualMemory).

use crate::{Arena, PageCodec, Rle, Xpress};
use std::ffi::c_void;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

const MEM_COMMIT: u32 = 0x1000;
const MEM_RELEASE: u32 = 0x8000;
const PAGE_READWRITE: u32 = 0x04;
const PAGE: usize = 4096;

type PfnVirtualAlloc = unsafe extern "system" fn(*mut c_void, usize, u32, u32) -> *mut c_void;
type PfnVirtualFree = unsafe extern "system" fn(*mut c_void, usize, u32) -> i32;

extern "system" {
    fn GetModuleHandleW(name: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const u8) -> *mut c_void;
    fn VirtualProtect(addr: *mut u8, size: usize, prot: u32, old: *mut u32) -> i32;
}

static REAL_ALLOC: AtomicUsize = AtomicUsize::new(0);
static REAL_FREE: AtomicUsize = AtomicUsize::new(0);
static MIN_BYTES: AtomicUsize = AtomicUsize::new(8 * 1024 * 1024);
static SERVED: AtomicU64 = AtomicU64::new(0);
static PASSED: AtomicU64 = AtomicU64::new(0);

/// Выданные ареной регионы (база -> арена). Отдельно от реестра VEH, чтобы путь отказа сюда не заходил.
fn hooked() -> &'static Mutex<Vec<(usize, Arc<Arena>)>> {
    static H: OnceLock<Mutex<Vec<(usize, Arc<Arena>)>>> = OnceLock::new();
    H.get_or_init(|| Mutex::new(Vec::new()))
}

thread_local! {
    static IN_HOOK: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

unsafe extern "system" fn hook_alloc(
    addr: *mut c_void,
    size: usize,
    typ: u32,
    prot: u32,
) -> *mut c_void {
    let real: PfnVirtualAlloc = std::mem::transmute(REAL_ALLOC.load(Ordering::Acquire));
    // Обслуживаем только простой случай: новый анонимный буфер COMMIT+READWRITE нужного размера.
    let serve = addr.is_null()
        && (typ & MEM_COMMIT) != 0
        && prot == PAGE_READWRITE
        && size >= MIN_BYTES.load(Ordering::Relaxed)
        && !IN_HOOK.with(|f| f.get());
    if !serve {
        PASSED.fetch_add(1, Ordering::Relaxed);
        return real(addr, size, typ, prot);
    }
    IN_HOOK.with(|f| f.set(true));
    let result = (|| {
        let pages = size.div_ceil(PAGE);
        // Квота хранилища = логический размер: в худшем случае ничего не сэкономим, но не упадём.
        let codec: Box<dyn PageCodec> = if std::env::var("EM_ARENA_CODEC").as_deref() == Ok("rle") {
            Box::new(Rle)
        } else {
            Box::new(Xpress)
        };
        let arena = Arena::new(codec, pages, pages * PAGE).ok()?;
        let base = arena.base() as usize;
        hooked().lock().ok()?.push((base, Arc::new(arena)));
        SERVED.fetch_add(1, Ordering::Relaxed);
        Some(base as *mut c_void)
    })();
    IN_HOOK.with(|f| f.set(false));
    match result {
        Some(p) => p,
        None => real(addr, size, typ, prot), // не смогли — обычное выделение
    }
}

unsafe extern "system" fn hook_free(addr: *mut c_void, size: usize, typ: u32) -> i32 {
    if (typ & MEM_RELEASE) != 0 {
        let mut g = hooked().lock().unwrap();
        if let Some(i) = g.iter().position(|&(b, _)| b == addr as usize) {
            let (_, arena) = g.remove(i); // Drop арены освободит адреса и снимет её с реестра VEH
            drop(g);
            drop(arena);
            return 1;
        }
    }
    let real: PfnVirtualFree = std::mem::transmute(REAL_FREE.load(Ordering::Acquire));
    real(addr, size, typ)
}

/// Пропатчить в модуле `module_base` все записи IAT, указывающие на `real`, заменив на `hook`.
unsafe fn patch_iat(module_base: *mut c_void, real: usize, hook: usize) -> usize {
    let base = module_base as *const u8;
    let e_lfanew = *(base.add(0x3c) as *const i32) as usize;
    let nt = base.add(e_lfanew);
    // OptionalHeader начинается через Signature(4) + FileHeader(20) = 24.
    let opt = nt.add(24);
    // DataDirectory[1] (Import): смещение 112 (начало DataDirectory) + 1*8 = 120 от OptionalHeader.
    let import_va = *(opt.add(120) as *const u32) as usize;
    if import_va == 0 {
        return 0;
    }
    let mut desc = base.add(import_va); // массив IMAGE_IMPORT_DESCRIPTOR по 20 байт
    let mut patched = 0usize;
    loop {
        let original_first_thunk = *(desc as *const u32);
        let first_thunk = *(desc.add(16) as *const u32);
        if original_first_thunk == 0 && first_thunk == 0 {
            break; // терминатор массива
        }
        if first_thunk != 0 {
            let mut thunk = base.add(first_thunk as usize) as *mut usize;
            while *thunk != 0 {
                if *thunk == real {
                    let mut old = 0u32;
                    if VirtualProtect(thunk as *mut u8, 8, PAGE_READWRITE, &mut old) != 0 {
                        *thunk = hook;
                        VirtualProtect(thunk as *mut u8, 8, old, &mut old);
                        patched += 1;
                    }
                }
                thunk = thunk.add(1);
            }
        }
        desc = desc.add(20);
    }
    patched
}

/// Установить перехват в главном модуле процесса. Возвращает число пропатченных записей.
pub fn install(min_bytes: usize) -> usize {
    MIN_BYTES.store(min_bytes.max(PAGE), Ordering::Relaxed);
    unsafe {
        let k32 = GetModuleHandleW(wide("kernel32.dll").as_ptr());
        let real_alloc = GetProcAddress(k32, b"VirtualAlloc\0".as_ptr()) as usize;
        let real_free = GetProcAddress(k32, b"VirtualFree\0".as_ptr()) as usize;
        if real_alloc == 0 || real_free == 0 {
            return 0;
        }
        REAL_ALLOC.store(real_alloc, Ordering::Release);
        REAL_FREE.store(real_free, Ordering::Release);
        let main = GetModuleHandleW(std::ptr::null());
        let a = patch_iat(main, real_alloc, hook_alloc as *const () as usize);
        let f = patch_iat(main, real_free, hook_free as *const () as usize);
        a + f
    }
}

/// Один проход компактора по всем выданным ареной регионам. Возвращает (сжато страниц).
pub fn compact_all(max_probe: usize, idle_ms: u64) -> usize {
    let snapshot: Vec<Arc<Arena>> = {
        let g = hooked().lock().unwrap();
        g.iter().map(|(_, a)| a.clone()).collect()
    };
    let mut compressed = 0;
    for a in snapshot {
        let (_, c) = a.compact(max_probe, idle_ms);
        compressed += c;
    }
    compressed
}

/// Сводка для отчёта: (регионов, логически МиБ, резидентно МиБ, сжато МиБ, хранилище МиБ, обслужено, пропущено).
pub fn summary() -> (usize, f64, f64, f64, f64, u64, u64) {
    let g = hooked().lock().unwrap();
    let (mut logical, mut resident, mut compressed, mut store) = (0u64, 0u64, 0u64, 0u64);
    for (_, a) in g.iter() {
        let s = a.stats();
        logical += (s.pages * PAGE) as u64;
        resident += ((s.resident_pages + s.pinned_pages + s.probing_pages) * PAGE) as u64;
        compressed += (s.compressed_pages * PAGE) as u64;
        store += s.store_bytes;
    }
    let mib = |b: u64| b as f64 / 1048576.0;
    (
        g.len(),
        mib(logical),
        mib(resident),
        mib(compressed),
        mib(store),
        SERVED.load(Ordering::Relaxed),
        PASSED.load(Ordering::Relaxed),
    )
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}
