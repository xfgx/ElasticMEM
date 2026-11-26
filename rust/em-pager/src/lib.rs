//! em-pager — пейджер памяти гостя для VMM на Windows Hypervisor Platform (QEMU -accel whpx).
//!
//! Память гостя — обычная память процесса VMM, отображённая в раздел через WHvMapGpaRange. Пейджер владеет
//! холодной её частью, кластерами по `cluster_pages` страниц (по умолчанию 16 = 64 КиБ: один exit WHP стоит
//! ~0,17 мс, поэтому восстанавливаем сразу кластер):
//!
//!  1. Resident -> Probing: снять отображение кластера у гостя (WHvUnmapGpaRange). Байты остаются на месте.
//!  2. Гость тронул кластер: memory-access exit с GpaUnmapped. Отображаем обратно, кластер горячий
//!     (пауза перед следующей пробой 2 с, ×2, до 2 мин), инструкция гостя повторяется.
//!  3. Probing дольше idle_ms: хостовые страницы только на чтение, каждая страница -> em-cas
//!     (zero / dedup / XOR-delta / временна́я дельта), память хоста освобождается (MEM_DECOMMIT) -> Evicted.
//!  4. Обращение к Evicted: гость — через exit, сам VMM (эмуляция дисков, DMA) — через access violation
//!     (VEH). Коммит, распаковка с проверкой CRC32 и generation, отображение обратно.
//!
//! Ядро переносимое: все системные действия — через трейт Ops, поэтому логика тестируется без Windows.
//! Любая ошибка восстановления возвращается как Err: вызывающий обязан остановиться (fail-closed).

use em_cas::{CasError, CasStore, Kind, Ref, PAGE};

#[cfg(windows)]
pub mod win;

#[derive(Clone, Copy, Debug)]
pub struct Cfg {
    pub cluster_pages: usize,
    pub idle_ms: u64,
    /// Диапазоны меньше этого (ROM, VGA, низкая память) не трогаем.
    pub min_range_bytes: u64,
    /// Временна́я дельта: после восстановления держать прошлые версии страниц базами следующего вытеснения.
    pub keep_ghost: bool,
}

impl Default for Cfg {
    fn default() -> Self {
        Cfg { cluster_pages: 16, idle_ms: 2000, min_range_bytes: 64 << 20, keep_ghost: false }
    }
}

/// Системные операции пейджера (WHP + VirtualAlloc на Windows, модель в тестах).
pub trait Ops {
    fn map(&self, gpa: u64, hva: *mut u8, len: usize, write: bool) -> bool;
    fn unmap(&self, gpa: u64, len: usize) -> bool;
    fn commit(&self, hva: *mut u8, len: usize) -> bool;
    fn decommit(&self, hva: *mut u8, len: usize);
    fn protect_ro(&self, hva: *mut u8, len: usize);
    fn protect_rw(&self, hva: *mut u8, len: usize);
    /// Остановить все остальные потоки процесса VMM на время «коммит + заполнение» кластера.
    /// Иначе поток, который тронет страницу сразу после коммита, увидит нули, а его запись затрётся
    /// восстановлением: ОС считает свежезакоммиченную страницу доступной (demand-zero) ещё до заполнения.
    fn freeze(&self) {}
    fn thaw(&self) {}
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Cl {
    Resident,
    Probing,
    Evicted,
}

pub struct Range {
    pub gpa: u64,
    pub hva: usize,
    pub bytes: u64,
    pub managed: bool,
    pub st: Vec<Cl>,
    next_probe: Vec<u64>,
    backoff: Vec<u32>,
    probe_at: Vec<u64>,
    refs: Vec<Ref>,          // по странице
    ghost: Vec<Option<Ref>>, // по странице (только при keep_ghost)
}

#[derive(Clone, Debug, Default)]
pub struct Stats {
    pub guest_bytes: u64,
    pub resident_cl: u64,
    pub probing_cl: u64,
    pub evicted_cl: u64,
    pub zero_pages_evicted: u64,
    pub probes: u64,
    pub probe_hits: u64,
    pub evictions: u64,
    pub evict_rollbacks: u64,
    pub exit_restores: u64,
    pub host_restores: u64,
    pub ghost_pages: u64,
    /// Гистограмма времени восстановления: корзина k — до 2^(k+1) мкс.
    pub restore_us_hist: [u64; 24],
}

pub struct Core {
    pub cfg: Cfg,
    pub cas: CasStore,
    pub ranges: Vec<Range>,
    pub st: Stats,
    cursor: (usize, usize),
    ghost_fifo: std::collections::VecDeque<(usize, usize)>,
    ghost_live: usize,
    scratch: Vec<u8>,
    restore_us: Vec<u32>, // последние 8192 времени восстановления (мкс) для точных квантилей
    restore_n: usize,
}

fn grow(b: u32) -> u32 {
    if b < 1000 { 2000 } else { b.saturating_mul(2).min(120_000) }
}

impl Core {
    pub fn new(cfg: Cfg, cas: CasStore) -> Self {
        Core { cfg, cas, ranges: Vec::new(), st: Stats::default(), cursor: (0, 0), ghost_fifo: Default::default(), ghost_live: 0, scratch: Vec::new(), restore_us: Vec::new(), restore_n: 0 }
    }

