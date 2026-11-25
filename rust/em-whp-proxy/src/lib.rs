//! Прокси WinHvPlatform.dll для НЕМОДИФИЦИРОВАННОГО QEMU (-accel whpx).
//!
//! QEMU грузит WHP через LoadLibrary("WinHvPlatform.dll") и GetProcAddress. Положенная рядом с
//! qemu-system-x86_64.exe, эта DLL встаёт между QEMU и настоящей System32\WinHvPlatform.dll:
//!  * все функции из списка QEMU пробрасываются как есть;
//!  * WHvMapGpaRange — после успешного отображения записываемой RAM диапазон отдаётся пейджеру em-pager;
//!  * WHvUnmapGpaRange — перед снятием отображения пейджер возвращает все байты диапазона в память хоста;
//!  * WHvRunVirtualProcessor — memory-access exit с GpaUnmapped по диапазону пейджера обрабатывается здесь же
//!    (кластер восстанавливается и отображается), и vCPU сразу запускается снова: QEMU этого exit'а не видит.
//! Пейджер включается только при EM_PAGER=1. Иначе — чистый проброс.
//! Экспортируются ровно функции, которые ищет QEMU 11.1 (include/system/whpx-internal.h), у всех не больше
//! 6 аргументов — поэтому универсальный проброс 6 машинных слов корректен для x64.

#![allow(non_snake_case)]
#![cfg(windows)]

use em_pager::win::{em_pager_add_ram, em_pager_del_ram, em_pager_init, em_pager_mem_exit};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

extern "system" {
    fn LoadLibraryW(name: *const u16) -> usize;
    fn GetProcAddress(m: usize, name: *const u8) -> usize;
    fn GetSystemDirectoryW(buf: *mut u16, n: u32) -> u32;
}

static REAL: AtomicUsize = AtomicUsize::new(0);
static INITED: AtomicBool = AtomicBool::new(false);
static INIT_TRIED: AtomicBool = AtomicBool::new(false);
const E_NOTIMPL: i32 = 0x8000_4001u32 as i32;

fn real_module() -> usize {
    let m = REAL.load(Ordering::Acquire);
    if m != 0 {
        return m;
    }
    let path: Vec<u16> = match std::env::var("EM_WHP_REAL") {
        Ok(p) if !p.is_empty() => p.encode_utf16().chain([0]).collect(),
        _ => {
            let mut buf = [0u16; 260];
            let n = unsafe { GetSystemDirectoryW(buf.as_mut_ptr(), 260) } as usize;
            let mut v: Vec<u16> = buf[..n].to_vec();
            v.extend("\\WinHvPlatform.dll".encode_utf16());
            v.push(0);
            v
        }
    };
    let m = unsafe { LoadLibraryW(path.as_ptr()) };
    REAL.store(m, Ordering::Release);
    // Для проверки набора: QEMU действительно загрузил прокси из своей папки.
    if let Ok(log) = std::env::var("EM_WHP_PROXY_LOG") {
        use std::io::Write;
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(log) {
            let _ = writeln!(f, "EM_WHP_PROXY loaded real_whp={} pager_enabled={}", if m != 0 { "ok" } else { "missing" }, enabled());
        }
    }
    m
}

fn real(name: &str) -> usize {
    let m = real_module();
    if m == 0 {
        return 0;
    }
    let mut n = name.as_bytes().to_vec();
    n.push(0);
    unsafe { GetProcAddress(m, n.as_ptr()) }
}

fn enabled() -> bool {
    matches!(std::env::var("EM_PAGER"), Ok(v) if !v.is_empty() && v != "0")
}

type F6 = unsafe extern "system" fn(usize, usize, usize, usize, usize, usize) -> i32;

macro_rules! forward {
    ($($name:ident),* $(,)?) => {$(
        #[no_mangle]
        pub unsafe extern "system" fn $name(a: usize, b: usize, c: usize, d: usize, e: usize, f: usize) -> i32 {
            static P: AtomicUsize = AtomicUsize::new(0);
            let mut p = P.load(Ordering::Relaxed);
            if p == 0 {
                p = real(stringify!($name));
                if p == 0 { return E_NOTIMPL; }
                P.store(p, Ordering::Relaxed);
            }
            let fp: F6 = std::mem::transmute(p);
            fp(a, b, c, d, e, f)
        }
    )*};
}

