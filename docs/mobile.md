# Mobile support (iOS and Android)

Findings recorded on October 21, 2026. Environment: iPhone 17 Pro simulator (iOS 26.5), Android 17 emulator (Medium_Phone, arm64), RetroArch 1.22.2 nightly (`com.retroarch.aarch64`), and the official Nestopia Android core.

## What works today

**Phones as remotes.** The Play page works in iOS Safari and Android Chrome. It uses the phone's Jellyfin session, lists paired computers, and fits the screen. When a paired computer runs `RetroBridge.Launcher listen`, pressing Play on the phone starts the game on that computer. Verified with a real tap in Android Chrome; the Mac listener launched the game. Without a listener, phones show instructions instead of opening a dead link.

## iOS: playing on the phone is not possible with this design

- iOS does not let an app start another app's process, and RetroArch for iOS documents no URL scheme or intent that loads content.
- `retrobridge://` has no handler on iOS (`simctl openurl` fails with LaunchServices error 115).
- The only route that would work is emulation inside the browser (a WebAssembly libretro core on the Play page). Saves would then live in browser storage unless synced to the server. Not started.

## Android: companion app (prototype)

`android/` contains a small Kotlin app with no AndroidX dependencies:

- **Pairing:** enter the server URL and use Quick Connect. The device-bound token is stored in app-private preferences. HTTPS is required, with HTTP allowed only to loopback (for example `adb reverse`), the same rule as desktop.
- **Launch:** an intent filter for `retrobridge://launch` (`LaunchActivity`, no UI) applies the same strict link rules as desktop. It redeems the ticket with the phone's own token, downloads to `/sdcard/RetroBridge/cache/<game id>.<ext>`, verifies size and SHA-256, and starts `com.retroarch.browser.retroactivity.RetroActivityFuture` with `ROM`, `LIBRETRO`, and `QUITFOCUS=true`.
- **Save protection:** before each launch, every RetroArch save named after the game ID is copied to `/sdcard/RetroBridge/backups/<game id>/` (last 5 kept). A save found empty is restored from the newest non-empty backup.
- **Play page:** on Android, Play opens the link even when no computer is listening. iOS still needs a listening computer.

Setup on a phone: install RetroArch, open it once, install the cores (NES: Nestopia; GB/GBC: Gambatte) from its Online Updater, install the RetroBridge APK, allow **All files access**, then pair.

### Verified on the Android 17 emulator (arm64, 16 KB pages)

`scripts/android-smoke.py --play-page` against an isolated real Jellyfin 12.1 container:

| Check | Result |
|---|---|
| Quick Connect pairing; device appears in `/RetroBridge/Devices` | ✅ |
| Ticket launch via a real `retrobridge://` intent; fixture save counter 1 → 2 | ✅ |
| Save truncated to 0 bytes → restored from backup on next launch | ✅ |
| Replayed ticket and a link with an injected `&server=` rejected; RetroArch not started | ✅ |
| Real tap on Play in Android Chrome → app → RetroArch boots, save increments | ✅ |

### Save reliability investigation

A 0-byte save appeared once during the first spike. It did not recur in 49 later cycles across five scenarios:

| Scenario | Cycles | Result |
|---|---|---|
| Launch, exit (Home with QUITFOCUS), read after process exit | 23 | Every save 8192 bytes, counter +1 each time |
| Reading the save repeatedly during exit | 5 | Intact |
| Relaunching immediately during exit | 5 | Intact. The relaunch resumes the exiting task instead of booting again |
| Force-stop 0–1.2 s after leaving | 8 | Intact. QUITFOCUS writes immediately on focus loss |
| Force-stop mid-game (crash/swipe-away) | 3 | File intact; that session's progress lost (expected) |

MediaProvider's `Database update failed while renaming …srm.tmp` warning appeared on every write. It concerns MediaProvider's index, not the file. The original truncation is unexplained, which is why the backup/restore exists.

### Limits

- `MANAGE_EXTERNAL_STORAGE` (All files access) is required because RetroArch only reads shared storage and the backups read RetroArch's save folder. Google Play restricts this permission, so distribution would be by APK or would need a different storage design.
- Saves use RetroArch's own folder. They are not separated by server and user as on desktop, because Android has no equivalent of `--appendconfig`.
- Core file names are assumed (`nestopia_libretro_android.so`, `gambatte_libretro_android.so`) and must be installed in RetroArch.
- A phone running the app does not listen for remote launches. Launch from the phone's own browser.
- Only the emulator has been tested. No real phone yet.

## Suggested next steps

1. Test on a real Android phone (Google Play RetroArch is `com.retroarch`).
2. Detect installed cores via RetroArch's `QUERY_INSTALLED_CORES` broadcast instead of assuming names.
3. For iOS, prototype in-browser emulation separately.