    fn clb(&self) -> u64 {
        (self.cfg.cluster_pages * PAGE) as u64
    }

    /// Новый отображённый диапазон RAM гостя. Возвращает, взят ли он под управление.
    pub fn add_range(&mut self, gpa: u64, hva: *mut u8, bytes: u64, writable: bool) -> bool {
        let clb = self.clb();
        let hva = hva as usize;
        let overlap = self.ranges.iter().position(|r| hva < r.hva + r.bytes as usize && r.hva < hva + bytes as usize);
        let mut managed = writable && bytes >= self.cfg.min_range_bytes && gpa % clb == 0 && hva as u64 % PAGE as u64 == 0;
        if let Some(i) = overlap {
            // одна и та же память гостя видна по двум GPA (алиас): вытеснять через один из них нельзя
            if self.ranges[i].st.iter().all(|&s| s == Cl::Resident) {
                self.ranges[i].managed = false;
            }
            managed = false;
        }
        let n = if managed { (bytes / clb) as usize } else { 0 };
        let pages = n * self.cfg.cluster_pages;
        self.ranges.push(Range {
            gpa, hva, bytes, managed,
            st: vec![Cl::Resident; n], next_probe: vec![0; n], backoff: vec![0; n], probe_at: vec![0; n],
            refs: vec![Ref::Zero; pages],
            ghost: if self.cfg.keep_ghost { vec![None; pages] } else { Vec::new() },
        });
        managed
    }

    /// Диапазон уходит из раздела: сначала вернуть все его байты в память хоста (и отображение гостю —
    /// для частей вне удаляемого окна), затем забыть.
    pub fn del_range(&mut self, gpa: u64, bytes: u64, ops: &dyn Ops) -> Result<(), CasError> {
        let mut i = 0;
        while i < self.ranges.len() {
            let r = &self.ranges[i];
            if gpa < r.gpa + r.bytes && r.gpa < gpa + bytes {
                for cl in 0..self.ranges[i].st.len() {
                    match self.ranges[i].st[cl] {
                        Cl::Resident => {}
                        Cl::Probing => {
                            self.map_cl(i, cl, ops);
                            self.ranges[i].st[cl] = Cl::Resident;
                        }
                        Cl::Evicted => {
                            self.restore(i, cl, ops)?;
                            self.map_cl(i, cl, ops);
                        }
                    }
                }
                self.drop_ghosts(i);
                self.ranges.remove(i);
                self.ghost_fifo.retain(|&(ri, _)| ri != i);
                for g in self.ghost_fifo.iter_mut() {
                    if g.0 > i {
                        g.0 -= 1;
                    }
                }
                self.cursor = (0, 0);
            } else {
                i += 1;
            }
        }
        Ok(())
    }

    fn drop_ghosts(&mut self, ri: usize) {
        let gs: Vec<Ref> = self.ranges[ri].ghost.iter_mut().filter_map(|g| g.take()).collect();
        for g in gs {
            self.cas.release(g);
            self.ghost_live -= 1;
        }
    }

