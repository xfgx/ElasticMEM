//! elastic-arena — прозрачно-сжатая память в пространстве пользователя для Windows.
//!
//! Идея «песочницы для памяти»: приложение (или движок) выделяет свои данные в арене, а не в обычной куче.
//! Арена держит горячие страницы распакованными, холодные — сжатыми, и прозрачно распаковывает их по
//! обращению через механизм отказов страниц (VEH), как `userfaultfd` в Linux. Windows при этом лишь
//! выдаёт и освобождает физические страницы; что и когда держать в памяти, решает арена.
//!
//! Это НЕ перехват памяти чужого процесса. Прозрачно управлять можно только теми страницами, которые
//! выделены через арену. Чтобы существующее приложение (Edge, игра) целиком работало в арене, нужен
//! лаунчер, который подменит его аллокатор (см. `launcher` и DESIGN.md) — с известными ограничениями
//! (Code Integrity Guard у Chromium, античиты у игр).

pub mod codec;
pub mod core;

pub use codec::{PageCodec, Rle, PAGE};

#[cfg(windows)]
pub mod arena;
#[cfg(windows)]
pub use arena::{Arena, Stats};
#[cfg(windows)]
pub use codec::Xpress;

// Перехват аллокатора чужого процесса и точка входа внедряемой DLL.
#[cfg(windows)]
pub mod hook;
#[cfg(windows)]
mod dllmain;

#[cfg(test)]
mod tests {
    use crate::codec::{Packed, PageCodec, Rle, PAGE};
    use crate::core::{State, Table};

    fn mk_page(seed: u64, kind: u8) -> [u8; PAGE] {
        let mut p = [0u8; PAGE];
        let mut x = seed.wrapping_mul(2654435761).wrapping_add(1);
        for i in 0..PAGE / 4 {
            let w = match kind {
                0 => (i as u32) & 1023, // мелкие числа
                1 => {
                    if i % 2 == 0 {
                        0x00007ff6
                    } else {
                        0x1000_0000 + ((i as u32 * 16) & 0xfffff)
                    }
                } // указатели
                2 => 0x2020_2020 ^ ((x as u32) & 0x1f1f1f1f), // текст с шумом
                _ => {
                    if i % 16 == 0 {
                        x as u32
                    } else {
                        0
                    }
                } // разреженное
            };
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            p[i * 4..i * 4 + 4].copy_from_slice(&w.to_le_bytes());
        }
        p
    }

    #[test]
    fn rle_roundtrip_lossless() {
        let c = Rle;
        for kind in 0..4u8 {
            for seed in 0..64u64 {
                let page = mk_page(seed, kind);
                let mut comp = [0u8; PAGE];
                match c.compress(&page, &mut comp) {
                    Packed::Zero => assert!(page.iter().all(|&b| b == 0)),
                    Packed::Bytes(n) => {
                        let mut back = [0u8; PAGE];
                        assert!(c.decompress(&comp[..n], &mut back));
                        assert_eq!(page, back, "kind {kind} seed {seed}");
                    }
                    Packed::Incompressible => {}
                }
            }
        }
    }

    #[test]
    fn zero_page_detected() {
        let c = Rle;
        let mut comp = [0u8; PAGE];
        assert!(matches!(c.compress(&[0u8; PAGE], &mut comp), Packed::Zero));
    }

