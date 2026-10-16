'use strict';

/* =====================================================================
   Claude Terminals – front end
   Terminals: xterm.js over ConPTY (host side). Git/diff: VS-style tool window + Monaco diff.
   ===================================================================== */

// ------------------------------------------------------------------ helpers

const $ = (sel, root = document) => root.querySelector(sel);

function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'style') el.style.cssText = v;
    else if (k === 'html') el.innerHTML = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

/** replaceChildren that skips the null/false left by conditional children. */
const fill = (el, ...kids) => el.replaceChildren(...kids.flat().filter((k) => k !== null && k !== undefined && k !== false));

const PATHS = {
  plus: 'M8 3v10M3 8h10',
  minus: 'M3 8h10',
  close: 'M4 4l8 8M12 4l-8 8',
  refresh: 'M13 3v3.5H9.5M12.6 6.2A5 5 0 1 0 13 9',
  grid: 'M2.5 2.5h4.5v4.5H2.5zM9 2.5h4.5v4.5H9zM2.5 9h4.5v4.5H2.5zM9 9h4.5v4.5H9z',
  git: 'M5 3v10M5 11a2 2 0 1 0 0 .01M5 3a1.6 1.6 0 1 0 0 .01M11 3a1.6 1.6 0 1 0 0 .01M11 4.6C11 8 5 7 5 10',
  branch: 'M5 2.5v8M5 12a1.5 1.5 0 1 0 0 .01M11 4a1.5 1.5 0 1 0 0 .01M11 5.5c0 3-6 2.5-6 5',
  chevR: 'M6 4l4 4-4 4',
  chevD: 'M4 6l4 4 4-4',
  up: 'M8 13V3M4 7l4-4 4 4',
  down: 'M8 3v10M4 9l4 4 4-4',
  more: 'M3.5 8h.01M8 8h.01M12.5 8h.01',
  collapse: 'M6 2v4H2M10 2v4h4M6 14v-4H2M10 14v-4h4',
  sideBySide: 'M2 3h12v10H2zM8 3v10',
  inline: 'M2 3h12v10H2zM2 8h12',
  whitespace: 'M3 12h10M3 9v3M13 9v3M6 4h4M8 4v5',
  openFile: 'M9 2H4v12h8V5zM9 2v3h3',
  folder: 'M1.5 4V13h13V5.5H7.5L6 4z',
  file: 'M4 1.5h5.5L12.5 4.5v10h-8.5zM9.5 1.5v3h3',
  terminal: 'M2 3h12v10H2zM4.5 6l2 2-2 2M8 10.5h3.5',
  restart: 'M3 8a5 5 0 1 0 1.5-3.6M3 2.5v2.5h2.5',
  undo: 'M3.5 6.5h6.5a3.25 3.25 0 0 1 0 6.5H7M3.5 6.5l3-3M3.5 6.5l3 3',
  checkOn: 'M2.5 2.5h11v11h-11zM5 8.2l2 2 4-4.4',
  checkOff: 'M2.5 2.5h11v11h-11z',
};

function icon(name, cls = '') {
  const filled = name === 'folder';
  return `<svg class="ic ${cls}" viewBox="0 0 16 16" fill="${filled ? 'currentColor' : 'none'}" fill-opacity="${filled ? .9 : 1}" stroke="currentColor" stroke-width="${filled ? 0 : 1.3}" stroke-linecap="round" stroke-linejoin="round"><path d="${PATHS[name]}"/></svg>`;
}
const iconEl = (name, cls) => { const t = document.createElement('template'); t.innerHTML = icon(name, cls); return t.content.firstChild; };

const store = {
  get(key, def) { try { const v = localStorage.getItem('ct.' + key); return v === null ? def : JSON.parse(v); } catch { return def; } },
  set(key, v) { localStorage.setItem('ct.' + key, JSON.stringify(v)); },
};

function debounce(fn, ms) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

