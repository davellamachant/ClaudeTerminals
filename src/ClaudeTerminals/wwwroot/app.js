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
  list: 'M5.8 4h7.7M5.8 8h7.7M5.8 12h7.7M2.6 4h.1M2.6 8h.1M2.6 12h.1',
  plug: 'M6 1.5v3M10 1.5v3M4 4.5h8v2.5a4 4 0 0 1-8 0zM8 11v3.5',   // a plugin's tab   // the Project view: a list of the project's worktrees
  git: 'M5 3v10M5 11a2 2 0 1 0 0 .01M5 3a1.6 1.6 0 1 0 0 .01M11 3a1.6 1.6 0 1 0 0 .01M11 4.6C11 8 5 7 5 10',
  branch: 'M5 2.5v8M5 12a1.5 1.5 0 1 0 0 .01M11 4a1.5 1.5 0 1 0 0 .01M11 5.5c0 3-6 2.5-6 5',
  chevR: 'M6 4l4 4-4 4',
  chevL: 'M10 4l-4 4 4 4',
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
  fetch: 'M8 2v1.8M8 5.2v1.8M8 8.4v1.6M4.5 9.5L8 13l3.5-3.5',   // dashed shaft, no underline (as Visual Studio)
  pull: 'M8 2.5V13M4.5 9.5L8 13l3.5-3.5',
  push: 'M3 2.5h10M8 14V6M4.5 9.5L8 6l3.5 3.5',   // bar at the top, arrow below it
  pr: 'M4 4.5a1.5 1.5 0 1 0 0-.01M4 6v4.5M4 13a1.5 1.5 0 1 0 0-.01M12 13a1.5 1.5 0 1 0 0-.01M12 11.5V6.5A2 2 0 0 0 10 4.5H7M8.5 3 7 4.5 8.5 6',
  checkOn: 'M2.5 2.5h11v11h-11zM5 8.2l2 2 4-4.4',
  checkOff: 'M2.5 2.5h11v11h-11z',
  chat: 'M2.5 3h11v7.5H7.5l-3 2.5v-2.5h-2z',
  bulb: 'M6 12.5h4M6.5 14.5h3M8 1.5a4.5 4.5 0 0 0-2.6 8.2c.4.3.6.7.6 1.2v.6h4v-.6c0-.5.2-.9.6-1.2A4.5 4.5 0 0 0 8 1.5z',
  gift: 'M2.5 6h11v2.5h-11zM3.5 8.5h9v6h-9zM8 6v8.5M8 6C6.5 3 4 3.5 4.5 5c.3.8 2 1 3.5 1zM8 6c1.5-3 4-2.5 3.5-1-.3.8-2 1-3.5 1z',
  info: 'M8 1.5a6.5 6.5 0 1 0 0 13 6.5 6.5 0 1 0 0-13M8 7v4.5M8 4.8h.01',
  sparkle: 'M7 2l1.2 3.3L11.5 6.5 8.2 7.7 7 11 5.8 7.7 2.5 6.5l3.3-1.2zM12 9.5l.6 1.4 1.4.6-1.4.6-.6 1.4-.6-1.4-1.4-.6 1.4-.6z',
};

function icon(name, cls = '') {
  // The theme icon is a painter's palette: an outline with a thumb hole and filled paint dots (the only icon mixing
  // stroke and fill).
  if (name === 'theme') {
    return `<svg class="ic ${cls}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"><path d="M8 1.9C4.5 1.9 1.8 4.6 1.8 8s2.7 6.1 6 6.1c.9 0 1.5-.6 1.5-1.3 0-.4-.2-.7-.4-1-.2-.3-.4-.6-.4-1 0-.8.6-1.3 1.4-1.3h1.7c1.9 0 3-1.3 3-3.1C14.6 4.4 11.6 1.9 8 1.9z"/><g fill="currentColor" stroke="none"><circle cx="4.9" cy="7.6" r="1"/><circle cx="6.5" cy="4.9" r="1"/><circle cx="9.6" cy="4.7" r="1"/><circle cx="11.6" cy="7" r="1"/></g></svg>`;
  }
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
  if (s < 86400 * 7) return Math.floor(s / 86400) === 1 ? '1 day ago' : `${Math.floor(s / 86400)} days ago`;
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
    case 'accent': {
      accent.palette = m.palette;
      const before = themeName();
      theme.windowsLight = m.lightApps ?? null;
      applyTheme(before);
      break;
    }
    case 'reply': {
      const p = pending.get(m.reqId);
      if (!p) return;
      pending.delete(m.reqId);
      m.ok ? p.resolve(m.result) : p.reject(new Error(m.error));
      break;
    }
    case 'sessions': state.agents = m.agents || state.agents; syncSessions(m.sessions, m.projects || []); break;
    case 'output': onOutput(m.id, m.data); break;
    case 'exited': onExited(m.id, m.code); break;
    case 'git.status': onGitStatus(m); break;
    case 'panels': onPanels(m); break;
    case 'typePrompt': typePrompt(m.id, m.text); break;
    case 'newConversations': showNewConversations(m.ids); break;
  }
});

// ------------------------------------------------------------------ state

const state = {
  sessions: [],              // ordered session objects
  agents: [],                // the coding agents the host knows (AgentProfile.Describe): id, name, programs, choosesId, follows, …
  byId: new Map(),
  expandedId: null,
  projectViewOf: null,       // the project whose Project view is showing: a place of its own, with no session open
  currentId: store.get('currentId', null),   // session shown in the Git panel
  gitVisible: store.get('gitVisible', true),
  gitWidth: store.get('gitWidth', 400),
  panels: [],                // shown by Claude through the app's MCP server (their tabs live in the dock)
  panelsLoaded: false,       // until the host sends them, saved panel tabs keep their place
  fontSize: store.get('fontSize', 14),
  surface: { w: 0, h: 0 },
  projects: [],                                        // [{ id, name }] in display order
  hiddenProjects: new Set(store.get('hiddenProjects', [])),   // hidden rather than shown, so new projects start visible
};

// ------------------------------------------------------------------ projects: which sessions are visible

/** The "Unknown" group: sessions with no project, shown and hidden like a project of their own. */
const UNKNOWN = '__unknown__';
const UNKNOWN_NAME = 'Unknown';

const projectIndex = (s) => {
  const i = state.projects.findIndex((p) => p.id === s.project);
  return i < 0 ? state.projects.length : i;   // no project (or one that no longer exists) sorts last, as Unknown
};

/** The group a session is shown and counted under: its project, or Unknown. */
const groupOf = (s) => (projectIndex(s) === state.projects.length ? UNKNOWN : s.project);

// With no projects the bar has nothing to untick (Unknown isn't shown), so nothing may be hidden either.
const isVisible = (s) => state.projects.length === 0 || !state.hiddenProjects.has(groupOf(s));

/** Every toggleable group id, Unknown last. */
const allGroups = () => [...state.projects.map((p) => p.id), UNKNOWN];

const projectOf = (s) => state.projects.find((p) => p.id === s.project) || null;

const PROJECT_COLORS = ['#4f9cf5', '#e8a23b', '#5cc48a', '#d16fd6', '#e5675f', '#47c1c9', '#c9b641', '#9b8cf0'];

/** The automatic colour, from the id rather than the position, so reordering or renaming never changes it. */
function autoProjectColor(id) {
  let hash = 0;
  for (const c of id) hash = (hash * 31 + c.charCodeAt(0)) >>> 0;
  return PROJECT_COLORS[hash % PROJECT_COLORS.length];
}

/** The project's chosen colour (the host only ever sends #rrggbb), or its automatic one. */
const projectColor = (p) => p.color || autoProjectColor(p.id);

/** A coloured tag naming the session's project; sessions in Unknown have none. */
function projectTag(s) {
  const p = projectOf(s);
  if (!p) return null;
  return h('span', { class: 'proj-tag', style: `--proj:${projectColor(p)}`, title: `Project: ${p.name}` }, p.name);
}

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
    ? 'Tick a project above (Unknown holds sessions with no project), or choose All.'
    : 'Add a session for each worktree you want Claude to work in.';
  layout(instant);
  for (const s of state.sessions) renderTileHeader(s);
  renderProjectBar();
  renderSessionTabs();
  syncDocTabs();   // the current session, and so the project whose tabs are shown, may have changed
  renderSessionPicker();
  renderGit();
}

function showOnlyProject(id) {
  state.hiddenProjects = new Set(allGroups().filter((g) => g !== id));
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

/** Hides every project, Unknown included. */
function hideAllProjects() {
  state.hiddenProjects = new Set(allGroups());
  saveHiddenProjects();
  applyProjectFilter();
}

/**
 * The project row: per project a checkbox (show or hide it alongside the others) and its name (show only it), with a
 * count of its sessions waiting for you and a dot while any of them is asking you something or working, so a hidden
 * project still gets noticed.
 */
function renderProjectBar() {
  const bar = $('#projectTabs');   // the grid button sits before it in the same row and is left alone
  // With no projects there is nothing to toggle, so Unknown only appears once a project exists.
  const groups = state.projects.length
    ? [...state.projects, { id: UNKNOWN, name: UNKNOWN_NAME, unknown: true }]
    : [];
  const tabs = groups.map((p) => {
    const members = state.sessions.filter((s) => groupOf(s) === p.id);
    const waiting = members.filter(waitingForYou).length;
    const shown = !state.hiddenProjects.has(p.id);
    const what = p.unknown ? 'sessions with no project' : p.name;
    return h('div', {
      class: 'project-tab' + (shown ? ' shown' : '') + (p.unknown ? ' unknown' : ''),
      style: p.unknown ? null : `--proj:${projectColor(p)}`,
      'data-id': p.id,
      oncontextmenu: (e) => { e.preventDefault(); projectMenu(p, e.clientX, e.clientY); },
    },
    h('button', {
      class: 'project-check', role: 'checkbox', 'aria-checked': String(shown),
      title: `${shown ? 'Hide' : 'Show'} ${what}, keeping the others as they are`,
      onclick: () => toggleProject(p.id),
    }, iconEl(shown ? 'checkOn' : 'checkOff')),
    // The name toggles like the checkbox; Ctrl+click (or the right-click menu) shows only this project.
    h('button', {
      class: 'project-name',
      title: `${shown ? 'Hide' : 'Show'} ${what} · Ctrl+click to show only this\n${members.length} session${members.length === 1 ? '' : 's'} · right-click for options`,
      onclick: (e) => (e.ctrlKey ? showOnlyProject(p.id) : toggleProject(p.id)),
    }, p.name,
    projectDot(members),
    waiting ? h('span', { class: 'badge', title: `${waiting} session${waiting === 1 ? '' : 's'} waiting for you` }, String(waiting)) : null));
  });
  const allShown = groups.length > 0 && groups.every((g) => !state.hiddenProjects.has(g.id));
  const noneShown = groups.length > 0 && groups.every((g) => state.hiddenProjects.has(g.id));
  fill(bar,
    h('span', { class: 'project-bar-label' }, 'Projects'),
    ...tabs,
    groups.length ? h('button', { class: 'project-all' + (allShown ? ' active' : ''), title: 'Show every project, Unknown included', onclick: showAllProjects }, 'All') : null,
    groups.length ? h('button', { class: 'project-all' + (noneShown ? ' active' : ''), title: 'Hide every project, Unknown included', onclick: hideAllProjects }, 'None') : null,
    h('button', { class: 'tb-btn text project-add', title: 'New project', onclick: showNewProjectDialog }, iconEl('plus'),
      state.projects.length ? 'Project' : 'New project (group sessions)'),
  );
  syncStatus(bar);
}

function projectMenu(p, x, y) {
  if (p.unknown) {   // a group, not a project: nothing to rename, move or delete
    showMenu(x, y, [
      { label: 'Project view: its worktrees side by side', action: () => openProjectView(UNKNOWN) },
      '-',
      { label: 'Show only sessions with no project', action: () => showOnlyProject(UNKNOWN) },
      { label: state.hiddenProjects.has(UNKNOWN) ? 'Show alongside the others' : 'Hide', action: () => toggleProject(UNKNOWN) },
      ...pluginMenuItems('project', { project: null }),
    ]);
    return;
  }
  const i = state.projects.indexOf(p);
  showMenu(x, y, [
    { label: 'Project view: its worktrees side by side', action: () => openProjectView(p.id) },
    '-',
    { label: 'Show only this project', action: () => showOnlyProject(p.id) },
    { label: state.hiddenProjects.has(p.id) ? 'Show alongside the others' : 'Hide', action: () => toggleProject(p.id) },
    '-',
    { label: 'Edit project (name, colour)…', action: () => showEditProjectDialog(p) },
    i > 0 && { label: 'Move left', action: () => moveProject(p, -1) },
    i < state.projects.length - 1 && { label: 'Move right', action: () => moveProject(p, 1) },
    '-',
    { label: 'Delete project…', danger: true, action: () => confirmDeleteProject(p) },
    ...pluginMenuItems('project', { project: p.name }),
  ]);
}

function moveProject(p, delta) {
  const ids = state.projects.map((x) => x.id);
  const i = ids.indexOf(p.id), j = i + delta;
  if (j < 0 || j >= ids.length) return;
  [ids[i], ids[j]] = [ids[j], ids[i]];
  request('projects.reorder', { ids }).catch((e) => toast(e.message, true));
}

/**
 * Name and colour for a new or existing project. The colour is null for "Automatic" (derived from the id; for a project
 * not created yet, previewed with the first palette colour) or a #rrggbb picked from the swatches or the custom picker.
 */
function projectDialog(title, initial, okLabel, onOk) {
  const name = h('input', { value: initial.name, placeholder: 'e.g. FiscFlow', spellcheck: 'false', maxlength: '60' });
  const error = h('div', { class: 'form-error', hidden: true });
  let color = initial.color ?? null;
  const automatic = initial.id ? autoProjectColor(initial.id) : PROJECT_COLORS[0];

  const preview = h('span', { class: 'proj-tag' });
  const custom = h('input', { type: 'color', class: 'swatch-custom-input', tabindex: '-1' });
  const customBtn = h('button', { class: 'swatch swatch-custom', title: 'Custom colour…', onclick: () => custom.click() }, '…');
  const autoBtn = h('button', { class: 'swatch-auto', title: 'Use the automatic colour', onclick: () => pick(null) }, 'Automatic');
  const swatches = PROJECT_COLORS.map((c) => h('button', { class: 'swatch', style: `--swatch:${c}`, title: c, onclick: () => pick(c) }));
  custom.addEventListener('input', () => pick(custom.value));

  function pick(c) {
    color = c;
    const shown = c ?? automatic;
    for (const s of swatches) s.classList.toggle('selected', c !== null && s.title === c);
    const isCustom = c !== null && !PROJECT_COLORS.includes(c);
    customBtn.classList.toggle('selected', isCustom);
    customBtn.style.setProperty('--swatch', isCustom ? c : 'transparent');
    autoBtn.classList.toggle('selected', c === null);
    custom.value = shown;
    preview.style.setProperty('--proj', shown);
    preview.textContent = name.value.trim() || 'Project';
  }
  name.addEventListener('input', () => pick(color));

  const ok = async () => {
    if (!name.value.trim()) { error.hidden = false; error.textContent = 'Enter a name.'; return; }
    try { await onOk(name.value.trim(), color); closeModal(); } catch (e) { error.hidden = false; error.textContent = e.message; }
  };
  name.addEventListener('keydown', (e) => { if (e.key === 'Enter') ok(); });
  openModal(title, [
    field('Name', name),
    field('Colour', h('div', { class: 'swatch-row' }, autoBtn, swatches, customBtn, custom, preview)),
    error,
  ], [h('button', { class: 'btn', onclick: closeModal }, 'Cancel'), h('button', { class: 'btn primary', onclick: ok }, okLabel)]);
  pick(color);
}

function showNewProjectDialog() {
  projectDialog('New project', { name: '', color: null }, 'Create', async (name, color) => {
    await request('projects.add', { name, color });
    if (!state.sessions.length) return;
    toast(`Project "${name}" created. Put sessions in it with right-click → Move to project…, or pick it when adding a session.`);
  });
}

function showEditProjectDialog(p) {
  projectDialog('Edit project', p, 'Save', (name, color) => request('projects.rename', { id: p.id, name, color: color ?? '' }));
}

function confirmDeleteProject(p) {
  const n = state.sessions.filter((s) => s.project === p.id).length;
  confirmDialog('Delete project',
    `Delete the project "${p.name}"?` + (n ? `\nIts ${n} session${n === 1 ? '' : 's'} will be kept and have no project, so ${n === 1 ? 'it shows' : 'they show'} with every project.` : ''),
    'Delete', true, () => request('projects.remove', { id: p.id }).catch((e) => toast(e.message, true)));
}

const PAD = 8, GAP = 8, HEADER_H = 30;
const RAIL_W = 16;   // the conversation rail's gutter at the right of each terminal
const DEFAULT_COMMAND = 'claude';   // each new session gets a conversation of its own (the host gives it one)
const MAX_WEBGL = 12;

// ------------------------------------------------------------------ theme
// System (follows Windows' app mode), Light, Dark, or a theme: a JSON file of colours, shipped in wwwroot/themes
// (Nerds) or added to the themes folder (see ThemeCatalog). The host reports Windows' app mode with the accent colour;
// until then the browser's own reading of it stands in. A theme is light or dark underneath (its base): that fills in
// every colour it leaves out, and is all Claude and plugins are told.

const theme = { pref: store.get('theme', 'system'), windowsLight: null };
// The themes the host found, and why any couldn't be loaded. The theme in use is also kept here, so it paints at once
// on the next start rather than once the host has listed the themes.
const themes = { list: [], problems: [], folder: null, names: null, last: store.get('themeLook', null) };
const BUILT_IN_THEMES = ['system', 'light', 'dark'];
/** The theme file in use, or null for System, Light and Dark. */
const currentTheme = () => BUILT_IN_THEMES.includes(theme.pref) ? null
  : themes.list.find((t) => t.id === theme.pref) ?? (themes.last?.id === theme.pref ? themes.last : null);
const isLight = () => {
  const t = currentTheme();
  if (t) return t.base === 'light';
  return theme.pref === 'light' || (theme.pref !== 'dark' && (theme.windowsLight ?? matchMedia('(prefers-color-scheme: light)').matches));
};
const themeName = () => (isLight() ? 'light' : 'dark');

/**
 * What the theme in use paints with, or null for System, Light and Dark: its own colours, over those built from the
 * accent colour for a monochrome theme (see monochromeLook). Until the host has listed the themes, the look kept from
 * last time stands in.
 */
function themeLook() {
  const t = currentTheme();
  if (!t) return null;
  if (!themes.list.includes(t)) return t;   // the kept look
  const made = t.monochrome ? monochromeLook(accentColour(), t.base === 'light') : {};
  return {
    id: t.id, name: t.name, base: t.base, font: t.font, glow: t.glow, monochrome: t.monochrome,
    colors: { ...made.colors, ...t.colors }, terminal: { ...made.terminal, ...t.terminal }, diff: { ...made.diff, ...t.diff },
    accent: t.accent ?? made.accent ?? null,
  };
}

let themeColors = [];   // the colours the theme in use set on the page, to take off again
/** Marks the page with the theme: data-theme light or dark for the stylesheet, and a theme's own colours, font and glow. */
function markTheme(look = themeLook()) {
  const root = document.documentElement;
  root.dataset.theme = themeName();
  for (const name of themeColors) root.style.removeProperty(name);
  themeColors = Object.entries(look?.colors ?? {}).map(([name, value]) => { root.style.setProperty('--' + name, value); return '--' + name; });
  if (look?.font) root.style.fontFamily = `"${look.font}", "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif`;
  else root.style.removeProperty('font-family');
  if (look?.glow) root.dataset.glow = '';
  else delete root.dataset.glow;
}
markTheme(themes.last?.id === theme.pref ? themes.last : null);   // the accent isn't known yet: last time's look

/** Reads the themes again (one added to the folder shows without restarting), and puts the one in use on the page. */
async function loadThemes() {
  try {
    const r = await request('themes.list');
    const before = themeName();
    Object.assign(themes, { list: r.themes, problems: r.problems, folder: r.folder, names: r.names });
    // A theme that's gone (its file deleted, or no longer valid) gives way to System.
    if (!currentTheme() && !BUILT_IN_THEMES.includes(theme.pref)) theme.pref = 'system';
    applyTheme(before);
  }
  catch (e) { console.error('The themes could not be read', e); }
}

/** The accent colour picked in Accent colour, as #rrggbb. */
function accentColour() {
  const c = accent.choice;
  return c === 'windows' ? accent.palette?.[3] ?? '#7160e8' : /^#[0-9a-f]{6}$/i.test(c) ? c : '#7160e8';
}

/** A colour's hue (0–360), saturation and lightness (0–100). */
function hexToHsl(hex) {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2, d = max - min;
  if (!d) return [0, 0, l * 100];
  const s = d / (1 - Math.abs(2 * l - 1));
  const h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [(h * 60 + 360) % 360, s * 100, l * 100];
}

/** #rrggbb for a hue, saturation and lightness (the diff editor only takes hex). */
function hslHex(h, s, l) {
  s = Math.min(100, Math.max(0, s)) / 100;
  l = Math.min(100, Math.max(0, l)) / 100;
  const k = (n) => (n + h / 30) % 12, a = s * Math.min(l, 1 - l);
  const f = (n) => Math.round((l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)))) * 255);
  return '#' + [0, 8, 4].map((n) => f(n).toString(16).padStart(2, '0')).join('');
}

/**
 * Every colour of a monochrome theme, in shades of one hue: backgrounds near black (near white on a light base), text and
 * highlights lighter. What needs telling apart (status, Git's changes, the terminal's sixteen colours, added and removed
 * lines) differs in how light and how strong its shade is rather than in hue.
 */
function monochromeLook(hex, light) {
  const [h, sAccent] = hexToHsl(hex);
  // A grey accent makes a grey theme; any other is held to a tint that's neither washed out nor loud.
  const sat = sAccent < 8 ? 0 : Math.min(90, Math.max(35, sAccent));
  const L = (l) => (light ? 100 - l : l);
  const bg = (l) => hslHex(h, sat * 0.5, L(l));      // backgrounds and borders: muted
  const chrome = bg(4);
  // Text and marks are picked by how well they read on the chrome, not by lightness, which reads very differently from
  // one hue to the next (a green is far brighter than a blue of the same lightness). Each level is a lightness on the
  // scale the colours below are written in, matched to a contrast ratio.
  const LEVELS = [[30, 2], [36, 3], [42, 3.6], [46, 4.2], [55, 5.8], [62, 7.5], [70, 9.5], [78, 11.5], [85, 13.5], [93, 16], [95, 17]];
  const ratioFor = (l) => {
    const i = LEVELS.findIndex(([at]) => at >= l);
    if (i <= 0) return LEVELS[Math.max(i, 0)][1];
    const [[l0, r0], [l1, r1]] = [LEVELS[i - 1], LEVELS[i]];
    return r0 + (r1 - r0) * (l - l0) / (l1 - l0);
  };
  const tone = (l, s) => {
    const want = ratioFor(l);
    let lo = 0, hi = 100;   // lightness, searched away from the chrome until the shade reads as well as wanted
    for (let n = 0; n < 16; n++) {
      const mid = (lo + hi) / 2;
      const enough = contrast(hslHex(h, s, light ? 100 - mid : mid), chrome) >= want;
      if (enough) hi = mid; else lo = mid;
    }
    return hslHex(h, s, light ? 100 - hi : hi);
  };
  const fg = (l) => tone(l, sat);           // text and marks
  const pale = (l) => tone(l, sat * 0.2);   // what's taken away (removed lines, errors), nearly grey
  // The accent: a bright shade that reads on the chrome for lines, and a deep one white text reads on for fills.
  const lineL = 62;
  let strongL = 48;
  while (contrast(hslHex(h, sat, strongL), '#ffffff') < 4.5 && strongL > 5) strongL -= 2;
  return {
    accent: { line: fg(lineL), strong: hslHex(h, sat, strongL) },
    colors: {
      chrome, 'chrome-2': bg(2.6), 'chrome-3': bg(1.6), panel: bg(5.5), 'panel-2': bg(7.5), hover: bg(10), 'hover-strong': bg(14),
      border: bg(20), 'border-soft': bg(13), 'border-strong': bg(32),
      text: fg(78), 'text-dim': fg(55), 'text-faint': fg(36), 'text-strong': fg(93),
      focus: fg(lineL), select: bg(18), 'select-inactive': bg(11), 'terminal-bg': bg(2.2), 'editor-bg': bg(3),
      ok: fg(66), working: fg(72), attention: fg(84), asking: fg(95), danger: pale(70), 'danger-fill': hslHex(h, sat * 0.3, strongL),
      'st-m': fg(80), 'st-a': fg(66), 'st-d': pale(52), 'st-r': fg(88), 'st-u': fg(58),
      'info-bg': bg(9), 'info-border': bg(26), 'error-bg': bg(8), 'error-border': bg(30), 'pv-warn-bg': bg(10), 'pv-warn-border': bg(28),
      'ico-folder': fg(70), 'ico-file': fg(52), graph: fg(55),
      'ref-bg': bg(16), 'ref-fg': fg(85), 'ref-head-bg': bg(24), 'ref-head-fg': fg(93), 'ref-tag-bg': bg(12), 'ref-tag-fg': fg(75),
      'scroll-thumb': bg(20), 'scroll-thumb-hover': bg(30), 'tab-border-shown': bg(30), 'tab-border-hover': bg(40),
      tick: bg(28), 'tick-active': fg(75), 'tick-hot': fg(95), 'card-bg': bg(6), 'card-border': bg(22), bubble: bg(10),
      'code-fg': fg(82), 'code-bg': bg(1.6), well: bg(3), 'table-head': bg(9), link: fg(72), 'update-fg': fg(93),
    },
    terminal: {
      background: bg(2.2), foreground: fg(70), cursor: fg(88), cursorAccent: bg(2.2),
      selectionBackground: bg(24), selectionInactiveBackground: bg(14),
      black: bg(8), red: pale(56), green: fg(62), yellow: fg(78), blue: fg(46), magenta: fg(56), cyan: fg(70), white: fg(80),
      brightBlack: fg(36), brightRed: pale(72), brightGreen: fg(74), brightYellow: fg(88), brightBlue: fg(60), brightMagenta: fg(68), brightCyan: fg(82), brightWhite: fg(95),
      overviewRulerBorder: bg(2.2),
      scrollbarSliderBackground: bg(30) + '66', scrollbarSliderHoverBackground: bg(40) + '88', scrollbarSliderActiveBackground: fg(60) + '66',
    },
    diff: {
      background: bg(3), foreground: fg(70), lineNumber: fg(34), lineNumberActive: fg(74), selection: bg(24), cursor: fg(88),
      lineHighlightBorder: bg(9),
      insertedLine: fg(40) + '33', insertedText: fg(55) + '66', removedLine: pale(30) + '55', removedText: pale(45) + '77',
      insertedGutter: fg(40) + '66', removedGutter: pale(30) + '88', insertedOverview: fg(66) + 'cc', removedOverview: pale(50) + 'cc',
      diagonalFill: bg(16) + '80', scrollbar: bg(30) + '66',
      comment: fg(42), keyword: fg(93), string: fg(76), number: fg(85), type: fg(88),
    },
  };
}

/** The accent changed: a monochrome theme is built from it, so everything is recoloured; otherwise only the accent. */
const accentChanged = () => (currentTheme()?.monochrome ? applyTheme() : applyAccent());

// How the status dots move (see app.css): picked in Status dots… from the theme menu, and kept per person.
const DOT_STYLES = [
  { id: 'glow', name: 'Glow', about: 'The dots themselves light up: three dots bounce while working, waiting beats and glows, asking hops and glows.' },
  { id: 'classic', name: 'Classic', about: 'Rings round the dots: a comet spins while working, waiting pings in a ring, and an asking session’s tab lights up pink.' },
  { id: 'calm', name: 'Calm', about: 'A different shape for each state, with only slow, small motion: a ring turning, a dot fading in a ring, a pink diamond.' },
  { id: 'signal', name: 'Signal', about: 'Bold, like a device’s status light: a ring filling while working, a blinking square, a dot that shakes for your answer.' },
];
const dotStyle = { id: DOT_STYLES.some((d) => d.id === store.get('dotStyle')) ? store.get('dotStyle') : 'glow' };
const applyDotStyle = () => { $('#app').dataset.dots = dotStyle.id; };
applyDotStyle();

const TERM_THEME_DARK = {
  background: '#181818', foreground: '#cccccc', cursor: '#d4d4d4', cursorAccent: '#181818',
  selectionBackground: '#264f78', selectionInactiveBackground: '#3a3d41',
  black: '#0c0c0c', red: '#c50f1f', green: '#13a10e', yellow: '#c19c00', blue: '#3b78ff', magenta: '#881798', cyan: '#3a96dd', white: '#cccccc',
  brightBlack: '#767676', brightRed: '#e74856', brightGreen: '#16c60c', brightYellow: '#f9f1a5', brightBlue: '#3b78ff', brightMagenta: '#b4009e', brightCyan: '#61d6d6', brightWhite: '#f2f2f2',
};
// Light terminals, after Visual Studio Code's light terminal palette (Claude gets its light theme to match).
const TERM_THEME_LIGHT = {
  background: '#ffffff', foreground: '#1e1e1e', cursor: '#1e1e1e', cursorAccent: '#ffffff',
  selectionBackground: '#add6ff', selectionInactiveBackground: '#e5ebf1',
  black: '#000000', red: '#cd3131', green: '#107c10', yellow: '#949800', blue: '#0451a5', magenta: '#bc05bc', cyan: '#0598bc', white: '#555555',
  brightBlack: '#666666', brightRed: '#cd3131', brightGreen: '#14ce14', brightYellow: '#b5ba00', brightBlue: '#0451a5', brightMagenta: '#bc05bc', brightCyan: '#0598bc', brightWhite: '#a5a5a5',
  // xterm's own scrollbar and its border default to dark-theme greys.
  overviewRulerBorder: '#ffffff',
  scrollbarSliderBackground: '#64646433', scrollbarSliderHoverBackground: '#64646466', scrollbarSliderActiveBackground: '#00000066',
};
/** The terminals' colours: a theme's own over its base's. */
const termTheme = (look = themeLook()) => ({ ...(isLight() ? TERM_THEME_LIGHT : TERM_THEME_DARK), ...look?.terminal });