    // Полный цикл состояний таблицы без Windows: вытеснение -> отказ -> распаковка, с проверкой байт в байт.
    #[test]
    fn table_evict_fault_is_lossless() {
        let pages = 256;
        let mut t = Table::new(Box::new(Rle), pages, pages * PAGE);
        let mut shadow: Vec<[u8; PAGE]> = (0..pages)
            .map(|i| mk_page(i as u64, (i % 4) as u8))
            .collect();
        // Сделать все страницы резидентными (эмулируем первое обращение из Zero + запись реальных байт).
        let mut buf = [0u8; PAGE];
        for p in 0..pages {
            t.on_fault(p, &mut buf, 0);
            // «приложение» записало свои байты
        }
        // Вытеснить все.
        let mut scratch = [0u8; PAGE];
        let mut compressed = 0;
        for p in 0..pages {
            // src — это актуальные байты страницы (в реальной арене читаются из памяти)
            if t.evict(p, &shadow[p], &mut scratch) {
                compressed += 1;
            }
        }
        assert!(compressed > 0, "some pages must compress");
        // Обратиться к каждой вытесненной (Compressed/Zero): распаковка должна дать те же байты.
        // Pinned/Resident в настоящей арене хранят байты прямо в памяти — в чистом тесте их не моделируем.
        for p in 0..pages {
            if !matches!(t.state(p), State::Compressed | State::Zero) {
                continue;
            }
            let mut page = [0u8; PAGE];
            t.on_fault(p, &mut page, 0);
            assert_eq!(page, shadow[p], "page {p} corrupted after evict/fault");
        }
        // Повторный цикл с изменением данных между вытеснениями.
        for p in 0..pages {
            // вернуть страницу в резидент, изменить байты, снова вытеснить
            let mut page = [0u8; PAGE];
            t.on_fault(p, &mut page, 0);
            shadow[p] = mk_page(p as u64 + 777, ((p + 1) % 4) as u8);
            t.evict(p, &shadow[p], &mut scratch);
        }
        for p in 0..pages {
            if !matches!(t.state(p), State::Compressed | State::Zero) {
                continue;
            }
            let mut page = [0u8; PAGE];
            t.on_fault(p, &mut page, 0);
            assert_eq!(page, shadow[p], "page {p} corrupted after second cycle");
        }
    }

    // Проба: тронутая страница становится горячей и не пробуется до истечения паузы; нетронутая сжимается.
    #[test]
    fn probe_marks_hot_and_idle_compresses() {
        use crate::core::Fault;
        let pages = 8;
        let mut t = Table::new(Box::new(Rle), pages, pages * PAGE);
        let mut buf = [0u8; PAGE];
        for p in 0..pages { t.on_fault(p, &mut buf, 0); }
        assert!(t.begin_probe(0, 100));
        assert!(t.begin_probe(1, 100));
        assert!(matches!(t.on_fault(0, &mut buf, 150), Fault::ProbeHit));
        assert_eq!(t.state(0), State::Resident);
        assert!(!t.begin_probe(0, 500), "hot page must wait for backoff");
        assert!(t.begin_probe(0, 150 + 2000));
        let (mut pr, mut idle) = (Vec::new(), Vec::new());
        t.candidates(0, pages, 100 + 1500, 1000, &mut pr, 16, &mut idle);
        assert!(idle.contains(&1), "untouched probed page is idle");
        let pg = mk_page(1, 3);
        let mut scratch = [0u8; PAGE];
        assert!(t.evict(1, &pg, &mut scratch));
        let mut back = [0u8; PAGE];
        t.on_fault(1, &mut back, 3000);
        assert_eq!(back, pg);
    }

    #[test]
    fn quota_pins_when_full() {
        let pages = 64;
        // Квота всего на несколько слотов — остальные страницы останутся Pinned, но без потерь.
        let mut t = Table::new(Box::new(Rle), pages, 512);
        let mut buf = [0u8; PAGE];
        let pg: Vec<[u8; PAGE]> = (0..pages).map(|i| mk_page(i as u64, 3)).collect();
        for p in 0..pages {
            t.on_fault(p, &mut buf, 0);
        }
        let mut scratch = [0u8; PAGE];
        for p in 0..pages {
            t.evict(p, &pg[p], &mut scratch);
        }
        assert!(t.pinned > 0, "quota should force some pins");
        for p in 0..pages {
            let mut page = [0u8; PAGE];
            if t.state(p) == State::Compressed || t.state(p) == State::Zero {
                t.on_fault(p, &mut page, 0);
                assert_eq!(page, pg[p]);
            }
        }
    }
}