    pub fn find_gpa(&self, gpa: u64) -> Option<(usize, usize)> {
        let clb = self.clb();
        self.ranges.iter().enumerate().find_map(|(i, r)| {
            if !r.managed || gpa < r.gpa || gpa >= r.gpa + r.bytes {
                return None;
            }
            let cl = ((gpa - r.gpa) / clb) as usize;
            (cl < r.st.len()).then_some((i, cl))
        })
    }

    pub fn find_hva(&self, a: usize) -> Option<(usize, usize)> {
        let clb = self.clb() as usize;
        self.ranges.iter().enumerate().find_map(|(i, r)| {
            if !r.managed || a < r.hva || a >= r.hva + r.bytes as usize {
                return None;
            }
            let cl = (a - r.hva) / clb;
            (cl < r.st.len()).then_some((i, cl))
        })
    }

    fn cl_addr(&self, ri: usize, cl: usize) -> (u64, *mut u8) {
        let r = &self.ranges[ri];
        let off = cl as u64 * self.clb();
        (r.gpa + off, (r.hva + off as usize) as *mut u8)
    }

    fn map_cl(&self, ri: usize, cl: usize, ops: &dyn Ops) -> bool {
        let (g, h) = self.cl_addr(ri, cl);
        ops.map(g, h, self.clb() as usize, true)
    }

    /// Вернуть байты Evicted-кластера в память хоста (без отображения гостю). Сначала распаковка с проверкой
    /// в буфер, затем — при остановленных остальных потоках — коммит и копирование: никто не увидит
    /// закоммиченную, но ещё не заполненную страницу.
    fn restore(&mut self, ri: usize, cl: usize, ops: &dyn Ops) -> Result<(), CasError> {
        let (_, h) = self.cl_addr(ri, cl);
        let n = self.cfg.cluster_pages;
        let mut buf = std::mem::take(&mut self.scratch);
        buf.resize(n * PAGE, 0);
        for k in 0..n {
            let r = self.ranges[ri].refs[cl * n + k];
            if let Err(e) = self.cas.get(r, &mut buf[k * PAGE..(k + 1) * PAGE]) {
                self.scratch = buf;
                return Err(e);
            }
        }
        ops.freeze();
        let ok = ops.commit(h, n * PAGE);
        if ok {
            unsafe { std::ptr::copy_nonoverlapping(buf.as_ptr(), h, n * PAGE) };
        }
        ops.thaw();
        self.scratch = buf;
        if !ok {
            return Err(CasError::NoMemory);
        }
        for k in 0..n {
            let pi = cl * n + k;
            let r = std::mem::replace(&mut self.ranges[ri].refs[pi], Ref::Zero);
            if self.cfg.keep_ghost && r != Ref::Zero {
                self.ranges[ri].ghost[pi] = Some(r);
                self.ghost_live += 1;
                self.ghost_fifo.push_back((ri, pi));
            } else {
                self.cas.release(r);
            }
        }
        self.ranges[ri].st[cl] = Cl::Resident;
        Ok(())
    }

    fn mark_hot(&mut self, ri: usize, cl: usize, now: u64) {
        let r = &mut self.ranges[ri];
        r.backoff[cl] = grow(r.backoff[cl]);
        r.next_probe[cl] = now + r.backoff[cl] as u64;
    }

    /// Memory-access exit гостя по GPA этого кластера. Ok(true) — повторить инструкцию.
    pub fn on_exit(&mut self, ri: usize, cl: usize, now: u64, ops: &dyn Ops) -> Result<bool, CasError> {
        match self.ranges[ri].st[cl] {
            Cl::Resident => {
                // другой vCPU уже вернул кластер — или отображение пропало не по нашей вине: отобразить ещё раз
                Ok(self.map_cl(ri, cl, ops))
            }
            Cl::Probing => {
                if !self.map_cl(ri, cl, ops) {
                    return Ok(false);
                }
                self.ranges[ri].st[cl] = Cl::Resident;
                self.st.probe_hits += 1;
                self.mark_hot(ri, cl, now);
                Ok(true)
            }
            Cl::Evicted => {
                self.restore(ri, cl, ops)?;
                let ok = self.map_cl(ri, cl, ops);
                self.st.exit_restores += 1;
                self.mark_hot(ri, cl, now);
                Ok(ok)
            }
        }
    }