/** The diff editor's theme: a theme's, defined from its colours over its base's, or Light's or Dark's. */
/** A theme's diff colours (ThemeCatalog.DiffNames) and the diff editor's names for them; the syntax colours are rules. */
const DIFF_COLOR_IDS = {
  background: 'editor.background', foreground: 'editor.foreground',
  lineNumber: 'editorLineNumber.foreground', lineNumberActive: 'editorLineNumber.activeForeground',
  selection: 'editor.selectionBackground', cursor: 'editorCursor.foreground', lineHighlightBorder: 'editor.lineHighlightBorder',
  insertedLine: 'diffEditor.insertedLineBackground', insertedText: 'diffEditor.insertedTextBackground',
  removedLine: 'diffEditor.removedLineBackground', removedText: 'diffEditor.removedTextBackground',
  insertedGutter: 'diffEditorGutter.insertedLineBackground', removedGutter: 'diffEditorGutter.removedLineBackground',
  insertedOverview: 'diffEditorOverview.insertedForeground', removedOverview: 'diffEditorOverview.removedForeground',
  diagonalFill: 'diffEditor.diagonalFill', scrollbar: 'scrollbarSlider.background',
};

function diffTheme(look = themeLook()) {
  const t = look;
  const base = isLight() ? 'vs2026-light' : 'vs2026-dark';
  if (!t || !window.monaco) return base;
  const d = t.diff ?? {};
  const colors = Object.fromEntries(Object.entries(DIFF_COLOR_IDS).filter(([name]) => d[name]).map(([name, id]) => [id, d[name]]));
  // Syntax colours are rules, which take the colour without its #.
  const rules = [['', d.foreground], ['comment', d.comment], ['keyword', d.keyword], ['string', d.string], ['number', d.number], ['type', d.type]]
    .filter(([, v]) => v).map(([token, v]) => ({ token, foreground: v.slice(1, 7) }));
  const name = 'theme-' + t.id;
  monaco.editor.defineTheme(name, { base: isLight() ? 'vs' : 'vs-dark', inherit: true, rules, colors: { ...MONACO_COLORS[base], ...colors } });
  return name;
}

// ------------------------------------------------------------------ sessions & tiles

function syncSessions(list, projects) {
  state.projects = projects;
  // Forget hidden projects that no longer exist, so a recreated project with the same id can't start hidden.
  const known = new Set([...projects.map((p) => p.id), UNKNOWN]);
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
      Object.assign(s, { name: info.name, directory: info.directory, command: info.command, conversation: info.conversation });
    }
    s.project = info.project || null;
    s.running = info.running || s.running;
  }
  for (const s of [...state.byId.values()]) {
    if (!seen.has(s.id)) disposeSession(s);
  }
  state.sessions = list.map((i) => state.byId.get(i.id));
  // Move only the tiles that are out of order: moving a tile takes the keyboard focus from its terminal, and the list
  // comes again whenever a session changes (the conversation Claude is in, say). If one has to move, focus goes back.
  const grid = $('#grid');
  const focused = document.activeElement;
  state.sessions.forEach((s, i) => { if (grid.children[i] !== s.tile) grid.insertBefore(s.tile, grid.children[i] ?? null); });
  if (focused && focused !== document.activeElement && focused.isConnected) focused.focus({ preventScroll: true });

  if (!state.byId.has(state.currentId)) state.currentId = state.sessions[0]?.id ?? null;
  if (state.expandedId && !state.byId.has(state.expandedId)) state.expandedId = null;

  layout(true);
  // Claude sessions brought back with the app start when they're first opened, not all at once: Claude's start-up
  // context (hooks, the date, Git status) is then fresh when you use it, and a session nobody opens runs nothing. A new
  // session, and any other command (a dev server, say), starts straight away, visible or not.
  const restoring = !state.restored;
  state.restored = true;
  for (const s of state.sessions) {
    if (s.running || s.started || s.startsOnOpen) continue;
    if (restoring && agentOf(s.command)) { s.startsOnOpen = true; renderTileHeader(s); }
    else startSession(s);
  }
  applyProjectFilter(true);
  // A Project view whose project has just been deleted goes back to all sessions.
  if (state.projectViewOf != null && !allGroups().includes(state.projectViewOf)) showTerminalDoc();
  projectViewChanged();
}

function createSession(info) {
  const s = {
    ...info, running: false, started: false, exitCode: null, title: '', git: null,
    attention: false, asking: false, act: { lastOut: 0, lastIn: 0, burstStart: 0, state: 'idle', workingSince: 0, drawnAt: 0, readAt: 0 },
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
  s.header.addEventListener('mouseenter', () => loadConversationInfo(s));
  s.rail = h('div', { class: 'chat-rail' });
  attachRail(s.rail);
  s.tile = h('div', { class: 'tile', 'data-id': s.id }, s.header, h('div', { class: 'tile-body' }, s.surface, s.rail, s.cover, s.exitedBadge));

  const term = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Courier New", monospace',
    fontSize: state.fontSize,
    lineHeight: 1.0,
    cursorBlink: true,
    scrollback: 10000,
    allowProposedApi: true,
    theme: termTheme(),
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
      if (data === '\r') (s.startsOnOpen ? startSession(s) : restartSession(s));
      return;
    }
    s.act.lastIn = performance.now();
    post({ type: 'input', id: s.id, data });
  });
  term.onResize(({ cols, rows }) => { if (s.running) post({ type: 'resize', id: s.id, cols, rows }); });
  term.onTitleChange((t) => { s.title = t; renderTileHeader(s); });
  // Some agents (Codex) report what they're doing in an OSC 7501 sequence, "state=working:app=codex", with state=blocked
  // while they wait on an answer from you. A session that reports it is taken at its word rather than read off the screen.
  term.parser.registerOscHandler(7501, (data) => {
    const reported = /(?:^|:)state=([a-z]+)/.exec(data)?.[1];
    if (reported) { s.reportsState = true; setAsking(s, reported === 'blocked'); }
    return true;
  });
  term.onBell(() => { if (state.expandedId !== s.id) setAttention(s, true); });
  // The right button only opens the menu. Left to xterm, a right press is reported to Claude when it tracks the mouse,
  // and Claude pastes on right-click, so the clipboard was pasted as the menu opened.
  for (const type of ['mousedown', 'mouseup', 'mousemove']) {
    s.surface.addEventListener(type, (e) => {
      if (e.button === 2 || (e.buttons & 2)) e.stopPropagation();
    }, true);
  }
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

/** The coding agent a command runs (one of state.agents, as the host's Agents.For), or null for any other command. */
const agentOf = (command) => {
  const program = (command || '').trim().split(' ')[0].toLowerCase();
  return state.agents.find((a) => a.programs.includes(program)) ?? null;
};
const agentById = (id) => state.agents.find((a) => a.id === id) ?? null;

/** Whether the command has an option an agent's pattern (a regex source from the host) describes. */
const hasOption = (pattern, command) => !!pattern && new RegExp(pattern, 'i').test(' ' + (command || '').trim());

function startSession(s) {
  s.started = true;
  s.startsOnOpen = false;
  fitTerm(s);
  s.startedTheme = themeName();
  request('start', { id: s.id, cols: s.term.cols, rows: s.term.rows, theme: s.startedTheme })
    .then(() => { s.running = true; s.exitCode = null; renderTileHeader(s); renderSessionTabs(); })
    .catch((err) => {
      s.running = false;
      s.term.writeln(`\x1b[31mFailed to start "${s.command}" in ${s.directory}\x1b[0m`);
      s.term.writeln(`\x1b[90m${err.message}\x1b[0m`);
      s.term.writeln('\x1b[90mPress Enter to retry.\x1b[0m');
      renderTileHeader(s);
    });
}

/** resume: carry on in the exact conversation the session is in (used when restarting for a new theme). */
function restartSession(s, { resume = false } = {}) {
  s.started = true;
  s.startsOnOpen = false;
  fitTerm(s);
  s.term.reset();
  setAsking(s, false);   // the question went with the old screen (and a failed restart writes no output to clear it)
  s.startedTheme = themeName();
  request('restart', { id: s.id, cols: s.term.cols, rows: s.term.rows, theme: s.startedTheme, resume })
    .then(() => { s.running = true; s.exitCode = null; renderTileHeader(s); renderSessionTabs(); })
    .catch((err) => { s.term.writeln(`\x1b[31m${err.message}\x1b[0m`); });
}

function onOutput(id, data) {
  const s = state.byId.get(id);
  if (!s) return;
  // xterm draws written output later, in chunks; the screen is only read for "asking" once this output is on it.
  s.term.write(data, () => { s.act.drawnAt = performance.now(); });
  noteActivity(s);
}

function onExited(id, code) {
  const s = state.byId.get(id);
  if (!s) return;
  s.running = false;
  s.exitCode = code;
  s.act.state = 'idle';
  s.asking = false;
  s.reportsState = false;   // the next start may run another command
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
      // Working again means it's no longer waiting for you, e.g. another session handed it a task or you answered its
      // question. "Waiting" takes priority in statusClass, so without this the dot stayed yellow until the session was
      // opened. A session that reports its state says when it stops asking (Codex blinks its title while it asks).
      const asked = s.asking && !s.reportsState;
      if (asked) s.asking = false;
      if (s.attention) setAttention(s, false);
      else { renderTileState(s); if (asked) renderDocTabs(); }
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
    // Once the screen has settled after new output has been drawn (mid-redraw it can be half drawn), see whether the
    // agent is asking. Only agents: a shell or a dev server can print text that looks like their menus.
    const asks = s.running && !s.reportsState && a.drawnAt !== a.readAt && now - a.drawnAt > 250 && askingScreen(s.command);
    if (asks) {
      a.readAt = a.drawnAt;
      setAsking(s, isAsking(s.term, asks));
    }
  }
}, 500);

function setAttention(s, on) {
  if (s.attention === on) return;
  s.attention = on;
  renderTileState(s);
  renderDocTabs();
}

function setAsking(s, on) {
  if (s.asking === on) return;
  s.asking = on;
  renderTileState(s);
  renderDocTabs();
}

/** How a command's agent shows on screen that it's asking (its profile's asks, as regexes), or null. */
function askingScreen(command) {
  const agent = agentOf(command);
  if (!agent?.asks) return null;
  const { choice, promptRule, promptLine } = agent.asks;
  return (agent.asksRe ??= {
    choice: new RegExp(choice),
    promptRule: promptRule && new RegExp(promptRule),
    promptLine: promptLine && new RegExp(promptLine),
  });
}

// An agent asks by putting a menu on screen, with a line its profile recognises (Claude's picked numbered choice, say).
// While its prompt box (a rule with the prompt under it) is there, it wants a prompt instead, so text like that in the
// conversation above doesn't count.
function isAsking(term, asks) {
  const b = term.buffer.active;
  let choice = false, prev = '';
  for (let y = b.baseY; y < b.baseY + term.rows; y++) {
    const line = b.getLine(y)?.translateToString(true) ?? '';
    if (asks.promptRule?.test(prev) && asks.promptLine?.test(line)) return false;
    if (asks.choice.test(line)) choice = true;
    prev = line;
  }
  return choice;
}

/** Whether a session counts in the "waiting for you" badges: done while you were away, or asking you something. */
function waitingForYou(s) {
  return s.attention || s.asking;
}

function statusClass(s) {
  if (!s.running) return 'exited';
  if (s.asking) return 'asking';
  if (s.attention) return 'attention';
  if (s.act.state === 'working') return 'working';
  return 'running';
}

function statusText(s) {
  if (s.startsOnOpen) return 'Starts when you open it';
  return { exited: 'Not running', asking: 'Needs your answer', attention: 'Waiting for you', working: 'Working…', running: 'Idle' }[statusClass(s)];
}

/** The dot's state, the one before it and when it began, which its animations go by (see syncStatus). */
function dotState(s) {
  const cls = statusClass(s);
  if (s.dotState?.cls !== cls) s.dotState = { cls, from: s.dotState?.cls ?? '', since: document.timeline.currentTime };
  return s.dotState;
}

function statusDot(s) {
  const d = dotState(s);
  return h('span', { class: 'dot ' + d.cls, 'data-from': d.from, 'data-since': d.since });
}

// Dots and tabs are rebuilt often (a tile header on every title change) and a rebuilt one's animation started over,
// which made it flicker. Endless animations run on the page's clock instead, so every dot of a state moves in step and
// a rebuilt one carries on; one-offs (the settle when a session is done) run from when the state began, so they play
// once.
function syncStatus(root) {
  const animated = '.dot, .tab-name, .session-tab';   // the tab for Classic's lit-up asking tab
  for (const el of [...(root.matches(animated) ? [root] : []), ...root.querySelectorAll(animated)]) {
    const since = Number(el.dataset.since) || 0;
    for (const a of el.getAnimations({ subtree: true })) {
      if (a.effect.target !== el) continue;   // an element inside it goes by its own
      a.startTime = a.effect.getTiming().iterations === Infinity ? 0 : since;
    }
  }
}

function renderTileState(s) {
  s.tile.classList.toggle('attention', s.attention);
  const d = dotState(s);
  s.tile.classList.toggle('asking', d.cls === 'asking');
  const tab = $(`.session-tab[data-id="${s.id}"]`);
  tab?.classList.toggle('working', d.cls === 'working');   // its name shimmers
  tab?.classList.toggle('asking', d.cls === 'asking');     // its name stands out
  for (const dot of [s.header.querySelector('.dot'), tab?.querySelector('.dot')]) {
    if (!dot) continue;
    dot.className = 'dot ' + d.cls;
    dot.dataset.from = d.from;
    dot.dataset.since = d.since;
    dot.title = statusText(s);
  }
  syncStatus(s.header);
  if (tab) syncStatus(tab);
  updateProjectIndicators(groupOf(s));
  projectViewChanged(s);
}

/** A project tab's dot: hopping while one of its sessions asks you something, else spinning while one works. */
function projectDot(members) {
  if (members.some((x) => x.running && x.asking)) return h('span', { class: 'dot asking', title: 'A session needs your answer' });
  if (members.some((x) => x.running && x.act.state === 'working')) return h('span', { class: 'dot working', title: 'A session is working' });
  return null;
}

/** Refreshes one project tab's dot and waiting badge in place (rebuilding the bar would drop keyboard focus). */
function updateProjectIndicators(projectId) {
  const name = document.querySelector(`.project-tab[data-id="${projectId}"] .project-name`);
  if (!name) return;
  const members = state.sessions.filter((x) => groupOf(x) === projectId);
  const waiting = members.filter(waitingForYou).length;
  // A dot in the same state stays, so its animation isn't restarted on every update.
  const dot = name.querySelector('.dot'), want = projectDot(members);
  if (!want) dot?.remove();
  else if (dot?.className !== want.className) { dot?.remove(); name.append(want); syncStatus(name); }
  name.querySelector('.badge')?.remove();
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
    h('span', { class: 'tile-name', title: conversationLine(s) }, s.name),
    projectTag(s),
    h('span', { class: 'tile-title', title: s.title }, s.title),
    gs && h('button', {
      class: 'chip', title: `${gs.count} changed file(s) on ${gs.branch} — show in Git Changes`,
      onclick: () => showGit(s.id),
    }, iconEl('branch'), gs.branch, gs.ahead ? ` ↑${gs.ahead}` : '', gs.count ? h('span', { class: 'mod' }, ` ●${gs.count}`) : ''),
    h('button', { class: 'icon-btn', title: 'Session options', onclick: (e) => { const r = e.currentTarget.getBoundingClientRect(); sessionMenu(s, r.left, r.bottom + 2); } }, iconEl('more')),
    expanded && h('button', { class: 'icon-btn', title: 'Back to all sessions (Shift+Esc)', onclick: collapse }, iconEl('collapse')),
  );
  s.exitedBadge.hidden = s.running || !(s.started || s.startsOnOpen);
  s.exitedBadge.textContent = s.startsOnOpen ? 'Starts when you open it' : s.exitCode === null ? 'Not running' : `Exited (${s.exitCode})`;
  renderTileState(s);
}

function sessionTabTitle(s) {
  return [s.directory, statusText(s), conversationLine(s), 'Click again to go back to all sessions · middle-click to close'].filter(Boolean).join('\n');
}

/**
 * What a Claude session's tooltips say about its own conversation, so one that isn't the conversation you expect shows
 * before you type into it. Null for other commands, and until it has been fetched (see loadConversationInfo).
 */
function conversationLine(s) {
  const agent = agentOf(s.command);
  if (!agent) return null;
  if (!s.conversation) {
    if (hasOption(agent.continue, s.command)) return `Conversation: the folder's latest, once ${agent.name} has picked it`;
    return agent.choosesId || hasOption(agent.ownConversation, s.command) ? null : `Conversation: a new one, once ${agent.name} has started it`;
  }
  const c = s.convInfo;
  if (c?.id !== s.conversation) return null;
  if (!c.saved) return 'Conversation: a new one, nothing in it yet';
  const started = c.started && new Date(c.started).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  return `Conversation: ${c.title}\n${started ? `Started ${started} · ` : ''}last used ${relTime(c.modified)}`;
}

/** Fetches the session's conversation for its tooltips when the pointer reaches its tab or tile header, so it's current. */
async function loadConversationInfo(s) {
  if (!agentOf(s.command) || !s.conversation) return;
  try { s.convInfo = await request('conversationInfo', { id: s.id }); } catch { return; }
  const tab = $(`.session-tab[data-id="${s.id}"]`);
  if (tab) tab.title = sessionTabTitle(s);
  s.header.querySelector('.tile-name')?.setAttribute('title', conversationLine(s) ?? '');
}

function renderSessionTabs() {
  const host = $('#sessionTabs');
  // The label and the close button are sibling buttons, since one button can't contain another: both stay reachable
  // with Tab, and Enter or Space on the label switches sessions. A click elsewhere on the tab counts as the label's.
  // Visible sessions only, grouped by project. Each group is outlined in its project's colour and named once at its
  // start, so the tabs themselves carry no project tag.
  const shown = visibleSessions();
  const tabs = shown.map((s, i) => h('div', {
    class: 'session-tab' + (state.expandedId === s.id ? ' active' : '') + (['working', 'asking'].includes(statusClass(s)) ? ' ' + statusClass(s) : ''),
    'data-id': s.id,
    title: sessionTabTitle(s),
    onmouseenter: () => loadConversationInfo(s),
    onclick: (e) => {
      if (e.target.closest('.tab-close')) return;
      // Clicking the session that's open (its terminal showing) goes back to the tiles; otherwise it opens the session,
      // or brings its terminal back in front of a diff or Conversation tab.
      if (state.expandedId === s.id && docs.active === 'terminal') collapse();
      else expand(s.id);
    },
    onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },   // no autoscroll cursor
    onauxclick: (e) => { if (e.button === 1) confirmRemove(s); },
    oncontextmenu: (e) => { e.preventDefault(); sessionMenu(s, e.clientX, e.clientY, 'sessionTab'); },
  }, h('button', { class: 'tab-label' },
    statusDot(s), h('span', { class: 'tab-name' }, s.name), i < 9 ? h('span', { class: 'key' }, `Ctrl+${i + 1}`) : null),
  // Closing a session tab removes the session, after the same confirmation as "Remove session…".
  h('button', { class: 'icon-btn small tab-close', title: 'Close session…', onclick: () => confirmRemove(s) }, iconEl('close'))));
  // A + at the end of each project's group (or of the row without projects): another session like one of that group's.
  const plus = (sessions, where) => {
    const like = sessions.find((s) => s.id === state.currentId) || sessions[0];
    return h('button', {
      class: 'icon-btn small session-add', 'aria-label': `New session ${where}`,
      title: `New session ${where}: in the same folder as ${like.name}${like.git?.isRepo ? ", in a new worktree" : ""}, or in a different folder`,
      onclick: (e) => quickAddMenu(like, e.currentTarget),
    }, iconEl('plus'));
  };
  if (!state.projects.length) {
    // Nothing to group by: the sessions are still outlined as one group, as Unknown's would be, but with no name.
    host.replaceChildren(...(shown.length ? [h('div', { class: 'session-group unknown unnamed' }, tabs, plus(shown, 'like these'))] : []));
  } else {
    const groups = [];
    shown.forEach((s, i) => {
      const id = groupOf(s);
      if (!groups.length || groups[groups.length - 1].id !== id) {
        const p = projectOf(s);
        groups.push({ id, name: p ? p.name : UNKNOWN_NAME, color: p ? projectColor(p) : null, tabs: [], sessions: [] });
      }
      groups[groups.length - 1].tabs.push(tabs[i]);
      groups[groups.length - 1].sessions.push(s);
    });
    host.replaceChildren(...groups.map((g) => h('div', {
      class: 'session-group' + (g.color ? '' : ' unknown'),
      style: g.color ? `--proj:${g.color}` : null,
    },
    h('button', {
      class: 'session-group-label' + (projectViewShowing(g.id) ? ' active' : ''), 'data-project': g.id,
      title: `${g.color ? g.name : `${UNKNOWN_NAME}: sessions with no project`}\nClick for its Project view, its worktrees side by side (again to go back)`,
      onclick: () => toggleProjectView(g.id),
    }, iconEl('list'), g.name),   // the Project tab's icon, so it's clear the name opens something
    g.tabs,
    plus(g.sessions, g.color ? `in ${g.name}` : 'with no project'))));
  }
  syncStatus(host);
  $('#btnGrid').classList.toggle('active', !state.expandedId);
  revealActive(host, '.session-tab.active');   // e.g. after Ctrl+N opens a tab scrolled out of view
  renderDocTabs();
}

// ------------------------------------------------------------------ layout

function surfaceSize() {
  const r = $('#termPane').getBoundingClientRect();
  return { w: Math.max(200, Math.floor(r.width - 2 * PAD - 2 - 8 - RAIL_W)), h:Math.max(100, Math.floor(r.height - 2 * PAD - HEADER_H - 2 - 4)) };
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
  const switched = state.currentId !== id;
  if (switched) { state.currentId = id; store.set('currentId', id); }
  renderSessionPicker();   // locked to the open session
  layout();
  if (s.startsOnOpen) startSession(s);   // after the layout, so Claude starts at the open terminal's size
  for (const x of state.sessions) renderTileHeader(x);
  renderSessionTabs();
  setTimeout(() => s.term.focus(), 30);
  renderTermRail(s);
  refreshTermRail(s);
  // Last, so a fault in the Git panel can never stop the terminal from opening.
  if (switched) {
    try { renderGit(); } catch (err) { console.error('Git panel render failed', err); }
  }
}

function collapse() {
  if (!state.expandedId) return;
  const s = state.byId.get(state.expandedId);
  state.expandedId = null;
  s?.term.blur();
  layout();
  for (const x of state.sessions) renderTileHeader(x);
  renderSessionTabs();
  renderSessionPicker();   // any visible session can be picked again
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
  // The physical key, not the character: with some keyboard layouts Ctrl plus a number key doesn't produce "1"…"9".
  const digit = /^(?:Digit|Numpad)([1-9])$/.exec(e.code)?.[1] ?? (/^[1-9]$/.test(k) ? k : null);
  if (e.ctrlKey && !e.altKey && !e.shiftKey && digit) {
    const s = visibleSessions()[+digit - 1];   // matches the Ctrl+N hints on the visible tabs
    if (s) expand(s.id);
    return true;
  }
  if (e.ctrlKey && !e.shiftKey && (k === 'PageUp' || k === 'PageDown')) { cycleDoc(k === 'PageUp' ? -1 : 1); return true; }
  if (e.ctrlKey && k === 'F4') { if (activeTab()) closeDoc(docs.active); return true; }
  // Ctrl+W closes a diff or Conversation tab. On the terminal it isn't taken: it deletes a word in Claude's prompt.
  if (e.ctrlKey && !e.shiftKey && !e.altKey && (k === 'w' || k === 'W') && activeTab()) { closeDoc(docs.active); return true; }
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
  if (menuKey(e)) { e.preventDefault(); e.stopPropagation(); return; }
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
  // Pressing on the menu mustn't take focus: after Paste in the terminal's menu, typing has to go on in the terminal.
  // Items: '-' for a separator ('~' for a lighter, shorter one within a section), { header } for a section title, or { label, action } with optional kbd, danger, and
  // checked (ticked when true). If any item can be ticked, all get the tick column so their labels line up.
  const ticks = items.some((it) => it && it.checked !== undefined);
  const menu = h('div', {
    class: 'menu', onmousedown: (e) => e.preventDefault(),
    onmousemove: (e) => e.currentTarget.querySelector('.menu-item.active')?.classList.remove('active'),   // the pointer takes over
  }, items.filter(Boolean).map((it) => it === '-' || it === '~'
    ? h('div', { class: 'menu-sep' + (it === '~' ? ' subtle' : '') })
    : it.header
      ? h('div', { class: 'menu-header' }, it.header)
      : h('div', { class: 'menu-item' + (it.danger ? ' danger' : ''), onclick: () => { closeMenu(); it.action(); } },
        ticks && h('span', { class: 'menu-check' }, it.checked ? '✓' : ''),
        it.label, it.kbd && h('span', { class: 'kbd' }, it.kbd))));
  $('#menuHost').append(menu);
  const r = menu.getBoundingClientRect();
  menu.style.left = Math.min(x, innerWidth - r.width - 4) + 'px';
  menu.style.top = Math.min(y, innerHeight - r.height - 4) + 'px';
  // Opened from the keyboard (a focused button's Enter or Space reports no click count): start on the first item.
  if (window.event?.type === 'click' && window.event.detail === 0) menu.querySelector('.menu-item')?.classList.add('active');
  openMenuEl = menu;
}

document.addEventListener('mousedown', (e) => { if (openMenuEl && !openMenuEl.contains(e.target)) closeMenu(); }, true);

/**
 * The keyboard for an open menu: the arrow keys (and Home/End) move a highlight, Enter or Space picks the highlighted
 * item, Escape closes it. Focus stays where it was (see showMenu), so these keys are taken here before the terminal or
 * a field gets them. True if the key was the menu's.
 */
function menuKey(e) {
  if (!openMenuEl || e.ctrlKey || e.altKey || e.metaKey) return false;
  const items = [...openMenuEl.querySelectorAll('.menu-item')];
  const at = items.findIndex((i) => i.classList.contains('active'));
  const move = { ArrowDown: at + 1, ArrowUp: (at < 0 ? items.length : at) - 1, Home: 0, End: items.length - 1 }[e.key];
  if (move !== undefined) {
    items[at]?.classList.remove('active');
    items[(move + items.length) % items.length]?.classList.add('active');
    return true;
  }
  if ((e.key === 'Enter' || e.key === ' ') && at >= 0) { items[at].click(); return true; }
  if (e.key === 'Escape' && !e.shiftKey) { closeMenu(); return true; }
  return false;
}

/** from: 'session' (the tile's ⋯ button or header) or 'sessionTab' (the session tab), for plugins' items (see pluginMenuItems). */
function sessionMenu(s, x, y, from = 'session') {
  showMenu(x, y, [
    state.expandedId === s.id ? { label: 'Back to all sessions', kbd: 'Shift+Esc', action: collapse } : { label: 'Expand', action: () => expand(s.id) },
    { label: 'Show Git changes', action: () => showGit(s.id) },
    { label: 'Show conversation', action: () => openConversation(s) },
    { label: `Project view: ${projectName(groupOf(s))}${projectName(groupOf(s)).endsWith('s') ? '’' : '’s'} worktrees side by side`, action: () => openProjectView(groupOf(s)) },
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
    ...pluginMenuItems(from, pluginSessionContext(s)),
  ]);
}

function terminalMenu(s, x, y) {
  // These leave you in the terminal, ready to type (Enter after Paste, say).
  const inTerminal = (fn) => async () => { await fn(); s.term.focus(); };
  showMenu(x, y, [
    { label: 'Copy', kbd: 'Ctrl+C', action: inTerminal(() => s.term.hasSelection() && navigator.clipboard.writeText(s.term.getSelection())) },
    { label: 'Paste', kbd: 'Ctrl+V', action: inTerminal(() => navigator.clipboard.readText().then((t) => t && s.term.paste(t))) },
    { label: 'Select all', action: inTerminal(() => s.term.selectAll()) },
    { label: 'Clear scrollback', action: inTerminal(() => s.term.clear()) },
    '-',
    { label: 'Show conversation', action: () => openConversation(s) },
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
  // A dialog replacing another closes it.
  const hook = onModalClose;
  onModalClose = null;
  hook?.();
  const hostEl = $('#modalHost');
  const footer = h('div', { class: 'modal-footer' }, buttons);
  hostEl.replaceChildren(h('div', { class: 'modal' }, h('div', { class: 'modal-title' }, title), h('div', { class: 'modal-body' }, body), footer));
  hostEl.hidden = false;
  // Never start focus on a destructive button: a stray Enter/Space must not confirm data loss.
  setTimeout(() => hostEl.querySelector('input, [data-autofocus], button.primary:not(.danger)')?.focus(), 20);
}

/** Called once when the open dialog closes, however it's closed (set by dialogs that preview changes). */
let onModalClose = null;

function closeModal() {
  const hook = onModalClose;
  onModalClose = null;
  hook?.();
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
    h('option', { value: '' }, `No project (shown under ${UNKNOWN_NAME})`),
    state.projects.map((p) => h('option', { value: p.id, selected: p.id === selectedId }, p.name)),
    h('option', { value: NEW }, 'New project…'));
  const newName = h('input', { placeholder: 'Project name', spellcheck: 'false', hidden: true, maxlength: '60' });
  select.addEventListener('change', () => {
    newName.hidden = select.value !== NEW;
    if (!newName.hidden) newName.focus();
  });
  let created = null;   // so retrying after a failed save doesn't create the project twice
  return {
    el: field('Project', h('div', { class: 'stack' }, h('div', { class: 'combo' }, select, h('span', { class: 'combo-btn' }, iconEl('chevD'))), newName)),
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
  const agent = agentOf(s.command);
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
    field('Command', command, 'For example: claude, claude --model opus, codex'),
    project.el,
    field('Folder', h('input', { value: s.directory, readonly: true })),
    agent && field('Conversation',
      h('input', {
        value: s.conversation ?? '', readonly: true, spellcheck: 'false',
        placeholder: agent.choosesId ? `Learned from ${agent.name} once it has picked one (--continue)` : `Learned from ${agent.name} once it has started one`,
      }),
      `The conversation this session reopens each time it starts, followed ${agent.follows}. Change it with Resume a different conversation…`),
    error,
  ], [h('button', { class: 'btn', onclick: closeModal }, 'Cancel'), h('button', { class: 'btn primary', onclick: save }, 'Save')]);
  if (focus === 'project') setTimeout(() => project.select.focus(), 30);
}

/** An agent's name as a tag, in its colour (AgentProfile.Color) when it has one; the stylesheet picks the theme's. */
function agentTag(id) {
  const a = agentById(id);
  return h('span', {
    class: 'conv-agent' + (a?.color ? ' colored' : ''),
    style: a?.color && `--agent-dark: ${a.color.dark}; --agent-light: ${a.color.light}`,
  }, a?.name ?? id);
}

