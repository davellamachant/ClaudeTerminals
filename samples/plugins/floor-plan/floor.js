'use strict';
// The Floor plan: a project's sessions on a city of its code. The repository's top-level folders are buildings, their
// footprint from how many files they hold and their height too; every file a session has read or edited lights a window
// (blue read, amber edited, outlined when two sessions touched it); each session is a beacon on the street in front of the
// building it's working in, with a light trail along the streets it came by, and a card beside the city tells its story
// turn by turn. Whoever needs your answer is at the top. Files outside the repository land on the plaza in front. The
// slider at the bottom rewinds through everything that happened; Live follows what's happening now.
//
// It's a Claude Terminals plugin with "sessions" access (see plugin.json): it asks the app for the sessions, what Claude
// did in each (read from their conversations) and the repository's folders, every few seconds. Its colours and font come
// from the app's theme (the theme message), so it looks right under Tron, Matrix, Nerds or one of your own.

const REFRESH_MS = 2500, FOLDERS_MS = 60_000, MAX_EVENTS = 6000;
const W = 1000, H = 600, LOT = 2, GAPT = 1, PLAZA_ROWS = 2, MAX_BUILDINGS = 15, MAX_LABELS = 14;
const COLORS = [1, 2, 3, 4, 5, 6, 7, 8].map((i) => `var(--s${i})`);
const NO_PROJECT = '\u0000none';   // the project key of sessions in no project

const $ = (sel) => document.querySelector(sel);
function h(tag, attrs = {}, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v === true ? '' : v);
  }
  for (const k of kids.flat()) if (k != null && k !== false) e.append(k instanceof Node ? k : String(k));
  return e;
}
function sv(tag, attrs = {}, ...kids) {
  const e = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [k, v] of Object.entries(attrs)) if (v != null) e.setAttribute(k, v);
  for (const k of kids.flat()) if (k != null) e.append(k instanceof Node ? k : String(k));
  return e;
}
const pts = (list) => list.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' ');

// ---- talking to Claude Terminals

let nextRequest = 1;
const pending = new Map();

/** A request to the app; it answers with what this plugin is allowed to see, or an error saying why not. */
function ask(name, args = {}) {
  const requestId = nextRequest++;
  return new Promise((resolve, reject) => {
    pending.set(requestId, { resolve, reject });
    parent.postMessage({ type: 'claude-terminals:request', requestId, name, ...args }, '*');
  });
}

/** This page's colours by the app's names for them (see the theme message in the app's README); its own dark and light sets stand in for an older app. */
const THEME_COLORS = { bg: 'chrome', chrome: 'chrome-2', panel: 'panel', border: 'border', 'border-soft': 'border-soft', hover: 'hover-strong', text: 'text', 'text-dim': 'text-dim',
  'text-faint': 'text-faint', 'card-bg': 'panel-2', 'card-border': 'border', focus: 'focus', attention: 'attention', ok: 'ok', working: 'working', accent: 'accent-strong', line: 'accent',
  read: 'working', edit: 'attention' };
function dress(m) {
  const root = document.documentElement;
  root.dataset.theme = m.theme === 'light' ? 'light' : 'dark';
  const c = m.colors ?? {};
  for (const [mine, theirs] of Object.entries(THEME_COLORS)) { if (c[theirs]) root.style.setProperty('--' + mine, c[theirs]); else root.style.removeProperty('--' + mine); }
  document.body.style.fontFamily = m.font ? `"${m.font}", "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif` : '';
  if (m.glow) root.dataset.glow = ''; else delete root.dataset.glow;
  fp.lanesLen = -1;
  render();
}

addEventListener('message', (e) => {
  if (e.source !== parent) return;
  const m = e.data;
  if (typeof m?.type !== 'string') return;
  if (m.type === 'claude-terminals:response') {
    const p = pending.get(m.requestId);
    if (!p) return;
    pending.delete(m.requestId);
    if ('error' in m) p.reject(new Error(m.error)); else p.resolve(m.result);
  } else if (m.type === 'claude-terminals:theme') {
    dress(m);
  } else if (m.type === 'claude-terminals:command' && m.context && 'project' in m.context) {
    // Opened from a project's or a session's menu: that project's city.
    choose(m.context.project ?? NO_PROJECT);
  }
});

// ---- state

const fp = {
  sessions: [], projectKey: null, members: [], data: new Map(),   // session id → { conversation, version, found, events }
  folders: new Map(),   // worktree (lower case) → { at, root, folders } (root: the repository's main worktree)
  rooms: null, root: null, geometry: null, cells: new Map(), counts: new Map(), shared: new Set(),
  merged: [], idx: null, focus: null, focusRoom: null, playing: false, playTimer: null, loading: false, world: null, problem: null,
  stageKey: null, stage: null, cellEls: new Map(), moreEls: new Map(), roomEls: new Map(), nEls: new Map(), fileLayer: null, trailEls: [], people: [], lanesLen: -1, initials: [],
};

const projectKey = (s) => s.project ?? NO_PROJECT;
const projectLabel = (key) => (key === NO_PROJECT ? 'Sessions in no project' : key);
const lower = (p) => p.replace(/\\/g, '/').replace(/\/$/, '').toLowerCase();

function choose(key) {
  if (fp.projectKey === key) return;
  Object.assign(fp, { projectKey: key, members: [], data: new Map(), rooms: null, root: null, geometry: null, cells: new Map(), counts: new Map(), shared: new Set(), merged: [], idx: null, focus: null, focusRoom: null, stageKey: null, world: null });
  stop();
  load();
}

