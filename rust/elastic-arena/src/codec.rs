//! Кодеки одной 4-КиБ страницы. Контракт: сжатие без потерь, распаковка восстанавливает байт в байт.
//! Путь отказа (VEH) не должен выделять память — поэтому обе операции пишут в заранее выданный буфер.

pub const PAGE: usize = 4096;

/// Результат сжатия страницы.
pub enum Packed {
    /// Страница целиком нулевая — payload не нужен.
    Zero,
    /// Сжато в `len` байт (payload уже лежит в выданном буфере `dst[..len]`).
    Bytes(usize),
    /// Сжатие невыгодно (payload >= PAGE) — страницу хранить сжатой нет смысла.
    Incompressible,
}

pub trait PageCodec: Send + Sync {
    /// Сжать `src` (ровно 4096 байт) в `dst`. `dst` не меньше PAGE.
    fn compress(&self, src: &[u8], dst: &mut [u8]) -> Packed;
    /// Распаковать `src[..len]` обратно в `dst` (ровно 4096 байт). Вернуть false при ошибке.
    fn decompress(&self, src: &[u8], dst: &mut [u8]) -> bool;
    fn name(&self) -> &'static str;
}

/// Переносимый кодек для тестов и не-Windows: RLE поверх нулей + повторов байт.
/// Без аллокаций. Детерминирован, без потерь.
pub struct Rle;

impl PageCodec for Rle {
    fn compress(&self, src: &[u8], dst: &mut [u8]) -> Packed {
        debug_assert_eq!(src.len(), PAGE);
        if src.iter().all(|&b| b == 0) {
            return Packed::Zero;
        }
        // Формат: пары (count:u8, value:u8), count 1..=255 подряд идущих одинаковых байт.
        let mut o = 0usize;
        let mut i = 0usize;
        while i < PAGE {
            let v = src[i];
            let mut run = 1usize;
            while i + run < PAGE && src[i + run] == v && run < 255 {
                run += 1;
            }
            if o + 2 > dst.len() || o + 2 >= PAGE {
                return Packed::Incompressible;
            }
            dst[o] = run as u8;
            dst[o + 1] = v;
            o += 2;
            i += run;
        }
        if o >= PAGE {
            Packed::Incompressible
        } else {
            Packed::Bytes(o)
        }
    }

    fn decompress(&self, src: &[u8], dst: &mut [u8]) -> bool {
        debug_assert_eq!(dst.len(), PAGE);
        let mut o = 0usize;
        let mut i = 0usize;
        while i + 1 < src.len() {
            let run = src[i] as usize;
            let v = src[i + 1];
            if run == 0 || o + run > PAGE {
                return false;
            }
            dst[o..o + run].iter_mut().for_each(|b| *b = v);
            o += run;
            i += 2;
        }
        o == PAGE
    }

    fn name(&self) -> &'static str {
        "rle"
    }
}

#[cfg(windows)]
mod xpress {
    use super::{Packed, PageCodec, PAGE};

    // ntdll XPRESS — тот же кодек, что использует сжатие памяти Windows. Без аллокаций в пути распаковки:
    // рабочий буфер сжатия выделяется один раз на поток.
    #[allow(non_camel_case_types)]
    type NTSTATUS = i32;
    const COMPRESSION_FORMAT_XPRESS: u16 = 0x0003;
    const COMPRESSION_ENGINE_STANDARD: u16 = 0x0000;

    #[link(name = "ntdll")]
    extern "system" {
        fn RtlGetCompressionWorkSpaceSize(
            format_and_engine: u16,
            compress_ws: *mut u32,
            decompress_ws: *mut u32,
        ) -> NTSTATUS;
        fn RtlCompressBuffer(
            format_and_engine: u16,
            uncompressed: *const u8,
            uncompressed_size: u32,
            compressed: *mut u8,
            compressed_size: u32,
            uncompressed_chunk_size: u32,
            final_compressed_size: *mut u32,
            work_space: *mut u8,
        ) -> NTSTATUS;
        fn RtlDecompressBuffer(
            format: u16,
            uncompressed: *mut u8,
            uncompressed_size: u32,
            compressed: *const u8,
            compressed_size: u32,
            final_uncompressed_size: *mut u32,
        ) -> NTSTATUS;
    }

    thread_local! {
        static WS: std::cell::RefCell<Vec<u8>> = std::cell::RefCell::new(Vec::new());
    }

    pub struct Xpress;

    impl Xpress {
        fn ensure_ws() -> usize {
            let mut comp = 0u32;
            let mut decomp = 0u32;
            unsafe {
                RtlGetCompressionWorkSpaceSize(
                    COMPRESSION_FORMAT_XPRESS | COMPRESSION_ENGINE_STANDARD,
                    &mut comp,
                    &mut decomp,
                );
            }
            comp.max(1) as usize
        }
    }

    impl PageCodec for Xpress {
        fn compress(&self, src: &[u8], dst: &mut [u8]) -> Packed {
            debug_assert_eq!(src.len(), PAGE);
            if src.iter().all(|&b| b == 0) {
                return Packed::Zero;
            }
            let need = Self::ensure_ws();
            let mut out = 0u32;
            let status = WS.with(|ws| {
                let mut ws = ws.borrow_mut();
                if ws.len() < need {
                    ws.resize(need, 0); // одноразовый рост на поток, не в пути распаковки
                }
                unsafe {
                    RtlCompressBuffer(
                        COMPRESSION_FORMAT_XPRESS | COMPRESSION_ENGINE_STANDARD,
                        src.as_ptr(),
                        PAGE as u32,
                        dst.as_mut_ptr(),
                        dst.len() as u32,
                        PAGE as u32,
                        &mut out,
                        ws.as_mut_ptr(),
                    )
                }
            });
            if status < 0 || out as usize >= PAGE {
                Packed::Incompressible
            } else {
                Packed::Bytes(out as usize)
            }
        }

        fn decompress(&self, src: &[u8], dst: &mut [u8]) -> bool {
            debug_assert_eq!(dst.len(), PAGE);
            let mut out = 0u32;
            let status = unsafe {
                RtlDecompressBuffer(
                    COMPRESSION_FORMAT_XPRESS,
                    dst.as_mut_ptr(),
                    PAGE as u32,
                    src.as_ptr(),
                    src.len() as u32,
                    &mut out,
                )
            };
            status >= 0 && out as usize == PAGE
        }

        fn name(&self) -> &'static str {
            "xpress"
        }
    }
}

#[cfg(windows)]
pub use xpress::Xpress;
