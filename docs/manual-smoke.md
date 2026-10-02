# Real integration acceptance checklist

These checks are pending, not asserted by the automated process-substitute tests.

- [ ] Install the packaged two-DLL plugin in a fresh Jellyfin 12.1.0 instance; confirm load logs and dashboard entry.
- [ ] Configure a mounted game directory containing a licensed/redistributable NES or Game Boy fixture; scan and inspect metadata.
- [ ] Confirm a second scan produces the same IDs and no duplicates.
- [ ] Confirm real Jellyfin anonymous and ordinary-user requests cannot list, scan, or download games.
- [ ] Use an administrator user session to download; verify bytes and SHA-256.
- [ ] Remove a configured root; verify its content endpoint immediately refuses downloads.
- [ ] Launch through the companion with an installed RetroArch and suitable core. Record OS, executable version, core name/version/checksum, and fixture provenance/checksum.
- [ ] Make an in-game save, exit RetroArch cleanly, relaunch, and verify it survives.
- [ ] Verify another Jellyfin user/server produces a different save directory.
- [ ] Confirm missing executable/core, offline server, and corrupt content produce actionable failures.
- [ ] Confirm repeated launches do not run concurrently against one save directory.
- [ ] Verify controller input, audio, window focus, and normal exit on macOS/Windows/Linux.
- [ ] Restart Jellyfin, rescan, and confirm content IDs remain stable.
- [ ] Check dashboard keyboard navigation and error reporting in real jellyfin-web.

Do not mark the integration milestone complete from mocked transport/process tests alone.
