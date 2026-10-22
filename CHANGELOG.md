# Changelog

Each release's section becomes "What's new" in its GitHub release notes and the changelog in Jellyfin's plugin catalog. `scripts/build-release.py` requires a section for the version being released.

## [Unreleased]
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
