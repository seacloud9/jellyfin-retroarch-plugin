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

1. Use a **test Jellyfin 12.1.0 server**. Loading is verified in Docker (see the [verification record](docs/verification.md)); this is still a prototype.
2. Stop Jellyfin. Create a `RetroBridge` directory under its configured plugins directory.
3. Extract **both DLLs** from the archive into that directory, then restart Jellyfin.
4. As an administrator, open Dashboard → Plugins → RetroBridge.
5. Enter absolute game-folder paths **as seen by the Jellyfin server**. Tick the non-administrator users who may play, save, then scan. Container paths must refer to mounted folders.
6. Search and filter games. Use only games you own or redistributable homebrew.

Scanning requires Jellyfin's `RequiresElevation` policy. Listing, launch tickets, and downloads require an administrator or a selected player. Access applies to the whole library; per-game permissions are not implemented. Removing a player takes effect immediately. Game directories must be administrator-controlled and readable by the server. Symbolic links inside roots are rejected. Do not use directories writable by untrusted users: path checks do not provide race-free protection against concurrent filesystem replacement.

Scans rehash supported files and publish a new in-memory catalog only after an error-free scan. Duplicate content on the same platform produces one entry. A failed/cancelled scan keeps the previous catalog. Each published catalog is saved to `<Jellyfin data>/retrobridge/catalog.json` and restored at startup, so restarts need no rescan. Rescan after changing game files. Large libraries and incremental scans are later work.

## Play (one click)

Install RetroArch and the cores you need separately. On each player's computer, run these once. `dotnet run --project src/RetroBridge.Launcher -c Release --` works in place of `RetroBridge.Launcher`:

```sh
RetroBridge.Launcher pair 'https://jellyfin.example/'
RetroBridge.Launcher configure --retroarch '/Applications/RetroArch.app/Contents/MacOS/RetroArch' \
  --core nes='/absolute/path/to/nestopia_libretro.dylib' --core gb='/absolute/path/to/gambatte_libretro.dylib'
RetroBridge.Launcher register
```

- **pair** uses Jellyfin Quick Connect (enable it under Dashboard → General). The companion prints a code. Sign in to Jellyfin as the player, open your profile → Quick Connect, and enter the code. No password is entered in the companion. The device-bound token is stored in `pairing.json` (owner-only on macOS/Linux). Revoke it under Dashboard → Devices.
- **configure** records the RetroArch executable and a core per platform (`nes`, `gb`, `gbc`). Launch links can never choose programs or cores.
- **register** makes this companion the current user's `retrobridge://` handler: an applet in `~/Applications` (macOS), a `.desktop` entry plus `xdg-mime` (Linux), or `HKCU\Software\Classes` (Windows). `unregister` removes it.

Data defaults to `~/Library/Application Support/RetroBridge`, `~/.local/share/RetroBridge`, or `%LOCALAPPDATA%\RetroBridge`. Pass `--data <dir>` to any command to change it.

**Play from a phone or any browser:** run `RetroBridge.Launcher listen` on the computer, or leave it running. While it runs, that device shows as *(ready)* on the Play page, and Play sends the game straight to it. This works from iPhone, Android, or another computer. The listener checks in every 3 seconds and launches one game at a time. Unpairing the device or removing the player stops it. Starting it automatically at login is not set up yet.

Then open **`https://jellyfin.example/RetroBridge/Play`** in a browser signed in to Jellyfin (administrators find a link on the plugin page). Choose the paired computer and press **Play**. The page requests a ticket and opens `retrobridge://launch?ticket=…`. The companion redeems the ticket, verifies and caches the game, and starts RetroArch. Link launches log to `launcher.log` in the data directory.

The link holds only the ticket. Tickets expire after 2 minutes and are consumed by any redemption attempt. Only the same user on the same paired device can redeem a ticket, so a repeated click or a forwarded link does nothing. Tickets live in server memory; a Jellyfin restart invalidates outstanding ones. Simultaneous launches of the same game are refused by a per-game lock.

