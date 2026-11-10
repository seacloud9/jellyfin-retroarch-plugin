# Third-party software

RetroBridge itself is GPL-2.0-or-later. In-browser play uses the components below (SHA-256 pinned in `BrowserPlayService.cs`). Nostalgist and the Nova64 web core **are included in RetroBridge releases** (see below) so the built-in demos play with no setup. The other cores are not included: when an administrator enables in-browser play, the Jellyfin server downloads those exact files and serves them to that server's players.

| Component | Version | License | Source |
|---|---|---|---|
| Nostalgist (browser wrapper for RetroArch) | 0.23.0 | MIT | https://github.com/arianrhodsandlot/nostalgist (tag `v0.23.0`) |
| RetroArch (Emscripten build, linked into each core file) | 1.22.2 | GPL-3.0 | https://github.com/libretro/RetroArch/tree/v1.22.2 (commit `69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576`) |
| FCEUmm core (NES) | libretro buildbot stable 1.22.2 | GPL-2.0-or-later | https://github.com/libretro/libretro-fceumm |
| mGBA core (Game Boy / Color / Advance) | libretro buildbot stable 1.22.2 | MPL-2.0 | https://github.com/libretro/mgba |
| ClownMDEmu core (Genesis / Mega Drive) | libretro buildbot stable 1.22.2 | AGPL-3.0 | https://github.com/Clownacy/clownmdemu-libretro |
| PCSX ReARMed core (PlayStation) | libretro buildbot stable 1.22.2 | GPL-2.0-or-later | https://github.com/libretro/pcsx_rearmed |
| Packaging of the above | `v1.22.2` (commit `d03a5b0d642ea638d4d8447960e9796a45fb074e`) | (unmodified buildbot files) | https://github.com/arianrhodsandlot/retroarch-emscripten-build |
| Nova64 web core (Nova64 fantasy console) | `v0.5.7` (commit `402dea3e62526dbb50e0565bd4c8772afe391aae`), `public/retroarch-web/nova64_libretro.{js,wasm}` | MIT (Nova64), linked with RetroArch 1.21.0 (GPL-3.0) | https://github.com/seacloud9/nova64 (core source: `retroarch/nova64_libretro.c`; build steps: `retroarch/WASM_CORE.md`; RetroArch: https://github.com/libretro/RetroArch/tree/v1.21.0) |

The core files are unmodified builds from https://buildbot.libretro.com/stable/1.22.2/emscripten/RetroArch.7z. Build recipes are in https://github.com/libretro/libretro-super.

**For administrators:** serving these files to players is distributing them, so GPL-3.0 applies to the RetroArch builds of every core, and ClownMDEmu's AGPL-3.0 also asks that players can get its source. GPL-3.0 explicitly allows combining it with AGPL-3.0. The play page links here, and this page links the corresponding source. Keep that link reachable if you modify or self-host the files. This is an engineering summary, not legal advice.

Some cores are intentionally not used for in-browser play because their licenses cannot be combined with GPL-3.0 RetroArch:

- Gambatte (GPL-2.0-only): Game Boy uses mGBA instead.
- Snes9x and Genesis Plus GX (non-commercial licenses): there is no compatible SNES core in the pinned build, so SNES is native only. Genesis uses ClownMDEmu.
- The pinned build has no Nintendo 64 core, so N64 is native only.

Native RetroArch on desktop and Android is unaffected; players can use any core there.

Native play uses RetroArch and cores that the player installs separately; RetroBridge does not distribute them.

## Included in RetroBridge releases

The plugin DLL embeds three upstream files, unmodified and byte-identical to their pinned checksums (`src/Jellyfin.Plugin.RetroBridge/Web/`):

- `nostalgist.umd.js` (Nostalgist 0.23.0, MIT): `licenses/LICENSE-Nostalgist-MIT.txt` in the plugin zip.
- `nova64_libretro.js` and `nova64_libretro.wasm` (Nova64 v0.5.7, MIT, linked with RetroArch 1.21.0, GPL-3.0): `licenses/LICENSE-Nova64-MIT.txt` and `licenses/LICENSE-RetroArch-GPL-3.0.txt` in the plugin zip. Corresponding source: the Nova64 and RetroArch links in the table above.

The Nova64 core is a separate program that RetroBridge serves to browsers; it is shipped alongside the GPL-2.0-or-later plugin code as an aggregate, not linked with it. Anyone redistributing the plugin zip must keep the license files and the source links. This is an engineering summary, not legal advice.

The plugin also contains eight Nova64 demo carts (`src/Jellyfin.Plugin.RetroBridge/Demos/*.nova`), copied unmodified from https://github.com/seacloud9/nova64 at tag `v0.5.7` (`examples/<name>/<name>.nova`). They are MIT-licensed; the license text is in `Demos/LICENSE.txt`.
