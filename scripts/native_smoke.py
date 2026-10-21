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
    saves = [p for p in (data / 'saves').rglob('*.srm')]
    if len(saves) != 1:
        raise AssertionError(f'Expected one emulator save file, found {len(saves)}')
    raw = saves[0].read_bytes()
    if raw[:4] != b'RB01':
        raise AssertionError('Save file is missing the fixture signature')
    return saves[0], raw[4]


def server_counter(url, token, game_id):
    """Boot counter in the user's server copy of the save (0 when there is none), so runs can follow other devices."""
    import urllib.request
    import urllib.error
    request = urllib.request.Request(url.rstrip('/') + f'/RetroBridge/Games/{game_id}/Saves/{game_id}.srm',
                                     headers={'Authorization': f'MediaBrowser Client="RetroBridge Smoke", Device="Native", DeviceId="retrobridge-native-smoke", Version="0.1.0", Token="{token}"'})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            data = response.read()
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return 0
        raise
    if data[:4] != b'RB01':
        raise AssertionError('Server save is missing the fixture signature')
    return data[4]


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
        env = {**os.environ, 'RETROBRIDGE_TOKEN': token}
        # Two data directories stand in for two computers of the same user; server save sync carries progress between them.
        computer_a, computer_b = workspace / 'computer-a', workspace / 'computer-b'
        start = server_counter(url, token, game['Id'])
        counters = []
        for boot, data in enumerate((computer_a, computer_a, computer_b, computer_a), start=1):
            result = subprocess.run([*companion_command(), url, game['Id'], str(wrapper), str(core.resolve()), str(data)],
                                    env=env, capture_output=True, text=True, timeout=180)
            if result.returncode:
                raise AssertionError(f'Launch {boot} failed ({result.returncode}): {result.stderr.strip()[-500:]}')
            if 'Save sync skipped' in result.stderr:
                raise AssertionError(f'Save sync failed on launch {boot}: {result.stderr.strip()[-300:]}')
            save, counter = read_save(data)
            counters.append(counter)
        expected = [start + n for n in (1, 2, 3, 4)]
        if counters != expected:
            raise AssertionError(f'Expected save counter {expected} (A, A, B, A; server started at {start}), observed {counters}')
        if server_counter(url, token, game['Id']) != start + 4:
            raise AssertionError('Server save was not updated after the last launch')
        return {'frames_per_boot': FRAMES, 'save_counters': counters, 'server_counter_before': start, 'synced_between_computers': True,
                'save_name': save.name, 'core': core.name}


if __name__ == '__main__':
    # Container entry point; the token arrives via RETROBRIDGE_TOKEN, never argv.
    import json
    import sys
    if len(sys.argv) != 5 or not os.environ.get('RETROBRIDGE_TOKEN'):
        sys.exit('usage: RETROBRIDGE_TOKEN=... native_smoke.py <url> <game-id> <retroarch> <core>')
    result = verify_native(sys.argv[1], os.environ['RETROBRIDGE_TOKEN'], {'Id': sys.argv[2]}, Path(sys.argv[3]), Path(sys.argv[4]))
    print(json.dumps(result))