// LIST_WINHVPLATFORM_FUNCTIONS и LIST_WINHVPLATFORM_FUNCTIONS_SUPPLEMENTAL из QEMU, кроме трёх перехватываемых.
forward!(
    WHvGetCapability, WHvCreatePartition, WHvSetupPartition, WHvDeletePartition, WHvGetPartitionProperty,
    WHvSetPartitionProperty, WHvTranslateGva, WHvCreateVirtualProcessor, WHvDeleteVirtualProcessor,
    WHvCancelRunVirtualProcessor, WHvGetVirtualProcessorRegisters, WHvSetVirtualProcessorRegisters,
    WHvSuspendPartitionTime, WHvRequestInterrupt, WHvGetVirtualProcessorInterruptControllerState2,
    WHvSetVirtualProcessorInterruptControllerState2, WHvResetPartition, WHvGetVirtualProcessorXsaveState,
    WHvSetVirtualProcessorXsaveState, WHvGetVirtualProcessorState, WHvSetVirtualProcessorState,
    WHvGetVirtualProcessorCpuidOutput,
);

type MapFn = unsafe extern "system" fn(usize, usize, u64, u64, u32) -> i32;
type UnmapFn = unsafe extern "system" fn(usize, u64, u64) -> i32;
type RunFn = unsafe extern "system" fn(usize, u32, *mut u8, u32) -> i32;

static P_MAP: AtomicUsize = AtomicUsize::new(0);
static P_UNMAP: AtomicUsize = AtomicUsize::new(0);
static P_RUN: AtomicUsize = AtomicUsize::new(0);

fn ptr(slot: &AtomicUsize, name: &str) -> usize {
    let mut p = slot.load(Ordering::Relaxed);
    if p == 0 {
        p = real(name);
        slot.store(p, Ordering::Relaxed);
    }
    p
}

#[no_mangle]
pub unsafe extern "system" fn WHvMapGpaRange(part: usize, src: usize, gpa: u64, size: u64, flags: u32) -> i32 {
    let p = ptr(&P_MAP, "WHvMapGpaRange");
    if p == 0 {
        return E_NOTIMPL;
    }
    let r = (std::mem::transmute::<usize, MapFn>(p))(part, src, gpa, size, flags);
    if r >= 0 && flags & 2 != 0 && enabled() {
        if !INIT_TRIED.swap(true, Ordering::SeqCst) {
            let u = ptr(&P_UNMAP, "WHvUnmapGpaRange");
            INITED.store(em_pager_init(part, p, u) == 0, Ordering::SeqCst);
        }
        if INITED.load(Ordering::Acquire) {
            em_pager_add_ram(gpa, src as *mut u8, size, 1);
        }
    }
    r
}

#[no_mangle]
pub unsafe extern "system" fn WHvUnmapGpaRange(part: usize, gpa: u64, size: u64) -> i32 {
    let p = ptr(&P_UNMAP, "WHvUnmapGpaRange");
    if p == 0 {
        return E_NOTIMPL;
    }
    if INITED.load(Ordering::Acquire) {
        em_pager_del_ram(gpa, size); // вернуть байты диапазона в память хоста до снятия отображения
    }
    (std::mem::transmute::<usize, UnmapFn>(p))(part, gpa, size)
}

// WHV_RUN_VP_EXIT_CONTEXT (x64, winhvplatformdefs.h): ExitReason @0; MemoryAccess @48: AccessInfo @68
// (биты 0-1 — тип доступа, бит 2 — GpaUnmapped), Gpa @72.
const EXIT_MEMORY_ACCESS: u32 = 1;

#[no_mangle]
pub unsafe extern "system" fn WHvRunVirtualProcessor(part: usize, vp: u32, ctx: *mut u8, size: u32) -> i32 {
    let p = ptr(&P_RUN, "WHvRunVirtualProcessor");
    if p == 0 {
        return E_NOTIMPL;
    }
    let run: RunFn = std::mem::transmute(p);
    loop {
        let r = run(part, vp, ctx, size);
        if r < 0 || !INITED.load(Ordering::Acquire) || ctx.is_null() || size < 80 {
            return r;
        }
        if *(ctx as *const u32) == EXIT_MEMORY_ACCESS {
            let ai = *(ctx.add(68) as *const u32);
            if ai & 4 != 0 {
                let gpa = *(ctx.add(72) as *const u64);
                if em_pager_mem_exit(gpa, ai & 3) == 1 {
                    continue; // наш кластер восстановлен — повторить ту же инструкцию гостя
                }
            }
        }
        return r;
    }
}
