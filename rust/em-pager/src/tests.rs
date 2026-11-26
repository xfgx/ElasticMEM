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
}

impl Sim {
    fn pg(&self, gpa: u64) -> usize { ((gpa - self.gpa0) / PAGE as u64) as usize }
    fn hp(&self, h: *mut u8) -> usize { (h as usize - self.base) / PAGE }
}

impl Ops for Sim {
    fn map(&self, gpa: u64, _h: *mut u8, len: usize, _w: bool) -> bool {
        let p = self.pg(gpa);
        self.mapped.borrow_mut()[p..p + len / PAGE].fill(true);
        true
    }
    fn unmap(&self, gpa: u64, len: usize) -> bool {
        let p = self.pg(gpa);
        self.mapped.borrow_mut()[p..p + len / PAGE].fill(false);
        true
    }
    fn commit(&self, h: *mut u8, len: usize) -> bool {
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
    let sim = Sim { base, gpa0: 0x1_0000_0000, mapped: RefCell::new(vec![true; pages]), committed: RefCell::new(vec![true; pages]), ro: RefCell::new(vec![false; pages]) };
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
                assert!(core.on_exit(ri, cl, now, &sim).unwrap());
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
                assert!(core.on_host_fault(ri, cl, now, &sim).unwrap());
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
    assert!(core.on_exit(ri, cl, 2000, &sim).is_err(), "повреждение должно вернуть ошибку, а не байты");
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
                assert!(core.on_exit(ri, c, now, &sim).unwrap());
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
    assert!(st.prefetched > 0, "{st:?}");
    eprintln!("coaccess: restore exits without prefetch {without}, with {with}; prefetched={}", st.prefetched);
    assert!(with * 2 < without, "exits with prefetch {with}, without {without}");
}
