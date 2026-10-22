# Manual acceptance checklist

Automated real-server tests now cover most integration checks (see the [verification record](verification.md)). This list holds what still needs a person, real hardware, or a platform the automation can't reach. Do not mark these complete from emulator, substitute-process, or jsdom results.

## Tonight: real-hardware session (about 15 minutes per device)

Use v0.2.0 or later. Have the plugin page open on a computer. If anything fails, open the play page's **Diagnostics → Copy report** (phone browser), run `RetroBridge.Launcher doctor` (computer), or read **Last problem** in the Android app, and keep the text.

**Setup (computer, 3 min)**
1. Plugin page: tick your player account, turn on **In-browser play**, tick NES/GB/GBC, **Download emulator files**, **Save settings**. Copy the play page link.
2. Put a legal game for each platform you have (homebrew is fine) in a game folder and **Scan**. Note whether box art appears.

**iPhone or iPad, Safari (5 min)**
3. Sign in to Jellyfin in Safari, open the play page link, and press **Play in browser** on an NES game.
4. Note: time to start, whether you hear sound (tap the screen once if not), and whether the touch buttons respond without zooming or scrolling the page.
5. Play 1–2 minutes and make an in-game save if the game has one. Switch to another app for 30 seconds, come back, and check the game continued. Press **Save and exit**.
6. Open the game again: the save must be there. Rotate the phone once and note how the layout looks.
7. Repeat steps 3–6 quickly with a Game Boy game.

**Android phone (5 min)**
8. Chrome: open the play page and press **Play in browser**. This is the step the emulator could not render; note whether the game appears and runs smoothly.
9. If you sideload the APK: install RetroArch and its Nestopia/Gambatte cores, open RetroArch once, install RetroBridge, allow **All files access**, pair, then press **Play** with the phone selected. Note any **Last problem**.

**Cross-device (2 min)**
10. Continue the same game on a different device (browser ↔ desktop companion ↔ Android). The progress must carry over. Each card's "Saved … on …" line shows the device.

Record per device: model, OS version, browser version, what worked, what didn't, and the copied report for any failure.

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
- [ ] Plugin upgrade on a long-running real server keeps cached artwork and save history (configuration and catalog are covered by the release gate).
- [ ] In-browser play on a real iPhone and a real Android phone: audio after a tap, touch controls in a real game, Bluetooth gamepad, rotating the device, switching apps mid-game (the save must be on the server afterwards), and memory or thermal behavior over 15 minutes.
- [ ] Play page "Saved … on …" line after playing on a paired desktop and on the Android app.
- [ ] Box art for a real No-Intro-named library (dozens of games): check match quality and misses, and the play page grid on phone and desktop.

## Verified automatically (for reference)

Plugin loading in Jellyfin 12.1.0, real authentication boundaries, scan/filter/download/rescan identity, root revocation, catalog persistence across restart, Quick Connect pairing, single-use tickets, one-click desktop launch (macOS LaunchServices handler), remote launch from a phone browser, and native save persistence on macOS (RetroArch 1.22.2) and Linux (Docker, RetroArch 1.18.0).
