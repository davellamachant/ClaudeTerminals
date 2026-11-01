'use strict';
// Timeline: a live map of your sessions in the side panel. Each session is a line, time running left to right: rings
// are your prompts, the marks between them what Claude did (edits, reads, commands, tests, questions), the newest ring
// shows the session's state. Each row says what the session is doing now and what its replies cost in tokens.
// "This session" follows the open tab and lists its prompts. Everything is read from the conversation files through
// the app's "sessions" access, so it costs nothing; only the Summarise button runs Claude (the "ask" access), once
// per press, and the header counts those calls. The look follows the app's theme: dark, light, or the Grid when the
// app's Tron theme is on (the app says which, see the theme message).

const REFRESH_MS = 3000;
const CONTEXT_MAX = 200_000;   // what a session's context is measured against, unless it has gone past it (a 1M-context model)
const CONTEXT_BIG = 1_000_000;
const WINDOWS = [15, 60, 240, 720];

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
  } else if (m.type === 'claude-terminals:command') {
    // The title bar button or the palette item shows every session; a session's menu item follows that session.
    const c = m.context ?? {};
    if (m.command === 'one' || c.sessionId) { st.mode = 'one'; st.pinned = c.sessionId ?? null; }
    else if (m.command === 'all') { st.mode = 'all'; st.pinned = null; }
    remember();
    render();
  }
});

/** Dresses the page for the app's theme: its dark or light colours, or the Grid when one of the app's Tron themes is on (red for Ares). */
function dress(m) {
  const root = document.documentElement, id = m.id;
  root.dataset.theme = m.theme === 'light' ? 'light' : 'dark';
  const grid = id === 'tron-ares' ? 'ares' : id === 'matrix' ? 'matrix' : null;   // the Grid's colour: cyan, red, or Matrix green
  const tron = id === 'tron' || !!grid;
  root.dataset.look = tron ? 'tron' : 'app';
  if (grid) root.dataset.grid = grid; else delete root.dataset.grid;
  // The plain look takes the app's own colours and font, so it matches whatever theme is on (Nerds, one of yours); the Grid
  // looks paint their own, so nothing is set over them.
  const c = tron ? {} : (m.colors ?? {});
  for (const [mine, theirs] of Object.entries(TIMELINE_COLORS)) { if (c[theirs]) root.style.setProperty('--' + mine, c[theirs]); else root.style.removeProperty('--' + mine); }
  const heat = [['heat1', 14], ['heat2', 32], ['heat3', 60], ['heat4', 100]];
  for (const [name, pct] of heat) { if (c.accent && c.chrome) root.style.setProperty('--' + name, `color-mix(in srgb, ${c.accent} ${pct}%, ${c.chrome})`); else root.style.removeProperty('--' + name); }
  document.body.style.fontFamily = !tron && m.font ? `"${m.font}", "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif` : '';
  if (!tron && m.glow) root.dataset.glow = ''; else delete root.dataset.glow;
}
/** This page's colours by the app's names for them (see the theme message in the app's README). */
const TIMELINE_COLORS = { bg: 'chrome', panel: 'panel', border: 'border', 'border-soft': 'border-soft', hover: 'hover-strong', text: 'text', 'text-dim': 'text-dim', 'text-faint': 'text-faint',
  code: 'code-bg', line: 'border-strong', station: 'text-dim', accent: 'accent-strong', danger: 'danger', working: 'working', asking: 'asking', attention: 'attention', idle: 'ok', stopped: 'text-faint' };

const opened = new URLSearchParams(location.search);   // the app says the theme on the address too, so the first paint matches
dress({ theme: opened.get('theme'), id: opened.get('look') });

// ---- state

