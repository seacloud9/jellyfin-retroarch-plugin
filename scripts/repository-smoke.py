"""Install RetroBridge into a fresh Jellyfin container through Jellyfin's own plugin repository flow.

Default: builds artifacts/release with a local base URL and serves it to the container (needs the plugin built).
--manifest-url URL tests a published repository instead (e.g. the GitHub release manifest).
"""
import argparse
import functools
import http.server
import importlib.util
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import threading
import time
import uuid

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / 'scripts'))
spec = importlib.util.spec_from_file_location('smoke', REPO / 'scripts/jellyfin-smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
from fixture_rom import write_fixture  # noqa: E402


def install(server, admin, manifest_url, version):
    """Point Jellyfin at a repository and install RetroBridge from it (the latest listed version when version is None)."""
    server.request('POST', '/Repositories', [{'Name': 'RetroBridge', 'Url': manifest_url, 'Enabled': True}], admin)
    package = next((p for p in server.request('GET', '/Packages', token=admin) if p['name'] == 'RetroBridge'), None)
    if not package or (version and package['versions'][0]['version'] != version):
        raise AssertionError(f'RetroBridge {version or ""} not listed in the catalog: {package}')
    version = package['versions'][0]['version']
    server.request('POST', f"/Packages/Installed/RetroBridge?assemblyGuid={package['guid']}&version={version}", token=admin)
    deadline = time.monotonic() + 120
    while not any(p['Name'] == 'RetroBridge' and p['Version'] == version for p in server.request('GET', '/Plugins', token=admin)):
        if time.monotonic() > deadline:
            raise AssertionError(f'Install of {version} did not complete')
        time.sleep(2)
    return version


def restart(server, container, password):
    smoke.docker('restart', container)
    server.url = smoke.host_url(container)
    server.ready()
    return server, server.login('admin', password)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest-url')
    parser.add_argument('--upgrade-from', help='Install this older repository manifest first, configure and scan, then upgrade')
    from version import VERSION
    parser.add_argument('--tag', default='v' + VERSION)
    args = parser.parse_args()
    httpd = None
    manifest_url = args.manifest_url
    if not manifest_url:
        # Serve the release folder on all interfaces only for the container's benefit, on a random port, for this run.
        httpd = http.server.ThreadingHTTPServer(('0.0.0.0', 0), functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(REPO / 'artifacts/release')))
        port = httpd.server_address[1]
        subprocess.run([sys.executable, str(REPO / 'scripts/build-release.py'), '--tag', args.tag,
                        '--base-url', f'http://host.docker.internal:{port}'], check=True, stdout=subprocess.DEVNULL)
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        manifest_url = f'http://host.docker.internal:{port}/manifest.json'
    container = 'retrobridge-repo-' + uuid.uuid4().hex[:12]
    version = args.tag.removeprefix('v') + '.0'
    with tempfile.TemporaryDirectory(prefix='retrobridge-repo-') as directory:
        try:
            # On Linux, bind-mounted files would otherwise be root-owned and block temp cleanup.
            user = ['--user', f'{os.getuid()}:{os.getgid()}'] if sys.platform.startswith('linux') else []
            games_dir = Path(directory) / 'games'
            write_fixture(games_dir / 'save-counter.nes')
            (Path(directory) / 'config').mkdir()
            smoke.docker('run', '-d', '--name', container, '--label', 'org.retrobridge.smoke=true', '-p', '127.0.0.1::8096', *user,
                         '--add-host', 'host.docker.internal:host-gateway',
                         '--mount', f'type=bind,src={directory}/config,dst=/config',
                         '--mount', f'type=bind,src={games_dir},dst=/games,readonly', smoke.IMAGE)
            server = smoke.Server(smoke.host_url(container))
            server.ready()
            password = secrets.token_urlsafe(18)
            server.request('GET', '/Startup/User')
            server.request('POST', '/Startup/User', {'Name': 'admin', 'Password': password})
            server.request('POST', '/Startup/Complete')
            admin = server.login('admin', password)
            if any(p['Name'] == 'RetroBridge' for p in server.request('GET', '/Plugins', token=admin)):
                raise AssertionError('Plugin unexpectedly preinstalled')
            games_before = None
            if args.upgrade_from:
                old = install(server, admin, args.upgrade_from, None)
                server, admin = restart(server, container, password)
                config_path = '/Plugins/' + smoke.PLUGIN_ID + '/Configuration'
                config = server.request('GET', config_path, token=admin)
                config.update(GameRoots=['/games'], PlayerUserIds=[])
                server.request('POST', config_path, config, admin)
                server.request('POST', '/RetroBridge/Scan?wait=true', token=admin)
                games_before = server.request('GET', '/RetroBridge/Games', token=admin)
                if len(games_before) != 1:
                    raise AssertionError(f'Old version did not index the fixture: {games_before}')
                print(f'Installed and configured RetroBridge {old}', flush=True)
            server.request('POST', '/Repositories', [{'Name': 'RetroBridge', 'Url': manifest_url, 'Enabled': True}], admin)
            install(server, admin, manifest_url, version)
            server, admin = restart(server, container, password)
            plugins = [p for p in server.request('GET', '/Plugins', token=admin) if p['Name'] == 'RetroBridge']
            active = [p for p in plugins if p['Status'] == 'Active']
            if len(active) != 1 or active[0]['Version'] != version:
                raise AssertionError(f'Expected exactly RetroBridge {version} active: {plugins}')
            games = server.request('GET', '/RetroBridge/Games', token=admin)
            if games_before is not None:
                config = server.request('GET', '/Plugins/' + smoke.PLUGIN_ID + '/Configuration', token=admin)
                if config['GameRoots'] != ['/games']:
                    raise AssertionError('Configuration lost in the upgrade')
                if [g['Id'] for g in games] != [g['Id'] for g in games_before]:
                    raise AssertionError('Catalog snapshot lost in the upgrade')
                print(f'Upgrade kept configuration and catalog ({len(games)} game)', flush=True)
        finally:
            smoke.docker('rm', '-f', container)
            if httpd:
                httpd.shutdown()
    print(f'PASS: Jellyfin installed RetroBridge {version} from {manifest_url} via its plugin repository and loaded it'
          + (f' (upgraded from {args.upgrade_from})' if args.upgrade_from else ''))


if __name__ == '__main__':
    main()
