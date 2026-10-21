// Drives the real /RetroBridge/Play page from a live server in jsdom with a player's Jellyfin session.
// Usage: node play-page.js <server-url> <user-id> [--mobile]   (access token in RETROBRIDGE_TOKEN)
const { JSDOM, VirtualConsole, ResourceLoader } = require('jsdom');
const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 26_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.5 Mobile/15E148 Safari/604.1';

async function main() {
  const [server, userId, flag] = process.argv.slice(2);
  const token = process.env.RETROBRIDGE_TOKEN;
  const pageUrl = new URL('RetroBridge/Play', server.endsWith('/') ? server : server + '/').href;
  const response = await fetch(pageUrl);
  const csp = response.headers.get('content-security-policy') || '';
  if (!response.ok || !csp.includes("default-src 'none'")) throw new Error('Play page missing or not locked down');
  const html = await response.text();
  const navigation = "window.location.href = 'retrobridge://launch?ticket=' + encodeURIComponent(result.Ticket);";
  if (!html.includes(navigation)) throw new Error('Play page navigation changed; update this test');
  // jsdom cannot navigate to custom schemes; capture the link instead. Everything else runs unmodified.
  const testable = html.replace(navigation, "window.__opened = 'retrobridge://launch?ticket=' + encodeURIComponent(result.Ticket);");
  const errors = [];
  const virtualConsole = new VirtualConsole();
  virtualConsole.on('jsdomError', e => errors.push(e.message));
  const dom = new JSDOM(testable, {
    url: pageUrl, runScripts: 'dangerously', virtualConsole,
    ...(flag === '--mobile' ? { resources: new ResourceLoader({ userAgent: IPHONE }) } : {}),
    beforeParse(window) {
      window.localStorage.setItem('jellyfin_credentials', JSON.stringify({ Servers: [{ AccessToken: token, UserId: userId, DateLastAccessed: Date.now() }] }));
      window.fetch = (path, options) => fetch(new URL(path, window.location.href), options);
    }
  });
  const { document } = dom.window;
  const until = async (check, what) => {
    for (let i = 0; i < 100; i++) { const value = check(); if (value) return value; await new Promise(r => setTimeout(r, 100)); }
    throw new Error('Timed out waiting for ' + what + '; page says: ' + document.body.textContent.replace(/\s+/g, ' ').slice(0, 300));
  };
  const button = await until(() => document.querySelector('button.play'), 'a Play button');
  const devices = [...document.querySelectorAll('#device option')].map(o => o.value);
  const games = [...document.querySelectorAll('#games li span')].map(s => s.firstChild.textContent);
  button.click();
  // A listening device gets the launch directly; otherwise the page opens a link.
  const status = () => document.getElementById('status').textContent;
  await until(() => dom.window.__opened || status().startsWith('Sent to') || status().includes('not listening'), 'a launch outcome');
  const link = dom.window.__opened || null;
  console.log(JSON.stringify({ link, devices, games, status: status(), errors }));
}
main().catch(e => { console.error(e.message); process.exit(1); });
