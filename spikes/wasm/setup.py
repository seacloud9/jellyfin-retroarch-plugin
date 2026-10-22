"""Prepare the in-browser (WASM) spike in artifacts/wasm-spike and print how to serve it.

Downloads pinned third-party files (Nostalgist, MIT; RetroArch Emscripten FCEUmm core built by
arianrhodsandlot/retroarch-emscripten-build) and copies the save-counter fixture. Nothing third-party is committed.
"""
import hashlib
import io
from pathlib import Path
import subprocess
import sys
import urllib.request
import zipfile

REPO = Path(__file__).resolve().parents[2]
OUT = REPO / 'artifacts/wasm-spike'
NOSTALGIST = 'https://cdn.jsdelivr.net/npm/nostalgist@0.23.0/dist/nostalgist.umd.js'
# Nostalgist 0.23.0 expects cores from this tag.
CORE = 'https://cdn.jsdelivr.net/gh/arianrhodsandlot/retroarch-emscripten-build@v1.22.2/retroarch/fceumm_libretro.zip'


def fetch(url):
    with urllib.request.urlopen(url, timeout=60) as response:
        return response.read()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / 'nostalgist.umd.js').write_bytes(fetch(NOSTALGIST))
    with zipfile.ZipFile(io.BytesIO(fetch(CORE))) as core:
        core.extractall(OUT)
    sys.path.insert(0, str(REPO / 'scripts'))
    from fixture_rom import write_fixture
    write_fixture(OUT / 'save-counter.nes')
    (OUT / 'index.html').write_text((Path(__file__).parent / 'index.html').read_text())
    for name in sorted(p.name for p in OUT.iterdir()):
        print(f'{hashlib.sha256((OUT / name).read_bytes()).hexdigest()[:16]}  {name}')
    print(f'\nServe with: python3 -m http.server 8099 --bind 127.0.0.1 --directory {OUT}')
    print('Open http://127.0.0.1:8099/ (iOS simulator: xcrun simctl openurl booted <url>; Android emulator: adb reverse tcp:8099 tcp:8099).')
    print('Reload to check that the save persists: the boot counter should increase.')


if __name__ == '__main__':
    main()
