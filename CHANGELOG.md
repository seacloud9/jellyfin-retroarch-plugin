# Changelog

Each release's section becomes "What's new" in its GitHub release notes and the changelog in Jellyfin's plugin catalog. `scripts/build-release.py` requires a section for the version being released.

## [Unreleased]
- **Requires Jellyfin 12.4.** Built and tested against Jellyfin 12.4.0 (was 12.1.0).
- **The built-in Nova64 demos are always included and play with no setup.** Every installation lists the eight demo games from the first start, before any scan, and they play in the browser right away: the Nova64 core and its loader now ship inside the plugin (the zip grows by about 2 MB), so nothing has to be enabled or downloaded. Game folders add your own games alongside them. The *Include the built-in Nova64 demo games* setting is gone, and Nova64 no longer appears in the in-browser play platform list because it is always on.
- In-browser play for the other systems is unchanged: still off by default, and still downloaded with **Download emulator files** (now about 12 MB).

## [0.3.0] - 2026-11-07
- **New systems:** SNES, Nintendo 64, Game Boy Advance, Genesis / Mega Drive, and PlayStation (CHD or PBP discs up to 2 GiB) in the catalog, the desktop companion, and the Android app. The Android app picks the best installed core for each system.
- **In-browser play** adds Game Boy Advance (mGBA), Genesis / Mega Drive (ClownMDEmu), and PlayStation (PCSX ReARMed, off by default, discs up to 512 MiB), with touch buttons that match each system. SNES and Nintendo 64 stay native only: no browser core for them has a license that allows serving it. Administrators press **Download emulator files** again to add the new cores; systems already downloaded keep working meanwhile.
- **Nova64** (`.nova` carts) in the catalog, the desktop companion, the Android app, and in-browser play. The server downloads Nova64's own WebAssembly core (pinned to Nova64 v0.5.7); press **Download emulator files** again to add it. Nova64 carts have no battery saves, so nothing is synced for them.
- **Built-in demos:** eight open-source Nova64 demo games (Hello 3D, Demoscene, Space Shooter, Neon Pinball, Neon Snake, Nova Drift, Platformer, Dungeon Crawler) ship with the plugin and appear after the next scan. Turn off *Include the built-in Nova64 demo games* to hide them.
- `RetroBridge.Launcher doctor` checks only the systems you configured and lists the others.

## [0.2.1] - 2026-10-22
- `RetroBridge.Launcher doctor` checks pairing, the server and plugin, RetroArch, cores, the link handler, autostart, and the listener, with a fix for each problem.
- The play page has a Diagnostics section with a token-free **Copy report**. The Android app shows its last problem and version.
- The pairing token is now readable only by the current user on Windows too (it already was on macOS and Linux).
- The play page links the licenses and source of the in-browser emulators ([third-party software](docs/third-party.md)).
- In-browser play: the screen stays awake while playing; phones held sideways get controls on both sides of the game; a Fullscreen button where the browser supports it; a sound hint on iOS when a game opens from a link; "Controller connected" feedback.
- Fixed: **Save and exit** could skip the final save if an automatic save was still uploading. Saves now run one at a time and exit waits for them.

## [0.2.0] - 2026-10-22
- **In-browser play (experimental, off by default):** admins enable it per platform. The server fetches pinned, checksum-verified WebAssembly emulators (FCEUmm for NES, mGBA for Game Boy/Color) and serves them itself. Players get **Play in browser** with touch controls, including on iPhone/iPad, and saves sync with the server.
- Fixed a build setting that would have made the plugin report the wrong version, breaking catalog updates.
- Releases are gated on upgrading from the previous public release with settings and catalog intact.

## [0.1.0] - 2026-10-22
- First prototype release: game catalog with box art, titles, and per-game access; one-click play through the desktop companion and Android app; remote launch from phones; save sync between devices; Jellyfin plugin repository.
