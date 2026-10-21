"""Extract a packaged companion archive and run the companion checks against that binary, with .NET hidden.

Usage: python scripts/verify-companion-package.py <archive> [--jellyfin-linux-docker]
"""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile

REPO = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive', type=Path)
    parser.add_argument('--jellyfin-linux-docker', action='store_true', help='Also run the real Jellyfin + RetroArch save test in a container without .NET')
    args = parser.parse_args()
    expected = Path(str(args.archive) + '.sha256').read_text().split()[0]
    if hashlib.sha256(args.archive.read_bytes()).hexdigest() != expected:
        sys.exit('Checksum mismatch for ' + args.archive.name)
    with tempfile.TemporaryDirectory(prefix='retrobridge-package-') as directory:
        target = Path(directory)
        if args.archive.suffix == '.zip':
            with zipfile.ZipFile(args.archive) as z:
                z.extractall(target)
        else:
            with tarfile.open(args.archive) as tar:
                tar.extractall(target, filter='data')
        binary = next(p for p in target.rglob('RetroBridge.Launcher*') if p.is_file())
        if os.name != 'nt' and not os.access(binary, os.X_OK):
            sys.exit('Packaged companion is not executable')
        # Hide any .NET install so the self-contained binary must stand alone.
        dotnet = shutil.which('dotnet')
        hidden = {str(Path(dotnet).resolve().parent), str(Path(dotnet).parent)} if dotnet else set()
        path = os.pathsep.join(p for p in os.environ.get('PATH', '').split(os.pathsep) if p not in hidden and 'dotnet' not in p.lower())
        env = {k: v for k, v in os.environ.items() if not k.startswith('DOTNET_')}
        env.update(PATH=path, RETROBRIDGE_COMPANION=str(binary))
        def run(*command):
            print('$', ' '.join(command), flush=True)
            subprocess.run([sys.executable, *command], check=True, env=env, cwd=REPO)
        subprocess.run([str(binary), '--help'], check=True, env=env, stdout=subprocess.DEVNULL)
        if os.name != 'nt':
            run('scripts/launcher-smoke.py')
        run('scripts/link-handler-check.py')
        if args.jellyfin_linux_docker:
            env['RETROBRIDGE_COMPANION_LINUX'] = str(binary)
            run('scripts/jellyfin-smoke.py', '--native-linux-docker')
    print(f'PASS: {args.archive.name} verified without .NET')


if __name__ == '__main__':
    main()
