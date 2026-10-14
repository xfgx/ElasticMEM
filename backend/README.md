# ElasticMem 0.27 — Windows backend orchestration preview

## Exact scope — do not call this a complete SYS/VMM pager

The host user keeps the Windows desktop. The new **Backend VM** tab manages one explicitly paired local Hyper-V Windows VM. Microsoft RemoteApp presents guest application windows on the host. Existing host PIDs are NOT migrated; RDS launches fresh guest processes and uses guest profiles/accounts.

Implemented source paths:
- Host GUI/tray mode `--backend-agent`; optional explicit per-user logon task (not SYSTEM service).
- Hyper-V pairing by VM GUID, start, guest shutdown (no force TurnOff), Dynamic Memory budgets while VM off.
- Guest admin credentials via local Get-Credential, DPAPI CLI XML, current user/machine, private ACL. No password sent to logs/chat or RDP files.
- PowerShell Direct read-only guest process/memory observations; stop exactly a selected allowlisted interactive-session PID with start-time and stable handle checks. No host PID termination, bulk tree kill or memory injection.
- Guest Memory Compression opt-in, existing RDS RemoteApp publication with valid publisher signatures. Clipboard/drive/printer/smartcard forwarding disabled; RDP server authentication required.
- RDP target DNS/IP is checked against guest IPv4; multi-session-host RDS farms not supported.
- Baseline/export logs: VM assignment/demand, host available physical counters, guest counters and process WS/private commit. Per-process unique physical saving and causal compression benefit remain null.

**This management path uses Windows guest compression + Hyper-V Dynamic Memory, NOT ElasticMemStore.sys.** Windows chooses what to compress; no per-app success guarantee. A local VM has overhead and may worsen total host RAM. Observe assignment reductions and full-host workload; do not infer savings by summing process working sets.

## SYS-backed custom-VMM building blocks (not connected to Hyper-V)

`src/ElasticMem.Core/Backend/Vmm/VmPageProvider.cs` implements a bounded lifecycle:
1. Caller quiesces all guest vCPUs AND I/O/DMA; pinned/DMA pages rejected.
2. Copy an owned page into SYS store; on quota/failure keep original.
3. Unmap GPA, then MEM_DECOMMIT owned backing; only then resume.
4. VMM unmapped-GPA exit calls restore before guest execution resumes: GET/hash check -> commit -> copy -> map.
5. Restore or rollback failure keeps VM paused, never returns fabricated bytes.

`WhpPageBacking.cs` provides Windows Hypervisor Platform mapping and native VirtualAlloc/MEM_DECOMMIT primitives for a partition **owned by a custom VMM**; `SysVmStore` uses the existing driver IOCTL store. No foreign PID/address API. This is **not** a Windows-bootable VMM, QEMU/WHPX plug-in or Hyper-V memory-provider registration. There is no automatic cold-page selector, implemented vCPU/DMA barrier, full I/O/firmware/device emulation, whole-VM persistence, or automatic application paging in this path. Existing SYS limits remain (4096 keys/session,8 sessions,16MiB requested/session,64MiB tracked/device). No new SYS binary was built or natively qualified in 0.27.

Portable lifecycle tests use test doubles, not real Windows/WHP/SYS. New orchestration was cross-built and scripts syntax-checked on Linux; **no Windows Hyper-V/RDS end-to-end test here**. The old 4042 native Server2025 checks apply only to the old exact driver/0.24 smoke, not these new modules.

## Windows setup prerequisites

1. Host Windows Pro/Enterprise/Windows Server with Hyper-V installed, administrator access, hardware/nested virtualization and enough RAM. If host itself is a VM, its parent must legitimately provide nested virtualization. Do not disable Secure Boot/HVCI/signature enforcement to force operation.
2. A registered local Windows Server backend VM with a snapshot, networking to host, PowerShell Direct integration, domain membership where required by RDS deployment.
3. **Supported configured RDS deployment + connection broker + single Session Host collection + appropriate RDS/Windows licenses**. No registry patches to enable unsupported Windows11 RemoteApp hosting; no license/grace reset. The program does not provision an AD domain, install a Windows OS, or silently set up the RDS infrastructure.
4. Install signed official x64 Edge, Chrome, Telegram system-wide inside guest. Telegram in an unrelated user's LocalAppData is intentionally not published; use an approved system-wide installation or existing supported RemoteApp deployment. Log into apps inside guest normally; no host tokens/profiles copied.
5. A trusted RDP server certificate matching the configured hostname, NLA and authorized RDS user. Do not accept an unexpected backend or weaken authentication. RDP user's credentials are independent of guest admin management credentials.
6. Windows PowerShell5.1 and an approved local script execution/signing policy. Worker does not pass Bypass/Unrestricted. If policy rejects it, stop and use your approved code-signing/admin process.

