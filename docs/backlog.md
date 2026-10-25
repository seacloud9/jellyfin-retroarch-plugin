# Backlog

## In-browser play (WASM) — experimental option (implemented, Oct 22)

**Goal:** play from the play page with no install, the only route to play *on* iOS. It is an addition to native RetroArch, not a replacement.

**Shape**
- Admin setting **In-browser play (experimental)**, off by default, with a per-platform allow-list (NES, GB, GBC first). Memory is not a concern for these systems (a few MB per core and game); revisit for larger systems (N64, PS1).
- Play page: a **Play in browser** button beside Play. It becomes the main action on iOS or when no device is paired.
- Emulator cores bundled with the plugin and served by Jellyfin, never a third-party CDN, to keep the privacy model.
- Saves use the existing save sync endpoints, so progress carries between browser, desktop, and Android. Flush on `visibilitychange` (iOS suspends background tabs).
- Play page CSP widened only as needed (`wasm-unsafe-eval`, workers, blob media).

**Spike first (proof of concept)**
1. One NES core running a game served by Jellyfin in iOS Safari and Android Chrome.
2. Measure memory, audio (needs a tap on iOS), touch controls, and gamepad.
3. Save round trip through `/RetroBridge/Games/{id}/Saves`.
4. Check every core's license against GPL-2.0-or-later before bundling.

**Spike results (Oct 22)** — `spikes/wasm/` (run `python3 spikes/wasm/setup.py`):

| Check | iOS 26.5 Safari (simulator) | Android 17 Chrome (emulator) |
|---|---|---|
| Nostalgist 0.23.0 + RetroArch Emscripten FCEUmm core, served from our own origin | ✅ boots in 1.2 s cold, 0.3 s cached | ⚠️ core loads (≈0.5 s), but rendering stalls under both emulator GPU modes (ANGLE shader-link failures, then a frozen frame) |
| Battery save round trip: SRAM exported, stored, loaded into the next boot | ✅ counter 1 → 2 across reloads | ❓ inconclusive; needs a real phone |
| Memory | 128 MiB WASM heap reserved by the RetroArch build (the game needs far less); no problems on iOS | — |
| Download size | 3.1 MB core + 0.26 MB JS + 0.11 MB Nostalgist per platform | same |

**Findings to act on**
- Core builds must match the Nostalgist version: cores from the repository's `main` failed (`getEmscripten is not a function`); the `v1.22.2` tag pinned by Nostalgist 0.23.0 works. Pin both together.
- Pass absolute URLs for self-hosted cores.
- The 128 MiB heap is fixed by the RetroArch build. That is the number to watch for low-memory devices and larger systems, not the game size.
- **Licensing:** the Emscripten build links RetroArch (GPL-3.0) into each core, so bundled browser cores ship under GPL-3.0. That is compatible with our GPL-2.0-or-later, but every core must be compatible too. Audit from the license files and source headers (an engineering check, not legal advice):

  | Core | License | Bundle in the WASM build? |
  |---|---|---|
  | FCEUmm (NES) | GPL-2.0-or-later | ✅ |
  | Nestopia (NES) | GPL-2.0-or-later | ✅ |
  | QuickNES (NES) | GPL-2.0-or-later | ✅ |
  | mGBA (GB/GBC) | MPL-2.0 | ✅ (MPL-2.0 allows combination with GPL) |
  | SameBoy (GB/GBC) | MIT | ✅ |
  | Gambatte (GB/GBC) | GPL-2.0-**only** | ❌ cannot be combined with GPL-3.0 RetroArch. Use mGBA or SameBoy in the browser; native Gambatte is unaffected |

  Distribute the corresponding source (or a written offer) for RetroArch and each bundled core with the plugin.
- Audio, touch controls, and gamepad were not exercised in the simulator (audio needs a tap on iOS).

**Implemented (Oct 22)**
- Admin toggle, per-platform allow-list, and **Download emulator files** (pinned SHA-256, server-side). *Play in browser* with touch and keyboard controls. Saves through the server save API. CSP widened only when enabled. `#play=` deep links.
- Game Boy confirmed with an original MBC1 save-counter fixture on mGBA. SameBoy has no build in the pinned repository; mGBA is the Game Boy core.
- Verified in iOS 26.5 Safari (simulator) for NES and Game Boy, with saves reaching the server across two sessions each.

**Still open**
1. A real Android phone, to confirm display. Findings on the Android 17 emulator (Oct 24): with the host GPU (`-gpu host`), Chrome ran the FCEUmm core and the save round trip worked (counter 3 → 4 → 5 → 6 → 7 across launches), but the canvas stayed black, including with `video_shader_enable=false`, the font overlay off, and the menu off. A minimal page (`spikes/wasm/webgl-test.html`) that only clears WebGL1/WebGL2 canvases also shows nothing while both contexts report "ok". The emulator does not composite **any** WebGL output, so this is an emulator display limitation, not a RetroArch or RetroBridge problem. Spike option: `?cfg=<json>` overrides RetroArch settings.
2. Real-device feel: audio, latency, gamepads, rotation, background behavior (see manual-smoke.md).
3. ~~Source availability~~ done: [third-party.md](third-party.md) lists exact sources; the play page links it while in-browser play is on.
4. Save states in the browser are not synced (battery saves only, as with the companions).

## Other items

- Code signing and notarization for the macOS and Windows companions (needs developer certificates).
- Release-signed Android APK (needs a signing key in repository secrets).
- Submission to Jellyfin's official plugin catalog.
- Real-device checks in [manual-smoke.md](manual-smoke.md): Android phone, Windows native RetroArch, dashboard in real jellyfin-web.