const stored = (key, fallback) => { try { return JSON.parse(localStorage.getItem(key)) ?? fallback; } catch { return fallback; } };
const st = {
  sessions: [],                              // what the app says about each session (see the app's pluginSessionInfo)
  data: new Map(),                           // session id → { conversation, version, found, events: [{ t (ms), k, file, text, ok, q, ctx, out }] }
  mode: stored('timeline.mode', 'all'),      // 'all' | 'one'
  open: new Set(),                           // 'session|prompt|kind' counts opened into what happened (This session)
  pinned: null,                              // in 'one' mode, a session chosen from its menu; otherwise the open tab's
  window: stored('timeline.window', 60),     // minutes shown
  summary: null,                             // { pending, error, text, when, names } from the last Summarise
  calls: stored('timeline.calls', null),     // { day, n }: Summarise presses today
  denied: null,                              // why the sessions can't be read, if they can't
  help: false,
};
if (!WINDOWS.includes(st.window)) st.window = 60;
if (st.mode !== 'one') st.mode = 'all';
const remember = () => { try { localStorage.setItem('timeline.mode', JSON.stringify(st.mode)); localStorage.setItem('timeline.window', JSON.stringify(st.window)); } catch { /* fine without */ } };

const byId = (id) => st.sessions.find((s) => s.id === id);
const events = (s) => st.data.get(s.id)?.events ?? [];
const basename = (file) => (file ?? '').split(/[\\/]/).pop();
const today = () => new Date().toISOString().slice(0, 10);
const callsToday = () => (st.calls?.day === today() ? st.calls.n : 0);

// ---- keeping up with the sessions

let timer = null;
async function refresh() {
  clearTimeout(timer);
  try {
    st.sessions = await ask('sessions');
    const have = {};
    for (const [id, d] of st.data) have[id] = { conversation: d.conversation, version: d.version };
    const results = await ask('activity', { ids: st.sessions.map((s) => s.id), have });
    for (const r of results) {
      const d = st.data.get(r.id) ?? { conversation: null, version: 0, found: false, events: [] };
      const fresh = r.events.map((e) => ({ ...e, t: Date.parse(e.t) }));
      if (!r.found) d.events = [];
      else if (d.conversation !== r.conversation || r.from > d.events.length) d.events = fresh;
      else d.events = d.events.slice(0, r.from).concat(fresh);
      Object.assign(d, { conversation: r.conversation, version: r.version, found: r.found });
      st.data.set(r.id, d);
    }
    for (const id of [...st.data.keys()]) if (!byId(id)) st.data.delete(id);
    st.denied = null;
  } catch (e) {
    st.denied = e.message;
  }
  render();
  if (!document.hidden) timer = setTimeout(refresh, REFRESH_MS);
}

document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });

// ---- what a session is doing, from its events

const BEAD = { read: 'read', search: 'read', edit: 'edit', run: 'run', build: 'run', agent: 'run', other: 'run', test: 'test', ask: 'ask', compact: 'compact' };
const BEAD_NAME = { read: 'read', edit: 'edit', run: 'command', test: 'test run', ask: 'question', compact: 'compaction' };
const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;

/** How long ago a moment (ms since the epoch) was, in words. */
function ago(t) {
  const m = Math.round((Date.now() - t) / 60000);
  if (m < 1) return 'just now';
  if (m < 60) return `${m} min ago`;
  const hrs = Math.floor(m / 60);
  return hrs < 24 ? `${hrs} h ${m % 60} min ago` : `${Math.floor(hrs / 24)} d ago`;
}
function span(ms) {
  const m = Math.round(ms / 60000);
  if (m < 1) return 'under a minute';
  if (m < 60) return `${m} min`;
  return `${Math.floor(m / 60)} h ${m % 60} min`;
}
const k = (n) => n >= 1_000_000 ? (n / 1_000_000).toFixed(1) + 'M' : n >= 1000 ? Math.round(n / 1000) + 'k' : String(n);

/** What one tool event reads as in a line: "editing MainForm.cs", "running tests: Build the solution". */
function doing(e) {
  switch (e.k) {
    case 'edit': return 'editing ' + basename(e.file);
    case 'read': return 'reading ' + (e.file ? basename(e.file) : 'files');
    case 'search': return 'searching for ' + (e.text ?? '');
    case 'test': return 'running tests' + (e.text ? ': ' + e.text : '');
    case 'build': return 'building' + (e.text ? ': ' + e.text : '');
    case 'run': return 'running: ' + (e.text ?? 'a command');
    case 'agent': return 'a helper: ' + (e.text ?? '');
    case 'ask': return 'asking: ' + (e.text ?? 'a question');
    case 'compact': return 'compacting the conversation';
    default: return e.text ?? e.k;
  }
}