    /// Обращение самого VMM к памяти Evicted-кластера (access violation). Ok(true) — повторить инструкцию.
    pub fn on_host_fault(&mut self, ri: usize, cl: usize, now: u64, ops: &dyn Ops) -> Result<bool, CasError> {
        match self.ranges[ri].st[cl] {
            Cl::Evicted => {
                self.restore(ri, cl, ops)?;
                self.map_cl(ri, cl, ops);
                self.st.host_restores += 1;
                self.mark_hot(ri, cl, now);
                Ok(true)
            }
            // Resident/Probing: память хоста закоммичена и доступна на запись — отказ был в окне вытеснения
            _ => Ok(true),
        }
    }

    /// Кандидаты за один проход: пробовать (Resident, срок подошёл) и вытеснять (Probing дольше idle_ms).
    /// Сканирует не больше `scan` кластеров от курсора по кругу.
    pub fn candidates(&mut self, now: u64, budget: usize, scan: usize, probe: &mut Vec<(usize, usize)>, idle: &mut Vec<(usize, usize)>) {
        let total: usize = self.ranges.iter().map(|r| r.st.len()).sum();
        if total == 0 {
            return;
        }
        let (mut ri, mut cl) = self.cursor;
        for _ in 0..scan.min(total) {
            while ri >= self.ranges.len() || cl >= self.ranges[ri].st.len() {
                if ri >= self.ranges.len() {
                    ri = 0;
                } else {
                    ri += 1;
                }
                cl = 0;
            }
            let r = &self.ranges[ri];
            match r.st[cl] {
                Cl::Resident if probe.len() < budget && now >= r.next_probe[cl] => probe.push((ri, cl)),
                Cl::Probing if now >= r.probe_at[cl] + self.cfg.idle_ms => idle.push((ri, cl)),
                _ => {}
            }
            cl += 1;
        }
        self.cursor = (ri, cl);
    }

    pub fn begin_probe(&mut self, ri: usize, cl: usize, now: u64, ops: &dyn Ops) -> bool {
        if ri >= self.ranges.len() || cl >= self.ranges[ri].st.len() {
            return false;
        }
        let r = &self.ranges[ri];
        if r.st[cl] != Cl::Resident || now < r.next_probe[cl] {
            return false;
        }
        let (g, _) = self.cl_addr(ri, cl);
        if !ops.unmap(g, self.clb() as usize) {
            return false;
        }
        let r = &mut self.ranges[ri];
        r.st[cl] = Cl::Probing;
        r.probe_at[cl] = now;
        self.st.probes += 1;
        true
    }