const leafName = (path) => (path || '').split(/[\\/]/).filter(Boolean).pop() || path;

/**
 * Searchable list of Claude Code's and Codex's saved conversations (optionally limited to one folder, or one agent's).
 * onPick(conv, confirm) fires on click; confirm is true for a double-click. Each conversation's agent is conv.agent.
 */
function conversationPicker({ directory = null, agent = null, onPick }) {
  const search = h('input', { placeholder: 'Search conversations by title, prompt or folder…', spellcheck: 'false' });
  const list = h('div', { class: 'conv-list' }, h('div', { class: 'conv-empty' }, 'Loading conversations…'));
  let all = [];
  let selected = null;

  const openIn = (conv) => state.sessions.find((x) => x.conversation === conv.sessionId || x.command.includes(conv.sessionId));

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
        !agent && agentTag(c.agent),
        open ? h('span', { class: 'conv-open' }, 'open in ' + open.name)
          : Date.now() - new Date(c.modified) < 3 * 60000 && h('span', { class: 'conv-open', title: 'Updated in the last few minutes — it may still be running in another terminal' }, 'active now')),
      h('div', { class: 'conv-meta' }, [!directory && c.cwd, c.branch && 'on ' + c.branch, relTime(c.modified)].filter(Boolean).join('  ·  ')),
      prompt && h('div', { class: 'conv-prompt' }, prompt));
    }));
  };

  search.addEventListener('input', render);
  request('conversations', { directory, agent })
    .then((r) => { all = r || []; render(); })
    .catch((e) => list.replaceChildren(h('div', { class: 'conv-empty' }, e.message)));
  return h('div', { class: 'conv-picker' }, search, list);
}

/**
 * from: a panel's button { directory, name, project, prompt }: the dialog opens filled in, and the session's first start
 * is a new conversation beginning with the (editable) prompt.
 */
function showAddDialog(mode = 'new', from = null) {
  const lastDir = store.get('lastDir', '');
  // New sessions get a conversation of their own, which they reopen every time. A remembered "claude --continue" is the
  // previous default rather than a choice, so it gives way to the new one.
  const remembered = store.get('lastCommand', null);
  const lastCommand = !remembered || remembered === 'claude --continue' ? DEFAULT_COMMAND : remembered;
  const dir = h('input', { value: from?.directory ?? '', placeholder: 'D:\\Source\\MyRepo', spellcheck: 'false' });
  const name = h('input', { value: from?.name ?? '', placeholder: '(Session Name)', spellcheck: 'false' });
  const command = h('input', { value: lastCommand, spellcheck: 'false' });
  const wtHost = h('div', { class: 'field', hidden: true });
  const error = h('div', { class: 'form-error', hidden: true });
  const pickerHost = h('div', { class: 'field' });
  const project = projectField(from ? from.project : defaultProjectId());
  const firstPrompt = from?.prompt != null && h('textarea', { class: 'first-prompt', spellcheck: 'false' }, from.prompt);
  const commandNote = h('span');
  let picked = null;   // the saved conversation chosen in "Resume existing conversation"

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
    if (mode === 'resume' ? !picked : !d) { error.hidden = false; error.textContent = mode === 'resume' ? 'Choose a conversation.' : 'Choose a folder.'; return; }
    const cmd = command.value.trim() || DEFAULT_COMMAND;
    const list = [{
      directory: d, name: name.value.trim(), command: cmd, prompt: firstPrompt ? firstPrompt.value.trim() : undefined,
      conversation: mode === 'resume' ? picked?.sessionId : undefined,
    }];
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
    picked = null;
    segNew.classList.toggle('active', m === 'new');
    segResume.classList.toggle('active', m === 'resume');
    error.hidden = true;
    if (m === 'resume') {
      wtHost.hidden = true;
      pickerHost.hidden = false;
      dir.value = '';
      name.value = '';
      pickerHost.replaceChildren(h('label', {}, 'Conversation (double-click to add)'), conversationPicker({
        onPick: (c, confirm) => {
          picked = c;
          dir.value = c.cwd || '';
          name.value = leafName(c.cwd);
          if (agentOf(command.value)?.id !== c.agent) command.value = agentById(c.agent)?.programs[0] ?? command.value;   // only its own agent can open it
          error.hidden = true;
          updateNote();
          if (confirm) add();
        },
      }));
      setTimeout(() => pickerHost.querySelector('input')?.focus(), 20);
    } else {
      pickerHost.hidden = true;
      pickerHost.replaceChildren();
      if (!command.value) command.value = lastCommand;
      loadWorktrees();
    }
    updateNote();
  };

  // What happens to the conversation, in words, under the Command box: it follows the mode, the pick and the command.
  function updateNote() {
    const cmd = command.value.trim() || DEFAULT_COMMAND;
    const agent = agentOf(cmd);
    let text;
    if (firstPrompt) text = 'Starts a new conversation with the first prompt above, and reopens it each time the session starts.';
    else if (!agent) text = 'Runs in the folder above.';
    else if (hasOption(agent.ownConversation, cmd)) text = 'Your command picks its own conversation, so the app leaves that to it.';
    else if (mode === 'resume') text = picked
      ? `Reopens "${picked.title}" each time the session starts, following it ${agent.follows}.`
      : 'Pick the conversation to carry on above.';
    else if (hasOption(agent.continue, cmd)) text = 'Carries on the folder\'s latest conversation, then keeps that one: it reopens each time the session starts.';
    else text = `${agent.choosesId ? 'Gets a conversation of its own' : 'Starts a new conversation'} and reopens it each time the session or the app restarts, following it ${agent.follows}.`;
    commandNote.textContent = text;
  }
  command.addEventListener('input', updateNote);
  // Each agent's plain command to pick from, dropped down from the box like a combo box's list (the arrow, or Alt+Down
  // in the box); the box stays free to type over (e.g. "claude --model opus").
  const suggestions = () => {
    const r = command.getBoundingClientRect();
    showMenu(r.left, r.bottom + 2, state.agents.map((a) => ({
      label: a.programs[0], kbd: a.name, checked: command.value.trim() === a.programs[0],
      action: () => { command.value = a.programs[0]; updateNote(); command.focus(); },
    })));
    openMenuEl.style.minWidth = r.width + 'px';
  };
  const suggest = h('button', { class: 'combo-btn', title: 'Suggested commands (Alt+Down)', tabindex: '-1', onclick: suggestions }, iconEl('chevD'));
  command.addEventListener('keydown', (e) => { if (e.altKey && e.key === 'ArrowDown') { e.preventDefault(); suggestions(); } });
  segNew.addEventListener('click', () => setMode('new'));
  segResume.addEventListener('click', () => setMode('resume'));

  openModal('New session', [
    !firstPrompt && h('div', { class: 'seg' }, segNew, segResume),
    pickerHost,
    field('Folder (repository or worktree)', h('div', { class: 'inline' }, dir, browse)),
    field('Name', name, 'Leave it empty to use the folder\'s name.'),
    firstPrompt && field('First prompt', firstPrompt, 'From the panel. Starts a new conversation in this session; edit it first if you like.'),
    field('Command', h('div', { class: 'combo' }, command, suggest), commandNote),
    project.el,
    wtHost,
    error,
  ], [h('button', { class: 'btn', onclick: closeModal }, 'Cancel'), h('button', { class: 'btn primary', onclick: add }, firstPrompt ? 'Start session' : 'Add')]);
  $('#modalHost .modal').classList.add('wide');
  setMode(mode);
}

// ------------------------------------------------------------------ quick new sessions, from the one you're on

/**
 * The + beside a project's name on the session tab row: another session in that project, like `s` (the group's
 * current session, else its first): in the same folder, in a new worktree of its repository, or anywhere.
 */
function quickAddMenu(s, anchor) {
  const r = anchor.getBoundingClientRect();
  const p = projectOf(s);
  showMenu(r.left, r.bottom + 2, [
    { header: p ? `New session in ${p.name}` : 'New session with no project' },
    { label: 'In the same folder', action: () => quickSession(s) },
    s.git?.isRepo && { label: 'In a new worktree…', action: () => showWorktreeDialog(s) },   // worktrees need a Git repository
    '-',
    { label: 'A different folder…', action: () => showAddDialog('new', { directory: s.directory, project: s.project ?? null }) },
  ]);
}

/** The session's command without anything that picks a conversation: a new session gets one of its own. */
function ownOptionsOnly(command) {
  const agent = agentOf(command);
  if (!agent) return command;
  for (const pattern of [agent.continue, agent.ownConversation].filter(Boolean)) command = command.replace(new RegExp(pattern, 'gi'), '');
  return command.trim();
}

/** "checkout-api", or "checkout-api 2" (3, …) when that's taken. */
function freeName(base) {
  const taken = new Set(state.sessions.map((x) => x.name.toLowerCase()));
  if (!taken.has(base.toLowerCase())) return base;
  for (let n = 2; ; n++) if (!taken.has(`${base} ${n}`.toLowerCase())) return `${base} ${n}`;
}

async function addAndOpen(item) {
  const before = new Set(state.sessions.map((x) => x.id));
  await request('addSessions', { sessions: [item] });
  if (item.project && state.hiddenProjects.has(item.project)) toggleProject(item.project);
  const added = state.sessions.find((x) => !before.has(x.id));
  if (added) expand(added.id);
}

/** One click: another session in the same folder and project, with a conversation of its own. */
function quickSession(s) {
  addAndOpen({ directory: s.directory, name: freeName(s.name), command: ownOptionsOnly(s.command), project: s.project ?? '' })
    .catch((e) => toast(e.message, true));
}

/**
 * A new worktree of the session's repository on a new branch and, unless unticked, a session in it (same project and
 * options). From the Project view the branch starts from the repository's base branch (startFrom); from the + menu,
 * from the session's own branch.
 */
function showWorktreeDialog(s, { startFrom = null } = {}) {
  const root = (s.git?.root || s.directory).replace(/[\\/]+$/, '');
  const parent = root.replace(/[\\/][^\\/]*$/, '');
  const slug = (b) => b.trim().replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^[-.]+|[-.]+$/g, '');
  const branch = h('input', { placeholder: 'feature/my-change', spellcheck: 'false' });
  const base = h('input', { value: startFrom ?? (s.git?.branch && !s.git.branch.startsWith('(') ? s.git.branch : 'HEAD'), spellcheck: 'false' });
  const folder = h('input', { spellcheck: 'false' });
  const name = h('input', { placeholder: '(the branch name)', spellcheck: 'false' });
  const withSession = h('input', { type: 'checkbox', checked: true });
  const nameField = field('Session name', name);
  const error = h('div', { class: 'form-error', hidden: true });
  let folderEdited = false;
  // The folder follows the branch (next to the repository) until you change it yourself.
  const suggest = () => { if (!folderEdited) folder.value = slug(branch.value) ? `${parent}\\${leafName(root)}-${slug(branch.value)}` : ''; };
  branch.addEventListener('input', suggest);
  folder.addEventListener('input', () => { folderEdited = true; });
  const create = h('button', { class: 'btn primary' });
  const createLabel = () => (withSession.checked ? 'Create worktree and session' : 'Create worktree');
  const syncSession = () => { create.textContent = createLabel(); nameField.hidden = !withSession.checked; };
  withSession.addEventListener('change', syncSession);
  syncSession();
  const go = async () => {
    if (create.disabled) return;   // already creating (Enter in a field doesn't go through the button)
    const b = branch.value.trim();
    if (!b) { error.hidden = false; error.textContent = 'Name the new branch.'; return; }
    if (!folder.value.trim()) { error.hidden = false; error.textContent = 'Choose a folder for the worktree.'; return; }
    create.disabled = true;
    create.textContent = 'Creating…';
    error.hidden = true;
    let path;
    try {
      path = await request('worktree.add', { id: s.id, branch: b, path: folder.value.trim(), base: base.value.trim() || null });
    } catch (e) {
      error.hidden = false;
      error.textContent = e.message;
      create.disabled = false;
      create.textContent = createLabel();
      return;
    }
    closeModal();
    if (!withSession.checked) {
      if (state.projectViewOf != null) loadProjectView(true);
      toast(`Created the worktree at ${path}, on ${b}.`);
      return;
    }
    // The worktree exists now, so the dialog can't be retried: a failure from here on is reported on its own.
    try {
      await addAndOpen({ directory: path, name: freeName(name.value.trim() || b), command: ownOptionsOnly(s.command), project: s.project ?? '' });
    } catch (e) {
      toast(`The worktree was created in ${path}, but no session could be added there: ${e.message}`, true);
    }
  };
  create.addEventListener('click', go);
  for (const i of [branch, base, folder, name]) i.addEventListener('keydown', (e) => { if (e.key === 'Enter') go(); });
  openModal('New worktree', [
    h('div', { class: 'dim' }, `A new worktree of ${root}, on a new branch.`),
    field('New branch', branch),
    field('Based on', base, 'A branch or commit; the new branch starts here.'),
    field('Folder', folder, 'Created for the worktree, next to the repository unless you change it.'),
    h('label', { class: 'check-field' }, withSession, `Start a session in it${projectOf(s) ? `, in ${projectOf(s).name}` : ''}`),
    nameField,
    error,
  ], [h('button', { class: 'btn', onclick: closeModal }, 'Cancel'), create]);
  $('#modalHost .modal').classList.add('wide');
}

/** Points an existing session at a different saved conversation of its folder (or a fresh one) and restarts it. */
function showResumeDialog(s) {
  let picked = null;
  const resumeBtn = h('button', { class: 'btn primary', disabled: true }, s.running ? 'Resume (restarts session)' : 'Resume');
  // The session keeps the conversation itself (its command keeps only your own options): a picked one, or 'new'.
  const apply = (conversation) => {
    closeModal();
    request('updateSession', { id: s.id, conversation })
      .then(() => { restartSession(s); expand(s.id); })
      .catch((e) => toast(e.message, true));
  };
  resumeBtn.addEventListener('click', () => picked && apply(picked.sessionId));
  openModal(`Resume a conversation in "${s.name}"`, [
    h('div', { class: 'dim' }, s.directory),
    conversationPicker({
      directory: s.directory,
      agent: agentOf(s.command)?.id ?? null,
      onPick: (c, confirm) => { picked = c; resumeBtn.disabled = false; if (confirm) apply(c.sessionId); },
    }),
    h('div', { class: 'dim' }, `Close the conversation in any other terminal first, so two ${agentOf(s.command)?.name ?? 'agent'} processes don’t write to it at once.`),
  ], [
    h('button', { class: 'btn', onclick: closeModal }, 'Cancel'),
    h('button', { class: 'btn', title: 'Start a fresh conversation in this folder', onclick: () => apply('new') }, 'New conversation'),
    resumeBtn,
  ]);
  $('#modalHost .modal').classList.add('wide');
}

/**
 * Sessions saved before each had a conversation of its own: the app never knew which conversation they were in, so
 * they now start a new one. Said once, so nobody types into one expecting the conversation it had.
 */
function showNewConversations(ids) {
  const names = ids.map((id) => state.byId.get(id)?.name).filter(Boolean);
  if (!names.length) return;
  const one = names.length === 1;
  openModal(one ? 'A session starts a new conversation' : 'Some sessions start a new conversation', [
    h('div', {}, `Earlier versions of Claude Terminals didn't keep track of which conversation ${one ? 'this session was' : 'these sessions were'} in, ` +
      `so ${one ? 'it starts' : 'each starts'} a new one, and reopens that one from now on:`),
    h('ul', { class: 'name-list' }, names.map((n) => h('li', {}, n))),
    h('div', { class: 'dim' }, 'To go back to an earlier conversation, pick it with Resume a different conversation… in the session’s menu.'),
  ], [h('button', { class: 'btn primary', onclick: closeModal }, 'OK')]);
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
  // The Git panel follows the open session: showing another one's Git means opening that session.
  if (state.expandedId && state.expandedId !== id) { expand(id); if (!state.gitVisible) showGit(id); return; }
  if (state.currentId !== id) {
    state.currentId = id;
    store.set('currentId', id);
    layout();
    syncDocTabs();
  }
  if (!state.gitVisible) {
    state.gitVisible = true;
    store.set('gitVisible', true);
    applyGitVisibility();
  }
  renderSessionPicker();
  renderGit();
}

/** While a session is open the Git panel shows that session, so the picker offers only it; on the tiles, any visible one. */
function renderSessionPicker() {
  const sel = $('#gitSession');
  const open = state.byId.get(state.expandedId);
  const choices = open ? [open] : visibleSessions();
  sel.replaceChildren(...choices.map((s) => h('option', { value: s.id, selected: s.id === state.currentId },
    `${s.name}${projectOf(s) ? ` [${projectOf(s).name}]` : ''}  —  ${s.directory}`)));
  sel.disabled = !!open;
  sel.title = open ? `Showing the open session. Go back to all sessions (Shift+Esc) to pick another.` : '';
}

$('#gitSession').addEventListener('change', (e) => showGit(e.target.value));

function onGitStatus(m) {
  const s = state.byId.get(m.id);
  if (!s) return;
  // A new last commit, or a push/pull changing what is outgoing, makes the cached history stale.
  if (s.git && (s.git.head !== m.head || s.git.upstream !== m.upstream || s.git.ahead !== m.ahead)) invalidateLog(s);
  if (s.git && s.git.root !== m.root) {
    // Another repository: what was shown for the last one (its history, pull request and file diffs) no longer applies.
    invalidateLog(s);
    s.prInfo = null;
    s.selectedCommit = null;
    s.commitInfo = null;
    for (const t of docs.tabs.filter((x) => x.sessionId === s.id && !isConvTab(x))) closeDoc(t.key);
  }
  s.git = m;
  renderTileHeader(s);
  refreshOpenDiff(s);   // the visible diff follows its own session, whichever one the Git panel shows
  projectViewChanged(s);
  const historyPending = !s.log && !logFailed(s);
  if (s.id === state.currentId && (isTabShown('changes') || historyPending)) renderGit();
}

const currentSession = () => state.byId.get(state.currentId);

/** The last history load failed and nothing has changed since (logGen is undefined until the first invalidation). */
const logFailed = (s) => s.logError != null && s.logError.gen === s.logGen;

/** Drops the cached history; a history request already on its way is then not cached either (it may predate the change). */
function invalidateLog(s) {
  s.log = null;
  s.logGen = (s.logGen || 0) + 1;
}

// ---- docking
// The side panel holds groups of tabs stacked top to bottom (Git Changes, Git Repository and panels), each showing one
// of its tabs, with dividers between them. Dragging a tab onto another group's tab row moves it there; onto the top or
// bottom half of a group, it splits that group. A group with nothing to show for the selected session (only panels
// that belong elsewhere) hides, keeping its place. The layout is remembered.

const GIT_TABS = { changes: 'Git Changes', repo: 'Git Repository' };
const isPanelTab = (t) => t.startsWith('panel:');
const panelByTab = (t) => state.panels.find((p) => panelTab(p) === t);

/** groups: [{ tabs, active, size }], top to bottom; size is a share of the height. Elements are added when built. */
const dock = { groups: loadDock() };

function loadDock() {
  const saved = store.get('dock', null);
  const seen = new Set();
  const groups = (Array.isArray(saved?.groups) ? saved.groups : [])
    .filter((g) => Array.isArray(g?.tabs))
    .map((g) => ({
      // Each tab once, and only tabs the app knows how to show.
      tabs: g.tabs.filter((t) => typeof t === 'string' && (t in GIT_TABS || isPanelTab(t) || isPluginDockTab(t)) && !seen.has(t) && seen.add(t)),
      active: typeof g.active === 'string' ? g.active : null,
      size: Number(g.size) > 0 ? Number(g.size) : 1,
      folded: g.folded === true,
    }))
    .filter((g) => g.tabs.length);
  if (!groups.length) groups.push({ tabs: [], active: 'changes', size: 1 });
  for (const t of Object.keys(GIT_TABS)) if (!seen.has(t)) groups[0].tabs.push(t);
  return groups;
}

function saveDock() {
  store.set('dock', { groups: dock.groups.map((g) => ({ tabs: g.tabs, active: g.active, size: g.size, folded: !!g.folded })) });
}

const groupOfTab = (t) => dock.groups.find((g) => g.tabs.includes(t));

/**
 * A new panel's tab goes to the bottom group; a closed panel's tab goes, and so does a group left with nothing. So does
 * the tab of a plugin no longer in the plugins folder (or no longer shown in the side panel).
 */
function placePanels() {
  if (!state.panelsLoaded) return;   // until the host has said which panels exist, keep the saved layout as it is
  let changed = false;
  const live = new Set(state.panels.map(panelTab));
  const pluginGone = (t) => plugins.loaded && pluginById(pluginOfTab(t))?.placement !== 'panel';
  for (const g of dock.groups) {
    const kept = g.tabs.filter((t) => isPluginDockTab(t) ? !pluginGone(t) : !isPanelTab(t) || live.has(t));
    if (kept.length !== g.tabs.length) { g.tabs = kept; changed = true; }
  }
  for (const t of live) if (!groupOfTab(t)) { dock.groups[dock.groups.length - 1].tabs.push(t); changed = true; }
  for (const g of [...dock.groups]) if (!g.tabs.length) { removeGroup(g); changed = true; }
  if (changed) saveDock();
}

/** The tabs a group shows for the selected session: the Git ones always, panels when they show with it. */
const groupTabs = (g) => g.tabs.filter((t) => isPluginDockTab(t) ? !!pluginById(pluginOfTab(t)) : !isPanelTab(t) || (panelByTab(t) && panelVisibleFor(panelByTab(t), currentSession())));

/**
 * The tab a group has open: its chosen one, or its first while the chosen one isn't shown for this session. The choice
 * itself is kept, so a panel tab opens again when its session (or the panel list, at start) comes back.
 */
const shownActive = (g) => { const tabs = groupTabs(g); return tabs.includes(g.active) ? g.active : tabs[0] ?? null; };

/** Whether a tab is on screen now: open in its group (not folded), with the side panel showing. */
const isTabShown = (t) => state.gitVisible && dock.groups.some((g) => !g.folded && shownActive(g) === t);

// The `updated` time each panel tab was last seen at: a tab Claude has changed since gets a dot until it's opened.
// At start every panel counts as seen; one that appears later and isn't opened is new, so it gets the dot too.
const panelSeen = new Map();
const markSeen = (t) => { const p = panelByTab(t); if (p) panelSeen.set(t, p.updated); };
const isUnseen = (p) => panelSeen.get(panelTab(p)) !== p.updated;

function renderGit() {
  if (!state.gitVisible) return;
  placePanels();
  buildDock();
  const s = currentSession();
  for (const g of dock.groups) {
    const tabs = groupTabs(g);
    g.el.hidden = !tabs.length;
    if (!tabs.length) continue;
    const open = shownActive(g);
    if (!g.folded && isPanelTab(open)) markSeen(open);
    g.el.classList.toggle('folded', !!g.folded);
    renderDockTabs(g, tabs, open);
    if (g.folded) continue;   // drawn when it's unfolded
    // A plugin's page is a frame of its own beside the body the other tabs draw into (redrawing would reload it).
    const plugin = isPluginDockTab(open);
    g.body.hidden = plugin;
    g.frames.hidden = !plugin;
    if (plugin) { showDockPlugin(g, open); continue; }
    if (!s) g.body.replaceChildren(h('div', { class: 'git-message' }, 'No session selected.'));
    else if (isPanelTab(open)) renderPanel(panelByTab(open), g.body);
    else if (open === 'repo') renderRepo(s, g.body);
    else renderChanges(s, g.body);
  }
  layoutDock();
}

/** Puts the groups' elements (and the dividers between them) in the page, in order, building any that are new. */
function buildDock() {
  const host = $('#dockGroups');
  const want = [];
  dock.groups.forEach((g, i) => {
    if (!g.el) makeGroupEl(g);
    if (i > 0) want.push(g.divider ||= makeDivider(g));
    want.push(g.el);
  });
  if (want.length !== host.children.length || want.some((el, i) => host.children[i] !== el)) host.replaceChildren(...want);
}

function makeGroupEl(g) {
  g.tabsEl = h('div', { class: 'tw-tabs', role: 'tablist' });
  g.body = h('div', { class: 'dock-body' });
  g.frames = h('div', { class: 'dock-frames', hidden: true });
  const fold = h('button', { class: 'icon-btn small dock-fold', onclick: () => toggleFold(g) }, iconEl('chevD'));
  const title = h('div', { class: 'tw-title' }, fold, g.tabsEl);
  // Double-clicking the tab row folds the group too (not its ◀ ▶ buttons, which are often clicked in quick succession).
  title.addEventListener('dblclick', (e) => { if (!e.target.closest('.scroll-btn, .dock-fold')) toggleFold(g); });
  g.el = h('div', { class: 'dock-group' }, title, g.body, g.frames);
  g.el.dock = g;
  g.foldBtn = fold;
  makeScrollable(g.tabsEl, 'tw');
}

/** A folded group shows only its tab row, so it stays one click away without taking room. */
function toggleFold(g) {
  g.folded = !g.folded;
  saveDock();
  renderGit();
}

/**
 * Heights follow each group's share, and a folded group takes only its tab row. Dividers resize open groups, so one
 * shows wherever there are open groups above and below it.
 */
function layoutDock() {
  // Shares are relative: the open groups split the whole height between them. (Used as raw flex-grow values they'd
  // leave part of it empty whenever they add up to less than 1, which splitting and folding easily cause.)
  const open = dock.groups.filter((g) => !g.el.hidden && !g.folded);
  const total = open.reduce((n, g) => n + g.size, 0) || 1;
  let prev = null;
  for (const g of dock.groups) {
    const shown = !g.el.hidden;
    // One divider per gap between open groups: right under the upper one, even with folded groups after it.
    if (g.divider) g.divider.hidden = !(shown && prev && !prev.folded && dividedGroups(g));
    if (shown) prev = g;
    g.el.style.flex = g.folded ? '0 0 auto' : `${g.size / total} 1 0`;
    const label = g.folded ? 'Unfold this group' : 'Fold this group to its tabs';
    g.foldBtn.title = label + ' (or double-click the tab row)';
    g.foldBtn.setAttribute('aria-label', label);
    g.foldBtn.setAttribute('aria-expanded', String(!g.folded));
  }
}

function renderDockTabs(g, tabs, open) {
  fill(g.tabsEl, tabs.map((t) => {
    const cls = 'tw-tab' + (t === open ? ' active' : '');
    if (isPluginDockTab(t)) {
      const id = pluginOfTab(t), p = pluginById(id);
      return h('button', {
        class: cls + ' plugin-tab', 'data-tab': t, role: 'tab', 'aria-selected': String(t === open),
        title: `${pluginCaption(id)}: a plugin${p?.description ? '\n' + p.description : ''}`,
      }, iconEl('plug'), h('span', { class: 'panel-tab-title' }, pluginCaption(id)), pluginBadge(id));
    }
    if (!isPanelTab(t)) return h('button', { class: cls, 'data-tab': t, role: 'tab', 'aria-selected': String(t === open) }, GIT_TABS[t]);
    const p = panelByTab(t);
    const owner = state.byId.get(p.owner);
    return h('button', {
      class: cls + ' panel-tab', 'data-tab': t, role: 'tab', 'aria-selected': String(t === open),
      title: `${p.title}${p.badge ? ` (${p.badge.text})` : ''}${p.owner !== state.currentId && owner ? `\nFrom ${owner.name}` : ''}`,
    }, h('span', { class: 'panel-tab-title' }, p.title), p.badge && h('span', { class: `tw-badge tone-${tone(p.badge.tone)}` }, p.badge.text),
    (t !== open || g.folded) && isUnseen(p) && h('span', { class: 'tw-dot', title: 'Updated since you last looked', 'aria-label': 'updated' }));
  }));
  // With many tabs the row scrolls sideways (◀ ▶): keep the open one in view.
  requestAnimationFrame(() => revealActive(g.tabsEl, '.tw-tab.active'));
}

function activateTab(g, t) {
  g.active = t;
  g.folded = false;   // opening a tab in a folded group unfolds it
  saveDock();
  const s = currentSession();
  if (s && t === 'repo') invalidateLog(s);   // refresh history whenever the tab is opened
  renderGit();
}

/** Takes a group out, giving its height to the group above it (or below, for the top one). */
function removeGroup(g) {
  const i = dock.groups.indexOf(g);
  const heir = dock.groups[i - 1] || dock.groups[i + 1];
  if (heir) heir.size += g.size;
  dock.groups.splice(i, 1);
  g.el?.remove();
  g.divider?.remove();
}

/**
 * Moves a tab. to: { kind: 'join', group, before } puts it in that group's row before the tab `before` (null: at the end);
 * { kind: 'above' | 'below', group } makes it a group of its own there, taking half that group's height.
 */
function moveTab(tab, to) {
  const from = groupOfTab(tab);
  if (!from) return;
  if (to.kind === 'join') {
    if (to.before === tab) return;
    from.tabs.splice(from.tabs.indexOf(tab), 1);
    const at = to.before ? to.group.tabs.indexOf(to.before) : -1;
    to.group.tabs.splice(at < 0 ? to.group.tabs.length : at, 0, tab);
    to.group.active = tab;
  } else {
    if (to.group === from && from.tabs.length === 1) return;   // splitting a group off itself changes nothing
    from.tabs.splice(from.tabs.indexOf(tab), 1);
    to.group.size /= 2;
    const fresh = { tabs: [tab], active: tab, size: to.group.size };
    const i = dock.groups.indexOf(to.group);
    dock.groups.splice(to.kind === 'above' ? i : i + 1, 0, fresh);
  }
  if (!from.tabs.length) removeGroup(from);
  saveDock();
  renderGit();
}

/** Menu items to bring back the panels closed most recently (kept while the app runs). */
function reopenItems() {
  const closed = (state.closedPanels || []).slice(0, 5);
  if (!closed.length) return [];
  return ['-', { header: 'Reopen a closed panel' }, ...closed.map((c) => {
    const owner = state.byId.get(c.owner);
    return {
      label: c.owner === state.currentId || !owner ? c.title : `${c.title} (${owner.name})`,
      action: () => request('panels.reopen', { owner: c.owner, id: c.id }).then(() => {
        // The host has already sent the panels: open it where it is, if it shows with this session.
        const t = `panel:${c.owner}/${c.id}`;
        const g = groupOfTab(t);
        if (g && groupTabs(g).includes(t)) activateTab(g, t);
        else toast(`Reopened "${c.title}". It shows with "${owner?.name ?? 'its session'}".`);
      }).catch((e) => toast(e.message, true)),
    };
  })];
}

