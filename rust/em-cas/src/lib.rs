//! em-cas — content-addressed хранилище страниц 4 КиБ. Меняем не кодек, а единицу хранения:
//!  * Zero — нулевая страница, payload не хранится;
//!  * точный дубликат — одна запись на все одинаковые страницы (SHA-256, refcount, побайтовая сверка);
//!  * Delta — XOR с похожей базой (поиск по скетчу super-features), сжатый; база всегда цельная, глубина 1;
//!  * временна́я дельта — база подсказана вызывающим (прошлая сохранённая версия той же страницы);
//!  * Full / Raw — обычное сжатие или несжимаемая страница как есть.
//! Восстановление безопасно: generation-токен в ссылке и CRC32 исходной страницы на каждую запись — ошибка
//! возвращается ошибкой, молча неверных байтов не бывает. Payload'ы лежат в сегментах ОС 64 КиБ (seg.rs),
//! пустые сегменты сразу возвращаются ОС, компактор переносит payload'ы и освобождает полупустые.

pub mod codec;
pub mod seg;

pub use codec::{by_name, Codec, Lz4};
#[cfg(windows)]
pub use codec::Xpress;

use seg::{Heap, Loc};
use sha2::{Digest, Sha256};
use std::collections::HashMap;

pub const PAGE: usize = 4096;

/// Ссылка на сохранённую страницу. Копируется свободно; владение — через refcount (put/retain/release).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Ref {
    Zero,
    Ent { id: u32, gen: u32 },
}

/// Чем стала страница при put.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Zero,
    Dedup,
    Delta,
    Temporal,
    Full,
    Raw,
}

#[derive(Debug, PartialEq, Eq)]
pub enum CasError {
    /// Ссылка устарела: запись освобождена или переиспользована (поздний / повторный restore).
    Stale,
    /// Распакованные байты не совпали с CRC32 исходной страницы.
    Crc,
    /// Кодек отверг payload.
    Decode,
    /// ОС не дала память под сегмент.
    NoMemory,
}

#[derive(Clone, Copy, Debug)]
pub struct Config {
    pub dedup: bool,
    pub delta: bool,
    /// Сверять байты при совпадении хеша (защита от коллизий 64-битного ключа индекса).
    pub verify: bool,
    /// Дельту принимаем, только если она меньше `delta_max_pct`% от обычного сжатия.
    pub delta_max_pct: usize,
}