    /// Вытеснить нетронутый Probing-кластер в em-cas. false — не вытеснен (уже не Probing / не выгодно).
    pub fn evict(&mut self, ri: usize, cl: usize, now: u64, ops: &dyn Ops) -> bool {
        if ri >= self.ranges.len() || cl >= self.ranges[ri].st.len() || self.ranges[ri].st[cl] != Cl::Probing {
            return false;
        }
        let n = self.cfg.cluster_pages;
        let (_, h) = self.cl_addr(ri, cl);
        ops.protect_ro(h, n * PAGE); // VMM, пишущий сюда, встанет на замок пейджера и повторит запись после
        let src = unsafe { std::slice::from_raw_parts(h, n * PAGE) };
        let mut refs = [Ref::Zero; 256];
        let (mut zero, mut kept_raw, mut stored) = (0u64, 0usize, 0usize);
        let mut ok = true;
        for k in 0..n {
            let pi = cl * n + k;
            let hint = if self.cfg.keep_ghost { self.ranges[ri].ghost[pi] } else { None };
            // Phase-aware: кластер, который уже возвращали (тёплый), сжимается быстрым кодеком (если задан),
            // холодный — плотным. Без второго кодека put_warm == put.
            let page = &src[k * PAGE..(k + 1) * PAGE];
            let res = if self.ranges[ri].backoff[cl] > 0 { self.cas.put_warm(page, hint) } else { self.cas.put(page, hint) };
            match res {
                Ok((r, kind)) => {
                    refs[k] = r;
                    match kind {
                        Kind::Zero => zero += 1,
                        Kind::Raw => kept_raw += 1,
                        _ => {}
                    }
                    stored += self.cas.stored_len(r);
                }
                Err(_) => {
                    ok = false;
                    for r in refs.iter().take(k) {
                        self.cas.release(*r);
                    }
                    break;
                }
            }
        }
        // Выгода меньше четверти кластера (почти всё несжимаемо) — не вытесняем, кластер надолго горячий.
        if ok && (kept_raw * 4 > n * 3 || stored * 4 > n * PAGE * 3) {
            for r in refs.iter().take(n) {
                self.cas.release(*r);
            }
            ok = false;
        }
        if self.cfg.keep_ghost {
            for k in 0..n {
                if let Some(g) = self.ranges[ri].ghost[cl * n + k].take() {
                    self.cas.release(g);
                    self.ghost_live -= 1;
                }
            }
        }
        if !ok {
            ops.protect_rw(h, n * PAGE);
            self.map_cl(ri, cl, ops);
            let r = &mut self.ranges[ri];
            r.st[cl] = Cl::Resident;
            r.backoff[cl] = 120_000;
            r.next_probe[cl] = now + 120_000;
            self.st.evict_rollbacks += 1;
            return false;
        }
        self.ranges[ri].refs[cl * n..cl * n + n].copy_from_slice(&refs[..n]);
        ops.decommit(h, n * PAGE);
        self.ranges[ri].st[cl] = Cl::Evicted;
        self.st.evictions += 1;
        self.st.zero_pages_evicted += zero;
        true
    }

    /// Обслуживание: уплотнить хранилище и ограничить число прошлых версий (временна́я дельта).
    pub fn maintain(&mut self, max_moves: usize, ghost_budget_bytes: u64) -> usize {
        let max_ghost = (ghost_budget_bytes / PAGE as u64) as usize;
        while self.ghost_live > max_ghost {
            let Some((ri, pi)) = self.ghost_fifo.pop_front() else { break };
            if let Some(g) = self.ranges.get_mut(ri).and_then(|r| r.ghost.get_mut(pi)).and_then(|g| g.take()) {
                self.cas.release(g);
                self.ghost_live -= 1;
            }
        }
        if self.ghost_fifo.len() > 4 * max_ghost + 4096 {
            let rs = &self.ranges;
            self.ghost_fifo.retain(|&(ri, pi)| rs.get(ri).and_then(|r| r.ghost.get(pi)).is_some_and(|g| g.is_some()));
        }
        self.cas.compact(max_moves)
    }

    /// Квантиль времени восстановления (мкс) по последним 8192 восстановлениям.
    pub fn restore_quantile_us(&self, q: f64) -> u64 {
        if self.restore_us.is_empty() {
            return 0;
        }
        let mut v = self.restore_us.clone();
        v.sort_unstable();
        v[((v.len() as f64 * q) as usize).min(v.len() - 1)] as u64
    }

    pub fn record_restore_us(&mut self, us: u64) {
        let u = us.min(u32::MAX as u64) as u32;
        if self.restore_us.len() < 8192 {
            self.restore_us.push(u);
        } else {
            self.restore_us[self.restore_n % 8192] = u;
        }
        self.restore_n += 1;
        let k = (64 - us.max(1).leading_zeros() as usize).saturating_sub(1).min(23);
        self.st.restore_us_hist[k] += 1;
    }

    pub fn stats(&self) -> Stats {
        let mut s = self.st.clone();
        s.guest_bytes = 0;
        s.resident_cl = 0;
        s.probing_cl = 0;
        s.evicted_cl = 0;
        for r in &self.ranges {
            s.guest_bytes += r.bytes;
            for c in &r.st {
                match c {
                    Cl::Resident => s.resident_cl += 1,
                    Cl::Probing => s.probing_cl += 1,
                    Cl::Evicted => s.evicted_cl += 1,
                }
            }
        }
        s.ghost_pages = self.ghost_live as u64;
        s
    }
}

#[cfg(test)]
mod tests;
