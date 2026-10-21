# Prototype decisions

## Build versus extend

The initial review inspected [JellyEmu's README](https://github.com/Jellyfin-PG/JellyEmu), which advertises game management, RetroArch, and other integrations. Those features were not installed or evaluated hands-on. This repository proceeds with a small independent feasibility prototype to test the native companion and server boundaries described in the supplied plan. This is a provisional decision, not evidence that JellyEmu cannot satisfy the requirements. Revisit it before building a full game interface. No JellyEmu code was copied.

## Compatibility

At kickoff, the [Jellyfin release API](https://api.github.com/repos/jellyfin/jellyfin/releases/latest) returned `v12.1`. Its [server project](https://github.com/jellyfin/jellyfin/blob/v12.1/Jellyfin.Server/Jellyfin.Server.csproj) targets `net10.0`; NuGet provides matching `12.1.0` Controller/Model packages. These are pinned. The [plugin template](https://github.com/jellyfin/jellyfin-plugin-template) still targeted an older server version when inspected, so its package versions were not copied blindly. Earlier Jellyfin releases are not supported by this build.

| Component | Selected version / status |
|---|---|
| .NET SDK | 10.0.401, pinned in global.json |
| Jellyfin packages | 12.1.0; compiles, live loading still unverified |
| RetroArch | User-installed; no version/core build verified yet |
| Desktop OS | Local checks on macOS arm64; CI matrix configured for Windows/Linux/macOS |

## Boundaries

`RetroBridge.Domain` contains scanning, path checks, game DTOs, and launch configuration/argument construction without Jellyfin dependencies. `Jellyfin.Plugin.RetroBridge` provides service registration, the administrator-only API, and an embedded dashboard. `RetroBridge.Launcher` is a command-line process on the player's computer; the server never starts an emulator.

Content IDs combine platform and SHA-256. Metadata responses do not expose source paths. Scan snapshots are replaced atomically in memory and survive scan failure/cancellation. Each error-free scan is also written atomically to a versioned JSON file under Jellyfin's data path. The file lives outside the plugin directory, so upgrades keep it. It is loaded at startup. A missing, corrupt, or unknown-version file yields an empty catalog, and entries with inconsistent identity or absolute paths are dropped. Downloads still check the root against the current configuration and re-resolve paths safely. JSON avoids shipping per-OS SQLite native libraries. Revisit when incremental scans need per-file state. Full scans still hash every file.

Pairing uses Jellyfin Quick Connect, so the companion never handles a password. It receives a normal Jellyfin access token bound to a random `retrobridge-<hex>` device ID, which Jellyfin lists and can revoke under Devices. Launch tickets follow the plan's flow. A player-authenticated request names the target device and receives an opaque 256-bit ticket. The server stores only its SHA-256, bound to user, device, and game, with a 2-minute expiry. Redemption needs the bound device's own token: the user and device are read from Jellyfin's `Jellyfin-UserId`/`Jellyfin-DeviceId` claims. Any redemption attempt consumes the ticket, and all failures share one 404 response. Tickets live in memory only. A ticket alone grants nothing without the paired device's token, so a leaked ticket or link is harmless to other machines.

Access uses an administrator-managed allow-list of player user IDs for the whole library. Scanning stays administrator-only. The plugin reads configuration through an injected delegate so tests can supply it. The dashboard does not yet offer a Play button or link handler.

RetroArch is started with `-L` and `--appendconfig`, following the [official CLI documentation](https://docs.libretro.com/guides/cli-intro/). Saves are separated by server/user/platform/content identity. No server save synchronization, core approval registry, or save-state compatibility validation is provided yet.
