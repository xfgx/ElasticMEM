//! Кодеки payload'ов. Контракт: без потерь; сжатие возвращает None, если выигрыша нет.

use crate::PAGE;

pub trait Codec: Send + Sync {
    /// Сжать `src` в `dst` (dst не меньше PAGE). None — сжатый вид не меньше исходного.
    fn compress(&self, src: &[u8], dst: &mut [u8]) -> Option<usize>;
    /// Распаковать `src` ровно в `dst` (длина dst — исходная длина). false — повреждённые данные.
    fn decompress(&self, src: &[u8], dst: &mut [u8]) -> bool;
    fn name(&self) -> &'static str;
}

/// LZ4 (block). Быстрая распаковка — для тёплых страниц.
pub struct Lz4;

impl Codec for Lz4 {
    fn compress(&self, src: &[u8], dst: &mut [u8]) -> Option<usize> {
        let mut tmp = [0u8; PAGE + PAGE / 2];
        let n = lz4_flex::block::compress_into(src, &mut tmp).ok()?;
        if n >= src.len() || n > dst.len() {
            return None;
        }
        dst[..n].copy_from_slice(&tmp[..n]);
        Some(n)
    }
    fn decompress(&self, src: &[u8], dst: &mut [u8]) -> bool {
        matches!(lz4_flex::block::decompress_into(src, dst), Ok(n) if n == dst.len())
    }
    fn name(&self) -> &'static str {
        "lz4"
    }
}

#[cfg(windows)]
mod xpress {
    use super::Codec;
    const FMT: u16 = 0x0003; // COMPRESSION_FORMAT_XPRESS — тот же кодек, что у сжатия памяти Windows

    #[link(name = "ntdll")]
    extern "system" {
        fn RtlGetCompressionWorkSpaceSize(f: u16, c: *mut u32, d: *mut u32) -> i32;
        fn RtlCompressBuffer(f: u16, u: *const u8, us: u32, c: *mut u8, cs: u32, chunk: u32, fin: *mut u32, ws: *mut u8) -> i32;
        fn RtlDecompressBuffer(f: u16, u: *mut u8, us: u32, c: *const u8, cs: u32, fin: *mut u32) -> i32;
    }

    thread_local! { static WS: std::cell::RefCell<Vec<u8>> = const { std::cell::RefCell::new(Vec::new()) }; }

    /// ntdll XPRESS (LZ77) — плотнее LZ4, для холодных страниц.
    pub struct Xpress;

    impl Codec for Xpress {
        fn compress(&self, src: &[u8], dst: &mut [u8]) -> Option<usize> {
            let (mut c, mut d) = (0u32, 0u32);
            unsafe { RtlGetCompressionWorkSpaceSize(FMT, &mut c, &mut d) };
            let mut out = 0u32;
            let st = WS.with(|ws| {
                let mut ws = ws.borrow_mut();
                if ws.len() < c as usize {
                    ws.resize(c as usize, 0);
                }
                unsafe { RtlCompressBuffer(FMT, src.as_ptr(), src.len() as u32, dst.as_mut_ptr(), dst.len() as u32, 4096, &mut out, ws.as_mut_ptr()) }
            });
            if st < 0 || out as usize >= src.len() {
                None
            } else {
                Some(out as usize)
            }
        }
        fn decompress(&self, src: &[u8], dst: &mut [u8]) -> bool {
            let mut out = 0u32;
            let st = unsafe { RtlDecompressBuffer(FMT, dst.as_mut_ptr(), dst.len() as u32, src.as_ptr(), src.len() as u32, &mut out) };
            st >= 0 && out as usize == dst.len()
        }
        fn name(&self) -> &'static str {
            "xpress"
        }
    }
}

#[cfg(windows)]
pub use xpress::Xpress;

/// Кодек по имени: "lz4" | "xpress" (на не-Windows xpress недоступен, берётся lz4).
pub fn by_name(name: &str) -> Box<dyn Codec> {
    #[cfg(windows)]
    if name != "lz4" {
        return Box::new(Xpress);
    }
    let _ = name;
    Box::new(Lz4)
}
