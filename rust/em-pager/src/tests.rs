use super::*;
use em_cas::{Config, Lz4};
use std::cell::RefCell;

/// Модель гипервизора и памяти хоста: какие страницы отображены гостю и какие закоммичены у хоста.
/// Декоммит портит байты (0xCC), коммит даёт нули — как VirtualFree/VirtualAlloc.
struct Sim {
    base: usize,
    gpa0: u64,
    mapped: RefCell<Vec<bool>>,
    committed: RefCell<Vec<bool>>,
    ro: RefCell<Vec<bool>>,
    /// Сколько следующих коммитов отказать (модель исчерпанного лимита выделения памяти ОС).
    fail_commits: std::cell::Cell<u32>,
}

impl Sim {
    fn pg(&self, gpa: u64) -> usize { ((gpa - self.gpa0) / PAGE as u64) as usize }
    fn hp(&self, h: *mut u8) -> usize { (h as usize - self.base) / PAGE }
}

impl Ops for Sim {
    fn map(&self, gpa: u64, _h: *mut u8, len: usize, _w: bool) -> bool {
        // как настоящий WHP: повторное отображение уже отображённого диапазона — ошибка
        let p = self.pg(gpa);
        if self.mapped.borrow()[p..p + len / PAGE].iter().any(|&m| m) {
            return false;
        }
        self.mapped.borrow_mut()[p..p + len / PAGE].fill(true);
        true
    }
    fn unmap(&self, gpa: u64, len: usize) -> bool {
        let p = self.pg(gpa);
        self.mapped.borrow_mut()[p..p + len / PAGE].fill(false);
        true
    }
    fn commit(&self, h: *mut u8, len: usize) -> bool {
        if self.fail_commits.get() > 0 {
            self.fail_commits.set(self.fail_commits.get() - 1);
            return false;
        }
        let p = self.hp(h);
        for k in 0..len / PAGE {
            if !self.committed.borrow()[p + k] {
                unsafe { std::ptr::write_bytes(h.add(k * PAGE), 0, PAGE) };
            }
        }
        self.committed.borrow_mut()[p..p + len / PAGE].fill(true);
        self.ro.borrow_mut()[p..p + len / PAGE].fill(false);
        true
    }
    fn decommit(&self, h: *mut u8, len: usize) {
        unsafe { std::ptr::write_bytes(h, 0xCC, len) };
        let p = self.hp(h);
        self.committed.borrow_mut()[p..p + len / PAGE].fill(false);
    }
    fn protect_ro(&self, h: *mut u8, len: usize) {
        let p = self.hp(h);
        self.ro.borrow_mut()[p..p + len / PAGE].fill(true);
    }
    fn protect_rw(&self, h: *mut u8, len: usize) {
        let p = self.hp(h);
        self.ro.borrow_mut()[p..p + len / PAGE].fill(false);
    }
}