/** The sessions, the buildings (now and then) and what's new in each conversation; then the city is drawn again. */
async function load() {
  if (fp.loading) return;
  fp.loading = true;
  try {
    fp.sessions = await ask('sessions');
    fp.problem = null;
    const keys = [...new Set(fp.sessions.map(projectKey))];
    if (fp.projectKey == null || !keys.includes(fp.projectKey)) {
      const open = fp.sessions.find((s) => s.open);
      fp.projectKey = open ? projectKey(open) : keys[0] ?? null;
    }
    renderProjects(keys);
    const members = fp.sessions.filter((s) => projectKey(s) === fp.projectKey);
    // Each worktree's repository, now and then: the city is the repository most of the sessions are in.
    for (const s of members) {
      if (!s.worktree) continue;
      const k = lower(s.worktree), had = fp.folders.get(k);
      if (had && Date.now() - had.at < FOLDERS_MS) continue;
      try { fp.folders.set(k, { at: Date.now(), ...(await ask('folders', { sessionId: s.id })) }); } catch { /* try again next time */ }
    }
    const byRoot = new Map();
    for (const s of members) {
      const f = s.worktree && fp.folders.get(lower(s.worktree));
      if (f?.root) byRoot.set(lower(f.root), [...(byRoot.get(lower(f.root)) ?? []), f]);
    }
    const main = [...byRoot.values()].sort((a, b) => b.length - a.length)[0]?.[0] ?? null;
    if ((main?.root ?? null) !== fp.root) Object.assign(fp, { root: main?.root ?? null, geometry: null, cells: new Map(), counts: new Map(), stageKey: null, focusRoom: null });
    fp.rooms = main?.folders ?? null;
    fp.members = members;
    const have = {};
    for (const [id, d] of fp.data) have[id] = { conversation: d.conversation, version: d.version };
    const results = await ask('activity', { ids: members.map((s) => s.id), have });
    for (const r of results) {
      const d = fp.data.get(r.id) ?? { conversation: null, version: 0, events: [] };
      const fresh = r.events.map((e) => ({ ...e, t: Date.parse(e.t) }));
      if (!r.found) d.events = [];
      else if (d.conversation !== r.conversation || r.from > d.events.length) d.events = fresh;
      else d.events = d.events.slice(0, r.from).concat(fresh);
      Object.assign(d, { conversation: r.conversation, version: r.version, found: r.found });
      fp.data.set(r.id, d);
    }
    merge();
  } catch (e) {
    fp.problem = e.message;
  } finally {
    fp.loading = false;
  }
  render();
}

function renderProjects(keys) {
  const select = $('#project');
  const want = keys.map((k) => k + '|' + projectLabel(k)).join('\n');
  if (select.dataset.keys !== want) {
    select.dataset.keys = want;
    select.replaceChildren(...keys.map((k) => h('option', { value: k }, projectLabel(k))));
  }
  select.value = fp.projectKey ?? '';
}
$('#project').addEventListener('change', (e) => choose(e.target.value));

// ---- the city: buildings on lots, the plaza in front, and a window for every file as it first appears

/** Where a file is: its building (top-level folder of the city's repository) and its path there, or the plaza outside. */
function place(file, s) {
  if (!file) return null;
  const f = file.replace(/\\/g, '/'), low = f.toLowerCase(), name = f.split('/').pop();
  // A session's own worktree counts only if it's a worktree of the city's repository: another repository's src\ isn't this one's.
  const own = s.worktree && fp.root && lower(fp.folders.get(lower(s.worktree))?.root ?? '') === lower(fp.root) ? s.worktree : null;
  for (const root of [own, fp.root].filter(Boolean).map(lower)) {
    if (!low.startsWith(root + '/')) continue;
    const rel = f.slice(root.length + 1);
    const top = rel.includes('/') ? rel.split('/')[0] : '(root)';
    const rooms = fp.geometry?.roomSet;
    const room = !rooms ? null : rooms.has(top) ? top : rooms.has('(other)') ? '(other)' : null;
    return { key: rel.toLowerCase(), rel, name, room };
  }
  return { key: 'outside:' + low, rel: f, name, room: null };
}

/** All sessions' events in time order, with a window for every file as it first appears, and which files two sessions share. */
function merge() {
  if (!fp.geometry) geometry();
  const g = fp.geometry, merged = [];
  fp.members.forEach((s, si) => { for (const e of fp.data.get(s.id)?.events ?? []) merged.push({ ...e, si, s }); });
  merged.sort((a, b) => a.t - b.t);
  fp.merged = merged.length > MAX_EVENTS ? merged.slice(-MAX_EVENTS) : merged;
  const who = new Map();
  for (const e of fp.merged) {
    if (!e.file) continue;
    const p = place(e.file, e.s);
    e.key = p.key; e.name = p.name;
    if (!fp.cells.has(p.key)) {
      const room = p.room ? g.rooms.find((r) => r.name === p.room) : g.hall;
      const i = fp.counts.get(room.name) ?? 0;
      fp.counts.set(room.name, i + 1);
      // A building has as many windows as its faces hold; files past that share the last one, which says how many more there are.
      const slot = Math.min(i, room.slots.length - 1), overflow = i > room.slots.length - 1;
      const w = room.slots[slot];
      fp.cells.set(p.key, { key: p.key, x: w.cx, y: w.cy, side: w.side, room, slot, name: p.name, path: p.rel, overflow });
    }
    (who.get(p.key) ?? who.set(p.key, new Set()).get(p.key)).add(e.si);
  }
  fp.shared = new Set([...who].filter(([, set]) => set.size > 1).map(([k]) => k));
}

/** A small deterministic shuffle, so a building's lit windows scatter the same way every time. */
function shuffled(n, seed) {
  let s = seed >>> 0 || 1;
  const rnd = () => (s = (s * 1103515245 + 12345) % 2147483648) / 2147483648;
  const out = Array.from({ length: n }, (_, i) => i);
  for (let i = n - 1; i > 0; i--) { const j = Math.floor(rnd() * (i + 1)); [out[i], out[j]] = [out[j], out[i]]; }
  return out;
}

/**
 * The buildings: the biggest folders at the back, on a square-ish grid of lots, each a block whose footprint and height
 * grow with its files; the plaza across the front for files outside the repository. Everything is sized to fit the stage.
 */