/** The row's one-liners: its state and what it's on now. */
function describe(s) {
  const ev = events(s), now = Date.now();
  const last = ev[ev.length - 1];
  const lastPrompt = [...ev].reverse().find((e) => e.k === 'prompt');
  const turn = lastPrompt ? ev.slice(ev.indexOf(lastPrompt) + 1) : [];
  const lastTool = [...turn].reverse().find((e) => e.k !== 'say');
  const lastSay = [...ev].reverse().find((e) => e.k === 'say');
  let state, intent;
  switch (s.status) {
    case 'working':
      state = 'working' + (lastPrompt ? ` · ${span(now - lastPrompt.t)} this turn` : '');
      intent = lastTool ? doing(lastTool) : (lastPrompt ? 'on: ' + lastPrompt.text : 'starting');
      break;
    case 'asking':
      state = 'needs you' + (last ? ` · waiting ${span(now - last.t)}` : '');
      intent = lastTool?.k === 'ask' ? doing(lastTool) : lastSay?.q ? 'asking: ' + lastSay.text : 'waiting for your answer';
      break;
    case 'attention':
      state = 'finished' + (last ? ` · ${ago(last.t)}` : '');
      intent = lastSay ? lastSay.text : lastTool ? 'last: ' + doing(lastTool) : '';
      break;
    case 'running':
      state = 'idle' + (last ? ` · last ${ago(last.t)}` : '');
      intent = lastSay ? lastSay.text : lastPrompt ? 'last: ' + lastPrompt.text : 'waiting for a prompt';
      break;
    default:
      state = 'not running' + (!st.data.get(s.id)?.found ? ' · no conversation' : '');
      intent = lastSay ? 'last: ' + lastSay.text : lastPrompt ? 'last: ' + lastPrompt.text : '';
  }
  return { state, intent, lastPrompt };
}

/** The tokens a session is using: its context at the last reply, and what it wrote in the window. */
function cost(s, since) {
  const ev = events(s);
  const ctx = [...ev].reverse().find((e) => e.ctx != null)?.ctx ?? null;
  let out = 0;
  for (const e of ev) if (e.t >= since && e.out) out += e.out;
  return { ctx, out };
}

// ---- the map

const esc = (s) => String(s ?? '');

function axis(since) {
  const w = st.window, step = w <= 15 ? 5 : w <= 60 ? 10 : w <= 240 ? 60 : 120;
  const el = h('div', { class: 'axis' });
  for (let m = w; m > 0; m -= step) el.append(h('span', { style: `left:${((w - m) / w * 100).toFixed(2)}%` }, m >= 60 && m % 60 === 0 ? `-${m / 60} h` : `-${m} min`));
  el.append(h('span', { class: 'now', style: 'left:100%' }, 'now'));
  return el;
}

/** The map's beads for a session's events: those in the window, neighbours of a kind merged into one with a count. */
function beads(ev, since, width) {
  const w = st.window * 60000, px = w / Math.max(100, width);   // ms per pixel
  const out = [];
  for (const e of ev) {
    if (e.t < since || e.k === 'prompt' || e.k === 'say') continue;
    const kind = BEAD[e.k];
    if (!kind) continue;
    const last = out[out.length - 1];
    if (last && last.kind === kind && e.t - last.t < px * 7) { last.n++; last.t = e.t; last.bad ||= e.ok === false; continue; }
    out.push({ kind, t: e.t, n: 1, bad: e.ok === false, text: doing(e) });
  }
  return out;
}

