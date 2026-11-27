//! Хранилище payload'ов: сегменты 64 КиБ прямо от ОС (VirtualAlloc / mmap), каждый сегмент — один размерный
//! класс (шаг 64 байта, 64..=4096). Пустой сегмент сразу возвращается ОС. Компактор переносит payload'ы из
//! малозаполненных сегментов в свободные слоты других сегментов того же класса и освобождает их —
//! это «перемещаемая холодная арена» для самого хранилища: владельцы ссылаются на payload по id, а не по адресу.

pub const SEG: usize = 64 * 1024;
pub const STEP: usize = 64;
pub const CLASSES: usize = 4096 / STEP;

#[cfg(windows)]
mod os {
    extern "system" {
        fn VirtualAlloc(a: *mut u8, s: usize, t: u32, p: u32) -> *mut u8;
        fn VirtualFree(a: *mut u8, s: usize, t: u32) -> i32;
    }
    pub fn alloc(n: usize) -> *mut u8 {
        unsafe { VirtualAlloc(std::ptr::null_mut(), n, 0x3000, 0x04) }
    }
    pub fn free(p: *mut u8, _n: usize) {
        unsafe { VirtualFree(p, 0, 0x8000) };
    }
}

#[cfg(unix)]
mod os {
    extern "C" {
        fn mmap(a: *mut u8, n: usize, prot: i32, flags: i32, fd: i32, off: i64) -> *mut u8;
        fn munmap(a: *mut u8, n: usize) -> i32;
    }
    pub fn alloc(n: usize) -> *mut u8 {
        #[cfg(target_os = "linux")]
        const ANON: i32 = 0x20;
        #[cfg(not(target_os = "linux"))]
        const ANON: i32 = 0x1000;
        let p = unsafe { mmap(std::ptr::null_mut(), n, 3, 0x02 | ANON, -1, 0) };
        if p as isize == -1 { std::ptr::null_mut() } else { p }
    }
    pub fn free(p: *mut u8, n: usize) {
        unsafe { munmap(p, n) };
    }
}

pub fn class_of(len: usize) -> usize {
    debug_assert!(len >= 1 && len <= 4096);
    (len + STEP - 1) / STEP - 1
}
pub fn class_size(c: usize) -> usize {
    (c + 1) * STEP
}

struct Seg {
    base: *mut u8,
    class: usize,
    used: u32,
    free: Vec<u16>,
    owner: Vec<u32>, // слот -> id владельца (u32::MAX — свободен)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(C, packed(2))] // 6 байт: запись хранилища укладывается в 48
pub struct Loc {
    pub seg: u32,
    pub slot: u16,
}

pub struct Heap {
    segs: Vec<Option<Seg>>,
    free_ids: Vec<u32>,
    open: Vec<Vec<u32>>, // по классу: сегменты со свободными слотами
    pub committed: u64,
    pub released: u64,
    pub moves: u64,
}
unsafe impl Send for Heap {}

impl Default for Heap {
    fn default() -> Self {
        Heap { segs: Vec::new(), free_ids: Vec::new(), open: vec![Vec::new(); CLASSES], committed: 0, released: 0, moves: 0 }
    }
}

impl Heap {
    pub fn alloc(&mut self, len: usize, owner: u32) -> Option<Loc> {
        let c = class_of(len);
        let sid = match self.open[c].last() {
            Some(&s) => s,
            None => {
                let base = os::alloc(SEG);
                if base.is_null() {
                    return None;
                }
                self.committed += SEG as u64;
                let n = SEG / class_size(c);
                let seg = Seg { base, class: c, used: 0, free: (0..n as u16).rev().collect(), owner: vec![u32::MAX; n] };
                let id = match self.free_ids.pop() {
                    Some(id) => {
                        self.segs[id as usize] = Some(seg);
                        id
                    }
                    None => {
                        self.segs.push(Some(seg));
                        (self.segs.len() - 1) as u32
                    }
                };
                self.open[c].push(id);
                id
            }
        };
        let seg = self.segs[sid as usize].as_mut().unwrap();
        let slot = seg.free.pop().unwrap();
        seg.used += 1;
        seg.owner[slot as usize] = owner;
        if seg.free.is_empty() {
            let o = &mut self.open[c];
            let i = o.iter().position(|&x| x == sid).unwrap();
            o.swap_remove(i);
        }
        Some(Loc { seg: sid, slot })
    }

