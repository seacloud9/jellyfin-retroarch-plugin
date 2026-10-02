"""Generate a local HTML test harness. ApiClient is a substitute, not a Jellyfin session."""
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
html = (repo / "src/Jellyfin.Plugin.RetroBridge/Configuration/configPage.html").read_text()
stub = '''<script>
window.harnessConfig = {GameRoots: ['/fixtures']};
window.harnessGames = [
 {Id:'nes-fixture',Title:'Demo NES',Platform:'nes'},
 {Id:'gb-fixture',Title:'Demo Game Boy',Platform:'gb'},
 {Id:'gbc-fixture',Title:'<img src=x onerror=alert(1)>',Platform:'gbc'}
];
window.ApiClient = {
 getPluginConfiguration: async () => structuredClone(window.harnessConfig),
 updatePluginConfiguration: async (_, config) => { window.harnessConfig = config; },
 getUrl: x => x,
 getJSON: async () => window.harnessGames,
 ajax: async () => ({Count:3,Published:true,Issues:[]})
};
window.addEventListener('DOMContentLoaded', () => document.querySelector('#RetroBridgePage').dispatchEvent(new Event('pageshow')));
</script><style>body{font:16px system-ui;max-width:900px;margin:32px auto;padding:0 20px}input,textarea,select{display:block;margin:10px 0 20px;width:100%;box-sizing:border-box}button{padding:10px}code{overflow-wrap:anywhere}</style>'''
html = html.replace('</head>', stub + '</head>').replace('<h1>RetroBridge</h1>', '<h1>RetroBridge — test harness</h1><p>Mock API data; Jellyfin styling and authentication are not loaded.</p>')
output = repo / 'artifacts/dashboard-harness.html'
output.parent.mkdir(exist_ok=True)
output.write_text(html)
print(output)
