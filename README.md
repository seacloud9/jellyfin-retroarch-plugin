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
6. Search and filter games. Titles are cleaned from file names (`Super_Mario_Bros_(USA)_[!]` becomes *Super Mario Bros*, region *USA*). **Edit title** overrides a title for everyone; leave it empty to restore the file-name title. Use only games you own or redistributable homebrew.

Scanning requires Jellyfin's `RequiresElevation` policy. Listing, launch tickets, and downloads require an administrator or a selected player. Every selected player sees the whole library unless a game is limited: **Access** on a game restricts it to chosen players. Restricted games behave as missing for everyone else (listing, tickets, downloads, artwork, and queued remote launches). Removing a player or restricting a game takes effect immediately, including for tickets already issued. Game directories must be administrator-controlled and readable by the server. Symbolic links inside roots are rejected. Do not use directories writable by untrusted users: path checks do not provide race-free protection against concurrent filesystem replacement.

Scans hash only new or changed files, identified by size and modification time; **Full rescan** (`POST /RetroBridge/Scan?full=true`) rehashes everything. A new catalog is published only after an error-free scan. Duplicate content on the same platform produces one entry. A failed/cancelled scan keeps the previous catalog. Each published catalog is saved to `<Jellyfin data>/retrobridge/catalog.json` and restored at startup, so restarts need no rescan. Scans run in the background. The dashboard shows progress, and a daily 03:00 scan is scheduled as **Scan RetroBridge game library** under Dashboard → Scheduled Tasks, where you can change triggers and see history. A scan with issues shows as failed there and keeps the previous catalog.

## Install the companion

Download the archive for your computer from the latest CI run's `retrobridge-companion-*` artifacts. Packages exist for `osx-arm64`, `osx-x64`, `win-x64`, `linux-x64`, and `linux-arm64`; check each against its `.sha256`. Extract it anywhere. It contains one self-contained `RetroBridge.Launcher` executable (no .NET needed) and a `QUICKSTART.txt`. To build one yourself: `python3 scripts/package-companion.py --rid <rid>`.

macOS builds are not signed or notarized. If macOS blocks the first run, use `xattr -d com.apple.quarantine RetroBridge.Launcher`. Windows SmartScreen may also warn about an unsigned executable. Keep the executable where you extracted it before running `register` or `autostart on`, because they record its path.

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

