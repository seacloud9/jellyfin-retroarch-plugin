# Prototype verification record

Recorded October 2, 2026, and updated October 21, 2026, on macOS arm64 using .NET SDK 10.0.401.

## Completed locally

| Check | Result | Scope |
|---|---|---|
| Locked dependency restore | Passed | NuGet package lock files match the projects |
| Release build | Passed, zero warnings/errors | Domain, Jellyfin plugin, companion, tests |
| .NET tests | 20 passed | Indexing, identity, filesystem checks, snapshot preservation, HTTP authorization/filtering, launch arguments, save-path isolation |
| Formatting | Passed | `dotnet format --verify-no-changes --no-restore` |
| Companion integration smoke | Passed | Substitute HTTP server and emulator process; cache reuse/repair, corrupt download rejection/cleanup, repeated save-directory use |
| Dashboard browser harness | Passed | Search, platform filter, folder saving, scan feedback; no reported browser errors |
| Real Jellyfin smoke (Oct 21) | Passed | `scripts/jellyfin-smoke.py` with pinned `jellyfin/jellyfin:12.1` (12.1.0) in Docker: packaged plugin loads Active, config page served, scan/filter/checksum/download/rescan, 401/403 for anonymous/non-admin users, root revocation, restart preserves config and identity |
| Native save persistence (Oct 21) | Passed | Same run with `--native-executable`: companion downloaded the save-counter fixture from real Jellyfin and launched sandboxed RetroArch 1.22.2 with Nestopia (buildbot nightly, arm64) twice; `.srm` counter went 1 → 2 |
| Linux native save (Oct 21) | Passed | `--native-linux-docker`: headless RetroArch 1.18.0 + Nestopia (Ubuntu 24.04 packages, arm64) in Docker against the same real Jellyfin; counter 1 → 2. See [Linux Docker test](linux-native-docker.md) |
| Catalog persistence (Oct 21) | Passed | 3 new unit tests (restart reload, failed scan keeps snapshot, corrupt/tampered rejected; 23 total); real Jellyfin restart restores catalog without rescan |
| Plugin packaging | Passed | ZIP contains `Jellyfin.Plugin.RetroBridge.dll` and `RetroBridge.Domain.dll`; SHA-256 sidecar generated |

Build artifacts and browser harness outputs are generated locally under ignored directories and are not committed. CI is configured to upload plugin and companion build artifacts; this record does not assert that hosted CI has run successfully.

## Limits of this evidence

The HTTP tests use an ASP.NET test host and a substitute identity provider. The browser harness uses substitute `ApiClient` responses without Jellyfin styling. The companion test uses synthetic bytes and a process substitute that writes a marker file. None of these establishes real Jellyfin authentication/plugin loading, actual gameplay, or RetroArch save persistence.

Real Jellyfin and native emulation are verified on macOS arm64 only. The native run uses a wrapper adding `--max-frames` so RetroArch exits unattended; interactive quitting and save states were not exercised. Linux was verified in Docker on arm64 only; the x86_64 CI run has not yet been observed. Windows native runs remain open. The fixture proves save loading/flushing, not gameplay compatibility.

The real-server runs found two companion bugs that substitutes missed: Jellyfin 12.1 rejects `X-Emby-Token`-only requests, and it serializes user IDs without dashes. Both are fixed.

## Reproduce

From the repository root with the pinned SDK and Python installed:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
python3 scripts/launcher-smoke.py
python3 scripts/package-plugin.py
python3 scripts/dashboard-harness.py
python3 scripts/jellyfin-smoke.py  # requires Docker
# Add --native-executable, --native-core (and --native-temp-parent for sandboxed macOS RetroArch) for the save test
```

The launcher smoke script currently requires macOS/Linux. Open `artifacts/dashboard-harness.html` for the browser check. Follow the [real integration checklist](manual-smoke.md) to complete the outstanding acceptance checks.
