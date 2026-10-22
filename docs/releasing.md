# Releasing

1. Update `<Version>` in `Directory.Build.props` (for example `0.2.0`) and commit it to `main`. CI must pass.
2. Tag and push: `git tag -a v0.2.0 -m "RetroBridge 0.2.0" && git push origin v0.2.0`.
3. `.github/workflows/release.yml` then:
   - builds and verifies the companion for all five runtimes (one verified per OS without .NET), and builds, tests, and lints the Android APK;
   - checks that the tag matches `<Version>`;
   - installs the plugin into a fresh Jellyfin through its repository flow (`scripts/repository-smoke.py`), against the exact assets being released;
   - publishes the GitHub release, marked **latest**, with the plugin zip (with `meta.json`), companion archives, debug APK, `manifest.json`, and `SHA256SUMS`.

Nothing is published if any step fails. Fix the problem and push the tag again: delete it with `git push origin :refs/tags/vX.Y.Z`, re-tag, and push.

## Repository URL

Admins add `https://github.com/seacloud9/jellyfin-retroarch-plugin/releases/latest/download/manifest.json`. GitHub's `latest` skips pre-releases, so releases are published as latest; otherwise this URL breaks. Each release's manifest lists only its own version. Jellyfin offers upgrades when a newer release becomes latest.

## After publishing

```sh
gh release download vX.Y.Z --dir /tmp/check && (cd /tmp/check && shasum -a 256 -c SHA256SUMS)
python3 scripts/repository-smoke.py --manifest-url https://github.com/seacloud9/jellyfin-retroarch-plugin/releases/latest/download/manifest.json
```

The URL can take a short time to resolve after a release is published or marked latest.

## Not yet automated

- Code signing and notarization (macOS, Windows). Needs developer certificates.
- A release-signed Android APK (currently debug-signed for sideloading). Needs a signing key kept in repository secrets.
- Submission to Jellyfin's official plugin catalog.