function track(s, since, width) {
  const ev = events(s), w = st.window * 60000;
  const x = (t) => (Math.max(0, t - since) / w * 100).toFixed(2) + '%';
  const el = h('div', { class: 'track' });
  const inWindow = ev.filter((e) => e.t >= since);
  // The prompts in the window, and the one before it that the window's first events belong to (drawn at the left edge).
  const earlier = [...ev].reverse().find((e) => e.k === 'prompt' && e.t < since);
  const prompts = (earlier && inWindow.length ? [earlier] : []).concat(inWindow.filter((e) => e.k === 'prompt'));
  const { state, intent } = describe(s);
  if (inWindow.length) {
    const first = inWindow[0].t, last = inWindow[inWindow.length - 1].t;
    el.append(h('div', { class: 'line', style: `left:${x(first)};width:calc(${x(last)} - ${x(first)})` }));
    if (s.status === 'working') {
      el.append(h('div', { class: 'line flow', style: `left:${x(last)};right:0` }));
      el.append(h('div', { class: 'cycle', style: `left:${x(first)};right:0` }));   // the Grid's light cycle rides the whole line
    } else if (s.status === 'asking') el.append(h('div', { class: 'line wait', style: `left:${x(last)};right:0` }));
    for (const b of beads(ev, since, width))
      el.append(h('span', { class: `bead ${b.kind}${b.n > 1 ? ' many' : ''}${b.bad ? ' bad' : ''}`, 'data-n': b.n > 1 ? b.n : null, style: `left:${x(b.t)}`,
        title: (b.n > 1 ? plural(b.n, BEAD_NAME[b.kind]) + ', last ' : '') + b.text + ' · ' + ago(b.t) }));
    prompts.forEach((p, i) => {
      const head = i === prompts.length - 1 && ev[ev.length - 1].t >= since;
      const turn = ev.slice(ev.indexOf(p) + 1, i + 1 < prompts.length ? ev.indexOf(prompts[i + 1]) : ev.length);
      el.append(h('span', { class: 'st' + (head ? ' head ' + s.status : ''), style: `left:${x(p.t)}`,
        'data-prompt': p.text, 'data-did': didOf(turn), 'data-when': ago(p.t),
        'data-session': s.id, 'data-at': new Date(p.t).toISOString(), 'data-index': ev.filter((e) => e.k === 'prompt').indexOf(p) }));
    });
  }
  el.append(h('div', { class: 'intent' }, s.status === 'working' ? [h('span', { class: 'now' }, intent)] : intent));
  return el;
}

/** What a turn came to, for a station's card: what Claude said at its end, else the last thing it did. */
function didOf(turn) {
  const say = [...turn].reverse().find((e) => e.k === 'say');
  if (say) return say.text;
  const tool = turn[turn.length - 1];
  return tool ? doing(tool) : '';
}

function gauge(s, since) {
  const { ctx, out } = cost(s, since);
  if (ctx == null && !out) return null;
  const max = (ctx ?? 0) > CONTEXT_MAX ? CONTEXT_BIG : CONTEXT_MAX;
  const pct = Math.min(100, (ctx ?? 0) / max * 100);
  return h('span', { class: 'ctx', title: `${ctx != null ? k(ctx) + ' tokens in context at its last reply, of ' + k(max) : 'no reply yet'} · ${k(out)} written in the last ${st.window >= 60 ? st.window / 60 + ' h' : st.window + ' min'}` },
    h('span', { class: 'gauge' }, h('i', { class: pct > 70 ? 'warm' : '', style: `width:${pct.toFixed(0)}%` })),
    ctx != null ? [h('b', {}, k(ctx)), ' ctx · '] : '', k(out) + ' out');
}

function row(s, since, width) {
  const { state } = describe(s);
  const stopped = !events(s).some((e) => e.t >= since);
  return h('div', { class: 'row' + (stopped ? ' stopped' : '') },
    h('div', { class: 'who' },
      h('span', { class: 'name', title: 'Open ' + s.name, onclick: () => ask('showSession', { sessionId: s.id }).catch((e) => note(e.message, true)) }, h('span', { class: 'dot ' + s.status }), s.name),
      h('span', { class: 'state' }, state),
      gauge(s, since)),
    track(s, since, width));
}

