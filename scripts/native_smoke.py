"""Launch real RetroArch twice through the companion and verify battery-save persistence.

Uses the save-counter fixture: boot 1 writes RB01 + counter 1, boot 2 must read it and write 2.
A wrapper adds --max-frames so RetroArch exits on its own and flushes SRAM; the companion's
production launch arguments are otherwise unchanged.
"""
import os
from pathlib import Path
import shlex
import subprocess
import tempfile

REPO = Path(__file__).resolve().parents[1]
LAUNCHER = REPO / 'src/RetroBridge.Launcher/bin/Release/net10.0/RetroBridge.Launcher.dll'

def companion_command():
    """The dev build via dotnet, or a packaged self-contained executable from RETROBRIDGE_COMPANION."""
    packaged = os.environ.get('RETROBRIDGE_COMPANION')
    return [packaged] if packaged else ['dotnet', str(LAUNCHER)]

FRAMES = 300


def read_save(data):
    saves = list((data / 'saves').rglob('*.srm'))
    if len(saves) != 1:
        raise AssertionError(f'Expected one emulator save file, found {len(saves)}')
    raw = saves[0].read_bytes()
    if raw[:4] != b'RB01':
        raise AssertionError('Save file is missing the fixture signature')
    return saves[0], raw[4]


def verify_native(url, token, game, executable, core, temp_parent=None):
    if os.name == 'nt':
        raise RuntimeError('The native smoke wrapper currently supports macOS/Linux only')
    if not os.environ.get('RETROBRIDGE_COMPANION') and not LAUNCHER.is_file():
        raise RuntimeError('Build the launcher first: dotnet build -c Release')
    for path in (executable, core):
        if not path.is_file():
            raise FileNotFoundError(path)
    with tempfile.TemporaryDirectory(prefix='retrobridge-native-', dir=temp_parent) as directory:
        workspace = Path(directory)
        wrapper = workspace / 'retroarch-wrapper.sh'
        wrapper.write_text(f'#!/bin/sh\nexec {shlex.quote(str(executable.resolve()))} --max-frames={FRAMES} "$@"\n')
        wrapper.chmod(0o700)
        data = workspace / 'data'
        env = {**os.environ, 'RETROBRIDGE_TOKEN': token}
        counters = []
        for boot in (1, 2):
            result = subprocess.run([*companion_command(), url, game['Id'], str(wrapper), str(core.resolve()), str(data)],
                                    env=env, capture_output=True, text=True, timeout=180)
            if result.returncode:
                raise AssertionError(f'Launch {boot} failed ({result.returncode}): {result.stderr.strip()[-500:]}')
            save, counter = read_save(data)
            counters.append(counter)
        if counters != [1, 2]:
            raise AssertionError(f'Expected save counter 1 then 2, observed {counters}')
        return {'frames_per_boot': FRAMES, 'save_counters': counters, 'save_name': save.name, 'core': core.name}


if __name__ == '__main__':
    # Container entry point; the token arrives via RETROBRIDGE_TOKEN, never argv.
    import json
    import sys
    if len(sys.argv) != 5 or not os.environ.get('RETROBRIDGE_TOKEN'):
        sys.exit('usage: RETROBRIDGE_TOKEN=... native_smoke.py <url> <game-id> <retroarch> <core>')
    result = verify_native(sys.argv[1], os.environ['RETROBRIDGE_TOKEN'], {'Id': sys.argv[2]}, Path(sys.argv[3]), Path(sys.argv[4]))
    print(json.dumps(result))