function geometry() {
  const list = fp.rooms ?? [];
  const kept = list.slice(0, MAX_BUILDINGS).map((r) => ({ name: r.name, files: r.files }));
  const rest = list.slice(MAX_BUILDINGS).reduce((n, r) => n + r.files, 0);
  if (rest) kept.push({ name: '(other)', files: rest });
  kept.sort((a, b) => b.files - a.files);
  const n = kept.length;
  const cols = Math.max(2, Math.ceil(Math.sqrt(Math.max(n, 1)))), rows = Math.max(1, Math.ceil(n / cols));
  const totalC = cols * (LOT + GAPT) + GAPT, totalR = rows * (LOT + GAPT) + GAPT + PLAZA_ROWS + GAPT;
  const maxFiles = Math.max(1, ...kept.map((r) => r.files));
  const zOf = (files) => 14 + Math.round(110 * Math.sqrt(files / maxFiles));
  const maxZ = kept.length ? Math.max(...kept.map((r) => zOf(r.files))) : 14;
  const span = (totalC + totalR) / 2;
  const TW = Math.min((W - 40) / span, 2 * (H - maxZ - 60) / span), TH = TW / 2;
  const OX = W / 2 - (totalC - totalR) * TW / 4, OY = maxZ + 24;
  const iso = (c, r, z = 0) => [OX + (c - r) * TW / 2, OY + (c + r) * TH / 2 - z];
  const buildings = kept.map((r, i) => {
    const size = Math.max(1, Math.min(LOT, Math.round(Math.sqrt(r.files / maxFiles) * LOT)));
    const lotC = GAPT + (i % cols) * (LOT + GAPT), lotR = GAPT + Math.floor(i / cols) * (LOT + GAPT);
    return building(r.name, r.files, lotC + (LOT - size) / 2, lotR + (LOT - size) / 2, size, size, zOf(r.files), iso, TW, TH, i);
  });
  // The plaza: a low slab across the front; its roof is paved with the files outside the repository.
  const hall = building('Outside the repository', 0, GAPT, totalR - PLAZA_ROWS - GAPT, totalC - 2 * GAPT, PLAZA_ROWS, 8, iso, TW, TH, 99, true);
  fp.geometry = { rooms: buildings, hall, roomSet: new Set(buildings.map((r) => r.name)), iso, TW, TH, totalC, totalR, maxZ };
}

/** One building: its faces as screen polygons, its door on the street in front, and the windows a file can light, scattered. */
function building(name, files, c0, r0, sc, sr, z, iso, TW, TH, seed, hall = false) {
  const base = [iso(c0, r0), iso(c0 + sc, r0), iso(c0 + sc, r0 + sr), iso(c0, r0 + sr)];
  const top = [iso(c0, r0, z), iso(c0 + sc, r0, z), iso(c0 + sc, r0 + sr, z), iso(c0, r0 + sr, z)];
  const faces = { left: [base[3], base[2], top[2], top[3]], right: [base[2], base[1], top[1], top[2]], top };
  const slots = [];
  const mid = iso(c0 + sc / 2, r0 + sr / 2)[0];
  if (hall) {
    // Paving stones on the roof; a file's label goes to whichever side of the plaza its stone is nearer.
    const nc = sc * 2, nr = sr * 2;
    for (let i = 0; i < nr; i++) for (let j = 0; j < nc; j++) {
      const cc = c0 + (j + 0.5) * sc / nc, rr = r0 + (i + 0.5) * sr / nr, dx = TW / nc * sc * .18, dy = TH / nc * sc * .18;
      const p = iso(cc, rr, z);
      slots.push({ cx: p[0], cy: p[1], side: p[0] < mid ? 'L' : 'R', quad: pts([[p[0], p[1] - 2 * dy], [p[0] + 2 * dx, p[1]], [p[0], p[1] + 2 * dy], [p[0] - 2 * dx, p[1]]]) });
    }
  } else {
    // Windows in rows on the two faces that show, three to a tile.
    const colsL = sr * 3, colsR = sc * 3, rows = Math.max(1, Math.floor((z - 6) / 12)), wh = 6;
    for (let row = 0; row < rows; row++) {
      const zz = 6 + row * 12;
      for (let col = 0; col < colsL; col++) {
        const t = (col + 0.5) / colsL, p = iso(c0, r0 + sr - t * sr, zz), dx = TW / (2 * colsL) * sr * .32, dy = -TH / (2 * colsL) * sr * .32;
        slots.push({ cx: p[0], cy: p[1] - wh / 2, side: 'L', quad: pts([[p[0] - dx, p[1] - dy], [p[0] + dx, p[1] + dy], [p[0] + dx, p[1] + dy - wh], [p[0] - dx, p[1] - dy - wh]]) });
      }
      for (let col = 0; col < colsR; col++) {
        const t = (col + 0.5) / colsR, p = iso(c0 + t * sc, r0 + sr, zz), dx = TW / (2 * colsR) * sc * .32, dy = TH / (2 * colsR) * sc * .32;
        slots.push({ cx: p[0], cy: p[1] - wh / 2, side: 'R', quad: pts([[p[0] - dx, p[1] - dy], [p[0] + dx, p[1] + dy], [p[0] + dx, p[1] + dy - wh], [p[0] - dx, p[1] - dy - wh]]) });
      }
    }
  }
  const order = shuffled(slots.length, seed * 7919 + files + name.length);
  const door = iso(c0 + sc / 2, r0 + sr + 0.55);
  const label = iso(c0 + sc / 2, r0 + sr + 0.12);
  const all = [...base, ...top], xs = all.map((p) => p[0]), ys = all.map((p) => p[1]);
  return { name, files, hall, c0, r0, sc, sr, z, faces, slots: order.map((i) => slots[i]), door, label, order: c0 + r0,
    left: Math.min(...xs), right: Math.max(...xs), topY: Math.min(...ys), bottomY: Math.max(...ys) };
}

// ---- narration: one plain sentence per event, and the state it leaves the session in

const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const plural = (n, w) => `${n} ${w}${n === 1 ? '' : 's'}`;
const clock = (t) => new Date(t).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
const day = (t) => new Date(t).toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
function ago(t, now) {
  const m = Math.round((now - t) / 60e3);
  return m < 1 ? 'just now' : m < 60 ? `${m} min ago` : m < 1440 ? `${Math.floor(m / 60)} h ago` : `${Math.floor(m / 1440)} d ago`;
}

