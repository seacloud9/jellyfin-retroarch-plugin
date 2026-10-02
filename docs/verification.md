# Prototype verification record

Recorded October 2, 2026, on macOS arm64 using .NET SDK 10.0.401.

## Completed locally

| Check | Result | Scope |
|---|---|---|
| Locked dependency restore | Passed | NuGet package lock files match the projects |
| Release build | Passed, zero warnings/errors | Domain, Jellyfin plugin, companion, tests |
| .NET tests | 20 passed | Indexing, identity, filesystem checks, snapshot preservation, HTTP authorization/filtering, launch arguments, save-path isolation |
| Formatting | Passed | `dotnet format --verify-no-changes --no-restore` |
| Companion integration smoke | Passed | Substitute HTTP server and emulator process; cache reuse/repair, corrupt download rejection/cleanup, repeated save-directory use |
| Dashboard browser harness | Passed | Search, platform filter, folder saving, scan feedback; no reported browser errors |
| Plugin packaging | Passed | ZIP contains `Jellyfin.Plugin.RetroBridge.dll` and `RetroBridge.Domain.dll`; SHA-256 sidecar generated |

Build artifacts and browser harness outputs are generated locally under ignored directories and are not committed. CI is configured to upload plugin and companion build artifacts; this record does not assert that hosted CI has run successfully.

## Limits of this evidence

The HTTP tests use an ASP.NET test host and a substitute identity provider. The browser harness uses substitute `ApiClient` responses without Jellyfin styling. The companion test uses synthetic bytes and a process substitute that writes a marker file. None of these establishes real Jellyfin authentication/plugin loading, actual gameplay, or RetroArch save persistence.

Docker's daemon was unavailable during local verification. A real emulator/core/fixture combination has not been verified. Windows and Linux checks remain dependent on the configured CI matrix and later native testing. The full vertical-slice milestone remains open.

## Reproduce

From the repository root with the pinned SDK and Python installed:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
python3 scripts/launcher-smoke.py
python3 scripts/package-plugin.py
python3 scripts/dashboard-harness.py
```

The launcher smoke script currently requires macOS/Linux. Open `artifacts/dashboard-harness.html` for the browser check. Follow the [real integration checklist](manual-smoke.md) to complete the outstanding acceptance checks.
