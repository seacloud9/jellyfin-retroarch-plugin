"""Fetch real box art from thumbnails.libretro.com through the packaged plugin in an isolated Jellyfin container.

The second fixture is the original save-counter program with one changed byte, named like a real
No-Intro title so it matches published box art; it contains no commercial code.
--keep-seconds N leaves the server up with a player-session helper page for a manual look in a phone browser.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import secrets
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
PNG = b'\x89PNG\r\n\x1a\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--keep-seconds', type=int, default=0)
    parser.add_argument('--port', type=int, help='Fixed loopback port (for simulators)')
    args = parser.parse_args()
    container = 'retrobridge-artwork-' + uuid.uuid4().hex[:12]
    summary = {}
    with tempfile.TemporaryDirectory(prefix='retrobridge-artwork-') as directory:
        workspace = Path(directory)
        plugin = workspace / 'config/plugins/RetroBridge'
        plugin.mkdir(parents=True)
        with zipfile.ZipFile(REPO / 'artifacts/RetroBridge-0.1.0.zip') as package:
            for name in ('Jellyfin.Plugin.RetroBridge.dll', 'RetroBridge.Domain.dll'):
                (plugin / name).write_bytes(package.read(name))
        games = workspace / 'games'
        smoke.write_fixture(games / 'save-counter.nes')
        named = bytearray((games / 'save-counter.nes').read_bytes())
        named[-1] ^= 0xFF  # distinct content, so it is not deduplicated
        (games / 'Super Mario Bros. (World).nes').write_bytes(named)
        try:
            smoke.docker('run', '-d', '--name', container, '--label', 'org.retrobridge.smoke=true',
                         '-p', f'127.0.0.1:{args.port or ""}:8096',
                         '--mount', f'type=bind,src={workspace / "config"},dst=/config',
                         '--mount', f'type=bind,src={games},dst=/games,readonly', smoke.IMAGE)
            server = smoke.Server(smoke.host_url(container))
            server.ready()
            admin_password, player_password = secrets.token_urlsafe(18), secrets.token_urlsafe(18)
            server.request('GET', '/Startup/User')
            server.request('POST', '/Startup/User', {'Name': 'admin', 'Password': admin_password})
            server.request('POST', '/Startup/Complete')
            admin = server.login('admin', admin_password)
            user = server.request('POST', '/Users/New', {'Name': 'player', 'Password': player_password}, admin)
            config_path = '/Plugins/' + smoke.PLUGIN_ID + '/Configuration'
            config = server.request('GET', config_path, token=admin)
            if config.get('FetchArtwork') is not True:
                raise AssertionError('Artwork fetching should default to on')
            config.update(GameRoots=['/games'], PlayerUserIds=[user['Id']])
            server.request('POST', config_path, config, admin)
            server.request('POST', '/RetroBridge/Scan?wait=true', token=admin)
            # The post-scan fetch runs in the background; wait for it, then an explicit fetch reports the counts.
            player = server.login('player', player_password)
            deadline = time.monotonic() + 60
            while not any(g['HasArt'] for g in server.request('GET', '/RetroBridge/Games', token=player)):
                if time.monotonic() > deadline:
                    raise AssertionError('Background artwork fetch after the scan did not finish')
                time.sleep(1)
            result = server.request('POST', '/RetroBridge/Artwork?wait=true', token=admin)
            if result != {'Fetched': 0, 'Missing': 1, 'Failed': 0, 'Cached': 1}:
                raise AssertionError(f'Unexpected artwork result: {result}')
            listed = {g['Title']: g for g in server.request('GET', '/RetroBridge/Games', token=player)}
            mario, counter = listed['Super Mario Bros.'], listed['save-counter']
            if not mario['HasArt'] or counter['HasArt'] or mario['Region'] != 'World':
                raise AssertionError(f'Unexpected catalog artwork state: {listed}')
            image = server.request('GET', '/RetroBridge/Games/' + mario['Id'] + '/Art', token=player, raw=True)
            if not image.startswith(PNG) or len(image) < 1000:
                raise AssertionError('Served artwork is not a real PNG')
            server.request('GET', '/RetroBridge/Games/' + counter['Id'] + '/Art', token=player, expected=(404,))
            server.request('GET', '/RetroBridge/Games/' + mario['Id'] + '/Art', expected=(401,))
            summary.update(fetched_from='thumbnails.libretro.com', image_bytes=len(image), unmatched_game_has_no_art=True)

            config['FetchArtwork'] = False
            server.request('POST', config_path, config, admin)
            server.request('POST', '/RetroBridge/Artwork?wait=true', token=admin, expected=(409,))
            config['FetchArtwork'] = True
            server.request('POST', config_path, config, admin)
            summary['toggle_respected'] = True

            if args.keep_seconds:
                helper = workspace / 'rb-session.html'
                helper.write_text('<!doctype html><meta charset=utf-8><script>localStorage.setItem("jellyfin_credentials", JSON.stringify({Servers:[{AccessToken:'
                                  + json.dumps(player) + ',UserId:' + json.dumps(user['Id']) + ',DateLastAccessed:Date.now()}]}));location.replace("/RetroBridge/Play");</script>')
                smoke.docker('cp', str(helper), container + ':/jellyfin/jellyfin-web/rb-session.html')
                print(f'Session helper: {server.url}/web/rb-session.html (up for {args.keep_seconds}s)', flush=True)
                time.sleep(args.keep_seconds)
        finally:
            smoke.docker('rm', '-f', container)
    print('PASS: real libretro box art fetched, cached, served to players only, unmatched game left without art, toggle respected')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
