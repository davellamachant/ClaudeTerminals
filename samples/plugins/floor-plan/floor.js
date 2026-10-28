'use strict';
// The Floor plan: a project's sessions on a map of its code. The repository's top-level folders are rooms, sized by how
// many files they hold; every file a session has read or edited is a cell in its room (blue read, amber edited, outlined
// when two sessions touched it); each session is a person standing at the file it's on, and a card beside the floor
// tells its story turn by turn. Whoever needs your answer is at the top. The slider at the bottom rewinds through
// everything that happened; Live follows what's happening now.
//
// It's a Claude Terminals plugin with "sessions" access (see plugin.json): it asks the app for the sessions, what Claude
// did in each (read from their conversations) and the repository's folders, every few seconds.

const REFRESH_MS = 2500, FOLDERS_MS = 60_000, MAX_EVENTS = 6000;
const W = 1000, H = 600, HALL = 50, PAD = 6, CELL = 9, GAP = 3;
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
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  for (const k of kids.flat()) if (k != null) e.append(k instanceof Node ? k : String(k));
  return e;
}

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

function setTheme(theme) {
  document.documentElement.dataset.theme = theme === 'light' ? 'light' : 'dark';
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
    setTheme(m.theme);
  } else if (m.type === 'claude-terminals:command' && m.context && 'project' in m.context) {
    // Opened from a project's or a session's menu: that project's floor.
    choose(m.context.project ?? NO_PROJECT);
  }
});

// ---- state

const fp = {
  sessions: [], projectKey: null, members: [], data: new Map(),   // session id → { conversation, version, found, events }
  folders: new Map(),   // worktree (lower case) → { at, root, folders } (root: the repository's main worktree)
  rooms: null, root: null, geometry: null, cells: new Map(), counts: new Map(), shared: new Set(),
  merged: [], idx: null, focus: null, playing: false, playTimer: null, loading: false, world: null, problem: null,
  stageKey: null, stage: null, cellEls: new Map(), moreEls: new Map(), roomEls: new Map(), trailEls: [], people: [], lanesLen: -1, initials: [],
};

const projectKey = (s) => s.project ?? NO_PROJECT;
const projectLabel = (key) => (key === NO_PROJECT ? 'Sessions in no project' : key);
const lower = (p) => p.replace(/\\/g, '/').replace(/\/$/, '').toLowerCase();

function choose(key) {
  if (fp.projectKey === key) return;
  Object.assign(fp, { projectKey: key, members: [], data: new Map(), rooms: null, root: null, geometry: null, cells: new Map(), counts: new Map(), shared: new Set(), merged: [], idx: null, focus: null, stageKey: null });
  stop();
  load();
}

/** The sessions, the rooms (now and then) and what's new in each conversation; then the floor is drawn again. */
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
    // Each worktree's repository, now and then: the floor is the repository most of the sessions are in.
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
    if ((main?.root ?? null) !== fp.root) Object.assign(fp, { root: main?.root ?? null, geometry: null, cells: new Map(), counts: new Map(), stageKey: null });
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

// ---- the floor: rooms, and a cell for every file as it first appears

/** Where a file is: its room (top-level folder of the floor's repository) and its path there, or outside it. */
function place(file, s) {
  if (!file) return null;
  const f = file.replace(/\\/g, '/'), low = f.toLowerCase(), name = f.split('/').pop();
  // A session's own worktree counts only if it's a worktree of the floor's repository: another repository's src\ isn't this one's.
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

/** All sessions' events in time order, with a cell for every file as it first appears, and which files two sessions share. */
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
      const cols = Math.max(1, Math.floor((room.w - 10) / (CELL + GAP)));
      // A room holds as many rows of cells as fit; files past that share the last slot, which says how many more there are.
      const rows = Math.max(1, Math.floor((room.h - (room.hall ? 18 : 22)) / (CELL + GAP)));
      const slot = Math.min(i, cols * rows - 1), overflow = i > cols * rows - 1;
      fp.cells.set(p.key, { key: p.key, col: slot % cols, x: room.x + 6 + (slot % cols) * (CELL + GAP), y: room.y + (room.hall ? 16 : 20) + Math.floor(slot / cols) * (CELL + GAP), room, name: p.name, path: p.rel, overflow });
    }
    (who.get(p.key) ?? who.set(p.key, new Set()).get(p.key)).add(e.si);
  }
  fp.shared = new Set([...who].filter(([, set]) => set.size > 1).map(([k]) => k));
}

/** The rooms as a squarified treemap (biggest first, top left), with a hallway along the bottom. A small room is drawn
 * bigger than its share (so it can hold some cells), but it's labelled with its own count. */
