use super::*;

struct Rng(u64);
impl Rng {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

/// Страница, похожая на кучу: указатели, мелкие числа, текст, разреженные данные, шум.
fn heap_page(seed: u64, kind: u64) -> Vec<u8> {
    let mut r = Rng(seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1);
    let mut p = vec![0u8; PAGE];
    for (i, w) in p.chunks_exact_mut(8).enumerate() {
        let v: u64 = match kind % 5 {
            0 => (i as u64 * 7 + seed) & 1023,
            1 => if i & 1 == 0 { 0x7ff6_1000_0000 + (r.next() & 0xffff) * 16 } else { r.next() & 0x3ff },
            2 => 0x2020_2020_2020_2020 ^ (r.next() & 0x1f1f_1f1f_1f1f_1f1f),
            3 => if i % 16 == 0 { r.next() } else { 0 },
            _ => r.next(),
        };
        w.copy_from_slice(&v.to_le_bytes());
    }
    p
}

fn store() -> CasStore {
    CasStore::new(Box::new(Lz4), Config::default())
}

#[test]
fn roundtrip_lossless_mixed() {
    let mut s = store();
    let mut r = Rng(7);
    let mut pages: Vec<Vec<u8>> = Vec::new();
    for i in 0..3000u64 {
        let p = match i % 10 {
            0 | 1 => vec![0u8; PAGE],
            2 if i > 20 => pages[(r.next() % i) as usize % pages.len()].clone(), // точная копия
            3 if i > 20 => {
                let mut p: Vec<u8> = pages[(r.next() % i) as usize % pages.len()].clone();
                for _ in 0..3 { let o = (r.next() as usize % (PAGE / 8)) * 8; p[o] ^= 0xA5; }
                p // почти копия
            }
            _ => heap_page(i, i),
        };
        pages.push(p);
    }
    let refs: Vec<Ref> = pages.iter().map(|p| s.put(p, None).unwrap().0).collect();
    let st = s.stats();
    assert!(st.put_zero > 0 && st.put_dedup > 0 && st.put_delta > 0 && st.put_full > 0, "{st:?}");
    let mut b = vec![0u8; PAGE];
    for (p, r) in pages.iter().zip(&refs) {
        s.get(*r, &mut b).unwrap();
        assert_eq!(&b, p);
    }
    for r in &refs {
        s.release(*r);
    }
    let st = s.stats();
    assert_eq!(st.live_entries, 0);
    assert_eq!(st.payload_bytes, 0);
    assert_eq!(st.segment_bytes, 0, "все сегменты должны вернуться ОС");
}

#[test]
fn dedup_shares_one_entry_and_refcounts() {
    let mut s = store();
    let p = heap_page(1, 1);
    let (a, ka) = s.put(&p, None).unwrap();
    let (b, kb) = s.put(&p, None).unwrap();
    assert_eq!((ka, kb), (Kind::Full, Kind::Dedup));
    assert_eq!(a, b);
    s.release(a);
    let mut out = vec![0u8; PAGE];
    s.get(b, &mut out).unwrap();
    assert_eq!(out, p);
    s.release(b);
    assert_eq!(s.get(b, &mut out), Err(CasError::Stale));
}

#[test]
fn delta_base_stays_alive_while_referenced() {
    let mut s = store();
    let base = heap_page(5, 1);
    let mut near = base.clone();
    near[100] ^= 1;
    near[2000] ^= 0x40;
    let (rb, _) = s.put(&base, None).unwrap();
    let (rn, kn) = s.put(&near, None).unwrap();
    assert_eq!(kn, Kind::Delta);
    assert!(s.stored_len(rn) < s.stored_len(rb) / 2);
    s.release(rb); // база уходит у владельца, но держится дельтой
    let mut out = vec![0u8; PAGE];
    s.get(rn, &mut out).unwrap();
    assert_eq!(out, near);
    assert_eq!(s.get(rb, &mut out), Ok(()), "та же запись базы жива (refcount дельты)");
    s.release(rn);
    assert_eq!(s.stats().live_entries, 0);
}

#[test]
fn temporal_hint_beats_plain() {
    let mut s = store();
    let v1 = heap_page(9, 4); // шум: сам по себе не сжимается
    let (r1, k1) = s.put(&v1, None).unwrap();
    assert_eq!(k1, Kind::Raw);
    let mut v2 = v1.clone();
    v2[8..16].copy_from_slice(&123u64.to_le_bytes());
    let (r2, k2) = s.put(&v2, Some(r1)).unwrap();
    assert!(matches!(k2, Kind::Temporal | Kind::Delta), "{k2:?}");
    assert!(s.stored_len(r2) < 200);
    s.release(r1);
    let mut out = vec![0u8; PAGE];
    s.get(r2, &mut out).unwrap();
    assert_eq!(out, v2);
}

#[test]
fn stale_generation_is_rejected() {
    let mut s = store();
    let (a, _) = s.put(&heap_page(1, 0), None).unwrap();
    s.release(a);
    let (b, _) = s.put(&heap_page(2, 0), None).unwrap(); // переиспользует ту же запись с новым gen
    let mut out = vec![0u8; PAGE];
    assert_eq!(s.get(a, &mut out), Err(CasError::Stale));
    s.get(b, &mut out).unwrap();
    assert_eq!(out, heap_page(2, 0));
}

#[test]
fn corrupted_payload_is_detected_not_returned() {
    let mut s = store();
    for kind in [0u64, 1, 2, 4] {
        let p = heap_page(kind + 10, kind);
        let (r, _) = s.put(&p, None).unwrap();
        s.corrupt_for_test(r);
        let mut out = vec![0u8; PAGE];
        let e = s.get(r, &mut out);
        assert!(matches!(e, Err(CasError::Crc) | Err(CasError::Decode)), "kind {kind}: {e:?}");
    }
    assert!(s.stats().crc_failures + 0 >= 1);
}

#[test]
fn compaction_moves_payloads_and_frees_segments() {
    let mut s = store();
    let pages: Vec<Vec<u8>> = (0..4000u64).map(|i| heap_page(i, 1 + (i % 2) * 2)).collect();
    let refs: Vec<Ref> = pages.iter().map(|p| s.put(p, None).unwrap().0).collect();
    let before = s.stats().segment_bytes;
    // освободить 3 из 4 страниц вразброс — сегменты остаются полупустыми
    for (i, r) in refs.iter().enumerate() {
        if i % 4 != 0 {
            s.release(*r);
        }
    }
    let mid = s.stats().segment_bytes;
    let mut moved = 0;
    loop {
        let n = s.compact(10_000);
        moved += n;
        if n == 0 { break; }
    }
    let after = s.stats().segment_bytes;
    assert!(moved > 0 && after < mid && mid <= before, "before={before} mid={mid} after={after} moved={moved}");
    let mut out = vec![0u8; PAGE];
    for (i, r) in refs.iter().enumerate() {
        if i % 4 == 0 {
            s.get(*r, &mut out).unwrap();
            assert_eq!(out, pages[i], "page {i} after compaction");
        }
    }
}

#[test]
fn no_dedup_no_delta_config_is_plain_compression() {
    let mut s = CasStore::new(Box::new(Lz4), Config { dedup: false, delta: false, ..Config::default() });
    let p = heap_page(3, 1);
    let (a, _) = s.put(&p, None).unwrap();
    let (b, kb) = s.put(&p, None).unwrap();
    assert_ne!(a, b);
    assert_eq!(kb, Kind::Full);
}

#[test]
fn entry_is_compact() {
    assert!(std::mem::size_of::<super::Entry>() <= 48, "Entry = {} байт", std::mem::size_of::<super::Entry>());
}

#[test]
fn warm_codec_roundtrip_and_mixed_delta() {
    struct Plain;
    impl Codec for Plain {
        fn compress(&self, src: &[u8], dst: &mut [u8]) -> Option<usize> {
            // «кодек» без сжатия нулей в конце: проверяет, что распаковка берёт кодек записи, а не хранилища
            let n = src.iter().rposition(|&b| b != 0).map_or(1, |i| i + 1);
            if n >= src.len() { return None; }
            dst[..n].copy_from_slice(&src[..n]);
            dst[n..n + 2].copy_from_slice(&(n as u16).to_le_bytes());
            Some(n + 2)
        }
        fn decompress(&self, src: &[u8], dst: &mut [u8]) -> bool {
            if src.len() < 2 { return false; }
            let n = u16::from_le_bytes(src[src.len() - 2..].try_into().unwrap()) as usize;
            if n + 2 != src.len() || n > dst.len() { return false; }
            dst[..n].copy_from_slice(&src[..n]);
            dst[n..].fill(0);
            true
        }
        fn name(&self) -> &'static str { "plain" }
    }
    let mut s = CasStore::with_alt(Box::new(Lz4), Box::new(Plain), Config::default());
    let pages: Vec<Vec<u8>> = (0..400u64).map(|i| { let mut p = heap_page(i % 50, 3); p[PAGE - 64..].fill(0); if i % 3 == 0 { p[10] ^= 1; } p }).collect();
    let refs: Vec<Ref> = pages.iter().enumerate().map(|(i, p)| if i % 2 == 0 { s.put_warm(p, None).unwrap().0 } else { s.put(p, None).unwrap().0 }).collect();
    let mut out = vec![0u8; PAGE];
    for (p, r) in pages.iter().zip(&refs) {
        s.get(*r, &mut out).unwrap();
        assert_eq!(&out, p);
    }
    assert!(s.codec_name().contains("plain"));
}

#[test]
fn plain_config_builds_no_similarity_index() {
    let mut s = CasStore::new(Box::new(Lz4), Config { dedup: false, delta: false, ..Config::default() });
    for i in 0..2000u64 {
        s.put(&heap_page(i, 1), None).unwrap();
    }
    assert!(s.sf_index.is_empty() && s.index.is_empty());
}