    pub fn bytes(&self, l: Loc, len: usize) -> &[u8] {
        let s = self.segs[l.seg as usize].as_ref().unwrap();
        unsafe { std::slice::from_raw_parts(s.base.add(l.slot as usize * class_size(s.class)), len) }
    }
    pub fn bytes_mut(&mut self, l: Loc, len: usize) -> &mut [u8] {
        let s = self.segs[l.seg as usize].as_ref().unwrap();
        unsafe { std::slice::from_raw_parts_mut(s.base.add(l.slot as usize * class_size(s.class)), len) }
    }

    pub fn free(&mut self, l: Loc) {
        let s = self.segs[l.seg as usize].as_mut().unwrap();
        let c = s.class;
        let was_full = s.free.is_empty();
        s.owner[l.slot as usize] = u32::MAX;
        s.free.push(l.slot);
        s.used -= 1;
        if s.used == 0 {
            os::free(s.base, SEG);
            self.committed -= SEG as u64;
            self.released += 1;
            self.segs[l.seg as usize] = None;
            self.free_ids.push(l.seg);
            if !was_full {
                let o = &mut self.open[c];
                if let Some(i) = o.iter().position(|&x| x == l.seg) {
                    o.swap_remove(i);
                }
            }
        } else if was_full {
            self.open[c].push(l.seg);
        }
    }

    /// Уплотнение: для класса, где свободных слотов хватает на целый сегмент, переносим payload'ы из самого
    /// пустого сегмента в другие. `relocate(owner, new_loc)` обновляет адрес у владельца. До `max_moves` переносов.
    pub fn compact(&mut self, max_moves: usize, mut relocate: impl FnMut(u32, Loc)) -> usize {
        let mut moved = 0;
        for c in 0..CLASSES {
            loop {
                if moved >= max_moves {
                    return moved;
                }
                let per = SEG / class_size(c);
                let open: Vec<u32> = self.open[c].clone();
                if open.len() < 2 {
                    break;
                }
                let free_slots: usize = open.iter().map(|&s| self.segs[s as usize].as_ref().unwrap().free.len()).sum();
                if free_slots < per {
                    break;
                }
                let src = *open.iter().min_by_key(|&&s| self.segs[s as usize].as_ref().unwrap().used).unwrap();
                let owners: Vec<(u16, u32)> = {
                    let s = self.segs[src as usize].as_ref().unwrap();
                    s.owner.iter().enumerate().filter(|(_, &o)| o != u32::MAX).map(|(i, &o)| (i as u16, o)).collect()
                };
                // убрать источник из open, чтобы alloc не выбрал его
                let o = &mut self.open[c];
                let i = o.iter().position(|&x| x == src).unwrap();
                o.swap_remove(i);
                let csz = class_size(c);
                let mut done = true;
                for (slot, owner) in owners {
                    if moved >= max_moves || self.open[c].is_empty() {
                        done = false;
                        break;
                    }
                    let from = Loc { seg: src, slot };
                    let to = self.alloc(csz, owner).unwrap();
                    let mut tmp = [0u8; 4096];
                    tmp[..csz].copy_from_slice(self.bytes(from, csz));
                    self.bytes_mut(to, csz).copy_from_slice(&tmp[..csz]);
                    relocate(owner, to);
                    // free источника: сегмент не в open, free() вернёт его ОС, когда опустеет
                    let s = self.segs[src as usize].as_mut().unwrap();
                    s.owner[slot as usize] = u32::MAX;
                    s.free.push(slot);
                    s.used -= 1;
                    moved += 1;
                    self.moves += 1;
                    if s.used == 0 {
                        os::free(s.base, SEG);
                        self.committed -= SEG as u64;
                        self.released += 1;
                        self.segs[src as usize] = None;
                        self.free_ids.push(src);
                        break;
                    }
                }
                if let Some(s) = self.segs[src as usize].as_ref() {
                    if !s.free.is_empty() {
                        self.open[c].push(src);
                    }
                }
                if !done {
                    break;
                }
            }
        }
        moved
    }

    pub fn live_segments(&self) -> usize {
        self.segs.iter().filter(|s| s.is_some()).count()
    }
}

impl Drop for Heap {
    fn drop(&mut self) {
        for s in self.segs.iter().flatten() {
            os::free(s.base, SEG);
        }
    }
}
