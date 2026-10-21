"""Generate a local HTML test harness. ApiClient is a substitute, not a Jellyfin session."""
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
html = (repo / "src/Jellyfin.Plugin.RetroBridge/Configuration/configPage.html").read_text()
stub = '''<script>
window.harnessConfig = {GameRoots: ['/fixtures'], PlayerUserIds: ['u1'], RestrictedGames: [{GameId:'gb-fixture',PlayerUserIds:['u1']}]};
window.harnessGames = [
 {Id:'nes-fixture',Title:'Demo NES',Platform:'nes'},
 {Id:'gb-fixture',Title:'Demo Game Boy',Platform:'gb'},
 {Id:'gbc-fixture',Title:'<img src=x onerror=alert(1)>',Platform:'gbc'}
];
window.ApiClient = {
 getPluginConfiguration: async () => structuredClone(window.harnessConfig),
 updatePluginConfiguration: async (_, config) => { window.harnessConfig = config; },
 getUsers: async () => [{Id:'u1',Name:'Player One',Policy:{IsAdministrator:false}},{Id:'u2',Name:'<b>Player Two</b>',Policy:{IsAdministrator:false}},{Id:'a',Name:'Admin',Policy:{IsAdministrator:true}}],
 getUrl: (x, q) => q && Object.keys(q).length ? x + '?' + new URLSearchParams(q) : x,
 getJSON: async (url) => url.includes('Scan') ? ({Running:false,Processed:3,Last:window.harnessLast}) : window.harnessGames,
 ajax: async (o) => { window.harnessLast = {Count:3,Published:true,Hashed:o.url.includes('full')?3:0,Reused:o.url.includes('full')?0:3,Issues:[]}; return {Running:true,Processed:1,Last:null}; }
};
window.addEventListener('DOMContentLoaded', () => document.querySelector('#RetroBridgePage').dispatchEvent(new Event('pageshow')));
</script><style>body{font:16px system-ui;max-width:900px;margin:32px auto;padding:0 20px}input[type=checkbox]{display:inline;width:auto;margin:4px}input,textarea,select{display:block;margin:10px 0 20px;width:100%;box-sizing:border-box}button{padding:10px}code{overflow-wrap:anywhere}</style>'''
html = html.replace('</head>', stub + '</head>').replace('<h1>RetroBridge</h1>', '<h1>RetroBridge — test harness</h1><p>Mock API data; Jellyfin styling and authentication are not loaded.</p>')
output = repo / 'artifacts/dashboard-harness.html'
output.parent.mkdir(exist_ok=True)
output.write_text(html)
print(output)
