"""Install RetroBridge into a fresh Jellyfin container through Jellyfin's own plugin repository flow.

Default: builds artifacts/release with a local base URL and serves it to the container (needs the plugin built).
--manifest-url URL tests a published repository instead (e.g. the GitHub release manifest).
"""
import argparse
import functools
import http.server
import importlib.util
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest-url')
    parser.add_argument('--tag', default='v0.1.0')
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
            smoke.docker('run', '-d', '--name', container, '--label', 'org.retrobridge.smoke=true', '-p', '127.0.0.1::8096',
                         '--add-host', 'host.docker.internal:host-gateway',
                         '--mount', f'type=bind,src={directory},dst=/config', smoke.IMAGE)
            server = smoke.Server(smoke.host_url(container))
            server.ready()
            password = secrets.token_urlsafe(18)
            server.request('GET', '/Startup/User')
            server.request('POST', '/Startup/User', {'Name': 'admin', 'Password': password})
            server.request('POST', '/Startup/Complete')
            admin = server.login('admin', password)
            if any(p['Name'] == 'RetroBridge' for p in server.request('GET', '/Plugins', token=admin)):
                raise AssertionError('Plugin unexpectedly preinstalled')
            server.request('POST', '/Repositories', [{'Name': 'RetroBridge', 'Url': manifest_url, 'Enabled': True}], admin)
            package = next((p for p in server.request('GET', '/Packages', token=admin) if p['name'] == 'RetroBridge'), None)
            if not package or package['versions'][0]['version'] != version:
                raise AssertionError(f'RetroBridge {version} not listed in the catalog: {package}')
            server.request('POST', f"/Packages/Installed/RetroBridge?assemblyGuid={package['guid']}&version={version}", token=admin)
            deadline = time.monotonic() + 120
            while not any(p['Name'] == 'RetroBridge' for p in server.request('GET', '/Plugins', token=admin)):
                if time.monotonic() > deadline:
                    raise AssertionError('Install did not complete')
                time.sleep(2)
            smoke.docker('restart', container)
            server.url = smoke.host_url(container)
            server.ready()
            admin = server.login('admin', password)
            plugin = next(p for p in server.request('GET', '/Plugins', token=admin) if p['Name'] == 'RetroBridge')
            if plugin['Status'] != 'Active' or plugin['Version'] != version:
                raise AssertionError(f'Installed plugin not active at {version}: {plugin}')
            server.request('GET', '/RetroBridge/Games', token=admin)
        finally:
            smoke.docker('rm', '-f', container)
            if httpd:
                httpd.shutdown()
    print(f'PASS: Jellyfin installed RetroBridge {version} from {manifest_url} via its plugin repository and loaded it')


if __name__ == '__main__':
    main()