function resetDock() {
  const tabs = ['changes', 'repo', ...dock.groups.flatMap((g) => g.tabs).filter((t) => isPanelTab(t) || isPluginDockTab(t))];
  for (const g of dock.groups) { g.el?.remove(); g.divider?.remove(); }
  dock.groups = [{ tabs, active: 'changes', size: 1 }];
  saveDock();
  renderGit();
}

/** Dragging a divider shares the height of the groups either side of it. */
/**
 * The open groups a divider above group g resizes: the nearest open one above it and the nearest open one at or below
 * it. Folded groups in between are skipped (they keep their one-row height and just move along).
 */
function dividedGroups(g) {
  const i = dock.groups.indexOf(g);
  const isOpen = (x) => !x.el.hidden && !x.folded;
  const above = dock.groups.slice(0, i).reverse().find(isOpen);
  const below = dock.groups.slice(i).find(isOpen);
  return above && below ? { above, below } : null;
}

const MIN_GROUP = 140;   // matches .dock-group's min-height

function makeDivider(g) {
  const div = h('div', { class: 'dock-divider', role: 'separator', 'aria-orientation': 'horizontal', title: 'Drag to resize' });
  div.addEventListener('mousedown', (e) => {
    e.preventDefault();
    const pair = dividedGroups(g);
    if (!pair) return;
    const { above: prev, below: next } = pair;
    const a = prev.el.getBoundingClientRect().height, b = next.el.getBoundingClientRect().height;
    const share = prev.size + next.size, startY = e.clientY;
    div.classList.add('dragging');
    const move = (ev) => {
      const top = Math.max(MIN_GROUP, Math.min(a + b - MIN_GROUP, a + ev.clientY - startY));
      prev.size = (share * top) / (a + b);
      next.size = share - prev.size;
      layoutDock();
    };
    const up = () => {
      div.classList.remove('dragging');
      saveDock();
      removeEventListener('mousemove', move);
      removeEventListener('mouseup', up);
    };
    addEventListener('mousemove', move);
    addEventListener('mouseup', up);
  });
  return div;
}

// Tabs: click to open, drag to move (pointer events, as the other tab rows: WebView2 blocks HTML drag and drop here),
// right-click for the same moves from a menu, middle-click to close a panel.
(() => {
  const host = $('#dockGroups');
  const THRESHOLD = 8;
  let press = null;     // { tab, x, y, pointerId, label } from pointerdown until pointerup
  let drag = null;      // { ghost, hint, target } once the pointer has moved far enough
  let swallowClick = false;

  const clearMarks = () => host.querySelectorAll('.drop-before, .drop-after').forEach((el) => el.classList.remove('drop-before', 'drop-after'));

  /** Where a drop at (x, y) would put the tab, or null for nowhere useful. */
  function dropTarget(x, y) {
    const el = document.elementFromPoint(x, y);
    const groupEl = el?.closest('#dockGroups .dock-group');
    if (!groupEl) return el?.closest('#dockGroups') ? belowEverything(y) : null;
    if (groupEl.hidden) return null;
    const g = groupEl.dock;
    const strip = el.closest('.tw-title');
    // A folded group is only its tab row, with no top or bottom half to split on: the row's top and bottom quarters
    // put the tab in a new group above or below it instead (a line shows where), the middle joins the row.
    if (strip && g.folded) {
      const r = strip.getBoundingClientRect();
      const edge = y < r.top + r.height / 4 ? 'above' : y > r.bottom - r.height / 4 ? 'below' : null;
      if (edge) {
        if (groupOfTab(press.tab) === g && g.tabs.length === 1) return null;
        const at = edge === 'above' ? r.top : r.bottom;
        return { kind: edge, group: g, line: true, rect: { left: r.left, width: r.width, top: at - 2, height: 4 } };
      }
    }
    if (strip) {
      const tabs = [...g.tabsEl.querySelectorAll('.tw-tab')];
      const next = tabs.find((t) => x < t.getBoundingClientRect().left + t.getBoundingClientRect().width / 2);
      return { kind: 'join', group: g, before: next ? next.dataset.tab : null, mark: next || tabs[tabs.length - 1], side: next ? 'drop-before' : 'drop-after', rect: strip.getBoundingClientRect() };
    }
    if (groupOfTab(press.tab) === g && g.tabs.length === 1) return null;
    const r = groupEl.getBoundingClientRect();
    const above = y < r.top + r.height / 2;
    return { kind: above ? 'above' : 'below', group: g, rect: { left: r.left, width: r.width, top: above ? r.top : r.top + r.height / 2, height: r.height / 2 } };
  }

  /** Empty space under the groups (left when the last ones are folded): a new group at the bottom. */
  function belowEverything(y) {
    const last = [...dock.groups].reverse().find((g) => !g.el.hidden);
    if (!last) return null;
    const r = last.el.getBoundingClientRect();
    if (y < r.bottom) return null;
    if (groupOfTab(press.tab) === last && last.tabs.length === 1) return null;   // it's already the bottom group
    const host = $('#dockGroups').getBoundingClientRect();
    return { kind: 'below', group: last, rect: { left: host.left, width: host.width, top: r.bottom, height: Math.max(24, host.bottom - r.bottom) } };
  }

  function showTarget(t) {
    clearMarks();
    drag.hint.hidden = !t || t.kind === 'join';
    if (!t) return;
    if (t.kind === 'join') { t.mark?.classList.add(t.side); return; }
    drag.hint.classList.toggle('line', !!t.line);
    Object.assign(drag.hint.style, { left: t.rect.left + 'px', top: t.rect.top + 'px', width: t.rect.width + 'px', height: t.rect.height + 'px' });
  }

  function endDrag(drop) {
    const was = drag;
    drag = null;
    document.body.classList.remove('dock-dragging');
    was.ghost.remove();
    was.hint.remove();
    clearMarks();
    swallowClick = true;
    setTimeout(() => { swallowClick = false; }, 0);
    if (drop && was.target) moveTab(press.tab, was.target);
  }

  host.addEventListener('pointerdown', (e) => {
    const tab = e.button === 0 && e.target.closest('.tw-tab[data-tab]');
    if (!tab) return;
    press = { tab: tab.dataset.tab, x: e.clientX, y: e.clientY, pointerId: e.pointerId, label: tab.textContent };
  });
  addEventListener('pointermove', (e) => {
    if (!press || e.pointerId !== press.pointerId) return;
    if (!drag) {
      if (Math.hypot(e.clientX - press.x, e.clientY - press.y) < THRESHOLD) return;
      drag = { ghost: h('div', { class: 'dock-ghost' }, press.label), hint: h('div', { class: 'dock-hint', hidden: true }), target: null };
      document.body.append(drag.ghost, drag.hint);
      document.body.classList.add('dock-dragging');
    }
    drag.ghost.style.left = e.clientX + 14 + 'px';
    drag.ghost.style.top = e.clientY + 10 + 'px';
    drag.target = dropTarget(e.clientX, e.clientY);
    showTarget(drag.target);
  });
  addEventListener('pointerup', (e) => {
    if (!press || e.pointerId !== press.pointerId) return;
    if (drag) endDrag(true);
    press = null;
  });
  addEventListener('pointercancel', () => { if (drag) endDrag(false); press = null; });
  addEventListener('keydown', (e) => { if (drag && e.key === 'Escape') { e.stopPropagation(); endDrag(false); press = null; } }, true);

  host.addEventListener('click', (e) => {
    const tab = e.target.closest('.tw-tab[data-tab]');
    if (!tab || swallowClick) return;
    const g = tab.closest('.dock-group').dock;
    if (g.tabs.includes(tab.dataset.tab)) activateTab(g, tab.dataset.tab);
  });

  host.addEventListener('mousedown', (e) => { if (e.button === 1 && e.target.closest('.panel-tab, .plugin-tab')) e.preventDefault(); });   // no autoscroll
  host.addEventListener('auxclick', (e) => {
    const pt = e.button === 1 && e.target.closest('.plugin-tab');
    if (pt) { e.preventDefault(); closeDockPlugin(pt.dataset.tab); return; }
    const tab = e.button === 1 && e.target.closest('.panel-tab');
    const p = tab && panelByTab(tab.dataset.tab);
    if (p) { e.preventDefault(); confirmClosePanel(p); }
  });

  host.addEventListener('contextmenu', (e) => {
    const tab = e.target.closest('.tw-tab[data-tab]');
    if (!tab) return;
    e.preventDefault();
    const t = tab.dataset.tab;
    const g = tab.closest('.dock-group').dock;
    const i = dock.groups.indexOf(g);
    const p = isPanelTab(t) && panelByTab(t);
    const alone = g.tabs.length === 1;
    const items = [
      !alone && { label: 'Move to a new group above', action: () => moveTab(t, { kind: 'above', group: g }) },
      !alone && { label: 'Move to a new group below', action: () => moveTab(t, { kind: 'below', group: g }) },
      i > 0 && { label: 'Move to the group above', action: () => moveTab(t, { kind: 'join', group: dock.groups[i - 1], before: null }) },
      i < dock.groups.length - 1 && { label: 'Move to the group below', action: () => moveTab(t, { kind: 'join', group: dock.groups[i + 1], before: null }) },
      { label: g.folded ? 'Unfold group' : 'Fold group to its tabs', action: () => toggleFold(g) },
      p && '-',
      p && { label: 'Close panel…', danger: true, action: () => confirmClosePanel(p) },
      isPluginDockTab(t) && '-',
      isPluginDockTab(t) && { label: 'Close plugin', action: () => closeDockPlugin(t) },
      ...reopenItems(),
      '-',
      { label: 'Example panel skill…', action: showExampleSkill },
      { label: 'Reset layout', action: resetDock },
    ].filter(Boolean);
    while (items[0] === '-') items.shift();   // nothing to move: just Reset layout
    showMenu(e.clientX, e.clientY, items);
  });
})();

/**
 * The Git panel's toolbar, after Visual Studio's: the branch with its outgoing/incoming counts, the branch's open pull
 * request (PR 958 ▾), and Fetch, Pull, Push and a ⋯ menu.
 */
function branchLine(s) {
  const g = s.git;
  const busy = s.remoteBusy;   // 'fetch' | 'pull' | 'push' while one is running for this session
  const onBranch = !!g.branch && !g.branch.startsWith('(');
  ensurePullRequest(s);
  // The lookup also answers for a known host with no open pull request (data.pr null): no chip then.
  const info = s.prInfo?.branch === g.branch ? s.prInfo.data : null;
  const pr = info?.pr ? info : null;

  const tool = (op, iconName, title, enabled = true) => h('button', {
    class: 'icon-btn remote-btn' + (busy === op ? ' busy' : ''),
    title: busy === op ? `${title.split(':')[0]}: running…` : title,
    disabled: !!busy || !enabled,
    onclick: () => remoteOp(s, op),
  }, iconEl(iconName));
  const ahead = g.ahead, behind = g.behind;
  const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;

  return h('div', { class: 'git-toolbar' },
    repoPicker(s),
    h('div', { class: 'git-branch', title: g.upstream ? `Tracking ${g.upstream}` : 'No upstream branch' },
      iconEl('branch'), h('span', { class: 'name' }, g.branch || '(unknown)'),
      g.upstream && h('span', { class: 'ab', title: `${plural(ahead, 'outgoing commit')} / ${plural(behind, 'incoming commit')}` },
        iconEl('up'), String(ahead), ' ', iconEl('down'), String(behind)),
      pr && h('button', {
        class: 'pr-chip' + (pr.pr.draft ? ' draft' : ''),
        title: `${pr.pr.draft ? 'Draft pull request' : 'Pull request'} #${pr.pr.number}: ${pr.pr.title}\n${pr.host}`,
        onclick: (e) => { const r = e.currentTarget.getBoundingClientRect(); pullRequestMenu(s, pr, r.left, r.bottom + 2); },
      }, iconEl('pr'), `PR ${pr.pr.number}`, iconEl('chevD'))),
    h('div', { class: 'git-tools' },
      tool('fetch', 'fetch', 'Fetch: download new commits and branches from the remote, without changing your files'),
      tool('pull', 'pull', g.upstream
        ? `Pull: bring in ${plural(behind, 'incoming commit')} from ${g.upstream}. Fast-forward only: never merges`
        : 'Pull: this branch has no upstream yet (push it first)', !!g.upstream && onBranch),
      tool('push', 'push', g.upstream
        ? `Push: send ${plural(ahead, 'outgoing commit')} to ${g.upstream}. Never forced`
        : 'Push: publish this branch to the remote and track it', onBranch),
      h('button', {
        class: 'icon-btn', title: 'More Git actions',
        onclick: (e) => { const r = e.currentTarget.getBoundingClientRect(); gitMoreMenu(s, r.left, r.bottom + 2); },
      }, iconEl('more'))));
}

/**
 * Switches between the repositories in the session's folder (its own, and any in folders below it), for a session
 * started above several repositories. Null when there is only the one.
 */
function repoPicker(s) {
  const repos = s.git?.repos || [];
  if (repos.length < 2) return null;
  const dir = s.directory.replace(/[\\/]+$/, '');
  const label = (r) => {
    const rel = r.toLowerCase().startsWith(dir.toLowerCase() + '\\') ? r.slice(dir.length + 1) : null;
    if (rel) return rel;
    return r.toLowerCase() === dir.toLowerCase() ? `${leafName(r)} (this folder)` : `${leafName(r)} (contains this folder)`;
  };
  return h('select', {
    class: 'repo-picker', title: `Repository: ${s.git.root}\nThe Git repositories in ${s.directory}`,
    onchange: (e) => request('git.selectRepo', { id: s.id, root: e.target.value }).catch((err) => { toast(err.message, true); renderGit(); }),
  }, ...repos.map((r) => h('option', { value: r, selected: r === s.git.root }, label(r))));
}

const PR_CACHE_MS = 3 * 60_000;

/** Looks up the branch's open pull request in the background (cached a few minutes), re-rendering when it arrives. */
function ensurePullRequest(s, force = false) {
  const branch = s.git?.branch;
  if (!branch || branch.startsWith('(') || s.prLoading) return;
  if (!force && s.prInfo?.branch === branch && Date.now() - s.prInfo.at < PR_CACHE_MS) return;
  s.prLoading = true;
  request('git.pr', { id: s.id })
    .then((data) => { s.prInfo = { branch, at: Date.now(), data: data || null }; })
    .catch(() => { s.prInfo = { branch, at: Date.now(), data: null }; })
    .finally(() => {
      s.prLoading = false;
      if (currentSession() === s) renderGit();
    });
}

function pullRequestMenu(s, info, x, y) {
  showMenu(x, y, [
    { label: 'Open in Browser', action: () => request('openUrl', { uri: info.pr.url }) },
    { label: 'Refresh', action: () => ensurePullRequest(s, true) },
    '-',
    { label: 'View Pull Requests', action: () => request('openUrl', { uri: info.pullRequestsUrl }) },
    { label: 'New Pull Request', action: () => request('openUrl', { uri: info.newUrl }) },
  ]);
}

function gitMoreMenu(s, x, y) {
  const info = s.prInfo?.data;
  const free = !s.remoteBusy;
  showMenu(x, y, [
    free && { label: 'Fetch', action: () => remoteOp(s, 'fetch') },
    free && s.git.upstream && { label: 'Pull', action: () => remoteOp(s, 'pull') },
    free && { label: 'Push', action: () => remoteOp(s, 'push') },
    info && '-',
    info && { label: `Open repository in browser (${info.host})`, action: () => request('openUrl', { uri: info.webUrl }) },
    info && { label: 'View Pull Requests', action: () => request('openUrl', { uri: info.pullRequestsUrl }) },
    info && !info.pr && { label: 'New Pull Request', action: () => request('openUrl', { uri: info.newUrl }) },
    '-',
    { label: 'Refresh', action: () => { request('git.refresh', { id: s.id }).catch((e) => toast(e.message, true)); ensurePullRequest(s, true); } },
  ]);
}

/** Runs fetch, pull or push for a session, one at a time; the result goes to the info bar. */
async function remoteOp(s, op) {
  if (s.remoteBusy) return;
  s.remoteBusy = op;
  renderGit();
  const done = { fetch: 'Fetched.', pull: 'Pulled.', push: 'Pushed.' }[op];
  try {
    const out = (await request('git.' + op, { id: s.id }))?.trim();
    s.info = { text: out ? `${done}\n${out}` : done };
    if (op === 'push') ensurePullRequest(s, true);   // a first push may have just made "New Pull Request" relevant
  } catch (e) {
    s.info = { text: e.message, error: true };
  } finally {
    s.remoteBusy = null;
  }
  if (currentSession() === s && !isTabShown('changes')) toast(s.info.text, !!s.info.error);   // the info bar is on Git Changes
  renderGit();
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
  const suggest = h('button', { class: 'btn commit-suggest', onclick: () => suggestCommitMessage(s) }, iconEl('sparkle'), 'Suggest');
  const level = h('button', {
    class: 'btn commit-suggest-level', title: 'How much to write: Short, Standard or Detailed',
    onclick: (e) => { const r = e.currentTarget.getBoundingClientRect(); suggestLevelMenu(s, r.right - 220, r.bottom + 2); },
  }, iconEl('chevD'));
  const wrap = h('div', { class: 'git-commit' }, msg,
    h('div', { class: 'git-commit-actions' }, btn, count, h('div', { class: 'spacer' }), h('div', { class: 'split-btn' }, suggest, level)));
  const update = () => {
    const g = s.git;
    const total = g ? g.staged.length + g.unstaged.length : 0;
    btn.textContent = g?.staged.length ? 'Commit Staged' : 'Commit All';
    btn.disabled = !msg.value.trim() || total === 0;
    count.textContent = total ? `${total} change${total === 1 ? '' : 's'}` : '';
    suggest.disabled = level.disabled = total === 0 || !!s.suggesting;
    suggest.classList.toggle('busy', !!s.suggesting);
    suggest.title = s.suggesting ? 'Writing a commit message…'
      : `Suggest a ${SUGGEST_LEVELS[suggestLevel()].toLowerCase()} commit message for the ${g?.staged.length ? 'staged changes' : 'changes'} (${suggestedBy(s)})`;
  };
  msg.addEventListener('input', () => { s.commitDraft = msg.value; s.commitEdits = (s.commitEdits || 0) + 1; update(); });
  msg.addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.ctrlKey && !btn.disabled) btn.click(); });
  btn.addEventListener('click', () => doCommit(s, !s.git?.staged.length));
  s.commitUi = { wrap, msg, update };
  return s.commitUi;
}

/** How much Suggest writes; the choice is remembered. */
const SUGGEST_LEVELS = { short: 'Short', standard: 'Standard', detailed: 'Detailed' };
const suggestLevel = () => (store.get('suggestDetail', 'standard') in SUGGEST_LEVELS ? store.get('suggestDetail', 'standard') : 'standard');

function suggestLevelMenu(s, x, y) {
  const level = suggestLevel();
  const pick = (l) => () => { store.set('suggestDetail', l); suggestCommitMessage(s); };
  showMenu(x, y, [
    { header: 'Commit message' },
    { label: 'Short (one line)', checked: level === 'short', action: pick('short') },
    { label: 'Standard', checked: level === 'standard', action: pick('standard') },
    { label: 'Detailed', checked: level === 'detailed', action: pick('detailed') },
  ]);
}

/** Who writes a session's suggested commit message: its agent, or Claude for a session whose agent can't. */
function suggestedBy(s) {
  const agent = agentOf(s.command);
  return agent?.suggests ? agent.name : 'Claude';
}

/**
 * Visual Studio's "Generate commit message": the session's agent writes one for what Commit would take. It replaces the box's text
 * as an edit, so Ctrl+Z brings back what was there.
 */
