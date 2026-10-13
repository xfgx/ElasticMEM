#!/usr/bin/env python3
"""Linux sampled page-idle observations; NEVER a complete access trace."""
import argparse, csv, hashlib, json, os, platform, struct, time
from pathlib import Path

IDLE = Path('/sys/kernel/mm/page_idle/bitmap')
def probe():
    cap = next((x.split(':')[1].strip() for x in Path('/proc/self/status').read_text().splitlines() if x.startswith('CapEff:')), '0')
    return {'platform': platform.platform(), 'page_idle_available': IDLE.exists(), 'cap_sys_admin': bool(int(cap, 16) & (1 << 21)),
            'damon_sysfs_available': Path('/sys/kernel/mm/damon/admin/kdamonds/nr_kdamonds').exists(), 'trace_kind': 'sampled',
            'full_trace_available': False}

def inventory(pid, ps, cap_pages):
    maps = Path(f'/proc/{pid}/maps').read_text(); addresses = []
    for line in maps.splitlines():
        parts = line.split(); lo, hi = (int(x, 16) for x in parts[0].split('-'))
        if not parts[1].startswith('rw') or parts[1][-1] != 'p': continue
        addresses.extend(range(lo, min(hi, lo + (cap_pages - len(addresses)) * ps), ps))
        if len(addresses) >= cap_pages: break
    return maps, addresses

def pfns(pid, addresses, ps):
    out = {}
    with open(f'/proc/{pid}/pagemap', 'rb', buffering=0) as f:
        for addr in addresses:
            raw = os.pread(f.fileno(), 8, addr // ps * 8)
            if len(raw) != 8: continue
            value = struct.unpack('<Q', raw)[0]
            if value >> 63: out[addr] = value & ((1 << 55) - 1)
    if out and all(p == 0 for p in out.values()): raise PermissionError('PFNs hidden: need CAP_SYS_ADMIN, not just root in a container')
    return out

def collect(pids, out, interval, samples, max_pages):
    info = probe()
    if not info['page_idle_available'] or not info['cap_sys_admin']:
        raise RuntimeError('page_idle unavailable or CAP_SYS_ADMIN missing; no trace collected')
    if interval <= 0 or samples < 1 or max_pages < 1: raise ValueError('Invalid collection configuration')
    ps = os.sysconf('SC_PAGE_SIZE'); out = Path(out); out.mkdir(parents=True, exist_ok=False)
    initial = {}
    for pid in pids:
        # Include process start time to detect PID reuse, inventory is capped, not whole-machine.
        stat = Path(f'/proc/{pid}/stat').read_text(); start = stat.rsplit(')', 1)[1].split()[19]
        initial[pid] = start
    valid, unknown, observed = 0, 0, 0
    with IDLE.open('r+b', buffering=0) as idle, (out / 'observations.csv').open('w', newline='') as f:
        writer = csv.writer(f); writer.writerow(['sample_start_ns','sample_end_ns','pid','vaddr','pfn','accessed','status'])
        for sample in range(samples):
            before = {}; masks = {}
            for pid in pids:
                start = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()[19]
                if start != initial[pid]: raise RuntimeError('PID reused')
                maps, addresses = inventory(pid, ps, max_pages)
                (out / f'maps-{pid}-{sample}.txt').write_text(maps)
                before[pid] = (addresses, pfns(pid, addresses, ps))
                for pfn in before[pid][1].values(): masks[pfn // 64] = masks.get(pfn // 64, 0) | (1 << (pfn % 64))
            t0 = time.monotonic_ns()
            for word, mask in masks.items():
                if os.pwrite(idle.fileno(), struct.pack('<Q', mask), word * 8) != 8: raise OSError('Short idle write')
            armed = time.monotonic_ns(); time.sleep(interval); words = {}
            for word in masks:
                raw = os.pread(idle.fileno(), 8, word * 8)
                if len(raw) != 8: raise OSError('Short idle read')
                words[word] = struct.unpack('<Q', raw)[0]
            t1 = time.monotonic_ns()
            for pid, (addresses, old) in before.items():
                now = pfns(pid, addresses, ps)
                for addr in addresses:
                    observed += 1; pfn = old.get(addr)
                    if pfn is None or now.get(addr) != pfn:
                        unknown += 1; writer.writerow([t0,t1,pid,hex(addr),'','', 'not_present_or_mapping_changed']); continue
                    accessed = not bool(words[pfn // 64] & (1 << (pfn % 64))); valid += 1
                    writer.writerow([t0,t1,pid,hex(addr),pfn,int(accessed),'sampled'])
            f.flush()
    meta = dict(info, schema='elasticmem.idle.v1', pids=pids, process_start_ticks=initial, page_size=ps, interval_s=interval, samples=samples,
                max_pages_per_pid=max_pages, observed_rows=observed, valid_rows=valid, unknown_rows=unknown,
                limitations=['rw private mappings only; capped address-order selection, present PFNs only',
                    'One accessed bit cannot reveal number/order/timestamps of accesses or reads versus writes',
                    'Arming/reading bitmap is non-atomic; effective windows differ across PFNs',
                    'PFN mapping check cannot exclude PFN reuse ABA; shared physical aliases possible',
                    'May interfere with reclaim/DAMON; do not run DAMON concurrently',
                    'Linux workload is not a Windows workload; never feed these observations to g0.py'])
    (out / 'metadata.json').write_text(json.dumps(meta, indent=2)+'\n')

if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('--probe', action='store_true'); p.add_argument('--pids', nargs='+', type=int)
    p.add_argument('--out'); p.add_argument('--interval', type=float, default=.1); p.add_argument('--samples', type=int, default=100); p.add_argument('--max-pages', type=int, default=8192)
    a = p.parse_args()
    if a.probe: print(json.dumps(probe(), indent=2))
    elif not a.pids or not a.out: p.error('--pids and --out required')
    else: collect(a.pids, a.out, a.interval, a.samples, a.max_pages)
