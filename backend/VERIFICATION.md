# Backend 0.27 verification boundary

Implemented, compiled source: host Backend VM GUI, Windows PowerShell Direct worker, RemoteApp file generation, explicit Hyper-V memory configuration, guest compression opt-in, selected guest PID termination and tray agent. These are **not natively executed here**.

Executed portable tests: 14 backend contract checks,13 VMM lifecycle test-double checks,16 sample-bench/privacy checks,73217 research selftests and main suite. Separate transport suite is recorded in QA. PowerShell7.4.13 Linux Parser syntax-checks all4 worker/agent/preflight scripts; this is not Windows5.1/Hyper-V/RDS execution.

Custom SYS path: model lifecycle and native WHP/VirtualAlloc/SYS adapters exist in source, but there is **no connected Windows-bootable VMM, actual pause/DMA barrier, memory-exit dispatcher or automatic eviction policy**. No new SYS produced. Driver C/kmdf/client unchanged from previously tested commit. Do not call Hyper-V DM/guest MMAgent a custom driver page-compression implementation.

Native checks still required: Windows GUI/tray/user-logon task, PowerShell5.1 execution policy, local Hyper-V GUID pairing, guest credential DPAPI, RemoteApp deployment/collection/licensing/cert/NLA/guest session flows, memory APIs and assignment observations, PID-race rejection, shutdown, export privacy, native WHP mapping/decommit/restore with a complete VMM, crashes/OOM/lifetime/fault/DMA races and Verifier/HVCI.

No source scripts disable security, bypass managed policy, patch Windows11 RDS support, reset licensing, inject code, migrate host PIDs or terminate host apps. Raw exception text and credentials are excluded from backend logs; users should still review guest metadata before sharing.

Transport suite: PASS578 Linux-owned-process/export checks. Not Windows execution.