function narrate(e, ctx) {
  const f = e.name ? `<span class="f">${esc(e.name)}</span>` : '';
  const out = (text, state = 'busy', short = null) => ({ text, state, short: short ?? text.replace(/<[^>]+>/g, '') });
  switch (e.k) {
    case 'prompt': ctx.edits = {}; return out(`You: “${esc(e.text)}”`, 'busy', 'Reading your message');
    case 'read': return out(e.key?.startsWith('outside:') ? `Reading ${f}, outside the repository` : `Reading ${f}`);
    case 'search': return out(`Searching the code for “${esc(e.text)}”`, 'busy', 'Searching the code');
    case 'edit': {
      if (e.ok === false) return out(`An edit of ${f} didn’t apply, trying again`);
      const n = ctx.edits[e.key] = (ctx.edits[e.key] ?? 0) + 1;
      return out(n > 2 ? `Editing ${f} again, ${n} edits with no test run` : n === 2 ? `Editing ${f} again` : `Editing ${f}`);
    }
    case 'test': ctx.edits = {}; return e.ok === true ? out('Tests passed') : e.ok === false ? out('Tests failed, looking at why') : out('Running the tests');
    case 'build': return e.ok === false ? out('Build failed, fixing it') : out('Building');
    case 'run': return out(`Running: ${esc(e.text)}`, 'busy', 'Running a command');
    case 'ask': return out(`Asking you: “${esc(e.text)}”`, 'needs', 'Needs your answer');
    case 'say': {
      if (e.q) return out(`Asking you: “${esc(e.text)}”`, 'needs', 'Needs your answer');
      // "Done. The roster…" would read "Done: Done. The roster…".
      const said = String(e.text).replace(/^(done|finished|all done)\b[.:!,]?\s*/i, '');
      return out(`Done: ${esc(said || e.text)}`, 'done', 'Finished, waiting for you');
    }
    case 'agent': return out(`Sent a helper to ${esc(e.text)}`, 'busy', 'Sent a helper');
    case 'compact': return out('Summarising the conversation to make room', 'busy', 'Making room');
    default: return out('Working');
  }
}

/** A turn is everything between two of your prompts, in one line: read 3 files, edited 2, tests passed. */
function turnSummary(tn) {
  const parts = [];
  if (tn.reads.size) parts.push(`read ${plural(tn.reads.size, 'file')}`);
  if (tn.edits.size) { const names = [...tn.edits]; parts.push(`edited ${names.slice(0, 2).map((n) => `<span class="f">${esc(n)}</span>`).join(', ')}${names.length > 2 ? ` and ${names.length - 2} more` : ''}`); }
  if (tn.tests.length) { const last = tn.tests[tn.tests.length - 1]; parts.push(last === true ? `tests passed${tn.tests.length > 1 ? ` after ${tn.tests.length} runs` : ''}` : last === false ? `tests still failing after ${plural(tn.tests.length, 'run')}` : 'ran the tests'); }
  if (tn.runs) parts.push(`ran ${plural(tn.runs, 'command')}`);
  if (!parts.length) return 'Thinking';
  const s = parts.join(', ');
  return s[0].toUpperCase() + s.slice(1);
}

/** What the app says a session is doing now (its status dot), as the city's states. */
const LIVE = { asking: 'needs', attention: 'done', working: 'busy', running: 'idle', exited: 'off' };
const LIVE_SHORT = { needs: 'Needs your answer', done: 'Finished, waiting for you', idle: 'Idle', off: 'Not running' };

/** The world at slider position idx, recomputed from the start so that rewinding is exact. Live, each session's state is
 * the app's own (its status dot), not a guess from its conversation. */
function compute(idx, live) {
  const per = fp.members.map(() => ({ state: 'off', text: '', short: '', pos: null, trail: [], edits: {}, lastT: null, turns: [], touched: new Map(), asked: null }));
  const cellState = {}, cellWho = {};
  for (let i = 0; i < idx; i++) {
    const e = fp.merged[i], p = per[e.si];
    p.lastT = e.t;
    const n = narrate(e, p);
    p.state = n.state; p.text = n.text; p.short = n.short;
    p.asked = n.state === 'needs' ? n.text : e.k === 'prompt' ? null : p.asked;
    if (e.k === 'prompt' || !p.turns.length) p.turns.push({ t: e.t, you: e.k === 'prompt' ? e.text : null, reads: new Set(), edits: new Set(), tests: [], runs: 0, outcome: null });
    const tn = p.turns[p.turns.length - 1];
    if (e.k === 'read' && e.key) tn.reads.add(e.key);
    if (e.k === 'edit' && e.name) tn.edits.add(e.name);
    if (e.k === 'test') tn.tests.push(e.ok);
    if (e.k === 'run' || e.k === 'build') tn.runs++;
    tn.outcome = n.state === 'needs' || n.state === 'done' ? { state: n.state, text: n.text } : e.k === 'prompt' ? tn.outcome : null;
    if (e.key) {
      const c = fp.cells.get(e.key);
      if (c) {
        p.pos = c;
        if (p.trail[p.trail.length - 1]?.room !== c.room) p.trail.push(c);   // the trail runs building to building
        if (e.k === 'edit') cellState[e.key] = 'edit'; else cellState[e.key] ??= 'read';
        (cellWho[e.key] ??= new Map()).set(e.si, { t: e.t, k: e.k });
        p.touched.set(e.key, e.t);
      }
    }
  }
  if (live) {
    fp.members.forEach((s, i) => {
      const p = per[i], state = LIVE[s.status] ?? 'idle';
      p.state = state;
      if (state !== 'busy') p.short = LIVE_SHORT[state];
      else if (!p.short) p.short = 'Working';
    });
  }
  return { per, cellState, cellWho };
}

/** Two letters for each session's avatar, told apart when two sessions would get the same ones. */
function initials(members) {
  const raw = members.map((s) => {
    const words = s.name.trim().split(/[\s\-_./\\]+/).filter(Boolean);
    return (words.length > 1 ? words[0][0] + words[1][0] : (words[0] ?? '?').slice(0, 2)).toUpperCase();
  });
  return raw.map((r, i) => (raw.indexOf(r) !== raw.lastIndexOf(r) ? r[0] + (raw.slice(0, i + 1).filter((x) => x === r).length) : r));
}

// ---- drawing

const liveIdx = () => fp.merged.length;
const curIdx = () => Math.min(fp.idx ?? liveIdx(), liveIdx());
const rank = (s) => ({ needs: 0, done: 1, busy: 2, idle: 3, off: 4 }[s]);

