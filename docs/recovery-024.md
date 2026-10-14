> **Update after native VM testing:** previous Linux LLD SYS is unsafe and must not be installed. Microsoft-native-link replacement passed Server 2025 research smoke (4042 checks), unload/reload and restricted-token ACL. Windows 11/Verifier/HVCI/MSBuild remain untested. See `driver/verification/REPORT.md`; earlier unvalidated status statements below describe the initial candidate.

# Recovery and 0.24 research candidate

Recovered from the publicly published 0.23.0 source archive. Archive SHA256:
`fcb481e8af485fbc083aae4b0588e29009ffa38e37b36154d9027998a502ad31`.
Recovered Git commit: `f8bc43cd2c4cb1431a07bf7e9ff27bbe47c3bba4`.
Full prior history is retained. No 0.23.1 published archive was available; its documented changes were reapplied and extended.

## IPC defect reproduced

The actual old synthetic structured page key62 serializes to **12138 characters** with default System.Text.Json: base64 `+` becomes `\u002B`. The 0.23.0 protocol's 12000-character request cap returns `request_too_large` and exits1. This reproduces a concrete defect in the old A/B sequence, without Windows or user data.

Old Windows ZIP only reported `IPC command failed`; it omitted the command/error class. The file was not available in the recovery session. Therefore the ZIP alone does not establish the original failing command. The fixture defect above is directly reproduced, not an assumption about UTF8.

0.24 fixes request AND response caps to shared **65536** characters: enough for the worst default-escaped base64 representation of a 4096-byte page plus bounded header. Deadlines cover send, flush and bounded read including absent-newline responses. UTF8/BOM contract is explicit. Failed IPC retains memory-stage progress and safe diagnostics in the logs-only ZIP. Failure remains `failed_no_valid_result`, never promoted to success.

Transport regressions include random and repetitive pages, a base64 string consisting almost entirely of `+`, raw/zstd modes, UTF8 with/without BOM, rejection, cancellation, deadline, oversized replies, private export exclusions.

## Driver path

`driver/`: actual KMDF non-PnP control driver, documented XPRESS codec in paged pool, handle isolation, CRC, quota, atomic overwrite, safe buffered IOCTLs, client and Windows smoke.

There is **no transparent foreign-process pager**. There are no supported Memory Manager replacement hooks implemented here. The backend only stores pages explicitly submitted by an owning client. It does not improve the memory usage of unchanged Chrome/Telegram. Driver storage correctness and kernel RAM/latency benefit are separate tasks.

Code is cross-compiled against official WDK/SDK headers/libraries. Kernel flags disable SSE/red-zone/vectorization, enable stack protection and CFG; native-link uses KMDF startup, driver characteristics, NX/ASLR/integrity check and checksum. This is **not** WDK/MSBuild verification, signing, HVCI certification or execution. The provided SYS is unsigned, not installable by the supplied installer as-is.

All Windows execution, Verifier, stop/unload/reload, reboot and ACL checks remain pending. PC/test1 MCP tools could not be discovered twice during recovery. No remote machine changes or release-server publication were performed.

## Dependencies and build

.NET SDK pinned to8.0.426. Codec dependencies unchanged; SDK ILLink lockfiles updated to8.0.32. Windows single-file locked publish is tested separately from portable .NET tests, because the restore properties differ. To reproduce the exact release use `release/build-release.sh`; don't rewrite the Windows lockfile with a Linux-only restore first.

See driver/README.md for limits, signing requirements, scripts and VM test plan. No credentials, private signing keys, WDK packages or user memory dumps are included in the candidate package.
