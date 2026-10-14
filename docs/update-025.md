# 0.25.0 — application compression measurement update (local research candidate)

- Application filter: all / Microsoft Edge renderers / Chrome renderers / Telegram Desktop.
- Existing read-only resident/private/exclusive capture and two identity-bound rounds retained; max 8 MiB/process/round, up to2 Edge +2 Chrome +1 Telegram. No trim, process write, injection, SeDebugPrivilege escalation or foreign page release.
- New summary CSV: application, PID, round, backend, codec, block bytes, input/stored bytes, representation ratio/reduction, descriptive encode/restore latency and full-byte verification. No content/addresses in this CSV.
- Optional explicit research-VM kernel path: SAME copied sample into RAW then XPRESS; bounded <=2048 pages, one PUT pass and first +3 warm GET passes, full equality verification and baseline RESET. Separate deterministic random control; never replaces missing app evidence. Kernel pages do not persist beyond handle lifetime.
- Portable benchmark accepts an interface; tests use a test double, NOT native XPRESS. New tests cover input/cap/empty/corruption/reset/cancellation, snake-case JSON and safe CSV export excluding snapshot/container bytes.
- New family coverage distinguishes unavailable and not_discovered; partial/error/cancelled runs cannot claim full successful measurements.
- Full Russian installation/trust/VM/uninstall/rollback/log interpretation guide embedded and included. New VM signing helper signs only exact previous verified unsigned native SYS, key non-exportable, requires already permitted test-signing; no auto-policy changes. New log ZIP verifier reads EXPORT.json without extracting files. Both new PowerShell wrappers still require Windows validation.
- Driver C and binary unchanged from native Server 2025 validation; stock MSBuild, Windows11/HVCI/Verifier still unqualified. Prior 4042 synthetic native checks are NOT a claim that 0.25 application test ran on Windows.

**No measured unique physical RAM saving, no application speedup, no transparent browser/Telegram compressed backing.** Missing metrics remain null. Raw captures/containers stay local on disk and are excluded from logs-only ZIP; review exported metadata before sharing. Delete local sensitive artifacts after use if not needed.

Build: pinned .NET8.0.426, self-contained Windows x64 single-file. No public updater/network release publication requested or performed.
