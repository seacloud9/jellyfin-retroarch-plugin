# Jellyfin RetroArch Plugin — Development Plan

A Jellyfin plugin that lets users browse their games and launch them in RetroArch, with modern engineering practices and testing built in.

The recommended architecture is a **Jellyfin server plugin, a game library interface, and a small desktop launcher**. Start with native RetroArch on macOS, Windows, and Linux; treat browser emulation and streaming as later features.

[JellyEmu](https://github.com/Jellyfin-PG/JellyEmu) already advertises Jellyfin game libraries and RetroArch support. The first milestone should evaluate whether extending it would meet the project goals before committing to a separate implementation. Its advertised capabilities still need hands-on verification.

## 1. Define the first release

### Core user journey

1. An administrator installs the plugin and selects game folders.
2. The plugin indexes supported games.
3. A user signs into the game interface using Jellyfin.
4. They browse, search, and select a game.
5. They choose **Play in RetroArch**.
6. The companion launcher obtains the game, selects an approved core, and starts RetroArch.
7. Game saves remain associated with that user and game.

### Proposed scope

| Include in the first release | Add after the foundation is reliable |
|---|---|
| Game library, search, platform filters | Browser emulation |
| Local artwork and editable metadata | External metadata providers |
| Native RetroArch launching | Remote game streaming |
| Per-user permissions | Netplay orchestration |
| Persistent local saves | Cross-device save synchronization |
| Clear installation and diagnostics | Android, iOS, and television integrations |
| A small verified core/platform matrix | Broader console coverage |

Start with two or three systems that have straightforward single-file games. Add disc images, multidisc games, archives, and BIOS-dependent systems incrementally.

## 2. Establish the integration boundary

A server plugin and the player’s device are different execution environments. The design must explicitly handle launching RetroArch **on the player’s computer**.

RetroArch documents command-line launching with a selected core and content file, along with configuration overrides. That gives us a concrete integration point. See the [RetroArch CLI documentation](https://docs.libretro.com/guides/cli-intro/).

| Component | Responsibilities | Proposed implementation |
|---|---|---|
| Jellyfin plugin | Configuration, indexing, authorization, catalog API, content delivery | C# using the supported Jellyfin plugin APIs |
| Game interface | Browsing, search, game details, launch status | React and TypeScript |
| Desktop companion | Device pairing, content cache, core detection, process launch, local saves | Small C# application with OS-specific adapters |
| Storage | Catalog records, device registrations, save metadata | Plugin-owned SQLite database and managed files |
| RetroArch | Emulation, gameplay input, audio, shaders | User-installed, verified versions and cores |

Use a dedicated web interface initially. Validate how to expose its entry point in the selected Jellyfin client during the prototype.

**Do not promise that installing the server plugin automatically adds a Games section to every Jellyfin client.** Existing projects illustrate the complication: [Mojo Snap](https://github.com/7CGPA-Labs/mojo-snap-plugin) documents a separate web UI injection step. Isolate any client-specific integration behind an adapter.

## 3. Use current, compatible dependencies

“Modern” should mean maintained, supported, and reproducible.

- Select the current stable Jellyfin release at implementation kickoff.
- Match its required .NET target and Jellyfin package versions.
- Pin the SDK, package dependencies, RetroArch test version, and core builds.
- Use strict TypeScript, C# nullable reference types, formatting, and static analysis.
- Keep business logic independent of Jellyfin APIs through small interfaces.
- Use asynchronous I/O, cancellation, bounded background work, and structured logs.
- Record architecture decisions and maintain a tested compatibility table.

[Jellyfin’s official template](https://github.com/jellyfin/jellyfin-plugin-template) explicitly warns that mismatched package and server versions can make a plugin unsupported. Verify the actual target project files rather than copying framework versions from an older tutorial.

### Suggested repository layout

```text
src/
  Jellyfin.Plugin.RetroBridge/
  RetroBridge.Domain/
  RetroBridge.Infrastructure/
  RetroBridge.Launcher/
web/
tests/
  Unit/
  Integration/
  Contract/
  Launcher/
  EndToEnd/
fixtures/
docs/
.github/workflows/
```

“RetroBridge” is a working name.

## 4. Build indexing and game identity carefully

Store separate records for a game, its content files, its platform, and supported launch profiles.

Each content record should include:

- Stable internal ID.
- File size and content checksum.
- Platform and format.
- Associated files, where needed.
- Relative location within an administrator-approved root.
- Metadata and artwork references.

Indexing should support incremental scans, cancellation, moved files, duplicates, and permission failures. Large scans should run in the background without blocking Jellyfin startup.

Use content checksums to identify game revisions. Filenames alone are insufficient for save association or cross-device synchronization.

**Acceptance criteria:** rescanning unchanged content creates no duplicates; interrupted scans recover; unreadable files produce actionable errors without stopping the whole scan.

## 5. Design a secure, reliable launch flow

### Recommended flow

1. The user pairs the desktop companion with their Jellyfin account.
2. The interface requests a launch for a game and paired device.
3. The plugin verifies access and creates a short-lived, single-use launch ticket.
4. An application URL opens the companion with that opaque ticket.
5. The companion redeems it over HTTPS.
6. It downloads or reuses verified cached content.
7. It checks the installed core and prepares a per-user configuration.
8. It starts RetroArch and reports the outcome.

### Implementation requirements

- Never put a permanent Jellyfin credential in the application URL.
- Bind tickets to the intended user, device, and game.
- Reject expired and replayed tickets.
- Resolve executable and core paths locally from approved configuration.
- Launch through structured process arguments, without shell interpolation.
- Keep RetroArch’s existing user configuration intact.
- Handle missing applications, missing cores, interrupted downloads, and repeated clicks.

**Acceptance criteria:** a user can launch a fixture game on each supported desktop platform; malformed launch data cannot select arbitrary executables or read unrelated files.

## 6. Treat saves as a data-integrity feature

First deliver persistent local saves. Add server synchronization once local behavior is verified.

For synchronization:

- Isolate data by user, content checksum, and save type.
- Track core identity, version, and relevant settings for save states.
- Upload only after a known flush point or process exit.
- Write atomically and retain recoverable previous versions.
- Use revision checks to detect competing updates.
- Preserve both copies when devices conflict.
- Test recovery after crashes and interrupted transfers.

Distinguish ordinary in-game saves from emulator save states. Compatibility must be verified per core: libretro’s documentation includes examples where changed settings prevent state loading and where states cannot migrate between core implementations. See [DOSBox Pure](https://docs.libretro.com/library/dosbox_pure/) and [melonDS DS](https://docs.libretro.com/library/melonds_ds/).

## 7. Build the test system alongside every feature

Use Playwright for browser workflows, Vitest/React Testing Library for UI behavior, and xUnit for C#.

| Test layer | What it proves | Execution |
|---|---|---|
| Unit | Identification, filtering, core selection, ticket expiry, save conflict rules | Every pull request |
| Integration | Real Jellyfin plugin loading, authentication, database migrations, file delivery | Every pull request |
| API contracts | Web interface and companion agree on requests, responses, and errors | Every pull request |
| UI behavior | Search, filters, keyboard access, loading and failure states | Every pull request |
| Browser E2E | Sign in, browse, request launch, display results | Every pull request |
| Launcher integration | Correct process arguments, cache behavior, process exit handling | Supported desktop OS matrix |
| Actual emulator smoke | A real core loads a fixture and produces expected output | Nightly and release builds |
| Recovery | Interrupted downloads, full disk, failed save uploads, restart recovery | Nightly and release builds |
| Upgrade | Existing configuration and data survive a plugin update | Every release |

Use redistributable homebrew or purpose-built test games, including a fixture that exercises persistent saves.

**A mocked launch is not proof that RetroArch works.** Maintain both fast tests with a process substitute and real emulator checks. Playwright covers the web interface; native gameplay needs a separate harness and hardware testing.

For gamepads, audio, focus changes, and Bluetooth reconnects, keep a small manual release checklist. Those behaviors cannot be fully established by browser automation.

## 8. Automate builds and releases from day one

Every pull request should run:

- Formatting, static analysis, and type checks.
- Unit, integration, and relevant end-to-end tests.
- Dependency and secret scanning.
- Plugin and companion builds.
- Compatibility checks for the supported Jellyfin versions.

Release automation should:

- Build from a tagged commit with locked inputs.
- Produce the plugin archive and Jellyfin repository manifest.
- Produce platform-specific companion packages.
- Generate checksums, dependency inventory, and build provenance.
- Sign installers where signing credentials are available.
- Publish release notes and the tested compatibility matrix.
- Verify installation and upgrade using the actual packaged artifacts.

Jellyfin supports third-party plugin repositories through a manifest pointing to versioned releases. Use its current manifest format when implementing distribution. See [Jellyfin plugin repositories](https://jellyfin.org/posts/plugin-updates/).

## 9. Implement in milestones with exit criteria

These are planning estimates for one experienced developer working consistently; revise them after the feasibility prototype.

| Milestone | Estimate | Required outcome |
|---|---|---|
| Evaluate existing projects | 2–3 days | Document extend-versus-build decision, dependencies, and gaps |
| Prove integration | 3–5 days | Plugin loads; one authorized game launches through a companion |
| Establish engineering foundation | 3–5 days | CI, fixtures, contracts, repeatable development setup |
| Build catalog and interface | 1–2 weeks | Scan, search, filter, permissions, and metadata editing work |
| Harden desktop launching | 1–2 weeks | Pairing, caching, configuration isolation, cross-platform errors |
| Verify saves and recovery | 1 week | Local saves persist; failure tests pass |
| Package and release beta | 1–2 weeks | Installers, upgrade tests, documentation, compatibility matrix |

**Expected scope: roughly 6–10 weeks for a dependable desktop beta.** Browser emulation, streaming, mobile support, and synchronized saves should receive separate estimates.

## 10. Define completion before implementation

The first release is ready when:

- [ ] A fresh Jellyfin installation can install and configure the plugin.
- [ ] Authorized users can browse and launch supported games.
- [ ] Unauthorized users cannot retrieve games or another user’s saves.
- [ ] All advertised desktop platforms pass real launch checks.
- [ ] Saves survive application restarts.
- [ ] Failed launches explain what the user needs to fix.
- [ ] Plugin upgrades preserve data and configuration.
- [ ] Packaged releases pass installation tests.
- [ ] Documentation identifies supported versions, cores, and known limitations.

## First development task

Build a complete vertical slice: load the plugin, index one test game, display it, authorize a launch, start native RetroArch, and verify that a save survives restarting the game. This validates the hardest integration assumptions before investing in the larger interface.

## Sources

- [JellyEmu repository](https://github.com/Jellyfin-PG/JellyEmu)
- [RetroArch command-line interface](https://docs.libretro.com/guides/cli-intro/)
- [Mojo Snap plugin repository](https://github.com/7CGPA-Labs/mojo-snap-plugin)
- [Official Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template)
- [DOSBox Pure core documentation](https://docs.libretro.com/library/dosbox_pure/)
- [melonDS DS core documentation](https://docs.libretro.com/library/melonds_ds/)
- [Jellyfin plugin repositories](https://jellyfin.org/posts/plugin-updates/)