**Play from a phone or any browser:** run `RetroBridge.Launcher listen` on the computer, or leave it running. While it runs, that device shows as *(ready)* on the Play page, and Play sends the game straight to it. This works from iPhone, Android, or another computer. The listener checks in every 3 seconds and launches one game at a time. Unpairing the device or removing the player stops it. **`RetroBridge.Launcher autostart on`** starts it at every login, for the current user only: a LaunchAgent on macOS, a systemd user service on Linux (XDG autostart where systemd isn't available), or the `HKCU` Run key on Windows, using a headless console. `autostart off` and `autostart status` turn it off and check it. Background listeners log to `listener.log` in the data directory. Only one listener runs per data directory, and it waits for the network at login.

**Box art:** after each scan, the server fetches box art from the libretro thumbnails project (`thumbnails.libretro.com`) and caches it under `<Jellyfin data>/retrobridge/artwork`. Only the server contacts that site; players load images from Jellyfin. Titles are matched by No-Intro file name first, then by cleaned title with region preference. Unmatched games show a platform tile and are retried weekly or with **Fetch missing artwork**. Administrators can **Upload art** (PNG or JPEG, up to 4 MiB) for any game. An upload replaces fetched art and is never overwritten by later fetches; **Remove uploaded art** goes back to the fetched image. Turn this off in the plugin settings to keep the server from contacting the site.

Then open **`https://jellyfin.example/RetroBridge/Play`** in a browser signed in to Jellyfin. Players find the link after `pair`, behind **Open play page** in the Android app, or from an administrator: the plugin page has a **Copy link** button. Administrators also get a **Retro Games** entry in Jellyfin's main menu. Jellyfin 12.1 shows plugin menu entries only to administrators, so players need the link. Choose the paired computer and press **Play**. The page requests a ticket and opens `retrobridge://launch?ticket=…`. The companion redeems the ticket, verifies and caches the game, and starts RetroArch. Link launches log to `launcher.log` in the data directory.

The link holds only the ticket. Tickets expire after 2 minutes and are consumed by any redemption attempt. Only the same user on the same paired device can redeem a ticket, so a repeated click or a forwarded link does nothing. Tickets live in server memory; a Jellyfin restart invalidates outstanding ones. Simultaneous launches of the same game are refused by a per-game lock.

Other commands: `launch <data-dir> <ticket> <retroarch> <core>` redeems a ticket with explicit paths. The development form `<server-url> <game-id> <retroarch> <core> <data-dir>` takes an existing user token from `RETROBRIDGE_TOKEN`.

The launcher requires HTTPS except for loopback development servers and refuses redirects. It supports Jellyfin base paths, validates cached/downloaded content against SHA-256, limits content to 64 MiB, cleans up interrupted download files, and uses structured process arguments without a shell.

**Save sync:** battery saves (`.srm`, `.sav`, `.rtc`; up to 1 MiB) sync through your Jellyfin server, so progress follows you between computers. The companion downloads a newer server copy before RetroArch starts, if the local save hasn't changed, and uploads changes after RetroArch exits. If both sides changed, the copy just played wins and the other is kept beside it as `<save>.server-r<revision>`. The server also keeps the last 5 versions of each save. Being offline never blocks play; unsynced changes upload at the next launch. Each game card on the play page shows when and where it was last saved ("Saved 5 min ago on Living-room PC"). Save states are not synced, because they depend on the core. The Android app syncs too. It pulls before each launch, and because Android doesn't report when RetroArch exits, it uploads when the app opens, before the next launch, and from a background job about every 15 minutes.

Saves and states live under `RetroBridge-data/saves/<server hash>/<user ID>/<platform>/<content hash>/`. A per-game lock prevents simultaneous launches against the same save directory. The companion waits for RetroArch to exit and returns its exit code. Its appended configuration disables config saving on exit to avoid saving these overrides over your existing RetroArch configuration. Changing cores can make save states incompatible; core-specific compatibility is not yet enforced.

## API

Use Jellyfin authentication (an `Authorization: MediaBrowser ..., Token="..."` header; Jellyfin 12.1 rejects `X-Emby-Token` alone). No credentials belong in query strings. (Exception: Jellyfin's own Quick Connect API polls with its pairing secret in the query string.)

| Method | Route | Access | Behavior |
|---|---|---|---|
| GET | `/RetroBridge/Games?search=...&platform=nes` | Player | Catalog metadata without server paths |
| POST | `/RetroBridge/Scan` | Administrator | Start a background scan (202 with status); `?full=true` rehashes all; `?wait=true` waits and returns count, hashed/reused counts, publication status, and issues; 409 if busy |
| GET | `/RetroBridge/Scan` | Administrator | Scan status: `Running`, `Processed`, `Last` result, `LastCompleted` |
| POST | `/RetroBridge/Games/{id}/LaunchTicket` | Player | Body `{"DeviceId"}`; for a listening device returns `Delivered: true` (no ticket), otherwise a single-use ticket and its expiry |
| POST | `/RetroBridge/Games/{id}/Title` | Administrator | Body `{"Title"}` (≤ 200 chars); empty restores the file-name title |
| POST | `/RetroBridge/Games/{id}/Access` | Administrator | Body `{"PlayerUserIds": [...]}` limits the game to those players; empty restores access for all players |
| GET | `/RetroBridge/Games/{id}/Art` | Player | Cached box art (PNG), 404 if none |
| PUT / DELETE | `/RetroBridge/Games/{id}/Art` | Administrator | Upload a raw PNG/JPEG body (≤ 4 MiB; 400 wrong type, 413 too large) / remove the upload |
| POST | `/RetroBridge/Artwork` | Administrator | Fetch missing art now, retrying misses (202; `?wait=true` returns counts); 409 if turned off |
| GET | `/RetroBridge/Saves` | Player | Latest save per visible game: revision, time, and the saving device's name (or "another device") |
| GET | `/RetroBridge/Games/{id}/Saves` | Player | The caller's server saves for the game (name, SHA-256, size, revision, modified, device) |
| GET / PUT | `/RetroBridge/Games/{id}/Saves/{name}` | Player | Download / upload a battery save; PUT needs `?baseRevision=` (0 for new) and returns 409 with the current copy on conflict |
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
- Descriptions and other metadata.
- Packaged companion installers (it currently runs from a .NET build).
- Signed/notarized installers, a Jellyfin plugin repository manifest, and published releases.

Phones work as remotes for a listening computer. **Android** can also play on the phone itself with the prototype companion in `android/` (build with `./gradlew :app:assembleDebug`). iOS can only act as a remote. Android is verified on the emulator only; a real-phone test is pending ([checklist](docs/manual-smoke.md#pending-real-android-phone-next)). See [mobile support](docs/mobile.md).

See [architecture notes](docs/architecture.md), the [verification record](docs/verification.md), and the [manual smoke checklist](docs/manual-smoke.md). The vertical-slice milestone passed on macOS arm64 on October 21, 2026: a fixture game served by real Jellyfin 12.1.0 launched in RetroArch/Nestopia and its battery save survived a restart. The same test passes on Linux in Docker ([setup and teardown](docs/linux-native-docker.md)). Windows native runs remain open.
