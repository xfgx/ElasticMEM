# Research map and gates after G1 — v0.14

## Evidence status
Historical D/E stand not available: prior figures remain reported/not reproduced. Old virtual-page snapshots have unknown pre-read residency and observer-effect contamination; compression bytes remain byte-count observations, not reclaimed RAM.
One verified Windows G1 0.13 snapshot (3 unidentified PIDs, 48MiB) had no extra net unique-page reduction from ordinal normalization; packed size worsened 2.68% / 8.58%. Raw page reanalysis: 62 additional cross-process copies (248KiB, 0.50% input), 92.60% of all duplicate copies were zero pages. Nothing in old inventory can recover pre-read residency retroactively.
Ordinal-region normalization is closed for this implementation/check. Semantic normalization is untested, not automatically disproven.

## Stage 1 delivered in source
QueryWorkingSetEx pre/immediate/post + VirtualQueryEx best-effort mapping checks; invalid/image/mapped/shareable skipped according to precheck. Primary input excludes observer's own PID. Per-process names and residency aggregates. Separate private/image/mapped/shareable counters, private-exclusive zero count. No claims of atomicity, zero observer effect or physical-RAM reclamation.
Brotli-only vs raw-dedup+Brotli use same per-page Fastest codec/layout, actual serialized files and byte-verified restore. Closed ordinal format remains marked reference. Raw equality decomposed within/across processes. Logs-only export excludes input data/page addresses and containers; review before sending, not guaranteed anonymous.

## Stage 2: G3 microbenchmark — pending, not implemented by this release
RX570 in supplied logs does not support GPU_UPLOAD; Agility SDK/ReBAR are not assumed to change that. Hardware-support guard remains. A separate DEFAULT-heap + UPLOAD/DMA/READBACK test is a different path, not a fallback falsely called GPU_UPLOAD.
For supported GPU_UPLOAD: bounded user-confirmed 1–4GiB pool only if available local budget allows; generation-checked CPU-write, normal CPU-read, supported aligned streaming-load instruction path, copy-engine/fence READBACK; p50/p95/p99 4KiB QD1 and throughput. Record warmup/randomization/QD, fence/driver CPU cost, clock, GPU budget/residency, device removal, cancellation and reserve.
D3D12 GPU_UPLOAD can fall back to system memory: support/budget alone does not prove that every measured byte stayed in VRAM. Detect/reject or disclose this uncertainty. Own GPU allocations only, no raw BAR access.
NVMe QD1 unbuffered file benchmark and VERIFIED pagefile-backed hard-fault benchmark must be separate; a working-set removal is not proof of a pagefile read. A controlled fault test requires source-attributed ETW evidence and checksums, without forcing dangerous whole-machine pressure.
Continuation gate: VRAM 4KiB read latency <=1/4 of measured NVMe pagefile path; disclose percentiles, not just mean. No actual gate outcome available yet.
Do not automatically disable MemoryCompression, change pagefiles, force page combining or replace system storage. Existing SSD pagefile is NOT automatically a mirror of data acknowledged only in GPU VRAM.

## Stage 2: G0 VM stand — pending, not implemented by this release
KVM Windows guest + page_idle/DAMON observations of correct guest RAM backing ranges, with known guest-touch positive/negative controls. Exclude QEMU code/host-I/O pages; handle huge-page granularity, loss, sampling windows and host reclaim. Verify tracking guest CPU accesses on actual kernel/KVM before analysis.
A/D/page_idle/DAMON measurements are sampled activity, not complete page-access events. Idle-age histogram alone cannot determine a rigorous miss-latency ceiling or process attribution. Report a model with assumptions and uncertainty; calibrate against actual misses. Never label these sampled inputs exact for existing GapModel.
Gaming/anti-cheat VM limitations remain. No claim of universal 2x hot-memory savings or physical gaming-PC ceiling.

## Stage 3: G2 — pending runtime integration
Current positive result concerns self-owned fixed-schema serialized records only; not CPython/V8 heap or managed RSS. Determine agent memory contribution before invasive runtime work.

## Kernel pagefile product gate — research boundary, not delivered
WDDM allocation/residency, physical-ownership protocol, reset/TDR durability, S3/S4, budget notifications and migration need independent validated designs. An MDL is not a demonstrated protocol for safely pinning arbitrary GPU BAR allocations. Recovery requires durable copies before acknowledged writes, not merely another enabled pagefile. No driver, signing, anti-cheat interaction, BAR takeover or automatic MemoryCompression disable in this release.

## Authoritative references
https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-psapi_working_set_ex_block
https://learn.microsoft.com/en-us/windows/win32/memory/working-set
https://microsoft.github.io/DirectX-Specs/d3d/D3D12GPUUploadHeaps.html
https://docs.kernel.org/admin-guide/mm/idle_page_tracking.html