async function suggestCommitMessage(s) {
  if (s.suggesting) return;
  s.suggesting = true;
  s.commitUi?.update();
  try {
    const text = await request('git.suggestMessage', { id: s.id, detail: suggestLevel() });
    const msg = s.commitUi?.msg;
    if (msg?.isConnected) {
      msg.focus();
      msg.select();
      document.execCommand('insertText', false, text);   // fires input, which saves the draft
      msg.setSelectionRange(0, 0);
      msg.scrollTop = 0;
    } else {
      s.commitDraft = text;   // the panel moved on to another session meanwhile
      if (msg) msg.value = text;
    }
  } catch (e) {
    s.info = { text: e.message, error: true };
    if (currentSession() === s) renderGit();
  } finally {
    s.suggesting = false;
    s.commitUi?.update();
  }
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
    const current = currentSession() === s && isTabShown('repo');
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

$('#gitRefresh').addEventListener('click', () => {
  const s = currentSession();
  if (!s) return;
  invalidateLog(s);
  request('git.refresh', { id: s.id }).catch((e) => toast(e.message, true));
  if (isTabShown('repo')) renderGit();
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

// ------------------------------------------------------------------ panels
// Claude shows these through the app's own MCP server (show_panel). They sit as tabs beside the Git ones and follow the
// selected session: a panel shows with the session that made it, every session of its project, or every session.

const panelTab = (p) => `panel:${p.owner}/${p.id}`;

function panelVisibleFor(p, s) {
  if (!s) return false;
  if (p.owner === s.id || p.scope === 'everywhere') return true;
  const owner = state.byId.get(p.owner);
  return p.scope === 'project' && !!owner?.project && owner.project === s.project;
}

function onPanels(m) {
  if (!state.panelsLoaded) for (const p of m.panels || []) panelSeen.set(panelTab(p), p.updated);   // nothing is "new" at start
  state.panels = m.panels || [];
  state.closedPanels = m.closed || [];
  state.panelsLoaded = true;
  // A closed panel's folds go with it.
  const live = new Set(state.panels.map(foldsKey));
  if (Object.keys(panelFolds).some((k) => !live.has(k))) {
    for (const k of Object.keys(panelFolds)) if (!live.has(k)) delete panelFolds[k];
    store.set('panelFolds', panelFolds);
  }
  const c = m.changed;
  // A panel Claude has just shown in the session you're looking at opens, side panel and all.
  if (c?.created && c.owner === state.currentId) {
    placePanels();   // its tab goes to the bottom group, and opens there
    const g = groupOfTab(panelTab(c));
    if (g) { g.active = panelTab(c); saveDock(); }
    if (!state.gitVisible) { state.gitVisible = true; store.set('gitVisible', true); applyGitVisibility(); }
  }
  renderGit();
}

function scopeText(p) {
  const owner = state.byId.get(p.owner);
  const proj = owner && projectOf(owner);
  if (p.scope === 'everywhere') return 'Shows with every session';
  if (p.scope === 'project' && proj) return `Shows with every ${proj.name} session`;
  return `Shows with ${owner ? owner.name : 'its session'} only`;
}

const TONES = ['neutral', 'info', 'success', 'warning', 'danger'];
const tone = (t) => (TONES.includes(t) ? t : 'neutral');

/** A list panel gets a filter box once it's long enough to need one. */
const FILTER_FROM = 8;
const panelFilters = new Map();   // what's typed in each panel's filter box, for as long as the app runs
const panelItemCount = (p) => (p.items ? p.items.length : (p.groups || []).reduce((n, g) => n + groupCount(g), 0));

/** A time shown as "4 min ago" and kept current (see the timer below). */
const liveTime = (iso) => h('span', { class: 'live-time', 'data-time': iso, title: new Date(iso).toLocaleString() }, relTime(iso));
setInterval(() => document.querySelectorAll('.live-time').forEach((el) => { el.textContent = relTime(el.dataset.time); }), 30_000);

function renderPanel(p, body) {
  const owner = state.byId.get(p.owner);
  const key = foldsKey(p);
  // Re-rendered on every change (a tick, Claude's update): keep the scroll position and the filter box's focus.
  const keepScroll = body.dataset.panel === key ? body.querySelector('.panel-scroll')?.scrollTop ?? 0 : 0;
  const filterFocused = document.activeElement?.classList.contains('panel-filter') && body.contains(document.activeElement);

  const meta = [p.subtitle, owner && `from ${owner.name}`].filter(Boolean).join(' · ');
  const ticked = allItems(p).filter((i) => i.checked).length;
  const scroll = h('div', { class: 'git-scroll panel-scroll' },
    p.stats?.length > 0 && panelStats(p),
    p.groups ? panelGroups(p)
      : p.items ? panelItems(p, p.items)
        : h('div', { class: 'md panel-md' }, foldHeadings(p, renderMarkdown(p.markdown || ''))));
  let filter = null;
  if ((p.items || p.groups) && panelItemCount(p) >= FILTER_FROM) {
    filter = h('input', { class: 'panel-filter', type: 'search', placeholder: 'Filter…', 'aria-label': 'Filter items', spellcheck: 'false', value: panelFilters.get(key) ?? '' });
    filter.addEventListener('input', () => { panelFilters.set(key, filter.value); applyPanelFilter(scroll, filter.value); });
  }
  fill(body,
    h('div', { class: 'panel-head' },
      h('div', { class: 'panel-heading' }, h('div', { class: 'panel-title' }, p.title),
        h('div', { class: 'panel-meta' }, meta, p.updated && [meta ? ' · ' : '', liveTime(p.updated)])),
      ticked > 0 && h('button', { class: 'btn', title: 'Types the ticked items into the session you\'re looking at, for you to say what to do with them', onclick: () => askAboutTicked(p) }, `Ask about ticked (${ticked})`),
      p.refresh && h('button', { class: 'btn', title: `Sends "${p.refresh}" to ${owner?.name ?? 'its session'}`, onclick: () => refreshPanel(p) }, 'Refresh'),
      h('button', { class: 'icon-btn', title: 'Panel options', 'aria-label': 'Panel options', onclick: (e) => panelMenu(p, e.currentTarget) }, iconEl('more'))),
    filter && h('div', { class: 'panel-filter-row' }, filter),
    scroll,
    h('div', { class: 'panel-foot' }, scopeText(p)));
  body.dataset.panel = key;
  if (filter?.value) applyPanelFilter(scroll, filter.value);
  scroll.scrollTop = keepScroll;
  if (filterFocused) filter?.focus();
  colorizeCode(scroll);
}

/** Shows only the items matching every word typed (key, title, subtitle, badges), with their groups opened. */
function applyPanelFilter(scroll, text) {
  const words = text.trim().toLowerCase().split(/\s+/).filter(Boolean);
  scroll.classList.toggle('filtering', words.length > 0);
  for (const el of scroll.querySelectorAll('.panel-item')) el.hidden = words.length > 0 && !words.every((w) => el.dataset.search.includes(w));
  for (const g of [...scroll.querySelectorAll('.panel-group')].reverse()) g.hidden = words.length > 0 && !g.querySelector('.panel-item:not([hidden])');
  scroll.querySelector('.panel-nomatch')?.remove();
  if (words.length && !scroll.querySelector('.panel-item:not([hidden])')) scroll.append(h('div', { class: 'git-message panel-nomatch' }, 'No items match.'));
}

function panelStats(p) {
  const arrows = { up: '▲', down: '▼', flat: '▬' };
  return h('div', { class: 'panel-stats' }, p.stats.map((s) => h('div', { class: `panel-stat tone-${tone(s.tone)}` },
    h('div', { class: 'panel-stat-label' }, s.label),
    h('div', { class: 'panel-stat-value' }, s.value,
      arrows[s.change] && h('span', { class: `panel-stat-change ${s.change}`, title: s.change === 'flat' ? 'No change' : s.change === 'up' ? 'Up' : 'Down' }, arrows[s.change])),
    s.note && h('div', { class: 'panel-stat-note' }, s.note),
    s.series?.length > 1 && sparkline(s.series, 96))));
}

/** A small trend line drawn here from Claude's numbers (never SVG from Claude), with a dot on the latest value. */
function sparkline(values, width = 120, height = 22) {
  const NS = 'http://www.w3.org/2000/svg';
  const el = (tag, attrs) => { const e = document.createElementNS(NS, tag); for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v); return e; };
  const min = Math.min(...values), max = Math.max(...values), span = max - min || 1;
  const pts = values.map((v, i) => [2 + (i / (values.length - 1)) * (width - 4), height - 2 - ((v - min) / span) * (height - 4)]);
  const svg = el('svg', { class: 'sparkline', width, height, viewBox: `0 0 ${width} ${height}`, role: 'img', 'aria-label': `Trend from ${values[0]} to ${values[values.length - 1]}` });
  svg.append(el('polyline', { points: pts.map((pt) => pt.map((n) => n.toFixed(1)).join(',')).join(' '), fill: 'none' }));
  const [x, y] = pts[pts.length - 1];
  svg.append(el('circle', { cx: x.toFixed(1), cy: y.toFixed(1), r: 2 }));
  return svg;
}

function panelProgress(pr) {
  const pct = Math.max(0, Math.min(100, (pr.value / pr.max) * 100));
  const label = pr.max === 100 ? `${Math.round(pr.value)}%` : `${pr.value} / ${pr.max}`;
  return h('div', { class: 'panel-progress', title: `${pr.value} of ${pr.max}` },
    h('div', { class: 'panel-progress-bar' }, h('span', { style: `width:${pct.toFixed(1)}%` })),
    h('span', { class: 'panel-progress-text' }, label));
}

/** Ticks are the person's: saved with the panel (as of the version they saw), and read by Claude with get_panel. */
function tickItem(p, path, checked) {
  request('panels.check', { owner: p.owner, id: p.id, path, checked, version: p.version })
    .then((done) => { if (!done) toast('The panel changed just then; tick it again if you still want to.'); })
    .catch((e) => toast(e.message, true));
}

/** Colours code blocks that name their language (```ts) with the editor the diffs use; text stays as written. */
function colorizeCode(root) {
  const blocks = [...root.querySelectorAll('pre.md-code code[data-lang]')];
  if (!blocks.length) return;
  loadMonaco().then(() => {
    const langs = monaco.languages.getLanguages();
    for (const code of blocks) {
      const n = code.dataset.lang.toLowerCase();
      const lang = langs.find((l) => l.id === n || l.aliases?.some((a) => a.toLowerCase() === n) || l.extensions?.includes('.' + n));
      if (!lang) continue;
      // colorize escapes the text itself and returns only its own token spans.
      monaco.editor.colorize(code.textContent, lang.id, { tabSize: 4 })
        .then((html) => { if (code.isConnected) { code.innerHTML = html; code.classList.add('colored'); } })
        .catch(() => {});
    }
  }).catch(() => {});
}

// What's folded in each panel ({ 'owner/id': { key: true|false } }), kept so a refresh or restart doesn't reopen it all.
// Groups are keyed by title, headings by level and text.
const panelFolds = store.get('panelFolds', {});
const foldsKey = (p) => `${p.owner}/${p.id}`;
const isFolded = (p, key, byDefault) => panelFolds[foldsKey(p)]?.[key] ?? byDefault;

function setFolded(p, key, folded) {
  (panelFolds[foldsKey(p)] ||= {})[key] = folded;
  store.set('panelFolds', panelFolds);
}

/** Makes `head` fold `body` (a click, or Enter/Space when it has focus); links inside the header still just open. */
function foldSection(p, key, byDefault, cls, head, body) {
  const folded = isFolded(p, key, byDefault);
  const sec = h('div', { class: cls + (folded ? ' folded' : '') });
  head.classList.add('fold-head');
  head.setAttribute('role', 'button');
  head.setAttribute('tabindex', '0');
  head.setAttribute('aria-expanded', String(!folded));
  head.prepend(h('span', { class: 'fold-chev', html: icon('chevD') }));
  const toggle = () => {
    const now = sec.classList.toggle('folded');
    head.setAttribute('aria-expanded', String(!now));
    setFolded(p, key, now);
  };
  head.addEventListener('click', (e) => { if (!e.target.closest('a')) toggle(); });
  head.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
  sec.append(head, h('div', { class: 'fold-body' }, body));
  return sec;
}

/** Each Markdown heading folds everything under it, up to the next heading of the same or a higher level. */
function foldHeadings(p, nodes) {
  const level = (n) => (n.classList?.contains('md-h') ? Number([...n.classList].find((c) => /^md-h\d$/.test(c)).slice(4)) : 0);
  const build = (i, above) => {
    const out = [];
    while (i < nodes.length) {
      const l = level(nodes[i]);
      if (l && l <= above) break;
      if (!l) { out.push(nodes[i++]); continue; }
      const head = nodes[i++];
      const [body, next] = build(i, l);
      i = next;
      out.push(body.length ? foldSection(p, `h${l}:${head.textContent}`, false, 'md-fold', head, body) : head);
    }
    return [out, i];
  };
  return build(0, 0)[0];
}

/** Items in a group and every group inside it. */
const groupCount = (g) => g.items.length + (g.groups || []).reduce((n, x) => n + groupCount(x), 0);

/** Groups fold one by one, nested groups too; each is keyed by its path so two "Low" sub-groups fold separately. */
function panelGroups(p, groups = p.groups, path = 'g:', depth = 0, at = '') {
  if (!groups.length) return h('div', { class: 'git-message' }, 'Nothing here.');
  return groups.map((g, gi) => {
    const key = path + g.title;
    const where = `${at}${gi}/`;   // group indexes, for ticking an item inside
    const inner = [g.items.length > 0 && panelItems(p, g.items, where), g.groups?.length > 0 && panelGroups(p, g.groups, key + '/', depth + 1, where)];
    return foldSection(p, key, !!g.collapsed, 'panel-group',
      h('div', { class: 'section-header panel-group-head', style: `--depth:${depth}` }, g.title, h('span', { class: 'count' }, `(${groupCount(g)})`)),
      inner.some(Boolean) ? inner : h('div', { class: 'panel-empty', style: `--depth:${depth}` }, 'None.'));
  });
}

const BUTTON_HINT = {
  new: 'Opens New Session with this prompt:\n\n',
  copy: 'Copies:\n\n',
  here: 'Types this into the session you\'re looking at, for you to send:\n\n',
};

/** at: the indexes of the groups holding these items ("0/2/"), so a tick names the item it was made on. */
function panelItems(p, items, at = '') {
  if (!items.length) return h('div', { class: 'git-message' }, 'Nothing here.');
  return sortedItems(p, items).map(([it, i]) => {
    const search = [it.key, it.title, it.subtitle, ...(it.badges || []).map((b) => b.text)].filter(Boolean).join(' ').toLowerCase();
    const tick = it.checked != null && h('input', {
      type: 'checkbox', class: 'panel-check', checked: it.checked, 'aria-label': `Done: ${it.title}`,
      onchange: (e) => tickItem(p, `${at}${i}`, e.target.checked),
    });
    const details = it.details && foldSection(p, 'd:' + (it.key || it.title), true, 'panel-details',
      h('div', { class: 'panel-details-head' }, 'Details'),
      h('div', { class: 'md panel-details-md' }, renderMarkdown(it.details)));
    return h('div', { class: 'panel-item' + (tick ? ' has-check' : '') + (it.checked ? ' done' : ''), 'data-search': search },
      tick,
      h('div', { class: 'panel-item-body' },
        (!!it.key || it.badges?.length > 0 || !!it.time) && h('div', { class: 'panel-item-top' },
          it.key && (it.link ? mdLink(it.link, h('span', { class: 'panel-key' }, it.key)) : h('span', { class: 'panel-key' }, it.key)),
          (it.badges || []).map((b) => h('span', { class: `panel-badge tone-${tone(b.tone)}` }, b.text)),
          it.time && h('span', { class: 'panel-time' }, liveTime(it.time))),
        h('div', { class: 'panel-item-title' }, !it.key && it.link ? mdLink(it.link, it.title) : it.title),
        it.subtitle && h('div', { class: 'panel-item-sub' }, it.subtitle),
        it.progress && panelProgress(it.progress),
        it.series?.length > 1 && sparkline(it.series),
        details,
        it.buttons?.length > 0 && h('div', { class: 'panel-item-actions' }, it.buttons.map((b) => h('button', {
          class: 'btn',
          title: (BUTTON_HINT[b.target] ?? BUTTON_HINT.here) + b.prompt,
          onclick: () => panelButton(p, b),
        }, b.target === 'new' && !b.label.endsWith('…') ? b.label + '…' : b.label)))));
  });
}

/**
 * A panel's button only ever puts its text in front of you: typed in the session you're looking at, in New Session,
 * or on the clipboard.
 */
function panelButton(p, b) {
  if (b.target === 'copy') {
    navigator.clipboard.writeText(b.prompt).then(() => toast('Copied.'), (e) => toast(e.message, true));
    return;
  }
  if (b.target === 'new') {
    const owner = state.byId.get(p.owner);
    showAddDialog('new', { directory: owner?.directory, name: b.name, project: owner?.project ?? null, prompt: b.prompt });
    return;
  }
  typeHere(b.prompt);
}

/** Types text into the session you're looking at, for you to finish or send (never sent from here). */
function typeHere(text) {
  const s = currentSession();
  if (!s) return;
  if (s.startsOnOpen) { expand(s.id); toast(`Starting "${s.name}". Try again once Claude is up.`); return; }
  if (!s.running) { toast(`"${s.name}" isn't running. Start it, then try again.`, true); return; }
  if (state.expandedId !== s.id) expand(s.id);
  s.term.paste(text);
  setTimeout(() => s.term.focus(), 40);
}

/** Every item in a panel, wherever it sits. */
const allItems = (p) => [...(p.items || []), ...(p.groups || []).flatMap(function walk(g) { return [...g.items, ...(g.groups || []).flatMap(walk)]; })];

/** Types the ticked items into the session you're looking at, so you can say what to do with them. */
function askAboutTicked(p) {
  const ticked = allItems(p).filter((i) => i.checked);
  const list = ticked.map((i) => (i.key ? `${i.key} (${i.title})` : `"${i.title}"`)).join(', ');
  typeHere(`About the items I ticked in the "${p.title}" panel: ${list}. `);
}

// How each list panel's items are sorted ({ 'owner/id': 'time' | 'key' | 'title' }); missing means as Claude sent them.
const panelSort = store.get('panelSort', {});
const SORTS = { time: 'Newest first', key: 'Key', title: 'Title' };

function setPanelSort(p, mode) {
  if (mode) panelSort[foldsKey(p)] = mode; else delete panelSort[foldsKey(p)];
  store.set('panelSort', panelSort);
  renderGit();
}

/** Items in the panel's chosen order, each with its index as sent (ticks name items by that). */
function sortedItems(p, items) {
  const order = items.map((it, i) => [it, i]);
  const mode = panelSort[foldsKey(p)];
  const text = (x) => (x ?? '').toString();
  if (mode === 'time') order.sort((a, b) => (b[0].time ? Date.parse(b[0].time) : -Infinity) - (a[0].time ? Date.parse(a[0].time) : -Infinity) || a[1] - b[1]);
  else if (mode === 'key') order.sort((a, b) => text(a[0].key ?? a[0].title).localeCompare(text(b[0].key ?? b[0].title), undefined, { numeric: true }));
  else if (mode === 'title') order.sort((a, b) => a[0].title.localeCompare(b[0].title, undefined, { numeric: true }));
  return order;
}

const EXAMPLE_SKILL = `---
name: my-tasks
description: Show my open issues in a Claude Terminals panel. Use when I ask for my tasks, or to refresh the My tasks panel.
---

1. Get my open issues with our tracker's MCP tools (change this line to name yours,
   e.g. "the llamatrack my_tasks tool for project ACME").

2. Show them with the claude-terminals show_panel tool:
   - id "my-tasks", title "My tasks", refresh "/my-tasks"
   - subtitle: the tracker and project, e.g. "LlamaTrack · ACME"
   - badge: the number of open issues
   - groups by status: "In progress", "To do", then "Backlog" with collapsed: true
   - each item: key, title, link to the issue, subtitle with the component,
     priority as a badge (tone "danger" for urgent, "warning" for high),
     time = when it was last updated, checked: false
   - each item's buttons:
     - "Start session" (target "new", name "<key> <a few words>"), prompt:
       Work on <tracker> issue <key>, "<title>". Read it and its comments with the
       <tracker> tools, create a branch named <key>-<short-slug>, and propose a fix
       before changing code.
     - "Ask here", prompt: Summarise <key> and its latest comments.
     - "Copy branch" (target "copy"), text: <key>-<short-slug>

3. Reply in one line: how many issues there are and which is most urgent.
`;

function showExampleSkill() {
  const text = h('textarea', { class: 'skill-text', readonly: true, spellcheck: 'false' }, EXAMPLE_SKILL);
  openModal('Example panel skill', [
    h('p', {}, 'A skill tells Claude when and how to build a panel. Save this as ', h('code', {}, '.claude/skills/my-tasks/SKILL.md'),
      ' in a repository (shared with everyone who works in it) or in ', h('code', {}, '~/.claude/skills/my-tasks/SKILL.md'),
      ' (just you), change step 1 to your tracker\'s MCP tools, then type ', h('code', {}, '/my-tasks'), ' in a session.'),
    text,
  ], [
    h('button', { class: 'btn', onclick: closeModal }, 'Close'),
    h('button', { class: 'btn primary', onclick: () => navigator.clipboard.writeText(EXAMPLE_SKILL).then(() => toast('Copied.'), (e) => toast(e.message, true)) }, 'Copy'),
  ]);
  $('#modalHost .modal').classList.add('wide');
}

/** Refresh asks the session that made the panel to rebuild it (the prompt it gave, e.g. the slash command). */
function refreshPanel(p) {
  const owner = state.byId.get(p.owner);
  if (owner?.startsOnOpen) { startSession(owner); toast(`Starting "${owner.name}". Refresh again once Claude is up.`); return; }
  if (!owner?.running) { toast(`"${owner?.name ?? 'Its session'}" isn't running, so it can't refresh this panel.`, true); return; }
  post({ type: 'input', id: owner.id, data: p.refresh });
  setTimeout(() => post({ type: 'input', id: owner.id, data: '\r' }), 150);
  toast(`Asked ${owner.name} to refresh "${p.title}".`);
}

/** A first prompt that couldn't go on Claude's command line: typed once Claude is up, for you to send. */
function typePrompt(id, text) {
  const s = state.byId.get(id);
  if (s?.running && text) s.term.paste(text);
}

function panelMarkdown(p) {
  if (!p.items && !p.groups) return p.markdown || '';
  const list = (items) => items.map((it) => {
    const key = it.key ? (it.link ? `[${it.key}](${it.link}) ` : `${it.key} `) : '';
    const badges = it.badges?.length ? ` (${it.badges.map((b) => b.text).join(', ')})` : '';
    const box = it.checked == null ? '' : it.checked ? '[x] ' : '[ ] ';
    return `- ${box}${key}**${it.title}**${badges}${it.subtitle ? ` — ${it.subtitle}` : ''}${!it.key && it.link ? ` ${it.link}` : ''}`;
  }).join('\n');
  const groups = (gs, level) => gs.map((g) => [`${'#'.repeat(Math.min(level, 6))} ${g.title}`,
    g.items.length && list(g.items), g.groups?.length && groups(g.groups, level + 1), !groupCount(g) && '_None._'].filter(Boolean).join('\n\n')).join('\n\n');
  const stats = p.stats?.length ? p.stats.map((s) => `**${s.label}:** ${s.value}${s.note ? ` (${s.note})` : ''}`).join(' · ') + '\n\n' : '';
  return `## ${p.title}\n\n` + stats + (p.groups ? groups(p.groups, 3) : list(p.items)) + '\n';
}

function confirmClosePanel(p) {
  const owner = state.byId.get(p.owner);
  confirmDialog('Close panel',
    `Close "${p.title}"? It's removed for every session it shows with, ticks included. ${owner ? `Claude in "${owner.name}"` : 'Claude'} can show it again.`,
    'Close', true, () => request('panels.close', { owner: p.owner, id: p.id }).catch((e) => toast(e.message, true)));
}


function panelMenu(p, anchor) {
  const owner = state.byId.get(p.owner);
  const proj = owner && projectOf(owner);
  const scope = p.scope === 'project' && !proj ? 'session' : p.scope;
  const setScope = (value) => request('panels.scope', { owner: p.owner, id: p.id, scope: value }).catch((e) => toast(e.message, true));
  const r = anchor.getBoundingClientRect();
  showMenu(r.left, r.bottom + 2, [
    { header: 'Show this panel with' },
    { label: `${owner ? owner.name : 'Its session'} only`, checked: scope === 'session', action: () => setScope('session') },
    proj && { label: `Every ${proj.name} session`, checked: scope === 'project', action: () => setScope('project') },
    { label: 'Every session', checked: scope === 'everywhere', action: () => setScope('everywhere') },
    ...(p.items || p.groups ? [
      { header: 'Sort items' },
      { label: 'As Claude sent them', checked: !panelSort[foldsKey(p)], action: () => setPanelSort(p, null) },
      ...Object.entries(SORTS).map(([mode, label]) => ({ label, checked: panelSort[foldsKey(p)] === mode, action: () => setPanelSort(p, mode) })),
    ] : []),
    '-',
    p.refresh && { label: 'Refresh', action: () => refreshPanel(p) },
    { label: 'Copy as Markdown', action: () => navigator.clipboard.writeText(panelMarkdown(p)).then(() => toast('Copied.')) },
    '-',
    { label: 'Close panel…', danger: true, action: () => confirmClosePanel(p) },
  ]);
}

// ------------------------------------------------------------------ diff document (Monaco)

/** The diff editor's colours for Dark and Light, after Visual Studio 2026's; a theme's own go over its base's. */
const MONACO_COLORS = {
  'vs2026-dark': {
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
  'vs2026-light': {
    'editor.background': '#ffffff',
    'editorLineNumber.foreground': '#237893',
    'editorLineNumber.activeForeground': '#0b216f',
    'editor.selectionBackground': '#add6ff',
    'editor.lineHighlightBackground': '#00000000',
    'editor.lineHighlightBorder': '#eeeeee',
    'diffEditor.insertedLineBackground': '#9bb95533',
    'diffEditor.insertedTextBackground': '#9ccc2c55',
    'diffEditor.removedLineBackground': '#ff000022',
    'diffEditor.removedTextBackground': '#ff000044',
    'diffEditor.diagonalFill': '#22222220',
    'diffEditorGutter.insertedLineBackground': '#9bb95555',
    'diffEditorGutter.removedLineBackground': '#ff000033',
    'diffEditorOverview.insertedForeground': '#4b8b2bcc',
    'diffEditorOverview.removedForeground': '#c53030cc',
    'scrollbarSlider.background': '#64646466',
  },
};

let monacoReady = null;
let diffEditor = null;
const diffState = { spec: null, sessionId: null, sideBySide: store.get('diffSideBySide', true), ignoreWs: store.get('diffIgnoreWs', false), changes: [], seq: 0 };

function loadMonaco() {
  if (monacoReady) return monacoReady;
  monacoReady = new Promise((resolve, reject) => {
    require.config({ paths: { vs: 'lib/monaco/vs' } });
    require(['vs/editor/editor.main'], (m) => {
      window.monaco = window.monaco || m;
      for (const [name, colors] of Object.entries(MONACO_COLORS))
        monaco.editor.defineTheme(name, { base: name === 'vs2026-light' ? 'vs' : 'vs-dark', inherit: true, rules: [], colors });
      diffEditor = monaco.editor.createDiffEditor($('#diffEditor'), {
        theme: diffTheme(),
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

// side: the tab shown beside the terminal (dragged there), or null for one at a time. While it's set, the tab you pick
// goes beside the terminal, and active is 'terminal' or side, whichever has your attention (Ctrl+W closes only side).
// dir and at: which side of the terminal the tab goes (a DOCK_SIDES key) and the terminal's share of the area.
const docs = {
  tabs: [], active: 'terminal', side: null, shown: null,
  dir: store.get('docSplitDir', 'right'), at: store.get('docSplitAt', 0.5),
};
/** The sides of the terminal a tab can go, in words, with the side across from each. */
const DOCK_SIDES = {
  right: { label: 'Right of the terminal', across: 'left' },
  left: { label: 'Left of the terminal', across: 'right' },
  top: { label: 'Above the terminal', across: 'bottom' },
  bottom: { label: 'Below the terminal', across: 'top' },
};
const docKey = (sessionId, spec) => [sessionId, spec.mode, spec.commit || '', spec.path].join('|');
const activeTab = () => docs.tabs.find((t) => t.key === docs.active) || null;
const sideTab = () => (docs.side && docs.tabs.find((t) => t.key === docs.side && tabShown(t))) || null;
/** The tab whose page is on screen: the one you're on, else the one beside the terminal. */
const shownDoc = () => activeTab() || sideTab();
const canSplit = (t) => !!t && !isProjectTab(t);   // a Project view stands alone

/**
 * Diff and Conversation tabs belong to their session's project, and only the current session's project's tabs are
 * shown (the session the Git panel is on, which expanding a session also makes current). Others wait until you're back.
 */
function tabShown(t) {
  // A Project view is shown on its own: neither a session's tabs with it, nor it with a session's.
  if (isProjectTab(t)) return state.projectViewOf === t.projectId;
  if (state.projectViewOf != null) return false;
  if (isPluginTab(t)) return true;   // a plugin belongs to no project
  const cur = currentSession();
  const s = state.byId.get(t.sessionId);
  return !cur || !s || groupOf(s) === groupOf(cur);
}
const shownTabs = () => docs.tabs.filter(tabShown);

/** After the current project changes: a tab that's now someone else's makes way for the terminal. */
function syncDocTabs() {
  const t = activeTab();
  if (t && !tabShown(t)) showTerminalDoc();
  else showDocs();   // the tab beside the terminal may have come or gone with the project
}

/** Single-click opens the reusable (italic) preview tab; pin=true opens or keeps a permanent tab. */
function openDiff(s, spec, pin = false) {
  const key = docKey(s.id, spec);
  let tab = docs.tabs.find((t) => t.key === key);
  if (tab) {
    if (pin) tab.preview = false;
  } else {
    tab = { key, sessionId: s.id, spec, preview: !pin, viewState: null };
    const previewIdx = pin ? -1 : docs.tabs.findIndex((t) => t.preview && tabShown(t));   // one preview tab per project
    if (previewIdx >= 0) docs.tabs[previewIdx] = tab;
    else docs.tabs.push(tab);
  }
  activateDoc(key);
}

function activateDoc(key) {
  // A Project tab is a place of its own, like a session: opening it closes the open session, and anything else leaves it.
  const target = docs.tabs.find((t) => t.key === key);
  state.projectViewOf = isProjectTab(target) ? target.projectId : null;
  if (state.projectViewOf != null) collapse();
  docs.active = docs.tabs.some((t) => t.key === key && tabShown(t)) ? key : 'terminal';
  if (docs.side && canSplit(activeTab())) {
    docs.side = docs.active;   // split: the tab you pick goes beside the terminal, and takes the keyboard from it
    if ($('#termPane').contains(document.activeElement)) document.activeElement.blur();
  }
  showDocs();
  if (!activeTab()) {
    const s = state.byId.get(state.expandedId);
    if (s) setTimeout(() => s.term.focus(), 0);
  }
}

/** Puts the shown tab's page on screen (beside the terminal when split), keeping the view of the one it replaces. */
function showDocs() {
  const prev = docs.shown;
  const tab = shownDoc();
  if (prev && prev !== tab && docs.tabs.includes(prev)) {   // not a tab being closed
    if (isConvTab(prev)) prev.scrollTop = $('#convBody').scrollTop;
    else if (isProjectTab(prev)) prev.scrollTop = $('#projectBody').scrollTop;
    else if (!isPluginTab(prev) && diffEditor) prev.viewState = diffEditor.saveViewState();
  }
  docs.shown = tab;
  const split = !!tab && tab === sideTab();
  $('#diffDoc').hidden = !tab || isConvTab(tab) || isProjectTab(tab) || isPluginTab(tab);
  $('#convDoc').hidden = !isConvTab(tab);
  $('#projectDoc').hidden = !isProjectTab(tab);
  $('#pluginDoc').hidden = !isPluginTab(tab);
  $('#termPane').classList.toggle('behind', !!tab && !split);
  const area = $('#docArea');
  for (const dir in DOCK_SIDES) area.classList.toggle('split-' + dir, split && docs.dir === dir);
  area.style.setProperty('--split', docs.at * 100 + '%');
  hideRailCard();
  renderDocTabs();
  if (tab && prev !== tab) {
    if (isConvTab(tab)) showConversation(tab);
    else if (isProjectTab(tab)) showProjectView(tab);
    else if (isPluginTab(tab)) showPlugin(tab);
    else loadDiff(tab);
  } else if (!tab) {
    diffState.seq++;
  }
}

/** Shows tab `key` on a side of the terminal (a DOCK_SIDES key), or on its own again (dir null). */
function splitDoc(key, dir) {
  if (dir) {
    docs.dir = dir;
    store.set('docSplitDir', dir);
  }
  docs.side = dir ? key : null;
  activateDoc(key);
}

const showTerminalDoc = () => activateDoc('terminal');

/** While split, whichever side you click or move focus into has your attention (so Ctrl+W closes only the tab's side). */
function attend(key) {
  if (!sideTab() || docs.active === key) return;
  docs.active = key;
  renderDocTabs();
}

function closeDoc(key) {
  const i = docs.tabs.findIndex((t) => t.key === key);
  if (i < 0) return;
  const wasActive = docs.active === key;
  const wasSide = docs.side === key;
  if (isPluginTab(docs.tabs[i])) closePluginFrame(docs.tabs[i].pluginId);
  docs.tabs.splice(i, 1);
  // The nearest shown tab after it, else before it, that `ok` accepts.
  const near = (ok) => docs.tabs.slice(i).find((t) => tabShown(t) && ok(t)) || docs.tabs.slice(0, i).reverse().find((t) => tabShown(t) && ok(t));
  if (wasSide) docs.side = near(canSplit)?.key ?? null;   // another tab takes its place beside the terminal
  if (wasActive) {
    docs.active = 'terminal';
    const next = docs.side ? sideTab() : near(() => true);
    activateDoc(next ? next.key : 'terminal');
  } else if (wasSide) {
    showDocs();
  } else {
    renderDocTabs();
  }
  if (!docs.tabs.some((t) => !isConvTab(t) && !isProjectTab(t) && !isPluginTab(t))) disposeDiffModels();
}

function cycleDoc(delta) {
  const keys = ['terminal', ...shownTabs().map((t) => t.key)];
  const i = keys.indexOf(docs.active);
  activateDoc(keys[(i + delta + keys.length) % keys.length]);
}

function renderDocTabs() {
  const ex = state.byId.get(state.expandedId);
  // Hidden projects show theirs on the project tabs. While a session is open this tab carries its name, so a count here
  // would read as that session's; the session tabs' dots show which are waiting instead.
  const waiting = ex ? 0 : visibleSessions().filter(waitingForYou).length;
  const termTab = h('div', {
    class: 'doc-tab terminal' + (docs.active === 'terminal' ? ' active' : ''),
    'data-id': 'terminal',
    title: 'Terminals (Shift+Esc from a diff, Ctrl+PgUp/PgDn to switch tabs)\nDrag to a side of the area to show a tab beside it',
    onclick: showTerminalDoc,
  }, iconEl('terminal'), h('span', { class: 'doc-title' }, ex ? ex.name : 'All sessions'), ex ? projectTag(ex) : null,
  waiting ? h('span', { class: 'badge', title: `${waiting} session(s) waiting for you` }, String(waiting)) : null);

  const tabs = shownTabs().map((t) => {
    const s = state.byId.get(t.sessionId);
    if (isProjectTab(t)) {
      return h('div', {
        class: 'doc-tab' + (docs.active === t.key ? ' active' : ''),
        'data-id': t.key,
        title: `${projectName(t.projectId)}: every worktree of the project, with its session, changes and pull request`,
        onclick: (e) => { if (!e.target.closest('button')) activateDoc(t.key); },
        onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },
        onauxclick: (e) => { if (e.button === 1) closeDoc(t.key); },
      }, iconEl('list'), h('span', { class: 'doc-title' }, 'Project'),
      h('span', { class: 'doc-sub' }, projectName(t.projectId)),
      h('button', { class: 'icon-btn small', title: 'Close (Ctrl+W)', onclick: () => closeDoc(t.key) }, iconEl('close')));
    }
    if (isPluginTab(t)) {
      const p = pluginById(t.pluginId);
      return h('div', {
        class: 'doc-tab' + (docs.active === t.key ? ' active' : ''),
        'data-id': t.key,
        title: `${pluginCaption(t.pluginId)}: a plugin${p?.description ? '\n' + p.description : ''}`,
        onclick: (e) => { if (!e.target.closest('button')) activateDoc(t.key); },
        onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },
        onauxclick: (e) => { if (e.button === 1) closeDoc(t.key); },
      }, iconEl('plug'), h('span', { class: 'doc-title' }, pluginCaption(t.pluginId)), pluginBadge(t.pluginId, 'badge'),
      h('button', { class: 'icon-btn small', title: 'Close (Ctrl+W)', onclick: () => closeDoc(t.key) }, iconEl('close')));
    }
    if (isConvTab(t)) {
      return h('div', {
        class: 'doc-tab' + (docs.active === t.key ? ' active' : ''),
        'data-id': t.key,
        title: `${s?.name ?? ''}: the conversation so far`,
        onclick: (e) => { if (!e.target.closest('button')) activateDoc(t.key); },
        onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },
        onauxclick: (e) => { if (e.button === 1) closeDoc(t.key); },
      }, iconEl('chat'), h('span', { class: 'doc-title' }, 'Conversation'),
      h('span', { class: 'doc-sub' }, s?.name ?? ''),
      h('button', { class: 'icon-btn small', title: 'Close (Ctrl+W)', onclick: () => closeDoc(t.key) }, iconEl('close')));
    }
    const sub = t.spec.mode === 'commit' ? t.spec.commit.slice(0, 7) : t.spec.mode === 'staged' ? 'staged' : 'diff';
    return h('div', {
      class: 'doc-tab' + (t.preview ? ' preview' : '') + (docs.active === t.key ? ' active' : ''),
      'data-id': t.key,
      title: `${s?.name ?? ''}: ${t.spec.path} (${sub})${t.preview ? '\nPreview tab — double-click to keep it open' : ''}`,
      onclick: (e) => { if (!e.target.closest('button')) activateDoc(t.key); },
      ondblclick: () => { t.preview = false; renderDocTabs(); },
      onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },
      onauxclick: (e) => { if (e.button === 1) closeDoc(t.key); },
    }, h('span', { class: 'doc-title' }, t.spec.path.split('/').pop()),
    h('span', { class: 'doc-sub' }, `${s?.name ?? ''} · ${sub}`),
    h('button', { class: 'icon-btn small', title: 'Close (Ctrl+W)', onclick: () => closeDoc(t.key) }, iconEl('close')));
  });
  fill($('#docTabs'), state.projectViewOf != null ? null : termTab, tabs);   // a Project view stands alone
  // Split: the terminal and the tab beside it are both on screen; the one without your attention is marked "beside".
  const side = sideTab();
  for (const el of $('#docTabs').querySelectorAll('.doc-tab')) {
    el.classList.toggle('beside', !!side && !el.classList.contains('active') && (el.classList.contains('terminal') || el.dataset.id === side.key));
  }
  revealActive($('#docTabs'), '.doc-tab.active');
  // The project label in the session tab row looks selected while its Project view is showing.
  for (const label of document.querySelectorAll('.session-group-label')) label.classList.toggle('active', projectViewShowing(label.dataset.project));
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
  const t = shownDoc();
  if (!t || isConvTab(t) || t.sessionId !== s.id || t.spec.mode === 'commit') return;
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
  const t = shownDoc();
  if (t && !isConvTab(t)) request('openFile', { id: t.sessionId, path: t.spec.path }).catch((e) => toast(e.message, true));
});
$('#diffDoc').addEventListener('keydown', (e) => {
  if (e.altKey && e.key === 'ArrowDown') { e.preventDefault(); goToChange(1); }
  if (e.altKey && e.key === 'ArrowUp') { e.preventDefault(); goToChange(-1); }
});

// ------------------------------------------------------------------ conversation rail and Conversation tab
// In its full-screen mode Claude keeps no terminal scrollback, so the rail outlines the conversation from the agent's own
// transcript (one tick per prompt; see AgentProfile.ReadConversation), and the Conversation tab shows the whole of it
// as text the app can scroll.

const CONV_POLL_MS = 3000;
const convKey = (sessionId) => 'conv|' + sessionId;
const isConvTab = (t) => t?.kind === 'conv';

/**
 * Fetches the session's conversation: the outline for the rail (s.conv) or everything for the tab (s.convFull).
 * Resolves true when what the page has changed.
 */
async function loadConversation(s, full) {
  const slot = full ? 'convFull' : 'conv';
  const busy = slot + 'Loading';
  if (s[busy]) return false;
  s[busy] = true;
  try {
    const have = s[slot];
    const r = await request('conversation', { id: s.id, full, sessionId: have?.sessionId, version: have?.version, count: have?.turns.length ?? 0 });
    if (!r.found) {
      s[slot] = null;
      return !!have;
    }
    if (r.unchanged) return false;
    // Only the turns from r.from on were sent; earlier ones are as the page already has them.
    if (r.from > 0 && have?.sessionId === r.sessionId) r.turns = have.turns.slice(0, r.from).concat(r.turns);
    s[slot] = r;
    return true;
  } catch {
    return false;   // the session went away, or its Claude is between conversations
  } finally {
    s[busy] = false;
  }
}

setInterval(async () => {
  if (document.hidden) return;
  const tab = shownDoc();
  if (isConvTab(tab)) {
    const s = state.byId.get(tab.sessionId);
    if (s?.running && await loadConversation(s, true) && docs.shown === tab) renderConversation(s, tab);
  }
  if (!tab || tab === sideTab()) {   // the terminal is on screen
    const s = state.byId.get(state.expandedId);
    if (s?.running) refreshTermRail(s);
  }
}, CONV_POLL_MS);

async function refreshTermRail(s) {
  if (await loadConversation(s, false)) renderTermRail(s);
}

/** The open terminal's rail: the latest prompt is marked, and a click opens that prompt in the Conversation tab. */
function renderTermRail(s) {
  const turns = s.conv?.turns || [];
  renderRail(s.rail, turns.length, turns.length - 1, {
    pick: (i) => openConversation(s, i),
    card: (i) => turns[i],
  });
}

// ---- the rail

/** Draws n ticks; their spacing is worked out in CSS from the rail's height, so it follows resizes and animations. */
function renderRail(rail, n, active, handlers) {
  rail.rail = { n, ...handlers };
  rail.style.setProperty('--n', String(Math.max(n, 1)));
  const ticks = [];
  for (let i = 0; i < n; i++) ticks.push(h('i', { class: i === active ? 'active' : null, style: `--k:${i - (n - 1) / 2}` }));
  rail.replaceChildren(...ticks);
  rail.title = n ? '' : 'No conversation to show yet';
}

function markRailActive(rail, active) {
  [...rail.children].forEach((t, i) => t.classList.toggle('active', i === active));
}

/** The tick nearest the pointer (ticks can be a pixel or two apart in a long conversation). */
function railIndexAt(rail, clientY) {
  const n = rail.rail?.n || 0;
  if (!n) return -1;
  const r = rail.getBoundingClientRect();
  const gap = Math.min(10, (r.height - 24) / n);
  const i = Math.round((clientY - r.top - r.height / 2) / gap + (n - 1) / 2);
  return Math.max(0, Math.min(n - 1, i));
}

function attachRail(rail) {
  let hot = -1;
  const setHot = (i) => {
    if (i === hot) return;
    rail.children[hot]?.classList.remove('hot');
    hot = i;
    rail.children[hot]?.classList.add('hot');
  };
  rail.addEventListener('pointermove', (e) => {
    const i = railIndexAt(rail, e.clientY);
    setHot(i);
    if (i < 0) return hideRailCard();
    showRailCard(rail, i);
  });
  rail.addEventListener('pointerleave', () => { setHot(-1); hideRailCard(); });
  rail.addEventListener('mousedown', (e) => e.preventDefault());   // keep focus where it is
  rail.addEventListener('click', (e) => {
    const i = railIndexAt(rail, e.clientY);
    if (i >= 0) { hideRailCard(); rail.rail.pick(i); }
  });
}

function showRailCard(rail, i) {
  const t = rail.rail.card(i);
  const card = $('#railCard');
  if (!t) return hideRailCard();
  const reply = plainText(t.reply ?? t.items?.find((x) => x.kind === 'text')?.text ?? '');
  const tools = t.tools ?? t.items?.filter((x) => x.kind === 'tool').length ?? 0;
  const meta = [`Prompt ${i + 1} of ${rail.rail.n}`, t.time && formatTurnTime(t.time), tools && `${tools} tool call${tools === 1 ? '' : 's'}`];
  fill(card,
    h('div', { class: 'rc-prompt' }, t.prompt),
    reply && h('div', { class: 'rc-reply' }, reply.length > 400 ? reply.slice(0, 400) + '…' : reply),
    h('div', { class: 'rc-meta' }, meta.filter(Boolean).join(' · ')));
  card.hidden = false;
  const r = rail.getBoundingClientRect();
  const n = rail.rail.n;
  const gap = Math.min(10, (r.height - 24) / n);
  const y = r.top + r.height / 2 + (i - (n - 1) / 2) * gap;
  card.style.right = `${Math.max(8, innerWidth - r.left + 6)}px`;
  card.style.top = `${Math.max(8, Math.min(innerHeight - card.offsetHeight - 8, y - 20))}px`;
}

/**
 * Claude's Markdown as plain text for the hover card: formatting marks go, and code blocks and tables (which don't
 * read in three lines) are left out.
 */
function plainText(md) {
  return md
    .replace(/^\s*(`{3,}|~{3,})[\s\S]*?^\s*\1\s*$/gm, ' ')   // fenced code blocks
    .replace(/^\s*(`{3,}|~{3,})[\s\S]*$/m, ' ')               // one left open (the preview was cut off)
    .replace(/^\s*\|.*$/gm, ' ')                               // table rows
    .replace(/^\s{0,3}(#{1,6}|>)\s*/gm, '')                    // headings, quotes
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')                   // links: just their text
    .replace(/\*\*|__|`/g, '')                                 // bold, inline code
    .replace(/\s+/g, ' ')
    .trim();
}

function hideRailCard() {
  const card = document.getElementById('railCard');
  if (card) card.hidden = true;
}

function formatTurnTime(iso) {
  const d = new Date(iso);
  if (isNaN(d)) return '';
  const today = new Date().toDateString() === d.toDateString();
  return today
    ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

// ---- the Conversation tab: one per session, next to the diff tabs

const convView = { key: null, sessionId: null, els: [] };

/** Opens (or switches to) the session's Conversation tab, scrolled to prompt `index` (or the end). */
function openConversation(s, index = null) {
  if (currentSession() && groupOf(currentSession()) !== groupOf(s)) expand(s.id);
  const key = convKey(s.id);
  let tab = docs.tabs.find((t) => t.key === key);
  if (!tab) {
    tab = { key, kind: 'conv', sessionId: s.id, preview: false, scrollTop: null };
    docs.tabs.push(tab);
  }
  tab.pendingTurn = index;
  if (docs.shown === tab) {
    if (index != null && convView.key === key && convView.els[index]) { tab.pendingTurn = null; scrollToTurn(index); }
  } else {
    activateDoc(key);
  }
}

async function showConversation(tab) {
  const s = state.byId.get(tab.sessionId);
  if (!s) return;
  $('#convSession').textContent = s.name;
  if (convView.key !== tab.key) {
    turnObserver.disconnect();
    $('#convBody').replaceChildren();
    Object.assign(convView, { key: tab.key, sessionId: null, els: [] });
  } else if (tab.pendingTurn == null && tab.scrollTop != null) {
    $('#convBody').scrollTop = tab.scrollTop;   // a hidden pane loses its scroll position
  }
  if (s.convFull) renderConversation(s, tab);
  else $('#convSummary').textContent = 'Loading…';
  if (s.running) await loadConversation(s, true);
  if (docs.shown === tab) renderConversation(s, tab);
  $('#convBody').focus({ preventScroll: true });   // so the keyboard scrolls it
}

function renderConversation(s, tab) {
  const body = $('#convBody');
  const data = s.convFull;
  if (!data || !data.turns.length) {
    convView.els = [];
    convView.sessionId = null;
    $('#convSummary').textContent = '';
    fill(body, h('div', { class: 'chat-empty' }, !s.running ? 'This session isn’t running.'
      : data ? 'No prompts in this conversation yet.' : 'No Claude conversation found in this session.'));
    renderRail($('#convRail'), 0, -1, { pick() {}, card() {} });
    return;
  }
  const atEnd = body.scrollHeight - body.scrollTop - body.clientHeight < 40;
  const firstShow = convView.sessionId === null;
  if (convView.sessionId !== data.sessionId) {
    // A different conversation (/clear or /resume in the terminal): start over.
    turnObserver.disconnect();
    body.replaceChildren();
    convView.els = [];
    convView.sessionId = data.sessionId;
  }
  // Earlier turns never change; the last shown one may have grown, so it's redrawn with any new ones.
  const keep = Math.max(0, Math.min(convView.els.length, data.turns.length) - 1);
  for (const el of convView.els.splice(keep)) { turnObserver.unobserve(el); el.remove(); }
  for (const t of data.turns.slice(keep)) {
    const el = turnEl(t);
    body.append(el);
    convView.els.push(el);
  }
  const n = data.turns.length;
  // Fill the end now (a placeholder's guessed height would spoil following along or opening at the end); the rest
  // fills in as it scrolls near the view.
  for (const el of convView.els.slice(Math.max(keep, n - 3))) fillTurn(el);
  $('#convSummary').textContent = `${n} prompt${n === 1 ? '' : 's'}`;

  if (tab.pendingTurn != null) {
    scrollToTurn(Math.min(tab.pendingTurn, n - 1));
    tab.pendingTurn = null;
  } else if (firstShow) {
    body.scrollTop = tab.scrollTop ?? body.scrollHeight;
  } else if (atEnd) {
    body.scrollTop = body.scrollHeight;   // following along as Claude answers
  }
  renderRail($('#convRail'), n, turnInView(), {
    pick: (i) => scrollToTurn(i),
    card: (i) => s.convFull?.turns[i],
  });
}

/**
 * A turn starts as an empty placeholder: a conversation can have thousands, so each is only built (Markdown and all)
 * when it comes near the view.
 */
function turnEl(t) {
  const el = h('section', { class: 'chat-turn', 'data-index': t.index });
  el.turn = t;
  turnObserver.observe(el);
  return el;
}

const turnObserver = new IntersectionObserver((entries) => {
  for (const e of entries) if (e.isIntersecting) fillTurn(e.target);
}, { root: $('#convBody'), rootMargin: '1500px 0px' });

function fillTurn(el) {
  if (!el.turn) return;
  const t = el.turn;
  el.turn = null;
  turnObserver.unobserve(el);
  el.classList.add('filled');
  const answer = [];
  let tools = [];
  const flushTools = () => {
    if (!tools.length) return;
    const lines = tools.map((x) => h('div', { class: 'chat-tool', title: `${x.tool} ${x.text}` },
      h('span', { class: 'chat-tool-name' }, x.tool), ' ', h('span', { class: 'chat-tool-detail' }, x.text)));
    if (lines.length <= 3) answer.push(h('div', { class: 'chat-tools' }, lines));
    else {
      const names = [...new Set(tools.map((x) => x.tool))];
      answer.push(h('details', { class: 'chat-tools' },
        h('summary', {}, `${tools.length} tool calls: ${names.slice(0, 4).join(', ')}${names.length > 4 ? '…' : ''}`), lines));
    }
    tools = [];
  };
  for (const it of t.items) {
    if (it.kind === 'tool') { tools.push(it); continue; }
    flushTools();
    answer.push(h('div', { class: 'md' }, renderMarkdown(it.text)));
  }
  flushTools();
  el.replaceChildren(
    h('div', { class: 'chat-prompt' },
      h('div', { class: 'chat-meta' }, `You${t.time ? ' · ' + formatTurnTime(t.time) : ''}`),
      h('div', { class: 'chat-prompt-text' }, t.prompt)),
    h('div', { class: 'chat-answer' }, answer));
}

function scrollToTurn(i) {
  const el = convView.els[i];
  if (!el) return;
  for (const x of convView.els.slice(i, i + 3)) fillTurn(x);   // so the view lands on real content
  const body = $('#convBody');
  body.scrollTop = el.offsetTop - 8;
  // Placeholders around it fill in over the next frames and change height; line up again unless the user has scrolled.
  let expected = body.scrollTop;
  for (const ms of [60, 200, 500]) {
    setTimeout(() => {
      if (body.scrollTop !== expected || !el.isConnected) return;
      body.scrollTop = el.offsetTop - 8;
      expected = body.scrollTop;
    }, ms);
  }
  el.classList.remove('flash');
  void el.offsetWidth;
  el.classList.add('flash');
  setTimeout(() => el.classList.remove('flash'), 900);
  markRailActive($('#convRail'), i);
}

/** The prompt at the top of the Conversation tab's view. */
function turnInView() {
  const top = $('#convBody').scrollTop + 12;
  const els = convView.els;
  let lo = 0, hi = els.length - 1, found = els.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (els[mid].offsetTop + els[mid].offsetHeight > top) { found = mid; hi = mid - 1; } else lo = mid + 1;
  }
  return found;
}

$('#convBody').addEventListener('scroll', debounce(() => {
  if (isConvTab(shownDoc())) markRailActive($('#convRail'), turnInView());
}, 50));
attachRail($('#convRail'));

// ------------------------------------------------------------------ Project view: a project's worktrees side by side
// One row per worktree of each repository the project's sessions are in (from git's own worktree list, so worktrees
// made outside the app and ones whose session was closed show too), with the session in it, what it's doing, what's
// changed, how far its branch is from the base branch, and its pull request. A row opens to the latest prompt, the
// changed files (marked when another worktree changes the same file) and the actions: open or start a session,
// the pull request, and removing a worktree that's merged and clean.

const projectKey = (projectId) => 'project|' + projectId;
const isProjectTab = (t) => t?.kind === 'project';
// With no projects every session is in Unknown, which is then simply all of them.
const projectName = (id) => (id === UNKNOWN ? (state.projects.length ? UNKNOWN_NAME : 'All sessions') : state.projects.find((p) => p.id === id)?.name ?? '');
const projectMembers = (id) => state.sessions.filter((s) => groupOf(s) === id);

const PROJECT_REFRESH_MS = 30_000;
// repos: overview per repository root (lower-cased), from the host. open: the paths of the rows opened to their detail.
const projectView = { repos: new Map(), open: new Set(), loading: 0, lastLoad: 0, hl: null, renderTimer: null };

/** Switches to the project's Project view, a place of its own (see activateDoc). */
function openProjectView(projectId) {
  const members = projectMembers(projectId);
  if (!members.length) { toast(`${projectName(projectId)} has no sessions yet.`); return; }
  const key = projectKey(projectId);
  if (!docs.tabs.some((t) => t.key === key)) docs.tabs.push({ key, kind: 'project', projectId, preview: false, scrollTop: null });
  activateDoc(key);
}

function projectViewShowing(projectId) {
  return state.projectViewOf === projectId;
}

/**
 * A project's label at the start of its group in the session tab row: switches to its Project view, as a session's
 * tab switches to that session, or, while that's showing, goes back to all sessions (as the open session's tab does).
 */
function toggleProjectView(projectId) {
  if (projectViewShowing(projectId)) showTerminalDoc();
  else openProjectView(projectId);
}

function showProjectView(tab) {
  $('#projectTitle').textContent = projectName(tab.projectId);
  projectView.hl = null;
  renderProjectView();
  if (tab.scrollTop != null) $('#projectBody').scrollTop = tab.scrollTop;
  loadProjectView(true);
  $('#projectBody').focus({ preventScroll: true });
}

/** Something about a session changed (its status, git state or the list): redraw the view if it's showing. */
function projectViewChanged(s = null) {
  const tab = activeTab();
  if (!isProjectTab(tab) || (s && groupOf(s) !== tab.projectId)) return;
  clearTimeout(projectView.renderTimer);
  projectView.renderTimer = setTimeout(renderProjectView, 150);
}

/** Reads every repository's worktrees again, with the latest prompt and pull request of each session. */
async function loadProjectView(force = false) {
  const tab = activeTab();
  if (!isProjectTab(tab) || projectView.loading) return;
  if (!force && Date.now() - projectView.lastLoad < 5000) return;
  projectView.lastLoad = Date.now();
  const members = projectMembers(tab.projectId);
  const firstIn = new Map();   // one overview per repository: asked through the first session in it
  for (const s of members) if (s.git?.isRepo && s.git.root && !firstIn.has(s.git.root.toLowerCase())) firstIn.set(s.git.root.toLowerCase(), s);
  projectView.loading++;
  $('#projectRefresh').classList.add('busy');
  try {
    await Promise.all([
      ...[...firstIn].map(async ([root, s]) => {
        try { projectView.repos.set(root, { ...await request('worktree.overview', { id: s.id }), error: null }); }
        catch (e) { projectView.repos.set(root, { root: s.git.root, baseBranch: null, worktrees: [], error: e.message }); }
      }),
      ...members.filter((s) => s.running).map((s) => loadConversation(s, false)),
      ...members.filter((s) => !s.running && !s.convInfo).map((s) => loadConversationInfo(s)),
    ]);
    for (const s of members) if (s.git?.isRepo) ensurePullRequest(s);
  } finally {
    projectView.loading--;
    $('#projectRefresh').classList.remove('busy');
  }
  if (docs.active === tab.key) renderProjectView();
}

$('#projectRefresh').replaceChildren(iconEl('refresh'));
$('#projectRefresh').addEventListener('click', () => loadProjectView(true));
$('#projectNewWorktree').replaceChildren(iconEl('plus'), 'New worktree…');
$('#projectNewWorktree').addEventListener('click', (e) => newProjectWorktree(e.currentTarget));

/**
 * New worktree… on the Project view: a new worktree of the project's repository, starting from its base branch (main,
 * say) rather than whatever a session is on. A project with sessions in several repositories asks which first.
 */
function newProjectWorktree(anchor) {
  const members = projectMembers(state.projectViewOf);
  const byRoot = new Map();   // repository root → the session it's asked through (an agent one, for its options)
  for (const s of members) {
    if (!s.git?.isRepo || !s.git.root) continue;
    const key = s.git.root.toLowerCase(), had = byRoot.get(key);
    if (!had || (!agentOf(had.command) && agentOf(s.command))) byRoot.set(key, s);
  }
  const open = (s) => showWorktreeDialog(s, { startFrom: projectView.repos.get(s.git.root.toLowerCase())?.baseBranch ?? null });
  const choices = [...byRoot.values()];
  if (!choices.length) { toast(`None of ${projectName(state.projectViewOf)}'s sessions is in a Git repository.`, true); return; }
  if (choices.length === 1) { open(choices[0]); return; }
  const r = anchor.getBoundingClientRect();
  showMenu(r.left, r.bottom + 2, [{ header: 'New worktree of' }, ...choices.map((s) => ({ label: leafName(s.git.root), kbd: s.git.root, action: () => open(s) }))]);
}
setInterval(() => { if (!document.hidden && isProjectTab(activeTab())) loadProjectView(); }, PROJECT_REFRESH_MS);

const normPath = (p) => (p || '').replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
const underPath = (dir, path) => normPath(dir) === normPath(path) || normPath(dir).startsWith(normPath(path) + '\\');

/**
 * The rows: every worktree of every repository the project's sessions are in, each with the sessions whose folder is
 * in it (the deepest worktree wins), then any session whose repository isn't known yet or isn't a repository at all.
 */
function projectRows(projectId) {
  const members = projectMembers(projectId);
  const repos = [];   // in the order the sessions are in, each once
  for (const s of members) {
    const o = s.git?.root && projectView.repos.get(s.git.root.toLowerCase());
    if (o && !o.error && !repos.some((r) => normPath(r.root) === normPath(o.root))) repos.push(o);
  }
  const rows = repos.flatMap((o) => o.worktrees.filter((w) => !w.bare).map((w) => ({ ...w, repo: o, sessions: [] })));
  const placed = new Set();
  for (const s of members) {
    let best = null;
    for (const r of rows) if (underPath(s.directory, r.path) && (!best || normPath(r.path).length > normPath(best.path).length)) best = r;
    if (best) { best.sessions.push(s); placed.add(s.id); }
  }
  for (const s of members) {
    if (placed.has(s.id)) continue;
    const o = s.git?.root && projectView.repos.get(s.git.root.toLowerCase());
    rows.push({
      path: s.directory, branch: s.git?.branch ?? null, main: false, missing: false, ahead: 0, behind: 0, changes: 0, merged: false,
      repo: null, sessions: [s], noRepo: s.git ? !s.git.isRepo : false, pending: !s.git || (s.git.isRepo && !o), error: o?.error ?? null,
    });
  }
  return rows;
}

const STATUS_RANK = { asking: 0, attention: 1, working: 2, running: 3, exited: 4 };
/** The session a row speaks for: the one most in need of you, else the first. */
const leadSession = (row) => [...row.sessions].sort((a, b) => STATUS_RANK[statusClass(a)] - STATUS_RANK[statusClass(b)])[0] ?? null;

/** The row's changed files as { path, status }, from the first session that has git in it; null for a row with none. */
function rowFiles(row) {
  const s = row.sessions.find((x) => x.git?.isRepo);
  if (!s) return null;
  const files = new Map();
  for (const f of [...s.git.staged, ...s.git.unstaged]) if (!files.has(f.path)) files.set(f.path, { path: f.path, status: f.status });
  return [...files.values()];
}

/** What a session is on: its latest prompt while it runs, else its conversation's title. */
function latestPrompt(s) {
  if (s.running) return s.conv?.turns?.at(-1)?.prompt ?? '';
  return s.convInfo?.title ?? '';
}

/** Files changed in more than one worktree of the same repository: { key: repo|path, path, rows }. */
function projectOverlaps(rows) {
  const byFile = new Map();
  for (const r of rows) {
    if (!r.repo) continue;
    for (const f of rowFiles(r) || []) {
      const key = normPath(r.repo.root) + '|' + f.path;
      if (!byFile.has(key)) byFile.set(key, { key, path: f.path, rows: [] });
      byFile.get(key).rows.push(r);
    }
  }
  return [...byFile.values()].filter((o) => o.rows.length > 1);
}

function renderProjectView() {
  const tab = activeTab();
  if (!isProjectTab(tab)) return;
  const body = $('#projectBody');
  const rows = projectRows(tab.projectId);
  const overlaps = projectOverlaps(rows);
  const overlapOf = (row, path) => overlaps.find((o) => o.rows.includes(row) && o.path === path);
  const repos = [...new Set(rows.map((r) => r.repo).filter(Boolean))];
  const bases = [...new Set(repos.map((r) => r.baseBranch).filter(Boolean))];
  const errors = [...new Set(projectMembers(tab.projectId).map((s) => s.git?.root && projectView.repos.get(s.git.root.toLowerCase())?.error).filter(Boolean))];

  const needs = rows.filter((r) => r.sessions.some(waitingForYou)).length;
  const behind = rows.filter((r) => r.behind > 0).length;
  const removable = rows.filter((r) => r.merged && r.changes === 0 && !r.main).length;
  const n = (k, one, many = one + 's') => `${k} ${k === 1 ? one : many}`;
  $('#projectSummary').textContent = [n(rows.length, 'worktree'), needs && `${needs} need${needs === 1 ? 's' : ''} you`,
    behind && `${behind} behind ${bases.length === 1 ? bases[0] : 'the base branch'}`, removable && `${removable} can be removed`].filter(Boolean).join(' · ');

  const head = h('div', { class: 'pv-head' },
    h('span'), h('span', {}, 'Worktree · branch'), h('span', {}, 'Session'), h('span', {}, 'What it\'s on'), h('span', {}, 'Changes'),
    h('span', { title: 'Commits on its branch that aren\'t on the base branch (↑), and the other way round (↓)' }, bases.length === 1 ? `vs ${bases[0]}` : 'vs base'),
    h('span', {}, 'Pull request'), h('span'));

  const rowEls = rows.flatMap((row) => {
    const lead = leadSession(row);
    const open = projectView.open.has(normPath(row.path));
    const files = rowFiles(row);
    const touched = files?.some((f) => overlapOf(row, f.path));
    const pr = lead && lead.prInfo?.branch === lead.git?.branch ? lead.prInfo.data : null;
    const changes = files ? files.length : row.changes;
    // The main worktree reads "base" only while it's on the base branch; on a feature branch it's compared like the rest.
    const base = row.repo?.baseBranch;
    const onBase = row.main && base && (row.branch === base || base === 'origin/' + row.branch);
    const detached = !row.branch || row.branch.startsWith('(');
    const el = h('div', {
      class: 'pv-row' + (open ? ' open' : '') + (projectView.hl?.includes(row) ? ' hl' : ''), 'data-path': normPath(row.path),
      tabindex: 0, role: 'button', 'aria-expanded': String(open),
      onclick: () => toggleProjectRow(row),
      onkeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleProjectRow(row); } },
    },
    h('span', { class: 'dot ' + (lead ? statusClass(lead) : 'exited'), title: lead ? statusText(lead) : 'No session in this worktree' }),
    h('div', { class: 'pv-branch' },
      h('b', { title: row.branch ?? '' }, row.branch ?? (row.noRepo ? 'not a Git repository' : row.pending ? '…' : '(detached)')),
      h('span', { title: row.path }, row.path, row.main ? ' · main worktree' : '', row.missing ? ' · folder missing' : '')),
    h('div', { class: 'pv-session' }, row.sessions.length
      ? row.sessions.map((s, i) => [i > 0 && ', ', h('span', { class: 'pv-name', title: s.name }, s.name)])
      : h('span', { class: 'pv-none' }, 'no session')),
    h('div', { class: 'pv-doing', title: lead ? latestPrompt(lead) : '' }, lead
      ? [h('span', { class: 'st' }, statusText(lead)), latestPrompt(lead) && ' · ', latestPrompt(lead)]
      : row.missing ? 'Its folder is gone; git still lists it' : 'Made outside the app, or its session was closed'),
    h('div', { class: 'pv-changes' }, row.pending || row.noRepo ? '—' : changes > 0
      ? [h('span', { class: 'n' }, String(changes)), ` file${changes === 1 ? '' : 's'}`, touched && h('span', { class: 'pv-also', title: 'A file here is also changed in another worktree' }, ' ⚠')]
      : changes < 0 ? '?' : '—'),
    h('div', { class: 'pv-vs' }, onBase || !base || row.pending || row.noRepo || detached ? h('span', { class: 'even', title: detached && base ? 'Not on a branch, so there\'s nothing to compare' : '' }, onBase ? 'base' : '—')
      : row.ahead || row.behind ? [row.ahead ? h('span', { class: 'ahead' }, `↑${row.ahead}`) : null, row.ahead && row.behind ? ' ' : null, row.behind ? h('span', { class: 'behind' }, `↓${row.behind}`) : null]
      : h('span', { class: 'even' }, 'even')),
    h('div', { class: 'pv-pr' }, pr?.pr
      ? [h('a', { href: '#', title: `${pr.pr.draft ? 'Draft pull request' : 'Pull request'} #${pr.pr.number}: ${pr.pr.title}`, onclick: (e) => { e.preventDefault(); e.stopPropagation(); request('openUrl', { uri: pr.pr.url }); } }, `#${pr.pr.number}`),
         pr.pr.draft && h('span', { class: 'pv-state' }, ' draft')]
      : h('span', { class: 'even', title: lead ? '' : 'Open a session here to look up its pull request' }, '—')),
    h('span', { class: 'pv-chev' }, iconEl('chevR')));
    return open ? [el, projectRowDetail(row, files, overlapOf, pr)] : [el];
  });

  const overlapEls = overlaps.length
    ? overlaps.map((o) => h('div', {
        class: 'pv-overlap',
        onmouseenter: () => { projectView.hl = o.rows; paintProjectHighlight(); },
        onmouseleave: () => { projectView.hl = null; paintProjectHighlight(); },
      }, h('span', { class: 'pv-tri' }, '⚠'), h('code', {}, o.path.split('/').pop()), ' is changed in ',
      o.rows.map((r) => r.sessions[0]?.name ?? r.branch ?? r.path).join(' and '), ': they will conflict when the second merges.'))
    : [h('div', { class: 'pv-overlap none' }, repos.length ? 'No file is changed in more than one worktree.' : '')];

  const atTop = body.scrollTop;
  fill(body,
    errors.map((e) => h('div', { class: 'pv-error' }, e)),
    h('div', { class: 'pv-table' }, head, rowEls, h('div', { class: 'pv-overlaps' }, overlapEls)),
    h('div', { class: 'pv-legend' },
      h('span', {}, h('i', { class: 'dot running' }), 'idle'), h('span', {}, h('i', { class: 'dot working' }), 'working'),
      h('span', {}, h('i', { class: 'dot attention' }), 'finished while you were away'), h('span', {}, h('i', { class: 'dot asking' }), 'needs your answer'),
      h('span', {}, h('i', { class: 'dot exited' }), 'not running, or no session')));
  body.scrollTop = atTop;
}

function paintProjectHighlight() {
  const keys = new Set((projectView.hl || []).map((r) => normPath(r.path)));
  for (const el of $('#projectBody').querySelectorAll('.pv-row')) el.classList.toggle('hl', keys.has(el.dataset.path));
}

function toggleProjectRow(row) {
  const key = normPath(row.path);
  projectView.open.has(key) ? projectView.open.delete(key) : projectView.open.add(key);
  renderProjectView();
}

function projectRowDetail(row, files, overlapOf, pr) {
  const base = row.repo?.baseBranch;
  const left = h('div', { class: 'pv-block' });
  const right = h('div', { class: 'pv-block' });

  // Each session in the worktree: its latest prompt and the start of Claude's answer, or its conversation when it isn't running.
  for (const s of row.sessions) {
    const last = s.running ? s.conv?.turns?.at(-1) : null;
    left.append(h('div', {}, h('h3', {}, row.sessions.length > 1 ? `${s.name} · ${statusText(s)}` : statusText(s)),
      h('div', { class: 'pv-conv' }, last
        ? [h('div', { class: 'pv-turn' }, h('span', { class: 't' }, last.time ? relTime(last.time) : ''), h('span', { class: 'you' }, last.prompt)),
           last.reply && h('div', { class: 'pv-turn' }, h('span', { class: 't' }, agentOf(s.command)?.name ?? 'Reply'), h('span', { class: 'cl' }, plainText(last.reply).slice(0, 400)))]
        : !agentOf(s.command) ? `Runs ${s.command}.`
        : s.running ? 'No prompts in this conversation yet.'
        : s.convInfo?.title ? `Conversation: ${s.convInfo.title}${s.convInfo.modified ? ` · last used ${relTime(s.convInfo.modified)}` : ''}`
        : conversationLine(s) ?? 'Not running.')));
  }
  if (!row.sessions.length) left.append(h('div', {}, h('h3', {}, 'No session'),
    h('div', { class: 'pv-conv' }, row.missing ? `Git lists this worktree at ${row.path}, but the folder is gone.`
      : `This worktree exists at ${row.path} but no session is open in it.`)));

  if (!row.noRepo && !row.pending) left.append(h('div', {}, h('h3', {}, files ? `Changed files (${files.length})` : row.changes > 0 ? `Changed files (${row.changes})` : 'Working tree'),
    files?.length ? h('ul', { class: 'pv-files' }, files.map((f) => {
      const o = overlapOf(row, f.path);
      return h('li', {}, h('span', { class: 's ' + f.status[0] }, f.status[0]), f.path,
        o && h('span', { class: 'pv-also' }, `also in ${o.rows.filter((r) => r !== row).map((r) => r.sessions[0]?.name ?? r.branch).join(', ')}`));
    }))
    : h('div', { class: 'pv-conv' }, files ? 'Clean.' : row.changes > 0 ? 'Open a session here to see them in Git Changes.' : row.changes < 0 ? 'Couldn\'t be read.' : 'Clean.')));

  // Worth knowing
  const hints = [];
  if (row.noRepo) hints.push({ text: 'This folder isn\'t in a Git repository, so there\'s nothing to compare.' });
  if (row.pending) hints.push({ text: 'Reading the repository…' });
  if (row.error) hints.push({ text: row.error, warn: true });
  if (row.behind && base) hints.push({ text: `${row.behind} commit${row.behind === 1 ? '' : 's'} behind ${base}: bring ${base} in before opening or updating a pull request.` });
  if (row.merged && !row.main && row.changes === 0) hints.push({ text: `Everything on ${row.branch} is already on ${base}, and the tree is clean: the worktree can be removed.` });
  if (row.merged && !row.main && row.changes > 0) hints.push({ text: `Everything on ${row.branch} is already on ${base}, but there are uncommitted changes here.`, warn: true });
  if (!row.main && row.ahead && !pr?.pr && pr) hints.push({ text: `${row.ahead} commit${row.ahead === 1 ? '' : 's'} not in any pull request yet.` });
  if (files?.some((f) => overlapOf(row, f.path))) hints.push({ text: 'A file here is also changed in another worktree of this repository.', warn: true });
  if (!row.sessions.length && !row.missing) hints.push({ text: 'No session: start one here to carry on, or remove the worktree.' });
  right.append(h('div', {}, h('h3', {}, 'Worth knowing'),
    hints.length ? h('div', { class: 'pv-hints' }, hints.map((x) => h('div', { class: x.warn ? 'pv-warn' : 'pv-conv' }, x.text))) : h('div', { class: 'pv-conv' }, 'Nothing outstanding.')));

  // Actions
  const stop = (fn) => (e) => { e.stopPropagation(); fn(); };
  const actions = h('div', { class: 'pv-actions' });
  for (const s of row.sessions) {
    actions.append(h('button', { class: 'btn primary', onclick: stop(() => expand(s.id)) }, row.sessions.length > 1 ? `Open ${s.name}` : s.startsOnOpen ? 'Open and start' : 'Open session'));
    if (s.running && agentOf(s.command)?.showsConversation) actions.append(h('button', { class: 'btn', onclick: stop(() => openConversation(s)) }, row.sessions.length > 1 ? `Conversation of ${s.name}` : 'Conversation'));
  }
  if (!row.sessions.length && !row.missing) actions.append(h('button', { class: 'btn primary', onclick: stop(() => startSessionIn(row)) }, 'Start a session here'));
  if (pr?.pr) actions.append(h('button', { class: 'btn', onclick: stop(() => request('openUrl', { uri: pr.pr.url })) }, `View PR #${pr.pr.number}`));
  else if (pr && !row.main && row.ahead) actions.append(h('button', { class: 'btn', onclick: stop(() => request('openUrl', { uri: pr.newUrl })) }, 'New pull request'));
  if (row.repo && !row.main) actions.append(h('button', { class: 'btn', onclick: stop(() => confirmRemoveWorktree(row)) }, 'Remove worktree…'));
  right.append(h('div', {}, h('h3', {}, 'Actions'), actions));

  return h('div', { class: 'pv-detail', onclick: (e) => e.stopPropagation() }, left, right);
}

/** A session in a worktree that has none, with the options of the project's other sessions (the first Claude one). */
function startSessionIn(row) {
  const tab = activeTab();
  const members = projectMembers(tab.projectId);
  const like = members.find((s) => agentOf(s.command)) ?? members[0];
  const name = freeName(leafName(row.branch && !row.branch.startsWith('(') ? row.branch : row.path));
  addAndOpen({ directory: row.path, name, command: like ? ownOptionsOnly(like.command) : 'claude', project: tab.projectId === UNKNOWN ? '' : tab.projectId })
    .catch((e) => toast(e.message, true));
}

/**
 * Removing a worktree: its sessions go first (a process with the folder as its working directory would keep it), then
 * git removes it, never forced, so one with uncommitted changes is refused and left as it is. The branch stays.
 */
function confirmRemoveWorktree(row) {
  const tab = activeTab();
  const projectId = tab.projectId;
  const repo = row.repo;
  const busy = row.changes > 0;
  const error = h('div', { class: 'form-error', hidden: true });
  const remove = h('button', { class: 'btn danger primary', disabled: busy }, 'Remove worktree');
  remove.addEventListener('click', async () => {
    remove.disabled = true;
    remove.textContent = 'Removing…';
    try {
      for (const s of row.sessions) await request('removeSession', { id: s.id });
      await request('worktree.remove', { root: repo.root, path: row.path });
      closeModal();
      toast(`Removed the worktree at ${row.path}. The branch ${row.branch ?? ''} stays.`);
    } catch (e) {
      error.hidden = false;
      error.textContent = e.message;
      remove.disabled = false;
      remove.textContent = 'Remove worktree';
    }
    projectView.open.delete(normPath(row.path));
    const t = activeTab();
    if (isProjectTab(t) && t.projectId === projectId) loadProjectView(true);
  });
  openModal('Remove worktree', [
    h('div', {}, `Remove the worktree at ${row.path}?`),
    h('div', { class: 'dim' }, `The branch ${row.branch ?? ''} stays in the repository; only the folder goes.`),
    row.sessions.length > 0 && h('div', {}, `${row.sessions.length === 1 ? 'This session is' : 'These sessions are'} in it and will be removed first: ${row.sessions.map((s) => `"${s.name}"`).join(', ')}.`),
    busy && h('div', { class: 'form-error' }, `${row.changes} file${row.changes === 1 ? ' is' : 's are'} changed or new in this worktree and would be lost. Commit or undo ${row.changes === 1 ? 'it' : 'them'} first.`),
    error,
  ], [h('button', { class: 'btn', onclick: closeModal, 'data-autofocus': true }, 'Cancel'), remove]);
}

// ---- Markdown: enough for Claude's answers, built as DOM nodes (never HTML strings)

function renderMarkdown(src) {
  const lines = src.replace(/\r\n?/g, '\n').split('\n');
  const out = [];
  const fenceRe = /^\s*(`{3,}|~{3,})/;
  const listRe = /^(\s*)([-*+]|\d+[.)])\s+(.*)$/;
  const hrRe = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
  const headRe = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/;
  const tableSepRe = /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$/;
  const cells = (l) => l.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim());
  const isTable = (i) => lines[i].includes('|') && i + 1 < lines.length && tableSepRe.test(lines[i + 1]);
  const startsBlock = (i) => fenceRe.test(lines[i]) || headRe.test(lines[i]) || hrRe.test(lines[i]) ||
    listRe.test(lines[i]) || /^\s*>/.test(lines[i]) || isTable(i);

  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    let m;
    if (!line.trim()) { i++; continue; }
    if ((m = fenceRe.exec(line))) {
      const fence = m[1];
      const lang = line.trim().slice(fence.length).trim().split(/\s+/)[0];   // ```ts: panels colour it (colorizeCode)
      const code = [];
      for (i++; i < lines.length && !lines[i].trim().startsWith(fence); i++) code.push(lines[i]);
      i++;
      out.push(h('pre', { class: 'md-code' }, h('code', { 'data-lang': /^[\w#+.-]{1,30}$/.test(lang) ? lang : null }, code.join('\n'))));
    } else if ((m = headRe.exec(line))) {
      out.push(h('div', { class: `md-h md-h${m[1].length}` }, inline(m[2])));
      i++;
    } else if (hrRe.test(line)) {
      out.push(h('hr'));
      i++;
    } else if (/^\s*>/.test(line)) {
      const quoted = [];
      for (; i < lines.length && /^\s*>/.test(lines[i]); i++) quoted.push(lines[i].replace(/^\s*>\s?/, ''));
      out.push(h('blockquote', {}, renderMarkdown(quoted.join('\n'))));
    } else if (isTable(i)) {
      const head = cells(line);
      const rows = [];
      for (i += 2; i < lines.length && lines[i].includes('|') && lines[i].trim(); i++) rows.push(cells(lines[i]));
      out.push(h('table', {},
        h('thead', {}, h('tr', {}, head.map((c) => h('th', {}, inline(c))))),
        h('tbody', {}, rows.map((r) => h('tr', {}, head.map((_, k) => h('td', {}, inline(r[k] ?? ''))))))));
    } else if (listRe.test(line)) {
      const items = [];
      for (; i < lines.length; i++) {
        const lm = listRe.exec(lines[i]);
        if (lm) items.push({ indent: lm[1].length, ordered: /\d/.test(lm[2]), start: parseInt(lm[2], 10), text: lm[3] });
        else if (lines[i].trim() && /^\s{2,}/.test(lines[i])) items[items.length - 1].text += ' ' + lines[i].trim();
        else break;
      }
      out.push(listEl(items));
    } else {
      const para = [];
      for (; i < lines.length && lines[i].trim() && (para.length === 0 || !startsBlock(i)); i++) para.push(lines[i]);
      out.push(h('p', {}, para.map((l, k) => [k ? h('br') : null, inline(l.trim())])));
    }
  }
  return out;
}

/** Nests list items by indentation. */
function listEl(items) {
  const stack = [];
  let root = null;
  for (const it of items) {
    while (stack.length > 1 && it.indent < stack[stack.length - 1].indent) stack.pop();
    let cur = stack[stack.length - 1];
    if (!cur || it.indent > cur.indent + 1) {
      const list = h(it.ordered ? 'ol' : 'ul', { start: it.ordered && it.start !== 1 ? it.start : null });
      if (cur) (cur.el.lastElementChild || cur.el).append(list);
      else root = list;
      stack.push(cur = { indent: it.indent, el: list });
    }
    cur.el.append(h('li', {}, inline(it.text)));
  }
  return root;
}

const INLINE_RE = /(`+)(.+?)\1(?!`)|\*\*(.+?)\*\*|__(.+?)__|\*([^*\s](?:.*?[^*\s])?)\*|(?<![\w])_([^_\s](?:.*?[^_\s])?)_(?![\w])|\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)|(https?:\/\/[^\s<>()]+[^\s<>().,;:!?'"])/g;

function inline(text) {
  const out = [];
  const re = new RegExp(INLINE_RE.source, 'g');
  let last = 0, m;
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(text.slice(last, m.index));
    if (m[1]) out.push(h('code', { class: 'md-inline' }, m[2].replace(/^ (.+) $/, '$1')));
    else if (m[3] || m[4]) out.push(h('strong', {}, inline(m[3] || m[4])));
    else if (m[5] || m[6]) out.push(h('em', {}, inline(m[5] || m[6])));
    else if (m[7]) out.push(mdLink(m[8], inline(m[7])));
    else out.push(mdLink(m[9], m[9]));
    last = re.lastIndex;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

const mdLink = (url, kids) => h('a', {
  href: '#', title: url,
  onclick: (e) => { e.preventDefault(); request('openUrl', { uri: url }).catch((err) => toast(err.message, true)); },
}, kids);

// ------------------------------------------------------------------ Windows accent colour
// The host sends Windows' accent shades (lightest first, the accent itself at index 3) at start and whenever they change
// in Settings. Without them, or with the setting off, the app keeps its own purple.

/**
 * choice: 'windows' (follow Windows' accent), 'purple' (the app's own) or a #rrggbb. Someone who had turned the Windows
 * accent off before there was a choice gets the purple.
 */
const accent = {
  palette: null,
  choice: store.get('accentColour', store.get('useWindowsAccent', true) ? 'windows' : 'purple'),
};

/** Seven shades of one colour, lightest first and the colour itself at 3, like the palette Windows gives for its accent. */
function shadesOf(hex) {
  const rgb = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
  const mix = (to, t) => '#' + rgb.map((v) => Math.round(v + (to - v) * t).toString(16).padStart(2, '0')).join('');
  return [mix(255, .6), mix(255, .4), mix(255, .2), hex.toLowerCase(), mix(0, .15), mix(0, .35), mix(0, .55)];
}

/** WCAG contrast ratio of two #rrggbb colours (1 to 21). */
function contrast(a, b) {
  const lum = (hex) => {
    const [r, g, bl] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255)
      .map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * bl;
  };
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

function applyAccent() {
  const root = document.documentElement.style;
  const own = themeLook()?.accent;   // a theme's own accent, or the one a monochrome theme built
  if (own) {
    root.setProperty('--accent-strong', own.strong);
    root.setProperty('--accent', own.line);
    return;
  }
  const c = accent.choice;
  const p = c === 'windows' ? accent.palette : /^#[0-9a-f]{6}$/i.test(c) ? shadesOf(c) : null;   // null: the purple
  if (!p || p.length < 7) {
    root.removeProperty('--accent');
    root.removeProperty('--accent-strong');
    return;
  }
  // Fills carry white text (active tabs, buttons, menus): the accent, or the first darker shade white reads well on.
  const strong = [3, 4, 5, 6].map((i) => p[i]).find((c) => contrast(c, '#ffffff') >= 4.5) ?? p[6];
  // Lines, ticks and highlights: on the dark chrome a lighter shade, as Windows uses in dark mode; on the light chrome
  // the accent or a darker one.
  const line = isLight()
    ? [3, 4, 5, 6].map((i) => p[i]).find((c) => contrast(c, '#eeeeee') >= 4.5) ?? p[6]
    : [2, 1, 0].map((i) => p[i]).find((c) => contrast(c, '#1f1f1f') >= 4.5) ?? p[0];
  root.setProperty('--accent-strong', strong);
  root.setProperty('--accent', line);
}

// ------------------------------------------------------------------ updates
// The host checks the repository's latest GitHub release; an update shows as a button in the title bar. Installing
// downloads and checks the installer, closes the app, updates silently and reopens it.

const UPDATE_EVERY_MS = 6 * 60 * 60 * 1000;
const updates = { info: null, current: null, checking: false, installing: false, auto: store.get('autoUpdateCheck', true) };

async function checkForUpdates(manual = false) {
  if (updates.checking) return;
  updates.checking = true;
  try {
    const r = await request('update.check');
    updates.current = r.current;
    updates.info = r.available ? r : null;
    renderUpdateButton();
    if (manual) {
      if (r.available) showUpdateDialog();
      else toast(`Claude Terminals ${r.current} is the latest version.`);
    }
  } catch (e) {
    if (manual) toast(`Couldn't check for updates: ${e.message}`, true);
  } finally {
    updates.checking = false;
  }
}

function renderUpdateButton() {
  const b = $('#btnUpdate');
  b.hidden = !updates.info;
  if (updates.info) {
    b.replaceChildren(iconEl('down'), `Update to ${updates.info.version}`);
    b.title = `${updates.info.name} is available. Click for what's new.`;
  }
}

/** The release notes without their install instructions and checksum, which are for people downloading by hand. */
const updateNotes = (notes) => (notes || '').split(/^###\s*Install\b/m)[0].trim();

function showUpdateDialog() {
  const u = updates.info;
  if (!u) return;
  const running = state.sessions.filter((s) => s.running).length;
  const status = h('div', { class: 'update-status' });
  const install = h('button', { class: 'btn primary', hidden: !u.canInstall }, 'Update and restart');
  install.addEventListener('click', async () => {
    if (updates.installing) return;
    updates.installing = true;
    install.disabled = true;
    install.textContent = 'Downloading…';
    status.classList.remove('error');
    status.textContent = '';
    try {
      await request('update.install');
      install.textContent = 'Closing…';   // the host closes the app; setup takes over
    } catch (e) {
      status.classList.add('error');
      status.textContent = e.message;
      install.disabled = false;
      install.textContent = 'Update and restart';
    } finally {
      updates.installing = false;
    }
  });
  openModal(`${u.name} is available`, [
    h('div', { class: 'update-from dim' }, `You have ${updates.current}.`),
    h('div', { class: 'update-notes md' }, renderMarkdown(updateNotes(u.notes) || 'No release notes.')),
    h('div', { class: 'update-what' }, u.canInstall
      ? `Claude Terminals will close, install the update and reopen.${running ? ` ${running} running session${running === 1 ? '' : 's'} will restart and carry on in ${running === 1 ? 'its' : 'their'} conversation${running === 1 ? '' : 's'}.` : ''}`
      : 'This release can’t be installed automatically (it has no checksum). Download it from the release page.'),
    status,
  ], [
    h('button', { class: 'btn', onclick: () => request('openUrl', { uri: u.pageUrl }) }, 'Release page'),
    h('div', { class: 'spacer' }),
    h('button', { class: 'btn', onclick: closeModal }, 'Later'),
    install,
  ]);
}

/**
 * Puts the current theme on everything: the page's colours, the terminals, diffs, accent shades and the window's title
 * bar. When it changed (from `before`), running Claude sessions are offered a restart to pick up their new colours.
 */
function applyTheme(before = null) {
  const name = themeName();
  const look = themeLook();
  store.set('themeLook', look);   // so it paints at once on the next start
  markTheme(look);
  for (const s of state.sessions) s.term.options.theme = termTheme(look);
  if (window.monaco) monaco.editor.setTheme(diffTheme(look));
  applyAccent();
  for (const f of pluginFrames.values()) postPluginTheme(f.frame);
  // A theme's window and title bar take its own background; Light and Dark keep Windows' own.
  post({ type: 'theme', light: isLight(), background: look?.colors.chrome ?? null });
  if (before && before !== name) offerClaudeRestart();
}

/** Claude only reads its theme when it starts, so running sessions keep their old colours until they restart. */
function offerClaudeRestart() {
  const name = themeName();
  const stale = state.sessions.filter((s) => s.running && s.startedTheme && s.startedTheme !== name &&
    /^claude(\.exe)?(\s|$)/i.test(s.command.trim()) && !/(^|\s)(--settings|-p|--print)(\s|=|$)/.test(s.command));
  if (!stale.length) return;
  const n = stale.length;
  confirmDialog(`Update Claude's colours?`,
    `Claude in ${n} running session${n === 1 ? '' : 's'} still uses its ${name === 'light' ? 'dark' : 'light'} colours, ` +
    `which can be hard to read now. ${n === 1 ? 'It switches' : 'They switch'} when restarted: restart now? Each carries on ` +
    `in the conversation it's in; anything Claude is in the middle of is interrupted.`,
    `Restart ${n === 1 ? 'session' : `${n} sessions`}`, false,
    () => { for (const s of stale) restartSession(s, { resume: true }); });
}

function setThemePref(pref) {
  const before = themeName();
  theme.pref = pref;
  store.set('theme', pref);
  applyTheme(before);
}

// ------------------------------------------------------------------ notices
// Tips, offers and news along the top of the window, from notices.json on our site (read by the host, which
// leaves out what isn't for this version or today). One shows at a time; closing one hides it for good, and the next
// waits for the next check. Tips and offers can be turned off from the version menu; news can't.

const NOTICES_EVERY_MS = 30 * 60 * 1000;
const notices = { list: [], shown: null, waiting: false, dismissed: new Set(store.get('dismissedNotices', [])), tips: store.get('showTips', true) };
const NOTICE_KINDS = { tip: { icon: 'bulb', label: 'Tip' }, offer: { icon: 'gift', label: 'Offer' }, news: { icon: 'info', label: 'News' } };

async function checkNotices() {
  try {
    notices.list = await request('notices.check');
  } catch {
    return;   // offline, or the file is unreadable: keep what's showing
  }
  // Forget closed notices that have left the feed, so the list doesn't grow for ever.
  const ids = new Set(notices.list.map((n) => n.id));
  const kept = [...notices.dismissed].filter((id) => ids.has(id));
  if (kept.length !== notices.dismissed.size) { notices.dismissed = new Set(kept); store.set('dismissedNotices', kept); }
  notices.waiting = false;
  renderNotice();
}

// After one's closed, tips and offers wait for the next check (closing one shouldn't bring another); news doesn't.
const noticeAllowed = (n) => !notices.dismissed.has(n.id) && (n.kind === 'news' || (notices.tips && !notices.waiting));

function renderNotice() {
  // The one showing (or asked for again) stays until it's closed or leaves the feed; otherwise the first allowed.
  const n = notices.list.find((x) => x.id === notices.shown?.id && !notices.dismissed.has(x.id)) ?? notices.list.find(noticeAllowed);
  notices.shown = n ?? null;
  const bar = $('#noticeBar');
  bar.hidden = !n;
  if (!n) { bar.replaceChildren(); return; }
  const kind = NOTICE_KINDS[n.kind] ?? NOTICE_KINDS.tip;
  bar.dataset.kind = n.kind;
  // A named colour has its tone in the stylesheet; a #rrggbb of the notice's own is mixed with the theme there too.
  const own = n.color?.startsWith('#');
  bar.dataset.color = own ? 'own' : n.color || 'accent';
  bar.style.setProperty('--notice', own ? n.color : '');
  bar.replaceChildren(
    h('span', { class: 'notice-kind' }, iconEl(kind.icon), kind.label),
    h('span', { class: 'notice-text' }, n.text),
    n.linkUrl && h('a', { class: 'notice-link', href: '#', title: n.linkUrl,
      onclick: (e) => { e.preventDefault(); request('openUrl', { uri: n.linkUrl }); } }, n.linkLabel, iconEl('chevR')),
    h('button', { class: 'tb-btn notice-close', title: "Close (this one won't show again)", 'aria-label': 'Close', onclick: () => dismissNotice(n) }, iconEl('close')),
  );
  bar.oncontextmenu = (e) => {
    e.preventDefault();
    showMenu(e.clientX, e.clientY, [
      { label: "Don't show this again", action: () => dismissNotice(n) },
      { label: 'All tips and offers…', action: showNoticesDialog },
      n.kind !== 'news' && { label: 'Show tips and offers', checked: true, action: () => setShowTips(false) },
    ]);
  };
}

function dismissNotice(n) {
  notices.dismissed.add(n.id);
  store.set('dismissedNotices', [...notices.dismissed]);
  notices.waiting = true;
  renderNotice();
}

function setShowTips(on) {
  notices.tips = on;
  store.set('showTips', on);
  if (notices.shown?.kind !== 'news') notices.shown = null;
  if (on) checkNotices(); else renderNotice();
}

/** Every notice in the feed today, closed or not, in full: one closed by mistake can be shown again. */
function showNoticesDialog() {
  const rows = notices.list.map((n) => {
    const kind = NOTICE_KINDS[n.kind] ?? NOTICE_KINDS.tip;
    const closed = notices.dismissed.has(n.id);
    const own = n.color?.startsWith('#');
    return h('div', { class: 'notice-row' + (closed ? ' closed' : ''), 'data-color': own ? 'own' : n.color || 'accent', style: own ? `--notice:${n.color}` : null },
      h('div', { class: 'notice-row-head' },
        h('span', { class: 'notice-kind' }, iconEl(kind.icon), kind.label),
        closed && h('span', { class: 'dim' }, 'Closed'),
        notices.shown?.id === n.id && h('span', { class: 'dim' }, 'Showing'),
        h('span', { class: 'spacer' }),
        closed && h('button', { class: 'btn', onclick: () => {
          notices.dismissed.delete(n.id);
          store.set('dismissedNotices', [...notices.dismissed]);
          notices.shown = n;
          renderNotice();
          showNoticesDialog();
        } }, 'Show again')),
      h('div', { class: 'notice-row-text' }, n.text),
      n.linkUrl && h('a', { class: 'notice-link', href: '#', title: n.linkUrl,
        onclick: (e) => { e.preventDefault(); request('openUrl', { uri: n.linkUrl }); } }, n.linkLabel, iconEl('chevR')));
  });
  openModal('Tips and offers', [
    rows.length ? h('div', { class: 'notice-rows' }, rows) : h('div', { class: 'dim' }, 'There are no tips, offers or news at the moment.'),
  ], [
    h('label', { class: 'check-field' }, h('input', { type: 'checkbox', checked: notices.tips, onchange: (e) => setShowTips(e.target.checked) }), 'Show tips and offers'),
    h('div', { class: 'spacer' }),
    h('button', { class: 'btn primary', onclick: closeModal }, 'Close'),
  ]);
}

/** The version number's menu: updates, release notes and the tips along the top. */
function versionMenu(x, y) {
  showMenu(x, y, [
    { label: 'Check for updates', action: () => checkForUpdates(true) },
    { label: 'Check for updates automatically', checked: updates.auto, action: () => {
      updates.auto = !updates.auto;
      store.set('autoUpdateCheck', updates.auto);
      if (updates.auto) checkForUpdates();
    } },
    { label: 'Show tips and offers', checked: notices.tips, action: () => setShowTips(!notices.tips) },
    { label: 'Tips and offers…', action: showNoticesDialog },
    '-',
    { label: `Claude Terminals ${updates.current ?? ''}`.trim() + ' · release notes',
      action: () => request('openUrl', { uri: 'https://github.com/davellamachant/ClaudeTerminals/releases' }) },
  ]);
}

/** The theme menu (next to the version number): System, Light, Dark and the themes, and the accent colour. */
function themeMenu(x, y) {
  const c = accent.choice;
  const colour = c === 'windows' ? accent.palette?.[3] ?? '#7160e8' : c === 'purple' ? '#7160e8' : c;
  const own = currentTheme();   // a theme with an accent of its own (not a monochrome one: that's built from yours)
  showMenu(x, y, [
    { header: 'Theme' },
    { label: 'System (follows Windows)', checked: theme.pref === 'system', action: () => setThemePref('system') },
    { label: 'Light', checked: theme.pref === 'light', action: () => setThemePref('light') },
    { label: 'Dark', checked: theme.pref === 'dark', action: () => setThemePref('dark') },
    ...themes.list.map((t) => ({ label: t.name, checked: theme.pref === t.id, kbd: t.builtIn ? '' : 'Yours', action: () => setThemePref(t.id) })),
    themes.problems.length > 0 && { label: `${themes.problems.length} theme${themes.problems.length === 1 ? '' : 's'} not loaded`, kbd: 'Why?', action: () => toast(themes.problems.join('\n'), true) },
    '~',
    { label: 'Save as a new theme…', action: showSaveThemeDialog },
    { label: 'Open the themes folder', action: () => request('themes.openFolder') },
    '-',
    { header: 'Accent colour' },
    own?.accent
      ? { label: [h('span', { class: 'accent-dot', style: `background:${own.accent.line}` }), `${own.name}'s own`],
        action: () => toast(`${own.name} has an accent colour of its own. Choose another theme to change it.`) }
      : { label: [h('span', { class: 'accent-dot', style: `background:${colour}` }), c === 'windows' ? 'Windows accent' : c === 'purple' ? 'Original purple' : 'Custom'],
        kbd: 'Change…', action: showAccentDialog },
    '-',
    { header: 'Status dots' },
    { label: DOT_STYLES.find((d) => d.id === dotStyle.id).name, kbd: 'Change…', action: showDotStyleDialog },
    '-',
    { header: 'Plugins' },
    ...plugins.list.flatMap((p) => [
      { label: p.title, kbd: p.placement === 'panel' ? 'Side panel' : '', action: () => openPlugin(p.id) },
      ...p.menu.filter((m) => m.where.includes('palette')).map((m) => ({ label: [h('span', { class: 'menu-indent' }), m.label], action: () => openPlugin(p.id, m.command, { menu: 'palette' }) })),
      p.toolbar && hiddenPluginButtons.has(p.id) && { label: [h('span', { class: 'menu-indent' }), 'Show its title bar button'], action: () => setPluginButton(p.id, true) },
    ]),
    plugins.problems.length > 0 && { label: `${plugins.problems.length} couldn't be loaded`, kbd: 'Why?', action: () => toast(plugins.problems.join('\n'), true) },
    { label: plugins.list.length ? 'Open the plugins folder' : 'Add a plugin: open the plugins folder', action: () => request('plugins.openFolder') },
  ]);
}

/**
 * Save as a new theme: the theme on screen, whichever it is, written to the themes folder as a file with every colour, to
 * start a theme of your own from. It's read from what's showing, so it's always complete and matches what you see; a
 * monochrome theme is saved with the colours your accent gives it now.
 */
function showSaveThemeDialog() {
  const look = themeLook();
  const now = look?.name ?? (isLight() ? 'Light' : 'Dark');
  const name = h('input', { type: 'text', value: `My ${now}`, maxlength: '40', spellcheck: 'false' });
  const save = async () => {
    const title = name.value.trim();
    if (!title) { name.focus(); return; }
    try {
      const id = await request('themes.save', { theme: themeSnapshot(title) });
      closeModal();
      await loadThemes();
      setThemePref(id);
      toast(`Saved "${title}" as ${id}.json in your themes folder.`);
    }
    catch (e) { toast(e.message, true); }
  };
  name.addEventListener('keydown', (e) => { if (e.key === 'Enter') save(); });
  openModal('Save as a new theme', [
    field('Name', name, `A copy of ${now} as it looks now, with every colour written out, in your themes folder. Change the file to make the theme your own; it shows in this menu.`),
  ], [
    h('button', { class: 'btn', onclick: closeModal }, 'Cancel'),
    h('button', { class: 'btn primary', onclick: save }, 'Save'),
  ]);
  setTimeout(() => name.select(), 30);
}

/** The theme on screen as a theme file (see ThemeCatalog): every colour the page, terminals and diffs are using. */
function themeSnapshot(title) {
  const look = themeLook();
  const css = getComputedStyle(document.documentElement);
  const value = (name) => css.getPropertyValue('--' + name).trim();
  const terminal = termTheme(look);
  const editor = MONACO_COLORS[isLight() ? 'vs2026-light' : 'vs2026-dark'];
  const diff = { ...Object.fromEntries(Object.entries(DIFF_COLOR_IDS).filter(([, id]) => editor[id]).map(([name, id]) => [name, editor[id]])), ...look?.diff };
  return {
    name: title,
    base: themeName(),
    ...(look?.font ? { font: look.font } : {}),
    ...(look?.glow ? { glow: true } : {}),
    accent: { line: value('accent'), strong: value('accent-strong') },
    colors: Object.fromEntries(themes.names.colors.map((n) => [n, value(n)]).filter(([, v]) => v)),
    terminal: Object.fromEntries(themes.names.terminal.filter((n) => terminal[n]).map((n) => [n, terminal[n]])),
    diff: Object.fromEntries(themes.names.diff.filter((n) => diff[n]).map((n) => [n, diff[n]])),
  };
}

/**
 * Status dots: each style as a card showing every state moving, picked like the accent colour: each click previews it
 * on the whole app, and Cancel puts back what was there. The cards sit outside #app, so each shows its own style.
 */
function showDotStyleDialog() {
  const before = dotStyle.id;
  const states = [['running', 'Idle'], ['working', 'Working'], ['attention', 'Waiting for you'], ['asking', 'Needs your answer'], ['exited', 'Not running']];
  const cards = DOT_STYLES.map((d) => h('button', { class: 'dot-style', 'data-dots': d.id, onclick: () => pick(d.id) },
    h('span', { class: 'dot-style-name' }, d.name),
    h('span', { class: 'dot-style-states' }, states.map(([cls, label]) => h('span', { class: 'dot-style-state' }, h('span', { class: 'dot ' + cls }), label))),
    h('span', { class: 'dot-style-about' }, d.about)));

  function pick(id) {
    dotStyle.id = id;
    applyDotStyle();   // preview on the whole app
    for (const c of cards) c.classList.toggle('selected', c.dataset.dots === id);
  }
  let saved = false;
  openModal('Status dots', [
    h('div', { class: 'dot-style-grid' }, cards),
    h('div', { class: 'field help' }, 'With Windows’ animation effects turned off, the dots stand still and their shapes tell the states apart.'),
  ], [
    h('button', { class: 'btn', onclick: closeModal }, 'Cancel'),
    h('button', { class: 'btn primary', onclick: () => { saved = true; store.set('dotStyle', dotStyle.id); closeModal(); } }, 'OK'),
  ]);
  $('#modalHost .modal').classList.add('wide');
  // Cancel, Esc or a click outside: put back the style from before.
  onModalClose = () => { if (!saved) { dotStyle.id = before; applyDotStyle(); } };
  pick(before);
}

/**
 * Accent colour: Windows' (following Settings), the original purple, a swatch or a custom colour. Each click previews
 * on the whole app; Cancel puts back what was there.
 */
function showAccentDialog() {
  const before = accent.choice;
  const windowsColour = accent.palette?.[3];
  const custom = h('input', { type: 'color', class: 'swatch-custom-input', tabindex: '-1' });
  const customBtn = h('button', { class: 'swatch swatch-custom', title: 'Custom colour…', onclick: () => custom.click() }, '…');
  const windowsBtn = h('button', {
    class: 'swatch-auto', disabled: !windowsColour,
    title: windowsColour ? 'Follow the accent colour in Windows Settings, and change with it' : "Windows didn't report an accent colour",
    onclick: () => pick('windows'),
  }, windowsColour ? h('span', { class: 'accent-dot', style: `background:${windowsColour}` }) : null, 'Windows accent');
  const purpleBtn = h('button', { class: 'swatch-auto', title: "The app's own colour", onclick: () => pick('purple') },
    h('span', { class: 'accent-dot', style: 'background:#7160e8' }), 'Original purple');
  const swatches = PROJECT_COLORS.map((c) => h('button', { class: 'swatch', style: `--swatch:${c}`, title: c, onclick: () => pick(c) }));
  const note = h('div', { class: 'field help' });
  custom.addEventListener('input', () => pick(custom.value));

  function pick(c) {
    accent.choice = c;
    accentChanged();   // preview on the whole app
    windowsBtn.classList.toggle('selected', c === 'windows');
    purpleBtn.classList.toggle('selected', c === 'purple');
    for (const sw of swatches) sw.classList.toggle('selected', sw.title === c);
    const isCustom = /^#/.test(c) && !PROJECT_COLORS.includes(c);
    customBtn.classList.toggle('selected', isCustom);
    customBtn.style.setProperty('--swatch', isCustom ? c : 'transparent');
    if (/^#/.test(c)) custom.value = c;
    note.textContent = c === 'windows'
      ? 'Changes when you change the accent colour in Windows Settings.'
      : 'Very light or dark colours are shaded where needed, so text on them stays readable in both themes.';
  }
  let saved = false;
  openModal('Accent colour', [
    h('div', { class: 'swatch-row' }, windowsBtn, purpleBtn),
    h('div', { class: 'swatch-row' }, swatches, customBtn, custom),
    note,
  ], [
    h('button', { class: 'btn', onclick: closeModal }, 'Cancel'),
    h('button', { class: 'btn primary', onclick: () => { saved = true; store.set('accentColour', accent.choice); closeModal(); } }, 'OK'),
  ]);
  // Cancel, Esc or a click outside: put back the colour from before.
  onModalClose = () => { if (!saved) { accent.choice = before; accentChanged(); } };
  pick(before);
}

$('#btnUpdate').addEventListener('click', showUpdateDialog);
$('#btnTheme').innerHTML = icon('theme');
// The plugins folder is read again each time, so a plugin added since is on the menu.
$('#btnTheme').addEventListener('click', async (e) => { const r = e.currentTarget.getBoundingClientRect(); await Promise.all([loadPlugins(), loadThemes()]); themeMenu(r.right - 260, r.bottom + 2); });
request('app.version').then((v) => {
  updates.current ??= v;
  $('#appVersion').replaceChildren(`v${v}`, iconEl('chevD'));   // the chevron says it opens a menu
}).catch(() => {});
$('#appVersion').addEventListener('click', (e) => { const r = e.currentTarget.getBoundingClientRect(); versionMenu(r.right - 260, r.bottom + 2); });
// Soon after start (not during it), then every few hours; never when turned off.
setTimeout(() => { if (updates.auto) checkForUpdates(); }, 15_000);
setInterval(() => { if (updates.auto) checkForUpdates(); }, UPDATE_EVERY_MS);
setTimeout(checkNotices, 5_000);
setInterval(checkNotices, NOTICES_EVERY_MS);

// ------------------------------------------------------------------ scrolling tab rows

/**
 * Gives a horizontally overflowing row ◀ ▶ buttons (shown only when there is more to see in that direction) and lets
 * the mouse wheel scroll it sideways. The row is wrapped in place; its own contents can be re-rendered freely.
 */
function makeScrollable(row, extraClass = '') {
  const wrap = h('div', { class: 'tab-scroller ' + extraClass });
  const left = h('button', { class: 'scroll-btn', title: 'Scroll left', tabindex: '-1', onclick: () => step(-1) }, iconEl('chevL'));
  const right = h('button', { class: 'scroll-btn', title: 'Scroll right', tabindex: '-1', onclick: () => step(1) }, iconEl('chevR'));
  row.replaceWith(wrap);
  wrap.append(left, row, right);

  function step(direction) {
    row.scrollBy({ left: direction * Math.max(80, row.clientWidth * 0.8), behavior: 'smooth' });
  }
  function update() {
    const max = row.scrollWidth - row.clientWidth;
    left.hidden = row.scrollLeft <= 1;
    right.hidden = row.scrollLeft >= max - 1;
  }
  // A vertical wheel scrolls the row sideways; a horizontal one (touchpad) already does.
  row.addEventListener('wheel', (e) => {
    if (row.scrollWidth <= row.clientWidth || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
    e.preventDefault();
    row.scrollLeft += e.deltaY;
  }, { passive: false });
  row.addEventListener('scroll', update, { passive: true });
  new ResizeObserver(update).observe(row);
  new MutationObserver(() => requestAnimationFrame(update)).observe(row, { childList: true, subtree: true });
  update();
}

/**
 * Drag-to-reorder for a tab row. Items matching `item` carry `data-id`; `canDrag(id)` vetoes pinned ones. While
 * dragging, the item under the pointer shows which side the drop lands on, and the row scrolls near its edges.
 * `onDrop(draggedId, targetId, before)` is told where to put it. Listeners sit on the row itself, so its contents can
 * be re-rendered freely. With `outside`, a tab can also be dragged out below the row: `outside.over(id, x, y)` shows
 * where it would land and returns that place (or null), `outside.over(null)` hides it, and `outside.drop(id, place)`
 * puts it there. `fixed(id)` items stay where they are: only dragged out, and never a place to drop beside.
 *
 * Built on pointer events rather than HTML5 drag and drop: in WebView2 an HTML5 drag runs through the operating
 * system's drag loop, which AllowExternalDrop = false (set so dropped files can't navigate the page) also blocks.
 */
function enableTabDrag(row, { item, canDrag = () => true, onDrop, outside = null, fixed = () => false }) {
  const THRESHOLD = 8;   // pixels the pointer must move before a press becomes a drag (a hurried click can move ~5)
  let press = null;      // { id, el, x, pointerId } from pointerdown until pointerup
  let dragging = false;
  let place = null;      // where outside.over says a drop below the row lands

  const clear = () => row.querySelectorAll('.drop-before, .drop-after').forEach((el) => el.classList.remove('drop-before', 'drop-after'));

  /** The item nearest the pointer (other than the dragged one), and which side of it the drop lands on. */
  const targetAt = (x) => {
    if (fixed(press.id)) return null;
    let best = null;
    for (const el of row.querySelectorAll(item)) {
      if (el.dataset.id === press.id || fixed(el.dataset.id)) continue;
      const r = el.getBoundingClientRect();
      const d = x < r.left ? r.left - x : x > r.right ? x - r.right : 0;
      if (!best || d < best.d) best = { el, before: x < r.left + r.width / 2, d };
    }
    return best;
  };

  const finish = () => {
    clear();
    if (place != null) outside.over(null);
    place = null;
    press?.el.classList.remove('dragging');
    document.body.classList.remove('tab-dragging');
    press = null;
    dragging = false;
  };

  row.addEventListener('pointerdown', (e) => {
    if (dragging) finish();   // a drag whose release never arrived must not swallow this new press
    press = null;
    if (e.button !== 0) return;
    const el = e.target.closest(item);
    if (!el || !row.contains(el) || !canDrag(el.dataset.id) || e.target.closest('.tab-close, .icon-btn')) return;
    press = { id: el.dataset.id, el, x: e.clientX, y: e.clientY, pointerId: e.pointerId };
  });

  row.addEventListener('pointermove', (e) => {
    if (!press || e.pointerId !== press.pointerId) return;
    // Only a held button drags. If its release was missed (let go outside the window, say), forget the press rather
    // than letting plain mouse movement start a drag that eats the next click.
    if ((e.buttons & 1) === 0) { if (dragging) finish(); else press = null; return; }
    if (!dragging) {
      // Along the row only, unless a tab can be dragged out of it: then down counts too (the row loses the pointer once it's
      // out, so a drag must start before then).
      const moved = outside ? Math.hypot(e.clientX - press.x, e.clientY - press.y) : Math.abs(e.clientX - press.x);
      if (moved < THRESHOLD) return;
      dragging = true;
      row.setPointerCapture(e.pointerId);   // keep receiving moves even when the pointer leaves the row
      press.el.classList.add('dragging');
      document.body.classList.add('tab-dragging');
    }
    const r = row.getBoundingClientRect();
    if (outside) {
      place = e.clientY > r.bottom + 6 ? outside.over(press.id, e.clientX, e.clientY) : (place != null && outside.over(null), null);
      if (place != null) { clear(); return; }
    }
    if (e.clientX < r.left + 30) row.scrollLeft -= 12;
    else if (e.clientX > r.right - 30) row.scrollLeft += 12;
    const t = targetAt(e.clientX);
    clear();
    if (t) t.el.classList.add(t.before ? 'drop-before' : 'drop-after');
  });

  row.addEventListener('pointerup', (e) => {
    if (!press || e.pointerId !== press.pointerId) return;
    if (!dragging) { press = null; return; }   // just a click: let it through
    // Let go back over the tab it started on: nothing would move, so treat it as the click it probably was.
    const own = press.el.getBoundingClientRect();
    if (e.clientX >= own.left && e.clientX <= own.right && e.clientY >= own.top && e.clientY <= own.bottom) {
      // The row captured the pointer, so the browser aims this click at the row, not the tab: click what's under the
      // pointer ourselves (the tab label, a project's checkbox or name…).
      const under = document.elementFromPoint(e.clientX, e.clientY);
      finish();
      if (under && row.contains(under)) under.click();
      return;
    }
    const t = targetAt(e.clientX);
    const id = press.id;
    const out = place;
    finish();
    // The click that follows this pointerup would also switch tabs; swallow it.
    const swallow = (c) => { c.stopPropagation(); c.preventDefault(); };
    window.addEventListener('click', swallow, { capture: true, once: true });
    setTimeout(() => window.removeEventListener('click', swallow, { capture: true }), 0);
    if (out != null) outside.drop(id, out);
    else if (t) onDrop(id, t.el.dataset.id, t.before);
  });

  row.addEventListener('pointercancel', finish);
  row.addEventListener('lostpointercapture', () => { if (dragging) finish(); });
  window.addEventListener('blur', () => { if (press) finish(); });   // switching windows mid-press loses its release
  // Escape cancels a drag in progress (capture phase, before the terminal or other shortcuts see it).
  window.addEventListener('keydown', (e) => {
    if (dragging && e.key === 'Escape') { e.stopPropagation(); e.preventDefault(); finish(); }
  }, true);
  // Images and links inside a tab must not start the browser's own drag instead.
  row.addEventListener('dragstart', (e) => e.preventDefault());
}

/** Scrolls a row so its active tab is fully visible (it may sit behind a scroll button otherwise). */
function revealActive(row, selector) {
  const go = () => row.querySelector(selector)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  go();
  // Scrolling can make a ◀ ▶ button appear, which narrows the row and covers part of the tab again: repeat once the
  // buttons have settled.
  requestAnimationFrame(() => requestAnimationFrame(go));
}

// ------------------------------------------------------------------ plugins

/**
 * Plugins: web pages of their own (see PluginCatalog), opened from the palette menu, each in a document tab or, with
 * "placement": "panel" in its plugin.json, a tab in the side panel. A plugin runs in a sandboxed frame on its own origin
 * (https://<id>.plugin.local/), so it can't reach the app, its sessions, files or terminals, or another plugin.
 *
 * Messages, each { type: 'claude-terminals:<name>', … }. To a plugin: theme { theme: 'light' | 'dark' } (also ?theme= on
 * its address) whenever it changes, and command { command, context } when one of its menu items (or its title bar
 * button, see renderPluginButtons) is picked, context
 * saying what was clicked (see pluginMenuItems): a name and folder to go on, not a way into the session. From a plugin, about
 * its own tab and nothing else: badge { count } or { dot: true } ({ count: 0 } clears it), and title { title } (null
 * puts its name back). Anything else it sends is ignored.
 */
const plugins = { list: [], problems: [], folder: null, loaded: false };
const pluginById = (id) => plugins.list.find((p) => p.id === id);
const pluginKey = (id) => 'plugin|' + id;   // its document tab
const isPluginTab = (t) => t?.kind === 'plugin';
// Its side panel tab (function declarations: the dock's saved layout is read before this part of the script runs).
function isPluginDockTab(t) { return t.startsWith('plugin:'); }
function pluginOfTab(t) { return t.slice('plugin:'.length); }

/** Each plugin with a page up: id → { frame, ready, queue (messages for when it has loaded), badge, title }. */
const pluginFrames = new Map();

/** Reads the plugins folder again (a plugin added since shows up without restarting). */
async function loadPlugins() {
  try {
    const r = await request('plugins.list');
    Object.assign(plugins, { list: r.plugins, problems: r.problems, folder: r.folder, loaded: true });
    renderPluginButtons();
  }
  catch (e) { console.error('The plugins could not be read', e); }
}

/** Opens a plugin where it shows; a command from one of its menu items reaches it once its page has loaded. */
function openPlugin(id, command = null, context = null) {
  const p = pluginById(id);
  if (!p) return;
  if (command != null) sendToPlugin(p, { type: 'claude-terminals:command', command, context });
  if (p.placement === 'panel') {
    const t = 'plugin:' + id;
    if (!groupOfTab(t)) dock.groups[dock.groups.length - 1].tabs.push(t);
    if (!state.gitVisible) { state.gitVisible = true; store.set('gitVisible', true); applyGitVisibility(); }
    activateTab(groupOfTab(t), t);
    return;
  }
  const key = pluginKey(id);
  if (!docs.tabs.some((t) => t.key === key)) docs.tabs.push({ key, kind: 'plugin', pluginId: id, preview: false });
  activateDoc(key);
}

const pluginSessionContext = (s) => ({ sessionId: s.id, session: s.name, directory: s.directory, project: groupOf(s) === UNKNOWN ? null : projectName(groupOf(s)) });

/**
 * The items plugins add to a menu ('project', 'session' or 'sessionTab'), under a Plugins heading. Picking one opens the
 * plugin and sends it the item's command with what was clicked: { menu, project } for a project (its name; null for
 * sessions with no project), and { menu, sessionId, session, directory, project } for a session (its id, name, working
 * folder and project's name). That's all a plugin learns: the id names the session, it doesn't open a way into it.
 */
function pluginMenuItems(where, context) {
  const items = plugins.list.flatMap((p) => p.menu.filter((m) => m.where.includes(where))
    .map((m) => ({ label: m.label, kbd: p.title, action: () => openPlugin(p.id, m.command, { menu: where, ...context }) })));
  return items.length ? ['-', { header: 'Plugins' }, ...items] : [];
}

/**
 * The plugin's frame, made on first use and kept until its tab closes, so switching tabs doesn't reload it. (Moving it
 * to another place in the page does: dragging its side panel tab to another group starts it again.)
 */
function pluginFrame(p) {
  let f = pluginFrames.get(p.id);
  if (f) return f;
  const url = new URL(p.url);
  url.searchParams.set('theme', isLight() ? 'light' : 'dark');
  // No allow-same-origin: the page gets an opaque origin, so it can't reach this page, and has no storage to share.
  const frame = h('iframe', { class: 'plugin-frame', sandbox: 'allow-scripts allow-forms allow-modals', referrerpolicy: 'no-referrer', title: p.title, src: url.href });
  f = { frame, ready: false, queue: [], badge: null, title: null };
  const entry = f;
  frame.addEventListener('load', () => {
    entry.ready = true;
    postPluginTheme(frame);
    for (const m of entry.queue.splice(0)) frame.contentWindow?.postMessage(m, '*');
  });
  pluginFrames.set(p.id, f);
  return f;
}

function sendToPlugin(p, message) {
  const f = pluginFrame(p);
  if (f.ready) f.frame.contentWindow?.postMessage(message, '*');
  else f.queue.push(message);
}

const postPluginTheme = (frame) => frame.contentWindow?.postMessage({ type: 'claude-terminals:theme', theme: isLight() ? 'light' : 'dark' }, '*');

/** Its page goes when its tab closes; so do the caption and badge it set, which start afresh next time. */
function closePluginFrame(id) {
  pluginFrames.get(id)?.frame.remove();
  pluginFrames.delete(id);
}

/** The plugin's tab caption: what it set, or its title. */
const pluginCaption = (id) => pluginFrames.get(id)?.title ?? pluginById(id)?.title ?? id;

/** The count or dot it put on its tab, if any. */
function pluginBadge(id, cls = 'tw-badge') {
  const b = pluginFrames.get(id)?.badge;
  if (!b) return null;
  return b.dot ? h('span', { class: 'tw-dot', 'aria-label': 'something new' }) : h('span', { class: cls }, b.count > 99 ? '99+' : String(b.count));
}

function showPlugin(tab) {
  const p = pluginById(tab.pluginId);
  if (!p) { toast(`The plugin "${tab.pluginId}" isn't in the plugins folder any more.`, true); closeDoc(tab.key); return; }
  const host = $('#pluginDoc');
  const { frame } = pluginFrame(p);
  if (frame.parentNode !== host) host.append(frame);
  for (const f of host.children) f.hidden = f !== frame;
  frame.focus();
}

/** A side panel plugin, open in its group. */
function showDockPlugin(g, t) {
  const { frame } = pluginFrame(pluginById(pluginOfTab(t)));
  if (frame.parentNode !== g.frames) g.frames.append(frame);
  for (const f of g.frames.children) f.hidden = f !== frame;
}

function closeDockPlugin(t) {
  const g = groupOfTab(t);
  if (!g) return;
  g.tabs.splice(g.tabs.indexOf(t), 1);
  if (!g.tabs.length) removeGroup(g);
  closePluginFrame(pluginOfTab(t));
  saveDock();
  renderGit();
}

/** Redraws a plugin's tab, wherever it is, and its title bar button, after it changed its caption or badge. */
function renderPluginTab(id) {
  if (pluginById(id)?.toolbar) renderPluginButtons();
  if (docs.tabs.some((t) => t.key === pluginKey(id))) renderDocTabs();
  const g = groupOfTab('plugin:' + id);
  if (g && state.gitVisible && !g.el?.hidden) renderDockTabs(g, groupTabs(g), shownActive(g));
}

// ---- title bar buttons

/** Plugins whose title bar button you hid (right-click it › Hide this button); the palette menu brings it back. */
const hiddenPluginButtons = new Set(store.get('hiddenPluginButtons', []));

function setPluginButton(id, shown) {
  if (shown) hiddenPluginButtons.delete(id); else hiddenPluginButtons.add(id);
  store.set('hiddenPluginButtons', [...hiddenPluginButtons]);
  renderPluginButtons();
}

/**
 * Each plugin's button, after New Session and Git Changes, set apart. Only an icon, its tooltip starting with the
 * plugin's name, so it can't pass for one of the app's own (such as Update). Clicking it opens the plugin and sends its
 * command, if it has one, with the open session as context ({ menu: 'toolbar', sessionId, session, directory, project },
 * or just { menu: 'toolbar' } with none open). It shows the plugin's badge too, while the plugin is open.
 */
function renderPluginButtons() {
  fill($('#pluginButtons'), plugins.list.filter((p) => p.toolbar && !hiddenPluginButtons.has(p.id)).map((p) => {
    const tip = `${p.title} (plugin)${p.toolbar.tooltip ? ': ' + p.toolbar.tooltip : ''}`;
    return h('button', {
      class: 'tb-btn plugin-btn', title: tip, 'aria-label': tip,
      onclick: () => {
        const s = state.byId.get(state.expandedId);
        openPlugin(p.id, p.toolbar.command, { menu: 'toolbar', ...(s ? pluginSessionContext(s) : {}) });
      },
      oncontextmenu: (e) => { e.preventDefault(); showMenu(e.clientX, e.clientY, [{ header: p.title }, { label: 'Hide this button', action: () => setPluginButton(p.id, false) }]); },
    }, h('img', { src: p.toolbar.icon, alt: '', draggable: 'false' }), pluginBadge(p.id, 'plugin-btn-count'));
  }));
}

// What a plugin asks for its own tab. Only its own frame is listened to, so it can't speak for another plugin.
addEventListener('message', (e) => {
  const id = [...pluginFrames].find(([, f]) => f.frame.contentWindow === e.source)?.[0];
  const m = e.data;
  if (!id || typeof m?.type !== 'string') return;
  const f = pluginFrames.get(id);
  if (m.type === 'claude-terminals:badge') {
    const n = Math.floor(Number(m.count));
    f.badge = n > 0 ? { count: Math.min(n, 999) } : m.dot === true ? { dot: true } : null;
  } else if (m.type === 'claude-terminals:title') {
    const t = typeof m.title === 'string' ? m.title.replace(/\s+/g, ' ').trim().slice(0, 60) : '';
    f.title = t || null;
  } else return;
  renderPluginTab(id);
});

// Side panel plugins kept from last time show once the folder has been read.
loadPlugins().then(() => renderGit());
loadThemes();

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

makeScrollable($('#projectTabs'));
makeScrollable($('#sessionTabs'));
makeScrollable($('#docTabs'), 'doc');

/** Moves `id` before or after `targetId` in a list of ids. */
function moveId(ids, id, targetId, before) {
  const rest = ids.filter((x) => x !== id);
  const at = rest.indexOf(targetId);
  rest.splice(before ? at : at + 1, 0, id);
  return rest;
}

// Session tabs: dropping into another project's group moves the session into that project, at that spot.
enableTabDrag($('#sessionTabs'), {
  item: '.session-tab',
  onDrop: (id, targetId, before) => {
    const s = state.byId.get(id), target = state.byId.get(targetId);
    const ids = moveId(state.sessions.map((x) => x.id), id, targetId, before);
    state.sessions = ids.map((x) => state.byId.get(x));
    post({ type: 'reorder', ids });
    if (groupOf(s) !== groupOf(target)) {
      s.project = projectOf(target)?.id ?? null;   // shown at once; the host's reply confirms it
      request('updateSession', { id, project: s.project ?? '' }).catch((e) => toast(e.message, true));
    }
    applyProjectFilter();
  },
});

// Project tabs; Unknown isn't draggable and stays last, so a drop beside it goes at the end of the real projects.
enableTabDrag($('#projectTabs'), {
  item: '.project-tab',
  canDrag: (id) => id !== UNKNOWN,
  onDrop: (id, targetId, before) => {
    const ids = state.projects.map((p) => p.id);
    const ordered = targetId === UNKNOWN ? moveId(ids, id, ids[ids.length - 1], false) : moveId(ids, id, targetId, before);
    request('projects.reorder', { ids: ordered }).catch((e) => toast(e.message, true));
  },
});

// Diff tabs. The Terminal tab stays first, but can be dragged to a side of the area like them.
enableTabDrag($('#docTabs'), {
  item: '.doc-tab',
  fixed: (key) => key === 'terminal',
  onDrop: (key, targetKey, before) => {
    const order = moveId(docs.tabs.map((t) => t.key), key, targetKey, before);
    docs.tabs.sort((a, b) => order.indexOf(a.key) - order.indexOf(b.key));
    renderDocTabs();
  },
  outside: { over: dockHint, drop: (key, dir) => (key === 'terminal' ? splitTerminal(dir) : splitDoc(key, dir || null)) },
});

/**
 * Where a tab dragged into the area would go: near an edge puts it on that side of the terminal, anywhere else on its
 * own ('' then). Shows that place, or hides it for key null. A Project tab only goes on its own.
 */
function dockHint(key, x, y) {
  const hint = $('#dockHint');
  const r = $('#docArea').getBoundingClientRect();
  const term = key === 'terminal';
  const t = term ? terminalPartner() : key && docs.tabs.find((d) => d.key === key);
  if (!t || x < r.left || x > r.right || y < r.top || y > r.bottom) { hint.hidden = true; return null; }
  const rx = (x - r.left) / r.width, ry = (y - r.top) / r.height;
  // How far into each edge's band (its outer 40%) the pointer is; the deepest wins.
  const [edge, depth] = Object.entries({ left: 0.4 - rx, right: rx - 0.6, top: 0.4 - ry, bottom: ry - 0.6 }).sort((a, b) => b[1] - a[1])[0];
  const dir = canSplit(t) && depth > 0 ? edge : '';
  // The part the dragged tab would take: a file tab what the terminal leaves it, the terminal its own share.
  const inset = { left: '0', top: '0', right: '0', bottom: '0' };
  if (dir) inset[DOCK_SIDES[dir].across] = (term ? 1 - docs.at : docs.at) * 100 + '%';
  Object.assign(hint.style, inset);
  hint.textContent = !dir ? 'On its own' : term ? TERMINAL_SIDES[dir] : DOCK_SIDES[dir].label;
  hint.hidden = false;
  return dir;
}

/** The terminal dragged to a side (a DOCK_SIDES key): the file tab goes across from it. '' shows it on its own. */
const TERMINAL_SIDES = { right: 'Terminal on the right', left: 'Terminal on the left', top: 'Terminal on top', bottom: 'Terminal below' };

/** The file tab the terminal shares the area with: the one on screen, else the last one open. */
const terminalPartner = () => [shownDoc(), ...shownTabs().reverse()].find(canSplit) || null;

function splitTerminal(dir) {
  const t = terminalPartner();
  if (dir && t) splitDoc(t.key, DOCK_SIDES[dir].across);
  else docs.side = null;
  showTerminalDoc();
}

$('#docTabs').addEventListener('contextmenu', (e) => {
  if (e.target.closest('.doc-tab.terminal')) {
    const partner = terminalPartner();
    if (!partner) return;
    e.preventDefault();
    const split = sideTab() && docs.side === partner.key;
    showMenu(e.clientX, e.clientY, [
      { header: 'Beside ' + (isConvTab(partner) ? 'the Conversation' : isPluginTab(partner) ? pluginCaption(partner.pluginId) : partner.spec.path.split('/').pop()) },
      ...Object.entries(TERMINAL_SIDES).map(([dir, label]) => ({
        label, checked: !!split && DOCK_SIDES[docs.dir].across === dir, action: () => splitTerminal(dir),
      })),
      { label: 'On its own', checked: !split, action: () => splitTerminal('') },
    ]);
    return;
  }
  const el = e.target.closest('.doc-tab');
  const t = el && docs.tabs.find((d) => d.key === el.dataset.id);
  if (!t) return;
  e.preventDefault();
  const beside = docs.side === t.key;
  showMenu(e.clientX, e.clientY, [
    ...Object.entries(DOCK_SIDES).map(([dir, side]) => canSplit(t) && { label: side.label, checked: beside && docs.dir === dir, action: () => splitDoc(t.key, dir) }),
    canSplit(t) && { label: 'On its own', checked: !beside, action: () => splitDoc(t.key, null) },
    canSplit(t) && '-',
    { label: 'Close', kbd: 'Ctrl+W', action: () => closeDoc(t.key) },
  ]);
});

// While split, the side you click or tab into has your attention (see attend).
for (const id of ['#diffDoc', '#convDoc', '#pluginDoc']) {
  for (const type of ['pointerdown', 'focusin']) $(id).addEventListener(type, () => attend(docs.side), true);
}
for (const type of ['pointerdown', 'focusin']) $('#termPane').addEventListener(type, () => attend('terminal'), true);

// The divider between the terminal and the tab beside it; double-click evens them up.
(() => {
  const sp = $('#docSplitter');
  const set = (at) => {
    docs.at = Math.max(0.2, Math.min(0.8, at));
    $('#docArea').style.setProperty('--split', docs.at * 100 + '%');
  };
  sp.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    e.preventDefault();
    sp.setPointerCapture(e.pointerId);   // keeps the moves coming over a plugin's frame too
    sp.classList.add('dragging');
    const move = (ev) => {
      const r = $('#docArea').getBoundingClientRect();
      set({
        right: (ev.clientX - r.left) / r.width, left: (r.right - ev.clientX) / r.width,
        bottom: (ev.clientY - r.top) / r.height, top: (r.bottom - ev.clientY) / r.height,
      }[docs.dir]);
    };
    const up = () => {
      sp.classList.remove('dragging');
      store.set('docSplitAt', docs.at);
      sp.removeEventListener('pointermove', move);
      sp.removeEventListener('lostpointercapture', up);
    };
    sp.addEventListener('pointermove', move);
    sp.addEventListener('lostpointercapture', up);
  });
  sp.addEventListener('dblclick', () => { set(0.5); store.set('docSplitAt', docs.at); });
})();

applyGitVisibility();
applyDiffOptions();
document.fonts.ready.then(() => post({ type: 'ready' }));