function geometry() {
  const list = fp.rooms ?? [];
  const kept = list.slice(0, 13).map((r) => ({ name: r.name, files: r.files }));
  const rest = list.slice(13).reduce((n, r) => n + r.files, 0);
  if (rest) kept.push({ name: '(other)', files: rest });
  const rooms = kept.length
    ? squarify(kept.map((r) => ({ ...r, n: Math.max(r.files, 60) })), PAD, PAD, W - 2 * PAD, H - HALL - 2 * PAD).map((r) => ({ ...r, x: r.x + 2, y: r.y + 2, w: r.w - 4, h: r.h - 4 }))
    : [];
  const hall = { name: 'Outside the repository', x: PAD + 2, y: H - HALL + 2, w: W - 2 * PAD - 4, h: HALL - PAD - 2, hall: true };
  fp.geometry = { rooms, hall, roomSet: new Set(rooms.map((r) => r.name)) };
}

function squarify(items, x, y, w, h) {
  const total = items.reduce((n, i) => n + i.n, 0), out = [], scale = (w * h) / total;
  let rest = items.slice(), rx = x, ry = y, rw = w, rh = h;
  while (rest.length) {
    const horiz = rw >= rh, side = horiz ? rh : rw;
    let row = [], best = Infinity;
    for (const it of rest) {
      const cand = [...row, it], len = cand.reduce((n, i) => n + i.n * scale, 0) / side;
      const worst = Math.max(...cand.map((i) => { const s = (i.n * scale) / len; return Math.max(len / s, s / len); }));
      if (worst <= best) { row = cand; best = worst; } else break;
    }
    const len = row.reduce((n, i) => n + i.n * scale, 0) / side;
    let off = 0;
    for (const it of row) { const s = (it.n * scale) / len; out.push(horiz ? { ...it, x: rx, y: ry + off, w: len, h: s } : { ...it, x: rx + off, y: ry, w: s, h: len }); off += s; }
    if (horiz) { rx += len; rw -= len; } else { ry += len; rh -= len; }
    rest = rest.slice(row.length);
  }
  return out;
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

/** What the app says a session is doing now (its status dot), as the floor's states. */
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
        p.pos = c; p.trail.push(c);
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

/** The stage (rooms, trails, people) is built once per repository and set of sessions; cells are added as files appear. */
function buildStage() {
  const g = fp.geometry, key = (fp.root ?? '') + '|' + fp.members.map((s) => s.id + ':' + s.name).join(',');
  if (fp.stageKey === key && fp.stage) return;
  fp.stageKey = key;
  fp.initials = initials(fp.members);
  const host = $('#stage');
  host.replaceChildren();
  const stage = h('div', { class: 'fl-stage' });
  const svg = sv('svg', { viewBox: `0 0 ${W} ${H}`, 'aria-label': 'Floor plan of the repository' });
  fp.roomEls = new Map(); fp.cellEls = new Map(); fp.moreEls = new Map();
  for (const r of [...g.rooms, g.hall]) {
    const el = sv('rect', { class: r.hall ? 'fl-hall' : 'fl-room', x: r.x, y: r.y, width: r.w, height: r.h, rx: 3 });
    svg.append(el); fp.roomEls.set(r.name, el);
    const fit = Math.floor((r.w - 12) / 6.4), label = r.name.length <= fit ? r.name : r.name.slice(0, Math.max(3, fit - 1)) + '…';
    svg.append(sv('text', { class: 'fl-room-name', x: r.x + 6, y: r.y + 12 }, label));
    if (!r.hall && r.w > 120) svg.append(sv('text', { class: 'fl-room-n', x: r.x + r.w - 6, y: r.y + 12, 'text-anchor': 'end' }, plural(r.files, 'file')));
  }
  fp.cellLayer = sv('g'); svg.append(fp.cellLayer);
  fp.trailEls = fp.members.map((s, i) => { const p = sv('path', { class: 'fl-trail', stroke: COLORS[i % COLORS.length], d: '' }); svg.append(p); return p; });
  if (!fp.rooms) {
    svg.append(sv('text', { class: 'fl-empty', x: W / 2, y: (H - HALL) / 2, 'text-anchor': 'middle' },
      fp.members.some((s) => s.worktree) ? 'Reading the repository…' : 'No session of this project is in a Git repository, so there are no rooms: everything happens in the hallway.'));
  }
  stage.append(svg);
  fp.people = fp.members.map((s, i) => {
    const colour = COLORS[i % COLORS.length];
    const p = h('div', { class: 'fl-person', 'data-si': i, hidden: true },
      h('div', { class: 'fl-av', style: `background:${colour};color:${colour}` }, h('span', { style: 'color:#fff' }, fp.initials[i])),
      h('div', { class: 'fl-badge' }), h('div', { class: 'fl-bubble' }));
    p.addEventListener('click', (e) => { e.stopPropagation(); focus(fp.focus === i ? null : i); });
    stage.append(p);
    return p;
  });
  host.append(stage);
  fp.unzoom = h('button', { class: 'btn unzoom', hidden: true, onclick: () => focus(null) }, '← Whole floor');
  host.append(fp.unzoom);
  fp.stage = stage;
}

/** The cell's rectangle (made on first sight) and its name, which shows when zoomed in; none for an overflow file. */
function cellEl(c) {
  if (c.overflow) return null;
  let el = fp.cellEls.get(c.key);
  if (el) return el;
  el = sv('rect', { class: 'fl-cell', x: c.x, y: c.y, width: CELL, height: CELL, rx: 1.5, 'data-key': c.key });
  // Names on neighbouring cells would overlap, so every other column's name sits a line lower.
  const name = sv('text', { class: 'fl-cell-name', x: c.x, y: c.y + CELL + (c.col % 2 ? 8.5 : 4) }, c.name.length > 22 ? c.name.slice(0, 21) + '…' : c.name);
  fp.cellLayer.append(el, name);
  fp.cellEls.set(c.key, el);
  el.nameEl = name;
  return el;
}

/** "+N more" after a room's last slot when more files were touched there than its cells can show. */
function moreLabels() {
  const more = new Map();
  for (const c of fp.cells.values()) if (c.overflow) more.set(c.room.name, (more.get(c.room.name) ?? 0) + 1);
  for (const [name, el] of fp.moreEls) if (!more.has(name)) { el.remove(); fp.moreEls.delete(name); }
  for (const [name, n] of more) {
    const slot = [...fp.cells.values()].find((c) => c.overflow && c.room.name === name);
    let el = fp.moreEls.get(name);
    if (!el) { el = sv('text', { class: 'fl-room-n', x: slot.x + CELL + 3, y: slot.y + CELL - 1 }); fp.cellLayer.append(el); fp.moreEls.set(name, el); }
    el.textContent = `+${n} more`;
  }
}

function showMessage(text, button) {
  const m = $('#message');
  m.hidden = !text;
  m.replaceChildren(...(text ? [h('div', {}, text), button ?? ''] : []));
  document.querySelector('.main').hidden = document.querySelector('.time').hidden = !!text;
}

function render() {
  if (fp.problem) { showMessage(/allowed/.test(fp.problem) ? 'The Floor plan isn’t allowed to see your sessions.' : `The Floor plan can’t see your sessions: ${fp.problem}`, h('div', { class: 'hint' }, 'You can let it from the palette menu: Plugins › Floor plan › Let it see your sessions.')); $('#attn').replaceChildren(); return; }
  if (!fp.sessions.length) { showMessage(fp.loading ? 'Reading your sessions…' : 'There are no sessions yet.'); return; }
  if (!fp.members.length) { showMessage(`${projectLabel(fp.projectKey)} has no sessions.`); return; }
  showMessage(null);
  if (!fp.geometry) geometry();
  buildStage();
  const live = fp.idx == null, idx = curIdx();
  const world = fp.world = compute(idx, live), { per, cellState, cellWho } = world;
  const now = live ? Date.now() : idx ? fp.merged[idx - 1].t : Date.now();
  const f = fp.focus, members = fp.members;
  const activeRooms = new Set();
  for (const c of fp.cells.values()) {
    const st = cellState[c.key], mine = f != null && st && per[f].touched.has(c.key), dim = f != null && st && !mine;
    if (st) activeRooms.add(c.room.name);
    const el = cellEl(c);
    if (!el) continue;
    el.setAttribute('class', `fl-cell ${st ?? ''}${st && fp.shared.has(c.key) && cellWho[c.key]?.size > 1 ? ' shared' : ''}${dim ? ' dim' : ''}`);
    el.nameEl.classList.toggle('mine', !!mine);
  }
  moreLabels();
  for (const [name, el] of fp.roomEls) el.classList.toggle('active', activeRooms.has(name));
  const hall = fp.geometry.hall;
  per.forEach((p, i) => {
    const person = fp.people[i];
    if (!person) return;
    const pos = p.pos ?? { x: hall.x + 60 + i * 70, y: hall.y + 28 };
    person.className = `fl-person ${p.state}${pos.x > W * 0.62 ? ' flip' : ''}${f != null && f !== i ? ' dim' : ''}${f === i ? ' focus' : ''}`;
    // Someone who hasn't started stays off the floor; someone who stopped stays where they were, faded.
    person.hidden = !p.lastT && p.state === 'off';
    person.style.left = (pos.x + CELL / 2) / W * 100 + '%';
    person.style.top = (pos.y + CELL / 2) / H * 100 + '%';
    person.querySelector('.fl-bubble').textContent = p.short;
    person.querySelector('.fl-badge').textContent = p.state === 'needs' ? '?' : p.state === 'done' ? '✓' : '';
    const tr = p.trail.slice(-6);
    fp.trailEls[i]?.setAttribute('d', tr.length > 1 ? tr.map((c, j) => `${j ? 'L' : 'M'}${c.x + CELL / 2},${c.y + CELL / 2}`).join('') : '');
    fp.trailEls[i]?.classList.toggle('dim', f != null && f !== i);
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
  zoom();
}

function showSession(s) {
  ask('showSession', { sessionId: s.id }).catch(() => load());   // gone since: read the sessions again
}

/** Zooms the floor to where the focused person has been; the others fade. */
function focus(i) { fp.focus = i; if (fp.unzoom) fp.unzoom.hidden = i == null; render(); }

function zoom() {
  const stage = fp.stage, host = $('#stage');
  if (!stage) return;
  const p = fp.focus != null ? fp.world?.per[fp.focus] : null;
  const pts = p ? [...p.trail.slice(-8), p.pos].filter(Boolean) : [];
  if (!pts.length) { stage.style.transform = ''; stage.style.setProperty('--k', 1); stage.classList.remove('zoomed'); return; }
  const xs = pts.map((c) => c.x), ys = pts.map((c) => c.y);
  const bx = Math.min(...xs) - 60, by = Math.min(...ys) - 40, bw = Math.max(...xs) - Math.min(...xs) + CELL + 120, bh = Math.max(...ys) - Math.min(...ys) + CELL + 80;
  const k = Math.min(3.2, Math.max(1.4, Math.min(W / bw, H / bh)));
  const cx = bx + bw / 2, cy = by + bh / 2, u = host.clientWidth / W;
  let tx = host.clientWidth / 2 - cx * u * k, ty = host.clientHeight / 2 - cy * u * k;
  tx = Math.min(0, Math.max(host.clientWidth - W * u * k, tx)); ty = Math.min(0, Math.max(host.clientHeight - H * u * k, ty));
  stage.style.transform = `translate(${tx}px, ${ty}px) scale(${k})`;
  stage.style.setProperty('--k', k);
  stage.classList.toggle('zoomed', k >= 2);
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

// Tooltips for cells and people.
const tip = $('#tip');
$('#stage').addEventListener('mousemove', (e) => {
  const cell = e.target.closest?.('.fl-cell'), person = e.target.closest?.('.fl-person'), world = fp.world;
  if (cell && world) {
    const c = fp.cells.get(cell.dataset.key), who = world.cellWho[c.key], now = fp.idx == null ? Date.now() : fp.merged[curIdx() - 1]?.t ?? Date.now();
    tip.innerHTML = `<div class="p">${esc(c.path)}</div>` + (who ? [...who].map(([si, v]) => `<div class="w">${esc(fp.members[si]?.name ?? '')} · ${v.k === 'edit' ? 'edited' : 'read'} ${ago(v.t, now)}</div>`).join('') : '<div class="w">Not touched yet</div>');
  } else if (person && world) {
    const p = world.per[Number(person.dataset.si)];
    tip.innerHTML = `<div>${p.text || esc(p.short)}</div><div class="w">Click to zoom in</div>`;
  } else { tip.hidden = true; return; }
  tip.hidden = false;
  tip.style.left = Math.min(e.clientX + 14, innerWidth - tip.offsetWidth - 8) + 'px';
  tip.style.top = Math.max(4, e.clientY - tip.offsetHeight - 10) + 'px';
});
$('#stage').addEventListener('mouseleave', () => { tip.hidden = true; });
$('#stage').addEventListener('click', (e) => { if (e.target.classList?.contains('fl-room') || e.target.classList?.contains('fl-hall') || e.target.tagName === 'svg') focus(null); });

$('#play').addEventListener('click', play);
$('#live').addEventListener('click', goLive);
$('#range').addEventListener('input', (e) => { stop(); const v = Number(e.target.value); fp.idx = v >= liveIdx() ? null : v; render(); });
addEventListener('keydown', (e) => {
  if (e.target.matches('input:not([type=range]), textarea, select')) return;
  if (e.key === ' ') { e.preventDefault(); play(); }
  else if (e.key === 'Escape' && fp.focus != null) focus(null);
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