Other commands: `launch <data-dir> <ticket> <retroarch> <core>` redeems a ticket with explicit paths. The development form `<server-url> <game-id> <retroarch> <core> <data-dir>` takes an existing user token from `RETROBRIDGE_TOKEN`.

The launcher requires HTTPS except for loopback development servers and refuses redirects. It supports Jellyfin base paths, validates cached/downloaded content against SHA-256, limits content to 64 MiB, cleans up interrupted download files, and uses structured process arguments without a shell.

Saves and states live under `RetroBridge-data/saves/<server hash>/<user ID>/<platform>/<content hash>/`. A per-game lock prevents simultaneous launches against the same save directory. The companion waits for RetroArch to exit and returns its exit code. Its appended configuration disables config saving on exit to avoid saving these overrides over your existing RetroArch configuration. Changing cores can make save states incompatible; core-specific compatibility is not yet enforced.

## API

Use Jellyfin authentication (an `Authorization: MediaBrowser ..., Token="..."` header; Jellyfin 12.1 rejects `X-Emby-Token` alone). No credentials belong in query strings. (Exception: Jellyfin's own Quick Connect API polls with its pairing secret in the query string.)

| Method | Route | Access | Behavior |
|---|---|---|---|
| GET | `/RetroBridge/Games?search=...&platform=nes` | Player | Catalog metadata without server paths |
| POST | `/RetroBridge/Scan` | Administrator | Scan saved folders; returns count, publication status, and issues; 409 if busy |
| POST | `/RetroBridge/Games/{id}/LaunchTicket` | Player | Body `{"DeviceId"}`; for a listening device returns `Delivered: true` (no ticket), otherwise a single-use ticket and its expiry |
| GET | `/RetroBridge/Devices` | Player | The caller's paired companion devices (ID, name, last activity; never tokens) |
| GET | `/RetroBridge/Play` | Anonymous (static) | Player page; uses the browser's Jellyfin session; strict CSP |
| POST | `/RetroBridge/LaunchTickets/Claim` | Player, as the device | Check in as listening and receive the oldest pending launch for this device (204 when none) |
| POST | `/RetroBridge/LaunchTickets/Redeem` | Player, from the bound device | Body `{"Ticket"}`; returns the game; 404 for any invalid, expired, replayed, or foreign ticket |
| GET | `/RetroBridge/Games/{id}/Content` | Player | Download an indexed file from a currently configured root |

Scan errors include paths for administrator diagnostics. Content changes after scanning are rejected by the companion's checksum check; rescan to update the catalog.

## Verification and remaining work

Implemented checks cover indexing, stable identities, duplicates, cancellation, symlinks/traversal, authorization boundaries in an ASP.NET test host, filtering, argument safety, save-directory isolation, cache repair, and corrupt downloads. Synthetic fixtures contain arbitrary bytes and are **not playable ROMs**. The CLI smoke test's process substitute writes a marker into the save directory; it does not prove emulator save persistence.

A local dashboard harness can be generated with `python3 scripts/dashboard-harness.py`; open `artifacts/dashboard-harness.html`. This uses substitute API data and does not test Jellyfin's styling or authentication.

Still required before a usable beta:

- Plugin upgrade tests and a visual check of the dashboard inside Jellyfin.
- Native RetroArch save/restart tests on Windows.
- Incremental/background scans, metadata/artwork editing.
- Per-game permissions, artwork, and a play page integrated into Jellyfin's own navigation.
- Packaged companion installers (it currently runs from a .NET build).
- Dedicated game UI, installers, repository manifest, and release verification.

See [architecture notes](docs/architecture.md), the [verification record](docs/verification.md), and the [manual smoke checklist](docs/manual-smoke.md). The vertical-slice milestone passed on macOS arm64 on October 21, 2026: a fixture game served by real Jellyfin 12.1.0 launched in RetroArch/Nestopia and its battery save survived a restart. The same test passes on Linux in Docker ([setup and teardown](docs/linux-native-docker.md)). Windows native runs remain open.
