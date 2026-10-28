//! Переносимое ядро сжатой арены: состояние каждой 4-КиБ страницы и компактное хранилище сжатых payload.
//! Никаких системных вызовов — только логика «что хранить и кого вытеснять», поэтому тестируется на любой ОС.

use crate::codec::{Packed, PageCodec, PAGE};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum State {
    Zero,       // страница целиком нулевая (payload не хранится)
    Resident,   // живые байты у вызывающего (страница закоммичена)
    Probing,    // байты на месте, но страница PAGE_NOACCESS: ждём, тронут ли её (эмуляция accessed-бита)
    Compressed, // сжатая копия лежит в хранилище, физическая страница освобождена
    Pinned,     // несжимаема / нет квоты — оставлена резидентной
}

/// Размерные классы слотов хранилища (байт). Payload кладётся в наименьший подходящий класс.
const CLASSES: [usize; 10] = [64, 128, 256, 384, 512, 768, 1024, 1536, 2048, 3072];

struct Slot {
    off: u32,
    len: u16,
    class: u8, // индекс в CLASSES, 0xFF — нет слота
}

/// Хранилище сжатых страниц: один растущий регион + свободные списки по классам.
/// Регион растёт только при вытеснении (под внешним замком); путь распаковки лишь читает слот и
/// возвращает его в свободный список — без аллокаций.
struct Store {
    region: Vec<u8>,
    free: [Vec<u32>; CLASSES.len()],
    bump: usize,
    quota: usize,
    used_payload: u64,
    slots_bytes: u64, // сумма class-size занятых слотов (фактическая память хранилища)
}

impl Store {
    fn new(quota: usize) -> Self {
        Store {
            region: Vec::new(),
            free: Default::default(),
            bump: 0,
            quota,
            used_payload: 0,
            slots_bytes: 0,
        }
    }

    fn class_for(len: usize) -> Option<usize> {
        CLASSES.iter().position(|&c| len <= c)
    }

    /// Выделить слот под `len` байт. Может расти регион (вызывается под замком при вытеснении).
    fn alloc(&mut self, len: usize) -> Option<(usize, usize)> {
        let ci = Self::class_for(len)?;
        let csz = CLASSES[ci];
        if let Some(off) = self.free[ci].pop() {
            self.slots_bytes += csz as u64;
            return Some((off as usize, ci));
        }
        if self.bump + csz > self.quota {
            return None; // квота исчерпана
        }
        let off = self.bump;
        self.bump += csz;
        if self.region.len() < self.bump {
            self.region.resize(self.bump, 0);
        }
        self.slots_bytes += csz as u64;
        Some((off, ci))
    }

    fn freeing(&mut self, s: &Slot) {
        if s.class != 0xFF {
            let csz = CLASSES[s.class as usize];
            self.slots_bytes -= csz as u64;
            self.used_payload -= s.len as u64;
            self.free[s.class as usize].push(s.off);
        }
    }
}

pub struct Table {
    codec: Box<dyn PageCodec>,
    state: Vec<State>,
    slot: Vec<Slot>,
    stamp: Vec<u64>,
    clock: u64,
    store: Store,
    next_probe: Vec<u64>, // мс: раньше этого времени страницу не пробовать (горячая, экспоненциальная пауза)
    backoff: Vec<u32>,    // мс
    probe_at: Vec<u64>,   // мс: когда страница перешла в Probing
    pub probing: usize,
    pub probe_hits: u64,
    pub resident: usize,
    pub compressed: usize,
    pub zero: usize,
    pub pinned: usize,
    pub faults: u64,
    pub decompressed: u64,
    pub evictions: u64,
}

pub enum Fault {
    /// Страница стала резидентной; данные записаны в dst (распаковкой или обнулением).
    Filled { decompressed: bool },
    /// Страница уже резидентна — ничего делать не нужно.
    AlreadyResident,
    /// Попадание в пробу: байты на месте, вызывающий должен лишь вернуть PAGE_READWRITE.
    ProbeHit,
}

/// Пауза перед повторной пробой страницы, которую тронули: 2 с, ×2, до 2 мин.
fn grow(b: u32) -> u32 {
    if b < 1000 { 2000 } else { (b.saturating_mul(2)).min(120_000) }
}

impl Table {
    pub fn new(codec: Box<dyn PageCodec>, pages: usize, quota_bytes: usize) -> Self {
        let mut slot = Vec::with_capacity(pages);
        slot.resize_with(pages, || Slot {
            off: 0,
            len: 0,
            class: 0xFF,
        });
        Table {
            codec,
            state: vec![State::Zero; pages],
            slot,
            stamp: vec![0; pages],
            clock: 0,
            store: Store::new(quota_bytes),
            next_probe: vec![0; pages],
            backoff: vec![0; pages],
            probe_at: vec![0; pages],
            probing: 0,
            probe_hits: 0,
            resident: 0,
            compressed: 0,
            zero: pages,
            pinned: 0,
            faults: 0,
            decompressed: 0,
            evictions: 0,
        }
    }

    pub fn pages(&self) -> usize {
        self.state.len()
    }
    pub fn state(&self, p: usize) -> State {
        self.state[p]
    }
    pub fn store_bytes(&self) -> u64 {
        self.store.slots_bytes
    }
    pub fn payload_bytes(&self) -> u64 {
        self.store.used_payload
    }

