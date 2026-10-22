"""In-browser play end to end in a real Jellyfin container, optionally driven in the iOS simulator's Safari.

The server downloads the real pinned emulator files. With --ios, each fixture game (NES via FCEUmm, Game Boy via mGBA)
is opened twice through the play page's #play= link in the booted simulator; the server's copy of the battery save
must reach counter 1 and then 2, proving the files, CSP, authenticated game download, and save sync in a real browser.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import time
import uuid
import zipfile

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / 'scripts'))
spec = importlib.util.spec_from_file_location('smoke', REPO / 'scripts/jellyfin-smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
from version import PLUGIN_ARCHIVE  # noqa: E402
from fixture_rom import write_fixture, write_gb_fixture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ios', action='store_true', help='Drive the booted iOS simulator (macOS only)')
    parser.add_argument('--port', type=int, default=8098)
    parser.add_argument('--keep-seconds', type=int, default=0, help='After the checks, keep the server up (for a manual look)')
    args = parser.parse_args()
    container = 'retrobridge-browser-' + uuid.uuid4().hex[:12]
    summary = {}
    with tempfile.TemporaryDirectory(prefix='retrobridge-browser-') as directory:
        workspace = Path(directory)
        plugin = workspace / 'config/plugins/RetroBridge'
        plugin.mkdir(parents=True)
        with zipfile.ZipFile(PLUGIN_ARCHIVE) as package:
            for name in ('Jellyfin.Plugin.RetroBridge.dll', 'RetroBridge.Domain.dll'):
                (plugin / name).write_bytes(package.read(name))
        write_fixture(workspace / 'games/save-counter.nes')
        write_gb_fixture(workspace / 'games/save-counter.gb')
        try:
            # On Linux, bind-mounted files would otherwise be root-owned and block temp cleanup.
            user = ['--user', f'{os.getuid()}:{os.getgid()}'] if sys.platform.startswith('linux') else []
            smoke.docker('run', '-d', '--name', container, '--label', 'org.retrobridge.smoke=true', '-p', f'127.0.0.1:{args.port}:8096', *user,
                         '--mount', f'type=bind,src={workspace / "config"},dst=/config',
                         '--mount', f'type=bind,src={workspace / "games"},dst=/games,readonly', smoke.IMAGE)
            server = smoke.Server(smoke.host_url(container))
            server.ready()
            password, player_password = secrets.token_urlsafe(18), secrets.token_urlsafe(18)
            server.request('GET', '/Startup/User')
            server.request('POST', '/Startup/User', {'Name': 'admin', 'Password': password})
            server.request('POST', '/Startup/Complete')
            admin = server.login('admin', password)
            user = server.request('POST', '/Users/New', {'Name': 'player', 'Password': player_password}, admin)
            config_path = '/Plugins/' + smoke.PLUGIN_ID + '/Configuration'
            config = server.request('GET', config_path, token=admin)
            if config.get('BrowserPlay') is not False:
                raise AssertionError('In-browser play must default to off')
            config.update(GameRoots=['/games'], PlayerUserIds=[user['Id']], FetchArtwork=False)
            server.request('POST', config_path, config, admin)
            server.request('POST', '/RetroBridge/Scan?wait=true', token=admin)
            player = server.login('player', player_password)
            server.request('GET', '/RetroBridge/Web/nostalgist.umd.js', expected=(404,))

            config['BrowserPlay'] = True
            server.request('POST', config_path, config, admin)
            server.request('POST', '/RetroBridge/Web/Prepare', token=player, expected=(403,))
            if not server.request('POST', '/RetroBridge/Web/Prepare', token=admin)['Ready']:
                raise AssertionError('Emulator files not ready after download')
            web = server.request('GET', '/RetroBridge/Web/Config', token=player)
            if not web['Enabled'] or sorted(web['Platforms']) != ['gb', 'gbc', 'nes']:
                raise AssertionError(f'Unexpected browser play config: {web}')
            script = server.request('GET', '/RetroBridge/Web/nostalgist.umd.js', raw=True)
            if hashlib.sha256(script).hexdigest() != '37ad8729399fa8164cf818932dc856c9cbcb243613866518d48020fe5025d9e1':
                raise AssertionError('Served Nostalgist differs from the pinned file')
            import urllib.request
            with urllib.request.urlopen(server.url + '/RetroBridge/Play', timeout=20) as page:
                csp = page.headers['Content-Security-Policy']
            if "'wasm-unsafe-eval'" not in csp or "default-src 'none'" not in csp:
                raise AssertionError('Play page CSP not widened for in-browser play: ' + csp)
            summary.update(files_downloaded_and_pinned=True, csp=csp)

            games = {g['Platform']: g for g in server.request('GET', '/RetroBridge/Games', token=player)}
            if args.ios:
                helper = workspace / 'rb-session.html'
                helper.write_text('<!doctype html><meta charset=utf-8><script>localStorage.setItem("jellyfin_credentials", JSON.stringify({Servers:[{AccessToken:'
                                  + json.dumps(player) + ',UserId:' + json.dumps(user['Id']) + ',DateLastAccessed:Date.now()}]}));'
                                  'location.replace("/RetroBridge/Play#play=" + new URLSearchParams(location.search).get("game"));</script>')
                smoke.docker('cp', str(helper), container + ':/jellyfin/jellyfin-web/rb-session.html')

                def server_counter(game):
                    listed = server.request('GET', '/RetroBridge/Games/' + game['Id'] + '/Saves', token=player)
                    if not listed:
                        return 0, None
                    data = server.request('GET', '/RetroBridge/Games/' + game['Id'] + '/Saves/' + game['Id'] + '.srm', token=player, raw=True)
                    return listed[0]['Revision'], data[4] if data[:4] == b'RB01' else None

                results = {}
                for platform in ('nes', 'gb'):
                    game = games[platform]
                    counters = []
                    for boot in (1, 2):
                        subprocess.run(['xcrun', 'simctl', 'openurl', 'booted',
                                        f'{server.url}/web/rb-session.html?game={game["Id"]}&n={uuid.uuid4().hex[:6]}'], check=True)
                        deadline = time.monotonic() + 90
                        while server_counter(game)[1] != boot:
                            if time.monotonic() > deadline:
                                subprocess.run(['xcrun', 'simctl', 'io', 'booted', 'screenshot', str(REPO / 'artifacts/browser-play-failure.png')], capture_output=True)
                                raise AssertionError(f'{platform}: server save did not reach counter {boot}: {server_counter(game)}')
                            time.sleep(2)
                        counters.append(server_counter(game))
                    results[platform] = counters
                    subprocess.run(['xcrun', 'simctl', 'io', 'booted', 'screenshot', str(REPO / f'artifacts/browser-play-{platform}.png')], capture_output=True)
                summary['ios_safari'] = results
                if args.keep_seconds:
                    print(f'Keeping the server up for {args.keep_seconds}s', flush=True)
                    time.sleep(args.keep_seconds)
        finally:
            smoke.docker('rm', '-f', container)
    (REPO / 'artifacts/browser-play-smoke.json').write_text(json.dumps(summary, indent=2) + '\n')
    print('PASS: in-browser play files pinned and served, CSP widened only when enabled' + (', NES and Game Boy saves synced from iOS Safari' if args.ios else ''))
    print(json.dumps(summary.get('ios_safari', {})))


if __name__ == '__main__':
    main()
