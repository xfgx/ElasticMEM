#!/usr/bin/env python3
"""Offline interval envelope, NOT a universal machine-memory ceiling."""
import argparse, csv, json, math
from collections import defaultdict
from pathlib import Path

HEADER = ['timestamp_ns', 'pid', 'page', 'op']

def load_trace(path, meta):
    if meta.get('schema') != 'elasticmem.events.v1':
        raise ValueError('metadata.schema must be elasticmem.events.v1')
    if meta.get('trace_kind') not in ('exact', 'synthetic'):
        raise ValueError('Sampled observations are not access traces. Refusing oracle analysis.')
    for key in ('source', 'scope', 'limitations', 'page_size', 'start_ns', 'end_ns', 'clock', 'event_loss_count', 'access_definition', 'process_identity'):
        if key not in meta: raise ValueError('Missing metadata: ' + key)
    if not isinstance(meta['limitations'], list) or any(not isinstance(x, str) for x in meta['limitations']): raise ValueError('limitations must be a list of strings')
    if type(meta['event_loss_count']) is not int or meta['event_loss_count'] != 0: raise ValueError('Known or unknown event loss: exact replay refused')
    for key in ('source', 'scope', 'clock', 'access_definition', 'process_identity'):
        if not isinstance(meta[key], str) or not meta[key].strip(): raise ValueError('Empty/invalid provenance field: ' + key)
    ps, start, end = meta['page_size'], meta['start_ns'], meta['end_ns']
    if type(ps) is not int or ps <= 0 or ps & (ps - 1): raise ValueError('Invalid page_size')
    if type(start) is not int or type(end) is not int or not 0 <= start < end: raise ValueError('Invalid trace window')
    live, intervals, stats = {}, [], defaultdict(lambda: {'page_time_ns': 0, 'accesses': 0, 'peak_pages': 0, 'live': 0})
    previous, event_count = start, 0
    with open(path, newline='') as f:
        reader = csv.DictReader(f)
        if reader.fieldnames != HEADER: raise ValueError('Expected CSV header ' + ','.join(HEADER))
        for row in reader:
            event_count += 1
            if None in row or any(v is None for v in row.values()): raise ValueError('Malformed CSV row: extra/missing fields')
            try: t, pid, page = int(row['timestamp_ns']), int(row['pid']), int(row['page'], 0)
            except (TypeError, ValueError): raise ValueError(f'Invalid numeric event at line {event_count + 1}')
            if t < previous or not start <= t <= end: raise ValueError('Unsorted/out-of-window event')
            if pid <= 0 or page < 0 or page % ps: raise ValueError('Invalid PID or unaligned page')
            previous = t; key = (pid, page); op = row['op']; s = stats[pid]
            if op == 'map':
                if key in live: raise ValueError('Duplicate map')
                live[key] = [t, t, False]; s['live'] += 1; s['peak_pages'] = max(s['peak_pages'], s['live'])
            elif op in ('read', 'write'):
                if key not in live: raise ValueError('Access without map (complete lifecycle required)')
                mapped, last, accessed = live[key]
                if t > last: intervals.append((pid, t - last, True, 'interior' if accessed else 'initial'))
                live[key][1] = t; live[key][2] = True; s['accesses'] += 1
            elif op == 'unmap':
                if key not in live: raise ValueError('Unmap without map')
                mapped, last, accessed = live.pop(key); s['page_time_ns'] += t - mapped; s['live'] -= 1
                if t > last: intervals.append((pid, t - last, False, 'unmap_tail'))
            else: raise ValueError('Unknown op: ' + op)
    for (pid, page), (mapped, last, accessed) in live.items():
        stats[pid]['page_time_ns'] += end - mapped
        if end > last: intervals.append((pid, end - last, False, 'censored_tail'))
    if not event_count or not sum(x['page_time_ns'] for x in stats.values()): raise ValueError('Empty trace or zero page-time')
    return intervals, dict(stats), event_count

