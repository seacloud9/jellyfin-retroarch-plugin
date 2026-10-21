"""Test the packaged plugin in an isolated, real Jellyfin Docker server.

Only creates/removes a uniquely named test container and a temporary workspace.
Generated credentials stay in memory. No existing Jellyfin installation is used.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zipfile

from fixture_rom import write_fixture

REPO = Path(__file__).resolve().parents[1]
PLUGIN_ID = '4be0a14e-f03e-4e52-b702-3e38b8cedf17'
# Multi-architecture manifest for the tested Jellyfin 12.1 release.
LINUX_NATIVE_IMAGE = 'retrobridge-native-linux:local'
IMAGE = 'jellyfin/jellyfin:12.1@sha256:78d3ea1207d1322471fcac39a614f004f2ccf7e878f95ab2977d752f07e4dd7e'


def docker(*args, env=None, timeout=180):
    result = subprocess.run(['docker', *args], capture_output=True, text=True, timeout=timeout, env=env)
    if result.returncode:
        raise RuntimeError(f'docker {args[0]} failed: {result.stderr.strip()}')
    return result.stdout.strip()


def host_url(container):
    port = json.loads(docker('inspect', container))[0]['NetworkSettings']['Ports']['8096/tcp'][0]['HostPort']
    return 'http://127.0.0.1:' + port


def verify_native_linux_docker(container, admin, game):
    """Run native_smoke.py in a headless Linux RetroArch container sharing Jellyfin's network."""
    docker('build', '-q', '-t', LINUX_NATIVE_IMAGE, '-f', str(REPO / 'docker/native-linux.Dockerfile'), str(REPO / 'docker'), timeout=900)
    # Sharing the network namespace keeps Jellyfin on loopback, which the companion allows over HTTP.
    output = docker('run', '--rm', '--label', 'org.retrobridge.smoke=true', '--network', 'container:' + container,
                    '-e', 'RETROBRIDGE_TOKEN', '--mount', f'type=bind,src={REPO},dst=/repo,readonly', LINUX_NATIVE_IMAGE,
                    'python3', '/repo/scripts/native_smoke.py', 'http://127.0.0.1:8096', game['Id'],
                    '/usr/bin/retroarch', '/opt/cores/nestopia_libretro.so',
                    env={**os.environ, 'RETROBRIDGE_TOKEN': admin}, timeout=600)
    return {'platform': 'linux-docker', **json.loads(output.splitlines()[-1])}