function stops(s, since) {
  const ev = events(s);
  const prompts = ev.map((e, i) => [e, i]).filter(([e]) => e.k === 'prompt' && e.t >= since).reverse();
  if (!prompts.length) return h('div', { class: 'message' }, 'No prompts in this window.');
  const list = h('div', { class: 'stops' });
  prompts.forEach(([p, i], n) => {
    const end = n === 0 ? ev.length : prompts[n - 1][1];
    const turn = ev.slice(i + 1, end);
    const groups = {};   // bead kind → the turn's events of that kind, so a count can open into what happened
    let out = 0;
    for (const e of turn) { const kind = BEAD[e.k]; if (kind) (groups[kind] ??= []).push(e); out += e.out ?? 0; }
    const live = n === 0 && (s.status === 'working' || s.status === 'asking');
    const promptIndex = ev.slice(0, i + 1).filter((e) => e.k === 'prompt').length - 1;
    const keyOf = (kind) => `${s.id}|${promptIndex}|${kind}`;
    const openKinds = Object.keys(groups).filter((kind) => st.open.has(keyOf(kind)));
    list.append(h('div', { class: 'stop' + (live ? ' ' + s.status : '') },
      h('div', { class: 't' }, ago(p.t)),
      h('div', { class: 'rail' }, h('i')),
      h('div', { class: 'body' },
        h('div', { class: 'prompt', title: 'Show this prompt in the session', 'data-session': s.id, 'data-at': new Date(p.t).toISOString(), 'data-index': promptIndex }, p.text),
        h('div', { class: 'did' }, n === 0 && s.status === 'working' ? describe(s).intent : didOf(turn)),
        (Object.keys(groups).length || out) ? h('div', { class: 'beads' },
          ...Object.entries(groups).map(([kind, list]) => h('button', {
            class: 'count' + (st.open.has(keyOf(kind)) ? ' on' : ''), title: `${st.open.has(keyOf(kind)) ? 'Hide' : 'Show'} the ${plural(list.length, BEAD_NAME[kind])}`,
            onclick: () => { const key = keyOf(kind); if (!st.open.delete(key)) st.open.add(key); render(); },
          }, h('span', { class: 'bead ' + kind }), plural(list.length, BEAD_NAME[kind]))),
          out ? h('span', { class: 'cost' }, k(out) + ' tokens written') : null) : null,
        ...openKinds.map((kind) => detail(kind, groups[kind])))));
  });
  return list;
}

/** What happened, one line each, under a count that was opened: the file, command, search or question, when, and a failure. */
function detail(kind, list) {
  return h('ul', { class: 'detail' },
    ...list.map((e) => h('li', { class: e.ok === false ? 'bad' : '', title: [e.file, e.text].filter(Boolean).join('\n') },
      h('span', { class: 'bead ' + kind }), h('span', { class: 'what' }, doing(e)), h('span', { class: 't' }, ago(e.t)))));
}

function tokensLine(list, since) {
  let ctx = 0, out = 0, replies = 0;
  for (const s of list) { const c = cost(s, since); if (c.ctx != null) { ctx += c.ctx; replies++; } out += c.out; }
  const el = $('#tokens');
  el.replaceChildren();
  if (!replies && !out) return;
  el.append(h('b', {}, k(ctx)), ' ctx', h('span', { class: 'sep' }, '|'), h('b', {}, k(out)), ' out');
}

function files(list, since) {
  const touched = new Map();   // file → { n, sessions: Set }
  for (const s of list) for (const e of events(s)) {
    if (e.k !== 'edit' || !e.file || e.t < since) continue;
    const f = touched.get(e.file) ?? { n: 0, sessions: new Set() };
    f.n++; f.sessions.add(s.name);
    touched.set(e.file, f);
  }
  const el = $('#files');
  el.hidden = touched.size === 0;
  if (el.hidden) return;
  const max = Math.max(...[...touched.values()].map((f) => f.n));
  el.replaceChildren(h('h3', {}, 'Files edited'), h('div', { class: 'tiles' },
    ...[...touched].sort((a, b) => b[1].n - a[1].n).slice(0, 40).map(([file, f]) =>
      h('span', { class: 'tile h' + Math.max(1, Math.ceil(f.n / max * 4)), title: `${file}\n${plural(f.n, 'edit')} by ${[...f.sessions].join(', ')}` }, basename(file)))));
}

