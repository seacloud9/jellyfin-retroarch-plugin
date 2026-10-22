"""Assemble release assets: plugin zip (with meta.json), companion archives, Android APK, Jellyfin repository manifest,
release notes, and SHA256SUMS in artifacts/release/.

Usage: python3 scripts/build-release.py --tag vX.Y.Z [--repo owner/name] [--base-url URL] [--apk PATH] [--companions DIR]
--base-url overrides where the manifest points (default: the GitHub release download URL), e.g. for a local test server.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

REPO = Path(__file__).resolve().parents[1]
GUID = '4be0a14e-f03e-4e52-b702-3e38b8cedf17'
TARGET_ABI = '12.1.0.0'
OVERVIEW = 'Play NES, Game Boy, and Game Boy Color games from your Jellyfin server in RetroArch, with one-click launch, box art, and save sync.'
DESCRIPTION = ('Indexes NES, Game Boy, and Game Boy Color games, serves a player page with box art, and launches them in RetroArch '
               'through the RetroBridge companion (desktop or Android) using single-use tickets. Battery saves sync between devices. '
               'Prototype: see the project README for setup and limits.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--repo', default='seacloud9/jellyfin-retroarch-plugin')
    parser.add_argument('--base-url')
    parser.add_argument('--apk', type=Path)
    parser.add_argument('--companions', type=Path, default=REPO / 'artifacts/companion')
    args = parser.parse_args()
    version = args.tag.removeprefix('v')
    if version.count('.') != 2:
        parser.error('Tag must look like v1.2.3')
    out = REPO / 'artifacts/release'
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    base_url = (args.base_url or f'https://github.com/{args.repo}/releases/download/{args.tag}').rstrip('/')
    timestamp = datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
    # The version's CHANGELOG.md section is required: it becomes the release's "What's new" and the catalog changelog.
    import re
    section = re.search(rf'^## \[{re.escape(version)}\][^\n]*\n(.*?)(?=^## \[|\Z)', (REPO / 'CHANGELOG.md').read_text(), re.S | re.M)
    if not section or not section.group(1).strip():
        parser.error(f'CHANGELOG.md has no section for {version}; add "## [{version}] - YYYY-MM-DD" with its changes')
    whats_new = section.group(1).strip()
    changelog = re.sub(r'\*\*|\[([^\]]+)\]\([^)]+\)', lambda m: m.group(1) or '', whats_new)  # plain text for Jellyfin's catalog

    # Plugin zip: plugin-owned assemblies plus meta.json, the layout Jellyfin installs from repositories.
    plugin_dir = REPO / 'src/Jellyfin.Plugin.RetroBridge/bin/Release/net10.0'
    meta = {'category': 'General', 'changelog': changelog, 'description': DESCRIPTION, 'guid': GUID, 'name': 'RetroBridge',
            'overview': OVERVIEW, 'owner': args.repo.split('/')[0], 'targetAbi': TARGET_ABI, 'timestamp': timestamp,
            'version': version + '.0', 'status': 'Active', 'autoUpdate': True, 'imagePath': '', 'assemblies': []}
    plugin_zip = out / f'RetroBridge-Plugin-{version}.zip'
    with zipfile.ZipFile(plugin_zip, 'w', zipfile.ZIP_DEFLATED) as z:
        for name in ('Jellyfin.Plugin.RetroBridge.dll', 'RetroBridge.Domain.dll'):
            z.writestr(zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0)), (plugin_dir / name).read_bytes())
        z.writestr(zipfile.ZipInfo('meta.json', date_time=(2026, 1, 1, 0, 0, 0)), json.dumps(meta, indent=2))

    # Jellyfin verifies repository downloads with MD5.
    manifest = [{
        'guid': GUID, 'name': 'RetroBridge', 'description': DESCRIPTION, 'overview': OVERVIEW,
        'owner': args.repo.split('/')[0], 'category': 'General', 'imageUrl': '',
        'versions': [{'version': version + '.0', 'changelog': changelog, 'targetAbi': TARGET_ABI,
                      'sourceUrl': f'{base_url}/{plugin_zip.name}', 'checksum': hashlib.md5(plugin_zip.read_bytes()).hexdigest(),
                      'timestamp': timestamp}],
    }]
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')

    companions = sorted(p for p in args.companions.rglob('RetroBridge-Companion-*') if p.suffix in ('.zip', '.gz'))
    for archive in companions:
        shutil.copy2(archive, out / archive.name)
    if args.apk:
        shutil.copy2(args.apk, out / f'RetroBridge-Android-{version}-debug.apk')

    names = sorted(p.name for p in out.iterdir() if p.is_file())
    (out / 'SHA256SUMS').write_text(''.join(f'{hashlib.sha256((out / n).read_bytes()).hexdigest()}  {n}\n' for n in names))
    assets = '\n'.join(f'- `{n}`' for n in names + ['SHA256SUMS'])
    (out / 'NOTES.md').write_text(f"""### What's new in {version}
{whats_new}

RetroBridge {version} — **prototype** release for **Jellyfin {TARGET_ABI[:-2]}** (published as "latest" so the repository URL below resolves). Licensed GPL-2.0-or-later.

### Install the plugin
Add this repository in Jellyfin (Dashboard → Plugins → Repositories → +):

`https://github.com/{args.repo}/releases/latest/download/manifest.json`

Then install **RetroBridge** from the catalog and restart Jellyfin. Or install `RetroBridge-Plugin-{version}.zip` manually (extract into `plugins/RetroBridge`).

### Companion (desktop)
Download the archive for your computer. It's self-contained, so no .NET is needed. Follow `QUICKSTART.txt`. The builds are **not signed or notarized**: macOS Gatekeeper and Windows SmartScreen will warn.

### Android
`RetroBridge-Android-{version}-debug.apk` is a debug-signed prototype for sideloading. It has been tested on the Android 17 emulator only, and needs RetroArch for Android plus "All files access".

### Third-party software
In-browser play (optional, off by default) makes the Jellyfin server download pinned open-source emulator files: RetroArch 1.22.2 (GPL-3.0) with FCEUmm (GPL-2.0-or-later) and mGBA (MPL-2.0) cores, plus Nostalgist 0.23.0 (MIT). They are not included in this release. Licenses and corresponding source: [docs/third-party.md](https://github.com/{args.repo}/blob/{args.tag}/docs/third-party.md).

### Status
Verified against real Jellyfin 12.1 in Docker, real RetroArch on macOS and Linux, and the Android emulator. Pending: a real Android phone, Windows native RetroArch, and the dashboard in real jellyfin-web. See `docs/verification.md` and `docs/manual-smoke.md`.

### Assets
{assets}

Verify downloads with `shasum -a 256 -c SHA256SUMS`.
""")
    print('\n'.join(str(out / n) for n in names + ['SHA256SUMS', 'NOTES.md']))


if __name__ == '__main__':
    main()
