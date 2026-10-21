# RetroBridge

An early Jellyfin plugin and native RetroArch companion prototype, based on the [development plan](jellyfin-retroarch-plugin-plan.md).

**Target: Jellyfin 12.1.0, .NET SDK 10.0.401. This is not a release-ready plugin.**

The plugin provides an administrator dashboard for configuring game folders, scanning, searching, and filtering NES (`.nes`), Game Boy (`.gb`), and Game Boy Color (`.gbc`) content. Its authenticated API supplies games to a command-line companion, which verifies downloads, caches content, starts a locally selected RetroArch/core, and separates save directories by server, user, platform, and content checksum.

## Build and test

Install the SDK specified in `global.json`, then:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
python3 scripts/launcher-smoke.py  # macOS/Linux, process substitute
python3 scripts/package-plugin.py
```

The archive is written to `artifacts/RetroBridge-0.1.0.zip`. CI runs .NET tests/builds on macOS, Windows, and Linux; the process-substitute smoke test currently runs on macOS/Linux.

## Install the prototype

1. Use a **test Jellyfin 12.1.0 server**. Loading in a real server has not yet been verified in this repository.
2. Stop Jellyfin. Create a `RetroBridge` directory under its configured plugins directory.
3. Extract **both DLLs** from the archive into that directory, then restart Jellyfin.
4. As an administrator, open Dashboard → Plugins → RetroBridge.
5. Enter absolute game-folder paths **as seen by the Jellyfin server**, save, then scan. Container paths must refer to mounted folders.
6. Search/filter games and copy the ID of a game you own or a redistributable homebrew game.

All endpoints require Jellyfin's `RequiresElevation` policy. Ordinary user access is intentionally unavailable until explicit library permissions are implemented. Game directories must be administrator-controlled and readable by the server. Symbolic links inside roots are rejected. Do not use directories writable by untrusted users: path checks do not provide race-free protection against concurrent filesystem replacement.

Scans rehash supported files and publish a new in-memory catalog only after an error-free scan. Duplicate content on the same platform produces one entry. A failed/cancelled scan keeps the previous catalog. Each published catalog is saved to `<Jellyfin data>/retrobridge/catalog.json` and restored at startup, so restarts need no rescan. Rescan after changing game files. Large libraries and incremental scans are later work.

## Run the companion

Install RetroArch and a suitable core separately. Use a Jellyfin **administrator user access token** in `RETROBRIDGE_TOKEN`; server-wide API keys do not provide the user identity required for saves. Keep the token in your local environment; do not place it in command arguments, URLs, or checked-in files. Interactive login/device pairing is not implemented yet.

```sh
dotnet run --project src/RetroBridge.Launcher -c Release -- \
  'https://jellyfin.example/' \
  'nes-COPY_THE_ACTUAL_CHECKSUM_FROM_THE_DASHBOARD' \
  '/Applications/RetroArch.app/Contents/MacOS/RetroArch' \
  '/absolute/path/to/nestopia_libretro.dylib' \
  '/absolute/path/to/RetroBridge-data'
```

Replace the example paths with your executable/core paths. The launcher requires HTTPS except for loopback development servers and refuses redirects. It supports Jellyfin base paths, validates cached/downloaded content against SHA-256, limits content to 64 MiB, cleans up interrupted download files, and uses structured process arguments without a shell. Only the locally supplied executable and core paths are used.

Saves and states live under `RetroBridge-data/saves/<server hash>/<user ID>/<platform>/<content hash>/`. A per-game lock prevents simultaneous launches against the same save directory. The companion waits for RetroArch to exit and returns its exit code. Its appended configuration disables config saving on exit to avoid saving these overrides over your existing RetroArch configuration. Changing cores can make save states incompatible; core-specific compatibility is not yet enforced.

## API

Use Jellyfin authentication (an `Authorization: MediaBrowser ..., Token="..."` header; Jellyfin 12.1 rejects `X-Emby-Token` alone). No credentials belong in query strings.

| Method | Route | Behavior |
|---|---|---|
| GET | `/RetroBridge/Games?search=...&platform=nes` | Catalog metadata without server paths |
| POST | `/RetroBridge/Scan` | Scan saved folders; returns count, publication status, and issues; 409 if busy |
| GET | `/RetroBridge/Games/{id}/Content` | Download an indexed file from a currently configured root |

Scan errors include paths for administrator diagnostics. Content changes after scanning are rejected by the companion's checksum check; rescan to update the catalog.

## Verification and remaining work

Implemented checks cover indexing, stable identities, duplicates, cancellation, symlinks/traversal, authorization boundaries in an ASP.NET test host, filtering, argument safety, save-directory isolation, cache repair, and corrupt downloads. Synthetic fixtures contain arbitrary bytes and are **not playable ROMs**. The CLI smoke test's process substitute writes a marker into the save directory; it does not prove emulator save persistence.

A local dashboard harness can be generated with `python3 scripts/dashboard-harness.py`; open `artifacts/dashboard-harness.html`. This uses substitute API data and does not test Jellyfin's styling or authentication.

Still required before a usable beta:

- Plugin upgrade tests and a visual check of the dashboard inside Jellyfin.
- Native RetroArch save/restart tests on Windows.
- Incremental/background scans, metadata/artwork editing.
- Per-user game access, device pairing, short-lived single-use launch tickets, and one-click play.
- Dedicated game UI, installers, repository manifest, and release verification.

See [architecture notes](docs/architecture.md), the [verification record](docs/verification.md), and the [manual smoke checklist](docs/manual-smoke.md). The vertical-slice milestone passed on macOS arm64 on October 21, 2026: a fixture game served by real Jellyfin 12.1.0 launched in RetroArch/Nestopia and its battery save survived a restart. The same test passes on Linux in Docker ([setup and teardown](docs/linux-native-docker.md)). Windows native runs remain open.