/** The sessions the map shows: all of them, or the one the tab shows (or the one chosen from its menu). */
function shown() {
  if (st.mode === 'all') return st.sessions;
  const one = (st.pinned && byId(st.pinned)) || st.sessions.find((s) => s.open) || st.sessions[0];
  return one ? [one] : [];
}

function render() {
  for (const b of document.querySelectorAll('#mode button')) b.classList.toggle('on', b.dataset.mode === st.mode);
  $('#window').value = String(st.window);
  const main = $('#main'), map = $('#map'), msg = $('#message');
  main.className = st.mode;
  const since = Date.now() - st.window * 60000;
  const list = shown();
  $('#calls').className = 'calls' + (callsToday() ? ' spent' : '');
  $('#calls').replaceChildren(h('b', {}, callsToday() ? plural(callsToday(), 'call') : 'no calls'), ' today');
  if (st.denied) {
    msg.hidden = false; msg.className = 'message error'; msg.textContent = st.denied;
    map.replaceChildren(); $('#files').hidden = true; $('#tokens').replaceChildren();
    return;
  }
  msg.hidden = list.length > 0;
  if (!list.length) { msg.className = 'message'; msg.textContent = 'No sessions yet.'; map.replaceChildren(); $('#files').hidden = true; return; }
  const width = map.clientWidth || 400;
  if (st.mode === 'all') map.replaceChildren(axis(since), ...list.map((s) => row(s, since, width)));
  else {
    const s = list[0];
    tell('title', { title: `Timeline: ${s.name}` });
    map.replaceChildren(row(s, since, width), stops(s, since));
  }
  if (st.mode === 'all') tell('title', { title: null });
  tokensLine(list, since);
  files(list, since);
  renderSummary();
}
const tell = (type, body) => parent.postMessage({ type: 'claude-terminals:' + type, ...body }, '*');

function note(text, error) {
  const msg = $('#message');
  msg.hidden = false; msg.className = 'message' + (error ? ' error' : ''); msg.textContent = text;
  setTimeout(() => { if (msg.textContent === text) msg.hidden = true; }, 4000);
}

// ---- clicking a prompt: the app opens the session's conversation at it (Claude's full-screen mode keeps no scrollback to jump through)

document.addEventListener('click', (e) => {
  const el = e.target.closest('[data-at][data-session]');
  if (!el) return;
  ask('showPrompt', { sessionId: el.dataset.session, at: el.dataset.at, index: Number(el.dataset.index), text: el.dataset.prompt ?? el.textContent })
    .catch((err) => note(err.message, true));
});

// ---- the station tooltip

let tip = null;
document.addEventListener('mouseover', (e) => {
  const stn = e.target.closest('.st[data-prompt]');
  if (!stn || tip) return;
  tip = h('div', { class: 'tip' }, h('b', {}, stn.dataset.prompt), stn.dataset.did, h('br'), h('span', { style: 'opacity:.7' }, stn.dataset.when));
  const left = parseFloat(stn.style.left);
  tip.classList.add(left < 20 ? 'left' : left > 80 ? 'right' : 'mid');
  tip.style.left = stn.style.left;
  stn.parentElement.append(tip);
});
document.addEventListener('mouseout', (e) => { if (e.target.closest('.st[data-prompt]') && tip) { tip.remove(); tip = null; } });

// ---- Summarise: the one call

async function summarise() {
  const list = shown().filter((s) => st.data.get(s.id)?.found);
  if (!list.length) { note('There is no conversation to summarise.', true); return; }
  const names = list.map((s) => s.name);
  st.summary = { pending: true, names };
  renderSummary();
  const question = list.length === 1
    ? `For this session, in plain Markdown with no preamble: three short bullets at most for each of "Done", "Decided" (with why) and "Next". Say "nothing yet" where the conversation shows nothing.`
    : `For each session, in plain Markdown with no preamble: a heading with the session's name, then three short bullets at most for each of "Done", "Decided" (with why) and "Next". Say "nothing yet" where its conversation shows nothing.`;
  try {
    const a = await ask('ask', { question, ids: list.map((s) => s.id), history: [] });
    st.calls = { day: today(), n: callsToday() + 1 };
    try { localStorage.setItem('timeline.calls', JSON.stringify(st.calls)); } catch { /* fine without */ }
    st.summary = { text: a.answer, when: Date.now(), names, writer: a.writer };
  } catch (e) {
    st.summary = { error: e.message, names };
  }
  render();
}

