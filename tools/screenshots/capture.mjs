// Drives a running Claude Terminals instance (started with --remote-debugging-port) through each README
// scene and saves PNGs. Usage: node capture.mjs <port> <outDir>
import fs from 'node:fs';
import path from 'node:path';

const port = Number(process.argv[2] ?? 9333);
const outDir = path.resolve(process.argv[3] ?? 'docs/images');
const WIDTH = 1600, HEIGHT = 900, SCALE = 1.5;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function connect() {
  for (let i = 0; i < 60; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      const page = targets.find((t) => t.type === 'page' && t.url.startsWith('https://app.local/'));
      if (page) return page.webSocketDebuggerUrl;
    } catch { /* app still starting */ }
    await sleep(500);
  }
  throw new Error(`No Claude Terminals page on port ${port}`);
}

const ws = new WebSocket(await connect());
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let seq = 0;
const waiting = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  waiting.get(m.id)?.(m);
  waiting.delete(m.id);
};
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const id = ++seq;
  waiting.set(id, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
  ws.send(JSON.stringify({ id, method, params }));
});

async function js(expression) {
  const r = await send('Runtime.evaluate', { expression: `(async () => { ${expression} })()`, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
  return r.result.value;
}

async function shot(name) {
  await sleep(400);
  const { data } = await send('Page.captureScreenshot', { format: 'png' });
  const file = path.join(outDir, `${name}.png`);
  fs.writeFileSync(file, Buffer.from(data, 'base64'));
  console.log(`saved ${path.relative(process.cwd(), file)}`);
}

fs.mkdirSync(outDir, { recursive: true });
await send('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: HEIGHT, deviceScaleFactor: SCALE, mobile: false });
// A slightly larger terminal font keeps the scaled-down tile previews readable (demo profile only).
await js('setFontSize(17);');

// Let every session start, the "working" ones spin, and web-frontend reach its permission prompt.
await js(`
  for (let i = 0; i < 60 && !(state.sessions.length === 6 && state.sessions.every((s) => s.running && s.git)); i++)
    await new Promise((r) => setTimeout(r, 500));
`);
await sleep(13_000);

const reset = `
  closeMenu(); if (!$('#modalHost').hidden) closeModal();
  for (const t of [...docs.tabs]) closeDoc(t.key);
  showTerminalDoc(); collapse();
  state.gitTab = 'changes';
  showAllProjects();
`;

// 1. Overview: Inventory unticked to show the project toggles; the other projects and Unknown at once.
console.log('states:', await js(`${reset} toggleProject('inv'); showGit('checkout');
  return state.sessions.map((s) => s.name + '=' + statusClass(s)).join(', ');`));
await shot('overview');

// 2. One session expanded with its Git Changes.
await js(`${reset} expand('checkout');
  const row = [...document.querySelectorAll('#gitPanel .row')].find((r) => r.textContent.startsWith('webhooks.ts'));
  row?.classList.add('selected');`);
await shot('session');

// 3. Side-by-side diff in a document tab next to the terminal.
await js(`${reset} expand('payments');
  const s = state.byId.get('payments');
  openDiff(s, { mode: 'unstaged', path: 'tests/payments.test.ts', status: 'M' }, true);
  await new Promise((r) => setTimeout(r, 1500));
  openDiff(s, { mode: 'unstaged', path: 'src/routes/payments.ts', status: 'M' }, true);
  await new Promise((r) => setTimeout(r, 2500));`);
await shot('diff');

// 4. Git Repository history with a commit selected.
await js(`${reset} expand('checkout');
  const s = state.byId.get('checkout');
  state.gitTab = 'repo'; s.log = null; renderGit();
  for (let i = 0; i < 20 && !s.log; i++) await new Promise((r) => setTimeout(r, 200));
  await selectCommit(s, s.log[1].sha);
  await new Promise((r) => setTimeout(r, 500));`);
await shot('history');

// 5. Resuming a saved conversation (fake conversations from the demo's CLAUDE_CONFIG_DIR).
await js(`${reset} showGit('checkout');
  showAddDialog('resume');
  for (let i = 0; i < 20 && !document.querySelector('.conv-item'); i++) await new Promise((r) => setTimeout(r, 200));
  document.querySelectorAll('.conv-item')[1]?.click();`);
await shot('resume');

await js(reset);
await send('Emulation.clearDeviceMetricsOverride');
ws.close();
