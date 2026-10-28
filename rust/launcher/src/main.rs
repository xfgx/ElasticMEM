//! Лаунчер: запускает целевой процесс, внедряет в него elastic_arena.dll и печатает отчёт арены.
//!
//! Схема: CreateProcess (НЕ suspended, чтобы загрузчик уже связал IAT) -> записать путь DLL в процесс
//! -> CreateRemoteThread(LoadLibraryW) -> дождаться загрузки -> ждать завершения -> прочитать отчёт.
//! Цель сама ждёт появления модуля (GetModuleHandle) перед выделением, поэтому гонки нет.
//!
//! Это работает для «дружественных» приложений без Code Integrity Guard. Renderer-процессы Chromium и
//! игры с античитом внедрение блокируют — см. DESIGN.md.

#[cfg(not(windows))]
fn main() {
    eprintln!("em-launch работает только на Windows.");
}

#[cfg(windows)]
fn main() {
    use std::ffi::c_void;
    use std::process::exit;

    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 {
        eprintln!(
            "usage: em-launch <elastic_arena.dll> <report.txt> [min_mb] [idle_ms] -- <target.exe> [args...]"
        );
        exit(2);
    }
    let dll = std::fs::canonicalize(&args[1]).expect("dll path");
    let report = &args[2];
    let mut i = 3;
    let mut min_mb = "8".to_string();
    let mut idle_ms = "1000".to_string();
    if i < args.len() && args[i] != "--" {
        min_mb = args[i].clone();
        i += 1;
    }
    if i < args.len() && args[i] != "--" {
        idle_ms = args[i].clone();
        i += 1;
    }
    if i < args.len() && args[i] == "--" {
        i += 1;
    }
    let target: Vec<String> = args[i..].to_vec();
    if target.is_empty() {
        eprintln!("нет цели после --");
        exit(2);
    }
    let _ = std::fs::remove_file(report);

    // Переменные окружения наследуются целью при создании; DLL читает их в DllMain.
    std::env::set_var("EM_ARENA_HOOK", "1");
    std::env::set_var("EM_ARENA_MIN_MB", &min_mb);
    std::env::set_var("EM_ARENA_IDLE_MS", &idle_ms);
    std::env::set_var(
        "EM_ARENA_REPORT",
        std::fs::canonicalize(".").unwrap().join(report).to_string_lossy().as_ref(),
    );

    let code = unsafe { run(&dll, &target) };
    // Печать отчёта.
    match std::fs::read_to_string(report) {
        Ok(s) => {
            println!("--- arena report ---");
            print!("{s}");
        }
        Err(_) => println!("report_missing=1 (DLL не записала отчёт)"),
    }
    println!("target_exit_code={code}");
    exit(code);

    // --- внедрение ---
    #[allow(non_snake_case)]
    unsafe fn run(dll: &std::path::Path, target: &[String]) -> i32 {
        #[repr(C)]
        struct StartupInfoW {
            cb: u32,
            _reserved: *mut u16,
            desktop: *mut u16,
            title: *mut u16,
            x: u32,
            y: u32,
            xsize: u32,
            ysize: u32,
            xcount: u32,
            ycount: u32,
            fill: u32,
            flags: u32,
            show: u16,
            cb_reserved2: u16,
            reserved2: *mut u8,
            std_in: *mut c_void,
            std_out: *mut c_void,
            std_err: *mut c_void,
        }
        #[repr(C)]
        struct ProcessInformation {
            process: *mut c_void,
            thread: *mut c_void,
            pid: u32,
            tid: u32,
        }
        extern "system" {
            fn CreateProcessW(
                app: *const u16,
                cmd: *mut u16,
                pa: *mut c_void,
                ta: *mut c_void,
                inherit: i32,
                flags: u32,
                env: *mut c_void,
                cwd: *const u16,
                si: *mut StartupInfoW,
                pi: *mut ProcessInformation,
            ) -> i32;
            fn GetModuleHandleW(name: *const u16) -> *mut c_void;
            fn GetProcAddress(m: *mut c_void, n: *const u8) -> *mut c_void;
            fn VirtualAllocEx(h: *mut c_void, a: *mut c_void, s: usize, t: u32, p: u32) -> *mut c_void;
            fn WriteProcessMemory(h: *mut c_void, a: *mut c_void, b: *const u8, s: usize, w: *mut usize) -> i32;
            fn CreateRemoteThread(h: *mut c_void, sa: *mut c_void, ss: usize, start: *mut c_void, param: *mut c_void, flags: u32, tid: *mut u32) -> *mut c_void;
            fn WaitForSingleObject(h: *mut c_void, ms: u32) -> u32;
            fn GetExitCodeProcess(h: *mut c_void, code: *mut u32) -> i32;
            fn CloseHandle(h: *mut c_void) -> i32;
        }
        const MEM_COMMIT_RESERVE: u32 = 0x3000;
        const PAGE_READWRITE: u32 = 0x04;
        const INFINITE: u32 = 0xFFFF_FFFF;

        let wide = |s: &str| -> Vec<u16> { s.encode_utf16().chain(std::iter::once(0)).collect() };
        let mut cmd: Vec<u16> = target
            .iter()
            .map(|a| format!("\"{a}\""))
            .collect::<Vec<_>>()
            .join(" ")
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();

        let mut si: StartupInfoW = std::mem::zeroed();
        si.cb = std::mem::size_of::<StartupInfoW>() as u32;
        let mut pi: ProcessInformation = std::mem::zeroed();
        if CreateProcessW(
            std::ptr::null(),
            cmd.as_mut_ptr(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            1,
            0,
            std::ptr::null_mut(),
            std::ptr::null(),
            &mut si,
            &mut pi,
        ) == 0
        {
            eprintln!("CreateProcessW failed: {}", std::io::Error::last_os_error());
            return 1;
        }

        // Записать путь DLL (UTF-16) в адресное пространство цели и загрузить его там.
        let dll_w = wide(&dll.to_string_lossy());
        let bytes = dll_w.len() * 2;
        let remote = VirtualAllocEx(pi.process, std::ptr::null_mut(), bytes, MEM_COMMIT_RESERVE, PAGE_READWRITE);
        let mut wrote = 0usize;
        WriteProcessMemory(pi.process, remote, dll_w.as_ptr() as *const u8, bytes, &mut wrote);
        let k32 = GetModuleHandleW(wide("kernel32.dll").as_ptr());
        let load = GetProcAddress(k32, b"LoadLibraryW\0".as_ptr());
        let th = CreateRemoteThread(pi.process, std::ptr::null_mut(), 0, load, remote, 0, std::ptr::null_mut());
        if th.is_null() {
            eprintln!("CreateRemoteThread failed: {}", std::io::Error::last_os_error());
        } else {
            WaitForSingleObject(th, INFINITE); // дождаться, пока DLL загрузится и поставит перехват
            CloseHandle(th);
        }

        WaitForSingleObject(pi.process, INFINITE);
        let mut code = 0u32;
        GetExitCodeProcess(pi.process, &mut code);
        CloseHandle(pi.thread);
        CloseHandle(pi.process);
        code as i32
    }
}
