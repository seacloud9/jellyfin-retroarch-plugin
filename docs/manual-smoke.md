# Manual acceptance checklist

Automated real-server tests now cover most integration checks (see the [verification record](verification.md)). This list holds what still needs a person, real hardware, or a platform the automation can't reach. Do not mark these complete from emulator, substitute-process, or jsdom results.

## Pending: real Android phone (next)

Everything below passed on the Android 17 arm64 emulator (`scripts/android-smoke.py --play-page`) but has **not** been tried on a physical phone.

Setup:

1. Install RetroArch from Google Play (`com.retroarch`) or the libretro nightly APK. Open it once.
2. In RetroArch → Online Updater → Core Downloader, install **Nestopia** (NES) and **Gambatte** (GB/GBC).
3. Install the RetroBridge APK. Download the `retrobridge-android` artifact from the latest CI run, then `adb install app-debug.apk` or open it on the phone.
4. Open RetroBridge, tap **Allow all files access**, enter the Jellyfin server's **HTTPS** URL, and pair with Quick Connect.

Checks (record phone model, Android version, RetroArch version and source, and core versions):

- [ ] Pairing completes and the phone appears under Dashboard → Devices and in the play page's *Play on* list.
- [ ] In the phone's browser, sign in to Jellyfin, open `/RetroBridge/Play`, choose the phone, press **Play**. The game boots in RetroArch.
- [ ] Note whether the browser asks before opening RetroBridge, and whether "always allow" sticks.
- [ ] Make an in-game save (the `save-counter` fixture counts boots), press Home, launch again, and confirm the save survived.
- [ ] Repeat launch/exit about 10 times. Confirm `/sdcard/RetroBridge/backups/<game id>/` holds at most 5 backups and the save is never empty.
- [ ] Google Play RetroArch: confirm `RetroActivityFuture` accepts the external launch, and check the logcat line `RetroArch reported N cores`. If it says `assuming defaults`, that build lacks the core query and uses the fallback path.
- [ ] With no GB core installed, launching a Game Boy game shows the "install Gambatte" message instead of a black screen.
- [ ] Touch controls, a Bluetooth controller, audio, and rotation work in the launched game.
- [ ] Airplane mode / server offline → a clear error toast, no crash.
- [ ] Revoke the phone under Dashboard → Devices → the next launch reports that it is no longer paired.
- [ ] Remove the player in the plugin settings → the next launch reports that access was removed.
- [ ] Battery optimization or an app killed in the background does not lose a save that was already written.
- [ ] Save sync between phone and desktop: play on the desktop, launch on the phone (it should continue), reopen the RetroBridge app, play on the desktop again. Also check that the background job uploads without opening the app (within about 15 minutes, on Wi-Fi and battery).

## Pending: other manual checks

- [ ] Windows: native RetroArch save/restart with the packaged `win-x64` companion (CI covers build, tests, and registration only).
- [ ] Download a companion package on a clean machine (no .NET, no developer tools) and follow `QUICKSTART.txt` end to end. Note Gatekeeper/SmartScreen prompts.
- [ ] Windows: pressing Play in Edge/Chrome launches through the registered `retrobridge://` handler.
- [ ] Linux desktop: pressing Play in Firefox/Chrome launches through the `.desktop` handler.
- [ ] `autostart on`, then sign out and back in (or reboot) on macOS, Windows, and Linux: the device shows *(ready)* on the play page without running anything by hand. On Windows, confirm no console window appears.
- [ ] Real jellyfin-web: dashboard page layout, keyboard navigation, player checkboxes, and error messages (checked only in jsdom).
- [ ] Controller input, audio, window focus, and normal exit on macOS/Windows/Linux with a playable homebrew game.
- [ ] Missing executable/core and corrupt content produce actionable messages in the desktop companion log.
- [ ] Plugin upgrade over an existing install keeps configuration, the catalog snapshot, and cached artwork.
- [ ] Box art for a real No-Intro-named library (dozens of games): check match quality and misses, and the play page grid on phone and desktop.

## Verified automatically (for reference)

Plugin loading in Jellyfin 12.1.0, real authentication boundaries, scan/filter/download/rescan identity, root revocation, catalog persistence across restart, Quick Connect pairing, single-use tickets, one-click desktop launch (macOS LaunchServices handler), remote launch from a phone browser, and native save persistence on macOS (RetroArch 1.22.2) and Linux (Docker, RetroArch 1.18.0).