def envelope(intervals, stats, meta, eviction_us, recovery_us, mean_budgets_us, max_fault_us, boundary_mode='interior'):
    if boundary_mode not in ('interior', 'all'): raise ValueError('Invalid boundary mode')
    if not mean_budgets_us: raise ValueError('At least one mean budget required')
    for value in [eviction_us, recovery_us, max_fault_us, *mean_budgets_us]:
        if not math.isfinite(value) or value < 0: raise ValueError('Costs/budgets must be finite and nonnegative')
    if eviction_us <= 0 or recovery_us <= 0: raise ValueError('Eviction/recovery must have positive cost')
    e, r = eviction_us * 1000, recovery_us * 1000
    # Defined gap-score objective; NOT an upper bound on demand paging's actual absence.
    candidates, tails = [], defaultdict(float)
    for pid, gap, needs_restore, kind in intervals:
        if boundary_mode == 'interior' and kind != 'interior': continue
        saved = max(0, gap - e - (r if needs_restore else 0))
        if not saved: continue
        if needs_restore: candidates.append((saved, pid))
        else: tails[pid] += saved
    candidates.sort(key=lambda x: (-x[0], x[1]))
    accesses = sum(s['accesses'] for s in stats.values()); duration = meta['end_ns'] - meta['start_ns']; ps = meta['page_size']
    def row(pid, saved, faults, budget):
        s = stats[pid]; denom = s['page_time_ns']
        return {'pid': pid, 'mean_stall_budget_us': budget, 'scope_accesses': s['accesses'],
                'baseline_mean_mapped_bytes': denom * ps / duration,
                'optimistic_mean_saved_bytes': saved * ps / duration,
                'saved_fraction_of_mapped_page_time': saved / denom if denom else 0,
                'modeled_recoveries': faults, 'serial_stall_sum_us': faults * recovery_us,
                'modeled_mean_added_us_per_access': faults * recovery_us / s['accesses'] if s['accesses'] else 0}
    curves = []
    for budget in sorted(set(mean_budgets_us)):
        k = min(len(candidates), math.floor(budget * accesses / recovery_us + 1e-12)) if recovery_us <= max_fault_us else 0
        saved, faults = defaultdict(float, tails), defaultdict(int)
        for amount, pid in candidates[:k]: saved[pid] += amount; faults[pid] += 1
        rows = [row(pid, saved[pid], faults[pid], budget) for pid in sorted(stats)]
        base = sum(s['page_time_ns'] for s in stats.values()); total_saved = sum(saved.values())
        curves.append({'mean_stall_budget_us': budget, 'aggregate': {
            'baseline_mean_mapped_bytes': base * ps / duration,
            'optimistic_mean_saved_bytes': total_saved * ps / duration,
            'saved_fraction_of_mapped_page_time': total_saved / base,
            'modeled_recoveries': k, 'serial_stall_sum_us': k * recovery_us,
            'modeled_mean_added_us_per_access': k * recovery_us / accesses if accesses else 0}, 'processes': rows})
    ideal = sum(tails.values()) + sum(x[0] for x in candidates)
    return {'schema': 'elasticmem.g0.v1', 'status': 'synthetic_validation_only' if meta['trace_kind'] == 'synthetic' else 'scenario_model_not_machine_ceiling',
            'boundary_mode': boundary_mode, 'trace_kind': meta['trace_kind'], 'source': meta['source'], 'scope': meta['scope'], 'costs': {'eviction_us': eviction_us, 'recovery_us': recovery_us, 'max_fault_us': max_fault_us},
            'curve_budget_definition': 'Global serial recovery cost / all observed accesses; NOT measured application slowdown. Per-PID rows share this global budget.',
            'metric': 'Time-average saved bytes / mapped page-time; not physical unique RSS or instantaneous capacity.',
            'unbudgeted_gap_score_optimum_fraction': ideal / sum(s['page_time_ns'] for s in stats.values()),
            'unbudgeted_optimum_definition': 'All eligible gap scores selected without latency-budget cap; NOT physical/demand-paging upper bound.',
            'curves': curves, 'limitations': [
                'Future-aware independent intervals; unlimited external storage, no bandwidth/queue contention, no metadata/buffers/reserve.',
                'No compression ratio assumed. No physical alias deduplication: shared pages may be counted repeatedly.',
                'Map implies fully resident initialized page. Default interior excludes initial and terminal gaps; all-mode includes censor-sensitive boundary savings.',
                'No rescheduling of subsequent accesses under stall; trace is baseline timeline. Not a deployable policy.',
                'gap-minus-transfer score is an abstract objective, neither demand-stall simulation nor a rigorous universal/prefetch upper bound.',
                'Do not close software research from a 15-20% threshold on this model alone.',
                *meta['limitations']]}

def analyze(trace, metadata, out, eviction_us=10, recovery_us=100, budgets=(0, .1, 1, 10), max_fault_us=100, boundary_mode='interior'):
    meta = json.loads(Path(metadata).read_text()); intervals, stats, count = load_trace(trace, meta)
    result = envelope(intervals, stats, meta, eviction_us, recovery_us, budgets, max_fault_us, boundary_mode)
    result['event_count'] = count
    out = Path(out); out.mkdir(parents=True, exist_ok=True)
    (out / 'g0.json').write_text(json.dumps(result, indent=2) + '\n')
    with (out / 'curve.csv').open('w', newline='') as f:
        rows = [dict(x['aggregate'], mean_stall_budget_us=x['mean_stall_budget_us']) for x in result['curves']]
        writer = csv.DictWriter(f, fieldnames=list(rows[0])); writer.writeheader(); writer.writerows(rows)
    return result

if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('trace'); p.add_argument('metadata'); p.add_argument('out')
    p.add_argument('--eviction-us', type=float, default=10); p.add_argument('--recovery-us', type=float, default=100)
    p.add_argument('--boundary-mode', choices=['interior','all'], default='interior')
    p.add_argument('--mean-budgets-us', default='0,0.1,1,10'); p.add_argument('--max-fault-us', type=float, default=100)
    a = p.parse_args(); analyze(a.trace, a.metadata, a.out, a.eviction_us, a.recovery_us, tuple(map(float, a.mean_budgets_us.split(','))), a.max_fault_us, a.boundary_mode)
