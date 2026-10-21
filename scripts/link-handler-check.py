"""Register and unregister the per-user retrobridge:// handler on this OS and inspect what was written.

Uses placeholder pairing/settings in a temporary data directory; nothing is launched.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

REPO = Path(__file__).resolve().parents[1]
LAUNCHER = REPO / 'src/RetroBridge.Launcher/bin/Release/net10.0/RetroBridge.Launcher.dll'


def companion(*args):
    result = subprocess.run(['dotnet', str(LAUNCHER), *args], capture_output=True, text=True, timeout=120)
    if result.returncode:
        raise AssertionError(f'{args[0]} failed: {result.stdout} {result.stderr}')
    return result.stdout


def registered(data):
    if sys.platform == 'win32':
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Classes\retrobridge\shell\open\command') as key:
                return winreg.QueryValue(key, None)
        except FileNotFoundError:
            return None
    if sys.platform == 'darwin':
        plist = Path.home() / 'Applications/RetroBridge Launcher.app/Contents/Info.plist'
        return plist.read_text() if plist.exists() else None
    base = Path(os.environ.get('XDG_DATA_HOME') or Path.home() / '.local/share')
    desktop = base / 'applications/retrobridge-launcher.desktop'
    return desktop.read_text() if desktop.exists() else None


def main():
    if registered(None) is not None:
        sys.exit('A retrobridge:// handler is already registered for this user; refusing to overwrite it.')
    with tempfile.TemporaryDirectory(prefix='retrobridge-link-') as directory:
        data = Path(directory) / 'data'
        data.mkdir()
        (data / 'pairing.json').write_text(json.dumps({'Server': 'https://jellyfin.invalid/', 'DeviceId': 'retrobridge-check', 'Token': 'placeholder'}))
        (data / 'settings.json').write_text(json.dumps({'RetroArch': sys.executable, 'Cores': {'nes': sys.executable}}))
        print(companion('register', '--data', str(data)).strip())
        try:
            entry = registered(data)
            if not entry or 'retrobridge' not in entry:
                raise AssertionError('Handler registration not found')
            # Compare the unique temp folder name: Windows may report 8.3 short paths for the same folder.
            if sys.platform != 'darwin' and (data.parent.name not in entry or ' open ' not in entry):
                raise AssertionError('Handler command does not pass the link to "open" with the data directory: ' + entry)
        finally:
            print(companion('unregister').strip())
        if registered(data) is not None:
            raise AssertionError('Handler still registered after unregister')
    print('PASS: per-user link handler registered with the expected command and removed cleanly')


if __name__ == '__main__':
    main()