function renderSummary() {
  const el = $('#summary');
  const s = st.summary;
  el.hidden = !s;
  if (!s) return;
  el.className = 'summary' + (s.pending ? ' pending' : s.error ? ' error' : '');
  el.replaceChildren(
    h('button', { class: 'close', title: 'Dismiss', onclick: () => { st.summary = null; renderSummary(); } }, '×'),
    h('div', { class: 'meta' }, s.pending ? 'Summarising ' + s.names.join(', ') + '…' : s.error ? 'Summary failed' : `Summary · ${s.names.length === 1 ? s.names[0] : plural(s.names.length, 'session')} · ${ago(s.when)}`),
    s.pending ? 'Claude is reading the conversations.' : s.error ? s.error : markdown(s.text));
}

/** Enough Markdown for an answer: headings, paragraphs, lists, code fences, inline code and bold. */
function markdown(text) {
  const out = document.createDocumentFragment();
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const inline = (s) => {
    const frag = document.createDocumentFragment();
    const re = /(`[^`]+`)|(\*\*[^*]+\*\*)/g;
    let last = 0, m;
    while ((m = re.exec(s))) {
      if (m.index > last) frag.append(s.slice(last, m.index));
      frag.append(m[1] ? h('code', {}, m[1].slice(1, -1)) : h('b', {}, m[2].slice(2, -2)));
      last = m.index + m[0].length;
    }
    if (last < s.length) frag.append(s.slice(last));
    return frag;
  };
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (line.trim() === '') { i++; continue; }
    if (line.startsWith('```')) {
      const code = [];
      for (i++; i < lines.length && !lines[i].startsWith('```'); i++) code.push(lines[i]);
      i++;
      out.append(h('pre', {}, h('code', {}, code.join('\n'))));
      continue;
    }
    if (/^#{1,6}\s/.test(line)) { out.append(h('h3', {}, inline(line.replace(/^#+\s*/, '')))); i++; continue; }
    const bullet = /^\s*[-*•]\s+/, numbered = /^\s*\d+[.)]\s+/;
    if (bullet.test(line) || numbered.test(line)) {
      const list = h(numbered.test(line) ? 'ol' : 'ul');
      for (; i < lines.length && (bullet.test(lines[i]) || numbered.test(lines[i])); i++) list.append(h('li', {}, inline(lines[i].replace(bullet, '').replace(numbered, ''))));
      out.append(list);
      continue;
    }
    const para = [];
    for (; i < lines.length && lines[i].trim() !== '' && !lines[i].startsWith('```') && !bullet.test(lines[i]) && !numbered.test(lines[i]) && !/^#{1,6}\s/.test(lines[i]); i++) para.push(lines[i]);
    out.append(h('p', {}, inline(para.join(' '))));
  }
  return out;
}

// ---- controls

$('#mode').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  st.mode = b.dataset.mode; st.pinned = null;
  remember();
  render();
});
$('#window').addEventListener('change', (e) => { st.window = Number(e.target.value); remember(); render(); });
$('#summarise').addEventListener('click', summarise);
function showHelp(on) {
  st.help = on;
  $('#helpPanel').hidden = !on;
  $('#help').classList.toggle('on', on);
  if (on) $('#helpPanel').scrollIntoView({ block: 'start' });
}
$('#help').addEventListener('click', () => showHelp(!st.help));
$('#helpClose').addEventListener('click', () => showHelp(false));
addEventListener('keydown', (e) => { if (e.key === 'Escape' && st.help) showHelp(false); });
addEventListener('resize', () => render());

refresh();
