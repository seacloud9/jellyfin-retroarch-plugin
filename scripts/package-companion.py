"""Build the self-contained, single-file desktop companion for one runtime and archive it with a quickstart.

Usage: python3 scripts/package-companion.py [--rid osx-arm64|osx-x64|win-x64|linux-x64|linux-arm64]
The default is this machine's runtime. Output: artifacts/companion/RetroBridge-Companion-<version>-<rid>.(tar.gz|zip) and .sha256.
Players need no .NET installation.
"""
import argparse
import hashlib
import io
import platform
from pathlib import Path
import subprocess
import sys
import tarfile
import zipfile

REPO = Path(__file__).resolve().parents[1]
VERSION = '0.1.0'
RIDS = ['osx-arm64', 'osx-x64', 'win-x64', 'linux-x64', 'linux-arm64']
QUICKSTART = """RetroBridge Companion {version} ({rid})
==========================================

Starts RetroArch on this computer for games in your Jellyfin RetroBridge library.
No .NET installation is needed. Install RetroArch and the cores you want first.

1. Pair with your Jellyfin account (Quick Connect must be enabled on the server):
     {exe} pair https://your-jellyfin.example/
   Enter the code it prints under your Jellyfin profile -> Quick Connect.

2. Tell it where RetroArch and the cores are:
     {exe} configure --retroarch "{retroarch}" --core nes="<path to nestopia core>" --core gb="<path to gambatte core>"

3. One-click play from the browser:
     {exe} register
   Optional, to play from your phone or any browser while this computer is on:
     {exe} autostart on

Then open the play page printed after pairing (https://your-jellyfin.example/RetroBridge/Play).

Undo: {exe} autostart off  /  {exe} unregister. Revoke the device in Jellyfin under Dashboard -> Devices.
{note}"""


def default_rid():
    machine = platform.machine().lower()
    arch = 'arm64' if machine in ('arm64', 'aarch64') else 'x64'
    system = {'darwin': 'osx', 'win32': 'win'}.get(sys.platform, 'linux')
    return f'{system}-{arch}'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', choices=RIDS, default=default_rid())
    args = parser.parse_args()
    rid = args.rid
    out = REPO / 'artifacts/companion' / rid
    subprocess.run(['dotnet', 'publish', str(REPO / 'src/RetroBridge.Launcher'), '-c', 'Release', '-r', rid, '--self-contained',
                    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none',
                    # The projects declare every packaged runtime, so the committed lock files already cover this one.
                    '-p:RestoreLockedMode=true',
                    '-p:Version=' + VERSION, '-o', str(out)], check=True)
    windows = rid.startswith('win')
    exe_name = 'RetroBridge.Launcher.exe' if windows else 'RetroBridge.Launcher'
    binary = (out / exe_name).read_bytes()
    note = ''
    if rid.startswith('osx'):
        note = ('\nmacOS: this build is not notarized. If macOS blocks it, run once:\n'
                '  xattr -d com.apple.quarantine RetroBridge.Launcher\n')
    quickstart = QUICKSTART.format(version=VERSION, rid=rid, exe='RetroBridge.Launcher.exe' if windows else './RetroBridge.Launcher',
                                   retroarch={'osx': '/Applications/RetroArch.app/Contents/MacOS/RetroArch', 'win': r'C:\RetroArch-Win64\retroarch.exe'}
                                   .get(rid.split('-')[0], '/usr/bin/retroarch'), note=note).encode()
    folder = f'RetroBridge-Companion-{VERSION}-{rid}'
    archive = REPO / 'artifacts/companion' / (folder + ('.zip' if windows else '.tar.gz'))
    if windows:
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
            for name, data in [(exe_name, binary), ('QUICKSTART.txt', quickstart)]:
                z.writestr(zipfile.ZipInfo(f'{folder}/{name}', date_time=(2026, 1, 1, 0, 0, 0)), data)
    else:
        with tarfile.open(archive, 'w:gz') as tar:
            for name, data, mode in [(exe_name, binary, 0o755), ('QUICKSTART.txt', quickstart, 0o644)]:
                info = tarfile.TarInfo(f'{folder}/{name}')
                info.size, info.mode, info.mtime = len(data), mode, 1767225600
                tar.addfile(info, io.BytesIO(data))
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    Path(str(archive) + '.sha256').write_text(f'{digest}  {archive.name}\n')
    print(archive)


if __name__ == '__main__':
    main()
