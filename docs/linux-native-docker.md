# Linux native save test (Docker) and teardown

`python3 scripts/jellyfin-smoke.py --native-linux-docker` runs the real Jellyfin smoke test, then the native save test in a headless Linux RetroArch container. CI runs it on `ubuntu-latest`.

## How it works

- [`docker/native-linux.Dockerfile`](../docker/native-linux.Dockerfile) builds `retrobridge-native-linux:local` from the pinned .NET 10 runtime image (Ubuntu 24.04) with Ubuntu's `retroarch` (1.18.0) and `libretro-nestopia` packages. RetroArch uses null video, audio, and input drivers and runs as an unprivileged `retro` user.
- The container joins the Jellyfin test container's network namespace (`--network container:<name>`). Jellyfin is then reachable at `http://127.0.0.1:8096`, so the companion's loopback-only HTTP exception still applies and no TLS setup is needed.
- The repository is mounted read-only at `/repo`. The container runs the host-built, framework-dependent `RetroBridge.Launcher.dll` through `scripts/native_smoke.py`. Build first with `dotnet build -c Release`.
- The admin token is passed as the `RETROBRIDGE_TOKEN` environment variable, never as an argument.
- On Linux hosts the Jellyfin container runs as the invoking user, so the temporary workspace can be deleted afterwards.

Results are written to `native_emulation_linux` in `artifacts/jellyfin-smoke.json`. The run passes when the fixture's battery-save counter goes 1 → 2.

## What the tests leave behind

All containers and the native image carry the label `org.retrobridge.smoke=true`. Containers are removed after every run, including failed runs. These items persist:

| Item | Approx. size | Created by |
|---|---|---|
| `retrobridge-native-linux:local` image | 1.7 GB | `--native-linux-docker` |
| `mcr.microsoft.com/dotnet/runtime:10.0` image | 330 MB | base of the native image |
| `jellyfin/jellyfin:12.1` image | 1.3 GB | every smoke run |

The macOS native run (October 21, 2026, on the maintainer's machine) also added:

| Item | Location |
|---|---|
| Nestopia core (libretro buildbot nightly, arm64) | `~/Library/Containers/com.libretro.dist.RetroArch/Data/Library/Application Support/RetroArch/cores/nestopia_libretro.dylib` |
| Empty temp parent for sandboxed RetroArch | `~/Library/Containers/com.libretro.dist.RetroArch/Data/tmp` |
| .NET SDK 10.0.401 (user-local install) | `~/.dotnet` |

## Teardown

```sh
# Leftover test containers (normally none)
docker rm -f $(docker ps -aq --filter label=org.retrobridge.smoke=true) 2>/dev/null

# Images
docker rmi retrobridge-native-linux:local retrobridge-native-linux:local-packaged
docker image prune -f --filter label=org.retrobridge.smoke=true   # dangling rebuilds
docker rmi mcr.microsoft.com/dotnet/runtime:10.0 jellyfin/jellyfin:12.1 ubuntu:24.04
```

`docker rmi` fails if another project uses the same base image. That's safe; leave it.

Optional macOS cleanup (only if nothing else uses these):

```sh
RA="$HOME/Library/Containers/com.libretro.dist.RetroArch/Data"
rm "$RA/Library/Application Support/RetroArch/cores/nestopia_libretro.dylib"
rmdir "$RA/tmp"
rm -rf ~/.dotnet   # removes the SDK; required to build this repository
```

One-click play leftovers (the smoke tests unregister their handler and use temporary data directories):

```sh
RetroBridge.Launcher unregister              # if you registered the link handler yourself
RetroBridge.Launcher autostart off           # if you enabled the login listener
rm -rf ~/Library/Application\ Support/RetroBridge   # macOS default data: pairing token, settings, cache, saves
rm -rf ~/.local/share/RetroBridge             # Linux default data
rm -rf tests/browser/node_modules             # jsdom for the play-page test
```

Android emulator leftovers (from the mobile tests):

```sh
adb uninstall org.retrobridge.android
adb uninstall com.retroarch.aarch64          # removes RetroArch, its cores, and its config
adb shell rm -rf /sdcard/RetroBridge /sdcard/RetroArch /sdcard/Download/nestopia_libretro_android.so /sdcard/Download/save-counter.nes
rm -rf android/.gradle android/app/build ~/.gradle/caches   # Gradle build output and shared cache
```

Deleting a data directory also deletes local saves. Revoke a pairing in Jellyfin under Dashboard → Devices.

Verify that nothing remains:

```sh
docker ps -a --filter label=org.retrobridge.smoke=true
docker images | grep -E 'retrobridge|jellyfin|dotnet/runtime'
```