class Server:
    def __init__(self, url):
        self.url = url
        self.client = 'MediaBrowser Client="RetroBridge Smoke", Device="Integration", DeviceId="' + uuid.uuid4().hex + '", Version="0.1.0"'

    def request(self, method, path, data=None, token=None, expected=(200, 204), raw=False):
        # Jellyfin reads the token from the Authorization header when one is present.
        headers = {'Authorization': self.client + (f', Token="{token}"' if token else '')}
        if data is not None:
            headers['Content-Type'] = 'application/json'
        request = urllib.request.Request(self.url + path, data=None if data is None else json.dumps(data).encode(), headers=headers, method=method)
        try:
            response = urllib.request.urlopen(request, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            payload = response.read()
            if response.status not in expected:
                # Never print response bodies; authentication responses can contain credentials.
                raise AssertionError(f'{method} {path}: expected {expected}, received {response.status}')
            if raw:
                return payload
            return json.loads(payload) if payload and response.headers.get('Content-Type', '').startswith('application/json') else None

    def ready(self):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            try:
                info = self.request('GET', '/System/Info/Public')
                # Early startup can answer with JSON that lacks server details.
                if info and 'Version' in info:
                    return info
            except (OSError, AssertionError):
                pass
            time.sleep(1)
        raise TimeoutError('Jellyfin did not become ready within 120 seconds')

    def login(self, name, password):
        return self.request('POST', '/Users/AuthenticateByName', {'Username': name, 'Pw': password})['AccessToken']


LAUNCHER = REPO / 'src/RetroBridge.Launcher/bin/Release/net10.0/RetroBridge.Launcher.dll'


def companion(*args, timeout=60):
    return subprocess.run(['dotnet', str(LAUNCHER), *args], capture_output=True, text=True, timeout=timeout)


def verify_one_click(server, ordinary, user_id, device, data, stub_core, os_handler, summary):
    """Play page → ticket → retrobridge:// link → companion, using the paired device and local settings."""
    devices = server.request('GET', '/RetroBridge/Devices', token=ordinary)
    if [d['Id'] for d in devices] != [device] or any('Token' in key for d in devices for key in d):
        raise AssertionError('Devices endpoint must list exactly the paired device and no tokens')
    configured = companion('configure', '--retroarch', '/usr/bin/true', '--core', 'nes=' + str(stub_core), '--data', str(data))
    if configured.returncode:
        raise AssertionError('configure failed: ' + configured.stderr.strip())
    browser = REPO / 'tests/browser'
    if not (browser / 'node_modules/jsdom').is_dir():
        raise RuntimeError('Run npm ci in tests/browser first')
    page = subprocess.run(['node', str(browser / 'play-page.js'), server.url, user_id], capture_output=True, text=True, timeout=60,
                          env={**os.environ, 'RETROBRIDGE_TOKEN': ordinary})
    if page.returncode:
        raise AssertionError('Play page failed: ' + page.stderr.strip()[-300:])
    clicked = json.loads(page.stdout)
    if clicked['devices'] != [device] or clicked['games'] != ['save-counter'] or clicked['errors']:
        raise AssertionError(f'Unexpected play page state: {clicked}')
    log = data / 'launcher.log'
    if os_handler:
        # Real OS dispatch: the registered handler receives the link exactly as a browser would hand it over.
        registered = companion('register', '--data', str(data))
        if registered.returncode:
            raise AssertionError('register failed: ' + registered.stderr.strip())
        try:
            opener = ['/usr/bin/open'] if sys.platform == 'darwin' else ['xdg-open']
            subprocess.run([*opener, clicked['link']], check=True, timeout=30)
            deadline = time.monotonic() + 60
            while 'RetroArch exited with code 0' not in (log.read_text() if log.exists() else ''):
                if time.monotonic() > deadline:
                    raise AssertionError('OS link handler did not launch: ' + (log.read_text()[-300:] if log.exists() else 'no log'))
                time.sleep(1)
        finally:
            companion('unregister')
        summary['os_link_handler'] = sys.platform
    else:
        opened = companion('open', clicked['link'], '--data', str(data))
        if opened.returncode or 'RetroArch exited with code 0' not in log.read_text():
            raise AssertionError('open failed: ' + log.read_text()[-300:])
    if companion('open', clicked['link'], '--data', str(data)).returncode != 1:
        raise AssertionError('A clicked link must not launch twice')
    summary['one_click_play'] = True

    def play_page(*flags):
        result = subprocess.run(['node', str(browser / 'play-page.js'), server.url, user_id, *flags], capture_output=True, text=True,
                                timeout=60, env={**os.environ, 'RETROBRIDGE_TOKEN': ordinary})
        if result.returncode:
            raise AssertionError('Play page failed: ' + result.stderr.strip()[-300:])
        return json.loads(result.stdout)

    # A phone must not get a dead link when nothing is listening.
    idle = play_page('--mobile')
    if idle['link'] or 'not listening' not in idle['status']:
        raise AssertionError(f'Phone without a listener: {idle}')
    listener = subprocess.Popen(['dotnet', str(LAUNCHER), 'listen', '--data', str(data)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    try:
        deadline = time.monotonic() + 30
        while not all(d['Listening'] for d in server.request('GET', '/RetroBridge/Devices', token=ordinary)):
            if time.monotonic() > deadline or listener.poll() is not None:
                raise AssertionError('Listener never checked in')
            time.sleep(1)
        remote = play_page('--mobile')
        if remote['link'] or not remote['status'].startswith('Sent to'):
            raise AssertionError(f'Phone launch was not delivered remotely: {remote}')
        output = ''
        deadline = time.monotonic() + 30
        while 'RetroArch exited with code 0' not in output:
            if time.monotonic() > deadline:
                raise AssertionError('Listener did not launch the game: ' + output[-300:])
            output += listener.stdout.readline()
    finally:
        listener.terminate()
        listener.wait(timeout=10)
    summary['remote_launch_from_phone'] = True


def verify_pairing(server, admin, ordinary, user_id, game, config, config_path, summary, os_handler=False):
    """Quick Connect pairing, ticket issue/redeem, replay/device binding, and access revocation."""
    if os.name == 'nt':
        summary['pairing_and_tickets'] = 'skipped on Windows (process stub is Unix-only)'
        return
    if not server.request('GET', '/QuickConnect/Enabled'):
        system = server.request('GET', '/System/Configuration', token=admin)
        system['QuickConnectAvailable'] = True
        server.request('POST', '/System/Configuration', system, admin)
    config['PlayerUserIds'] = [user_id]
    server.request('POST', config_path, config, admin)
    if len(server.request('GET', '/RetroBridge/Games', token=ordinary)) != 1:
        raise AssertionError('Allowed player cannot list games')
    server.request('POST', '/RetroBridge/Scan', token=ordinary, expected=(403,))

    with tempfile.TemporaryDirectory(prefix='retrobridge-pair-') as directory:
        data = Path(directory) / 'data'
        pairing = subprocess.Popen(['dotnet', str(LAUNCHER), 'pair', server.url, str(data)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            line = pairing.stdout.readline()
            if not line.startswith('Quick Connect code: '):
                raise AssertionError('Companion did not print a Quick Connect code: ' + pairing.stderr.read()[-300:])
            code = line.split(': ', 1)[1].strip()
            server.request('POST', '/QuickConnect/Authorize?code=' + code, token=ordinary)
            output, errors = pairing.communicate(timeout=60)
        finally:
            if pairing.poll() is None:
                pairing.kill()
        if pairing.returncode:
            raise AssertionError('Pairing failed: ' + errors.strip()[-300:])
        stored = json.loads((data / 'pairing.json').read_text())
        if (data / 'pairing.json').stat().st_mode & 0o077:
            raise AssertionError('Pairing file is readable by other users')
        device = stored['DeviceId']

        # /usr/bin/true stands in for RetroArch; native emulation is covered separately.
        stub_core = Path(directory) / 'core.so'
        stub_core.write_bytes(b'stub')
        def ticket(for_device=device):
            return server.request('POST', '/RetroBridge/Games/' + game['Id'] + '/LaunchTicket', {'DeviceId': for_device}, ordinary)['Ticket']
        def launch(value):
            return companion('launch', str(data), value, '/usr/bin/true', str(stub_core))

        first = ticket()
        result = launch(first)
        if result.returncode or 'Starting save-counter' not in result.stdout:
            raise AssertionError('Ticket launch failed: ' + result.stderr.strip()[-300:])
        cached = data / 'cache' / (game['Id'] + '.nes')
        if hashlib.sha256(cached.read_bytes()).hexdigest() != game['Sha256']:
            raise AssertionError('Ticket launch did not cache verified content')
        for name, value in [('replayed', first), ('other-device', ticket('retrobridge-not-this-device'))]:
            rejected = launch(value)
            if rejected.returncode != 1 or 'invalid or expired' not in rejected.stderr:
                raise AssertionError(f'{name} ticket was not rejected')
        verify_one_click(server, ordinary, user_id, device, data, stub_core, os_handler, summary)

        config['PlayerUserIds'] = []
        server.request('POST', config_path, config, admin)
        server.request('POST', '/RetroBridge/Games/' + game['Id'] + '/LaunchTicket', {'DeviceId': device}, ordinary, expected=(403,))
        server.request('GET', '/RetroBridge/Games/' + game['Id'] + '/Content', token=stored['Token'], expected=(403,))
    summary['pairing_and_tickets'] = True


def verify_server(server, container, fixtures, summary, os_handler=False):
    info = server.ready()
    if info['Version'] not in ('12.1', '12.1.0'):
        raise AssertionError(f'Unexpected Jellyfin version: {info["Version"]}')
    summary['jellyfin_version'] = info['Version']
    server.request('GET', '/Startup/User')
    admin_password = secrets.token_urlsafe(24)
    server.request('POST', '/Startup/User', {'Name': 'retrobridge-admin', 'Password': admin_password})
    server.request('POST', '/Startup/Complete')
    admin = server.login('retrobridge-admin', admin_password)
    plugins = server.request('GET', '/Plugins', token=admin)
    plugin = next((p for p in plugins if p['Id'].replace('-', '') == PLUGIN_ID.replace('-', '')), None)
    if not plugin or plugin.get('Status') != 'Active':
        raise AssertionError('RetroBridge did not load as an active Jellyfin plugin')
    summary['plugin_loaded'] = True
    page = server.request('GET', '/web/ConfigurationPage?name=RetroBridge', token=admin, raw=True)
    if b'GameRoots' not in page or b'RetroBridge/Games' not in page:
        raise AssertionError('Embedded configuration page is missing')

    config_path = '/Plugins/' + PLUGIN_ID + '/Configuration'
    config = server.request('GET', config_path, token=admin)
    config['GameRoots'] = ['/games']
    server.request('POST', config_path, config, admin)
    scan = server.request('POST', '/RetroBridge/Scan', token=admin)
    if not scan['Published'] or scan['Count'] != 1:
        raise AssertionError('Fixture scan failed')
    games = server.request('GET', '/RetroBridge/Games?search=save-counter&platform=nes', token=admin)
    if len(games) != 1:
        raise AssertionError('Expected exactly one filtered fixture')
    game = games[0]
    if 'Root' in game or 'RelativePath' in game:
        raise AssertionError('Catalog metadata leaked a server path')
    expected = (fixtures / 'save-counter.nes').read_bytes()
    if game['Sha256'] != hashlib.sha256(expected).hexdigest():
        raise AssertionError('Catalog checksum does not match fixture')
    route = '/RetroBridge/Games/' + game['Id'] + '/Content'
    if server.request('GET', route, token=admin, raw=True) != expected:
        raise AssertionError('Downloaded fixture differs from source')
    # Header shape sent by the companion (no server-specific client fields).
    launcher_style = urllib.request.Request(server.url + route, headers={'Authorization': 'MediaBrowser Client="RetroBridge", Device="smoke", DeviceId="retrobridge-launcher", Version="0.1.0", Token="' + admin + '"'})
    try:
        with urllib.request.urlopen(launcher_style, timeout=20) as response:
            if response.read() != expected:
                raise AssertionError('Launcher-style download differs from source')
    except urllib.error.HTTPError as error:
        raise AssertionError(f'Launcher-style authorization rejected: {error.code}') from None
    summary['launcher_token_header'] = True
    server.request('POST', '/RetroBridge/Scan', token=admin)
    if server.request('GET', '/RetroBridge/Games', token=admin) != games:
        raise AssertionError('Rescan changed identity or duplicated content')
    summary['scan_filter_download_rescan'] = True

    password = secrets.token_urlsafe(24)
    user = server.request('POST', '/Users/New', {'Name': 'retrobridge-user', 'Password': password}, admin)
    if user['Policy']['IsAdministrator']:
        raise AssertionError('Test user must not be an administrator')
    ordinary = server.login('retrobridge-user', password)
    for method, endpoint in [('GET', '/RetroBridge/Games'), ('POST', '/RetroBridge/Scan'), ('GET', route)]:
        server.request(method, endpoint, expected=(401,))
        server.request(method, endpoint, token=ordinary, expected=(403,))
    summary['real_authentication_boundaries'] = True
    verify_pairing(server, admin, ordinary, user['Id'], game, config, config_path, summary, os_handler)

    config['GameRoots'] = []
    server.request('POST', config_path, config, admin)
    server.request('GET', route, token=admin, expected=(404,))
    config['GameRoots'] = ['/games']
    server.request('POST', config_path, config, admin)
    summary['root_revocation'] = True

    docker('restart', container)
    # A restart reassigns the ephemeral host port.
    server.url = host_url(container)
    server.ready()
    admin = server.login('retrobridge-admin', admin_password)
    if server.request('GET', config_path, token=admin)['GameRoots'] != ['/games']:
        raise AssertionError('Plugin configuration did not survive restart')
    if server.request('GET', '/RetroBridge/Games', token=admin) != games:
        raise AssertionError('Catalog was not restored from its snapshot after restart')
    summary['catalog_persisted_across_restart'] = True
    server.request('POST', '/RetroBridge/Scan', token=admin)
    if server.request('GET', '/RetroBridge/Games', token=admin) != games:
        raise AssertionError('Content identity did not survive restart and rescan')
    summary['restart_configuration_identity'] = True
    return admin, game


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native-executable', type=Path, help='Also launch a real RetroArch executable twice (Unix)')
    parser.add_argument('--native-core', type=Path, help='Installed Nestopia core for the native save test')
    parser.add_argument('--os-link-handler', action='store_true', help='Register the real retrobridge:// handler for this user during the test, then unregister it')
    parser.add_argument('--native-linux-docker', action='store_true', help='Also run the native save test in a headless Linux RetroArch container')
    parser.add_argument('--native-temp-parent', type=Path, help='Directory accessible to RetroArch; needed for sandboxed macOS builds')
    args = parser.parse_args()
    if bool(args.native_executable) != bool(args.native_core):
        parser.error('--native-executable and --native-core must be supplied together')
    archive = REPO / 'artifacts/RetroBridge-0.1.0.zip'
    if not archive.is_file():
        parser.error('Build and run scripts/package-plugin.py first')
    docker('info', '--format', '{{.ServerVersion}}')
    container = 'retrobridge-smoke-' + uuid.uuid4().hex[:12]
    summary = {'image': IMAGE, 'native_emulation': 'not requested'}
    with tempfile.TemporaryDirectory(prefix='retrobridge-jellyfin-') as directory:
        workspace = Path(directory)
        config = workspace / 'config'
        plugin = config / 'plugins/RetroBridge'
        plugin.mkdir(parents=True)
        with zipfile.ZipFile(archive) as package:
            for name in ('Jellyfin.Plugin.RetroBridge.dll', 'RetroBridge.Domain.dll'):
                (plugin / name).write_bytes(package.read(name))
        fixtures = workspace / 'games'
        write_fixture(fixtures / 'save-counter.nes')
        started = False
        try:
            # On Linux, bind-mounted files would otherwise be root-owned and block temp cleanup.
            user = ['--user', f'{os.getuid()}:{os.getgid()}'] if sys.platform.startswith('linux') else []
            docker('run', '-d', '--name', container, '--label', 'org.retrobridge.smoke=true', *user, '-p', '127.0.0.1::8096',
                   '--mount', f'type=bind,src={config},dst=/config',
                   '--mount', f'type=bind,src={fixtures},dst=/games,readonly', IMAGE)
            started = True
            server = Server(host_url(container))
            print('Testing real Jellyfin at ' + server.url, flush=True)
            admin, game = verify_server(server, container, fixtures, summary, args.os_link_handler)
            if args.native_executable:
                from native_smoke import verify_native
                summary['native_emulation'] = verify_native(server.url, admin, game, args.native_executable, args.native_core, args.native_temp_parent)
            if args.native_linux_docker:
                summary['native_emulation_linux'] = verify_native_linux_docker(container, admin, game)
            output = REPO / 'artifacts/jellyfin-smoke.json'
            output.write_text(json.dumps(summary, indent=2) + '\n')
            print('PASS: packaged plugin loads, real authentication, scan/filter/download, root revocation, restart and identity', flush=True)
            print('Verification record: ' + str(output))
        finally:
            if started:
                docker('rm', '-f', container)


if __name__ == '__main__':
    main()