impl Default for Config {
    fn default() -> Self {
        Config { dedup: true, delta: true, verify: true, delta_max_pct: 50 }
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct CasStats {
    pub put_zero: u64,
    pub put_dedup: u64,
    pub put_delta: u64,
    pub put_temporal: u64,
    pub put_full: u64,
    pub put_incompressible: u64,
    pub live_entries: u64,
    pub payload_bytes: u64,
    pub segment_bytes: u64,
    pub meta_bytes: u64,
    pub crc_failures: u64,
    pub stale_refs: u64,
    pub compact_moves: u64,
    pub segments_released: u64,
}

impl CasStats {
    /// Сколько памяти хранилище реально держит: сегменты ОС + оценка индексов и таблицы записей.
    pub fn total_bytes(&self) -> u64 {
        self.segment_bytes + self.meta_bytes
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
enum EKind {
    Free,
    Full,
    Raw,
    Delta,
}

/// Запись хранилища: 48 байт на уникальную страницу (плюс индексы).
#[derive(Clone, Copy)]
struct Entry {
    key: u64,
    base: u32, // для Delta — id базы
    gen: u32,
    refcnt: u32,
    crc: u32,
    sf: [u32; 3],
    loc: Loc,
    len: u16,
    kind: EKind,
    has_sf: bool,
}

pub struct CasStore {
    codec: Box<dyn Codec>,
    cfg: Config,
    heap: Heap,
    ents: Vec<Entry>,
    free_ents: Vec<u32>,
    index: HashMap<u64, u32>, // первые 64 бита SHA-256 -> запись (совпадение сверяется побайтово)
    sf_index: HashMap<u64, u32>,
    st: CasStats,
}

// ------------------------------ хеши ------------------------------

static CRC_T: [u32; 256] = {
    let mut t = [0u32; 256];
    let mut i = 0;
    while i < 256 {
        let mut c = i as u32;
        let mut k = 0;
        while k < 8 {
            c = if c & 1 != 0 { 0xEDB8_8320 ^ (c >> 1) } else { c >> 1 };
            k += 1;
        }
        t[i] = c;
        i += 1;
    }
    t
};

pub fn crc32(b: &[u8]) -> u32 {
    let mut c = !0u32;
    for &x in b {
        c = CRC_T[((c ^ x as u32) & 0xff) as usize] ^ (c >> 8);
    }
    !c
}

/// Ключ содержимого: первые 128 бит SHA-256.
pub fn key(page: &[u8]) -> u128 {
    let d = Sha256::digest(page);
    u128::from_le_bytes(d[..16].try_into().unwrap())
}

fn key64(page: &[u8]) -> u64 {
    key(page) as u64
}

const SF_MUL: [u64; 12] = [
    0x9E37_79B9_7F4A_7C15, 0xC2B2_AE3D_27D4_EB4F, 0x1656_67B1_9E37_79F9, 0xD6E8_FEB8_6659_FD93,
    0xFF51_AFD7_ED55_8CCD, 0xC4CE_B9FE_1A85_EC53, 0x2545_F491_4F6C_DD1D, 0x9FB2_1C65_1E98_DF25,
    0xA076_1D64_78BD_642F, 0xE703_7ED1_A0B4_28DB, 0x8EBC_6AF0_9C88_C6E3, 0x5899_65CC_7537_4CC3,
];

/// Скетч похожести: 3 super-feature, каждая — хеш от 4 минимумов (min-hash по ненулевым 8-байтовым словам).
/// Страницы, отличающиеся немногими словами, с высокой вероятностью совпадают хотя бы в одной super-feature.
pub fn sketch(page: &[u8]) -> Option<[u32; 3]> {
    let mut mins = [u64::MAX; 12];
    let mut nz = 0;
    for (i, w) in page.chunks_exact(8).enumerate() {
        let w = u64::from_le_bytes(w.try_into().unwrap());
        if w == 0 {
            continue;
        }
        nz += 1;
        let v = w ^ (i as u64).wrapping_mul(0x100_0000_01B3); // позиция слова тоже важна
        for k in 0..12 {
            let mut h = v.wrapping_mul(SF_MUL[k]);
            h ^= h >> 29;
            if h < mins[k] {
                mins[k] = h;
            }
        }
    }
    if nz < 8 {
        return None;
    }
    let mut sf = [0u32; 3];
    for j in 0..3 {
        let mut h = 0xCBF2_9CE4_8422_2325u64;
        for k in 0..4 {
            h = (h ^ mins[j * 4 + k]).wrapping_mul(0x100_0000_01B3);
        }
        sf[j] = (h ^ (h >> 32)) as u32;
    }
    Some(sf)
}

fn is_zero(p: &[u8]) -> bool {
    let (pre, mid, post) = unsafe { p.align_to::<u64>() };
    pre.iter().all(|&b| b == 0) && mid.iter().all(|&w| w == 0) && post.iter().all(|&b| b == 0)
}

fn xor_into(dst: &mut [u8], a: &[u8], b: &[u8]) {
    for i in 0..dst.len() {
        dst[i] = a[i] ^ b[i];
    }
}

// ------------------------------ хранилище ------------------------------

impl CasStore {
    pub fn new(codec: Box<dyn Codec>, cfg: Config) -> Self {
        CasStore { codec, cfg, heap: Heap::default(), ents: Vec::new(), free_ents: Vec::new(), index: HashMap::new(), sf_index: HashMap::new(), st: CasStats::default() }
    }

    pub fn codec_name(&self) -> &'static str {
        self.codec.name()
    }
    pub fn config(&self) -> Config {
        self.cfg
    }

    fn ent(&self, r: Ref) -> Option<u32> {
        match r {
            Ref::Zero => None,
            Ref::Ent { id, gen } => {
                let e = self.ents.get(id as usize)?;
                (e.kind != EKind::Free && e.gen == gen).then_some(id)
            }
        }
    }

    /// Жива ли ссылка (не освобождена, не переиспользована).
    pub fn is_live(&self, r: Ref) -> bool {
        r == Ref::Zero || self.ent(r).is_some()
    }

    /// Распаковать запись `id` в dst (4096) без проверки CRC (её делает get).
    fn decode(&self, id: u32, dst: &mut [u8]) -> Result<(), CasError> {
        let e = self.ents[id as usize];
        let payload = self.heap.bytes(e.loc, e.len as usize);
        match e.kind {
            EKind::Raw => dst.copy_from_slice(payload),
            EKind::Full => {
                if !self.codec.decompress(payload, dst) {
                    return Err(CasError::Decode);
                }
            }
            EKind::Delta => {
                self.decode(e.base, dst)?;
                let mut x = [0u8; PAGE];
                if !self.codec.decompress(payload, &mut x) {
                    return Err(CasError::Decode);
                }
                for i in 0..PAGE {
                    dst[i] ^= x[i];
                }
            }
            EKind::Free => return Err(CasError::Stale),
        }
        Ok(())
    }

    /// Восстановить страницу. Ошибка — ссылка устарела или байты не прошли CRC; dst тогда не годится.
    pub fn get(&mut self, r: Ref, dst: &mut [u8]) -> Result<(), CasError> {
        let dst = &mut dst[..PAGE];
        let id = match r {
            Ref::Zero => {
                dst.fill(0);
                return Ok(());
            }
            _ => match self.ent(r) {
                Some(id) => id,
                None => {
                    self.st.stale_refs += 1;
                    return Err(CasError::Stale);
                }
            },
        };
        self.decode(id, dst)?;
        if crc32(dst) != self.ents[id as usize].crc {
            self.st.crc_failures += 1;
            return Err(CasError::Crc);
        }
        Ok(())
    }

    /// Ещё одна ссылка на ту же запись (например, держать прошлую версию страницы как базу временно́й дельты).
    pub fn retain(&mut self, r: Ref) -> bool {
        match self.ent(r) {
            Some(id) => {
                self.ents[id as usize].refcnt += 1;
                true
            }
            None => r == Ref::Zero,
        }
    }

    pub fn release(&mut self, r: Ref) {
        let Some(id) = self.ent(r) else { return };
        self.release_id(id);
    }

    fn release_id(&mut self, id: u32) {
        let e = &mut self.ents[id as usize];
        e.refcnt -= 1;
        if e.refcnt > 0 {
            return;
        }
        let e = *e;
        if self.index.get(&e.key) == Some(&id) {
            self.index.remove(&e.key);
        }
        if e.has_sf {
            for j in 0..3 {
                let k = (j as u64) << 32 | e.sf[j] as u64;
                if self.sf_index.get(&k) == Some(&id) {
                    self.sf_index.remove(&k);
                }
            }
        }
        self.heap.free(e.loc);
        self.st.payload_bytes -= e.len as u64;
        self.st.live_entries -= 1;
        let s = &mut self.ents[id as usize];
        s.kind = EKind::Free;
        s.gen = s.gen.wrapping_add(1);
        self.free_ents.push(id);
        if e.kind == EKind::Delta {
            self.release_id(e.base);
        }
    }

    fn new_entry(&mut self, kind: EKind, base: u32, crc: u32, key: u64, payload: &[u8], sf: Option<[u32; 3]>) -> Result<Ref, CasError> {
        let id = match self.free_ents.pop() {
            Some(id) => id,
            None => {
                self.ents.push(Entry { key: 0, base: 0, gen: 0, refcnt: 0, crc: 0, sf: [0; 3], loc: Loc { seg: 0, slot: 0 }, len: 0, kind: EKind::Free, has_sf: false });
                (self.ents.len() - 1) as u32
            }
        };
        let loc = match self.heap.alloc(payload.len(), id) {
            Some(l) => l,
            None => {
                self.free_ents.push(id);
                return Err(CasError::NoMemory);
            }
        };
        self.heap.bytes_mut(loc, payload.len()).copy_from_slice(payload);
        let gen = self.ents[id as usize].gen;
        self.ents[id as usize] = Entry { key, base, gen, refcnt: 1, crc, sf: sf.unwrap_or([0; 3]), loc, len: payload.len() as u16, kind, has_sf: sf.is_some() };
        if let Some(sf) = sf {
            for j in 0..3 {
                self.sf_index.insert((j as u64) << 32 | sf[j] as u64, id);
            }
        }
        if self.cfg.dedup {
            self.index.insert(key, id);
        }
        self.st.payload_bytes += payload.len() as u64;
        self.st.live_entries += 1;
        Ok(Ref::Ent { id, gen })
    }

    /// Сохранить страницу. `hint` — необязательная база (прошлая версия этой же страницы): временна́я дельта.
    /// Возвращает ссылку (refcount +1 у вызывающего) и то, чем стала страница.
    pub fn put(&mut self, page: &[u8], hint: Option<Ref>) -> Result<(Ref, Kind), CasError> {
        self.put_inner(page, hint, true).map(|r| r.unwrap())
    }

    /// Как put, но несжимаемую страницу без дубликата и без дельты не сохраняет: Ok(None) — держите её у себя.
    pub fn put_compressible(&mut self, page: &[u8], hint: Option<Ref>) -> Result<Option<(Ref, Kind)>, CasError> {
        self.put_inner(page, hint, false)
    }

    fn put_inner(&mut self, page: &[u8], hint: Option<Ref>, keep_raw: bool) -> Result<Option<(Ref, Kind)>, CasError> {
        let page = &page[..PAGE];
        if is_zero(page) {
            self.st.put_zero += 1;
            return Ok(Some((Ref::Zero, Kind::Zero)));
        }
        let k = key64(page);
        let mut tmp = [0u8; PAGE];
        if self.cfg.dedup {
            if let Some(&id) = self.index.get(&k) {
                let same = !self.cfg.verify || (self.decode(id, &mut tmp).is_ok() && tmp[..] == page[..]);
                if same {
                    let e = &mut self.ents[id as usize];
                    e.refcnt += 1;
                    self.st.put_dedup += 1;
                    return Ok(Some((Ref::Ent { id, gen: e.gen }, Kind::Dedup)));
                }
            }
        }
        let crc = crc32(page);
        let mut full = [0u8; PAGE];
        let flen = self.codec.compress(page, &mut full);
        let sf = sketch(page);
        let mut best: Option<(u32, usize, bool, [u8; PAGE])> = None;
        if self.cfg.delta {
            let limit = flen.unwrap_or(PAGE) * self.cfg.delta_max_pct / 100;
            let mut cands: Vec<(u32, bool)> = Vec::with_capacity(4);
            if let Some(h) = hint.and_then(|h| self.ent(h)) {
                cands.push((h, true));
            }
            if let Some(sf) = sf {
                for j in 0..3 {
                    if let Some(&id) = self.sf_index.get(&((j as u64) << 32 | sf[j] as u64)) {
                        if !cands.iter().any(|c| c.0 == id) {
                            cands.push((id, false));
                        }
                    }
                }
            }
            for (id, temporal) in cands {
                if matches!(self.ents[id as usize].kind, EKind::Delta | EKind::Free) {
                    continue; // база — только цельная запись: глубина цепочки 1
                }
                if self.decode(id, &mut tmp).is_err() {
                    continue;
                }
                let mut x = [0u8; PAGE];
                xor_into(&mut x, page, &tmp);
                let mut d = [0u8; PAGE];
                if let Some(n) = self.codec.compress(&x, &mut d) {
                    if n < limit && best.as_ref().map_or(true, |b| n < b.1) {
                        best = Some((id, n, temporal, d));
                    }
                }
            }
        }
        if let Some((base, n, temporal, d)) = best {
            self.ents[base as usize].refcnt += 1;
            match self.new_entry(EKind::Delta, base, crc, k, &d[..n], None) {
                Ok(r) => {
                    if temporal { self.st.put_temporal += 1 } else { self.st.put_delta += 1 }
                    return Ok(Some((r, if temporal { Kind::Temporal } else { Kind::Delta })));
                }
                Err(e) => {
                    self.release_id(base);
                    return Err(e);
                }
            }
        }
        match flen {
            Some(n) => {
                let r = self.new_entry(EKind::Full, 0, crc, k, &full[..n], sf)?;
                self.st.put_full += 1;
                Ok(Some((r, Kind::Full)))
            }
            None => {
                self.st.put_incompressible += 1;
                if !keep_raw {
                    return Ok(None);
                }
                let r = self.new_entry(EKind::Raw, 0, crc, k, page, sf)?;
                Ok(Some((r, Kind::Raw)))
            }
        }
    }

    /// Сжатый размер записи (для решений вызывающего), 0 для Zero.
    pub fn stored_len(&self, r: Ref) -> usize {
        self.ent(r).map_or(0, |id| self.ents[id as usize].len as usize)
    }

    /// Уплотнить сегменты: до `max_moves` переносов payload'ов. Возвращает число переносов.
    pub fn compact(&mut self, max_moves: usize) -> usize {
        let ents = &mut self.ents;
        let n = self.heap.compact(max_moves, |owner, loc| ents[owner as usize].loc = loc);
        n
    }

    pub fn stats(&self) -> CasStats {
        let mut s = self.st;
        s.segment_bytes = self.heap.committed;
        s.compact_moves = self.heap.moves;
        s.segments_released = self.heap.released;
        let idx = |n: usize, kv: usize| (n as f64 * (kv as f64 + 1.0) / 0.875) as u64; // hashbrown: ~7/8 заполнения
        s.meta_bytes = (self.ents.capacity() * std::mem::size_of::<Entry>()) as u64
            + idx(self.index.capacity(), 8 + 4)
            + idx(self.sf_index.capacity(), 8 + 4)
            + (self.free_ents.capacity() * 4) as u64;
        s
    }

    /// Только для тестов внедрения сбоев: испортить байт payload'а записи.
    #[doc(hidden)]
    pub fn corrupt_for_test(&mut self, r: Ref) {
        if let Some(id) = self.ent(r) {
            let e = self.ents[id as usize];
            let b = self.heap.bytes_mut(e.loc, e.len as usize);
            let i = b.len() / 2;
            b[i] ^= 0x5A;
        }
    }
}

#[cfg(test)]
mod tests;