function relTime(iso) {
  const d = new Date(iso), s = (Date.now() - d) / 1000;
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} hr ago`;
  if (s < 86400 * 7) return `${Math.floor(s / 86400)} days ago`;
  return d.toLocaleDateString();
}

function toast(message, isError = false) {
  const el = h('div', { class: 'toast' + (isError ? ' error' : '') }, message);
  $('#toastHost').append(el);
  setTimeout(() => el.remove(), isError ? 7000 : 3500);
}

// ------------------------------------------------------------------ host bridge

const host = window.chrome.webview;
let reqSeq = 0;
const pending = new Map();
const post = (msg) => host.postMessage(msg);

function request(type, payload = {}) {
  const reqId = ++reqSeq;
  return new Promise((resolve, reject) => {
    pending.set(reqId, { resolve, reject });
    post({ type, reqId, ...payload });
  });
}

host.addEventListener('message', (e) => {
  const m = e.data;
  switch (m.type) {
    case 'reply': {
      const p = pending.get(m.reqId);
      if (!p) return;
      pending.delete(m.reqId);
      m.ok ? p.resolve(m.result) : p.reject(new Error(m.error));
      break;
    }
    case 'sessions': syncSessions(m.sessions, m.projects || []); break;
    case 'output': onOutput(m.id, m.data); break;
    case 'exited': onExited(m.id, m.code); break;
    case 'git.status': onGitStatus(m); break;
  }
});

// ------------------------------------------------------------------ state

const state = {
  sessions: [],              // ordered session objects
  byId: new Map(),
  expandedId: null,
  currentId: store.get('currentId', null),   // session shown in the Git panel
  gitVisible: store.get('gitVisible', true),
  gitWidth: store.get('gitWidth', 400),
  gitTab: 'changes',
  fontSize: store.get('fontSize', 14),
  surface: { w: 0, h: 0 },
  projects: [],                                        // [{ id, name }] in display order
  hiddenProjects: new Set(store.get('hiddenProjects', [])),   // hidden rather than shown, so new projects start visible
};

// ------------------------------------------------------------------ projects: which sessions are visible

const projectIndex = (s) => {
  const i = state.projects.findIndex((p) => p.id === s.project);
  return i < 0 ? state.projects.length : i;   // no project (or an unknown one) sorts last
};

/** Sessions with no project are visible whichever projects are selected. */
const isVisible = (s) => !s.project || projectIndex(s) === state.projects.length || !state.hiddenProjects.has(s.project);

/** Visible sessions, grouped by project in project order (no project last), keeping their own order within a group. */
function visibleSessions() {
  return state.sessions
    .map((s, i) => ({ s, i }))
    .filter(({ s }) => isVisible(s))
    .sort((a, b) => projectIndex(a.s) - projectIndex(b.s) || a.i - b.i)
    .map(({ s }) => s);
}

function saveHiddenProjects() {
  store.set('hiddenProjects', [...state.hiddenProjects]);
}

/** Applies a change in which sessions are visible: a hidden session can't stay open or be the Git panel's. */
function applyProjectFilter(instant = false) {
  const shown = visibleSessions();
  if (state.expandedId && !shown.some((s) => s.id === state.expandedId)) {
    state.byId.get(state.expandedId)?.term.blur();
    state.expandedId = null;
  }
  if (shown.length && !shown.some((s) => s.id === state.currentId)) {
    state.currentId = shown[0].id;
    store.set('currentId', state.currentId);
  }
  const anySessions = state.sessions.length > 0;
  $('#empty').hidden = shown.length > 0;
  $('#empty .empty-title').textContent = anySessions ? 'No sessions in the selected projects' : 'No sessions yet';
  $('#empty .empty-sub').textContent = anySessions
    ? 'Tick a project above, or choose All.'
    : 'Add a session for each worktree you want Claude to work in.';
  layout(instant);
  for (const s of state.sessions) renderTileHeader(s);
  renderProjectBar();
  renderSessionTabs();
  renderSessionPicker();
  renderGit();
}

function showOnlyProject(id) {
  state.hiddenProjects = new Set(state.projects.filter((p) => p.id !== id).map((p) => p.id));
  saveHiddenProjects();
  applyProjectFilter();
}

function toggleProject(id) {
  state.hiddenProjects.has(id) ? state.hiddenProjects.delete(id) : state.hiddenProjects.add(id);
  saveHiddenProjects();
  applyProjectFilter();
}

function showAllProjects() {
  state.hiddenProjects.clear();
  saveHiddenProjects();
  applyProjectFilter();
}

/**
 * The project row: per project a checkbox (show or hide it alongside the others) and its name (show only it), with a
 * count of its sessions waiting for you and a dot while any of them is working, so a hidden project still gets noticed.
 */
function renderProjectBar() {
  const bar = $('#projectBar');
  const tabs = state.projects.map((p) => {
    const members = state.sessions.filter((s) => s.project === p.id);
    const waiting = members.filter((s) => s.attention).length;
    const working = members.some((s) => s.running && s.act.state === 'working');
    const shown = !state.hiddenProjects.has(p.id);
    return h('div', {
      class: 'project-tab' + (shown ? ' shown' : ''),
      'data-id': p.id,
      oncontextmenu: (e) => { e.preventDefault(); projectMenu(p, e.clientX, e.clientY); },
    },
    h('button', {
      class: 'project-check', role: 'checkbox', 'aria-checked': String(shown),
      title: `${shown ? 'Hide' : 'Show'} ${p.name}, keeping the other projects as they are`,
      onclick: () => toggleProject(p.id),
    }, iconEl(shown ? 'checkOn' : 'checkOff')),
    h('button', {
      class: 'project-name', title: `Show only ${p.name}\n${members.length} session${members.length === 1 ? '' : 's'} · right-click for options`,
      onclick: () => showOnlyProject(p.id),
    }, p.name,
    working ? h('span', { class: 'dot working', title: 'A session is working' }) : null,
    waiting ? h('span', { class: 'badge', title: `${waiting} session${waiting === 1 ? '' : 's'} waiting for you` }, String(waiting)) : null));
  });
  const allShown = state.projects.length > 0 && state.projects.every((p) => !state.hiddenProjects.has(p.id));
  fill(bar,
    h('span', { class: 'project-bar-label' }, 'Projects'),
    ...tabs,
    state.projects.length ? h('button', { class: 'project-all' + (allShown ? ' active' : ''), title: 'Show every project', onclick: showAllProjects }, 'All') : null,
    h('button', { class: 'tb-btn text project-add', title: 'New project', onclick: showNewProjectDialog }, iconEl('plus'),
      state.projects.length ? 'Project' : 'New project (group sessions)'),
  );
}

function projectMenu(p, x, y) {
  const i = state.projects.indexOf(p);
  showMenu(x, y, [
    { label: 'Show only this project', action: () => showOnlyProject(p.id) },
    { label: state.hiddenProjects.has(p.id) ? 'Show alongside the others' : 'Hide', action: () => toggleProject(p.id) },
    '-',
    { label: 'Rename…', action: () => showRenameProjectDialog(p) },
    i > 0 && { label: 'Move left', action: () => moveProject(p, -1) },
    i < state.projects.length - 1 && { label: 'Move right', action: () => moveProject(p, 1) },
    '-',
    { label: 'Delete project…', danger: true, action: () => confirmDeleteProject(p) },
  ]);
}

function moveProject(p, delta) {
  const ids = state.projects.map((x) => x.id);
  const i = ids.indexOf(p.id), j = i + delta;
  if (j < 0 || j >= ids.length) return;
  [ids[i], ids[j]] = [ids[j], ids[i]];
  request('projects.reorder', { ids }).catch((e) => toast(e.message, true));
}

function projectNameDialog(title, initial, okLabel, onOk) {
  const name = h('input', { value: initial, placeholder: 'e.g. FiscFlow', spellcheck: 'false', maxlength: '60' });
  const error = h('div', { class: 'form-error', hidden: true });
  const ok = async () => {
    if (!name.value.trim()) { error.hidden = false; error.textContent = 'Enter a name.'; return; }
    try { await onOk(name.value.trim()); closeModal(); } catch (e) { error.hidden = false; error.textContent = e.message; }
  };
  name.addEventListener('keydown', (e) => { if (e.key === 'Enter') ok(); });
  openModal(title, [field('Name', name), error],
    [h('button', { class: 'btn', onclick: closeModal }, 'Cancel'), h('button', { class: 'btn primary', onclick: ok }, okLabel)]);
}

function showNewProjectDialog() {
  projectNameDialog('New project', '', 'Create', async (name) => {
    await request('projects.add', { name });
    if (!state.sessions.length) return;
    toast(`Project "${name}" created. Put sessions in it with right-click → Move to project…, or pick it when adding a session.`);
  });
}

function showRenameProjectDialog(p) {
  projectNameDialog('Rename project', p.name, 'Rename', (name) => request('projects.rename', { id: p.id, name }));
}

function confirmDeleteProject(p) {
  const n = state.sessions.filter((s) => s.project === p.id).length;
  confirmDialog('Delete project',
    `Delete the project "${p.name}"?` + (n ? `\nIts ${n} session${n === 1 ? '' : 's'} will be kept and have no project, so ${n === 1 ? 'it shows' : 'they show'} with every project.` : ''),
    'Delete', true, () => request('projects.remove', { id: p.id }).catch((e) => toast(e.message, true)));
}

const PAD = 8, GAP = 8, HEADER_H = 30;
const MAX_WEBGL = 12;

const TERM_THEME = {
  background: '#181818', foreground: '#cccccc', cursor: '#d4d4d4', cursorAccent: '#181818',
  selectionBackground: '#264f78', selectionInactiveBackground: '#3a3d41',
  black: '#0c0c0c', red: '#c50f1f', green: '#13a10e', yellow: '#c19c00', blue: '#3b78ff', magenta: '#881798', cyan: '#3a96dd', white: '#cccccc',
  brightBlack: '#767676', brightRed: '#e74856', brightGreen: '#16c60c', brightYellow: '#f9f1a5', brightBlue: '#3b78ff', brightMagenta: '#b4009e', brightCyan: '#61d6d6', brightWhite: '#f2f2f2',
};

// ------------------------------------------------------------------ sessions & tiles

function syncSessions(list, projects) {
  state.projects = projects;
  // Forget hidden projects that no longer exist, so a recreated project with the same id can't start hidden.
  const known = new Set(projects.map((p) => p.id));
  for (const id of [...state.hiddenProjects]) if (!known.has(id)) state.hiddenProjects.delete(id);
  saveHiddenProjects();

  const seen = new Set();
  for (const info of list) {
    seen.add(info.id);
    let s = state.byId.get(info.id);
    if (!s) {
      s = createSession(info);
      state.byId.set(s.id, s);
    } else {
      Object.assign(s, { name: info.name, directory: info.directory, command: info.command });
    }
    s.project = info.project || null;
    s.running = info.running || s.running;
  }
  for (const s of [...state.byId.values()]) {
    if (!seen.has(s.id)) disposeSession(s);
  }
  state.sessions = list.map((i) => state.byId.get(i.id));
  for (const s of state.sessions) $('#grid').append(s.tile);

  if (!state.byId.has(state.currentId)) state.currentId = state.sessions[0]?.id ?? null;
  if (state.expandedId && !state.byId.has(state.expandedId)) state.expandedId = null;

  layout(true);
  // Every session starts, visible or not: a project being hidden only hides it from view.
  for (const s of state.sessions) {
    if (!s.running && !s.started) startSession(s);
  }
  applyProjectFilter(true);
}

function createSession(info) {
  const s = {
    ...info, running: false, started: false, exitCode: null, title: '', git: null,
    attention: false, act: { lastOut: 0, lastIn: 0, burstStart: 0, state: 'idle', workingSince: 0 },
    log: null, selectedCommit: null, commitInfo: null, commitDraft: '', info: null, collapsed: new Set(),
  };

  s.surface = h('div', { class: 'surface' });
  s.cover = h('div', { class: 'tile-cover', title: 'Click to expand' });
  s.cover.addEventListener('click', () => expand(s.id));
  s.exitedBadge = h('div', { class: 'tile-exited', hidden: true });
  s.header = h('div', { class: 'tile-header' });
  s.header.addEventListener('click', (e) => {
    if (e.target.closest('button')) return;
    if (state.expandedId !== s.id) expand(s.id);
  });
  s.header.addEventListener('contextmenu', (e) => { e.preventDefault(); sessionMenu(s, e.clientX, e.clientY); });
  s.tile = h('div', { class: 'tile', 'data-id': s.id }, s.header, h('div', { class: 'tile-body' }, s.surface, s.cover, s.exitedBadge));

  const term = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Courier New", monospace',
    fontSize: state.fontSize,
    lineHeight: 1.0,
    cursorBlink: true,
    scrollback: 10000,
    allowProposedApi: true,
    theme: TERM_THEME,
    windowsPty: { backend: 'conpty', buildNumber: 22621 },
    rightClickSelectsWord: false,
  });
  s.term = term;
  s.fit = new FitAddon.FitAddon();
  term.loadAddon(s.fit);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';
  term.loadAddon(new WebLinksAddon.WebLinksAddon((ev, uri) => request('openUrl', { uri })));
  term.open(s.surface);
  if (state.byId.size < MAX_WEBGL) {
    try {
      const gl = new WebglAddon.WebglAddon();
      gl.onContextLoss(() => gl.dispose());
      term.loadAddon(gl);
    } catch { /* DOM renderer fallback */ }
  }

  term.attachCustomKeyEventHandler((e) => terminalKey(s, e));
  term.onData((data) => {
    if (!s.running) {
      if (data === '\r') restartSession(s);
      return;
    }
    s.act.lastIn = performance.now();
    post({ type: 'input', id: s.id, data });
  });
  term.onResize(({ cols, rows }) => { if (s.running) post({ type: 'resize', id: s.id, cols, rows }); });
  term.onTitleChange((t) => { s.title = t; renderTileHeader(s); });
  term.onBell(() => { if (state.expandedId !== s.id) setAttention(s, true); });
  s.surface.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (state.expandedId === s.id) terminalMenu(s, e.clientX, e.clientY);
  });
  return s;
}

function disposeSession(s) {
  s.term.dispose();
  s.tile.remove();
  state.byId.delete(s.id);
  if (state.expandedId === s.id) state.expandedId = null;
  for (const t of docs.tabs.filter((x) => x.sessionId === s.id)) closeDoc(t.key);
}

function fitTerm(s) {
  try { s.fit.fit(); } catch { /* not attached yet */ }
}

function startSession(s) {
  s.started = true;
  fitTerm(s);
  request('start', { id: s.id, cols: s.term.cols, rows: s.term.rows })
    .then(() => { s.running = true; s.exitCode = null; renderTileHeader(s); renderSessionTabs(); })
    .catch((err) => {
      s.running = false;
      s.term.writeln(`\x1b[31mFailed to start "${s.command}" in ${s.directory}\x1b[0m`);
      s.term.writeln(`\x1b[90m${err.message}\x1b[0m`);
      s.term.writeln('\x1b[90mPress Enter to retry.\x1b[0m');
      renderTileHeader(s);
    });
}

function restartSession(s) {
  fitTerm(s);
  s.term.reset();
  request('restart', { id: s.id, cols: s.term.cols, rows: s.term.rows })
    .then(() => { s.running = true; s.exitCode = null; renderTileHeader(s); renderSessionTabs(); })
    .catch((err) => { s.term.writeln(`\x1b[31m${err.message}\x1b[0m`); });
}

function onOutput(id, data) {
  const s = state.byId.get(id);
  if (!s) return;
  s.term.write(data);
  noteActivity(s);
}

function onExited(id, code) {
  const s = state.byId.get(id);
  if (!s) return;
  s.running = false;
  s.exitCode = code;
  s.act.state = 'idle';
  const what = code == null ? 'Process stopped' : `Process exited with code ${code}`;   // null: stopped from the menu
  s.term.write(`\r\n\x1b[90m[${what} — press Enter to restart]\x1b[0m\r\n`);
  renderTileHeader(s);
  renderSessionTabs();
}

// Activity: Claude's spinner redraws continuously while it works and goes quiet when it is waiting on
// you, so a sustained burst of output followed by silence means "your turn" → flag the tile.
function noteActivity(s) {
  const now = performance.now();
  const a = s.act;
  if (now - a.lastIn < 400) { a.lastOut = now; return; }   // echo of our own typing
  if (a.state !== 'working') {
    if (now - a.lastOut > 1500) a.burstStart = now;
    if (now - a.burstStart > 1200) {
      a.state = 'working';
      a.workingSince = a.burstStart;
      renderTileState(s);
    }
  }
  a.lastOut = now;
}

setInterval(() => {
  const now = performance.now();
  for (const s of state.sessions) {
    const a = s.act;
    if (a.state === 'working' && now - a.lastOut > 2500) {
      a.state = 'idle';
      if (now - a.workingSince > 4000 && state.expandedId !== s.id) setAttention(s, true);
      else renderTileState(s);
    }
  }
}, 500);

function setAttention(s, on) {
  if (s.attention === on) return;
  s.attention = on;
  renderTileState(s);
  renderDocTabs();
}

function statusClass(s) {
  if (!s.running) return 'exited';
  if (s.attention) return 'attention';
  if (s.act.state === 'working') return 'working';
  return 'running';
}

function statusText(s) {
  return { exited: 'Not running', attention: 'Waiting for you', working: 'Working…', running: 'Idle' }[statusClass(s)];
}

function renderTileState(s) {
  s.tile.classList.toggle('attention', s.attention);
  const dot = s.header.querySelector('.dot');
  if (dot) { dot.className = 'dot ' + statusClass(s); dot.title = statusText(s); }
  const tab = $(`.session-tab[data-id="${s.id}"] .dot`);
  if (tab) { tab.className = 'dot ' + statusClass(s); tab.title = statusText(s); }
  if (s.project) updateProjectIndicators(s.project);
}

/** Refreshes one project tab's working dot and waiting badge in place (rebuilding the bar would drop keyboard focus). */
function updateProjectIndicators(projectId) {
  const name = document.querySelector(`.project-tab[data-id="${projectId}"] .project-name`);
  if (!name) return;
  const members = state.sessions.filter((x) => x.project === projectId);
  const waiting = members.filter((x) => x.attention).length;
  const working = members.some((x) => x.running && x.act.state === 'working');
  name.querySelector('.dot')?.remove();
  name.querySelector('.badge')?.remove();
  if (working) name.append(h('span', { class: 'dot working', title: 'A session is working' }));
  if (waiting) name.append(h('span', { class: 'badge', title: `${waiting} session${waiting === 1 ? '' : 's'} waiting for you` }, String(waiting)));
}

function gitSummary(s) {
  const g = s.git;
  if (!g || !g.isRepo) return null;
  const all = [...g.staged, ...g.unstaged];
  const paths = new Set(all.map((f) => f.path));
  return { branch: g.branch, count: paths.size, ahead: g.ahead, behind: g.behind };
}

function renderTileHeader(s) {
  const expanded = state.expandedId === s.id;
  const gs = gitSummary(s);
  fill(s.header,
    h('span', { class: 'dot ' + statusClass(s), title: statusText(s) }),
    h('span', { class: 'tile-name' }, s.name),
    h('span', { class: 'tile-title', title: s.title }, s.title),
    gs && h('button', {
      class: 'chip', title: `${gs.count} changed file(s) on ${gs.branch} — show in Git Changes`,
      onclick: () => showGit(s.id),
    }, iconEl('branch'), gs.branch, gs.ahead ? ` ↑${gs.ahead}` : '', gs.count ? h('span', { class: 'mod' }, ` ●${gs.count}`) : ''),
    h('button', { class: 'icon-btn', title: 'Session options', onclick: (e) => { const r = e.currentTarget.getBoundingClientRect(); sessionMenu(s, r.left, r.bottom + 2); } }, iconEl('more')),
    expanded && h('button', { class: 'icon-btn', title: 'Back to all sessions (Shift+Esc)', onclick: collapse }, iconEl('collapse')),
  );
  s.exitedBadge.hidden = s.running || !s.started;
  s.exitedBadge.textContent = s.exitCode === null ? 'Not running' : `Exited (${s.exitCode})`;
  renderTileState(s);
}

function renderSessionTabs() {
  const host = $('#sessionTabs');
  // The label and the close button are sibling buttons, since one button can't contain another: both stay reachable
  // with Tab, and Enter or Space on the label switches sessions. A click elsewhere on the tab counts as the label's.
  // Visible sessions only, grouped by project, with a divider wherever the project changes.
  const shown = visibleSessions();
  const tabs = shown.map((s, i) => h('div', {
    class: 'session-tab' + (state.expandedId === s.id ? ' active' : ''),
    'data-id': s.id,
    title: `${s.directory}\n${statusText(s)}\nMiddle-click to close`,
    onclick: (e) => {
      if (e.target.closest('.tab-close')) return;
      state.expandedId === s.id ? collapse() : expand(s.id);
    },
    onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },   // no autoscroll cursor
    onauxclick: (e) => { if (e.button === 1) confirmRemove(s); },
    oncontextmenu: (e) => { e.preventDefault(); sessionMenu(s, e.clientX, e.clientY); },
  }, h('button', { class: 'tab-label' },
    h('span', { class: 'dot ' + statusClass(s) }), s.name, i < 9 ? h('span', { class: 'key' }, `Ctrl+${i + 1}`) : null),
  // Closing a session tab removes the session, after the same confirmation as "Remove session…".
  h('button', { class: 'icon-btn small tab-close', title: 'Close session…', onclick: () => confirmRemove(s) }, iconEl('close'))));
  const withDividers = [];
  shown.forEach((s, i) => {
    if (i > 0 && projectIndex(s) !== projectIndex(shown[i - 1])) {
      const p = state.projects[projectIndex(s)];
      withDividers.push(h('span', { class: 'session-divider', title: p ? p.name : 'No project: shown with every project' }));
    }
    withDividers.push(tabs[i]);
  });
  host.replaceChildren(...withDividers);
  $('#btnGrid').classList.toggle('active', !state.expandedId);
  renderDocTabs();
}

// ------------------------------------------------------------------ layout

function surfaceSize() {
  const r = $('#termPane').getBoundingClientRect();
  return { w: Math.max(200, Math.floor(r.width - 2 * PAD - 2 - 8)), h: Math.max(100, Math.floor(r.height - 2 * PAD - HEADER_H - 2 - 4)) };
}

const refit = debounce(() => {
  for (const s of state.sessions) fitTerm(s);
}, 60);

function layout(instant = false) {
  if (instant) document.body.classList.add('no-anim');
  const stage = $('#termPane').getBoundingClientRect();
  const W = stage.width, H = stage.height;
  const sz = surfaceSize();
  state.surface = sz;
  let resized = false;
  for (const s of state.sessions) {
    const w = sz.w + 'px', hh = sz.h + 'px';
    if (s.surface.style.width !== w || s.surface.style.height !== hh) {
      s.surface.style.width = w;
      s.surface.style.height = hh;
      resized = true;
    }
  }
  if (resized) refit();

  // Sessions in hidden projects keep running; their tiles are just taken out of the grid.
  const shown = visibleSessions();
  for (const s of state.sessions) s.tile.classList.toggle('off-project', !shown.includes(s));
  const n = shown.length;
  // Pick the column count that gives the largest terminal preview.
  let best = { cols: 1, scale: 0 };
  for (let cols = 1; cols <= n; cols++) {
    const rows = Math.ceil(n / cols);
    const tw = (W - 2 * PAD - (cols - 1) * GAP) / cols;
    const th = (H - 2 * PAD - (rows - 1) * GAP) / rows;
    const scale = Math.min(tw / sz.w, (th - HEADER_H) / sz.h);
    if (scale > best.scale) best = { cols, scale, tw, th, rows };
  }

  shown.forEach((s, i) => {
    const t = s.tile;
    const expanded = state.expandedId === s.id;
    t.classList.toggle('expanded', expanded);
    t.classList.toggle('concealed', !!state.expandedId && !expanded);
    t.classList.toggle('current', state.currentId === s.id);
    let x, y, w, hh, scale;
    if (expanded) {
      x = PAD; y = PAD; w = W - 2 * PAD; hh = H - 2 * PAD; scale = 1;
    } else {
      const c = i % best.cols, r = Math.floor(i / best.cols);
      // Centre a partially filled last row.
      const inRow = r === best.rows - 1 ? n - r * best.cols : best.cols;
      const rowOffset = (best.cols - inRow) * (best.tw + GAP) / 2;
      x = PAD + rowOffset + c * (best.tw + GAP); y = PAD + r * (best.th + GAP); w = best.tw; hh = best.th;
      scale = Math.min(1, best.scale);
    }
    Object.assign(t.style, { left: x + 'px', top: y + 'px', width: w + 'px', height: hh + 'px' });
    const bodyW = w - 2 - 12;
    const offX = expanded ? 0 : Math.max(0, (bodyW - sz.w * scale) / 2);
    s.surface.style.transform = `translate(${offX}px, 0) scale(${scale})`;
  });

  if (instant) requestAnimationFrame(() => requestAnimationFrame(() => document.body.classList.remove('no-anim')));
}

new ResizeObserver(() => layout(true)).observe($('#termPane'));

function expand(id) {
  const s = state.byId.get(id);
  if (!s) return;
  closeMenu();
  if (docs.active !== 'terminal') showTerminalDoc();
  state.expandedId = id;
  setAttention(s, false);
  if (state.currentId !== id) { state.currentId = id; store.set('currentId', id); renderSessionPicker(); renderGit(); }
  layout();
  for (const x of state.sessions) renderTileHeader(x);
  renderSessionTabs();
  setTimeout(() => s.term.focus(), 30);
}

function collapse() {
  if (!state.expandedId) return;
  const s = state.byId.get(state.expandedId);
  state.expandedId = null;
  s?.term.blur();
  layout();
  for (const x of state.sessions) renderTileHeader(x);
  renderSessionTabs();
}

function cycle(delta) {
  const shown = visibleSessions();
  if (!shown.length) return;
  const idx = shown.findIndex((s) => s.id === (state.expandedId ?? state.currentId));
  const next = shown[(idx + delta + shown.length) % shown.length];
  expand(next.id);
}

function setFontSize(size) {
  state.fontSize = Math.max(8, Math.min(28, size));
  store.set('fontSize', state.fontSize);
  for (const s of state.sessions) s.term.options.fontSize = state.fontSize;
  refit();
}

// ------------------------------------------------------------------ keyboard

/** Returns true for app-level shortcuts that should not reach the terminal. */
function handleShortcut(e) {
  if (e.type !== 'keydown') return false;
  const k = e.key;
  if (k === 'Escape' && e.shiftKey && !e.ctrlKey && !e.altKey) {
    if (!$('#modalHost').hidden) closeModal();
    else if (activeTab()) showTerminalDoc();
    else collapse();
    return true;
  }
  if (e.ctrlKey && !e.altKey && !e.shiftKey && /^[1-9]$/.test(k)) {
    const s = visibleSessions()[+k - 1];   // matches the Ctrl+N hints on the visible tabs
    if (s) expand(s.id);
    return true;
  }
  if (e.ctrlKey && !e.shiftKey && (k === 'PageUp' || k === 'PageDown')) { cycleDoc(k === 'PageUp' ? -1 : 1); return true; }
  if (e.ctrlKey && k === 'F4') { if (activeTab()) closeDoc(docs.active); return true; }
  if (e.ctrlKey && k === 'Tab') { cycle(e.shiftKey ? -1 : 1); return true; }
  if (e.ctrlKey && e.shiftKey && (k === 'G' || k === 'g')) { toggleGit(); return true; }
  if (e.ctrlKey && e.shiftKey && (k === 'N' || k === 'n')) { showAddDialog(); return true; }
  if (e.ctrlKey && !e.shiftKey && (k === '=' || k === '+')) { setFontSize(state.fontSize + 1); return true; }
  if (e.ctrlKey && !e.shiftKey && k === '-') { setFontSize(state.fontSize - 1); return true; }
  if (e.ctrlKey && !e.shiftKey && k === '0') { setFontSize(14); return true; }
  return false;
}

function terminalKey(s, e) {
  if (handleShortcut(e)) { e.preventDefault(); e.stopPropagation(); return false; }
  // Shift+Enter inserts a newline in Claude Code's prompt (same as Alt+Enter). Cancel the keydown and swallow the
  // keypress as well: xterm turns an uncancelled Enter keypress into a second CR, which submits the prompt.
  if (e.key === 'Enter' && e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey) {
    if (e.type === 'keydown' && s.running) post({ type: 'input', id: s.id, data: '\x1b\r' });
    e.preventDefault();
    return false;
  }
  if (e.type !== 'keydown') return true;
  // Ctrl+V pastes. Left to xterm it becomes ^V and the event is cancelled, so the browser's paste never happens; returning
  // false without cancelling lets the native paste reach xterm's paste handler (bracketed paste included).
  if (e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey && (e.key === 'v' || e.key === 'V')) return false;
  // Ctrl+C copies when there is a selection, otherwise it is sent as an interrupt.
  if (e.ctrlKey && !e.shiftKey && (e.key === 'c' || e.key === 'C') && s.term.hasSelection()) {
    navigator.clipboard.writeText(s.term.getSelection());
    s.term.clearSelection();
    return false;
  }
  if (e.ctrlKey && e.shiftKey && (e.key === 'C' || e.key === 'c')) {
    if (s.term.hasSelection()) navigator.clipboard.writeText(s.term.getSelection());
    return false;
  }
  if (e.ctrlKey && e.shiftKey && (e.key === 'V' || e.key === 'v')) {
    navigator.clipboard.readText().then((t) => t && s.term.paste(t));
    return false;
  }
  return true;
}

document.addEventListener('keydown', (e) => {
  if (handleShortcut(e)) { e.preventDefault(); e.stopPropagation(); return; }
  if (e.key === 'Escape' && !e.shiftKey) {
    if (closeMenu()) return;
    if (!$('#modalHost').hidden) closeModal();
  }
}, true);

// Mouse "back" button collapses to the overview.
document.addEventListener('mouseup', (e) => {
  if (e.button === 3) { e.preventDefault(); if (activeTab()) showTerminalDoc(); else collapse(); }
});

// ------------------------------------------------------------------ menus & modals

let openMenuEl = null;

function closeMenu() {
  if (!openMenuEl) return false;
  openMenuEl.remove();
  openMenuEl = null;
  return true;
}

function showMenu(x, y, items) {
  closeMenu();
  const menu = h('div', { class: 'menu' }, items.filter(Boolean).map((it) => it === '-'
    ? h('div', { class: 'menu-sep' })
    : h('div', { class: 'menu-item' + (it.danger ? ' danger' : ''), onclick: () => { closeMenu(); it.action(); } },
      it.label, it.kbd && h('span', { class: 'kbd' }, it.kbd))));
  $('#menuHost').append(menu);
  const r = menu.getBoundingClientRect();
  menu.style.left = Math.min(x, innerWidth - r.width - 4) + 'px';
  menu.style.top = Math.min(y, innerHeight - r.height - 4) + 'px';
  openMenuEl = menu;
}

document.addEventListener('mousedown', (e) => { if (openMenuEl && !openMenuEl.contains(e.target)) closeMenu(); }, true);

function sessionMenu(s, x, y) {
  showMenu(x, y, [
    state.expandedId === s.id ? { label: 'Back to all sessions', kbd: 'Shift+Esc', action: collapse } : { label: 'Expand', action: () => expand(s.id) },
    { label: 'Show Git changes', action: () => showGit(s.id) },
    '-',
    { label: 'Resume a different conversation…', action: () => showResumeDialog(s) },
    { label: 'Rename / edit command…', action: () => showEditDialog(s) },
    { label: 'Move to project…', action: () => showEditDialog(s, 'project') },
    { label: s.running ? 'Restart session' : 'Start session', action: () => (s.running ? confirmRestart(s) : restartSession(s)) },
    s.running && { label: 'Stop session', action: () => confirmStop(s) },
    { label: 'Open folder in Explorer', action: () => request('openFolder', { id: s.id }) },
    '-',
    { label: 'Move left', action: () => moveSession(s, -1) },
    { label: 'Move right', action: () => moveSession(s, 1) },
    '-',
    { label: 'Remove session…', danger: true, action: () => confirmRemove(s) },
  ]);
}

function terminalMenu(s, x, y) {
  showMenu(x, y, [
    { label: 'Copy', kbd: 'Ctrl+C', action: () => s.term.hasSelection() && navigator.clipboard.writeText(s.term.getSelection()) },
    { label: 'Paste', kbd: 'Ctrl+V', action: () => navigator.clipboard.readText().then((t) => t && s.term.paste(t)) },
    { label: 'Select all', action: () => s.term.selectAll() },
    { label: 'Clear scrollback', action: () => s.term.clear() },
    '-',
    { label: 'Back to all sessions', kbd: 'Shift+Esc', action: collapse },
  ]);
}

function moveSession(s, delta) {
  // Swap with the neighbour shown next to it, within its own project group (tabs are grouped by project).
  const shown = visibleSessions();
  const neighbour = shown[shown.indexOf(s) + delta];
  if (!neighbour || projectIndex(neighbour) !== projectIndex(s)) return;
  const ids = state.sessions.map((x) => x.id);
  const i = ids.indexOf(s.id), j = ids.indexOf(neighbour.id);
  [ids[i], ids[j]] = [ids[j], ids[i]];
  state.sessions = ids.map((id) => state.byId.get(id));
  post({ type: 'reorder', ids });
  renderSessionTabs();
  renderSessionPicker();
  layout();
}

function openModal(title, body, buttons) {
  const hostEl = $('#modalHost');
  const footer = h('div', { class: 'modal-footer' }, buttons);
  hostEl.replaceChildren(h('div', { class: 'modal' }, h('div', { class: 'modal-title' }, title), h('div', { class: 'modal-body' }, body), footer));
  hostEl.hidden = false;
  // Never start focus on a destructive button: a stray Enter/Space must not confirm data loss.
  setTimeout(() => hostEl.querySelector('input, [data-autofocus], button.primary:not(.danger)')?.focus(), 20);
}

function closeModal() {
  $('#modalHost').hidden = true;
  $('#modalHost').replaceChildren();
  const s = state.byId.get(state.expandedId);
  if (s) s.term.focus();
}

$('#modalHost').addEventListener('mousedown', (e) => { if (e.target.id === 'modalHost') closeModal(); });

function confirmDialog(title, text, okLabel, danger, onOk) {
  openModal(title, h('div', {}, text), [
    h('button', { class: 'btn', onclick: closeModal, 'data-autofocus': danger }, 'Cancel'),
    h('button', { class: 'btn ' + (danger ? 'danger primary' : 'primary'), onclick: () => { closeModal(); onOk(); } }, okLabel),
  ]);
}

const confirmRestart = (s) => confirmDialog('Restart session', `Restart "${s.name}"? The running process will be terminated.`, 'Restart', true, () => restartSession(s));
const confirmStop = (s) => confirmDialog('Stop session', `Stop "${s.name}"? The running process will be terminated.`, 'Stop', true, () => request('stop', { id: s.id }));
const confirmRemove = (s) => confirmDialog('Remove session',
  `Remove "${s.name}" from Claude Terminals?${s.running ? ' Its running process will be terminated.' : ''}\nThe folder and its files are not touched.`,
  'Remove', true, () => request('removeSession', { id: s.id }).catch((e) => toast(e.message, true)));

function field(label, input, help) {
  return h('div', { class: 'field' }, h('label', {}, label), input, help && h('div', { class: 'help' }, help));
}

/**
 * A project picker: "No project", each project, or "New project…" (which reveals a name box). resolve() returns the
 * chosen project id, creating the new project first if needed, or null for no project.
 */
function projectField(selectedId) {
  const NEW = '__new__';
  const select = h('select', {},
    h('option', { value: '' }, 'No project (shown with every project)'),
    state.projects.map((p) => h('option', { value: p.id, selected: p.id === selectedId }, p.name)),
    h('option', { value: NEW }, 'New project…'));
  const newName = h('input', { placeholder: 'Project name', spellcheck: 'false', hidden: true, maxlength: '60' });
  select.addEventListener('change', () => {
    newName.hidden = select.value !== NEW;
    if (!newName.hidden) newName.focus();
  });
  let created = null;   // so retrying after a failed save doesn't create the project twice
  return {
    el: field('Project', h('div', { class: 'stack' }, select, newName)),
    select,
    newName,
    async resolve() {
      if (select.value !== NEW) return select.value || null;
      const wanted = newName.value.trim();
      if (!wanted) throw new Error('Enter a name for the new project.');
      if (created?.name !== wanted) created = { name: wanted, id: await request('projects.add', { name: wanted }) };
      return created.id;
    },
  };
}

/** The project new sessions go into: the one you're looking at if exactly one is shown, otherwise none. */
function defaultProjectId() {
  const shown = state.projects.filter((p) => !state.hiddenProjects.has(p.id));
  return shown.length === 1 ? shown[0].id : null;
}

function showEditDialog(s, focus) {
  const name = h('input', { value: s.name, spellcheck: 'false' });
  const command = h('input', { value: s.command, spellcheck: 'false' });
  const project = projectField(s.project);
  const error = h('div', { class: 'form-error', hidden: true });
  const save = async () => {
    let projectId;
    try {
      projectId = await project.resolve();
    } catch (e) {
      error.hidden = false;
      error.textContent = e.message;
      return;
    }
    request('updateSession', { id: s.id, name: name.value, command: command.value, project: projectId ?? '' }).catch((e) => toast(e.message, true));
    closeModal();
    if (s.running && command.value.trim() !== s.command) toast('Command changes apply the next time the session starts.');
    if (projectId && state.hiddenProjects.has(projectId)) toast(`"${s.name}" moved to a project that's hidden; tick it above to see the session.`);
  };
  for (const i of [name, command, project.newName]) i.addEventListener('keydown', (e) => { if (e.key === 'Enter') save(); });
  openModal('Edit session', [
    field('Name', name),
    field('Command', command, 'For example: claude, claude --continue, claude --model opus'),
    project.el,
    field('Folder', h('input', { value: s.directory, readonly: true })),
    error,
  ], [h('button', { class: 'btn', onclick: closeModal }, 'Cancel'), h('button', { class: 'btn primary', onclick: save }, 'Save')]);
  if (focus === 'project') setTimeout(() => project.select.focus(), 30);
}

const leafName = (path) => (path || '').split(/[\\/]/).filter(Boolean).pop() || path;
const resumeCommand = (id) => `claude --resume ${id}`;

/**
 * Searchable list of Claude Code's saved conversations (optionally limited to one folder).
 * onPick(conv, confirm) fires on click; confirm is true for a double-click.
 */
function conversationPicker({ directory = null, onPick }) {
  const search = h('input', { placeholder: 'Search conversations by title, prompt or folder…', spellcheck: 'false' });
  const list = h('div', { class: 'conv-list' }, h('div', { class: 'conv-empty' }, 'Loading conversations…'));
  let all = [];
  let selected = null;

  const openIn = (conv) => state.sessions.find((x) => x.command.includes(conv.sessionId));

  const render = () => {
    const q = search.value.trim().toLowerCase();
    const items = all.filter((c) => !q || [c.title, c.firstPrompt, c.lastPrompt, c.cwd, c.branch].some((v) => v && v.toLowerCase().includes(q)));
    if (!items.length) {
      list.replaceChildren(h('div', { class: 'conv-empty' }, all.length ? 'No conversations match.' : directory ? 'No saved conversations for this folder.' : 'No saved conversations found.'));
      return;
    }
    list.replaceChildren(...items.slice(0, 200).map((c) => {
      const open = openIn(c);
      const prompt = c.lastPrompt && c.lastPrompt !== c.firstPrompt ? c.lastPrompt : c.firstPrompt;
      return h('div', {
        class: 'conv-item' + (selected === c ? ' selected' : '') + (open ? ' open' : ''),
        title: `${c.title}\n${c.cwd ?? ''}\nSession ${c.sessionId}${open ? `\nAlready open in "${open.name}"` : ''}`,
        onclick: () => { if (open) return; selected = c; render(); onPick(c, false); },
        ondblclick: () => { if (!open) onPick(c, true); },
      },
      h('div', { class: 'conv-title' }, h('span', {}, c.title),
        open ? h('span', { class: 'conv-open' }, 'open in ' + open.name)
          : Date.now() - new Date(c.modified) < 3 * 60000 && h('span', { class: 'conv-open', title: 'Updated in the last few minutes — it may still be running in another terminal' }, 'active now')),
      h('div', { class: 'conv-meta' }, [!directory && c.cwd, c.branch && 'on ' + c.branch, relTime(c.modified)].filter(Boolean).join('  ·  ')),
      prompt && h('div', { class: 'conv-prompt' }, prompt));
    }));
  };

  search.addEventListener('input', render);
  request('conversations', { directory })
    .then((r) => { all = r || []; render(); })
    .catch((e) => list.replaceChildren(h('div', { class: 'conv-empty' }, e.message)));
  return h('div', { class: 'conv-picker' }, search, list);
}

function showAddDialog(mode = 'new') {
  const lastDir = store.get('lastDir', '');
  const lastCommand = store.get('lastCommand', 'claude');
  const dir = h('input', { value: '', placeholder: 'D:\\Source\\MyRepo', spellcheck: 'false' });
  const name = h('input', { placeholder: '(folder name)', spellcheck: 'false' });
  const command = h('input', { value: lastCommand, spellcheck: 'false' });
  const wtHost = h('div', { class: 'field', hidden: true });
  const error = h('div', { class: 'form-error', hidden: true });
  const pickerHost = h('div', { class: 'field' });
  const project = projectField(defaultProjectId());

  const loadWorktrees = debounce(async () => {
    const d = dir.value.trim();
    wtHost.hidden = true;
    if (!d || mode !== 'new') return;
    let worktrees;
    try { worktrees = (await request('worktrees', { directory: d })) || []; } catch { worktrees = []; }
    if (dir.value.trim() !== d || mode !== 'new') return;   // a slow reply for a folder no longer in the dialog
    const norm = (p) => p.replace(/[\\/]+$/, '').toLowerCase();
    const existing = new Set(state.sessions.map((s) => norm(s.directory)));
    const others = worktrees.filter((w) => !w.bare && !w.prunable && norm(w.path) !== norm(d));   // prunable: folder is gone
    if (!others.length || mode !== 'new') return;
    wtHost.hidden = false;
    wtHost.dataset.for = d;
    wtHost.replaceChildren(
      h('label', {}, 'Other worktrees of this repository — add sessions for them too:'),
      h('div', { class: 'wt-list' }, others.map((w) => {
        const exists = existing.has(norm(w.path));
        return h('label', { class: 'wt-item' + (exists ? ' exists' : ''), title: exists ? 'Already has a session' : w.path },
          h('input', { type: 'checkbox', 'data-path': w.path, 'data-branch': w.branch || '', disabled: exists }),
          h('span', { class: 'branch' }, w.branch || '(detached)'),
          h('span', { class: 'path' }, w.path));
      })));
  }, 250);
  dir.addEventListener('input', loadWorktrees);

  const browse = h('button', {
    class: 'btn', onclick: async () => {
      const picked = await request('pickFolder', { initial: dir.value || lastDir });
      if (picked) { dir.value = picked; loadWorktrees(); }
    },
  }, 'Browse…');

  const add = async () => {
    const d = dir.value.trim();
    if (!d) { error.hidden = false; error.textContent = mode === 'resume' ? 'Choose a conversation.' : 'Choose a folder.'; return; }
    const cmd = command.value.trim() || 'claude';
    const list = [{ directory: d, name: name.value.trim(), command: cmd }];
    if (mode === 'new' && !wtHost.hidden && wtHost.dataset.for === d) {   // only ticks made for this folder
      for (const cb of wtHost.querySelectorAll('input[type=checkbox]:checked')) {
        list.push({ directory: cb.dataset.path, name: leafName(cb.dataset.path), command: cmd });
      }
    }
    const before = new Set(state.sessions.map((x) => x.id));
    try {
      const projectId = await project.resolve();
      for (const item of list) item.project = projectId ?? '';
      await request('addSessions', { sessions: list });
      // Make sure the new session is on screen even if its project was hidden.
      if (projectId && state.hiddenProjects.has(projectId)) toggleProject(projectId);
      store.set('lastDir', d);
      if (mode === 'new') store.set('lastCommand', cmd);
      closeModal();
      const added = state.sessions.find((x) => !before.has(x.id));
      if (added) expand(added.id);
    } catch (e) {
      error.hidden = false;
      error.textContent = e.message;
    }
  };
  for (const i of [dir, name, command, project.newName]) i.addEventListener('keydown', (e) => { if (e.key === 'Enter') add(); });

  const segNew = h('button', { class: 'seg-btn' }, 'New conversation');
  const segResume = h('button', { class: 'seg-btn' }, 'Resume existing conversation');
  const setMode = (m) => {
    mode = m;
    segNew.classList.toggle('active', m === 'new');
    segResume.classList.toggle('active', m === 'resume');
    error.hidden = true;
    if (m === 'resume') {
      wtHost.hidden = true;
      pickerHost.hidden = false;
      dir.value = '';
      name.value = '';
      command.value = '';
      pickerHost.replaceChildren(h('label', {}, 'Conversation (double-click to add)'), conversationPicker({
        onPick: (c, confirm) => {
          dir.value = c.cwd || '';
          name.value = leafName(c.cwd);
          command.value = resumeCommand(c.sessionId);
          error.hidden = true;
          if (confirm) add();
        },
      }));
      setTimeout(() => pickerHost.querySelector('input')?.focus(), 20);
    } else {
      pickerHost.hidden = true;
      pickerHost.replaceChildren();
      if (!command.value || command.value.startsWith('claude --resume')) command.value = lastCommand;
      loadWorktrees();
    }
  };
  segNew.addEventListener('click', () => setMode('new'));
  segResume.addEventListener('click', () => setMode('resume'));

  openModal('New session', [
    h('div', { class: 'seg' }, segNew, segResume),
    pickerHost,
    field('Folder (repository or worktree)', h('div', { class: 'inline' }, dir, browse)),
    field('Name', name),
    field('Command', command, 'Runs in the folder above. For example: claude, claude --continue, claude --resume <id>'),
    project.el,
    wtHost,
    error,
  ], [h('button', { class: 'btn', onclick: closeModal }, 'Cancel'), h('button', { class: 'btn primary', onclick: add }, 'Add')]);
  $('#modalHost .modal').classList.add('wide');
  setMode(mode);
}

/** Points an existing session at a different saved conversation of its folder (or a fresh one) and restarts it. */
function showResumeDialog(s) {
  let picked = null;
  const resumeBtn = h('button', { class: 'btn primary', disabled: true }, s.running ? 'Resume (restarts session)' : 'Resume');
  const apply = (command) => {
    closeModal();
    request('updateSession', { id: s.id, command })
      .then(() => { s.command = command; restartSession(s); expand(s.id); })
      .catch((e) => toast(e.message, true));
  };
  resumeBtn.addEventListener('click', () => picked && apply(resumeCommand(picked.sessionId)));
  openModal(`Resume a conversation in "${s.name}"`, [
    h('div', { class: 'dim' }, s.directory),
    conversationPicker({
      directory: s.directory,
      onPick: (c, confirm) => { picked = c; resumeBtn.disabled = false; if (confirm) apply(resumeCommand(c.sessionId)); },
    }),
    h('div', { class: 'dim' }, 'Close the conversation in any other terminal first, so two Claude processes don’t write to it at once.'),
  ], [
    h('button', { class: 'btn', onclick: closeModal }, 'Cancel'),
    h('button', { class: 'btn', title: 'Start a fresh conversation in this folder', onclick: () => apply('claude') }, 'New conversation'),
    resumeBtn,
  ]);
  $('#modalHost .modal').classList.add('wide');
}

// ------------------------------------------------------------------ Git tool window

function applyGitVisibility() {
  document.body.classList.toggle('git-hidden', !state.gitVisible);
  $('#gitPanel').style.width = state.gitWidth + 'px';
  $('#btnGit').classList.toggle('active', state.gitVisible);
}

function toggleGit() {
  state.gitVisible = !state.gitVisible;
  store.set('gitVisible', state.gitVisible);
  applyGitVisibility();
  renderGit();
}

function showGit(id) {
  if (state.currentId !== id) {
    state.currentId = id;
    store.set('currentId', id);
    layout();
  }
  if (!state.gitVisible) {
    state.gitVisible = true;
    store.set('gitVisible', true);
    applyGitVisibility();
  }
  renderSessionPicker();
  renderGit();
}

function renderSessionPicker() {
  const sel = $('#gitSession');
  sel.replaceChildren(...visibleSessions().map((s) => h('option', { value: s.id, selected: s.id === state.currentId }, `${s.name}  —  ${s.directory}`)));
}

$('#gitSession').addEventListener('change', (e) => showGit(e.target.value));

function onGitStatus(m) {
  const s = state.byId.get(m.id);
  if (!s) return;
  // A new last commit, or a push/pull changing what is outgoing, makes the cached history stale.
  if (s.git && (s.git.head !== m.head || s.git.upstream !== m.upstream || s.git.ahead !== m.ahead)) invalidateLog(s);
  s.git = m;
  renderTileHeader(s);
  refreshOpenDiff(s);   // the visible diff follows its own session, whichever one the Git panel shows
  const historyPending = !s.log && !logFailed(s);
  if (s.id === state.currentId && (state.gitTab === 'changes' || historyPending)) renderGit();
}

const currentSession = () => state.byId.get(state.currentId);

/** The last history load failed and nothing has changed since (logGen is undefined until the first invalidation). */
const logFailed = (s) => s.logError != null && s.logError.gen === s.logGen;

/** Drops the cached history; a history request already on its way is then not cached either (it may predate the change). */
function invalidateLog(s) {
  s.log = null;
  s.logGen = (s.logGen || 0) + 1;
}

function renderGit() {
  if (!state.gitVisible) return;
  document.querySelectorAll('.tw-tab').forEach((b) => b.classList.toggle('active', b.dataset.tab === state.gitTab));
  const body = $('#gitBody');
  const s = currentSession();
  if (!s) { body.replaceChildren(h('div', { class: 'git-message' }, 'No session selected.')); return; }
  if (state.gitTab === 'repo') renderRepo(s, body);
  else renderChanges(s, body);
}

function branchLine(s) {
  const g = s.git;
  return h('div', { class: 'git-branch', title: g.upstream ? `Tracking ${g.upstream}` : 'No upstream branch' },
    iconEl('branch'), h('span', { class: 'name' }, g.branch || '(unknown)'),
    g.upstream && h('span', { class: 'ab', title: `${g.ahead} outgoing / ${g.behind} incoming` },
      iconEl('up'), String(g.ahead), ' ', iconEl('down'), String(g.behind)));
}

function renderChanges(s, body) {
  const g = s.git;
  if (!g) { body.replaceChildren(h('div', { class: 'git-message' }, 'Loading…')); return; }
  if (!g.isRepo) {
    body.replaceChildren(h('div', { class: 'git-message' }, g.error ? `Git error: ${g.error}` : `${s.directory} is not inside a Git repository.`));
    return;
  }

  const scroller = body.querySelector('.git-scroll');
  const scrollTop = scroller ? scroller.scrollTop : 0;

  const ui = commitArea(s);
  ui.update();

  const scroll = h('div', { class: 'git-scroll' });
  const total = g.staged.length + g.unstaged.length;
  if (total === 0) scroll.append(h('div', { class: 'git-message' }, 'No changes. The working tree is clean.'));
  if (g.staged.length) scroll.append(...fileSection(s, 'staged', 'Staged Changes', g.staged));
  if (g.unstaged.length) scroll.append(...fileSection(s, 'unstaged', 'Changes', g.unstaged));

  const before = branchLine(s);
  const after = [
    s.info && h('div', { class: 'infobar' + (s.info.error ? ' error' : '') }, h('span', { class: 'msg' }, s.info.text),
      h('button', { class: 'icon-btn small', title: 'Dismiss', onclick: () => { s.info = null; renderGit(); } }, iconEl('close'))),
    scroll,
  ].filter(Boolean);
  if (ui.wrap.parentNode === body) {
    // Rebuild everything around the commit box but leave the box itself in place: taking it out of the page, even
    // briefly, drops its focus and any IME composition, and this runs on every status update.
    for (const n of [...body.childNodes]) if (n !== ui.wrap) n.remove();
    ui.wrap.before(before);
    ui.wrap.after(...after);
  } else {
    fill(body, before, ui.wrap, ...after);
  }
  scroll.scrollTop = scrollTop;
}

/** The session's commit message box and button, created once so its text, caret, selection and focus survive refreshes. */
function commitArea(s) {
  if (s.commitUi) return s.commitUi;
  const msg = h('textarea', { class: 'commit-msg', placeholder: 'Enter a message <Required>', spellcheck: 'true' });
  msg.value = s.commitDraft;
  const btn = h('button', { class: 'btn primary' });
  const count = h('span', { class: 'dim' });
  const wrap = h('div', { class: 'git-commit' }, msg, h('div', { class: 'git-commit-actions' }, btn, count));
  const update = () => {
    const g = s.git;
    const total = g ? g.staged.length + g.unstaged.length : 0;
    btn.textContent = g?.staged.length ? 'Commit Staged' : 'Commit All';
    btn.disabled = !msg.value.trim() || total === 0;
    count.textContent = total ? `${total} change${total === 1 ? '' : 's'}` : '';
  };
  msg.addEventListener('input', () => { s.commitDraft = msg.value; s.commitEdits = (s.commitEdits || 0) + 1; update(); });
  msg.addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.ctrlKey && !btn.disabled) btn.click(); });
  btn.addEventListener('click', () => doCommit(s, !s.git?.staged.length));
  s.commitUi = { wrap, msg, update };
  return s.commitUi;
}

async function doCommit(s, all) {
  const submitted = s.commitDraft;
  if (!submitted.trim()) return;
  const edits = s.commitEdits;
  try {
    const out = await request('git.commit', { id: s.id, message: submitted, all });
    const sha = /\[[^\]]*?\s([0-9a-f]{7,})\]/.exec(out || '')?.[1];
    // Clear the box only if nobody typed in it while git ran (even retyping the same text counts as a new message).
    if (s.commitEdits === edits) {
      s.commitDraft = '';
      if (s.commitUi) s.commitUi.msg.value = '';
    }
    s.info = { text: sha ? `Commit ${sha} created locally.` : 'Commit created locally.' };
    invalidateLog(s);
  } catch (e) {
    s.info = { text: e.message, error: true };
  }
  renderGit();
}

/** Builds VS-style folder tree rows for a set of changed files. */
function fileSection(s, kind, title, files) {
  const key = kind + ':';
  const collapsed = s.collapsed.has(key);
  const undoable = files.filter((f) => f.status !== 'U');
  const stageAll = () => gitOp(s, kind === 'staged' ? 'git.unstage' : 'git.stage', files.map((f) => f.path));
  const header = h('div', {
    class: 'section-header',
    onclick: (e) => {
      if (e.target.closest('button')) return;
      collapsed ? s.collapsed.delete(key) : s.collapsed.add(key);
      renderGit();
    },
    oncontextmenu: (e) => {
      e.preventDefault();
      showMenu(e.clientX, e.clientY, [
        { label: kind === 'staged' ? 'Unstage All' : 'Stage All', action: stageAll },
        undoable.length && { label: 'Undo All Changes…', danger: true, action: () => confirmUndo(s, kind, undoable) },
      ]);
    },
  },
  h('span', { class: 'chev', html: icon(collapsed ? 'chevR' : 'chevD') }), title, h('span', { class: 'count' }, `(${files.length})`),
  h('span', { class: 'actions' },
    undoable.length ? h('button', { class: 'icon-btn small', title: 'Undo All Changes…', onclick: () => confirmUndo(s, kind, undoable) }, iconEl('undo')) : null,
    h('button', { class: 'icon-btn small', title: kind === 'staged' ? 'Unstage All' : 'Stage All', onclick: stageAll }, iconEl(kind === 'staged' ? 'minus' : 'plus'))));
  if (collapsed) return [header];
  return [header, ...treeRows(files, 1, kind, s, (f, pin) => openDiff(s, { mode: kind, path: f.path, origPath: f.origPath, status: f.status }, pin),
    (f) => (kind === 'staged'
      ? h('button', { class: 'icon-btn small', title: 'Unstage', onclick: (e) => { e.stopPropagation(); gitOp(s, 'git.unstage', [f.path]); } }, iconEl('minus'))
      : h('button', { class: 'icon-btn small', title: 'Stage', onclick: (e) => { e.stopPropagation(); gitOp(s, 'git.stage', [f.path]); } }, iconEl('plus'))),
    (f, e) => showMenu(e.clientX, e.clientY, [
      { label: 'Compare with Unmodified', action: () => openDiff(s, { mode: kind, path: f.path, origPath: f.origPath, status: f.status }, true) },
      { label: 'Open', action: () => request('openFile', { id: s.id, path: f.path }) },
      kind === 'staged'
        ? { label: 'Unstage', action: () => gitOp(s, 'git.unstage', [f.path]) }
        : { label: 'Stage', action: () => gitOp(s, 'git.stage', [f.path]) },
      f.status !== 'U' && { label: 'Undo Changes…', danger: true, action: () => confirmUndo(s, kind, [f]) },
      '-',
      { label: 'Copy Full Path', action: () => navigator.clipboard.writeText(joinPath(s.git?.root || s.directory, f.path)) },
      { label: 'Copy Relative Path', action: () => navigator.clipboard.writeText(f.path.replaceAll('/', '\\')) },
    ]),
    (folder, folderFiles, e) => {
      const undoableInFolder = folderFiles.filter((f) => f.status !== 'U');
      showMenu(e.clientX, e.clientY, [
        kind === 'staged'
          ? { label: 'Unstage', action: () => gitOp(s, 'git.unstage', folderFiles.map((f) => f.path)) }
          : { label: 'Stage', action: () => gitOp(s, 'git.stage', folderFiles.map((f) => f.path)) },
        undoableInFolder.length && { label: 'Undo Changes…', danger: true, action: () => confirmUndo(s, kind, undoableInFolder, folder) },
      ]);
    })];
}

/**
 * VS-style "Undo Changes" confirmation. Spells out what is lost: unstaged undo reverts to the staged version
 * (or deletes new files); staged undo reverts to the last commit and drops unstaged edits to those files too.
 */
function confirmUndo(s, kind, files, folder) {
  const staged = kind === 'staged';
  const unstagedPaths = new Set((s.git?.unstaged || []).map((f) => f.path));
  const isNew = (f) => (staged ? f.status === 'A' : f.untracked);
  const created = files.filter(isNew);
  const reverted = files.filter((f) => !isNew(f));
  const renamed = staged ? files.filter((f) => f.status === 'R') : [];
  const alsoUnstaged = staged ? files.filter((f) => unstagedPaths.has(f.path)) : [];
  const head = s.git?.head ?? null;   // the commit this dialog describes: s.git may be replaced before the click
  // Single file: 'The file …'; several: '2 files …' / '1 file …'.
  const count = (n, single, noun) => (files.length === 1 ? single : `${n} ${noun}${n === 1 ? '' : 's'}`);

  const what = files.length === 1
    ? `"${files[0].path.split('/').pop()}"`
    : folder ? `${files.length} files in "${folder.replaceAll('/', '\\')}"` : `${files.length} files`;
  const list = files.slice(0, 12).map((f) => h('div', { class: 'undo-file' },
    h('span', { class: `status st-${f.status}` }, f.status), f.path.replaceAll('/', '\\')));
  if (files.length > 12) list.push(h('div', { class: 'dim' }, `…and ${files.length - 12} more`));

  const notes = [];
  if (reverted.length) {
    notes.push(staged
      ? `${count(reverted.length, 'The file', 'file')} will be restored to the last commit. These changes can't be recovered.`
      : `${count(reverted.length, 'The file', 'file')} will be restored to the staged version (or the last commit if nothing is staged). These changes can't be recovered.`);
  }
  if (renamed.length) notes.push(`${count(renamed.length, 'The rename', 'rename')} will be reverted to the original name.`);
  if (created.length) notes.push(`${count(created.length, 'This new file', 'new file')} will be moved to the Recycle Bin.`);
  if (alsoUnstaged.length) notes.push(`Unstaged edits to ${files.length === 1 ? 'this file' : `${alsoUnstaged.length} of these files`} will be lost as well.`);

  confirmDialog('Undo Changes',
    h('div', { class: 'undo-confirm' },
      h('div', {}, `Are you sure you want to undo changes to ${what}?`),
      h('div', { class: 'undo-list' }, list),
      notes.map((n) => h('div', { class: 'undo-note' }, n))),
    'Undo Changes', true,
    async () => {
      try {
        // Send what this dialog showed (the files, the commit, which ones had unstaged edits); the host refuses if any changed since.
        await request('git.undo', { id: s.id, staged, head, alsoUnstaged: alsoUnstaged.map((f) => f.path),
          files: files.map(({ path, origPath, status, untracked, inHead }) => ({ path, origPath, status, untracked, inHead })) });
      } catch (e) {
        s.info = { text: e.message, error: true };
        renderGit();
      }
    });
}

const joinPath = (root, rel) => root.replace(/[\\/]+$/, '') + '\\' + rel.replaceAll('/', '\\');

function treeRows(files, depth, scope, s, onOpen, rowAction, onMenu, onFolderMenu) {
  const root = { dirs: new Map(), files: [] };
  for (const f of files) {
    const parts = f.path.split('/');
    let node = root;
    for (const part of parts.slice(0, -1)) {
      if (!node.dirs.has(part)) node.dirs.set(part, { dirs: new Map(), files: [] });
      node = node.dirs.get(part);
    }
    node.files.push(f);
  }

  const filesUnder = (node) => [...node.files, ...[...node.dirs.values()].flatMap(filesUnder)];
  const rows = [];
  const walk = (node, d, prefix) => {
    const dirs = [...node.dirs.entries()].sort((a, b) => a[0].localeCompare(b[0], undefined, { sensitivity: 'base' }));
    for (let [name, child] of dirs) {
      let full = prefix + name;
      // Compact single-child folder chains ("src\Foo\Bar"), as VS does.
      while (child.files.length === 0 && child.dirs.size === 1) {
        const [n2, c2] = [...child.dirs.entries()][0];
        name += '\\' + n2;
        full += '/' + n2;
        child = c2;
      }
      const key = `${scope}:${full}/`;
      const isCollapsed = s.collapsed.has(key);
      const folderNode = child;
      rows.push(h('div', {
        class: 'row', style: `padding-left:${d * 12}px`, title: full,
        onclick: () => { isCollapsed ? s.collapsed.delete(key) : s.collapsed.add(key); renderGit(); },
        oncontextmenu: (e) => { e.preventDefault(); onFolderMenu?.(full, filesUnder(folderNode), e); },
      }, h('span', { class: 'chev', html: icon(isCollapsed ? 'chevR' : 'chevD') }), iconEl('folder', 'ico-folder'), h('span', { class: 'label' }, name)));
      if (!isCollapsed) walk(child, d + 1, full + '/');
    }
    const fl = [...node.files].sort((a, b) => a.path.localeCompare(b.path, undefined, { sensitivity: 'base' }));
    for (const f of fl) {
      const fname = f.path.split('/').pop();
      const selKey = `${scope}:${f.path}`;
      const isSel = s.selectedFile === selKey;
      const statusTitle = { M: 'Modified', A: 'Added', D: 'Deleted', R: 'Renamed', U: 'Conflict' }[f.status] || f.status;
      rows.push(h('div', {
        class: `row file-${f.status}` + (isSel ? ' selected' : ''), style: `padding-left:${d * 12}px`,
        title: `${f.path}${f.origPath ? `\nRenamed from ${f.origPath}` : ''}\n${statusTitle}`,
        tabindex: '-1',
        onclick: (e) => {
          s.selectedFile = selKey;
          // Select in place (no re-render) so a following double-click lands on the same row.
          document.querySelectorAll('#gitPanel .row.selected').forEach((r) => r.classList.remove('selected'));
          e.currentTarget.classList.add('selected');
          onOpen(f, false);
        },
        ondblclick: () => onOpen(f, true),
        oncontextmenu: (e) => { e.preventDefault(); s.selectedFile = selKey; onMenu?.(f, e); },
      },
      h('span', { class: 'chev' }), iconEl('file', 'ico-file'), h('span', { class: 'label' }, fname),
      f.origPath && h('span', { class: 'hint' }, `← ${f.origPath.split('/').pop()}`),
      rowAction && h('span', { class: 'row-actions' }, rowAction(f)),
      h('span', { class: `status st-${f.status}`, title: statusTitle }, f.status)));
    }
  };
  walk(root, depth, '');
  return rows;
}

async function gitOp(s, type, paths) {
  try {
    await request(type, { id: s.id, paths });
  } catch (e) {
    s.info = { text: e.message, error: true };
    renderGit();
  }
}

// ---- Git Repository (history) view

async function renderRepo(s, body) {
  if (!s.git?.isRepo) {
    body.replaceChildren(h('div', { class: 'git-message' }, s.git ? `${s.directory} is not inside a Git repository.` : 'Loading…'));
    return;
  }
  // An unborn branch has no history to load, and git log would only fail.
  if (s.git.head === '(initial)') {
    body.replaceChildren(branchLine(s), h('div', { class: 'git-message' }, 'No commits yet.'));
    return;
  }
  // A failure is kept until the history actually changes (invalidateLog bumps logGen), so status updates don't
  // re-request a log that just failed, over and over while a session is editing files.
  if (!s.log && logFailed(s)) {
    body.replaceChildren(h('div', { class: 'git-message' }, `Could not load the history: ${s.logError.message} `,
      h('button', { class: 'btn', onclick: () => { invalidateLog(s); renderGit(); } }, 'Retry')));
    return;
  }
  if (!s.log) {
    body.replaceChildren(h('div', { class: 'git-message' }, 'Loading history…'));
    if (s.logLoading) return;   // already on its way; it renders when it arrives
    s.logLoading = true;
    const asked = s.logGen;
    let log, failure;
    try { log = await request('git.log', { id: s.id }); } catch (e) { failure = e.message; } finally { s.logLoading = false; }
    const current = currentSession() === s && state.gitTab === 'repo';
    if (s.logGen !== asked) { if (current) renderGit(); return; }   // the history changed while loading: ask again
    if (failure) {   // not cached as an empty history; remembered for this generation, so Retry or a real change asks again
      s.logError = { gen: asked, message: failure };
      if (current) renderGit();
      return;
    }
    s.log = log;
    if (!current) return;
  }
  const logEl = h('div', { class: 'repo-log' });
  const outgoing = s.log.filter((c) => c.outgoing);
  const rest = s.log.filter((c) => !c.outgoing);
  const commitRow = (c) => h('div', {
    class: 'commit-row' + (c.outgoing ? ' outgoing' : '') + (s.selectedCommit === c.sha ? ' selected' : ''),
    title: `${c.subject}\n${c.author} · ${new Date(c.date).toLocaleString()}\n${c.sha}`,
    onclick: () => selectCommit(s, c.sha),
  }, h('span', { class: 'graph' }), refBadges(c.refs), h('span', { class: 'commit-subject' }, c.subject),
  h('span', { class: 'commit-meta' }, `${c.author} · ${relTime(c.date)}`));

  if (outgoing.length) logEl.append(h('div', { class: 'group-label' }, `Outgoing (${outgoing.length})`), ...outgoing.map(commitRow));
  logEl.append(h('div', { class: 'group-label' }, s.git.branch ? `Local history · ${s.git.branch}` : 'Local history'), ...rest.map(commitRow));
  if (!s.log.length) logEl.append(h('div', { class: 'git-message' }, 'No commits yet.'));

  const details = h('div', { class: 'repo-details' });
  renderCommitDetails(s, details);
  body.replaceChildren(branchLine(s), h('div', { class: 'repo-split' }, logEl, details));
}

function refBadges(refs) {
  if (!refs) return null;
  return refs.split(', ').filter(Boolean).slice(0, 3).map((r) => {
    const head = r.startsWith('HEAD -> ');
    const tag = r.startsWith('tag: ');
    const label = r.replace('HEAD -> ', '').replace('tag: ', '');
    return h('span', { class: 'ref' + (head ? ' head' : tag ? ' tag' : '') }, label);
  });
}

async function selectCommit(s, sha) {
  s.selectedCommit = sha;
  s.commitInfo = null;
  renderGit();
  try {
    s.commitInfo = await request('git.commitDetails', { id: s.id, sha });
  } catch (e) {
    toast(e.message, true);
  }
  if (currentSession() === s && s.selectedCommit === sha) renderGit();
}

function renderCommitDetails(s, el) {
  if (!s.selectedCommit) { el.append(h('div', { class: 'git-message' }, 'Select a commit to see its details.')); return; }
  const c = s.commitInfo;
  if (!c) { el.append(h('div', { class: 'git-message' }, 'Loading…')); return; }
  const [subject, ...bodyLines] = c.message.split('\n');
  el.append(
    h('div', { class: 'details-head' },
      h('span', { class: 'k' }, 'Commit'), h('span', {}, c.sha.slice(0, 10)),
      h('span', { class: 'k' }, 'Author'), h('span', {}, c.author),
      h('span', { class: 'k' }, 'Date'), h('span', {}, new Date(c.date).toLocaleString())),
    h('div', { class: 'details-msg' }, h('b', {}, subject), bodyLines.join('\n').trim() ? '\n\n' + bodyLines.join('\n').trim() : ''),
    h('div', { class: 'section-header' }, h('span', { class: 'chev', html: icon('chevD') }), 'Changes', h('span', { class: 'count' }, `(${c.files.length})`)),
    ...treeRows(c.files, 1, 'commit-' + c.sha, s, (f, pin) => openDiff(s, { mode: 'commit', path: f.path, origPath: f.origPath, status: f.status, commit: c.sha, parent: c.parent }, pin)),
  );
}

document.querySelectorAll('.tw-tab').forEach((b) => b.addEventListener('click', () => {
  state.gitTab = b.dataset.tab;
  const s = currentSession();
  if (s && state.gitTab === 'repo') invalidateLog(s);   // refresh history whenever the tab is opened
  renderGit();
}));

$('#gitRefresh').addEventListener('click', () => {
  const s = currentSession();
  if (!s) return;
  invalidateLog(s);
  request('git.refresh', { id: s.id }).catch((e) => toast(e.message, true));
  if (state.gitTab === 'repo') renderGit();
});
$('#gitHide').addEventListener('click', toggleGit);

// splitter
(() => {
  const sp = $('#splitter');
  sp.addEventListener('mousedown', (e) => {
    e.preventDefault();
    sp.classList.add('dragging');
    const startX = e.clientX, startW = state.gitWidth;
    const move = (ev) => {
      state.gitWidth = Math.max(260, Math.min(innerWidth - 400, startW + (startX - ev.clientX)));
      $('#gitPanel').style.width = state.gitWidth + 'px';
    };
    const up = () => {
      sp.classList.remove('dragging');
      store.set('gitWidth', state.gitWidth);
      removeEventListener('mousemove', move);
      removeEventListener('mouseup', up);
    };
    addEventListener('mousemove', move);
    addEventListener('mouseup', up);
  });
})();

// ------------------------------------------------------------------ diff document (Monaco)

let monacoReady = null;
let diffEditor = null;
const diffState = { spec: null, sessionId: null, sideBySide: store.get('diffSideBySide', true), ignoreWs: store.get('diffIgnoreWs', false), changes: [], seq: 0 };

function loadMonaco() {
  if (monacoReady) return monacoReady;
  monacoReady = new Promise((resolve, reject) => {
    require.config({ paths: { vs: 'lib/monaco/vs' } });
    require(['vs/editor/editor.main'], (m) => {
      window.monaco = window.monaco || m;
      monaco.editor.defineTheme('vs2026-dark', {
        base: 'vs-dark',
        inherit: true,
        rules: [],
        colors: {
          'editor.background': '#1e1e1e',
          'editorLineNumber.foreground': '#2b91af',
          'editorLineNumber.activeForeground': '#2b91af',
          'editor.selectionBackground': '#264f78',
          'editor.lineHighlightBackground': '#00000000',
          'editor.lineHighlightBorder': '#2a2a2a',
          'diffEditor.insertedLineBackground': '#2d4a1e66',
          'diffEditor.insertedTextBackground': '#4f7a2c99',
          'diffEditor.removedLineBackground': '#5a1d1d66',
          'diffEditor.removedTextBackground': '#9e2a2a99',
          'diffEditor.diagonalFill': '#3a3a3a80',
          'diffEditorGutter.insertedLineBackground': '#2d4a1e99',
          'diffEditorGutter.removedLineBackground': '#5a1d1d99',
          'diffEditorOverview.insertedForeground': '#57a64acc',
          'diffEditorOverview.removedForeground': '#d64545cc',
          'scrollbarSlider.background': '#4a4a4f99',
        },
      });
      diffEditor = monaco.editor.createDiffEditor($('#diffEditor'), {
        theme: 'vs2026-dark',
        automaticLayout: true,
        readOnly: true,
        originalEditable: false,
        renderSideBySide: diffState.sideBySide,
        ignoreTrimWhitespace: diffState.ignoreWs,
        renderIndicators: true,
        renderOverviewRuler: true,
        enableSplitViewResizing: true,
        useInlineViewWhenSpaceIsLimited: false,
        fontFamily: '"Cascadia Mono", Consolas, monospace',
        fontSize: 13,
        scrollBeyondLastLine: false,
        minimap: { enabled: false },
        glyphMargin: false,
        folding: false,
        renderLineHighlight: 'line',
        stickyScroll: { enabled: false },
        diffAlgorithm: 'advanced',
      });
      diffEditor.onDidUpdateDiff(() => updateDiffSummary());
      resolve();
    }, reject);
  });
  return monacoReady;
}

function languageFor(path) {
  const lower = path.toLowerCase();
  const ext = '.' + lower.split('.').pop();
  const base = lower.split('/').pop();
  for (const l of monaco.languages.getLanguages()) {
    if (l.filenames?.some((f) => f.toLowerCase() === base)) return l.id;
    if (l.extensions?.some((e) => e.toLowerCase() === ext)) return l.id;
  }
  if (['.csproj', '.props', '.targets', '.xaml', '.resx', '.config', '.nuspec'].includes(ext)) return 'xml';
  if (['.razor', '.cshtml'].includes(ext)) return 'razor';
  return 'plaintext';
}

// ---- document tabs: a pinned Terminal tab followed by diff tabs (VS preview-tab semantics)

const docs = { tabs: [], active: 'terminal' };
const docKey = (sessionId, spec) => [sessionId, spec.mode, spec.commit || '', spec.path].join('|');
const activeTab = () => docs.tabs.find((t) => t.key === docs.active) || null;

/** Single-click opens the reusable (italic) preview tab; pin=true opens or keeps a permanent tab. */
function openDiff(s, spec, pin = false) {
  const key = docKey(s.id, spec);
  let tab = docs.tabs.find((t) => t.key === key);
  if (tab) {
    if (pin) tab.preview = false;
  } else {
    tab = { key, sessionId: s.id, spec, preview: !pin, viewState: null };
    const previewIdx = pin ? -1 : docs.tabs.findIndex((t) => t.preview);
    if (previewIdx >= 0) docs.tabs[previewIdx] = tab;
    else docs.tabs.push(tab);
  }
  activateDoc(key);
}

function activateDoc(key) {
  const prev = activeTab();
  if (prev && diffEditor && prev.key !== key) prev.viewState = diffEditor.saveViewState();
  docs.active = docs.tabs.some((t) => t.key === key) ? key : 'terminal';
  const tab = activeTab();
  $('#diffDoc').hidden = !tab;
  $('#termPane').classList.toggle('behind', !!tab);
  renderDocTabs();
  if (tab) {
    if (prev !== tab) loadDiff(tab);
  } else {
    diffState.seq++;
    const s = state.byId.get(state.expandedId);
    if (s) setTimeout(() => s.term.focus(), 0);
  }
}

const showTerminalDoc = () => activateDoc('terminal');

function closeDoc(key) {
  const i = docs.tabs.findIndex((t) => t.key === key);
  if (i < 0) return;
  const wasActive = docs.active === key;
  docs.tabs.splice(i, 1);
  if (wasActive) {
    docs.active = 'terminal';   // don't save view state for the tab being closed
    const next = docs.tabs[i] || docs.tabs[i - 1];
    activateDoc(next ? next.key : 'terminal');
  } else {
    renderDocTabs();
  }
  if (!docs.tabs.length) disposeDiffModels();
}

function cycleDoc(delta) {
  const keys = ['terminal', ...docs.tabs.map((t) => t.key)];
  const i = keys.indexOf(docs.active);
  activateDoc(keys[(i + delta + keys.length) % keys.length]);
}

function renderDocTabs() {
  const ex = state.byId.get(state.expandedId);
  const waiting = visibleSessions().filter((s) => s.attention).length;   // hidden projects show theirs on the project tabs
  const termTab = h('div', {
    class: 'doc-tab terminal' + (docs.active === 'terminal' ? ' active' : ''),
    title: 'Terminals (Shift+Esc from a diff, Ctrl+PgUp/PgDn to switch tabs)',
    onclick: showTerminalDoc,
  }, iconEl('terminal'), h('span', { class: 'doc-title' }, ex ? ex.name : 'All sessions'),
  waiting ? h('span', { class: 'badge', title: `${waiting} session(s) waiting for you` }, String(waiting)) : null);

  const tabs = docs.tabs.map((t) => {
    const s = state.byId.get(t.sessionId);
    const sub = t.spec.mode === 'commit' ? t.spec.commit.slice(0, 7) : t.spec.mode === 'staged' ? 'staged' : 'diff';
    return h('div', {
      class: 'doc-tab' + (t.preview ? ' preview' : '') + (docs.active === t.key ? ' active' : ''),
      title: `${s?.name ?? ''}: ${t.spec.path} (${sub})${t.preview ? '\nPreview tab — double-click to keep it open' : ''}`,
      onclick: (e) => { if (!e.target.closest('button')) activateDoc(t.key); },
      ondblclick: () => { t.preview = false; renderDocTabs(); },
      onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },
      onauxclick: (e) => { if (e.button === 1) closeDoc(t.key); },
    }, h('span', { class: 'doc-title' }, t.spec.path.split('/').pop()),
    h('span', { class: 'doc-sub' }, `${s?.name ?? ''} · ${sub}`),
    h('button', { class: 'icon-btn small', title: 'Close (Ctrl+F4)', onclick: () => closeDoc(t.key) }, iconEl('close')));
  });
  fill($('#docTabs'), termTab, tabs);
  $('#docTabs .doc-tab.active')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
}

async function loadDiff(tab, keepView = false) {
  const seq = ++diffState.seq;
  const s = state.byId.get(tab.sessionId);
  const spec = tab.spec;
  if (!s) return;
  $('#diffSession').textContent = s.name + (spec.mode === 'commit' ? ` · ${spec.commit.slice(0, 7)}` : spec.mode === 'staged' ? ' · Staged' : '');
  if (!keepView) $('#diffSummary').textContent = 'Loading…';

  let result;
  try {
    await loadMonaco();
    result = await request('git.diff', { id: s.id, ...spec });
  } catch (e) {
    if (seq === diffState.seq) { $('#diffSummary').textContent = ''; showNotice(e.message); }
    return;
  }
  if (seq !== diffState.seq) return;

  $('#diffLeftLabel').textContent = result.originalLabel;
  $('#diffRightLabel').textContent = result.modifiedLabel;
  showNotice(result.notice);

  const old = diffEditor.getModel();
  if (keepView && old && old.original.getValue() === result.original && old.modified.getValue() === result.modified) return;
  const view = keepView ? diffEditor.saveViewState() : tab.viewState;

  const lang = languageFor(spec.path);
  diffEditor.setModel({
    original: monaco.editor.createModel(result.original, lang),
    modified: monaco.editor.createModel(result.modified, lang),
  });
  old?.original.dispose();
  old?.modified.dispose();
  diffState.revealFirst = !view;
  if (view) diffEditor.restoreViewState(view);
}

function disposeDiffModels() {
  const m = diffEditor?.getModel();
  diffEditor?.setModel(null);
  m?.original.dispose();
  m?.modified.dispose();
}

function showNotice(text) {
  $('#diffNotice').hidden = !text;
  $('#diffNotice').textContent = text || '';
}

function updateDiffSummary() {
  const changes = diffEditor.getLineChanges() || [];
  diffState.changes = changes;
  let added = 0, removed = 0;
  for (const c of changes) {
    if (c.modifiedEndLineNumber >= c.modifiedStartLineNumber && c.modifiedEndLineNumber > 0) added += c.modifiedEndLineNumber - c.modifiedStartLineNumber + 1;
    if (c.originalEndLineNumber >= c.originalStartLineNumber && c.originalEndLineNumber > 0) removed += c.originalEndLineNumber - c.originalStartLineNumber + 1;
  }
  // An empty side (added/deleted file) still has one empty line; don't count it.
  const m = diffEditor.getModel();
  if (m && m.original.getValueLength() === 0) removed = 0;
  if (m && m.modified.getValueLength() === 0) added = 0;
  const el = $('#diffSummary');
  el.replaceChildren(`${changes.length} change${changes.length === 1 ? '' : 's'}  `, h('span', { class: 'add' }, `+${added}`), ' ', h('span', { class: 'del' }, `−${removed}`));
  $('#diffPrev').disabled = $('#diffNext').disabled = changes.length === 0;
  if (diffState.revealFirst && changes.length) {
    diffState.revealFirst = false;
    goToChange(0, true);
  }
}

function goToChange(dir, absolute = false) {
  const changes = diffState.changes;
  if (!changes.length) return;
  const ed = diffEditor.getModifiedEditor();
  const line = ed.getPosition()?.lineNumber ?? 0;
  const start = (c) => Math.max(1, c.modifiedStartLineNumber || c.modifiedEndLineNumber || 1);
  let target;
  if (absolute) target = changes[dir];
  else if (dir > 0) target = changes.find((c) => start(c) > line) ?? changes[0];
  else target = [...changes].reverse().find((c) => start(c) < line) ?? changes[changes.length - 1];
  const ln = start(target);
  ed.setPosition({ lineNumber: ln, column: 1 });
  ed.revealLineInCenter(ln);
  ed.focus();
}

/** Keeps the visible diff live while the session edits the file. */
function refreshOpenDiff(s) {
  const t = activeTab();
  if (!t || t.sessionId !== s.id || t.spec.mode === 'commit') return;
  loadDiff(t, true);
}

function applyDiffOptions() {
  $('#diffDoc').classList.toggle('inline', !diffState.sideBySide);
  $('#diffMode').innerHTML = icon(diffState.sideBySide ? 'sideBySide' : 'inline');
  $('#diffMode').title = diffState.sideBySide ? 'Side-by-side (click for inline)' : 'Inline (click for side-by-side)';
  $('#diffWs').classList.toggle('on', diffState.ignoreWs);
  diffEditor?.updateOptions({ renderSideBySide: diffState.sideBySide, ignoreTrimWhitespace: diffState.ignoreWs });
}

$('#diffPrev').addEventListener('click', () => goToChange(-1));
$('#diffNext').addEventListener('click', () => goToChange(1));
$('#diffMode').addEventListener('click', () => { diffState.sideBySide = !diffState.sideBySide; store.set('diffSideBySide', diffState.sideBySide); applyDiffOptions(); });
$('#diffWs').addEventListener('click', () => { diffState.ignoreWs = !diffState.ignoreWs; store.set('diffIgnoreWs', diffState.ignoreWs); applyDiffOptions(); });
$('#diffOpenFile').addEventListener('click', () => {
  const t = activeTab();
  if (t) request('openFile', { id: t.sessionId, path: t.spec.path }).catch((e) => toast(e.message, true));
});
$('#diffDoc').addEventListener('keydown', (e) => {
  if (e.altKey && e.key === 'ArrowDown') { e.preventDefault(); goToChange(1); }
  if (e.altKey && e.key === 'ArrowUp') { e.preventDefault(); goToChange(-1); }
});

// ------------------------------------------------------------------ boot

$('#btnGrid').innerHTML = icon('grid');
$('#btnGit').innerHTML = icon('git');
$('#btnAdd').innerHTML = icon('plus') + '<span>New Session</span>';
$('#gitRefresh').innerHTML = icon('refresh');
$('#gitHide').innerHTML = icon('close');
$('#diffPrev').innerHTML = icon('up');
$('#diffNext').innerHTML = icon('down');
$('#diffWs').innerHTML = icon('whitespace');
$('#diffOpenFile').innerHTML = icon('openFile');

$('#btnGrid').addEventListener('click', () => { showTerminalDoc(); collapse(); });
$('#btnGit').addEventListener('click', toggleGit);
// Not addEventListener('click', showAddDialog): the click event would arrive as its `mode` argument.
$('#btnAdd').addEventListener('click', () => showAddDialog());
$('#btnAddEmpty').addEventListener('click', () => showAddDialog());

applyGitVisibility();
applyDiffOptions();
document.fonts.ready.then(() => post({ type: 'ready' }));