    /// Обработать отказ доступа к странице `p`: заполнить `dst` (ровно 4096) её содержимым.
    pub fn on_fault(&mut self, p: usize, dst: &mut [u8], now: u64) -> Fault {
        match self.state[p] {
            State::Resident | State::Pinned => Fault::AlreadyResident,
            State::Probing => {
                // Страницу тронули во время пробы — она горячая. Распаковка не нужна.
                self.state[p] = State::Resident;
                self.probing -= 1;
                self.resident += 1;
                self.probe_hits += 1;
                self.backoff[p] = grow(self.backoff[p]);
                self.next_probe[p] = now + self.backoff[p] as u64;
                self.stamp[p] = self.tick();
                Fault::ProbeHit
            }
            State::Zero => {
                dst[..PAGE].iter_mut().for_each(|b| *b = 0);
                self.state[p] = State::Resident;
                self.zero -= 1;
                self.resident += 1;
                self.stamp[p] = self.tick();
                self.faults += 1;
                Fault::Filled {
                    decompressed: false,
                }
            }
            State::Compressed => {
                let s = &self.slot[p];
                let start = s.off as usize;
                let end = start + s.len as usize;
                let ok = self
                    .codec
                    .decompress(&self.store.region[start..end], &mut dst[..PAGE]);
                debug_assert!(ok, "decompress must restore page byte-for-byte");
                let s = Slot {
                    off: 0,
                    len: 0,
                    class: 0xFF,
                };
                let old = std::mem::replace(&mut self.slot[p], s);
                self.store.freeing(&old);
                self.state[p] = State::Resident;
                self.compressed -= 1;
                self.resident += 1;
                self.stamp[p] = self.tick();
                self.faults += 1;
                self.decompressed += 1;
                // Сжатую страницу всё же тронули: тёплая — не пробовать сразу снова.
                self.backoff[p] = grow(self.backoff[p]);
                self.next_probe[p] = now + self.backoff[p] as u64;
                Fault::Filled { decompressed: true }
            }
        }
    }

    /// Вытеснить резидентную страницу `p`, сжав её байты `src` (ровно 4096). Возвращает true, если
    /// физическую страницу можно освободить (Zero/Compressed). Pinned/переполнение квоты оставляют резидентной.
    pub fn evict(&mut self, p: usize, src: &[u8], scratch: &mut [u8]) -> bool {
        match self.state[p] {
            State::Resident => {}
            State::Probing => {
                // считаем как резидентную для единообразия переходов ниже
                self.probing -= 1;
                self.resident += 1;
                self.state[p] = State::Resident;
            }
            _ => return false,
        }
        match self.codec.compress(&src[..PAGE], scratch) {
            Packed::Zero => {
                self.state[p] = State::Zero;
                self.resident -= 1;
                self.zero += 1;
                self.evictions += 1;
                true
            }
            Packed::Bytes(len) => match self.store.alloc(len) {
                Some((off, ci)) => {
                    self.store.region[off..off + len].copy_from_slice(&scratch[..len]);
                    self.store.used_payload += len as u64;
                    self.slot[p] = Slot {
                        off: off as u32,
                        len: len as u16,
                        class: ci as u8,
                    };
                    self.state[p] = State::Compressed;
                    self.resident -= 1;
                    self.compressed += 1;
                    self.evictions += 1;
                    true
                }
                None => {
                    // квота исчерпана — держим резидентной
                    self.state[p] = State::Pinned;
                    self.resident -= 1;
                    self.pinned += 1;
                    false
                }
            },
            Packed::Incompressible => {
                self.state[p] = State::Pinned;
                self.resident -= 1;
                self.pinned += 1;
                false
            }
        }
    }

    /// До `count` самых холодных резидентных страниц (по времени последнего доступа).
    pub fn choose_cold(&self, count: usize, out: &mut Vec<usize>) {
        out.clear();
        for (i, &s) in self.state.iter().enumerate() {
            if s == State::Resident {
                out.push(i);
            }
        }
        out.sort_unstable_by_key(|&i| self.stamp[i]);
        out.truncate(count);
    }

    /// Начать пробу: Resident -> Probing (вызывающий ставит PAGE_NOACCESS). true, если переход выполнен.
    pub fn begin_probe(&mut self, p: usize, now: u64) -> bool {
        if self.state[p] != State::Resident || now < self.next_probe[p] {
            return false;
        }
        self.state[p] = State::Probing;
        self.resident -= 1;
        self.probing += 1;
        self.probe_at[p] = now;
        true
    }

    /// Собрать кандидатов в окне [from, to): пробовать (Resident, срок подошёл) и сжимать (Probing дольше idle_ms).
    pub fn candidates(&self, from: usize, to: usize, now: u64, idle_ms: u64, probe: &mut Vec<usize>, max_probe: usize, idle: &mut Vec<usize>) {
        for p in from..to.min(self.state.len()) {
            match self.state[p] {
                State::Resident if probe.len() < max_probe && now >= self.next_probe[p] => probe.push(p),
                State::Probing if now >= self.probe_at[p] + idle_ms => idle.push(p),
                _ => {}
            }
        }
    }

    fn tick(&mut self) -> u64 {
        self.clock += 1;
        self.clock
    }
}
