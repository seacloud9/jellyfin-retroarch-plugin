# Third-party software

RetroBridge itself is GPL-2.0-or-later. Optional in-browser play uses the components below. They are **not** included in RetroBridge releases. When an administrator enables in-browser play, the Jellyfin server downloads these exact files (SHA-256 pinned in `BrowserPlayService.cs`) and serves them to that server's players.

| Component | Version | License | Source |
|---|---|---|---|
| Nostalgist (browser wrapper for RetroArch) | 0.23.0 | MIT | https://github.com/arianrhodsandlot/nostalgist (tag `v0.23.0`) |
| RetroArch (Emscripten build, linked into each core file) | 1.22.2 | GPL-3.0 | https://github.com/libretro/RetroArch/tree/v1.22.2 (commit `69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576`) |
| FCEUmm core (NES) | libretro buildbot stable 1.22.2 | GPL-2.0-or-later | https://github.com/libretro/libretro-fceumm |
| mGBA core (Game Boy / Color) | libretro buildbot stable 1.22.2 | MPL-2.0 | https://github.com/libretro/mgba |
| Packaging of the above | `v1.22.2` (commit `d03a5b0d642ea638d4d8447960e9796a45fb074e`) | (unmodified buildbot files) | https://github.com/arianrhodsandlot/retroarch-emscripten-build |

The core files are unmodified builds from https://buildbot.libretro.com/stable/1.22.2/emscripten/RetroArch.7z. Build recipes are in https://github.com/libretro/libretro-super.

**For administrators:** serving these files to players is distributing them, so GPL-3.0 applies to the RetroArch/FCEUmm builds. The play page links here, and this page links the corresponding source. Keep that link reachable if you modify or self-host the files. This is an engineering summary, not legal advice.

Gambatte (GPL-2.0-only) is intentionally not used for in-browser play because it cannot be combined with GPL-3.0 RetroArch. Native RetroArch on desktop and Android is unaffected.

Native play uses RetroArch and cores that the player installs separately; RetroBridge does not distribute them.
