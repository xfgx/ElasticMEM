# Application integration 0.9.0

G0/G1/G2/G3 now run inside C# app. D3D12 via pinned Vortice.Direct3D12 3.8.3, no Python/VS/C++ required by user.
This is a research application build, NOT a transparent VRAM pager or CPython heap patch.
Full Windows access-trace acquisition remains external; no automatic global optimization based on model numbers.

GUI: left navigation, consistent dark/light palette, scrollable rows, larger targets, own ListView headers.
Native visual QA is NOT performed on Linux. User must inspect all tab screenshots via built-in UI audit at actual DPI.

Run manifests: actual EXE plus external assembly inventory; source commit, build-dirty/layout fields and parameters;
raw outputs/hashes; integrity checked before export. Exported-byte integrity distinguished from source-run integrity.
Network uploader/embedded credential removed. Old credential still needs revocation by its service owner.
Legacy agent is explicit/manual; laboratory mutually excludes agent/legacy work inside this instance.

Core selftests + prior randomized store/codec tests run on Linux. App cross-build and self-contained publish are
build validation only; actual Windows P/Invoke, D3D12, cancellation/shutdown and UI behavior require user smoke test.
See README-WINDOWS.md and CHECK-WINDOWS.cmd in release package for log return procedure.