/** The stage (ground, buildings, trails, beacons) is built once per repository and set of sessions; windows light as files appear. */
function buildStage() {
  const g = fp.geometry, key = (fp.root ?? '') + '|' + fp.members.map((s) => s.id + ':' + s.name).join(',');
  if (fp.stageKey === key && fp.stage) return;
  fp.stageKey = key;
  fp.initials = initials(fp.members);
  const host = $('#stage');
  host.replaceChildren();
  const stage = h('div', { class: 'fl-stage' });
  const svg = sv('svg', { viewBox: `0 0 ${W} ${H}`, 'aria-label': 'The repository as a city' });
  fp.roomEls = new Map(); fp.cellEls = new Map(); fp.moreEls = new Map(); fp.nEls = new Map();
  // The ground: streets ruled across the whole block.
  const ground = sv('g', { class: 'fl-ground' });
  for (let i = 0; i <= g.totalC; i++) ground.append(sv('line', { x1: g.iso(i, 0)[0], y1: g.iso(i, 0)[1], x2: g.iso(i, g.totalR)[0], y2: g.iso(i, g.totalR)[1] }));
  for (let i = 0; i <= g.totalR; i++) ground.append(sv('line', { x1: g.iso(0, i)[0], y1: g.iso(0, i)[1], x2: g.iso(g.totalC, i)[0], y2: g.iso(g.totalC, i)[1] }));
  svg.append(ground);
  // Buildings back to front, each with its windows dark until a file lights one; their names go on a layer above them all.
  const labels = sv('g', { class: 'fl-labels' });
  for (const b of [...g.rooms, g.hall].sort((a, c) => a.order - c.order)) {
    const el = sv('g', { class: 'fl-bld' + (b.hall ? ' hall' : ''), 'data-room': b.name });
    el.addEventListener('click', (e) => { if (e.target.classList.contains('face')) { e.stopPropagation(); focusRoom(fp.focusRoom === b.name ? null : b.name); } });
    el.append(sv('polygon', { class: 'face left', points: pts(b.faces.left) }), sv('polygon', { class: 'face right', points: pts(b.faces.right) }),
      sv('polygon', { class: 'face top', points: pts(b.faces.top) }));
    const windows = sv('g', { class: 'fl-windows' });
    for (const w of b.slots) windows.append(sv('polygon', { class: 'fl-dark', points: w.quad }));
    el.append(windows);
    el.cellLayer = sv('g'); el.append(el.cellLayer);
    const nameEl = sv('text', { class: 'fl-name', x: b.label[0], y: b.label[1] + 11, 'text-anchor': 'middle' }, b.name);
    const n = sv('text', { class: 'fl-n', x: b.label[0], y: b.label[1] + 21, 'text-anchor': 'middle' }, b.hall ? '' : plural(b.files, 'file'));
    labels.append(nameEl, n);
    fp.nEls.set(b.name, n);
    n.nameEl = nameEl;
    svg.append(el);
    fp.roomEls.set(b.name, el);
  }
  svg.append(labels);
  fp.fileLayer = sv('g', { class: 'fl-files' });
  svg.append(fp.fileLayer);
  fp.trailEls = fp.members.map((s, i) => { const p = sv('path', { class: 'fl-trail', stroke: COLORS[i % COLORS.length], d: '' }); svg.append(p); return p; });
  if (!fp.rooms) {
    svg.append(sv('text', { class: 'fl-empty', x: W / 2, y: H / 2, 'text-anchor': 'middle' },
      fp.members.some((s) => s.worktree) ? 'Reading the repository…' : 'No session of this project is in a Git repository, so there is no city: everything happens on the plaza.'));
  }
  // The beacons: one per session, on the street in front of the building it's in.
  fp.people = fp.members.map((s, i) => {
    const colour = COLORS[i % COLORS.length];
    const p = sv('g', { class: 'fl-person', 'data-si': i, hidden: '' },
      sv('circle', { class: 'ring', r: 10 }), sv('circle', { class: 'glow', r: 13, fill: colour }), sv('circle', { class: 'av', r: 10, fill: colour }),
      sv('text', { class: 'ini', y: 3.5, 'text-anchor': 'middle' }, fp.initials[i]),
      sv('circle', { class: 'badge', cx: 9, cy: -9, r: 6 }), sv('text', { class: 'badge-t', x: 9, y: -6, 'text-anchor': 'middle' }, ''),
      sv('text', { class: 'bubble', x: 16, y: 4 }, ''));
    p.addEventListener('click', (e) => { e.stopPropagation(); focus(fp.focus === i ? null : i); });
    svg.append(p);
    return p;
  });
  stage.append(svg);
  host.append(stage);
  fp.unzoom = h('button', { class: 'btn unzoom', hidden: true, onclick: () => { fp.focusRoom = null; focus(null); } }, '← Whole city');
  host.append(fp.unzoom);
  fp.stage = stage;
}

/** The window's polygon (made on first sight), lit in the building it belongs to; none for an overflow file. */
function cellEl(c) {
  if (c.overflow) return null;
  let el = fp.cellEls.get(c.key);
  if (el) return el;
  el = sv('polygon', { class: 'fl-cell', points: c.room.slots[c.slot].quad, 'data-key': c.key });
  fp.roomEls.get(c.room.name)?.cellLayer.append(el);
  fp.cellEls.set(c.key, el);
  return el;
}

/** "+N more" by a building's door when more files were touched there than its windows can show. */
function moreLabels() {
  const more = new Map();
  for (const c of fp.cells.values()) if (c.overflow) more.set(c.room.name, (more.get(c.room.name) ?? 0) + 1);
  for (const [name, el] of fp.moreEls) if (!more.has(name)) { el.remove(); fp.moreEls.delete(name); }
  for (const [name, n] of more) {
    const room = fp.geometry.rooms.find((r) => r.name === name) ?? fp.geometry.hall;
    let el = fp.moreEls.get(name);
    if (!el) { el = sv('text', { class: 'fl-n', x: room.label[0], y: room.label[1] + (room.hall ? 22 : 31), 'text-anchor': 'middle' }); fp.nEls.get(name)?.after(el); fp.moreEls.set(name, el); }
    el.textContent = `+${n} more than the windows show`;
  }
}

/** The files a building or a session has lit, most recent first: what gets a label when it's zoomed in on. */
function recentCells(cells, cellWho, limit) {
  const last = (c) => Math.max(...[...(cellWho[c.key] ?? new Map()).values()].map((v) => v.t));
  return cells.filter((c) => !c.overflow && cellWho[c.key]).map((c) => [c, last(c)]).sort((a, b) => b[1] - a[1]).slice(0, limit).map(([c]) => c);
}

/**
 * Labels beside lit windows: each file's name on its building's near side, stacked so that they don't overlap, with a
 * lead line back to its window. Sized against the zoom, so they read the same whatever the magnification.
 */
