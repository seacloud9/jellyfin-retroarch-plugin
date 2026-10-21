# Mobile support (iOS and Android)

Findings recorded on October 21, 2026. Environment: iPhone 17 Pro simulator (iOS 26.5), Android 17 emulator (Medium_Phone, arm64), RetroArch 1.22.2 nightly (`com.retroarch.aarch64`), and the official Nestopia Android core.

## What works today

**Phones as remotes.** The Play page works in iOS Safari and Android Chrome. It uses the phone's Jellyfin session, lists paired computers, and fits the screen. When a paired computer runs `RetroBridge.Launcher listen`, pressing Play on the phone starts the game on that computer. Verified with a real tap in Android Chrome; the Mac listener launched the game. Without a listener, phones show instructions instead of opening a dead link.

## iOS: playing on the phone is not possible with this design

- iOS does not let an app start another app's process, and RetroArch for iOS documents no URL scheme or intent that loads content.
- `retrobridge://` has no handler on iOS (`simctl openurl` fails with LaunchServices error 115).
- The only route that would work is emulation inside the browser (a WebAssembly libretro core on the Play page). Saves would then live in browser storage unless synced to the server. Not started.

## Android: feasible, with one blocking risk

What was proven on the emulator using adb in place of a companion app:

| Step | Result |
|---|---|
| Install a core without root or UI | ✅ `com.retroarch.browser.debug.CoreSideloadActivity` (exported) with extras `LIBRETRO=<core .so>` and `ROM=<content>` copies the core into RetroArch's private `cores/` folder and starts the game. RetroArch must have been opened once first (first-run setup). |
| Launch content from another app | ✅ `com.retroarch.browser.retroactivity.RetroActivityFuture` (exported) with `ROM`, `LIBRETRO=/data/user/0/com.retroarch.aarch64/cores/nestopia_libretro_android.so`, and `QUITFOCUS=true` auto-starts the game. The fixture showed its first-boot color. |
| Clean exit | ✅ With `QUITFOCUS`, pressing Home exits RetroArch and flushes SRAM |
| Save persistence | ⚠️ The counter went 1 → 2 → 3 across launches, then the `.srm` was **0 bytes** after the next exit |

**Blocking risk: save truncation in shared storage.** RetroArch writes saves to `/storage/emulated/0/RetroArch/saves/` and renames a `.tmp` file into place. Android's MediaProvider logged `Database update failed while renaming …save-counter.srm.tmp` on each write, and one exit left a 0-byte save. A concurrent `adb cat` may have raced the write, but data loss cannot be ruled out. Before an Android companion is built, it needs:

1. A reproducible test that runs many launch/exit cycles without outside reads, to confirm or clear the truncation.
2. A save location that avoids shared-storage renames. One option is a CONFIGFILE that points `savefile_directory` at RetroArch's app-specific folder. That replaces the user's whole RetroArch config, unlike the desktop `--appendconfig`, so it needs care.
3. A save backup before each launch, with restore when the save shrinks to 0 bytes. This is only possible if the companion can read the save folder, which may need storage permissions that Google Play restricts.

Other Android notes:

- Content must be in shared storage (RetroArch was granted all-files access here). RetroArch cannot read another app's private files, so the companion would have to place ROMs in shared storage or a location RetroArch can read.
- Per-user/per-server save isolation (as on desktop) is not available without a custom CONFIGFILE.
- On 16 KB page-size devices (Android 17 emulator), RetroArch's native library runs in compatibility mode and shows a one-time warning.
- Cores come from RetroArch's Online Updater on real devices; the sideload activity is a debug entry point and may change.

## Suggested next steps

1. Run the save truncation stress test on the Android emulator and on a real device.
2. If saves can be made reliable, build a small Kotlin companion: Quick Connect pairing, `retrobridge://` intent filter and listener, verified download, `RetroActivityFuture` launch.
3. For iOS, prototype in-browser emulation separately.
