//! em-pagestat — Gate 0 для смены единицы хранения. По настоящей памяти считает, сколько дали бы:
//!  * нулевые страницы; точные дубликаты внутри процесса и между процессами;
//!  * обычное постраничное сжатие (LZ4, XPRESS — кодек сжатия памяти Windows);
//!  * em-cas в четырёх вариантах в одном и том же контейнере (сегменты + записи), чтобы сравнение было честным:
//!    plain (только сжатие), dedup, cas (dedup + XOR-delta, 3 super-feature), cas1 (1 super-feature);
//!    каждый вариант проверяется на восстановление без потерь;
//!  * временна́я дельта: XOR страницы с её же версией из прошлого снимка (--snapshots 2+).
//! Источники: --pid / --name (живые процессы, только резидентные страницы), --dump (полный минидамп),
//! --raw (сырой образ, например память гостя), --synthetic МиБ (самопроверка).
//! Память читается локально, наружу выводятся только агрегаты.

mod src;

use em_cas::{CasStore, Codec, Config, Ref};
use src::{Opts, PAGE};
use std::collections::HashMap;

struct Args {
    pids: Vec<u32>,
    names: Vec<String>,
    dump: Option<String>,
    raw: Option<String>,
    synthetic: Option<u64>,
    private_only: bool,
    snapshots: u32,
    interval_ms: u64,
    max_mib: u64,
    codec: String,
}

fn parse() -> Args {
    let mut a = Args { pids: vec![], names: vec![], dump: None, raw: None, synthetic: None, private_only: false, snapshots: 1, interval_ms: 30000, max_mib: 4096, codec: if cfg!(windows) { "xpress".into() } else { "lz4".into() } };
    let v: Vec<String> = std::env::args().skip(1).collect();
    let mut i = 0;
    let need = |i: usize| -> String { v.get(i + 1).cloned().unwrap_or_else(|| { eprintln!("нет значения для {}", v[i]); std::process::exit(2) }) };
    while i < v.len() {
        match v[i].as_str() {
            "--pid" => { a.pids.push(need(i).parse().unwrap()); i += 1; }
            "--name" => { a.names.push(need(i)); i += 1; }
            "--dump" => { a.dump = Some(need(i)); i += 1; }
            "--raw" => { a.raw = Some(need(i)); i += 1; }
            "--synthetic" => { a.synthetic = Some(need(i).parse().unwrap()); i += 1; }
            "--private-only" => a.private_only = true,
            "--snapshots" => { a.snapshots = need(i).parse().unwrap(); i += 1; }
            "--interval-ms" => { a.interval_ms = need(i).parse().unwrap(); i += 1; }
            "--max-mib" => { a.max_mib = need(i).parse().unwrap(); i += 1; }
            "--codec" => { a.codec = need(i); i += 1; }
            "-h" | "--help" => {
                println!("em-pagestat [--pid N]... [--name EXE]... [--dump F.dmp] [--raw F] [--synthetic MIB] [--private-only] [--snapshots K] [--interval-ms MS] [--max-mib M] [--codec xpress|lz4]");
                std::process::exit(0);
            }
            x => { eprintln!("неизвестный аргумент {x}"); std::process::exit(2); }
        }
        i += 1;
    }
    a
}

#[derive(Default)]
struct Snap {
    pages: u64,
    zero: u64,
    dup: u64,
    dup_cross: u64,
    lz4: u64,
    xpress: u64,
    sources: std::collections::BTreeSet<u32>,
    t_matched: u64,
    t_unchanged: u64,
    t_changed: u64,
    t_plain: u64,
    t_xor: u64,
}

fn clen(c: &dyn Codec, p: &[u8]) -> u64 {
    let mut out = [0u8; PAGE];
    c.compress(p, &mut out).map_or(PAGE as u64, |n| n as u64)
}

