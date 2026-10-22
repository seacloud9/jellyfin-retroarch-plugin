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
| Players, pairing, tickets (Oct 21) | Passed | 2 new API tests (allow-list, ticket binding/replay/expiry; 25 total). Real Jellyfin: companion paired through real Quick Connect as a non-admin player, redeemed a ticket, downloaded verified content, started a stub process; replayed and other-device tickets rejected; removing the player blocked tickets and the device token's downloads |
| Dashboard players (Oct 21) | Passed | jsdom run of the harness: player checkboxes exclude administrators, render names as text, save selections; fixed a malformed NES filter option |
| One-click play (Oct 21) | Passed | 15 new tests (link parser rejects 12 hostile shapes, device isolation, Play page CSP; 40 total). Real Jellyfin: jsdom drove the served Play page with a player session, listed the paired device via `IDeviceManager`, clicked Play, and the captured link launched through `open`; a second click was refused. With `--os-link-handler` on macOS, LaunchServices delivered the link to the registered applet, which launched; it was unregistered and deleted afterwards |
| Phone remote launch (Oct 21) | Passed | Unit test: delivery only while listening, device-bound single claim, expiry, listener timeout (41 total). Real Jellyfin smoke: jsdom with an iPhone user agent gets a clear message and no link when idle; with `listen` running, Play is delivered and the listener launches. Manual: a real tap in Chrome on the Android 17 emulator started the game through the Mac's listener; iOS 26.5 Safari showed the device as ready |
| Mobile platforms (Oct 21) | Assessed | iOS cannot launch RetroArch content from another app; it works as a remote only |
| Android companion (Oct 21) | Passed on emulator | `scripts/android-smoke.py --play-page` on the Android 17 arm64 emulator with RetroArch 1.22.2 nightly and real Jellyfin: Quick Connect pairing, ticket launch, save 1 → 2, empty-save restore, replay/hostile rejection, real Chrome Play tap. 49-cycle save stress test clean. 4 Kotlin unit tests; CI builds, tests, and lints the APK. No real phone tested |
| Link registration (Oct 21) | macOS passed locally | `scripts/link-handler-check.py` registers, inspects, and removes the handler; CI runs it on all three OSes |
| Incremental scans (Oct 21) | Passed | 3 new tests (unchanged/changed/new files, same-size-and-timestamp limit, restart via snapshot; 44 total). Real Jellyfin: unchanged rescan hashed 0, full rescan hashed 1, first scan after restart hashed 0; dashboard status checked in jsdom |
| Android core detection (Oct 21) | Passed on emulator | RetroArch's installed-cores query answered with the Nestopia core; Android smoke passed with the detected path. 2 new Kotlin tests (preference order, hostile core names; 6 total) |
| Listener autostart (Oct 21) | macOS passed | `jellyfin-smoke.py --os-autostart`: launchd started the listener from the LaunchAgent; a phone-style Play was delivered and launched; `autostart off` unloaded and removed it. A second listener on the same data directory was refused. `link-handler-check.py` checks the autostart entry on all three OSes in CI |
| Background and scheduled scans (Oct 21) | Passed | API test: 202, status polling, admin-only (45 total). Real Jellyfin: background scan finished and published; Jellyfin listed `RetroBridgeScan` with its daily trigger and ran it to `Completed` via `/ScheduledTasks/Running`; dashboard polling checked in jsdom |
| Titles (Oct 21) | Passed | 6 title-cleaning cases and an override API test (52 total). Real Jellyfin: override applied, survived restart via XML plugin configuration, and reset |
| Box art (Oct 21) | Passed | 8 matcher cases, 2 service tests with a fake thumbnail server (non-PNG and oversized rejected, misses remembered, outage reported), art endpoint access test (64 total). `scripts/artwork-smoke.py` against real thumbnails.libretro.com: fetched and served a 324 KB PNG, unmatched homebrew left without art, toggle respected. iPhone 17 Pro simulator (iOS 26.5) Safari showed the cover in the play page grid |
| Main menu entry (Oct 21) | Passed (admins) | Real Jellyfin listed *Retro Games* (`sports_esports`) in the admin main-menu pages and served the hand-off page. The same endpoint returns 403 to ordinary users in Jellyfin 12.1, so player discovery uses the link from pairing, the Android app, or the dashboard |
| Per-game access (Oct 21) | Passed | 2 API tests: hidden on every path, earlier tickets and queued remote launches cancelled, admin and chosen player unaffected, restore (66 total). Real Jellyfin: restriction saved in XML config, enforced on list/content/ticket, restored. Dashboard Access panel checked in jsdom |
| Artwork uploads (Oct 21) | Passed | API test: admin-only, PNG/JPEG by signature, GIF/SVG/oversized rejected, precedence over fetched art, fetch does not overwrite, removal falls back (67 total). Real Jellyfin: JPEG upload served, GIF 400, oversized 413 (was 500 before the fix), player 403, removal 404 |
| Companion packages (Oct 21) | Passed (macOS, Linux) | Self-contained single-file builds. With .NET hidden from `PATH` on macOS arm64, the packaged binary passed the launcher smoke test, link/autostart registration, real Jellyfin through the real LaunchServices handler and real launchd autostart, and real RetroArch 1.22.2 save 1 → 2. The `linux-arm64` package in plain Ubuntu 24.04 (no .NET) passed real RetroArch 1.18.0 save 1 → 2. CI builds all five RIDs and verifies one per OS |
| Save sync (Oct 21) | Passed | 15 tests (sync decisions, synced names, API revisions/409/per-user/size/visibility; 82 total). Real RetroArch: macOS computers A, A, B, A gave 1, 2, 3, 4; then the packaged Linux companion (no .NET) continued from the server at 4 to 5, 6, 7, 8. Real Jellyfin conflict test: both copies kept, the last played won, the other computer received it, server revision 3 |
| Android save sync (Oct 21) | Passed on emulator | 2 Kotlin tests matching the desktop rules (8 total). `android-smoke.py --play-page`: phone upload on app open, desktop save pulled before launch (40 → 41), phone's 41 uploaded on the next open. Lint caught an API 33-only call and the emulator run caught a missing `ACCESS_NETWORK_STATE` permission; both fixed |
| Save status (Oct 21) | Passed | API test: latest revision per game, paired device name resolved, other users see nothing (82 total). Real Jellyfin: status reported revision 3 after the conflict test |
| Plugin repository (Oct 21) | Passed | `scripts/repository-smoke.py`: a fresh Jellyfin 12.1 container added a locally served release manifest, listed RetroBridge 0.1.0.0 in its catalog, downloaded and MD5-verified the zip, installed it, and loaded it as Active after restart. The release workflow runs the same check before publishing |
| Release v0.1.0 (Oct 22) | Passed | The release workflow gated on all builds and on a repository install of the release assets, then published. Downloaded assets match `SHA256SUMS`. A fresh Jellyfin installed 0.1.0.0 from the **public** `releases/latest/download/manifest.json` and loaded it. The downloaded macOS companion passed verification without .NET. Found while publishing: `latest` ignores pre-releases (fixed by publishing as latest) and the Linux release gate's root-owned files (fixed) |
| Plugin packaging | Passed | ZIP contains `Jellyfin.Plugin.RetroBridge.dll` and `RetroBridge.Domain.dll`; SHA-256 sidecar generated |

Build artifacts and browser harness outputs are generated locally under ignored directories and are not committed. CI is configured to upload plugin and companion build artifacts; this record does not assert that hosted CI has run successfully.

## Limits of this evidence

The HTTP tests use an ASP.NET test host and a substitute identity provider. The browser harness uses substitute `ApiClient` responses without Jellyfin styling. The companion test uses synthetic bytes and a process substitute that writes a marker file. None of these establishes real Jellyfin authentication/plugin loading, actual gameplay, or RetroArch save persistence.

Real Jellyfin and native emulation are verified on macOS arm64 only. The native run uses a wrapper adding `--max-frames` so RetroArch exits unattended; interactive quitting and save states were not exercised. Linux was verified in Docker on arm64 locally and x86_64 in GitHub Actions. Windows native runs remain open. The fixture proves save loading/flushing, not gameplay compatibility.

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