function labelFiles(cells, cellState, k) {
  const layer = fp.fileLayer;
  layer.replaceChildren();
  if (!cells.length) return;
  const fs = Math.max(4, 10.5 / k), gap = fs * 1.3;
  for (const side of ['L', 'R']) {
    const list = cells.filter((c) => c.side === side).sort((a, b) => a.y - b.y);
    let y = -Infinity;
    for (const c of list) {
      y = Math.max(c.y, y + gap);
      // Beside the building, or beside the stone itself out on the plaza.
      const x = c.room.hall ? c.x + (side === 'L' ? -9 : 9) : side === 'L' ? c.room.left - 5 : c.room.right + 5;
      layer.append(sv('line', { class: 'fl-lead', x1: c.x, y1: c.y, x2: x + (side === 'L' ? 2 : -2), y2: y }),
        sv('text', { class: `fl-file ${cellState[c.key] ?? ''}`, x, y: y + fs * .35, 'font-size': fs, 'text-anchor': side === 'L' ? 'end' : 'start' }, c.name));
    }
  }
}

function showMessage(text, button) {
  const m = $('#message');
  m.hidden = !text;
  m.replaceChildren(...(text ? [h('div', {}, text), button ?? ''] : []));
  document.querySelector('.main').hidden = document.querySelector('.time').hidden = !!text;
}