fn main() {
    let a = parse();
    let o = Opts { private_only: a.private_only, max_bytes: a.max_mib << 20 };
    let mut pids = a.pids.clone();
    for n in &a.names {
        #[cfg(any(windows, target_os = "linux"))]
        pids.extend(src::live::pids_by_name(n));
    }
    pids.sort();
    pids.dedup();
    if pids.is_empty() && a.dump.is_none() && a.raw.is_none() && a.synthetic.is_none() {
        eprintln!("не задан источник: --pid / --name / --dump / --raw / --synthetic");
        std::process::exit(2);
    }
    println!("PAGESTAT_RUN sources_pids={} names={:?} dump={} raw={} synthetic={:?} private_only={} snapshots={} interval_ms={} max_mib={} cas_codec={}",
        pids.len(), a.names, a.dump.is_some(), a.raw.is_some(), a.synthetic, a.private_only, a.snapshots, a.interval_ms, a.max_mib, a.codec);
    let lz4 = em_cas::Lz4;
    #[cfg(windows)]
    let xp: Option<Box<dyn Codec>> = Some(Box::new(em_cas::Xpress));
    #[cfg(not(windows))]
    let xp: Option<Box<dyn Codec>> = None;
    let keep_prev = a.snapshots > 1;
    let mut prev: HashMap<(u32, u64), (u128, Vec<u8>)> = HashMap::new();
    for snap in 1..=a.snapshots {
        let mut s = Snap::default();
        let mut seen: HashMap<u128, (u32, bool)> = HashMap::new(); // ключ -> (первый источник, был ли в другом)
        let variants: [(&str, Config); 4] = [
            ("plain", Config { dedup: false, delta: false, ..Config::default() }),
            ("dedup", Config { dedup: true, delta: false, ..Config::default() }),
            ("cas", Config::default()),
            ("cas1", Config { sketch_features: 1, ..Config::default() }),
        ];
        let mut stores: Vec<CasStore> = variants.iter().map(|(_, c)| CasStore::new(em_cas::by_name(&a.codec), *c)).collect();
        let mut refs: Vec<Vec<Ref>> = vec![Vec::new(); stores.len()];
        let mut keys: Vec<u128> = Vec::new();
        let mut cur: HashMap<(u32, u64), (u128, Vec<u8>)> = HashMap::new();
        let mut cas_err = 0u64;
        {
            let mut sink = |sid: u32, addr: u64, p: &[u8]| {
                s.pages += 1;
                s.sources.insert(sid);
                let zero = p.iter().all(|&b| b == 0);
                let k = if zero { 0 } else { em_cas::key(p) };
                if zero {
                    s.zero += 1;
                } else {
                    match seen.get_mut(&k) {
                        Some((first, _)) => {
                            s.dup += 1;
                            if *first != sid {
                                s.dup_cross += 1;
                            }
                        }
                        None => {
                            seen.insert(k, (sid, false));
                        }
                    }
                    s.lz4 += clen(&lz4, p);
                    if let Some(x) = &xp {
                        s.xpress += clen(x.as_ref(), p);
                    }
                }
                keys.push(k);
                for (i, st) in stores.iter_mut().enumerate() {
                    match st.put(p, None) {
                        Ok((r, _)) => refs[i].push(r),
                        Err(_) => {
                            cas_err += 1;
                            refs[i].push(Ref::Zero);
                        }
                    }
                }
                if snap > 1 {
                    if let Some((pk, pc)) = prev.get(&(sid, addr)) {
                        s.t_matched += 1;
                        if *pk == k {
                            s.t_unchanged += 1;
                        } else {
                            s.t_changed += 1;
                            let mut old = [0u8; PAGE];
                            let base_ok = if pc.is_empty() { true } else { lz4.decompress(pc, &mut old) };
                            if base_ok {
                                let mut x = [0u8; PAGE];
                                for i in 0..PAGE {
                                    x[i] = p[i] ^ old[i];
                                }
                                s.t_plain += if zero { 0 } else { clen(&lz4, p) };
                                s.t_xor += clen(&lz4, &x);
                            }
                        }
                    }
                }
                if keep_prev && snap < a.snapshots {
                    let mut c = vec![0u8; PAGE];
                    let n = if zero { 0 } else { lz4.compress(p, &mut c).unwrap_or(0) };
                    if n == 0 && !zero {
                        c = p.to_vec(); // несжимаемая: храним как есть (длина PAGE отличает её от сжатой)
                    } else {
                        c.truncate(n);
                    }
                    cur.insert((sid, addr), (k, c));
                }
            };
            let mut budget = o.max_bytes;
            if let Some(m) = a.synthetic {
                src::synthetic(m.min(a.max_mib), 0x9E37_79B9, &mut sink);
            }
            if let Some(d) = &a.dump {
                if let Err(e) = src::minidump(d, &o, &mut sink) {
                    eprintln!("dump: {e}");
                }
            }
            if let Some(r) = &a.raw {
                if let Err(e) = src::raw(r, &o, &mut sink) {
                    eprintln!("raw: {e}");
                }
            }
            #[cfg(any(windows, target_os = "linux"))]
            for &pid in &pids {
                if let Err(e) = src::live::process(pid, &o, &mut budget, &mut sink) {
                    eprintln!("pid {pid}: {e}");
                }
            }
            let _ = &mut budget;
        }
        // Проверка без потерь: каждая страница восстанавливается из каждого варианта и совпадает по SHA-256.
        let mut bad = 0u64;
        let mut b = vec![0u8; PAGE];
        for (i, st) in stores.iter_mut().enumerate() {
            for (r, k) in refs[i].iter().zip(&keys) {
                let ok = st.get(*r, &mut b).is_ok() && (if *k == 0 { b.iter().all(|&x| x == 0) } else { em_cas::key(&b) == *k });
                if !ok {
                    bad += 1;
                }
            }
        }
        let all: Vec<em_cas::CasStats> = stores.iter().map(|s| s.stats()).collect();
        let cs = all[2];
        let raw_b = s.pages * PAGE as u64;
        let mib = |x: u64| x as f64 / 1048576.0;
        let pct = |x: u64, of: u64| if of == 0 { 0.0 } else { 100.0 * x as f64 / of as f64 };
        let ratio = |x: u64| if raw_b == 0 { 0.0 } else { x as f64 / raw_b as f64 };
        println!(
            "PAGESTAT snap={snap} sources={} pages={} resident_mib={:.1} zero_pct={:.1} dup_pct={:.1} dup_cross_pct={:.1} lz4_mib={:.1} lz4_ratio={:.3} xpress_mib={} xpress_ratio={} cas_mib={:.1} cas_ratio={:.3} cas_payload_mib={:.1} cas_meta_mib={:.1} cas_segments_mib={:.1} cas_dedup={} cas_delta={} cas_full={} cas_incompressible={} cas_errors={} cas_lossless={}",
            s.sources.len(), s.pages, mib(raw_b), pct(s.zero, s.pages), pct(s.dup, s.pages), pct(s.dup_cross, s.pages),
            mib(s.lz4), ratio(s.lz4),
            if xp.is_some() { format!("{:.1}", mib(s.xpress)) } else { "n/a".into() },
            if xp.is_some() { format!("{:.3}", ratio(s.xpress)) } else { "n/a".into() },
            mib(cs.total_bytes()), ratio(cs.total_bytes()), mib(cs.payload_bytes), mib(cs.meta_bytes), mib(cs.segment_bytes),
            cs.put_dedup, cs.put_delta, cs.put_full, cs.put_incompressible, cas_err,
            if bad == 0 && cas_err == 0 { "PASS".to_string() } else { format!("FAIL({bad})") }
        );
        for ((name, _), st) in variants.iter().zip(&all) {
            println!(
                "STORE snap={snap} variant={name} total_mib={:.1} payload_mib={:.1} meta_mib={:.1} segments_mib={:.1} ratio={:.3} meta_bytes_per_page={:.1} dedup={} delta={} full={} incompressible={}",
                mib(st.total_bytes()), mib(st.payload_bytes), mib(st.meta_bytes), mib(st.segment_bytes), ratio(st.total_bytes()),
                if s.pages == 0 { 0.0 } else { st.meta_bytes as f64 / s.pages as f64 },
                st.put_dedup, st.put_delta, st.put_full, st.put_incompressible
            );
        }
        let gain = |x: u64, base: u64| if base == 0 { 0.0 } else { 100.0 * (base as f64 - x as f64) / base as f64 };
        let plain = all[0].total_bytes();
        println!(
            "GATE0_FAIR snap={snap} codec={} plain_mib={:.1} dedup_mib={:.1} cas_mib={:.1} cas1_mib={:.1} dedup_gain_pct={:.1} cas_gain_pct={:.1} cas1_gain_pct={:.1} delta_extra_gain_pct={:.1}",
            a.codec, mib(plain), mib(all[1].total_bytes()), mib(all[2].total_bytes()), mib(all[3].total_bytes()),
            gain(all[1].total_bytes(), plain), gain(all[2].total_bytes(), plain), gain(all[3].total_bytes(), plain),
            gain(all[2].total_bytes(), all[1].total_bytes())
        );
        let (best_name, best) = if xp.is_some() && s.xpress < s.lz4 { ("xpress", s.xpress) } else { ("lz4", s.lz4) };
        // Честное сравнение: обычному сжатию нулевые страницы тоже ничего не стоят (их и так не храним).
        println!(
            "GATE0 snap={snap} best_plain={best_name} best_plain_mib={:.1} cas_mib={:.1} cas_gain_vs_best_plain_pct={:.1} cas_payload_gain_vs_best_plain_pct={:.1}",
            mib(best), mib(cs.total_bytes()),
            if best == 0 { 0.0 } else { 100.0 * (best as f64 - cs.total_bytes() as f64) / best as f64 },
            if best == 0 { 0.0 } else { 100.0 * (best as f64 - cs.payload_bytes as f64) / best as f64 }
        );
        if snap > 1 {
            println!(
                "TEMPORAL snap={snap} matched_pages={} unchanged_pct={:.1} changed_pages={} changed_plain_lz4_mib={:.2} changed_xor_prev_lz4_mib={:.2} xor_gain_pct={:.1}",
                s.t_matched, pct(s.t_unchanged, s.t_matched), s.t_changed, mib(s.t_plain), mib(s.t_xor),
                if s.t_plain == 0 { 0.0 } else { 100.0 * (s.t_plain as f64 - s.t_xor as f64) / s.t_plain as f64 }
            );
        }
        if bad != 0 || cas_err != 0 {
            std::process::exit(1);
        }
        drop(stores);
        prev = cur;
        if snap < a.snapshots {
            std::thread::sleep(std::time::Duration::from_millis(a.interval_ms));
        }
    }
}
