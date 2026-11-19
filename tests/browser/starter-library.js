// A new Jellyfin user's first visit through the web menu, using the installed plugin and real WASM.
const assert = require('node:assert/strict');
const { chromium } = require('playwright');

// Inspect composited frames because the emulator can report a successful boot with a blank screen.
async function assertRendered(page, canvas, title) {
  const deadline = Date.now() + 10000;
  do {
    const frame = await canvas.screenshot();
    const colors = await page.evaluate(async bytes => {
      const image = await createImageBitmap(new Blob([new Uint8Array(bytes)], { type: 'image/png' }));
      const ctx = new OffscreenCanvas(128, 72).getContext('2d');
      ctx.drawImage(image, 0, 0, 128, 72);
      return new Set(new Uint32Array(ctx.getImageData(0, 0, 128, 72).data.buffer)).size;
    }, Array.from(frame));
    if (colors > 8) return;
    await page.waitForTimeout(150);
  } while (Date.now() < deadline);
  assert.fail(title + ' rendered a blank frame');
}

async function run() {
  const [server, username] = process.argv.slice(2);
  const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  try {
    const context = await browser.newContext({ viewport: { width: 1152, height: 900 } });
    const home = await context.newPage();
    await home.goto(server + '/web/');
    await home.locator('#txtManualName:visible').fill(username);
    await home.locator('#txtManualPassword:visible').fill(process.env.RETROBRIDGE_TEST_PASSWORD);
    await home.locator('#txtManualPassword:visible').press('Enter');
    await home.waitForFunction(() => (JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers || []).some(s => s.AccessToken));
    const link = home.getByRole('link', { name: 'Retro Games', exact: true });
    const opened = context.waitForEvent('page');
    await link.click();
    const page = await opened;
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.waitForFunction(() => {
      const images = [...document.querySelectorAll('#included img')];
      return images.length === 8 && images.every(img => img.complete && img.naturalWidth > 0);
    });
    assert.equal(await page.locator('#games button').count(), 8);
    const titles = await page.locator('#included .card-title').allTextContents();

    // Exit while a cart is loading, then launch every game in the same tab. The loader changes
    // the canvas id, so a successful first launch alone doesn't establish that replays work.
    let finishRequest;
    const paused = new Promise(resolve => { finishRequest = resolve; });
    const pending = page.waitForRequest('**/Content');
    await page.route('**/Content', async route => {
      await paused;
      await route.continue();
    }, { times: 1 });
    await page.getByRole('button', { name: 'Play ' + titles[0] + ' in this browser' }).click();
    await pending;
    await page.getByRole('button', { name: 'Back to library' }).click();
    finishRequest();

    for (const title of titles) {
      await page.getByRole('button', { name: 'Play ' + title + ' in this browser' }).click();
      await page.waitForFunction(() => window.__browserPlay?.booted || window.__browserPlay?.error);
      assert.equal(await page.evaluate(() => window.__browserPlay.booted), true, title);
      const canvas = page.locator('#player canvas');
      await canvas.waitFor({ state: 'visible' });
      assert.equal(await page.locator('canvas').count(), 1, title);
      await assertRendered(page, canvas, title);
      await page.getByRole('button', { name: 'Back to library' }).click();
      await page.locator('#player').waitFor({ state: 'hidden' });
    }
    assert.deepEqual(errors, []);
    console.log('Passed the Jellyfin Web menu, eight previews, cancelled launch and eight consecutive game launches.');
  } finally {
    await browser.close();
  }
}
run().catch(error => { console.error(error.message); process.exitCode = 1; });