function render() {
  if (fp.problem) { showMessage(/allowed/.test(fp.problem) ? 'The Floor plan isn’t allowed to see your sessions.' : `The Floor plan can’t see your sessions: ${fp.problem}`, h('div', { class: 'hint' }, 'You can let it from the palette button, under Plugins.')); return; }
  if (!fp.sessions.length) { showMessage(fp.loading ? 'Reading your sessions…' : 'There are no sessions yet.'); return; }
  if (!fp.members.length) { showMessage(`${projectLabel(fp.projectKey)} has no sessions.`); return; }
  showMessage(null);
  if (!fp.geometry) geometry();
  buildStage();
  const live = fp.idx == null, idx = curIdx();
  const world = fp.world = compute(idx, live), { per, cellState, cellWho } = world;
  const now = live ? Date.now() : idx ? fp.merged[idx - 1].t : Date.now();
  const f = fp.focus, members = fp.members, fr = fp.focusRoom;
  const lit = new Map();
  for (const c of fp.cells.values()) {
    const st = cellState[c.key], mine = f != null && st && per[f].touched.has(c.key), dim = f != null && st && !mine;
    if (st) { const l = lit.get(c.room.name) ?? { read: 0, edit: 0 }; l[st]++; lit.set(c.room.name, l); }
    const el = cellEl(c);
    if (!el) continue;
    el.setAttribute('class', `fl-cell ${st ?? ''}${st && fp.shared.has(c.key) && cellWho[c.key]?.size > 1 ? ' shared' : ''}${dim ? ' dim' : ''}`);
  }
  moreLabels();
  for (const [name, el] of fp.roomEls) {
    const l = lit.get(name);
    el.classList.toggle('active', !!l);
    el.classList.toggle('dim', fr != null && fr !== name);
    const b = fp.geometry.rooms.find((r) => r.name === name) ?? fp.geometry.hall;
    const n = fp.nEls.get(name);
    if (n) {
      n.textContent = [b.hall ? '' : plural(b.files, 'file'), l && `${l.read + l.edit} lit, ${l.edit} edited`].filter(Boolean).join(' · ');
      for (const t of [n, n.nameEl, fp.moreEls.get(name)]) t?.classList.toggle('dim', fr != null && fr !== name);
    }
  }
  const g = fp.geometry;
  per.forEach((p, i) => {
    const person = fp.people[i];
    if (!person) return;
    // On the street in front of the building it's in, or on the paving stone it's on out on the plaza; the ones in no
    // building yet wait at the plaza's edge, spread out.
    const door = !p.pos ? [g.hall.door[0] - 90 + i * 60, g.hall.door[1] + 14] : p.pos.room.hall ? [p.pos.x, p.pos.y] : p.pos.room.door;
    const x = door[0] + (p.pos && !p.pos.room.hall ? (i % 3 - 1) * 14 : 0), y = door[1] + (p.pos && !p.pos.room.hall ? Math.floor(i / 3) * 10 : 0);
    person.setAttribute('class', `fl-person ${p.state}${x > W * 0.68 ? ' flip' : ''}${f != null && f !== i ? ' dim' : ''}${f === i ? ' focus' : ''}`);
    // Someone who hasn't started stays off the street; someone who stopped stays where they were, faded.
    if (!p.lastT && p.state === 'off') person.setAttribute('hidden', ''); else person.removeAttribute('hidden');
    person.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px)`;
    const bubble = person.querySelector('.bubble');
    bubble.textContent = p.short;
    if (x > W * 0.68) { bubble.setAttribute('x', -16); bubble.setAttribute('text-anchor', 'end'); } else { bubble.setAttribute('x', 16); bubble.setAttribute('text-anchor', 'start'); }
    person.querySelector('.badge-t').textContent = p.state === 'needs' ? '?' : p.state === 'done' ? '✓' : '';
    const tr = p.trail.slice(-6).map((c) => c.room.hall ? [c.x, c.y] : c.room.door);
    if (p.pos) tr.push([x, y]);
    fp.trailEls[i]?.setAttribute('d', tr.length > 1 ? tr.map((d, j) => `${j ? 'L' : 'M'}${d[0].toFixed(1)},${d[1].toFixed(1)}`).join('') : '');
    fp.trailEls[i]?.setAttribute('class', `fl-trail ${p.state}${f != null && f !== i ? ' dim' : ''}`);
  });
  // Whoever needs your answer, at the top. The answer goes in the session itself: this page can't type into it.
  const attn = $('#attn');
  attn.replaceChildren();
  per.forEach((p, i) => {
    if (p.state !== 'needs') return;
    const s = members[i];
    const question = p.asked ? p.asked.replace(/^Asking you: /, '').replace(/^“|”$/g, '') : 'It’s waiting for your answer in its terminal.';
    attn.append(h('div', { class: 'ask' }, h('div', { class: 'fl-av', style: `background:${COLORS[i % COLORS.length]}` }, fp.initials[i]),
      h('div', {}, h('div', { class: 'who' }, `${s.name}${p.lastT ? ' · asked ' + ago(p.lastT, now) : ''}`), h('div', { class: 'q' }, question)),
      h('button', { class: 'btn primary', onclick: () => showSession(s) }, 'Answer in the session')));
  });
  const n = per.filter((p) => p.state === 'needs').length, d = per.filter((p) => p.state === 'done').length, w = per.filter((p) => p.state === 'busy').length;
  $('#summary').replaceChildren(h('span', { class: 'n' }, h('b', {}, String(n)), n === 1 ? ' needs you' : ' need you'), h('span', { class: 'd' }, h('b', {}, String(d)), ' finished'), h('span', { class: 'w' }, h('b', {}, String(w)), ' working'));
  // The cards: whoever needs you first.
  const order = per.map((p, i) => i).sort((a, b) => rank(per[a].state) - rank(per[b].state) || (per[b].lastT ?? 0) - (per[a].lastT ?? 0));
  const rail = $('#rail');
  rail.replaceChildren();
  for (const i of order) {
    const s = members[i], p = per[i];
    const label = { needs: 'Needs your answer', done: 'Finished, waiting for you', busy: 'Working', idle: 'Idle',
      off: !live ? 'Not started yet' : fp.data.get(s.id)?.found === false ? 'Not running, no conversation yet' : 'Not running' }[p.state];
    const card = h('div', { class: `card ${p.state}${f === i ? ' focus' : ''}`, tabindex: '0', role: 'button', 'aria-expanded': String(f === i),
      onclick: () => focus(f === i ? null : i), onkeydown: (e) => { if (e.key === 'Enter') { e.preventDefault(); focus(f === i ? null : i); } } },
      h('div', { class: 'ch' }, h('div', { class: 'fl-av', style: `background:${COLORS[i % COLORS.length]}` }, fp.initials[i]), h('span', { class: 'cname' }, s.name), h('span', { class: 'chip ' + p.state }, label)));
    if (p.lastT) {
      const nowEl = h('div', { class: 'now' });
      nowEl.innerHTML = p.text;
      card.append(nowEl, h('div', { class: 'ago' }, `${ago(p.lastT, now)} · ${plural(p.touched.size, 'file')} touched · ${plural(p.turns.length, 'turn')}`));
    }
    if (f === i && p.turns.length) {
      const turns = h('div', { class: 'turns' });
      for (const tn of p.turns.slice(-6).reverse()) {
        const did = h('div', { class: 'did' }); did.innerHTML = turnSummary(tn);
        const out = tn.outcome ? h('div', { class: 'out ' + tn.outcome.state }) : null;
        if (out) out.innerHTML = tn.outcome.text;
        turns.append(h('div', { class: 'turn' }, h('time', {}, clock(tn.t)), h('div', {}, h('div', { class: 'you', title: tn.you ?? '' }, tn.you ? `You: ${tn.you}` : 'Picked up where it left off'), did, out)));
      }
      card.append(turns);
      const shared = [...p.touched.keys()].filter((k) => fp.shared.has(k) && cellWho[k]?.size > 1);
      if (shared.length) card.append(h('div', { class: 'shared' }, 'Also touched by another session: ', ...shared.slice(0, 3).flatMap((k, j) => [j ? ', ' : '', h('span', { class: 'f' }, fp.cells.get(k).name)]), shared.length > 3 ? ` and ${shared.length - 3} more` : ''));
    }
    if (f === i) card.append(h('button', { class: 'btn go', onclick: (e) => { e.stopPropagation(); showSession(s); } }, 'Go to the session'));
    rail.append(card);
  }
  // The slider, the clock and the lanes.
  const total = liveIdx(), range = $('#range');
  range.max = total; range.value = idx;
  $('#playhead').style.left = (total ? idx / total * 100 : 0) + '%';
  $('#pos').textContent = live ? 'now' : idx ? `${day(now)} ${clock(now)}` : 'the start';
  $('#live').classList.toggle('on', live);
  if (fp.lanesLen !== total) { fp.lanesLen = total; lanes(); days(); }
  // Zoomed in on a building, its lit files get their names; on a session, the files it was at last.
  const k = zoom();
  const room = fr != null ? fp.geometry.rooms.find((r) => r.name === fr) ?? fp.geometry.hall : null;
  const labelled = room ? recentCells([...fp.cells.values()].filter((c) => c.room === room), cellWho, MAX_LABELS * 2)
    : f != null ? recentCells([...per[f].touched.keys()].map((key) => fp.cells.get(key)).filter(Boolean), cellWho, MAX_LABELS) : [];
  labelFiles(labelled, cellState, k);
  if (fp.unzoom) fp.unzoom.hidden = f == null && fr == null;
}

/** Zooms the city to one building and names the files lit in it. */
function focusRoom(name) { fp.focusRoom = name; fp.focus = null; render(); }

function showSession(s) {
  ask('showSession', { sessionId: s.id }).catch(() => load());   // gone since: read the sessions again
}

/** Zooms the city to where the focused session has been; the others fade. */
function focus(i) { fp.focus = i; if (i != null) fp.focusRoom = null; render(); }

/** Zooms the stage to the focused building or session's whereabouts; gives back the magnification (1 for the whole city). */
function zoom() {
  const stage = fp.stage, host = $('#stage');
  if (!stage) return 1;
  const p = fp.focus != null ? fp.world?.per[fp.focus] : null;
  const room = fp.focusRoom != null ? fp.geometry.rooms.find((r) => r.name === fp.focusRoom) ?? fp.geometry.hall : null;
  // A building gets room either side for its file labels.
  const points = room ? [[room.left - 110, room.topY - 10], [room.right + 110, room.bottomY + 30]]
    : p ? [...p.trail.slice(-8), p.pos].filter(Boolean).flatMap((c) => [c.room.door, c.room.faces.top[0], c.room.faces.top[2], [c.room.left - 90, c.y], [c.room.right + 90, c.y]]) : [];
  if (!points.length) { stage.style.transform = ''; stage.classList.remove('zoomed'); return 1; }
  const xs = points.map((d) => d[0]), ys = points.map((d) => d[1]);
  const bx = Math.min(...xs) - 20, by = Math.min(...ys) - 30, bw = Math.max(...xs) - Math.min(...xs) + 40, bh = Math.max(...ys) - Math.min(...ys) + 70;
  const k = Math.min(2.6, Math.max(1.25, Math.min(W / bw, H / bh)));
  const cx = bx + bw / 2, cy = by + bh / 2, u = host.clientWidth / W;
  let tx = host.clientWidth / 2 - cx * u * k, ty = host.clientHeight / 2 - cy * u * k;
  tx = Math.min(0, Math.max(host.clientWidth - W * u * k, tx)); ty = Math.min(0, Math.max(host.clientHeight - H * u * k, ty));
  stage.style.transform = `translate(${tx}px, ${ty}px) scale(${k})`;
  stage.classList.toggle('zoomed', k >= 1.8);
  return k;
}

/** A lane per session under the slider, a tick per event (your prompts brighter), so you can see who was busy when. */
function lanes() {
  const cv = $('#lanes'), r = cv.getBoundingClientRect();
  if (!r.width) return;
  cv.width = Math.round(r.width * devicePixelRatio); cv.height = Math.round(r.height * devicePixelRatio);
  const ctx = cv.getContext('2d');
  ctx.scale(devicePixelRatio, devicePixelRatio);
  const merged = fp.merged, lane = r.height / Math.max(1, fp.members.length), cs = getComputedStyle(document.documentElement);
  merged.forEach((e, i) => {
    ctx.fillStyle = cs.getPropertyValue(`--s${(e.si % COLORS.length) + 1}`).trim();
    ctx.globalAlpha = e.k === 'prompt' ? 1 : .45;
    ctx.fillRect(i / merged.length * r.width, e.si * lane + 2, 1.2, lane - 4);
  });
}

function days() {
  const el = $('#days');
  el.replaceChildren();
  let last = '';
  for (const e of fp.merged) { const d = day(e.t); if (d !== last) { el.append(h('span', {}, d)); last = d; } }
}

function stop() { fp.playing = false; clearInterval(fp.playTimer); fp.playTimer = null; $('#play').textContent = '▶ Replay'; }
function play() {
  if (fp.playing) return stop();
  if (curIdx() >= liveIdx()) fp.idx = 0;
  fp.playing = true;
  $('#play').textContent = '❚❚ Pause';
  fp.playTimer = setInterval(() => {
    fp.idx = Math.min(liveIdx(), curIdx() + 1);
    if (fp.idx >= liveIdx()) { fp.idx = null; stop(); }
    render();
  }, 80);
}
function goLive() { stop(); fp.idx = null; render(); }

// Tooltips for windows and beacons.
const tip = $('#tip');
$('#stage').addEventListener('mousemove', (e) => {
  const cell = e.target.closest?.('.fl-cell'), person = e.target.closest?.('.fl-person'), world = fp.world;
  if (cell && world) {
    const c = fp.cells.get(cell.dataset.key), who = world.cellWho[c.key], now = fp.idx == null ? Date.now() : fp.merged[curIdx() - 1]?.t ?? Date.now();
    tip.innerHTML = `<div class="p">${esc(c.path)}</div>` + (who ? [...who].map(([si, v]) => `<div class="w">${esc(fp.members[si]?.name ?? '')} · ${v.k === 'edit' ? 'edited' : 'read'} ${ago(v.t, now)}</div>`).join('') : '<div class="w">Not touched yet</div>');
  } else if (person && world) {
    const p = world.per[Number(person.dataset.si)];
    tip.innerHTML = `<div>${p.text || esc(p.short)}</div><div class="w">Click to zoom in</div>`;
  } else if (e.target.classList?.contains('face') && world) {
    // A building: what's lit in it and the files touched there last.
    const name = e.target.closest('.fl-bld').dataset.room, room = fp.geometry.rooms.find((r) => r.name === name) ?? fp.geometry.hall;
    const mine = [...fp.cells.values()].filter((c) => c.room === room), lit = mine.filter((c) => world.cellState[c.key]);
    const edited = lit.filter((c) => world.cellState[c.key] === 'edit').length;
    const now = fp.idx == null ? Date.now() : fp.merged[curIdx() - 1]?.t ?? Date.now();
    const recent = recentCells(mine, world.cellWho, 8).map((c) => {
      const [si, v] = [...world.cellWho[c.key]].sort((a, b) => b[1].t - a[1].t)[0];
      return `<div class="w"><span class="p">${esc(c.name)}</span> · ${esc(fp.members[si]?.name ?? '')} ${v.k === 'edit' ? 'edited' : 'read'} ${ago(v.t, now)}</div>`;
    }).join('');
    tip.innerHTML = `<div><b>${esc(name)}</b>${room.hall ? '' : ` · ${plural(room.files, 'file')}`}</div><div class="w">${lit.length ? `${plural(lit.length, 'file')} touched, ${edited} edited` : 'Nothing touched here yet'}</div>${recent}${lit.length > 8 ? `<div class="w">and ${lit.length - 8} more</div>` : ''}<div class="w">Click to zoom in and name the files</div>`;
  } else { tip.hidden = true; return; }
  tip.hidden = false;
  tip.style.left = Math.min(e.clientX + 14, innerWidth - tip.offsetWidth - 8) + 'px';
  tip.style.top = Math.max(4, e.clientY - tip.offsetHeight - 10) + 'px';
});
$('#stage').addEventListener('mouseleave', () => { tip.hidden = true; });
$('#stage').addEventListener('click', (e) => { if (e.target.tagName === 'svg' || e.target.tagName === 'line') { fp.focusRoom = null; focus(null); } });

$('#play').addEventListener('click', play);
$('#live').addEventListener('click', goLive);
$('#range').addEventListener('input', (e) => { stop(); const v = Number(e.target.value); fp.idx = v >= liveIdx() ? null : v; render(); });
addEventListener('keydown', (e) => {
  if (e.target.matches('input:not([type=range]), textarea, select')) return;
  if (e.key === ' ') { e.preventDefault(); play(); }
  else if (e.key === 'Escape' && (fp.focus != null || fp.focusRoom != null)) { fp.focusRoom = null; focus(null); }
  else if (e.key === 'End') goLive();
  else if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
    e.preventDefault(); stop();
    const v = Math.max(0, Math.min(liveIdx(), curIdx() + (e.shiftKey ? 20 : 1) * (e.key === 'ArrowLeft' ? -1 : 1)));
    fp.idx = v >= liveIdx() ? null : v;
    render();
  } else if (/^[1-9]$/.test(e.key) && Number(e.key) <= fp.members.length) focus(Number(e.key) - 1);
});
addEventListener('resize', () => { fp.lanesLen = -1; render(); });

// Start: the theme the app gave on the address, then a read every few seconds while the page is showing.
document.documentElement.dataset.theme = new URLSearchParams(location.search).get('theme') === 'light' ? 'light' : 'dark';
render();
load();
setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