Suggested supported RDS provisioning, **inside an already domain-joined Server**, by its authorized administrator (not executed by ElasticMem): use Server Manager Remote Desktop Services deployment wizard, add Session Host/Broker/Web Access roles, create a single-host session collection and configure licenses/certificates/user access. Follow your Microsoft deployment policy; domain, certificates, networking and licensing are environment-specific. Do not create a fake registry-based RemoteApp server.

## Installation and GUI workflow

1. Check package SHA256, extract **all files** into a new folder. Close old ElasticMem agents; don't run two copies. Backend worker is embedded in EXE; source scripts included for review.
2. Run `backend\windows\Preflight.ps1` (read-only) in elevated Windows PowerShell. Missing Hyper-V/admin is failure, not successful optimization.
3. Run ElasticMem.exe as Administrator -> **Backend VM**. Choose VM name, reachable RDS hostname/IP, existing collection, min/startup/max MiB and buffer. Defaults2/4/8GiB: ensure host capacity; they are not universally optimal.
4. **1 · Save/pair** confirms VM GUID. If missing/mismatched VM, no actions. **2 · Guest account** opens native credential prompt; credential never enters chat/logs. Default stored under `%LOCALAPPDATA%\ElasticMem\backend-private` with private ACL.
5. VM off: **3 · Dynamic Memory**, then **Start VM**. Existing running VM cannot receive startup/min configuration through this button.
6. **4 · Enable guest Windows compression**; confirm. Unsupported/rejected API stays failed; it is NOT emulated with trim. Host Memory Compression/pagefile unchanged.
7. **5 · Publish RemoteApps** requires the preconfigured deployment. Partial/not-installed/signature-rejected rows are not full success. Existing conflicting aliases are not overwritten.
8. **Refresh**: ensure running VM, guest memory status and matching RDP target. **Open Edge/Chrome/Telegram on host** launches mstsc RemoteApp using alias. Normal RDP authentication may appear. A started mstsc is not proof guest app launched; refresh list and confirm guest PID/session.
9. Select guest PID to stop; explicit confirmation warns unsaved data can be lost. Noninteractive/session0 or reused PID identity is rejected.
10. Capture a baseline, do the same workload, wait for Windows/Hyper-V decisions, refresh/export. Repeat comparable runs. `MemoryAssigned` delta is an observation, not causal compression savings or application speedup. `compressed_store_process_ws_bytes` is not compressed payload and may be null.

## Background at user logon

- Test the GUI first. Check **Auto-start selected VM** only if desired; no automatic shutdown/restart, guest OS install or migration.
- `ElasticMem.exe --backend-agent` opens Backend tab, starts monitoring and minimizes to existing tray. This is an interactive user agent, not a privileged SYSTEM service; RemoteApp requires a logged-in user.
- Optional elevated PowerShell:
  ```powershell
  .\backend\windows\Install-Agent.ps1 -ProgramDirectory 'C:\path\ElasticMem' -ConfirmInstall
  ```
  Copies EXE to protected ProgramFiles, registers one user-logon Highest task with explicit consent. No password is stored in scheduled task. If destination/task already exists, installer refuses to overwrite. After install launch the protected EXE with `--backend-agent` or log off/on.
- DPAPI credential must belong to the same Windows user/machine as the task. If installing with a DIFFERENT admin account, that account's task/credential is not yours; use correct authorized account.
- Tray Open restores normal UI. Exit stops monitoring, **not guest VM/apps**. Disable task with `Uninstall-Agent.ps1`; inspect/remove protected program folder and private credentials separately. Guest settings/publication not auto-reverted; use checkpoint for full rollback.

## Logs / safety / testing boundary

Export ZIP via Backend tab. It excludes DPAPI credentials, private captures, command lines, passwords and RDP files. Includes backend events, process PIDs/session IDs/start ticks/WS/private commit, VM GUID/guest computer/OS/IP observations; review metadata before sending. A timeout reports **outcome_unknown**; refresh before retrying mutations. Never force unload or power-off to mask an error.

Source scripts/contract tests/QA.json state exactly what was checked. New Windows5.1 runtime, Hyper-V/nested Hyper-V, RDS licensing/certificate flows, native WHP eviction/restore, cold-page policies, shutdown races and production qualification remain to be tested. **Do not label the custom-SYS paging architecture complete or production-ready.**