struct Rng(u64);
impl Rng {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

#[derive(Clone)]
#[repr(C, align(4096))]
struct Pg([u64; 512]);

/// Память гостя, выровненная по странице (как у VirtualAlloc).
struct Mem(Vec<Pg>);
impl std::ops::Deref for Mem {
    type Target = [u64];
    fn deref(&self) -> &[u64] {
        unsafe { std::slice::from_raw_parts(self.0.as_ptr() as *const u64, self.0.len() * 512) }
    }
}
impl std::ops::DerefMut for Mem {
    fn deref_mut(&mut self) -> &mut [u64] {
        unsafe { std::slice::from_raw_parts_mut(self.0.as_mut_ptr() as *mut u64, self.0.len() * 512) }
    }
}

fn setup(mib: usize, ghost: bool) -> (Mem, Sim, Core) {
    setup_pf(mib, ghost, 0)
}

fn setup_pf(mib: usize, ghost: bool, prefetch: usize) -> (Mem, Sim, Core) {
    let pages = mib * 256;
    let mut mem = Mem(vec![Pg([0; 512]); pages]);
    let mut r = Rng(42);
    for p in 0..pages {
        let w = &mut mem[p * 512..(p + 1) * 512];
        match p % 10 {
            0..=2 => {} // свободная память гостя (нули)
            3 if p > 32 => {
                let src = (r.next() as usize) % p;
                let (a, b) = mem.split_at_mut(p * 512);
                b[..512].copy_from_slice(&a[src * 512..src * 512 + 512]);
            }
            _ => {
                for (i, x) in w.iter_mut().enumerate() {
                    *x = if i % 4 == 0 { 0x7ff6_1000_0000 + (r.next() & 0xffff) * 16 } else if i % 4 == 1 { r.next() & 0x3ff } else { 0 };
                }
            }
        }
    }
    let base = mem.as_mut_ptr() as usize;
    let sim = Sim { base, gpa0: 0x1_0000_0000, mapped: RefCell::new(vec![true; pages]), committed: RefCell::new(vec![true; pages]), ro: RefCell::new(vec![false; pages]), fail_commits: std::cell::Cell::new(0) };
    let cfg = Cfg { cluster_pages: 16, idle_ms: 100, min_range_bytes: 1 << 20, keep_ghost: ghost, prefetch_depth: prefetch, coaccess_ms: 50 };
    let mut core = Core::new(cfg, CasStore::new(Box::new(Lz4), Config::default()));
    assert!(core.add_range(sim.gpa0, base as *mut u8, (pages * PAGE) as u64, true));
    (mem, sim, core)
}

/// Гость и VMM пишут в память, пейджер параллельно пробует и вытесняет; в конце память == тень.
fn run(ghost: bool) -> Stats {
    let (mem, sim, mut core) = setup(16, ghost);
    let pages = mem.len() / 512;
    let mut shadow: Vec<u64> = mem.to_vec();
    let base = sim.base;
    let mut r = Rng(7);
    let hot: Vec<usize> = (0..pages / 16).filter(|_| r.next() % 8 == 0).collect(); // горячие кластеры
    let mut now = 0u64;
    let (mut probe, mut idle) = (Vec::new(), Vec::new());
    for step in 0..4000u64 {
        now += 10;
        // гость: горячие кластеры часто, случайные редко
        for _ in 0..8 {
            let p = if r.next() % 16 != 0 { hot[r.next() as usize % hot.len()] * 16 + (r.next() as usize % 16) } else { r.next() as usize % pages };
            let gpa = sim.gpa0 + (p * PAGE) as u64;
            if !sim.mapped.borrow()[p] {
                let (ri, cl) = core.find_gpa(gpa).unwrap();
                assert!(core.on_exit(ri, cl, now, 0, &sim).unwrap());
            }
            assert!(sim.mapped.borrow()[p] && sim.committed.borrow()[p], "guest saw unmapped/decommitted page");
            let w = p * 512 + 7;
            let v = unsafe { *((base + w * 8) as *const u64) };
            assert_eq!(v, shadow[w], "guest read wrong bytes at page {p}");
            unsafe { *((base + w * 8) as *mut u64) = v + 1 };
            shadow[w] = v + 1;
        }
        // VMM (эмуляция устройства) напрямую в память хоста
        if step % 3 == 0 {
            let p = r.next() as usize % pages;
            if !sim.committed.borrow()[p] || sim.ro.borrow()[p] {
                let (ri, cl) = core.find_hva(base + p * PAGE).unwrap();
                assert!(core.on_host_fault(ri, cl, now, 1, &sim).unwrap());
            }
            let w = p * 512 + 300;
            let v = unsafe { *((base + w * 8) as *const u64) };
            assert_eq!(v, shadow[w], "host read wrong bytes at page {p}");
            unsafe { *((base + w * 8) as *mut u64) = v ^ 0xABCD };
            shadow[w] = v ^ 0xABCD;
        }
        if step % 5 == 0 {
            probe.clear();
            idle.clear();
            core.candidates(now, 64, 4096, &mut probe, &mut idle);
            for &(ri, cl) in &idle {
                core.evict(ri, cl, now, &sim);
            }
            for &(ri, cl) in &probe {
                core.begin_probe(ri, cl, now, &sim);
            }
            core.maintain(256, 1 << 20);
        }
    }
    let st = core.stats();
    assert!(st.evicted_cl > 0 && st.exit_restores > 0 && st.probe_hits > 0, "{st:?}");
    core.del_range(sim.gpa0, (pages * PAGE) as u64, &sim).unwrap();
    assert!(sim.mapped.borrow().iter().all(|&m| m) && sim.committed.borrow().iter().all(|&c| c));
    let bad = (0..pages).filter(|&p| mem[p * 512..(p + 1) * 512] != shadow[p * 512..(p + 1) * 512]).count();
    assert_eq!(bad, 0, "pages differ after del_range");
    assert_eq!(core.cas.stats().live_entries, 0, "все записи хранилища освобождены");
    st
}

#[test]
fn pager_is_lossless_under_guest_and_host_access() {
    let st = run(false);
    assert!(st.host_restores > 0 || st.exit_restores > 0);
}

#[test]
fn pager_with_temporal_delta_is_lossless() {
    run(true);
}

#[test]
fn aliased_ranges_are_not_managed() {
    let (mut mem, sim, mut core) = setup(2, false);
    let base = mem.as_mut_ptr() as *mut u8;
    assert!(!core.add_range(0x2_0000_0000, base, (2 << 20) as u64, true));
    assert!(core.find_gpa(sim.gpa0).is_none(), "первый диапазон тоже снят с управления");
    assert!(!core.add_range(0x3_0000_0000, base, 4096, false));
}

#[test]
fn corrupted_store_fails_closed() {
    let (_mem, sim, mut core) = setup(2, false);
    let (mut probe, mut idle) = (Vec::new(), Vec::new());
    core.candidates(0, 1000, 1000, &mut probe, &mut idle);
    for &(ri, cl) in &probe {
        core.begin_probe(ri, cl, 0, &sim);
    }
    probe.clear();
    core.candidates(1000, 0, 1000, &mut probe, &mut idle);
    let mut victim = None;
    for &(ri, cl) in &idle {
        if core.evict(ri, cl, 1000, &sim) && victim.is_none() {
            let r = core.ranges[ri].refs[cl * 16..cl * 16 + 16].iter().copied().find(|r| *r != Ref::Zero);
            if let Some(r) = r {
                core.cas.corrupt_for_test(r);
                victim = Some((ri, cl));
            }
        }
    }
    let (ri, cl) = victim.expect("some cluster evicted");
    assert!(core.on_exit(ri, cl, 2000, 0, &sim).is_err(), "повреждение должно вернуть ошибку, а не байты");
}

/// Co-access: гость обходит группы разбросанных кластеров в одном и том же порядке (как связный список).
/// С prefetch exit'ов на восстановление заметно меньше, данные без потерь.
fn groups_run(prefetch: usize) -> (u64, Stats) {
    let (mem, sim, mut core) = setup_pf(64, false, prefetch);
    let pages = mem.len() / 512;
    let ncl = pages / 16;
    let base = sim.base;
    let mut shadow: Vec<u64> = mem.to_vec();
    let mut r = Rng(99);
    let groups: Vec<Vec<usize>> = (0..16).map(|_| (0..8).map(|_| r.next() as usize % ncl).collect()).collect();
    let (mut probe, mut idle) = (Vec::new(), Vec::new());
    let mut now = 0u64;
    let mut restores_by_exit = 0u64;
    for round in 0..64u64 {
        let g = &groups[(round % 16) as usize];
        for &cl in g {
            now += 1; // обход группы укладывается в окно co-access
            let p = cl * 16 + 3;
            let gpa = sim.gpa0 + (p * PAGE) as u64;
            if !sim.mapped.borrow()[p] {
                let (ri, c) = core.find_gpa(gpa).unwrap();
                if core.ranges[ri].st[c] == Cl::Evicted {
                    restores_by_exit += 1;
                }
                assert!(core.on_exit(ri, c, now, 0, &sim).unwrap());
            }
            let w = p * 512 + 9;
            let v = unsafe { *((base + w * 8) as *const u64) };
            assert_eq!(v, shadow[w]);
            unsafe { *((base + w * 8) as *mut u64) = v + 1 };
            shadow[w] = v + 1;
        }
        // между раундами пейджер успевает всё пробовать и вытеснить (без пауз для горячих кластеров)
        for _ in 0..3 {
            now += 3_000_000;
            probe.clear();
            idle.clear();
            core.candidates(now, 1 << 20, 1 << 20, &mut probe, &mut idle);
            for &(ri, cl) in &idle {
                core.evict(ri, cl, now, &sim);
            }
            for &(ri, cl) in &probe {
                core.begin_probe(ri, cl, now, &sim);
            }
        }
    }
    core.del_range(sim.gpa0, (pages * PAGE) as u64, &sim).unwrap();
    let bad = (0..pages).filter(|&p| mem[p * 512..(p + 1) * 512] != shadow[p * 512..(p + 1) * 512]).count();
    assert_eq!(bad, 0);
    (restores_by_exit, core.stats())
}

#[test]
fn coaccess_prefetch_cuts_restore_exits() {
    let (without, _) = groups_run(0);
    let (with, st) = groups_run(8);
    eprintln!("coaccess: restore exits without prefetch {without}, with {with}; prefetched={} checks={} hits={} active={}", st.prefetched, st.pf_checks, st.pf_check_hits, st.pf_active);
    assert!(st.prefetched > 0 && st.pf_active, "{st:?}");
    assert!(with * 10 < without * 8, "exits with prefetch {with}, without {without}");
}

/// Два vCPU получают exit по одному и тому же кластеру: первый его возвращает, второй приходит, когда кластер уже
/// Resident и отображён. Второй exit тоже должен считаться обработанным (повторить инструкцию), а не уйти в QEMU
/// как MMIO — на настоящем госте это давало «Unimplemented handler for PUSH» и синий экран.
#[test]
fn second_vcpu_exit_on_already_restored_cluster_is_handled() {
    let (_mem, sim, mut core) = setup(2, false);
    let (mut probe, mut idle) = (Vec::new(), Vec::new());
    core.candidates(0, 1000, 1000, &mut probe, &mut idle);
    for &(ri, cl) in &probe {
        core.begin_probe(ri, cl, 0, &sim);
    }
    let (ri, cl) = probe[0];
    // probing: оба vCPU получили exit, обрабатываются по очереди
    assert!(core.on_exit(ri, cl, 10, 0, &sim).unwrap());
    assert!(core.on_exit(ri, cl, 10, 0, &sim).unwrap(), "второй exit по уже возвращённому кластеру");
    // evicted: то же самое
    probe.clear();
    idle.clear();
    core.candidates(5000, 1000, 1000, &mut probe, &mut idle);
    for &(ri, cl) in &probe {
        core.begin_probe(ri, cl, 5000, &sim);
    }
    probe.clear();
    core.candidates(9000, 0, 1000, &mut probe, &mut idle);
    let mut ev = None;
    for &(ri, cl) in &idle {
        if core.evict(ri, cl, 9000, &sim) && ev.is_none() {
            ev = Some((ri, cl));
        }
    }
    let (ri, cl) = ev.expect("evicted cluster");
    assert!(core.on_exit(ri, cl, 9100, 0, &sim).unwrap());
    assert!(core.on_exit(ri, cl, 9100, 0, &sim).unwrap(), "второй exit по уже восстановленному кластеру");
    assert!(core.on_host_fault(ri, cl, 9100, 1, &sim).unwrap());
}

/// Буфер ввода-вывода VMM: вытесненные кластеры возвращаются до вызова, закреплённые не пробуются и не вытесняются.
#[test]
fn io_buffer_is_restored_and_pinned() {
    let (_mem, sim, mut core) = setup(2, false);
    let (mut probe, mut idle) = (Vec::new(), Vec::new());
    core.candidates(0, 1000, 1000, &mut probe, &mut idle);
    for &(ri, cl) in &probe {
        core.begin_probe(ri, cl, 0, &sim);
    }
    probe.clear();
    core.candidates(5000, 0, 1000, &mut probe, &mut idle);
    for &(ri, cl) in &idle {
        core.evict(ri, cl, 5000, &sim);
    }
    let evicted: Vec<usize> = (0..core.ranges[0].st.len()).filter(|&c| core.ranges[0].st[c] == Cl::Evicted).collect();
    assert!(evicted.len() > 4);
    let base = core.ranges[0].hva;
    let (c0, c1) = (evicted[0], evicted[1]);
    // буфер на стыке двух кластеров
    let a = base + c0 * 16 * PAGE + 100;
    let len = (c1 - c0) * 16 * PAGE + 50;
    let n = core.prepare_io(a, len, 6000, &sim).unwrap();
    assert!(n >= 2);
    for c in c0..=c1 {
        assert_eq!(core.ranges[0].st[c], Cl::Resident, "cluster {c} must be resident");
        assert!(sim.committed.borrow()[c * 16] && sim.mapped.borrow()[c * 16]);
    }
    // закреплённый кластер не пробуется
    assert!(!core.begin_probe(0, c0, 6000 + 3_000_000.min(IO_PIN_MS - 1), &sim));
    assert!(core.begin_probe(0, c0, 6000 + IO_PIN_MS + 1, &sim) || core.ranges[0].st[c0] != Cl::Resident);
}

/// Probing-кластер, у которого WHvMapGpaRange не проходит (часть страниц осталась отображённой): exit всё равно
/// обрабатывается (снять отображение и отобразить заново), а не уходит в QEMU как MMIO — на настоящем госте
/// Windows 11 QEMU падал на «failed to decode instruction» сразу после начала проб.
#[test]
fn exit_on_probing_cluster_with_failing_map_is_handled() {
    let (_mem, sim, mut core) = setup(2, false);
    let (mut probe, mut idle) = (Vec::new(), Vec::new());
    core.candidates(0, 1000, 1000, &mut probe, &mut idle);
    let (ri, cl) = probe[0];
    assert!(core.begin_probe(ri, cl, 0, &sim));
    let p = sim.pg(core.find_gpa_addr(ri, cl));
    sim.mapped.borrow_mut()[p + 3] = true; // одна страница кластера «осталась» отображённой
    assert!(core.on_exit(ri, cl, 10, 0, &sim).unwrap(), "exit должен быть обработан");
    assert_eq!(core.ranges[ri].st[cl], Cl::Resident);
    assert!(sim.mapped.borrow()[p..p + core.cfg.cluster_pages].iter().all(|&m| m), "кластер отображён целиком");
}

/// ОС временно не дала память под восстановление (лимит выделения исчерпан другим процессом): ошибка NoMemory,
/// кластер остаётся Evicted с байтами в хранилище; повтор, когда память есть, восстанавливает его без потерь.
/// На настоящем ПК это случилось во время em-pagestat и останавливало QEMU (FATAL).
#[test]
fn restore_without_os_memory_is_retryable_and_lossless() {
    let (mem, sim, mut core) = setup(2, false);
    let snapshot: Vec<u64> = mem.to_vec();
    let (mut probe, mut idle) = (Vec::new(), Vec::new());
    core.candidates(0, 1000, 1000, &mut probe, &mut idle);
    for &(ri, cl) in &probe {
        core.begin_probe(ri, cl, 0, &sim);
    }
    probe.clear();
    core.candidates(5000, 0, 1000, &mut probe, &mut idle);
    let ev: Vec<(usize, usize)> = idle.iter().copied().filter(|&(ri, cl)| core.evict(ri, cl, 5000, &sim)).collect();
    let (ri, cl) = *ev.iter().find(|&&(ri, cl)| {
        let n = core.cfg.cluster_pages;
        snapshot[cl * n * 512..(cl + 1) * n * 512].iter().any(|&x| x != 0) && ri == 0
    }).expect("evicted non-zero cluster");
    sim.fail_commits.set(1);
    assert!(matches!(core.on_exit(ri, cl, 5100, 0, &sim), Err(em_cas::CasError::NoMemory)));
    assert_eq!(core.ranges[ri].st[cl], Cl::Evicted, "кластер остался в хранилище");
    assert!(core.on_exit(ri, cl, 5200, 0, &sim).unwrap(), "повтор, когда память есть");
    let n = core.cfg.cluster_pages;
    assert_eq!(&mem[cl * n * 512..(cl + 1) * n * 512], &snapshot[cl * n * 512..(cl + 1) * n * 512], "байты без потерь");
}
