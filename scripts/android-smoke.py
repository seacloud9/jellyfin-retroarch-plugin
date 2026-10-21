"""End-to-end Android companion test on a connected emulator/device against an isolated real Jellyfin container.

Preconditions on the device: RetroArch for Android installed, opened once, with the Nestopia core installed
(see docs/mobile.md). Builds nothing; run `./gradlew :app:assembleDebug` in android/ first.
Pass --play-page to also tap Play in Android Chrome (coordinates assume a 1080x2400 screen).
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import secrets
import shlex
import subprocess
import sys
import tempfile
import time
import uuid

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / 'scripts'))
spec = importlib.util.spec_from_file_location('smoke', REPO / 'scripts/jellyfin-smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)

APK = REPO / 'android/app/build/outputs/apk/debug/app-debug.apk'
APP = 'org.retrobridge.android'
ADB = str(Path(os.environ.get('ANDROID_HOME', Path.home() / 'Library/Android/sdk')) / 'platform-tools/adb')


def adb(*args, check=True):
    result = subprocess.run([ADB, *args], capture_output=True, text=True, timeout=120)
    if check and result.returncode:
        raise RuntimeError(f'adb {args[0]} failed: {result.stderr.strip()}')
    return result.stdout.strip()


def wait_log(pattern, timeout=60):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for line in adb('logcat', '-d', '-s', 'RetroBridge').splitlines():
            if pattern in line:
                return line
        time.sleep(1)
    raise AssertionError(f'Timed out waiting for log: {pattern}')


def view(link, *extra):
    # adb shell joins arguments into a device shell command; quote so '&' and friends reach the app intact.
    adb('shell', 'am start -a android.intent.action.VIEW -d ' + shlex.quote(link) + ''.join(' ' + shlex.quote(e) for e in extra))


def retroarch_running(package):
    return bool(adb('shell', 'pidof', package, check=False))


def save_state(game):
    path = adb('shell', f'find /sdcard/RetroArch/saves -name {game["Id"]}.srm', check=False).splitlines()
    if not path:
        return None, None
    size = int(adb('shell', 'stat', '-c', '%s', path[0]))
    counter = adb('shell', f'od -An -tu1 -j4 -N1 {path[0]}').strip() if size else None
    return path[0], (size, int(counter) if counter else None)


def play_once(package, link):
    """Open a launch link as a browser would, let the game boot, then leave so RetroArch exits and flushes SRAM."""
    adb('logcat', '-c')
    view(link)
    deadline = time.monotonic() + 60
    while not retroarch_running(package):
        if time.monotonic() > deadline:
            raise AssertionError('RetroArch did not start: ' + adb('logcat', '-d', '-s', 'RetroBridge'))
        time.sleep(1)
    time.sleep(8)
    adb('shell', 'input', 'keyevent', 'KEYCODE_HOME')
    deadline = time.monotonic() + 30
    while retroarch_running(package):
        if time.monotonic() > deadline:
            raise AssertionError('RetroArch did not exit')
        time.sleep(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--play-page', action='store_true')
    args = parser.parse_args()
    if not APK.is_file():
        parser.error('Build the APK first: (cd android && ./gradlew :app:assembleDebug)')
    installed = set(adb('shell', 'pm', 'list', 'packages').split())
    # Same preference order as the app; exact names, since com.retroarch is a prefix of the others.
    package = next((p for p in ('com.retroarch', 'com.retroarch.aarch64', 'com.retroarch.ra32') if f'package:{p}' in installed), None)
    if not package:
        parser.error('Install RetroArch for Android on the device first')
    summary = {'device': adb('shell', 'getprop', 'ro.product.model'), 'android': adb('shell', 'getprop', 'ro.build.version.release'), 'retroarch': package}
    container = 'retrobridge-android-' + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix='retrobridge-android-') as directory:
        workspace = Path(directory)
        plugin = workspace / 'config/plugins/RetroBridge'
        plugin.mkdir(parents=True)
        import zipfile
        with zipfile.ZipFile(REPO / 'artifacts/RetroBridge-0.1.0.zip') as package_zip:
            for name in ('Jellyfin.Plugin.RetroBridge.dll', 'RetroBridge.Domain.dll'):
                (plugin / name).write_bytes(package_zip.read(name))
        smoke.write_fixture(workspace / 'games/save-counter.nes')
        port = None
        try:
            smoke.docker('run', '-d', '--name', container, '--label', 'org.retrobridge.smoke=true', '-p', '127.0.0.1::8096',
                         '--mount', f'type=bind,src={workspace / "config"},dst=/config',
                         '--mount', f'type=bind,src={workspace / "games"},dst=/games,readonly', smoke.IMAGE)
            url = smoke.host_url(container)
            port = url.rsplit(':', 1)[1]
            server = smoke.Server(url)
            server.ready()
            admin_password, player_password = secrets.token_urlsafe(18), secrets.token_urlsafe(18)
            server.request('GET', '/Startup/User')
            server.request('POST', '/Startup/User', {'Name': 'admin', 'Password': admin_password})
            server.request('POST', '/Startup/Complete')
            admin = server.login('admin', admin_password)
            user = server.request('POST', '/Users/New', {'Name': 'player', 'Password': player_password}, admin)
            config_path = '/Plugins/' + smoke.PLUGIN_ID + '/Configuration'
            config = server.request('GET', config_path, token=admin)
            config.update(GameRoots=['/games'], PlayerUserIds=[user['Id']])
            server.request('POST', config_path, config, admin)
            server.request('POST', '/RetroBridge/Scan?wait=true', token=admin)
            game = server.request('GET', '/RetroBridge/Games', token=admin)[0]
            player = server.login('player', player_password)

            # The device reaches the container through its own loopback, which the app allows over HTTP.
            adb('reverse', f'tcp:{port}', f'tcp:{port}')
            adb('install', '-r', str(APK))
            adb('shell', 'pm', 'clear', APP)
            adb('shell', 'appops', 'set', APP, 'MANAGE_EXTERNAL_STORAGE', 'allow')
            # A RetroArch left open by an aborted run would resume instead of booting fresh.
            adb('shell', 'am', 'force-stop', package)
            adb('shell', 'rm', '-rf', '/sdcard/RetroBridge', f'/sdcard/RetroArch/saves/Nestopia/{game["Id"]}.srm')
            adb('logcat', '-c')
            adb('shell', 'am', 'start', '-n', f'{APP}/.MainActivity', '--es', 'server', f'http://127.0.0.1:{port}/')
            code = wait_log('Quick Connect code: ').split('Quick Connect code: ')[1].strip()
            server.request('POST', '/QuickConnect/Authorize?code=' + code, token=player)
            device = wait_log('Paired device ').split('Paired device ')[1].strip()
            devices = server.request('GET', '/RetroBridge/Devices', token=player)
            if [d['Id'] for d in devices] != [device]:
                raise AssertionError(f'Paired Android device not listed: {devices}')
            summary['paired'] = True

            def ticket():
                result = server.request('POST', '/RetroBridge/Games/' + game['Id'] + '/LaunchTicket', {'DeviceId': device}, player)
                if result['Delivered']:
                    raise AssertionError('Android device unexpectedly marked as listening')
                return 'retrobridge://launch?ticket=' + result['Ticket']

            counters = []
            for _ in range(2):
                play_once(package, ticket())
                counters.append(save_state(game)[1])
            if counters != [(8192, 1), (8192, 2)]:
                raise AssertionError(f'Expected save counter 1 then 2, observed {counters}')
            summary['launch_and_save_persistence'] = counters

            # Simulate the truncation seen once during the feasibility spike: the next launch must restore a backup.
            path, _ = save_state(game)
            adb('shell', f'truncate -s 0 {path}')
            play_once(package, ticket())
            wait_log('Restored empty')
            restored = save_state(game)[1]
            if restored != (8192, 2):
                raise AssertionError(f'Expected the restored save to boot as counter 2 (backup 1, +1 boot), observed {restored}')
            summary['empty_save_restored'] = True

            for name, link in [('replayed', None), ('hostile', 'retrobridge://launch?ticket=' + 'A' * 30 + '&server=https://evil')]:
                if name == 'replayed':
                    link = ticket()
                    view(link)
                    deadline = time.monotonic() + 60
                    while not retroarch_running(package):
                        if time.monotonic() > deadline:
                            raise AssertionError('Replay setup launch did not start')
                        time.sleep(1)
                    time.sleep(5)
                    adb('shell', 'input', 'keyevent', 'KEYCODE_HOME')
                    while retroarch_running(package):
                        time.sleep(1)
                adb('logcat', '-c')
                view(link)
                expected = 'invalid or expired' if name == 'replayed' else 'Not a valid RetroBridge launch link'
                wait_log(expected)
                if retroarch_running(package):
                    raise AssertionError(f'{name} link started RetroArch')
            summary['replayed_and_hostile_links_rejected'] = True

            if args.play_page:
                adb('logcat', '-c')
                helper = workspace / 'rb-session.html'
                helper.write_text('<!doctype html><meta charset=utf-8><script>localStorage.setItem("jellyfin_credentials", JSON.stringify({Servers:[{AccessToken:'
                                  + json.dumps(player) + ',UserId:' + json.dumps(user['Id']) + ',DateLastAccessed:Date.now()}]}));location.replace("/RetroBridge/Play");</script>')
                smoke.docker('cp', str(helper), container + ':/jellyfin/jellyfin-web/rb-session.html')
                before = save_state(game)[1][1]
                adb('shell', 'am', 'start', '-a', 'android.intent.action.VIEW', '-d', f'http://127.0.0.1:{port}/web/rb-session.html', 'com.android.chrome')
                time.sleep(10)
                adb('shell', 'input', 'tap', '918', '1342')
                deadline = time.monotonic() + 60
                while not retroarch_running(package):
                    if time.monotonic() > deadline:
                        raise AssertionError('Tapping Play did not start RetroArch: ' + adb('logcat', '-d', '-s', 'RetroBridge'))
                    time.sleep(1)
                time.sleep(8)
                adb('shell', 'input', 'keyevent', 'KEYCODE_HOME')
                while retroarch_running(package):
                    time.sleep(1)
                if save_state(game)[1][1] != before + 1:
                    raise AssertionError('Play page launch did not boot the game')
                summary['play_page_tap'] = True
        finally:
            if port:
                adb('reverse', '--remove', f'tcp:{port}', check=False)
            smoke.docker('rm', '-f', container)
            adb('shell', 'pm', 'clear', APP, check=False)
    output = REPO / 'artifacts/android-smoke.json'
    output.write_text(json.dumps(summary, indent=2) + '\n')
    print('PASS: Android pairing, ticket launch, save persistence, backup restore, replay/hostile rejection')
    print('Verification record: ' + str(output))


if __name__ == '__main__':
    main()
