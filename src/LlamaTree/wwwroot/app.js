'use strict';

/* =====================================================================
   LlamaTree – front end
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
  shield: 'M8 1.5l5.5 2v4.2c0 3.2-2.3 5.6-5.5 6.8-3.2-1.2-5.5-3.6-5.5-6.8V3.5z',   // the Permissions view
  bag: 'M3 5.5h10l-.8 8.5H3.8zM5.5 7V4a2.5 2.5 0 0 1 5 0v3',   // Get plugins and Get themes
  link: 'M6.8 9.2l2.4-2.4M7.3 4.7l1-1a2.6 2.6 0 0 1 3.7 3.7l-1 1M8.7 11.3l-1 1a2.6 2.6 0 0 1-3.7-3.7l1-1',   // a session that may message others
  server: 'M2.5 2.5h11v4.5h-11zM2.5 9h11v4.5h-11zM5 4.75h.01M5 11.25h.01',   // a server: a session that runs on one
  mail: 'M2 4h12v8.5H2zM2 4.5l6 4.5 6-4.5',   // unread messages
  save: 'M2.5 2.5h9l2 2v9h-11zM5 2.5v3.5h5v-3.5M5 13.5v-4h6v4',   // Save as a new theme
  warn: 'M8 2l6.5 11.5h-13zM8 6.5v3.5M8 12h.01',   // something couldn't be loaded
  markAllow: 'M3.5 8.5l3 3 6-7',                                          // Permissions: allowed
  markAsk: 'M5.8 5.8a2.2 2.2 0 1 1 3.2 2c-.7.4-1 .9-1 1.6v.6M8 12.6h.01',  // Permissions: you're asked first
  markDeny: 'M8 2a6 6 0 1 0 0 12A6 6 0 1 0 8 2M3.8 3.8l8.4 8.4',         // Permissions: not allowed
  git: 'M5 3v10M5 11a2 2 0 1 0 0 .01M5 3a1.6 1.6 0 1 0 0 .01M11 3a1.6 1.6 0 1 0 0 .01M11 4.6C11 8 5 7 5 10',
  branch: 'M5 2.5v8M5 12a1.5 1.5 0 1 0 0 .01M11 4a1.5 1.5 0 1 0 0 .01M11 5.5c0 3-6 2.5-6 5',
  branchTracked: 'M4 2.5v8M4 12a1.5 1.5 0 1 0 0 .01M10 4a1.5 1.5 0 1 0 0 .01M10 5.5c0 3-6 2.5-6 5M12.5 14V9.5M10.5 11.5l2-2 2 2',   // a branch with an upstream
  newBranch:'M4 2.5v8M4 12a1.5 1.5 0 1 0 0 .01M10 4a1.5 1.5 0 1 0 0 .01M10 5.5c0 3-6 2.5-6 5M12.5 9.5v4M10.5 11.5h4',   // branch with a +
  agent: 'M4 5.5h8v7H4zM8 5.5V3M8 2.5h.01M6.5 8.5h.01M9.5 8.5h.01M6.5 10.5h3M2.5 8v2M13.5 8v2',   // a robot head: a session an agent started
  chevR: 'M6 4l4 4-4 4',
  chevL: 'M10 4l-4 4 4 4',
  chevD: 'M4 6l4 4 4-4',
  chevU: 'M4 10l4-4 4 4',
  edit: 'M10.5 2.5l3 3-8 8h-3v-3zM9 4l3 3',   // a pencil: changed files, as on Visual Studio's status bar
  up: 'M8 13V3M4 7l4-4 4 4',
  down: 'M8 3v10M4 9l4 4 4-4',
  more: 'M3.5 8h.01M8 8h.01M12.5 8h.01',
  collapse: 'M6 2v4H2M10 2v4h4M6 14v-4H2M10 14v-4h4',
  sideBySide: 'M2 3h12v10H2zM8 3v10',
  panel: 'M2 3h12v10H2zM10 3v10',   // the side panel, with the Git tabs off
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
  pin: 'M5.5 2h5M6.5 2v4.2L4.5 9h7l-2-2.8V2M8 9v5',   // a pushpin: a project tab kept on the row
  chat: 'M2.5 3h11v7.5H7.5l-3 2.5v-2.5h-2z',
  bulb: 'M6 12.5h4M6.5 14.5h3M8 1.5a4.5 4.5 0 0 0-2.6 8.2c.4.3.6.7.6 1.2v.6h4v-.6c0-.5.2-.9.6-1.2A4.5 4.5 0 0 0 8 1.5z',
  gift: 'M2.5 6h11v2.5h-11zM3.5 8.5h9v6h-9zM8 6v8.5M8 6C6.5 3 4 3.5 4.5 5c.3.8 2 1 3.5 1zM8 6c1.5-3 4-2.5 3.5-1-.3.8-2 1-3.5 1z',
  info: 'M8 1.5a6.5 6.5 0 1 0 0 13 6.5 6.5 0 1 0 0-13M8 7v4.5M8 4.8h.01',
  // Which agent a session runs and where (sessionMarks): Claude, Codex or anything else; this computer (a server is `server`).
  computer: 'M2 2.5h12v8.5H2zM5.5 14h5M8 11v3',
  agentClaude: 'M8 2v12M2 8h12M3.8 3.8l8.4 8.4M12.2 3.8l-8.4 8.4',
  agentCodex: 'M5.5 4.5L2 8l3.5 3.5M10.5 4.5L14 8l-3.5 3.5M9.2 3l-2.4 10',
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

function toast(message, isError = false, ms = isError ? 7000 : 3500) {
  const el = h('div', { class: 'toast' + (isError ? ' error' : '') }, message);
  $('#toastHost').append(el);
  setTimeout(() => el.remove(), ms);
}

// ------------------------------------------------------------------ host bridge

// Windows: WebView2's bridge. macOS and Linux: Photino's (window.external), which every frame can reach, so the host only
// trusts messages that carry the token it wrote into this page as it served it (taken out of the page straight away).
const PHOTINO = typeof window.external?.sendMessage === 'function';
const host = PHOTINO ? photinoBridge() : window.chrome.webview;

function photinoBridge() {
  const listeners = [];
  const meta = document.querySelector('meta[name="llamatree-bridge"]');
  const token = meta?.content ?? '';
  meta?.remove();
  window.external.receiveMessage((text) => {
    const data = JSON.parse(text);
    for (const fn of listeners) fn({ data });
  });
  // A file dropped on the window would otherwise replace the page; the page's own drop targets still get theirs.
  for (const type of ['dragover', 'drop']) window.addEventListener(type, (e) => e.preventDefault());
  return {
    postMessage: (m) => window.external.sendMessage(JSON.stringify({ ...m, bridgeToken: token })),
    addEventListener: (type, fn) => { if (type === 'message') listeners.push(fn); },
  };
}

/** macOS: ⌘ does what Ctrl does elsewhere (app shortcuts, copy and paste); Ctrl still goes to the terminal as itself. */
const IS_MAC = /Mac/i.test(navigator.platform || navigator.userAgent);
const IS_LINUX = !IS_MAC && /Linux/i.test(navigator.platform || navigator.userAgent);
const FILE_MANAGER = IS_MAC ? 'Finder' : IS_LINUX ? 'Files' : 'File Explorer';

// A Mac app gets ⌘C, ⌘V, ⌘X, ⌘A and ⌘Z in text fields from its Edit menu, which a Photino window may not have, so
// the page does them itself (the terminal has its own, see terminalKey).
if (IS_MAC && PHOTINO) {
  document.addEventListener('keydown', (e) => {
    const field = e.target;
    if (!e.metaKey || e.ctrlKey || e.altKey || !(field instanceof HTMLInputElement || field instanceof HTMLTextAreaElement)) return;
    if (field.closest('.xterm, .monaco-editor')) return;   // they handle their own
    const key = e.key.toLowerCase();
    if (key === 'v') {
      e.preventDefault();
      readClipboardText().then((text) => {
        if (!text) return;
        field.setRangeText(text, field.selectionStart ?? field.value.length, field.selectionEnd ?? field.value.length, 'end');
        field.dispatchEvent(new Event('input', { bubbles: true }));
      }, () => {});
    } else if (key === 'c' || key === 'x') {
      e.preventDefault();
      document.execCommand(key === 'c' ? 'copy' : 'cut');
    } else if (key === 'a') {
      e.preventDefault();
      field.select();
    } else if (key === 'z') {
      e.preventDefault();
      document.execCommand(e.shiftKey ? 'redo' : 'undo');
    }
  }, true);
}
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

/** The clipboard's text. On macOS and Linux the app reads it: WebKit asks first (macOS) or refuses (Linux) when a page does. */
function readClipboardText() {
  return PHOTINO ? request('clipboard.text').then((text) => text ?? '') : navigator.clipboard.readText();
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
    case 'sessions': state.agents = m.agents || state.agents; setServers(m.servers); syncSessions(m.sessions, m.projects || []); break;
    case 'servers': setServers(m.servers); break;
    case 'output': onOutput(m.id, m.data); break;
    case 'replay': onReplay(m.id, m.data); break;
    case 'closeRequested': onCloseRequested(); break;
    case 'host.failed': onHostFailed(m); break;
    case 'host.reconnecting': onReconnecting(m); break;
    case 'machine': onMachine(m); break;
    case 'servers.tooMany': toast(`${m.server} has more than ${m.shown} sessions (${m.listed}). Only ${m.shown} of them are shown here.`, true); break;
    case 'files.opened': toast(`Opened a read-only copy of ${m.name} from ${m.server}. Changes to it aren’t saved on the server: edit the file there, through the session.`); break;
    case 'files.browse': browseServerFolder(m.path, m.select, m.server); break;
    case 'fileProblem': toast(m.text, true, 20000); break;   // a damaged file of the app's, set aside (JsonFile)
    case 'exited': onExited(m.id, m.code); break;
    case 'git.status': onGitStatus(m); break;
    case 'panels': onPanels(m); break;
    case 'typePrompt': typePrompt(m.id, m.text); break;
    case 'newConversations': showNewConversations(m.ids); break;
    case 'relay': relayChanged(m.state); break;
    case 'relay.ask': askRelay(m.from, m.to); break;
    case 'relay.message': onRelayMessage(m); break;
    case 'control': controlChanged(m.state); break;
    case 'control.ask': askSpawn(m.request); break;
    case 'control.settled': spawnSettled(m.id); break;
    case 'agent.ready': { const s = state.byId.get(m.id); if (s) s.readyAt = performance.now(); break; }
    case 'control.start': { const s = state.byId.get(m.id); if (s && !s.started) startSession(s); break; }
    case 'control.prompt': controlPrompt(m.id, m.text); break;
    case 'plugins.grants': applyGrants(m.grants); break;
    case 'plugins.changed': loadPlugins().then(() => renderGit()); break;   // a server's plugins arrived or changed
  }
});

// ------------------------------------------------------------------ state

/**
 * Messages between sessions (RelayHub on the host): the rules (from, to: a session id, 'project' or '*', decision),
 * whether they're paused, what waits for your answer ({ from, to, count }), each session's unread count, and the
 * sessions to tell they have messages once they're idle. Up here, as tile code reads it.
 */
const relay = { paused: false, rules: [], waiting: [], unread: {}, logVersion: 0, told: {}, from: {}, asks: [], asking: null };

/**
 * Sessions starting and driving others (SessionControl on the host): the rules ({ ability: 'spawn' | 'answer', scope:
 * a session id, 'project:<id>' or '*', decision }), the requests to start a session waiting for your answer, and the
 * state of each session last told to the host.
 */
const control = { rules: [], pending: [], logVersion: 0, asking: null, told: {} };

const state = {
  sessions: [],              // ordered session objects
  agents: [],                // the coding agents the host knows (AgentProfile.Describe): id, name, programs, choosesId, follows, …
  byId: new Map(),
  expandedId: null,
  projectViewOf: null,       // the project whose Project view is showing: a place of its own, with no session open
  currentId: store.get('currentId', null),   // session shown in the Git panel
  gitVisible: store.get('gitVisible', true),
  hiddenGitTabs: new Set(store.get('hiddenGitTabs', [])),   // 'changes' and/or 'repo', turned off in the side panel's ▾
  gitWidth: store.get('gitWidth', 400),
  panels: [],                // shown by Claude through the app's MCP server (their tabs live in the dock)
  panelsLoaded: false,       // until the host sends them, saved panel tabs keep their place
  fontSize: store.get('fontSize', 14),
  surface: { w: 0, h: 0 },
  projects: [],                                        // [{ id, name }] in display order
  hiddenProjects: new Set(store.get('hiddenProjects', [])),   // hidden rather than shown, so new projects start visible
  // Where projects and sessions go (see NAV_LAYOUTS): 'top', 'hybrid' (projects on the left) or 'left' (both, as a tree).
  navLayout: ['hybrid', 'left'].includes(store.get('navLayout')) ? store.get('navLayout') : 'top',
  navWidth: store.get('navWidth', 230),
  navBranches: store.get('navBranches', true),   // each session's branch beneath its name, down the left
  // In the Top layout, projects as tabs (a click shows only that project) rather than as filters to tick.
  projectTabs: store.get('projectTabs', false),
  // Projects (and Unknown) taken off the tab row into its ▾ list. Unpinned rather than pinned, so new projects start pinned.
  unpinnedProjects: new Set(store.get('unpinnedProjects', [])),
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

/**
 * A colour from the host as it may go into a style: a #rgb, #rgba, #rrggbb or #rrggbbaa, or null for anything else. A
 * server's host (in a --remote window) sends what it likes, and these go into style text, where more would be more CSS.
 */
const cssColor = (c) => typeof c === 'string' && /^#(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/i.test(c) ? c : null;

/** The project's chosen colour (the host only ever sends #rrggbb), or its automatic one. */
const projectColor = (p) => cssColor(p.color) || autoProjectColor(p.id);

/** An agent's colours (AgentProfile.Color) as a style, or null when it has none (or they aren't colours). */
const agentStyle = (a) => {
  const dark = cssColor(a?.color?.dark), light = cssColor(a?.color?.light);
  return dark && light ? `--agent-dark: ${dark}; --agent-light: ${light}` : null;
};

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
    ? `${tabbedProjects() ? 'Pick' : 'Tick'} a project ${state.navLayout === 'top' ? 'above' : 'on the left'} (Unknown holds sessions with no project), or choose All.`
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
  $('#projectBar').classList.toggle('tabbed', tabbedProjects());   // tabs sit on the row's bottom edge (see app.css)
  if (state.navLayout !== 'top') { bar.replaceChildren(); renderNav(); return; }   // the projects are on the left
  const groups = projectGroups();
  fill(bar,
    h('span', { class: 'project-bar-label' }, 'Projects'),
    tabbedProjects() ? projectTabRow(groups) : [...groups.map((p) => projectTab(p)), ...projectAllButtons(groups)],
    projectAddButton(),
  );
  syncStatus(bar);
}

// ------------------------------------------------------------------ projects as tabs (Top layout)

/** Projects shown as tabs rather than filters: an option of the Top layout only, and only once there are projects. */
const tabbedProjects = () => state.projectTabs && state.navLayout === 'top' && state.projects.length > 0;
const isPinned = (id) => !state.unpinnedProjects.has(id);
const groupMembers = (id) => state.sessions.filter((s) => groupOf(s) === id);
const allProjectsShown = () => allGroups().every((g) => !state.hiddenProjects.has(g));
/** The one project shown on its own, or null when several (or none, or all) are. */
function onlyShownProject() {
  const shown = allGroups().filter((g) => !state.hiddenProjects.has(g));
  return shown.length === 1 ? shown[0] : null;
}

/**
 * The project row as tabs: All, then the pinned projects, then any unpinned project that's being shown (a preview tab
 * with its pin showing, as Visual Studio's), then a ▾ listing the projects that aren't pinned.
 */
function projectTabRow(groups) {
  const unpinned = groups.filter((g) => !isPinned(g.id));
  return [
    h('button', { class: 'project-all' + (allProjectsShown() ? ' active' : ''), title: 'Show every project, Unknown included', onclick: showAllProjects }, 'All'),
    ...groups.filter((g) => isPinned(g.id) || (!state.hiddenProjects.has(g.id) && !allProjectsShown())).map((g) => projectTabbed(g)),
    unpinned.length ? projectMoreButton(unpinned) : null,
  ];
}

/**
 * A project as a tab: a click shows only it (again, all of them), Ctrl+click shows or hides it alongside the others.
 * It's active while shown, unless every project is (then All is). Its pin takes it off the row, or puts it back.
 */
function projectTabbed(p) {
  const members = groupMembers(p.id);
  const waiting = members.filter(waitingForYou).length;
  const active = !state.hiddenProjects.has(p.id) && !allProjectsShown();
  const only = onlyShownProject() === p.id;
  const pinned = isPinned(p.id);
  const what = p.unknown ? 'sessions with no project' : p.name;
  return h('div', {
    class: 'project-tab tabbed' + (active ? ' shown' : '') + (p.unknown ? ' unknown' : '') + (pinned ? '' : ' preview'),
    style: p.unknown ? null : `--proj:${projectColor(p)}`,
    'data-id': p.id,
    oncontextmenu: (e) => { e.preventDefault(); projectMenu(p, e.clientX, e.clientY); },
  },
  h('button', {
    class: 'project-name',
    title: `${only ? 'Back to every project' : `Show only ${what}`} · Ctrl+click to ${active ? 'hide it, keeping' : 'show it alongside'} the others\n` +
      `${members.length} session${members.length === 1 ? '' : 's'} · right-click for options`,
    onclick: (e) => (e.ctrlKey ? toggleProject(p.id) : only ? showAllProjects() : showOnlyProject(p.id)),
  }, h('span', { class: 'project-label' }, p.name),
  projectDot(members),
  waiting ? h('span', { class: 'badge', title: `${waiting} session${waiting === 1 ? '' : 's'} waiting for you` }, String(waiting)) : null),
  h('button', {
    class: 'project-pin' + (pinned ? '' : ' unpinned'),
    title: pinned ? 'Unpin: move it to the ▾ list after the tabs' : 'Pin: keep this tab on the row',
    onclick: () => setPinned(p.id, !pinned),
  }, iconEl('pin')));
}

/** The ▾ after the tabs: the projects that aren't pinned, with a dot and a count if any of their sessions want you. */
function projectMoreButton(unpinned) {
  return h('button', {
    class: 'project-more', 'aria-haspopup': 'menu',
    title: `${unpinned.length} more project${unpinned.length === 1 ? '' : 's'}, not pinned`,
    onclick: (e) => { const r = e.currentTarget.getBoundingClientRect(); projectMoreMenu(r.left, r.bottom + 2); },
  }, iconEl('chevD'), projectMoreIndicators(unpinned));
}

function projectMoreIndicators(unpinned) {
  const members = unpinned.flatMap((g) => groupMembers(g.id));
  const waiting = members.filter(waitingForYou).length;
  return [projectDot(members), waiting ? h('span', { class: 'badge', title: `${waiting} session${waiting === 1 ? '' : 's'} waiting for you` }, String(waiting)) : null];
}

/** Refreshes the ▾'s dot and count in place, as updateProjectIndicators does a tab's. */
function updateProjectMoreIndicators() {
  const more = document.querySelector('#projectTabs .project-more');
  if (!more) return;
  const want = projectMoreIndicators(projectGroups().filter((g) => !isPinned(g.id)));
  // A dot in the same state stays, so its animation isn't restarted on every update.
  const dot = more.querySelector('.dot');
  if (!want[0]) dot?.remove();
  else if (dot?.className !== want[0].className) { dot?.remove(); more.append(want[0]); syncStatus(more); }
  more.querySelector('.badge')?.remove();
  if (want[1]) more.append(want[1]);
}

function projectMoreMenu(x, y) {
  const groups = projectGroups(), only = onlyShownProject();
  const onlyGroup = only && !isPinned(only) ? groups.find((g) => g.id === only) : null;
  showMenu(x, y, [
    { header: 'Not pinned' },
    ...groups.filter((g) => !isPinned(g.id)).map((g) => {
      const waiting = groupMembers(g.id).filter(waitingForYou).length;
      return {
        icon: h('span', { class: 'accent-dot', style: `background:${g.unknown ? 'var(--text-faint)' : projectColor(g)}` }),
        checked: only === g.id || undefined,
        label: g.name,
        kbd: waiting ? `${waiting} waiting` : '',
        action: () => showOnlyProject(g.id),
      };
    }),
    '-',
    onlyGroup && { icon: 'pin', label: `Pin ${onlyGroup.name}`, action: () => setPinned(onlyGroup.id, true) },
    { icon: 'pin', label: 'Pin them all', action: pinAllProjects },
  ]);
}

function saveUnpinnedProjects() {
  store.set('unpinnedProjects', [...state.unpinnedProjects]);
}

function setPinned(id, pinned) {
  if (pinned) state.unpinnedProjects.delete(id);
  else state.unpinnedProjects.add(id);
  saveUnpinnedProjects();
  renderProjectBar();
}

function pinAllProjects() {
  state.unpinnedProjects.clear();
  saveUnpinnedProjects();
  renderProjectBar();
}

function setProjectTabs(on) {
  state.projectTabs = on;
  store.set('projectTabs', on);
  applyProjectFilter();   // the bar, and the empty grid's hint
}

/** Pin or Unpin, for a project's right-click menu while projects are tabs. */
const pinMenuItems = (id) => (!tabbedProjects() ? [] : [isPinned(id)
  ? { label: 'Unpin (move to the ▾ list)', action: () => setPinned(id, false) }
  : { label: 'Pin (keep it on the tab row)', action: () => setPinned(id, true) }]);

/** The groups the projects bar lists: the projects, then Unknown. With no projects there is nothing to toggle, so none. */
const projectGroups = () => (state.projects.length ? [...state.projects, { id: UNKNOWN, name: UNKNOWN_NAME, unknown: true }] : []);

/** A project's tab in the projects bar (or row down the left): its checkbox and its name, with `extra` at the end. */
function projectTab(p, extra = null) {
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
  }, h('span', { class: 'project-label' }, p.name),
  projectDot(members),
  waiting ? h('span', { class: 'badge', title: `${waiting} session${waiting === 1 ? '' : 's'} waiting for you` }, String(waiting)) : null),
  extra);
}

/** All and None: show or hide every project at once. */
function projectAllButtons(groups) {
  if (!groups.length) return [];
  const allShown = groups.every((g) => !state.hiddenProjects.has(g.id));
  const noneShown = groups.every((g) => state.hiddenProjects.has(g.id));
  return [
    h('button', { class: 'project-all' + (allShown ? ' active' : ''), title: 'Show every project, Unknown included', onclick: showAllProjects }, 'All'),
    h('button', { class: 'project-all' + (noneShown ? ' active' : ''), title: 'Hide every project, Unknown included', onclick: hideAllProjects }, 'None'),
  ];
}

const projectAddButton = () => h('button', { class: 'tb-btn text project-add', title: 'New project', onclick: showNewProjectDialog }, iconEl('plus'),
  state.projects.length ? 'Project' : 'New project (group sessions)');

/**
 * The bar down the left in the Hybrid and Left layouts: a row per project, as its tab in the Top layout's projects bar,
 * with a button for its Project view; in Left, also its visible sessions beneath it, as a tree, in place of the session
 * tab row. Rows carry the tabs' own classes (project-tab, session-tab), so what updates a tab in place finds them.
 */
function renderNav() {
  if (state.navLayout === 'top') return;
  const tree = state.navLayout === 'left';
  const groups = projectGroups();
  const shown = tree ? visibleSessions() : [];
  const tabOf = (s) => sessionTab(s, shown.indexOf(s), true);   // the index gives its Ctrl+1…9, as in the tab row
  const rows = groups.map((p) => {
    const members = shown.filter((s) => groupOf(s) === p.id);
    return h('div', { class: 'nav-group' + (p.unknown ? ' unknown' : ''), style: p.unknown ? null : `--proj:${projectColor(p)}` },
      projectTab(p, h('button', {
        class: 'icon-btn small nav-view' + (projectViewShowing(p.id) ? ' active' : ''), 'data-project': p.id,
        title: 'Project view: its worktrees side by side (again to go back)',
        onclick: () => toggleProjectView(p.id),
      }, iconEl('list'))),
      members.length > 0 && h('div', { class: 'nav-sessions' }, members.map(tabOf), sessionPlus(members, p.unknown ? 'with no project' : `in ${p.name}`)));
  });
  // No projects: in Left the sessions are still listed, as one group with no name; otherwise just what projects are for.
  if (!groups.length) {
    rows.push(tree && shown.length
      ? h('div', { class: 'nav-sessions unnamed' }, shown.map(tabOf), sessionPlus(shown, 'like these'))
      : h('div', { class: 'nav-hint' }, 'Projects group your sessions, so you can show and hide them together.'));
  }
  // In Left there's no title bar (see applyNavLayout): All sessions heads the bar, and New Session joins + Project.
  fill($('#navHead'), tree && $('#btnGrid'), h('span', { class: 'project-bar-label' }, 'Projects'), h('span', { class: 'spacer' }), projectAllButtons(groups));
  fill($('#navList'), rows);
  fill($('#navFoot'), tree && $('#btnAdd'), projectAddButton());
  syncStatus($('#navList'));
  if (tree) revealActive($('#navList'), '.session-tab.active');
}

function projectMenu(p, x, y) {
  if (p.unknown) {   // a group, not a project: nothing to rename, move or delete
    showMenu(x, y, [
      { label: 'Project view: its worktrees side by side', action: () => openProjectView(UNKNOWN) },
      '-',
      { label: 'Show only sessions with no project', action: () => showOnlyProject(UNKNOWN) },
      { label: state.hiddenProjects.has(UNKNOWN) ? 'Show alongside the others' : 'Hide', action: () => toggleProject(UNKNOWN) },
      ...pinMenuItems(UNKNOWN),
      ...pluginMenuItems('project', { project: null }, NO_PROJECT),
    ]);
    return;
  }
  const i = state.projects.indexOf(p);
  const across = state.navLayout === 'top';   // a row of tabs; down the left, a list
  showMenu(x, y, [
    { label: 'Project view: its worktrees side by side', action: () => openProjectView(p.id) },
    '-',
    { label: 'Show only this project', action: () => showOnlyProject(p.id) },
    { label: state.hiddenProjects.has(p.id) ? 'Show alongside the others' : 'Hide', action: () => toggleProject(p.id) },
    ...pinMenuItems(p.id),
    '-',
    { label: 'Edit project (name, colour)…', action: () => showEditProjectDialog(p) },
    i > 0 && { label: across ? 'Move left' : 'Move up', action: () => moveProject(p, -1) },
    i < state.projects.length - 1 && { label: across ? 'Move right' : 'Move down', action: () => moveProject(p, 1) },
    '-',
    { label: 'Delete project…', danger: true, action: () => confirmDeleteProject(p) },
    ...pluginMenuItems('project', { project: p.name }, p.id),
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
/** A theme being tried on in Get themes, shown in place of the one picked until it's installed or put away (see previewTheme). */
let themePreview = null;
/** The theme file in use (or being tried on), or null for System, Light and Dark. */
const currentTheme = () => themePreview ?? (BUILT_IN_THEMES.includes(theme.pref) ? null
  : themes.list.find((t) => t.id === theme.pref) ?? (themes.last?.id === theme.pref ? themes.last : null));
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
  if (!themes.list.includes(t) && t !== themePreview) return t;   // the kept look
  const made = t.monochrome ? monochromeLook(accentColour(), t.base === 'light') : {};
  return {
    id: t.id, name: t.name, base: t.base, font: t.font, glow: t.glow, grid: !!t.grid, rain: !!t.rain, monochrome: t.monochrome, cursor: t.cursor ?? null, terminalBackground: t.terminalBackground ?? null,
    colors: { ...made.colors, ...t.colors }, terminal: { ...made.terminal, ...t.terminal }, diff: { ...made.diff, ...t.diff },
    accent: t.accent ?? made.accent ?? null,
  };
}

let themeColors = [];   // the colours the theme in use set on the page, to take off again
// The rain (a theme's "rain": true): glyphs falling down the window's floor in the accent colour, on a canvas fixed behind
// everything (see #rain in app.css); the title bar, tabs, panels and terminals are see-through over it as over the Grid.
// Each column is one drop: a bright head, a tail that fades as the canvas is washed with the floor colour each frame.
const rain = { canvas: null, ctx: null, cols: [], timer: null, colour: '#39ff6a', floor: '#000000' };
const RAIN_GLYPHS = 'ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789Z:・"=*+-<>¦|ç';
const RAIN_CELL = 16, RAIN_MS = 70;

/** Starts the rain, or stops it and takes its canvas away. */
function digitalRain(on) {
  if (!on) {
    clearInterval(rain.timer); rain.timer = null;
    rain.canvas?.remove(); rain.canvas = rain.ctx = null; rain.cols = [];
    return;
  }
  if (rain.canvas) { rainColours(); return; }
  const canvas = h('canvas', { id: 'rain', 'aria-hidden': 'true' });
  document.body.prepend(canvas);
  rain.canvas = canvas; rain.ctx = canvas.getContext('2d');
  rainColours();
  rainSize();
  const still = matchMedia('(prefers-reduced-motion: reduce)').matches;
  if (still) { for (let i = 0; i < 60; i++) rainFrame(); return; }   // a settled picture, not a moving one
  rain.timer = setInterval(() => { if (!document.hidden) rainFrame(); }, RAIN_MS);
}

/** The rain's colours, from the theme on screen: the accent for the glyphs, the chrome for the floor it washes to. */
function rainColours() {
  const css = getComputedStyle(document.documentElement);
  rain.colour = css.getPropertyValue('--accent').trim() || '#39ff6a';
  rain.floor = css.getPropertyValue('--chrome').trim() || '#000000';
}

/** Fits the canvas to the window at the device's resolution and starts a drop in every column. */
function rainSize() {
  const { canvas, ctx } = rain;
  if (!canvas) return;
  const dpr = devicePixelRatio || 1, w = innerWidth, hgt = innerHeight;
  canvas.width = Math.round(w * dpr); canvas.height = Math.round(hgt * dpr);
  canvas.style.width = w + 'px'; canvas.style.height = hgt + 'px';
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = rain.floor; ctx.fillRect(0, 0, w, hgt);
  const n = Math.ceil(w / RAIN_CELL);
  rain.cols = Array.from({ length: n }, () => ({ y: -Math.random() * hgt * 1.6 / RAIN_CELL, speed: 0.4 + Math.random() * 0.8 }));   // staggered, so the columns don't all rain at once
  rain.rows = Math.ceil(hgt / RAIN_CELL);
}

function rainFrame() {
  const { ctx, canvas } = rain;
  if (!ctx) return;
  const w = canvas.clientWidth, hgt = canvas.clientHeight;
  ctx.fillStyle = rain.floor; ctx.globalAlpha = 0.16; ctx.fillRect(0, 0, w, hgt); ctx.globalAlpha = 1;   // the tails fade
  ctx.font = `${RAIN_CELL - 2}px "MS Gothic", "Cascadia Mono", Consolas, Menlo, "DejaVu Sans Mono", monospace`;
  ctx.textBaseline = 'top';
  for (let i = 0; i < rain.cols.length; i++) {
    const c = rain.cols[i];
    const y = Math.floor(c.y);
    if (y >= 0 && y < rain.rows + 1) {
      const glyph = RAIN_GLYPHS[Math.floor(Math.random() * RAIN_GLYPHS.length)];
      ctx.fillStyle = rain.colour; ctx.globalAlpha = 0.6;
      ctx.fillText(glyph, i * RAIN_CELL + 1, (y - 1) * RAIN_CELL);   // last cell: back to the accent
      ctx.fillStyle = '#ffffff'; ctx.globalAlpha = 0.8;
      ctx.fillText(RAIN_GLYPHS[Math.floor(Math.random() * RAIN_GLYPHS.length)], i * RAIN_CELL + 1, y * RAIN_CELL);   // the head, white-hot
      ctx.globalAlpha = 1;
    }
    c.y += c.speed;
    if (c.y > rain.rows + 2 && Math.random() < 0.035) { c.y = -Math.random() * 20; c.speed = 0.4 + Math.random() * 0.8; }
  }
}
addEventListener('resize', () => rainSize());

// ---- Terminal background --------------------------------------------------------------------------------------------
// How solid the terminals are over the window's floor: the tile paints the terminal's background at --terminal-veil (see
// .tile in app.css). A theme that lets you see through says how solid they start ("terminalBackground": Matrix's 42 shows
// its rain, Tron's 100 starts solid), and only then is there a slider in each terminal's corner to change it. Yours is kept
// per theme, so Matrix can be nearly solid for you while Tron stays as it is. Without it the terminals are solid.
const veil = { byTheme: store.get('terminalVeil', {}) };
const veilKey = () => theme.pref;
/** The theme's own amount, 0–100, or null when it keeps the terminals solid (and has no slider). */
const veilOwn = (look = themeLook()) => (typeof look?.terminalBackground === 'number' ? look.terminalBackground : null);
/** Your amount for the theme on, 0–100, or null for the theme's own. */
function veilSetting() {
  const v = veil.byTheme?.[veilKey()];
  return typeof v === 'number' && Number.isFinite(v) ? Math.min(100, Math.max(0, Math.round(v))) : null;
}
/** The amount on the page now, 0–100. */
function veilAmount() {
  const v = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--terminal-veil'));
  return Number.isFinite(v) ? Math.min(100, Math.max(0, v)) : 100;
}
/** Whether the terminals' own canvas must be see-through: over a floor, or under a veil of less than 100%. */
const seeThrough = (look = themeLook()) => !!(look?.grid || look?.rain) || veilAmount() < 100;
/**
 * Puts the amount on the page (yours, else the theme's own; none when the theme keeps the terminals solid), marks the
 * page while the terminals are see-through, and while the theme has a slider (data-veil-slider shows it).
 */
function markVeil(look = themeLook(), value = veilSetting()) {
  const root = document.documentElement;
  const own = veilOwn(look);
  const amount = own === null ? null : value ?? own;
  if (amount === null) root.style.removeProperty('--terminal-veil');
  else root.style.setProperty('--terminal-veil', amount + '%');
  if (own === null) delete root.dataset.veilSlider;
  else root.dataset.veilSlider = '';
  if (seeThrough(look)) root.dataset.veil = '';
  else delete root.dataset.veil;
}
/** The amount on the page, every terminal dressed for it, and every tile's slider at it. */
function applyVeil(value = veilSetting()) {
  markVeil(themeLook(), value);
  for (const s of state.sessions) if (s.term) dressCursor(s);
  syncVeilSliders();
}
/**
 * The small slider in a tile's corner (see .tile-veil in app.css), shown while the theme has one. Dragging shows at once;
 * letting go keeps it for the theme on (or forgets yours, back at the theme's own). Its clicks stay its own, so they
 * don't expand the tile.
 */
function veilSlider() {
  const v = String(Math.round(veilAmount()));
  const input = h('input', { type: 'range', min: '0', max: '100', step: '1', value: v, 'aria-label': 'How solid the terminal is' });
  const out = h('span', {}, v + '%');
  input.addEventListener('input', () => applyVeil(+input.value));
  input.addEventListener('change', () => { const n = +input.value; setVeil(n === veilOwn() ? null : n); });
  const el = h('div', { class: 'tile-veil', title: "How solid the terminals are over the theme's floor, for this theme. Drag to change it." }, input, out);
  for (const type of ['pointerdown', 'mousedown', 'click', 'dblclick', 'contextmenu']) el.addEventListener(type, (e) => e.stopPropagation());
  return el;
}
/** Every tile's slider at the amount on the page. */
function syncVeilSliders() {
  const v = String(Math.round(veilAmount()));
  for (const s of state.sessions) {
    if (!s.veil) continue;
    const input = s.veil.querySelector('input');
    if (input.value !== v) input.value = v;
    s.veil.querySelector('span').textContent = v + '%';
  }
}
/** Your amount for the theme on (null takes it away), kept and shown. */
function setVeil(value) {
  if (value === null) delete veil.byTheme[veilKey()];
  else veil.byTheme[veilKey()] = value;
  store.set('terminalVeil', veil.byTheme);
  applyVeil();
}

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
  if (look?.grid) root.dataset.grid = '';
  else delete root.dataset.grid;
  if (look?.rain) root.dataset.rain = '';
  else delete root.dataset.rain;
  markVeil(look);   // after the floor is marked: whether the terminals are see-through depends on it
  digitalRain(!!look?.rain);
}
markTheme(themes.last?.id === theme.pref ? themes.last : null);   // the accent isn't known yet: last time's look

/** Reads the themes again (one added to the folder shows without restarting), and puts the one in use on the page. */
async function loadThemes() {
  try {
    const r = await request('themes.list');
    const before = themeName();
    Object.assign(themes, { list: r.themes, problems: r.problems, folder: r.folder, names: r.names });
    // A theme that's gone (its file deleted, or no longer valid) gives way to System, rather than living on as last time's look.
    if (!BUILT_IN_THEMES.includes(theme.pref) && !themes.list.some((t) => t.id === theme.pref)) theme.pref = 'system';
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
const accentChanged = () => { if (currentTheme()?.monochrome) applyTheme(); else applyAccent(); rainColours(); };

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

// The light cycle (a theme's "cursor": "light-cycle"): a light cycle drawn in the terminal's cursor colour, riding a little to
// the right of the cursor (CYCLE_OFFSET; three cells wide) and leaving a ribbon of light that fades behind it as it moves,
// turning at right angles when it changes line. It's a layer over the terminal's screen. The cursor itself stays a
// blinking bar in the cursor colour, so you can see where you're typing and whether the terminal has focus; the cycle
// dims while it hasn't (see .cycle-head in app.css).
const TRAIL_MAX = 48;        // pieces of ribbon kept at once
const CYCLE_CELLS = 3;       // how many cells wide the light cycle is drawn
const CYCLE_OFFSET = 25;     // how far right of the cursor it rides, in pixels, so it never covers what you're typing
// Side view, facing right, in a 24 by 16 box: two tall wheels with hubs, the canopy sweeping over them, the rider's
// back, and the white light stripe along the canopy.
const CYCLE_SVG = '<svg viewBox="0 0 24 16" preserveAspectRatio="xMinYMid meet" aria-hidden="true">' +
  '<circle cx="5.5" cy="10.5" r="4.6"/><circle cx="18.5" cy="10.5" r="4.6"/>' +
  '<circle cx="5.5" cy="10.5" r="1.3" fill="currentColor"/><circle cx="18.5" cy="10.5" r="1.3" fill="currentColor"/>' +
  '<path d="M1.5 8 C3.5 3.6 8 2.2 12 2.6 C16.5 3 20.5 5 22.5 8.5"/><path d="M8.5 6.2 L10.5 3.6"/><path d="M9 10.5 H15"/>' +
  '<path d="M4 6.4 C8 3.2 15 3 20 5.8" stroke="#fff" stroke-width="1" opacity=".9"/></svg>';
// A program repainting its screen (a listing streaming by, Claude's own interface) moves the cursor every frame; that's
// not riding, so past this many moves in half a second only the glow follows, and the ribbon waits for your own moves.
const BURST_MOVES = 12, BURST_MS = 500;
const trailsAllowed = () => !matchMedia('(prefers-reduced-motion: reduce)').matches;

/**
 * The terminal's colours as the theme wants them on screen: see-through over the Grid floor or the rain (the tile under
 * it paints the floor's lines, see .tile in app.css) and under a veil of less than 100% (the slider in its corner), and the
 * cursor a bar, blinking as ever, while the light cycle rides beside it.
 */
function dressCursor(s) {
  const t = termTheme();
  const theme = { ...t };
  const look = themeLook();
  if (seeThrough(look)) theme.background = 'rgba(0, 0, 0, 0)';
  s.term.options.theme = theme;
  s.term.options.cursorStyle = s.cycle ? 'bar' : 'block';   // a bar beside the cycle: the cell stays readable
}

/** Puts the light cycle on a session's terminal, or takes it off, and sets the terminal's colours either way. */
function lightCycle(s, on) {
  if (!on) {
    if (s.cycle) {
      s.cycle.move.dispose(); s.cycle.resize.dispose(); s.cycle.layer.remove();
      s.cycle = null;
    }
    dressCursor(s);
    return;
  }
  if (s.cycle) { s.cycle.layer.style.setProperty('--cycle', termTheme().cursor); dressCursor(s); return; }
  const screen = s.surface.querySelector('.xterm-screen');
  if (!screen) return;
  const head = h('div', { class: 'cycle-head', hidden: true });
  head.innerHTML = CYCLE_SVG;
  const layer = h('div', { class: 'cycle-layer' }, head);
  layer.style.setProperty('--cycle', termTheme().cursor);
  screen.append(layer);
  const c = { layer, head, last: null, queued: false, pieces: [], moves: [] };
  const ride = () => {
    c.queued = false;
    const term = s.term, b = term.buffer.active;
    if (b.viewportY !== b.baseY || !term.cols || !term.rows) { head.hidden = true; c.last = null; return; }   // scrolled up: the cursor is off the screen
    const cw = screen.clientWidth / term.cols, ch = screen.clientHeight / term.rows;
    const x = b.cursorX, y = b.cursorY;
    head.hidden = false;
    Object.assign(head.style, { left: `${x * cw + CYCLE_OFFSET}px`, top: `${y * ch}px`, width: `${cw * CYCLE_CELLS}px`, height: `${ch}px` });
    const from = c.last;
    c.last = { x, y };
    if (!from || (from.x === x && from.y === y)) return;
    const now = performance.now();
    c.moves = c.moves.filter((t) => now - t < BURST_MS);
    c.moves.push(now);
    if (c.moves.length > BURST_MOVES || !trailsAllowed()) return;
    // The ribbon, from the rear wheel: down (or up) the old column to the new line, then along it to the new cell.
    const midX = (col) => (col + 0.5) * cw + CYCLE_OFFSET;   // a column's middle, where the cycle rides
    const wheel = (row) => (row + 0.69) * ch;   // the wheels' height in the cell (11 of 16 in CYCLE_SVG)
    const piece = (left, top, width, height) => {
      const p = h('div', { class: 'cycle-trail', style: `left:${left}px;top:${top}px;width:${width}px;height:${height}px` });
      p.addEventListener('animationend', () => { p.remove(); c.pieces = c.pieces.filter((q) => q !== p); });
      layer.append(p);
      c.pieces.push(p);
      while (c.pieces.length > TRAIL_MAX) c.pieces.shift().remove();
    };
    if (from.y !== y) piece(midX(from.x) - 1, Math.min(wheel(from.y), wheel(y)), 2, Math.abs(y - from.y) * ch);
    if (from.x !== x) piece(Math.min(midX(from.x), midX(x)), wheel(y) - 1, Math.abs(x - from.x) * cw, 2);
  };
  const queue = () => { if (!c.queued) { c.queued = true; requestAnimationFrame(ride); } };
  c.move = s.term.onCursorMove(queue);
  c.resize = s.term.onResize(() => { c.last = null; queue(); });
  s.cycle = c;
  dressCursor(s);
  queue();
}

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
  for (const id of [...state.unpinnedProjects]) if (!known.has(id)) state.unpinnedProjects.delete(id);
  saveUnpinnedProjects();

  const seen = new Set();
  const stopped = [];
  for (const info of list) {
    seen.add(info.id);
    let s = state.byId.get(info.id);
    if (!s) {
      s = createSession(info);
      state.byId.set(s.id, s);
    } else {
      Object.assign(s, { name: info.name, directory: info.directory, command: info.command, conversation: info.conversation, server: info.server ?? null });
    }
    s.project = info.project || null;
    // Running already: it ran on in the session host while no window was open (its output follows as a replay).
    if (info.running && !s.running) s.started = true;
    s.startedBy = info.startedBy || null;   // the session whose agent started this one (start_session)
    // A server's list is its word on what runs there. Its sessions can end without an "exited" reaching here: its
    // LlamaTree restarting on a new version stops them as the connection closes, and so does the server restarting.
    // Taken as exited, the terminal says so, and Enter starts it again there, rather than typing into nothing.
    if (info.server && s.running && !info.running) stopped.push(s.id);
    else if (info.running && !s.running) {
      // Ran on without this window: its console has whatever size it had, so it's given this terminal's.
      s.running = true;
      s.ptySize = null;
      refit();   // once it's fitted
    }
  }
  for (const id of stopped) onExited(id, null);
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
  prunePluginHidden();   // a session removed is no longer hidden from anything
  permissionsChanged();
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
  s.veil = veilSlider();
  s.tile = h('div', { class: 'tile', 'data-id': s.id }, s.header, h('div', { class: 'tile-body' }, s.surface, s.rail, s.cover, s.exitedBadge, s.veil));

  const term = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, Menlo, "DejaVu Sans Mono", "Courier New", monospace',
    fontSize: state.fontSize,
    lineHeight: 1.0,
    cursorBlink: true,
    allowTransparency: true,   // so a theme with a Grid floor can show it under the text (dressCursor); must be set before open()
    scrollback: 10000,
    allowProposedApi: true,
    theme: termTheme(),
    windowsPty: { backend: 'conpty', buildNumber: 22621 },
    rightClickSelectsWord: false,
    linkHandler: oscLinkHandler(s),
  });
  s.term = term;
  s.fit = new FitAddon.FitAddon();
  term.loadAddon(s.fit);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';
  term.loadAddon(new WebLinksAddon.WebLinksAddon((ev, uri) => request('openUrl', { uri })));
  term.registerLinkProvider(pathLinkProvider(s));
  term.open(s.surface);
  if (state.byId.size < MAX_WEBGL) {
    try {
      const gl = new WebglAddon.WebglAddon();
      gl.onContextLoss(() => gl.dispose());
      term.loadAddon(gl);
    } catch { /* DOM renderer fallback */ }
  }
  lightCycle(s, themeLook()?.cursor === 'light-cycle');   // either way dresses the terminal for the theme (see-through over the Grid or the rain)

  term.attachCustomKeyEventHandler((e) => terminalKey(s, e));
  term.onData((data) => {
    if (!s.running) {
      if (data === '\r') (s.startsOnOpen ? startSession(s) : restartSession(s));
      return;
    }
    s.act.lastIn = performance.now();
    // Enter gives it something to do. Not just any input: xterm answers the terminal's own queries (which Claude sends
    // as it starts) through here too.
    if (data.includes('\r')) s.act.starting = false;
    post({ type: 'input', id: s.id, data });
  });
  term.onResize(() => syncPtySize(s));
  term.onTitleChange((t) => { s.title = t; renderTileHeader(s); });
  // Some agents (Codex) report what they're doing in an OSC 7501 sequence, "state=working:app=codex" (see reportState).
  term.parser.registerOscHandler(7501, (data) => {
    const reported = /(?:^|:)state=([a-z]+)/.exec(data)?.[1];
    if (reported) reportState(s, reported);
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
    const link = s.hoverPath;   // right-clicked on a path link: its own items first
    if (link) {
      const shown = state.expandedId === s.id;
      showMenu(e.clientX, e.clientY, [
        { label: link.folder ? 'Open folder' : 'Open', action: () => openTermPath(s, link) },
        { label: 'Open containing folder', action: () => openTermPath(s, link, true) },
        { label: 'Copy path', action: () => navigator.clipboard.writeText(link.path) },
        ...(shown ? ['-', ...terminalMenuItems(s)] : []),
      ]);
    } else if (state.expandedId === s.id) terminalMenu(s, e.clientX, e.clientY);
  });
  // Ctrl+V with an image or files on the clipboard and no text: xterm would paste nothing, so paste their paths instead.
  s.surface.addEventListener('paste', (e) => {
    const types = e.clipboardData?.types ?? [];
    if (types.includes('text/plain') || !types.includes('Files')) return;
    e.preventDefault();
    e.stopPropagation();
    pasteClipboardFiles(s);
  }, true);
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

/**
 * Gives the session's console the terminal's size, when it isn't that size already (s.ptySize is the size it was last
 * given). Not only on xterm's resize: one that comes while a start is on its way can't be sent, and xterm, at that size
 * already, never reports it again, so the agent stayed laid out for the size the terminal had when it started.
 */
function syncPtySize(s) {
  if (!s.running) return;
  const { cols, rows } = s.term;
  if (s.ptySize?.cols === cols && s.ptySize?.rows === rows) return;
  s.ptySize = { cols, rows };
  post({ type: 'resize', id: s.id, cols, rows });
}

/** Starts (or restarts) the session's console at the terminal's size, and gives it any size the terminal took meanwhile. */
function requestStart(s, type, extra) {
  const { cols, rows } = s.term;
  s.ptySize = { cols, rows };
  return request(type, { id: s.id, cols, rows, theme: s.startedTheme, ...extra })
    .then((r) => { if (r?.prompted) s.act.starting = false; s.running = true; s.exitCode = null; syncPtySize(s); renderTileHeader(s); renderSessionTabs(); });
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
  s.act.starting = true;
  requestStart(s, 'start')
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
  s.act.starting = true;
  requestStart(s, 'restart', { resume })
    .catch((err) => { s.term.writeln(`\x1b[31m${err.message}\x1b[0m`); });
}

function onOutput(id, data) {
  const s = state.byId.get(id);
  if (!s) return;
  // xterm draws written output later, in chunks; the screen is only read for "asking" once this output is on it.
  s.term.write(data, () => { s.act.drawnAt = performance.now(); });
  noteActivity(s);
}

/**
 * What a session showed while no window was open (the session host kept the end of it). Written without counting as
 * activity, so a session isn't marked as working or waiting for you just because the window opened; the host then
 * has the program redraw its screen at this terminal's size.
 */
function onReplay(id, data) {
  const s = state.byId.get(id);
  if (!s) return;
  fitTerm(s);   // at its real size first: the output's cursor moves assume the width it was written at
  s.replaying = true;   // the states it reported back then are taken, but a turn it finished then doesn't flag it now
  s.term.write(data, () => { s.act.drawnAt = performance.now(); s.replaying = false; });
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

/**
 * What an agent that reports its state (OSC 7501: Codex) says it's doing, taken at its word instead of being guessed
 * from its output: "working" while it works; "done" when it finishes a turn, which is your turn (flagged as waiting for
 * you unless you're looking at it); "idle" when it's ready without having worked (it has just started); "blocked" while
 * it waits on your answer to a question or approval.
 */
function reportState(s, reported) {
  s.reportsState = true;
  const a = s.act, was = a.state;
  a.starting = false;
  if (reported === 'working') {
    if (was !== 'working') { a.state = 'working'; a.workingSince = performance.now(); }
  } else a.state = 'idle';
  setAsking(s, reported === 'blocked');
  if (reported === 'working' && s.attention) setAttention(s, false);
  else if (reported === 'done' && was === 'working' && !s.replaying && state.expandedId !== s.id) setAttention(s, true);
  else if (a.state !== was) renderTileState(s);
}

// Activity: Claude's spinner redraws continuously while it works and goes quiet when it is waiting on
// you, so a sustained burst of output followed by silence means "your turn" → flag the tile. Not for an agent that
// reports its state (see reportState).
function noteActivity(s) {
  const now = performance.now();
  const a = s.act;
  if (now - a.lastIn < 400 || s.reportsState) { a.lastOut = now; return; }   // echo of our own typing
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
    if (a.state === 'working' && !s.reportsState && now - a.lastOut > 2500) {
      a.state = 'idle';
      // The output a program writes as it starts (Claude's start-up, a dev server's build) isn't a turn finished: it's
      // ready, not waiting for you. Once it's been given something to do (see term.onData), quiet means your turn.
      const startUp = a.starting;
      a.starting = false;
      if (!startUp && now - a.workingSince > 4000 && state.expandedId !== s.id) setAttention(s, true);
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

/** Presses Enter in the session for LlamaTree (sending a prompt or a notice typed into it): it now has something to do. */
function pressEnter(s) {
  s.act.starting = false;
  post({ type: 'input', id: s.id, data: '\r' });
}

function setAttention(s, on) {
  if (s.attention === on) return;
  s.attention = on;
  renderTileState(s);
  syncControlStatus(s);
  renderDocTabs();
}

function setAsking(s, on) {
  if (s.asking === on) return;
  s.asking = on;
  renderTileState(s);
  syncControlStatus(s);
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
  relayNotify(s);   // an idle session told of messages it has
}

/** A project tab's dot: hopping while one of its sessions asks you something, else spinning while one works. */
function projectDot(members) {
  if (members.some((x) => x.running && x.asking)) return h('span', { class: 'dot asking', title: 'A session needs your answer' });
  if (members.some((x) => x.running && x.act.state === 'working')) return h('span', { class: 'dot working', title: 'A session is working' });
  return null;
}

/** Refreshes one project tab's dot and waiting badge in place (rebuilding the bar would drop keyboard focus). */
function updateProjectIndicators(projectId) {
  if (tabbedProjects() && !isPinned(projectId)) updateProjectMoreIndicators();
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
    serverTag(s),
    projectTag(s),
    relayTileMark(s),
    startedByMark(s),
    h('span', { class: 'tile-title', title: s.title }, s.title),
    gs && h(gitTabOn('changes') ? 'button' : 'span', {
      class: 'chip', title: `${gs.count} changed file(s) on ${gs.branch}${gitTabOn('changes') ? ' — show in Git Changes' : ''}`,
      onclick: gitTabOn('changes') && (() => showGit(s.id)),
    }, iconEl('branch'), gs.branch, gs.ahead ? ` ↑${gs.ahead}` : '', gs.count ? h('span', { class: 'mod' }, ` ●${gs.count}`) : ''),
    h('button', { class: 'icon-btn', title: 'Session options', onclick: (e) => { const r = e.currentTarget.getBoundingClientRect(); sessionMenu(s, r.left, r.bottom + 2); } }, iconEl('more')),
    expanded && h('button', { class: 'icon-btn', title: 'Back to all sessions (Shift+Esc)', onclick: collapse }, iconEl('collapse')),
  );
  s.tile.classList.toggle('on-server', !!s.server);   // its edge says it runs elsewhere
  s.tile.classList.toggle('server-down', serverDown(s));
  const away = serverDown(s);   // a session on a server that can't be reached: why, rather than whether it runs
  s.exitedBadge.hidden = !away && (s.running || !(s.started || s.startsOnOpen));
  s.exitedBadge.textContent = away ? serverStateText(serverOf(s))
    : s.startsOnOpen ? 'Starts when you open it' : s.exitCode === null ? 'Not running' : `Exited (${s.exitCode})`;
  renderTileState(s);
  syncControlStatus(s);
}

function sessionTabTitle(s) {
  return [s.directory, s.git?.branch && `Branch: ${s.git.branch}`, s.server && serverTitle(serverOf(s)), statusText(s), conversationLine(s), 'Click again to go back to all sessions · middle-click to close'].filter(Boolean).join('\n');
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

/**
 * A session's tab, `i` its place among the visible sessions (for its Ctrl+1…9). The label and the close button are
 * sibling buttons, since one button can't contain another: both stay reachable with Tab, and Enter or Space on the label
 * switches sessions. A click elsewhere on the tab counts as the label's. With `marks` (the list down the left), it also
 * shows which agent the session runs and where (see sessionMarks).
 */
function sessionTab(s, i, marks = false) {
  return h('div', {
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
    statusDot(s),
    // Down the left, its branch beneath the name.
    marks ? h('span', { class: 'tab-text' }, h('span', { class: 'tab-name' }, s.name), tabBranch(s)) : h('span', { class: 'tab-name' }, s.name),
    // Down the left, icons for its agent and where it runs; across the top, the server's name for a session on one.
    marks ? sessionMarks(s) : s.server &&h('span', { class: 'tab-server' + (serverDown(s) ? ' down' : ''), title: serverTitle(serverOf(s)) }, iconEl('server'), h('span', {}, serverName(serverOf(s)))),
    i < 9 ? h('span', { class: 'key' }, `Ctrl+${i + 1}`) : null),
  // Closing a session tab removes the session, after the same confirmation as "Remove session…".
  h('button', { class: 'icon-btn small tab-close', title: 'Close session…', onclick: () => confirmRemove(s) }, iconEl('close')));
}

/** The branch the session's folder is on, for its row down the left; always there (empty until Git has said, or outside a repository) so it updates in place. */
function tabBranch(s) {
  const b = s.git?.branch || '';
  return h('span', { class: 'tab-branch' }, b && iconEl('branch'), b && h('span', {}, b));   // empty (and hidden) without one
}

/** Puts the session's branch on its tabs after a Git status: beneath the name down the left, and in their tooltips. */
function updateTabBranch(s) {
  for (const tab of document.querySelectorAll(`.session-tab[data-id="${s.id}"]`)) {
    tab.title = sessionTabTitle(s);
    tab.querySelector('.tab-branch')?.replaceWith(tabBranch(s));
  }
}

const AGENT_ICONS = { claude: 'agentClaude', codex: 'agentCodex' };   // by AgentProfile.Id

/**
 * Two small icons: the agent the session runs (Claude or Codex in its colour, AgentProfile.Color; a terminal for any
 * other command) and where it runs (this computer, or its server, in red while that can't be reached).
 */
function sessionMarks(s) {
  const a = agentOf(s.command);
  const glyph = a ? AGENT_ICONS[a.id] ?? 'sparkle' : 'terminal';   // an agent with no icon of its own gets a generic one
  return h('span', { class: 'session-marks' },
    h('span', {
      class: 'session-mark agent' + (agentStyle(a) ? ' colored' : ''),
      style: agentStyle(a),
      title: a ? `Runs ${a.name}` : `Runs ${(s.command || '').trim().split(' ')[0] || 'a command'}: not a coding agent`,
    }, iconEl(glyph)),
    h('span', {
      class: 'session-mark where' + (s.server ? ' remote' : '') + (serverDown(s) ? ' down' : ''),
      title: s.server ? `${serverName(serverOf(s))}: ${serverStateText(serverOf(s))}` : 'Runs on this computer',
    }, iconEl(s.server ? 'server' : 'computer')));
}

/** The + at the end of a project's sessions (or of them all, without projects): another session like one of them. */
function sessionPlus(sessions, where) {
  const like = sessions.find((s) => s.id === state.currentId) || sessions[0];
  return h('button', {
    class: 'icon-btn small session-add', 'aria-label': `New session ${where}`,
    title: `New session ${where}: in the same folder as ${like.name}${like.git?.isRepo ? ", in a new worktree" : ""}, or in a different folder`,
    onclick: (e) => quickAddMenu(like, e.currentTarget),
  }, iconEl('plus'));
}

function renderSessionTabs() {
  const host = $('#sessionTabs');
  // Visible sessions only, grouped by project. Each group is outlined in its project's colour and named once at its
  // start, so the tabs themselves carry no project tag. In the Left layout they're listed down the left instead.
  const shown = state.navLayout === 'left' ? [] : visibleSessions();
  const tabs = shown.map((s, i) => sessionTab(s, i));
  if (state.navLayout === 'left') {
    host.replaceChildren();
    renderNav();
  } else if (!state.projects.length) {
    // Nothing to group by: the sessions are still outlined as one group, as Unknown's would be, but with no name.
    host.replaceChildren(...(shown.length ? [h('div', { class: 'session-group unknown unnamed' }, tabs, sessionPlus(shown, 'like these'))] : []));
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
    sessionPlus(g.sessions, g.color ? `in ${g.name}` : 'with no project'))));
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
  for (const s of state.sessions) { fitTerm(s); syncPtySize(s); }
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

/** The app's shortcut modifier: ⌘ on a Mac, Ctrl elsewhere. */
const mod = (e) => (IS_MAC ? e.metaKey : e.ctrlKey);

/** Returns true for app-level shortcuts that should not reach the terminal. */
function handleShortcut(e) {
  if (e.type !== 'keydown') return false;
  const k = e.key;
  if (k === 'Escape' && e.shiftKey && !mod(e) && !e.altKey) {
    if (!$('#modalHost').hidden) closeModal();
    else if (activeTab()) showTerminalDoc();
    else collapse();
    return true;
  }
  // The physical key, not the character: with some keyboard layouts Ctrl plus a number key doesn't produce "1"…"9".
  const digit = /^(?:Digit|Numpad)([1-9])$/.exec(e.code)?.[1] ?? (/^[1-9]$/.test(k) ? k : null);
  if (mod(e) && !e.altKey && !e.shiftKey && digit) {
    const s = visibleSessions()[+digit - 1];   // matches the Ctrl+N hints on the visible tabs
    if (s) expand(s.id);
    return true;
  }
  if (mod(e) && !e.shiftKey && (k === 'PageUp' || k === 'PageDown')) { cycleDoc(k === 'PageUp' ? -1 : 1); return true; }
  if (mod(e) && k === 'F4') { if (activeTab()) closeDoc(docs.active); return true; }
  // Ctrl+W closes a diff or Conversation tab. On the terminal it isn't taken: it deletes a word in Claude's prompt.
  if (mod(e) && !e.shiftKey && !e.altKey && (k === 'w' || k === 'W') && activeTab()) { closeDoc(docs.active); return true; }
  if (e.ctrlKey && k === 'Tab') { cycle(e.shiftKey ? -1 : 1); return true; }
  if (mod(e) && e.shiftKey && (k === 'G' || k === 'g')) { toggleGit(); return true; }
  if (mod(e) && e.shiftKey && (k === 'N' || k === 'n')) { showAddDialog(); return true; }
  if (mod(e) && !e.shiftKey && (k === '=' || k === '+')) { setFontSize(state.fontSize + 1); return true; }
  if (mod(e) && !e.shiftKey && k === '-') { setFontSize(state.fontSize - 1); return true; }
  if (mod(e) && !e.shiftKey && k === '0') { setFontSize(14); return true; }
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
  // On a Mac ⌘C, ⌘V and ⌘A copy, paste and select all, and Ctrl+C and Ctrl+V go to the program as themselves.
  if (IS_MAC) {
    if (!e.metaKey || e.ctrlKey || e.altKey) return true;
    const key = e.key.toLowerCase();
    if (key === 'c') { if (s.term.hasSelection()) navigator.clipboard.writeText(s.term.getSelection()); }
    else if (key === 'v') pasteClipboard(s);
    else if (key === 'a') s.term.selectAll();
    else return true;
    e.preventDefault();
    return false;
  }
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
    e.preventDefault();   // WebKitGTK pastes on Ctrl+Shift+V by itself too, which would paste twice
    pasteClipboard(s);
    return false;
  }
  return true;
}

/** Pastes the clipboard's text or, without text, the paths of the image or files on it (see pasteClipboardFiles). */
async function pasteClipboard(s) {
  const text = await readClipboardText().catch(() => '');
  if (text) s.term.paste(text);
  else pasteClipboardFiles(s);
}

/**
 * An image on the clipboard (a screenshot, a copied picture) is saved as a PNG by the host and its path pasted; files
 * copied in Explorer paste as their paths. Claude Code and Codex attach an image whose path is pasted.
 */
async function pasteClipboardFiles(s) {
  try {
    let paths = await request('clipboard.files');
    // A session on a server gets copies there: their paths there are pasted.
    if (paths?.length && s.server) paths = await request('files.upload', { id: s.id, paths });
    if (paths?.length) s.term.paste(paths.map((p) => (/\s/.test(p) ? `"${p}"` : p)).join(' '));
  } catch (err) {
    toast(err.message, true);
  }
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
  // Items: '-' for a separator ('~' for a lighter, shorter one within a section), { header } for a section title, or { label, action } with optional kbd, danger,
  // checked (ticked when true) and icon (a name from PATHS, or an element such as a plugin's image), which share a column
  // at the start: a ticked item shows the tick in place of its icon. If any item has either, all get the column.
  const lead = items.some((it) => it && (it.checked !== undefined || it.icon));
  const menu = h('div', {
    class: 'menu', onmousedown: (e) => e.preventDefault(),
    onmousemove: (e) => e.currentTarget.querySelector('.menu-item.active')?.classList.remove('active'),   // the pointer takes over
  }, items.filter(Boolean).map((it) => it === '-' || it === '~'
    ? h('div', { class: 'menu-sep' + (it === '~' ? ' subtle' : '') })
    : it.header
      ? h('div', { class: 'menu-header' }, it.header)
      : h('div', { class: 'menu-item' + (it.danger ? ' danger' : ''), onclick: () => { closeMenu(); it.action(); } },
        lead && h('span', { class: 'menu-icon' }, it.checked ? '✓' : typeof it.icon === 'string' ? iconEl(it.icon) : it.icon ?? ''),
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
  // A menu with a box to type in (the branch picker's filter) keeps Space, Home and End for the box.
  const typing = e.target instanceof HTMLInputElement && openMenuEl.contains(e.target);
  const move = { ArrowDown: at + 1, ArrowUp: (at < 0 ? items.length : at) - 1, ...(typing ? {} : { Home: 0, End: items.length - 1 }) }[e.key];
  if (move !== undefined) {
    items[at]?.classList.remove('active');
    const next = items[(move + items.length) % items.length];
    next?.classList.add('active');
    next?.scrollIntoView({ block: 'nearest' });
    return true;
  }
  if ((e.key === 'Enter' || (e.key === ' ' && !typing)) && at >= 0) { items[at].click(); return true; }
  if (e.key === 'Escape' && !e.shiftKey) { closeMenu(); return true; }
  return false;
}

/** from: 'session' (the tile's ⋯ button or header) or 'sessionTab' (the session tab), for plugins' items (see pluginMenuItems). */
function sessionMenu(s, x, y, from = 'session') {
  const c = serverOf(s);
  showMenu(x, y, [
    c && c.state !== 'online' && { header: serverStateText(c) },
    c?.notInstalled && { label: `Install LlamaTree on ${c.target}…`, action: () => installServer(c.target) },
    c && ['offline', 'elsewhere'].includes(c.state) && { label: `Reconnect to ${c.target}`, action: () => request('servers.reconnect', { server: c.target }).catch((e) => toast(e.message, true)) },
    c && c.state !== 'online' && '-',
    c?.state === 'online' && c.outdated && { header: serverVersionText(c) },
    c?.state === 'online' && c.outdated && { label: `Update LlamaTree on ${serverName(c)}…`, action: () => updateServer(c) },
    c?.state === 'online' && c.outdated && '-',
    state.expandedId === s.id ? { label: 'Back to all sessions', kbd: 'Shift+Esc', action: collapse } : { label: 'Expand', action: () => expand(s.id) },
    gitTabOn('changes') && { label: 'Show Git changes', action: () => showGit(s.id) },
    { label: 'Show conversation', action: () => openConversation(s) },
    { label: 'Permissions…', action: () => openPermissions('session', s.id) },
    { label: `Project view: ${projectName(groupOf(s))}${projectName(groupOf(s)).endsWith('s') ? '’' : '’s'} worktrees side by side`, action: () => openProjectView(groupOf(s)) },
    '-',
    { label: 'Resume a different conversation…', action: () => showResumeDialog(s) },
    { label: 'Rename / edit command…', action: () => showEditDialog(s) },
    { label: 'Move to project…', action: () => showEditDialog(s, 'project') },
    { label: s.running ? 'Restart session' : 'Start session', action: () => (s.running ? confirmRestart(s) : restartSession(s)) },
    s.running && { label: 'Stop session', action: () => confirmStop(s) },
    { label: machine || s.server ? 'Browse the folder' : `Open folder in ${IS_MAC || IS_LINUX ? FILE_MANAGER : 'Explorer'}`, action: () => request('openFolder', { id: s.id }) },
    '-',
    { label: 'Move left', action: () => moveSession(s, -1) },
    { label: 'Move right', action: () => moveSession(s, 1) },
    '-',
    { label: 'Remove session…', danger: true, action: () => confirmRemove(s) },
    ...pluginMenuItems(from, pluginSessionContext(s)),
  ]);
}

function terminalMenu(s, x, y) {
  showMenu(x, y, terminalMenuItems(s));
}

function terminalMenuItems(s) {
  // These leave you in the terminal, ready to type (Enter after Paste, say).
  const inTerminal = (fn) => async () => { await fn(); s.term.focus(); };
  return [
    { label: 'Copy', kbd: 'Ctrl+C', action: inTerminal(() => s.term.hasSelection() && navigator.clipboard.writeText(s.term.getSelection())) },
    { label: 'Paste', kbd: 'Ctrl+V', action: inTerminal(() => pasteClipboard(s)) },
    { label: 'Select all', action: inTerminal(() => s.term.selectAll()) },
    { label: 'Clear scrollback', action: inTerminal(() => s.term.clear()) },
    '-',
    { label: 'Show conversation', action: () => openConversation(s) },
    { label: 'Back to all sessions', kbd: 'Shift+Esc', action: collapse },
  ];
}

// ---------------------------------------------------------------- file paths in a terminal

// A word that may be a path (no spaces), and a quoted one (which may have spaces): "C:\My Files\a.txt", 'a b/c.md'.
// Both are looked for, as apostrophes in prose can look like quotes around a path.
const PATH_WORD = /[^\s"'`<>|,;()[\]{}]+/g;
const QUOTED_PATH = /"([^"\n]{2,})"|'([^'\n]{2,})'|`([^`\n]{2,})`/g;

/**
 * The path in a word of terminal output, or null if it can't be one: unless it was quoted, it needs a folder
 * separator, a file extension or to be ~. Sentence punctuation after it and a :line or :line:column (as compilers and
 * Claude write them) aren't part of it. Links (anything://), options (-x) and user@host:path aren't paths.
 */
function termPathText(word, quoted = false) {
  if (word === '~') return word;
  let t = word.replace(/[.,;:!?]+$/, '').replace(/:\d+(?::\d+)?$/, '');
  if (t.length < 2 || t.length > 1024 || t.includes('://') || t.startsWith('-') || /^[\d.]+$/.test(t)) return null;
  if (/^[a-z][\w+.@-]*:/i.test(t) && !/^[a-z]:[\\/]/i.test(t)) return null;
  return quoted || /[\\/]/.test(t) || /\.[A-Za-z][A-Za-z0-9]{0,7}$/.test(t) ? t : null;
}

/**
 * Handles OSC 8 hyperlinks (a URL behind other text) in a terminal's output: they open through openUrl like any other
 * link (the host opens http/https only), and where the text shown isn't the URL itself, the real URL is shown first and
 * the person confirms. xterm passes only http/https links here (allowNonHttpProtocols: false).
 */
function oscLinkHandler(s) {
  const shownText = (range) => {
    const buf = s.term.buffer.active;
    let text = '';
    for (let y = range.start.y; y <= range.end.y; y++) {
      const line = buf.getLine(y - 1);
      if (!line) break;
      text += line.translateToString(true, y === range.start.y ? range.start.x - 1 : 0, y === range.end.y ? range.end.x : undefined);
    }
    return text.trim();
  };
  const same = (a, b) => a.replace(/\/+$/, '') === b.replace(/\/+$/, '');
  return {
    allowNonHttpProtocols: false,
    activate(ev, uri, range) {
      const open = () => request('openUrl', { uri }).catch((err) => toast(err.message, true));
      const text = shownText(range);
      if (same(text, uri)) return open();
      openModal('Open link', h('div', {},
        h('div', {}, 'The link says:'),
        h('div', { class: 'link-confirm-url' }, text || '(no text)'),
        h('div', {}, 'but it opens:'),
        h('div', { class: 'link-confirm-url' }, uri)), [
        h('button', { class: 'btn', onclick: closeModal, 'data-autofocus': true }, 'Cancel'),
        h('button', { class: 'btn primary', onclick: () => { closeModal(); open(); } }, 'Open'),
      ]);
    },
  };
}

/**
 * Makes the file and folder paths in a terminal's output links, once the host has said they're there (relative ones
 * are from the session's folder). Click opens one (a file is opened for viewing or editing, never run); its right-click
 * menu also opens the folder it's in. Paths wrapped onto the next row are found whole.
 */
function pathLinkProvider(s) {
  const cache = new Map();   // path text -> { found, at }: what the host said, rechecked after a while
  const fresh = (c) => c && performance.now() - c.at < (c.found ? 30000 : 3000);
  return {
    provideLinks(y, callback) {
      const buf = s.term.buffer.active;
      // The whole logical line this row is part of, with where each character is on screen.
      let first = y - 1;
      while (first > 0 && buf.getLine(first)?.isWrapped && y - 1 - first < 8) first--;
      let text = '';
      const at = [];
      for (let row = first; row < first + 16; row++) {
        const line = buf.getLine(row);
        if (!line || (row > first && !line.isWrapped)) break;
        for (let x = 0; x < line.length; x++) {
          const cell = line.getCell(x);
          if (!cell || cell.getWidth() === 0) continue;
          const chars = cell.getChars() || ' ';
          for (let i = 0; i < chars.length; i++) at.push({ x: x + 1, y: row + 1 });
          text += chars;
        }
      }
      const words = [];
      const add = (word, start, quoted = false) => {
        const path = termPathText(word, quoted);
        if (!path) return;
        const end = start + path.length - 1;
        if (at[start]?.y > y || at[end]?.y < y) return;   // not on this row
        words.push({ path, start, end });
      };
      for (const m of text.matchAll(QUOTED_PATH)) add(m[1] ?? m[2] ?? m[3], m.index + 1, true);
      for (const m of text.matchAll(PATH_WORD)) add(m[0], m.index);
      if (!words.length) return callback(undefined);
      const ask = [...new Set(words.map((w) => w.path).filter((p) => !fresh(cache.get(p))))];
      const lookup = ask.length ? request('terminal.paths', { id: s.id, paths: ask }) : Promise.resolve([]);
      lookup.then((found) => {
        ask.forEach((p, i) => cache.set(p, { found: found?.[i] ?? null, at: performance.now() }));
        if (cache.size > 500) cache.clear();
        const real = words.filter((w) => cache.get(w.path)?.found);
        // Where a quoted path and a word inside it are both there, the longer (the quoted one) is the link.
        const kept = real.filter((w) => !real.some((o) => o !== w && o.start <= w.start && o.end >= w.end && o.end - o.start > w.end - w.start));
        callback(kept.map((w) => {
          const link = cache.get(w.path).found;
          return {
            range: { start: at[w.start], end: at[w.end] },
            text: w.path,
            decorations: { underline: true, pointerCursor: true },
            activate: (e) => { if (e.button === 0) openTermPath(s, link); },
            hover: () => {
              s.hoverPath = link;
              s.surface.title = `${link.path}\nClick to open · right-click to open the folder it's in`;
            },
            leave: () => {
              if (s.hoverPath === link) s.hoverPath = null;
              s.surface.removeAttribute('title');
            },
          };
        }));
      }, () => callback(undefined));
    },
  };
}

function openTermPath(s, link, inFolder = false) {
  request('terminal.openPath', { id: s.id, path: link.path, inFolder }).catch((e) => toast(e.message, true));
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
  `Remove "${s.name}" from LlamaTree?${s.running ? ' Its running process will be terminated.' : ''}\nThe folder and its files are not touched.`,
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
    class: 'conv-agent' + (agentStyle(a) ? ' colored' : ''),
    style: agentStyle(a),
  }, a?.name ?? id);
}

const leafName = (path) => (path || '').split(/[\\/]/).filter(Boolean).pop() || path;

/**
 * Searchable list of Claude Code's and Codex's saved conversations (optionally limited to one folder, or one agent's).
 * onPick(conv, confirm) fires on click; confirm is true for a double-click. Each conversation's agent is conv.agent.
 */
function conversationPicker({ directory = null, agent = null, server = null, onPick }) {
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
  request('conversations', { directory, agent, ...onServer(server) })
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
  const dir = h('input', { value: from?.directory ?? '', spellcheck: 'false' });
  // Which computer the session runs on: this one, or a server whose sessions show here too (LlamaTree installed there,
  // reached with ssh as for a server's window). A window showing one server's sessions adds them there.
  const NEW_SERVER = '*new';
  const knownServers = [...new Set([...state.sessions.map((x) => x.server), ...servers.keys(), ...store.get('servers', [])].filter(Boolean))];
  const computer = h('select', {},
    h('option', { value: '' }, 'This computer'),
    knownServers.map((t) => h('option', { value: t }, t)),
    h('option', { value: NEW_SERVER }, 'A server…'));
  computer.value = from?.server && knownServers.includes(from.server) ? from.server : '';
  const newServer = h('input', { placeholder: 'user@server, or a Host from your SSH config', spellcheck: 'false', hidden: true });
  const serverValue = () => (computer.value === NEW_SERVER ? newServer.value.trim() || null : computer.value || null);
  const setPlaceholder = () => { dir.placeholder = machine || serverValue() || IS_LINUX ? '/home/you/my-repo' : IS_MAC ? '/Users/you/my-repo' : 'D:\\Source\\MyRepo'; };
  setPlaceholder();
  const computerChanged = () => {
    newServer.hidden = computer.value !== NEW_SERVER;
    if (!newServer.hidden && document.activeElement === computer) newServer.focus();
    browserHost.hidden = true;
    wtHost.hidden = true;
    setPlaceholder();
    if (mode === 'resume') setMode('resume');   // that computer's conversations
    else loadWorktrees();
  };
  computer.addEventListener('change', computerChanged);
  newServer.addEventListener('change', computerChanged);
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
    try { worktrees = (await request('worktrees', { directory: d, ...onServer(serverValue()) })) || []; } catch { worktrees = []; }
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

  // A server's folders are browsed in the page (this computer's folder picker can't reach them), below the folder box.
  const browserHost = h('div', { class: 'field', hidden: true });
  const browse = h('button', {
    class: 'btn',
    onclick: async () => {
      if (machine || serverValue()) {
        browserHost.hidden = !browserHost.hidden;
        if (!browserHost.hidden) browserHost.replaceChildren(serverBrowser({
          server: serverValue(),
          path: dir.value.trim() || (serverValue() ? '' : lastDir),
          onPick: (p) => { dir.value = p; browserHost.hidden = true; loadWorktrees(); dir.focus(); },
        }));
        return;
      }
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
      const server = serverValue();
      if (computer.value === NEW_SERVER && !server) throw new Error('Enter the server as user@server, or a Host from your SSH config.');
      await request('addSessions', { sessions: list, ...onServer(server) });
      if (server) store.set('servers', [server, ...store.get('servers', []).filter((x) => x !== server)].slice(0, 8));
      // Make sure the new session is on screen even if its project was hidden.
      if (projectId && state.hiddenProjects.has(projectId)) toggleProject(projectId);
      if (!server) store.set('lastDir', d);
      if (mode === 'new') store.set('lastCommand', cmd);
      closeModal();
      const added = state.sessions.find((x) => !before.has(x.id));
      if (added) expand(added.id);
    } catch (e) {
      error.hidden = false;
      error.textContent = e.message;
      const server = serverValue();
      if (server && /not installed on this server/.test(e.message))
        error.append(' ', h('button', { class: 'btn', onclick: () => installServer(server) }, `Install LlamaTree on ${server}…`));
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
        server: serverValue(),
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
    !machine && field('Computer', h('div', { class: 'inline' }, computer, newServer), 'This computer, or a server: the session runs there, and shows here with the others.'),
    pickerHost,
    field('Folder (repository or worktree)', h('div', { class: 'inline' }, dir, browse)),
    browserHost,
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
    { label: 'A different folder…', action: () => showAddDialog('new', { directory: s.directory, project: s.project ?? null, server: s.server ?? null }) },
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
  await request('addSessions', { sessions: [item], ...onServer(item.server) });
  if (item.project && state.hiddenProjects.has(item.project)) toggleProject(item.project);
  const added = state.sessions.find((x) => !before.has(x.id));
  if (added) expand(added.id);
}

/** One click: another session in the same folder and project, with a conversation of its own. */
function quickSession(s) {
  addAndOpen({ directory: s.directory, name: freeName(s.name), command: ownOptionsOnly(s.command), project: s.project ?? '', server: s.server ?? undefined })
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
      await addAndOpen({ directory: path, name: freeName(name.value.trim() || b), command: ownOptionsOnly(s.command), project: s.project ?? '', server: s.server ?? undefined });
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
      server: s.server,
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
    h('div', {}, `Earlier versions of LlamaTree didn't keep track of which conversation ${one ? 'this session was' : 'these sessions were'} in, ` +
      `so ${one ? 'it starts' : 'each starts'} a new one, and reopens that one from now on:`),
    h('ul', { class: 'name-list' }, names.map((n) => h('li', {}, n))),
    h('div', { class: 'dim' }, 'To go back to an earlier conversation, pick it with Resume a different conversation… in the session’s menu.'),
  ], [h('button', { class: 'btn primary', onclick: closeModal }, 'OK')]);
}

// ------------------------------------------------------------------ Git tool window

function applyGitVisibility() {
  document.body.classList.toggle('git-hidden', !state.gitVisible);
  $('#gitPanel').style.width = state.gitWidth + 'px';
  applySideEmpty();
}

/** Anything in the side panel for the selected session: the Git tabs, or (with those off) a panel or plugin. */
const sideHasTabs = () => dock.groups.some((g) => groupTabs(g).length > 0);

/**
 * With the Git tabs off, the side panel shows only while it has a panel or plugin to show, and its title bar button
 * stands for the side panel rather than Git. The button stays, so its ▾ can always bring the Git tabs back.
 */
function applySideEmpty() {
  const empty = !sideHasTabs();
  document.body.classList.toggle('side-empty', empty);
  const btn = $('#btnGit');
  btn.classList.toggle('active', state.gitVisible && !empty);
  const git = anyGitTab();
  btn.innerHTML = icon(git ? 'git' : 'panel');
  btn.title = git ? 'Git and the side panel (Ctrl+Shift+G)'
    : empty ? 'Side panel: nothing open (click for its options)' : 'Side panel: panels and plugins (Ctrl+Shift+G)';
  $('#gitRefresh').hidden = !git;
}

/** Whether a Git tab ('changes' or 'repo') is turned on in the side panel's ▾. */
const gitTabOn = (t) => !state.hiddenGitTabs.has(t);
const anyGitTab = () => Object.keys(GIT_TABS).some(gitTabOn);

/** Shows or hides Git Changes or Git Repository; where it sat in the side panel is kept for when it's back. */
function setGitTab(t, on) {
  if (on) state.hiddenGitTabs.delete(t); else state.hiddenGitTabs.add(t);
  store.set('hiddenGitTabs', [...state.hiddenGitTabs]);
  for (const s of state.sessions) renderTileHeader(s);   // the branch chip opens Git Changes only while it's on
  if (on) { openDockTab(t); return; }   // turned on: open it, showing the side panel if need be
  applyGitVisibility();
  renderGit();
  toast(`${GIT_TABS[t]} is hidden. Bring it back from the ▾ beside the side panel button.`);
}

/** Opens a side panel tab (a panel or plugin), showing the side panel if it's hidden. */
function openDockTab(t) {
  if (!state.gitVisible) { state.gitVisible = true; store.set('gitVisible', true); applyGitVisibility(); }
  activateTab(groupOfTab(t), t);
}

/** The side panel button's ▾: the Git tabs on or off, what's open in the panel to jump to, and the layout. */
function sidePanelMenu() {
  const r = $('#btnGitMenu').getBoundingClientRect();
  const open = dock.groups.flatMap(groupTabs).filter((t) => isPanelTab(t) || isPluginDockTab(t));
  const empty = !sideHasTabs();
  showMenu(r.right - 280, r.bottom + 2, [
    { header: 'Side panel' },
    !empty && { icon: 'panel', label: state.gitVisible ? 'Hide the side panel' : 'Show the side panel', kbd: 'Ctrl+Shift+G', action: toggleGit },
    ...Object.entries(GIT_TABS).map(([t, label]) => ({ icon: 'git', label, checked: gitTabOn(t), action: () => setGitTab(t, !gitTabOn(t)) })),
    open.length > 0 && '-',
    open.length > 0 && { header: 'Open in the side panel' },
    ...open.map((t) => isPluginDockTab(t)
      ? { icon: pluginIcon(pluginOfTab(t)), label: pluginCaption(pluginOfTab(t)), action: () => openDockTab(t) }
      : { icon: 'list', label: panelByTab(t).title, action: () => openDockTab(t) }),
    ...reopenItems(),
    '-',
    { label: 'Reset layout', action: resetDock },
  ]);
}

function toggleGit() {
  if (!sideHasTabs()) { sidePanelMenu(); return; }   // nothing to show: offer the way to turn the Git tabs back on
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
  updateTabBranch(s);
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
  const pluginGone = (t) => plugins.loaded && (!pluginById(pluginOfTab(t)) || pluginPlacement(pluginById(pluginOfTab(t))) !== 'panel');
  for (const g of dock.groups) {
    const kept = g.tabs.filter((t) => isPluginDockTab(t) ? !pluginGone(t) : !isPanelTab(t) || live.has(t));
    if (kept.length !== g.tabs.length) { g.tabs = kept; changed = true; }
  }
  for (const t of live) if (!groupOfTab(t)) { dock.groups[dock.groups.length - 1].tabs.push(t); changed = true; }
  for (const g of [...dock.groups]) if (!g.tabs.length) { removeGroup(g); changed = true; }
  if (changed) saveDock();
}

/** The tabs a group shows for the selected session: the Git ones unless they're turned off, panels when they show with it. */
const groupTabs = (g) => g.tabs.filter((t) => isPluginDockTab(t) ? !!pluginById(pluginOfTab(t))
  : isPanelTab(t) ? !!panelByTab(t) && panelVisibleFor(panelByTab(t), currentSession())
  : gitTabOn(t));

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
  applySideEmpty();   // with the Git tabs off, the side panel comes and goes with its panels
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
      }, pluginIcon(id), h('span', { class: 'panel-tab-title' }, pluginCaption(id)), pluginBadge(id));
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
  // More tabs than fit the width go on to a second line (see .tw-tabs in app.css).
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
    // A plugin's tab let go over the document area moves the plugin there, as a tab.
    if (isPluginDockTab(press.tab) && el?.closest('#docArea, #docTabs')) return { kind: 'doc', label: 'As a tab', rect: $('#docArea').getBoundingClientRect() };
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
    drag.hint.classList.toggle('labelled', !!t.label);
    drag.hint.textContent = t.label ?? '';
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
    if (drop && was.target?.kind === 'doc') movePlugin(pluginOfTab(press.tab), 'tab', null);
    else if (drop && was.target) moveTab(press.tab, was.target);
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
      isPluginDockTab(t) && { label: 'Open as a tab', action: () => movePlugin(pluginOfTab(t), 'tab', null) },
      isPluginDockTab(t) && { label: 'Close plugin', action: () => closeDockPlugin(t) },
      ...reopenItems(),
      '-',
      t in GIT_TABS && { label: `Hide ${GIT_TABS[t]}`, action: () => setGitTab(t, false) },
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
      h('button', {
        class: 'branch-btn', title: `${g.upstream ? `Tracking ${g.upstream}` : 'No upstream branch'}\nSwitch branches or create a new one`,
        onclick: (e) => branchPicker(s, e.currentTarget),
      }, iconEl('branch'), h('span', { class: 'name' }, g.branch || '(unknown)'), iconEl('chevD')),
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
    { label: 'New Branch…', icon: 'newBranch', action: () => showNewBranchDialog(s) },
    free && '-',
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

/**
 * New Branch, after Visual Studio's: a name, what it's based on (a local or remote branch, or the commit it was opened
 * from in Git Repository) and whether to switch to it. Switching with uncommitted changes asks first whether to bring
 * them to the new branch or stash them (see askAboutChanges). A failure comes back to this dialog, as it was filled in.
 */
function showNewBranchDialog(s, { base = null, commit = null, name = '', checkout = true, error = null } = {}) {
  const g = s.git;
  if (!g?.isRepo) return;
  const current = g.branch && !g.branch.startsWith('(') ? g.branch : null;
  const branch = h('input', { value: name, placeholder: 'feature/my-change', spellcheck: 'false' });
  const shortSha = (sha) => sha.slice(0, 7);
  // The list fills in once the branches arrive; until then it holds just what it starts on.
  const start = base ?? commit?.sha ?? current ?? 'HEAD';
  const startLabel = commit && start === commit.sha ? `Commit ${shortSha(commit.sha)}: ${commit.subject}` : start === 'HEAD' ? 'HEAD (the current commit)' : start;
  const select = h('select', {}, h('option', { value: start }, startLabel));
  request('git.branches', { id: s.id }).then((all) => {
    const local = all.filter((b) => !b.remote).map((b) => b.name), remote = all.filter((b) => b.remote).map((b) => b.name);
    const chosen = select.value;
    const opt = (v, label = v) => h('option', { value: v, selected: v === chosen }, label);
    select.replaceChildren(...[
      commit && opt(commit.sha, `Commit ${shortSha(commit.sha)}: ${commit.subject}`),
      !current && opt('HEAD', 'HEAD (the current commit)'),
      local.length > 0 && h('optgroup', { label: 'Local' }, local.map((b) => opt(b))),
      remote.length > 0 && h('optgroup', { label: 'Remote' }, remote.map((b) => opt(b))),
    ].filter(Boolean));
    if (select.value !== chosen) select.prepend(opt(chosen, startLabel));   // e.g. a branch deleted meanwhile: keep it, git says if it's gone
  }).catch(() => { /* keep the one option; creating still works */ });
  const switchTo = h('input', { type: 'checkbox', checked: checkout });
  const err = h('div', { class: 'form-error', hidden: !error }, error ?? '');
  const create = h('button', { class: 'btn primary' }, 'Create');
  const go = () => {
    const b = branch.value.trim();
    if (!b) { err.hidden = false; err.textContent = 'Name the new branch.'; branch.focus(); return; }
    const opts = { base: select.value, commit, name: b, checkout: switchTo.checked };
    if (opts.checkout && changeCount(s)) askAboutChanges(s, opts.name, (stash, button) => createBranch(s, opts, stash, button), () => showNewBranchDialog(s, opts));
    else createBranch(s, opts, false, create);
  };
  create.addEventListener('click', go);
  branch.addEventListener('keydown', (e) => { if (e.key === 'Enter') go(); });
  openModal('Create a new branch', [
    field('Branch name', branch),
    field('Based on', h('div', { class: 'combo' }, select, h('span', { class: 'combo-btn' }, iconEl('chevD')))),
    h('label', { class: 'check-field' }, switchTo, 'Checkout branch'),
    err,
  ], [h('button', { class: 'btn', onclick: closeModal }, 'Cancel'), create]);
}

const changeCount = (s) => (s.git?.staged.length ?? 0) + (s.git?.unstaged.length ?? 0);

/**
 * Switching branches with uncommitted changes, as Visual Studio asks: bring them along (the default) or stash them.
 * run(stash, button) does the switch; back, if given, returns to the dialog this came from. An error from a try is
 * shown here, so the other choice can be made.
 */
function askAboutChanges(s, target, run, back = null, error = null) {
  const changes = changeCount(s);
  const bring = h('button', { class: 'btn primary' }, 'Bring the changes');
  const stash = h('button', { class: 'btn' }, 'Stash and continue');
  bring.addEventListener('click', () => run(false, bring));
  stash.addEventListener('click', () => run(true, stash));
  openModal('Uncommitted changes', [
    h('div', {}, `You have ${changes} uncommitted change${changes === 1 ? '' : 's'} on ${s.git.branch || 'the current branch'}. What should happen to ${changes === 1 ? 'it' : 'them'} on ${target}?`),
    h('ul', { class: 'dim' },
      h('li', {}, h('b', {}, 'Bring the changes'), `: they come along to ${target}, uncommitted, as they are now. Git refuses if one is in a file ${target} has differently.`),
      h('li', {}, h('b', {}, 'Stash and continue'), `: they are put away in a Git stash and ${target} starts clean. git stash pop brings them back.`)),
    h('div', { class: 'form-error', hidden: !error }, error ?? ''),
  ], [h('button', { class: 'btn', onclick: back ?? closeModal }, back ? 'Back' : 'Cancel'), stash, bring]);
}

/** Disables the open dialog's buttons and labels the one pressed while a branch operation runs. */
function busyButtons(button, label) {
  if (!button) return;
  for (const b of $('#modalHost').querySelectorAll('.modal-footer button')) b.disabled = true;
  button.textContent = label;
}

function branchDone(s, text) {
  closeModal();
  toast(text);
  invalidateLog(s);   // a new branch shows on its commit in Git Repository, even when not switched to
  renderGit();
}

async function createBranch(s, opts, stash, button) {
  if (button.disabled) return;
  busyButtons(button, 'Creating…');
  try {
    branchDone(s, await request('git.createBranch', { id: s.id, branch: opts.name, base: opts.base, checkout: opts.checkout, stash }));
  } catch (e) {
    showNewBranchDialog(s, { ...opts, error: e.message });
  }
}

/** Switches to a branch from the picker; from the changes prompt, button is the choice pressed and errors stay there. */
async function checkoutBranch(s, b, stash, button = null) {
  if (button?.disabled) return;
  busyButtons(button, 'Switching…');
  try {
    branchDone(s, await request('git.checkout', { id: s.id, branch: b.name, remote: b.remote, stash }));
  } catch (e) {
    if (button) askAboutChanges(s, localNameOf(b), (st, btn) => checkoutBranch(s, b, st, btn), null, e.message);
    else toast(e.message, true);
  }
}

/** The local branch a picked branch means: itself, or for origin/feature, feature. */
const localNameOf = (b) => (b.remote ? b.name.replace(/^[^/]+\//, '') : b.name);

/**
 * The branch picker, after Visual Studio's branch button: a filter, New Branch, and the local and remote branches in
 * two tabs. Picking one switches to it (a remote one through its local branch, made if needed), asking first what to
 * do with uncommitted changes. Arrows and Enter work from the filter (see menuKey).
 */
async function branchPicker(s, anchor) {
  // A second click on the button closes it: the press already did, as it was outside the picker.
  if (Date.now() - (+anchor.dataset.closedAt || 0) < 400) return;
  const filter = h('input', { class: 'bp-filter', placeholder: 'Filter branches', spellcheck: 'false' });
  const list = h('div', { class: 'bp-list' }, h('div', { class: 'git-message' }, 'Loading…'));
  let tab = 'local', branches = null;
  const tabBtn = (key, label) => h('button', {
    class: 'bp-tab', 'data-tab': key,
    onclick: () => { tab = key; fill(); filter.focus(); },
  }, label);
  const tabs = h('div', { class: 'bp-tabs' }, tabBtn('local', 'Locals'), tabBtn('remote', 'Remotes'));
  const here = (s.git?.root || s.directory).replace(/[\\/]+$/, '').toLowerCase();
  const pick = (b) => {
    closeMenu();
    if (!b.remote && b.name === s.git.branch) return;
    if (changeCount(s)) askAboutChanges(s, localNameOf(b), (stash, btn) => checkoutBranch(s, b, stash, btn));
    else checkoutBranch(s, b, false);
  };
  const row = (b) => {
    const current = !b.remote && b.name === s.git.branch;
    const elsewhere = !b.remote && !current && b.worktree && b.worktree.replace(/[\\/]+$/, '').toLowerCase() !== here ? b.worktree : null;
    const state = [b.ahead && `↑${b.ahead}`, b.behind && `↓${b.behind}`, b.gone && 'gone'].filter(Boolean).join(' ');
    return h('div', {
      class: 'menu-item bp-row' + (current ? ' current' : '') + (elsewhere ? ' elsewhere' : ''),
      title: [b.name, current && 'The current branch', b.upstream && `Tracking ${b.upstream}${b.gone ? ' (gone from the remote)' : ''}`,
        elsewhere && `Checked out in the worktree ${elsewhere}; Git can't check it out here too`].filter(Boolean).join('\n'),
      onclick: () => {
        if (elsewhere) { closeMenu(); toast(`${b.name} is checked out in the worktree ${elsewhere}, so it can't be checked out here too.`, true); return; }
        pick(b);
      },
    }, h('span', { class: 'menu-icon' }, iconEl(b.upstream ? 'branchTracked' : 'branch')),
    h('span', { class: 'bp-name' }, b.name),
    elsewhere ? h('span', { class: 'bp-note' }, `in ${leafName(elsewhere)}`) : state && h('span', { class: 'bp-note' }, state));
  };
  const fill = () => {
    for (const t of tabs.children) t.classList.toggle('active', t.dataset.tab === tab);
    if (!branches) return;
    const q = filter.value.trim().toLowerCase();
    const shown = branches.filter((b) => b.remote === (tab === 'remote') && (!q || b.name.toLowerCase().includes(q)));
    list.replaceChildren(...(shown.length ? shown.map(row)
      : [h('div', { class: 'git-message' }, q ? 'No branches match.' : tab === 'remote' ? 'No remote branches. Fetch to see them.' : 'No branches.')]));
    // Typing picks the first match, so Enter switches to it.
    if (q) list.querySelector('.menu-item')?.classList.add('active');
  };
  filter.addEventListener('input', fill);
  const popup = h('div', { class: 'menu branch-picker' },
    h('div', { class: 'bp-head' }, filter,
      h('button', { class: 'btn', onclick: () => { closeMenu(); showNewBranchDialog(s); } }, 'New Branch')),
    tabs, list);
  closeMenu();
  $('#menuHost').append(popup);
  const r = anchor.getBoundingClientRect();
  popup.style.left = Math.max(4, Math.min(r.left, innerWidth - popup.offsetWidth - 4)) + 'px';
  // Below the button, or above it when that's where the room is (the status bar's): pinned by its bottom edge then,
  // so it grows upward as the branches arrive.
  if (innerHeight - r.bottom >= 360 || r.top < innerHeight - r.bottom) popup.style.top = r.bottom + 2 + 'px';
  else { popup.style.bottom = innerHeight - r.top + 2 + 'px'; popup.style.maxHeight = r.top - 8 + 'px'; }
  openMenuEl = popup;
  anchor.classList.add('open');
  // closeMenu just removes the element: take the anchor's open look off once it's gone.
  new MutationObserver((_, obs) => { if (!popup.isConnected) { anchor.classList.remove('open'); anchor.dataset.closedAt = Date.now(); obs.disconnect(); } }).observe($('#menuHost'), { childList: true });
  fill();
  filter.focus();
  try {
    branches = await request('git.branches', { id: s.id });
  } catch (e) {
    list.replaceChildren(h('div', { class: 'git-message' }, `Couldn't list the branches: ${e.message}`));
    return;
  }
  if (popup.isConnected) fill();
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
    gitStatusBar(s),
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

/**
 * The bar along the bottom of Git Changes, after Visual Studio's status bar: outgoing/incoming commits (opening Git
 * Repository), how many files are changed, and the branch, which opens the branch picker.
 */
function gitStatusBar(s) {
  const g = s.git;
  const files = new Set([...g.staged, ...g.unstaged].map((f) => f.path)).size;
  const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
  const sync = g.upstream
    ? h(gitTabOn('repo') ? 'button' : 'span', {
      class: 'sb-item',
      title: `${plural(g.ahead, 'outgoing commit')} / ${plural(g.behind, 'incoming commit')} (${g.upstream})${gitTabOn('repo') ? '\nShow in Git Repository' : ''}`,
      onclick: gitTabOn('repo') && (() => openDockTab('repo')),
    }, iconEl('up'), String(g.ahead), iconEl('down'), String(g.behind))
    : h('span', { class: 'sb-item dim', title: 'This branch isn\'t on the remote yet: Push publishes it' }, iconEl('up'), 'Not published');
  return h('div', { class: 'git-statusbar' },
    sync,
    h('span', { class: 'sb-item', title: `${plural(files, 'changed file')}` }, iconEl('edit'), String(files)),
    h('button', {
      class: 'sb-item sb-branch', title: 'Switch branches or create a new one',
      onclick: (e) => branchPicker(s, e.currentTarget),
    }, iconEl('branch'), h('span', { class: 'name' }, g.branch || '(unknown)'), iconEl('chevU')));
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
        '-',
        { label: machine || s.server ? 'Browse the folder' : `Open in ${FILE_MANAGER}`, action: () => request('git.openFolder', { id: s.id, path: folder }) },
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
    oncontextmenu: (e) => {
      e.preventDefault();
      showMenu(e.clientX, e.clientY, [
        { label: 'New Branch…', icon: 'newBranch', action: () => showNewBranchDialog(s, { commit: { sha: c.sha, subject: c.subject } }) },
        { label: 'Copy Commit ID', action: () => navigator.clipboard.writeText(c.sha) },
      ]);
    },
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
  typeHere(b.prompt, p);
}

/**
 * Text to type into a terminal with no keys of its own: control characters (Esc and the sequences it starts, Enter,
 * Ctrl+C, C1 codes) dropped, line endings made \n, and only the characters in keep (such as '\n') left in.
 */
function typeable(text, keep = '') {
  return [...(text ?? '').replace(/\r\n?/g, '\n')].filter((c) => !/[\u0000-\u001f\u007f-\u009f]/.test(c) || keep.includes(c)).join('');
}

/**
 * Types text into the session you're looking at, for you to finish or send (never sent from here). From a panel's
 * button (panel given), you're asked first when it would go to a session other than the panel's own, or is more than
 * one line (in a plain shell, each line could run as it's typed).
 */
function typeHere(text, panel) {
  const s = currentSession();
  if (!s) return;
  if (s.startsOnOpen) { expand(s.id); toast(`Starting "${s.name}". Try again once Claude is up.`); return; }
  if (!s.running) { toast(`"${s.name}" isn't running. Start it, then try again.`, true); return; }
  text = typeable(text, '\n');
  if (!text) return;
  const type = () => {
    if (state.byId.get(s.id) !== s || !s.running) { toast(`"${s.name}" isn't running any more.`, true); return; }
    if (state.expandedId !== s.id) expand(s.id);
    s.term.paste(text);   // bracketed when the program asked for it, and never followed by Enter
    setTimeout(() => s.term.focus(), 40);
  };
  const other = panel && panel.owner !== s.id;
  const lines = text.split('\n').length;
  if (!panel || (!other && lines === 1)) { type(); return; }
  const owner = state.byId.get(panel.owner);
  openModal(`Type into “${s.name}”?`, h('div', {},
    other && h('p', {}, `This button is from the “${panel.title}” panel of ${owner ? `“${owner.name}”` : 'a removed session'}, not of “${s.name}”, which it would type into.`),
    lines > 1 && h('p', {}, `It is ${lines} lines. In a plain shell, each line could run as it is typed.`),
    h('p', { class: 'dim' }, 'LlamaTree doesn’t press Enter: read it, then send it yourself.'),
    h('pre', { class: 'spawn-prompt' }, text)), [
    h('button', { class: 'btn', 'data-autofocus': true, onclick: closeModal }, 'Cancel'),
    h('button', { class: 'btn primary', onclick: () => { closeModal(); type(); } }, `Type into “${s.name}”`),
  ]);
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
description: Show my open issues in a LlamaTree panel. Use when I ask for my tasks, or to refresh the My tasks panel.
---

1. Get my open issues with our tracker's MCP tools (change this line to name yours,
   e.g. "the llamatrack my_tasks tool for project ACME").

2. Show them with the llamatree show_panel tool:
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

/**
 * Refresh asks the session that made the panel to rebuild it: its slash command, typed as a paste (never as keys) and
 * sent. The host accepts only a one-line slash command; this checks again, so nothing else is ever sent with Enter.
 */
function refreshPanel(p) {
  const owner = state.byId.get(p.owner);
  if (owner?.startsOnOpen) { startSession(owner); toast(`Starting "${owner.name}". Refresh again once Claude is up.`); return; }
  if (!owner?.running) { toast(`"${owner?.name ?? 'Its session'}" isn't running, so it can't refresh this panel.`, true); return; }
  const command = typeable(p.refresh);
  if (command !== p.refresh || !/^\/[A-Za-z0-9][A-Za-z0-9_:.-]*( .*)?$/.test(command)) { toast(`This panel's refresh isn't a slash command, so it wasn't sent.`, true); return; }
  owner.term.paste(command);
  enterAfterTyping(owner);
  toast(`Asked ${owner.name} to refresh "${p.title}".`);
}

/** A first prompt that couldn't go on Claude's command line: typed once Claude is up, for you to send. */
function typePrompt(id, text) {
  const s = state.byId.get(id);
  if (s?.running && text) s.term.paste(typeable(text, '\n\t'));
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
        fontFamily: '"Cascadia Mono", Consolas, Menlo, "DejaVu Sans Mono", monospace',
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
  if (isPluginTab(t) || isPermsTab(t) || isGalleryTab(t)) return true;   // a plugin, Permissions or Get plugins belongs to no project
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
    else if (isPermsTab(prev)) prev.scrollTop = $('#permsBody').scrollTop;
    else if (isGalleryTab(prev)) prev.scrollTop = $('#galleryBody').scrollTop;
    else if (!isPluginTab(prev) && !isPermsTab(prev) && diffEditor) prev.viewState = diffEditor.saveViewState();
  }
  docs.shown = tab;
  const split = !!tab && tab === sideTab();
  $('#diffDoc').hidden = !tab || isConvTab(tab) || isProjectTab(tab) || isPluginTab(tab) || isPermsTab(tab) || isGalleryTab(tab);
  $('#convDoc').hidden = !isConvTab(tab);
  $('#projectDoc').hidden = !isProjectTab(tab);
  $('#pluginDoc').hidden = !isPluginTab(tab);
  $('#permsDoc').hidden = !isPermsTab(tab);
  $('#galleryDoc').hidden = !isGalleryTab(tab);
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
    else if (isPermsTab(tab)) showPermissions(tab);
    else if (isGalleryTab(tab)) showGallery(tab);
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
  if (isPluginTab(docs.tabs[i])) { closePluginFrame(docs.tabs[i].pluginId); seatPanelPlugins(); }   // one opened wide goes back to the side panel
  if (isGalleryTab(docs.tabs[i]) && themePreview) { themePreview = null; applyTheme(); }   // a theme tried on goes with it
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
  if (!docs.tabs.some((t) => !isConvTab(t) && !isProjectTab(t) && !isPluginTab(t) && !isPermsTab(t) && !isGalleryTab(t))) disposeDiffModels();
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
      }, pluginIcon(t.pluginId), h('span', { class: 'doc-title' }, pluginCaption(t.pluginId)), pluginBadge(t.pluginId, 'badge'),
      h('button', { class: 'icon-btn small', title: 'Close (Ctrl+W)', onclick: () => closeDoc(t.key) }, iconEl('close')));
    }
    if (isPermsTab(t)) {
      return h('div', {
        class: 'doc-tab' + (docs.active === t.key ? ' active' : ''),
        'data-id': t.key,
        title: 'Permissions: what each plugin and session may do',
        onclick: (e) => { if (!e.target.closest('button')) activateDoc(t.key); },
        onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },
        onauxclick: (e) => { if (e.button === 1) closeDoc(t.key); },
      }, iconEl('shield'), h('span', { class: 'doc-title' }, 'Permissions'),
      h('button', { class: 'icon-btn small', title: 'Close (Ctrl+W)', onclick: () => closeDoc(t.key) }, iconEl('close')));
    }
    if (isGalleryTab(t)) {
      return h('div', {
        class: 'doc-tab' + (docs.active === t.key ? ' active' : ''),
        'data-id': t.key,
        title: 'Get plugins and themes: from the LlamaTree-Plugins and LlamaTree-Themes repositories',
        onclick: (e) => { if (!e.target.closest('button')) activateDoc(t.key); },
        onmousedown: (e) => { if (e.button === 1) e.preventDefault(); },
        onauxclick: (e) => { if (e.button === 1) closeDoc(t.key); },
      }, iconEl('bag'), h('span', { class: 'doc-title' }, gallery.by === 'themes' ? 'Get themes' : 'Get plugins'),
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
  // The project label in the session tab row (or its button down the left) looks selected while its Project view is showing.
  for (const label of document.querySelectorAll('.session-group-label, .nav-view')) label.classList.toggle('active', projectViewShowing(label.dataset.project));
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
        try { projectView.repos.set(root, { ...await request('worktree.overview', { id: s.id }), server: s.server ?? null, error: null }); }
        catch (e) { projectView.repos.set(root, { root: s.git.root, server: s.server ?? null, baseBranch: null, worktrees: [], error: e.message }); }
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
  addAndOpen({ directory: row.path, name, command: like ? ownOptionsOnly(like.command) : 'claude', project: tab.projectId === UNKNOWN ? '' : tab.projectId, server: row.repo?.server ?? undefined })
    .catch((e) => toast(e.message, true));
}

/**
 * Removing a worktree: its sessions go first (a process with the folder as its working directory would keep it), then
 * git removes it, never forced, so one with uncommitted changes is refused and left as it is. Git deletes the ignored
 * files in it all the same (a .env, local settings, build output), so those are listed, and Remove waits for a tick
 * saying they may go. The branch stays.
 */
function confirmRemoveWorktree(row) {
  const tab = activeTab();
  const projectId = tab.projectId;
  const repo = row.repo;
  let busy = row.changes > 0;
  let ignored = [];
  const error = h('div', { class: 'form-error', hidden: true });
  const remove = h('button', { class: 'btn danger primary', disabled: true }, 'Remove worktree');
  const agree = h('input', { type: 'checkbox' });
  const ignoredBox = h('div', { hidden: true });
  const busyNote = h('div', { class: 'form-error', hidden: !busy });
  const checking = h('div', { class: 'dim', hidden: row.missing }, 'Checking the folder for ignored files…');
  const ready = () => { remove.disabled = busy || (ignored.length > 0 && !agree.checked); };
  agree.addEventListener('change', ready);
  const showBusy = (n) => {
    busy = n > 0;
    busyNote.hidden = !busy;
    busyNote.textContent = `${n} file${n === 1 ? ' is' : 's are'} changed or new in this worktree and would be lost. Commit or undo ${n === 1 ? 'it' : 'them'} first.`;
  };
  if (busy) showBusy(row.changes);
  if (row.missing) ready();
  else {
    request('worktree.contents', { root: repo.root, path: row.path, ...onServer(repo.server) }).then((c) => {
      showBusy(c.changes);
      ignored = c.ignored ?? [];
      if (ignored.length) {
        const shown = 8;
        ignoredBox.replaceChildren(
          h('div', { class: 'form-error' }, `${ignored.length} ignored file${ignored.length === 1 ? ' or folder' : 's or folders'} in it will be deleted too. Git doesn't keep them, so they can't be got back:`),
          h('ul', { class: 'pv-files' }, ignored.slice(0, shown).map((f) => h('li', {}, f)),
            ignored.length > shown && h('li', { class: 'dim' }, `and ${ignored.length - shown} more`)),
          h('label', { class: 'check-field' }, agree, 'Delete these ignored files with the worktree'));
        ignoredBox.hidden = false;
      }
    }, () => {
      // An older server doesn't answer this; its removal deletes ignored files without asking, as it always did.
      ignoredBox.replaceChildren(h('div', { class: 'dim' }, 'Couldn\'t check the folder for ignored files; any there are deleted with it.'));
      ignoredBox.hidden = false;
    }).finally(() => { checking.hidden = true; ready(); });
  }
  remove.addEventListener('click', async () => {
    remove.disabled = true;
    remove.textContent = 'Removing…';
    try {
      for (const s of row.sessions) await request('removeSession', { id: s.id });
      await request('worktree.remove', { root: repo.root, path: row.path, deleteIgnored: ignored.length > 0 && agree.checked, ...onServer(repo.server) });
      closeModal();
      toast(`Removed the worktree at ${row.path}. The branch ${row.branch ?? ''} stays.`);
    } catch (e) {
      error.hidden = false;
      error.textContent = e.message;
      remove.textContent = 'Remove worktree';
      ready();
    }
    projectView.open.delete(normPath(row.path));
    const t = activeTab();
    if (isProjectTab(t) && t.projectId === projectId) loadProjectView(true);
  });
  openModal('Remove worktree', [
    h('div', {}, `Remove the worktree at ${row.path}?`),
    h('div', { class: 'dim' }, `The branch ${row.branch ?? ''} stays in the repository; only the folder goes.`),
    row.sessions.length > 0 && h('div', {}, `${row.sessions.length === 1 ? 'This session is' : 'These sessions are'} in it and will be removed first: ${row.sessions.map((s) => `"${s.name}"`).join(', ')}.`),
    busyNote,
    checking,
    ignoredBox,
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

const UPDATE_EVERY_MS = 15 * 60 * 1000;
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
      else toast(`LlamaTree ${r.current} is the latest version.`);
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
    install.textContent = u.asksPassword ? 'Installing…' : 'Downloading…';
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
      ? `${u.asksPassword ? 'LlamaTree will install the update (your system asks for an administrator’s password), then close and reopen.' : 'LlamaTree will close, install the update and reopen.'}${running ? ` ${running} running session${running === 1 ? '' : 's'} will restart and carry on in ${running === 1 ? 'its' : 'their'} conversation${running === 1 ? '' : 's'}.` : ''}`
      : 'This copy of LlamaTree can’t install this release itself. Download it from the release page.'),
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
  syncVeilSliders();   // the new theme's amount (its own, or yours for it)
  for (const s of state.sessions) lightCycle(s, look?.cursor === 'light-cycle');   // sets the terminal's colours either way
  if (window.monaco) monaco.editor.setTheme(diffTheme(look));
  applyAccent();
  rainColours();   // the rain takes the accent, which is only now on the page
  for (const f of pluginFrames.values()) if (f.ready) postPluginTheme(f.frame);   // one still loading gets it on its load
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
  themePreview = null;
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

// ---- Counting users -------------------------------------------------------------------------------------------------
// Whether you've said yes to being counted (count.state and count.set in MainForm, TelemetryService behind them). The
// host keeps the answer and makes the hourly request; the page asks the question once, along the top, until it's
// answered either way, and offers the switch in the version menu.

const count = { enabled: undefined };   // undefined: not read yet; null: not asked yet; true or false: your answer
const COUNT_SENT = "Once an hour while it's open, the app makes one request to api.llamatree.dev that says six things: a random id made when you said yes, the app's version, the operating system and its version, and how many projects and sessions are open. Only how many: never their names, their folders or anything in them, and nothing that says who you are. The id stays the same from one request to the next (that's how a copy counts once), so the count shows which hours this copy was open; like any server, ours also sees the IP address each request comes from. We keep the id and those numbers by the hour for 400 days, and not the IP address, the user agent or anything else about the request. Turning it off throws the id away here, so turning it on again starts over with a new one.";
const PRIVACY_URL = 'https://github.com/LlamachantTechnology/LlamaTree/blob/main/docs/privacy.md#counting-users';

async function loadCount() {
  try { count.enabled = (await request('count.state')).enabled; } catch { count.enabled = false; }
  renderNotice();
}

async function setCount(on) {
  try { count.enabled = (await request('count.set', { enabled: on })).enabled; } catch (e) { toast(String(e), true); }
  renderNotice();
}

/** What the count is and what it sends, with the switch. */
function showCountDialog() {
  openModal('Counting users', [
    h('div', {}, "We'd like to know how many people use LlamaTree, on which versions and systems, and how much, and we don't want to know anything else."),
    h('div', {}, COUNT_SENT),
    h('div', {}, h('a', { href: '#', onclick: (e) => { e.preventDefault(); request('openUrl', { uri: PRIVACY_URL }); } }, 'Privacy and network, in full')),
  ], [
    h('label', { class: 'check-field' }, h('input', { type: 'checkbox', checked: count.enabled === true, onchange: (e) => setCount(e.target.checked) }), 'Count me among the users'),
    h('div', { class: 'spacer' }),
    h('button', { class: 'btn primary', onclick: closeModal }, 'Close'),
  ]);
}

// ---- Let my other computers connect --------------------------------------------------------------------------------
// This Windows computer as a server for LlamaTree on your others, through Windows' own OpenSSH Server (RemoteAccess in
// the window, which asks Windows for permission where it needs it). Only in a window of this computer's own sessions.

const canShareThisComputer = () => !machine && !IS_MAC && !IS_LINUX;

async function showRemoteAccessDialog() {
  let st;
  try { st = await request('remoteAccess.state'); } catch (e) { toast(e.message, true); return; }
  let busy = null;   // what's being done, while Windows asks or it runs
  let keysOnly = true, publicNetworks = false;
  const keyBox = h('textarea', { class: 'key-text', rows: 3, spellcheck: 'false', placeholder: 'ssh-ed25519 AAAA… you@laptop' });

  const run = async (what, type, args, after) => {
    busy = what;
    render();
    try {
      const said = await request(type, args);
      if (typeof said === 'string') toast(said);
      after?.();
    } catch (e) {
      toast(e.message, true);
    }
    busy = null;
    try { st = await request('remoteAccess.state'); } catch { /* keep what's shown */ }
    if (!$('#modalHost').hidden && $('#modalHost .remote-access')) render();
  };

  const code = (text) => h('code', { class: 'remote-access-code' }, text);
  const copy = (text) => h('button', { class: 'btn', onclick: () => navigator.clipboard.writeText(text).then(() => toast('Copied.'), (e) => toast(e.message, true)) }, 'Copy');

  function serverPart() {
    if (st.entra) {
      return [h('p', {}, `This PC is signed in with ${st.account}, a Microsoft Entra ID (work or school) account. Windows' SSH server can't sign those in with a key, which is all LlamaTree uses, so your other computers can't connect to this one. A PC signed in with a local or Microsoft account can be one.`)];
    }
    const on = st.installed && st.running;
    const parts = [h('div', { class: 'remote-access-status' }, h('span', { class: 'dot' + (on ? ' on' : '') }), on
      ? `On: OpenSSH Server is running${st.automatic ? ' and starts with Windows' : ' (it won’t start again after a restart)'}.`
      : st.installed ? 'Off: OpenSSH Server is installed, but not running.' : 'Off.')];
    if (on) {
      parts.push(h('p', {}, 'On your other computer, add a session with ', h('b', {}, 'Computer'), ' set to ', code(`${st.login}@${st.computer}`), ' (or this PC’s address in place of its name).'));
      if (st.addresses.length) parts.push(h('ul', { class: 'remote-access-list' }, st.addresses.map((a) =>
        h('li', {}, code(a.address), ` on ${a.network}`, a.tailscale ? ' (Tailscale: reachable from your other networks too)' : ''))));
      if (st.passwordSignIn) parts.push(h('p', { class: 'remote-access-warn' }, 'Password sign-in is on, so anyone who can reach this PC can try passwords. LlamaTree only needs keys. ',
        h('button', { class: 'btn', disabled: !!busy, onclick: () => run('Turning off password sign-in…', 'remoteAccess.enable', { keysOnly: true, publicNetworks: false }) }, 'Turn off password sign-in')));
      if (st.defaultShell) parts.push(h('p', { class: 'remote-access-warn' }, `OpenSSH Server runs commands in ${st.defaultShell} (its DefaultShell setting). LlamaTree needs cmd, its default: remove that setting for LlamaTree to connect.`));
    } else {
      parts.push(h('p', {}, 'Turning it on installs Windows’ own OpenSSH Server (from Windows Update; it can take a few minutes), starts it now and with Windows, and opens it in the firewall. Windows asks for your permission once. Your sessions here keep running in your desktop session, and your other computers show them beside their own.'));
      parts.push(h('label', { class: 'check-field' }, h('input', { type: 'checkbox', checked: keysOnly, onchange: (e) => { keysOnly = e.target.checked; } }), 'Keys only: turn off password sign-in (recommended)'));
      parts.push(h('label', { class: 'check-field' }, h('input', { type: 'checkbox', checked: publicNetworks, onchange: (e) => { publicNetworks = e.target.checked; } }), 'Also on networks Windows calls public (a cloud VM’s, say)'));
    }
    // The keys that can sign in as you.
    parts.push(h('div', { class: 'remote-access-head' }, 'Computers that can connect'));
    parts.push(st.keys.length
      ? h('ul', { class: 'remote-access-list' }, st.keys.map((k) => h('li', {}, k.comment || k.fingerprint, ' ',
        h('button', { class: 'btn', disabled: !!busy, onclick: () => run('Removing the key…', 'remoteAccess.removeKey', { fingerprint: k.fingerprint }) }, 'Remove'))))
      : h('p', {}, st.admin ? 'None added here yet.' : 'None yet.', ' Paste another computer’s public key below (on it: the one line of ~/.ssh/id_ed25519.pub, or Copy in this dialog there).'));
    if (st.admin) parts.push(h('p', { class: 'remote-access-note' }, 'Keys added some other way aren’t listed: Windows keeps an administrator’s where only administrators can read them.'));
    parts.push(keyBox);
    parts.push(h('div', {}, h('button', { class: 'btn', disabled: !!busy, onclick: () => {
      if (!keyBox.value.trim()) { toast('Paste the other computer’s public key first.', true); return; }
      run('Adding the key…', 'remoteAccess.addKey', { key: keyBox.value }, () => { keyBox.value = ''; });
    } }, 'Add key'), st.admin ? h('span', { class: 'remote-access-note' }, ' Windows asks for permission: an administrator’s keys are kept where only administrators can change them.') : null));
    return parts;
  }

  function myKeyPart() {
    const parts = [h('div', { class: 'remote-access-head' }, 'This computer’s key'),
      h('p', {}, 'For connecting from this computer to another: add this on that one.')];
    if (st.myKey) {
      parts.push(h('div', { class: 'remote-access-key' }, code(st.myKey.text), copy(st.myKey.text)));
    } else {
      parts.push(h('p', {}, 'This computer has no SSH key yet. ',
        h('button', { class: 'btn', disabled: !!busy, onclick: async () => {
          try { st.myKey = await request('remoteAccess.myKey.make'); render(); } catch (e) { toast(e.message, true); }
        } }, 'Make one')));
    }
    return parts;
  }

  function render() {
    const on = st.installed && st.running;
    openModal('Let my other computers connect', [
      h('div', { class: 'remote-access' }, ...serverPart(), ...myKeyPart()),
    ], [
      busy ? h('span', { class: 'remote-access-note' }, busy) : null,
      h('div', { class: 'spacer' }),
      !st.entra && on ? h('button', { class: 'btn', disabled: !!busy, onclick: () => run('Turning it off…', 'remoteAccess.disable', {}) }, 'Turn off') : null,
      !st.entra && !on ? h('button', { class: 'btn primary', disabled: !!busy, onclick: () => run('Turning it on… (installing OpenSSH Server can take a few minutes)', 'remoteAccess.enable', { keysOnly, publicNetworks }) }, 'Turn on') : null,
      h('button', { class: 'btn' + (on || st.entra ? ' primary' : ''), onclick: closeModal }, 'Close'),
    ]);
  }
  render();
}

/** The one question, along the top where tips go, until it's answered. */
function renderCountAsk(bar) {
  bar.hidden = false;
  bar.dataset.kind = 'count';
  bar.dataset.color = 'accent';
  bar.style.setProperty('--notice', '');
  bar.replaceChildren(
    h('span', { class: 'notice-kind' }, iconEl('info'), 'A question'),
    h('span', { class: 'notice-text' }, "May we count you? Once an hour, one request with a random id that stays the same, the app's version, your operating system and how many projects and sessions are open. Never their names or what's in them. With the IP address the request comes from, that shows when this copy is running (we don't keep the address). ",
      h('a', { class: 'notice-inline-link', href: '#', onclick: (e) => { e.preventDefault(); showCountDialog(); } }, 'What it sends')),
    h('span', { class: 'notice-actions' },
      h('button', { class: 'btn primary', onclick: () => setCount(true) }, 'Yes, count me'),
      h('button', { class: 'btn', onclick: () => setCount(false) }, 'No thanks')),
  );
  bar.oncontextmenu = null;
}
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

/** A notice's text, with each [label](https://…) in it a link that opens in the browser (the host has checked them). */
function noticeText(text) {
  const parts = [];
  let at = 0;
  for (const m of text.matchAll(/\[([^[\]\n]+)\]\(([^()\s]+)\)/g)) {
    parts.push(text.slice(at, m.index));
    const url = m[2];
    parts.push(url.startsWith('https://')
      ? h('a', { class: 'notice-inline-link', href: '#', title: url, onclick: (e) => { e.preventDefault(); request('openUrl', { uri: url }); } }, m[1])
      : m[1]);
    at = m.index + m[0].length;
  }
  parts.push(text.slice(at));
  return parts;
}

// After one's closed, tips and offers wait for the next check (closing one shouldn't bring another); news doesn't.
const noticeAllowed = (n) => !notices.dismissed.has(n.id) && (n.kind === 'news' || (notices.tips && !notices.waiting));

function renderNotice() {
  const bar = $('#noticeBar');
  if (count.enabled === null) { renderCountAsk(bar); return; }   // the question about the count comes before any tip
  // The one showing (or asked for again) stays until it's closed or leaves the feed; otherwise the first allowed.
  const n = notices.list.find((x) => x.id === notices.shown?.id && !notices.dismissed.has(x.id)) ?? notices.list.find(noticeAllowed);
  notices.shown = n ?? null;
  bar.hidden = !n;
  if (!n) { bar.replaceChildren(); return; }
  const kind = NOTICE_KINDS[n.kind] ?? NOTICE_KINDS.tip;
  bar.dataset.kind = n.kind;
  // A named colour has its tone in the stylesheet; a #rrggbb of the notice's own is mixed with the theme there too.
  const own = !!cssColor(n.color);
  bar.dataset.color = own ? 'own' : n.color || 'accent';
  bar.style.setProperty('--notice', own ? n.color : '');
  fill(bar,
    h('span', { class: 'notice-kind' }, iconEl(kind.icon), kind.label),
    h('span', { class: 'notice-text' }, noticeText(n.text)),
    n.linkUrl && h('a', { class: 'notice-link', href: '#', title: n.linkUrl,
      onclick: (e) => { e.preventDefault(); request('openUrl', { uri: n.linkUrl }); } }, n.linkLabel, iconEl('chevR')),
    h('button', { class: 'tb-btn notice-close', title: "Close (this one won't show again)", 'aria-label': 'Close', onclick: () => dismissNotice(n) }, iconEl('close')),
  );
  bar.oncontextmenu = (e) => {
    e.preventDefault();
    showMenu(e.clientX, e.clientY, [
      { icon: 'close', label: "Don't show this again", action: () => dismissNotice(n) },
      { icon: 'gift', label: 'All tips and offers…', action: showNoticesDialog },
      n.kind !== 'news' && { icon: 'bulb', label: 'Show tips and offers', checked: true, action: () => setShowTips(false) },
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
    const own = !!cssColor(n.color);
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
      h('div', { class: 'notice-row-text' }, noticeText(n.text)),
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
// ------------------------------------------------------------------ closing the window

/** What closing the window does with running sessions: 'ask', 'keep' (they run on without it) or 'stop'. */
const closeChoice = () => store.get('closeChoice', 'ask');

let closeAsking = false;

/**
 * The window's close button (HostLink.AskToClose): sessions run in the session host, so closing the window can leave
 * them running. With any running, this asks whether to keep them running or stop them, unless you've chosen once
 * and for all. The answer goes to the host, which closes the window.
 */
async function onCloseRequested() {
  post({ type: 'window.closing' });   // the window waits a moment for this, then closes regardless
  if (closeAsking) return;
  let info;
  try { info = await request('app.closeInfo'); } catch { info = { running: 0 }; }
  const choice = closeChoice();
  if (!info.running || choice !== 'ask') {
    request('app.close', { stop: !!info.running && choice === 'stop' });
    return;
  }
  closeAsking = true;
  const remember = h('input', { type: 'checkbox' });
  const answer = (stop) => {
    if (remember.checked) store.set('closeChoice', stop ? 'stop' : 'keep');
    closeModal();
    request('app.close', { stop });
  };
  openModal('Close LlamaTree', [
    ...info.message.split('\n\n').map((p) => h('p', {}, p)),
    h('label', { class: 'check-field' }, remember, 'Don’t ask again (change it in the version menu, top right)'),
  ], [
    h('button', { class: 'btn', onclick: closeModal }, 'Cancel'),
    h('div', { class: 'spacer' }),
    h('button', { class: 'btn danger', onclick: () => answer(true) }, 'Stop sessions'),
    h('button', { class: 'btn primary', 'data-autofocus': '', onclick: () => answer(false) }, 'Keep running'),
  ]);
  onModalClose = () => { closeAsking = false; };
}

/** The window couldn't reach a session host, nor start one. */
// ------------------------------------------------------------------ servers

/**
 * Whose sessions this window shows: null for this computer's; for a server's (a window opened with Connect to a
 * server…), { remote: the SSH target, machine, version: the server's LlamaTree, windowVersion: this one's }.
 */
let machine = null;

/**
 * Servers whose sessions show among this computer's (s.server is how each is reached: user@server, or a Host from the
 * SSH config). This computer's LlamaTree keeps a connection to each (AppHost.Servers): target → { target, state:
 * 'connecting' | 'online' | 'offline' | 'elsewhere', problem, machine, version }.
 */
const servers = new Map();

function setServers(list) {
  if (!list) return;
  const before = JSON.stringify([...servers.values()]);
  servers.clear();
  for (const c of list) servers.set(c.target, c);
  if (JSON.stringify([...servers.values()]) === before || !state.restored) return;
  for (const s of state.sessions) if (s.server) renderTileHeader(s);
  renderSessionTabs();
}

/** The connection to the session's server, or null for a session on this computer. */
const serverOf = (s) => (s?.server ? servers.get(s.server) ?? { target: s.server, state: 'connecting' } : null);

/** Whether the session is on a server that can't be reached now. */
const serverDown = (s) => !!s?.server && serverOf(s).state !== 'online';

/** For a request about a folder rather than a session: which server it's on ({} for this computer). */
const onServer = (server) => (server ? { server } : {});

function serverStateText(c) {
  return {
    online: `On ${c.target}`,
    connecting: `Connecting to ${c.target}…`,
    offline: `Can’t reach ${c.target}`,
    elsewhere: `Showing in another window`,
  }[c.state] ?? c.target;
}

function serverTitle(c) {
  if (!c) return '';
  if (c.state === 'online') return `Runs on ${c.machine || c.target}, reached over SSH as ${c.target}. It keeps running there when LlamaTree closes or the connection drops.` +
    (c.outdated ? `\n${serverVersionText(c)} Click to update it.` : '');
  if (c.state === 'connecting') return `Connecting to ${c.target}…`;
  if (c.notInstalled) return `LlamaTree isn’t installed on ${c.target}.\nThe session is there, but nothing runs it until LlamaTree is. Click to install it.`;
  if (c.state === 'elsewhere') return `${c.problem ?? ''}\nClick to show ${c.target}’s sessions here instead.`;
  return `Can’t reach ${c.target}${c.problem ? ':\n' + c.problem : '.'}\nThe session keeps running there; LlamaTree keeps trying. Click to try now.`;
}

/** The server a session runs on, by its tile's name: click it to reconnect when the server can't be reached. */
function serverTag(s) {
  const c = serverOf(s);
  if (!c) return null;
  const down = c.state !== 'online';
  const stale = !down && c.outdated;
  return h('span', {
    class: 'server-tag' + (down ? ' down' : stale ? ' stale' : ''), title: serverTitle(c),
    onclick: c.notInstalled ? ((e) => { e.stopPropagation(); installServer(c.target); })
      : down && c.state !== 'connecting' ? ((e) => { e.stopPropagation(); request('servers.reconnect', { server: c.target }).catch((err) => toast(err.message, true)); })
      : stale && ((e) => { e.stopPropagation(); updateServer(c); }),
  }, iconEl('server'), serverName(c), stale && iconEl('up'));
}

/**
 * Installs LlamaTree on a server that doesn't have it (servers.install), once you've said yes: its install script from
 * the latest release, run over ssh, installs the package with sudo where that needs no password, and otherwise a copy
 * of your own in your home folder there. Then the connection is made again.
 */
function installServer(target) {
  confirmDialog(`Install LlamaTree on ${target}?`, [
    `LlamaTree isn't installed on ${target}, and its sessions need it there.`,
    `${target} downloads LlamaTree's install script from its latest GitHub release and runs it. The script checks what it downloads against the checksums GitHub publishes, installs the package with sudo where sudo needs no password, and otherwise installs a copy just for your account, in ~/.local/share/LlamaTree (it updates itself).`,
  ].join('\n\n'), 'Install', false, async () => {
    toast(`Installing LlamaTree on ${target}…`);
    try {
      toast(await request('servers.install', { server: target }));
    } catch (err) {
      openModal(`LlamaTree wasn't installed on ${target}`, [h('pre', { class: 'host-error' }, err.message)],
        [h('div', { class: 'spacer' }), h('button', { class: 'btn primary', onclick: closeModal }, 'OK')]);
    }
  });
}

/** What a server's version is next to this computer's, when they differ. */
function serverVersionText(c) {
  return `${serverName(c)} runs LlamaTree ${c.version}; this computer runs ${c.current}.`;
}

/**
 * Updates a server's LlamaTree to this computer's version (servers.update), once you've said yes: the server downloads
 * the release's package from GitHub, checks it and installs it with sudo where that needs no password (otherwise you're
 * told what to run there), then LlamaTree restarts there, which stops its sessions.
 */
function updateServer(c) {
  const name = serverName(c);
  const running = state.sessions.filter((s) => s.server === c.target && s.running);
  confirmDialog(`Update LlamaTree on ${name}?`, [
    serverVersionText(c),
    `${name} downloads LlamaTree ${c.current} from GitHub, checks it against the checksum GitHub publishes, and installs it with sudo. That works where sudo needs no password; otherwise you're told what to run there.`,
    running.length
      ? `LlamaTree then restarts on ${name}, which stops ${running.length === 1 ? 'its running session' : `its ${running.length} running sessions`} (${running.map((s) => s.name).join(', ')}). ${running.length === 1 ? 'It picks' : 'Each picks'} up its conversation where it left off when you open it again.`
      : `LlamaTree then restarts on ${name}.`,
  ].join('\n\n'), 'Update', running.length > 0, async () => {
    toast(`Updating LlamaTree on ${name}…`);
    try {
      toast(await request('servers.update', { server: c.target }));
    } catch (err) {
      openModal(`LlamaTree wasn't updated on ${name}`, [h('pre', { class: 'host-error' }, err.message)],
        [h('div', { class: 'spacer' }), h('button', { class: 'btn primary', onclick: closeModal }, 'OK')]);
    }
  });
}

/** A server, briefly: its own name once connected (vm-build), else how it's reached without the user (4.204.25.78). */
function serverName(c) {
  return c.machine || c.target.replace(/^ssh:\/\//, '').replace(/:\d+$/, '').replace(/^.*@/, '');
}

function onMachine(m) {
  if (!m.remote) return;
  machine = m;
  document.body.classList.add('remote');
  $('#remoteBadge')?.remove();
  $('#appVersion').before(h('span', { id: 'remoteBadge', class: 'remote-badge', title: `These sessions run on ${m.machine || m.remote}, reached over SSH as ${m.remote}. They keep running there when this window closes or the connection drops.` },
    iconEl('terminal'), m.remote));
  if (m.version && m.windowVersion && m.version !== m.windowVersion)
    toast(`${m.remote} has LlamaTree ${m.version}, and this computer ${m.windowVersion}. If something doesn’t work, update the older one.`, true);
}

/** The connection to the server dropped: its sessions run on there, and the window is trying to reach them again. */
function onReconnecting(m) {
  openModal(`Reconnecting to ${m.remote}…`, [
    h('p', {}, 'The connection to the server dropped. Your sessions keep running there, and LlamaTree is trying to reach them again.'),
    h('p', {}, 'They’ll show here as soon as it does.'),
  ], [h('div', { class: 'spacer' }), h('button', { class: 'btn primary', onclick: closeModal }, 'OK')]);
}

/**
 * A server's folders and files, for a window showing a server's sessions (this computer's file manager and folder
 * picker can't reach them). Double-clicking a folder (or Enter) goes into it; a file opens on this computer, from a
 * copy the server sends (fs.open). With onPick it chooses a folder: its button picks the folder selected, else the one
 * shown. `select` names an entry to select once the folder is shown. Returns the element.
 */
function serverBrowser({ path = '', select = null, onPick = null, server = null } = {}) {
  let shown = null;   // the folder's listing (fs.list): { path, parent, home, separator, entries, more }
  let showHidden = store.get('browser.hidden', false);
  const where = h('input', { class: 'fb-path', spellcheck: 'false', title: 'A folder on the server: type a path and press Enter' });
  const list = h('div', { class: 'fb-list', tabindex: '0' });
  const status = h('span', { class: 'fb-status' });
  const pick = onPick && h('button', { class: 'btn primary', onclick: () => onPick(selectedEntry()?.folder ? full(selectedEntry().name) : shown?.path) }, 'Use this folder');
  const full = (name) => shown.path.replace(/[\\/]+$/, '') + shown.separator + name;
  const rows = () => [...list.querySelectorAll('.fb-item')];
  const selectedEntry = () => { const r = list.querySelector('.fb-item.selected'); return r ? r._entry : null; };
  const choose = (row) => {
    for (const r of rows()) r.classList.toggle('selected', r === row);
    row?.scrollIntoView({ block: 'nearest' });
    if (pick) pick.textContent = row?._entry.folder ? `Use ${row._entry.name}` : 'Use this folder';
  };
  const go = async (p, sel = null) => {
    try {
      shown = await request('fs.list', { path: p, ...onServer(server) });
    } catch (err) {
      status.textContent = err.message;
      status.classList.add('form-error');
      if (!shown) where.value = p;
      return;
    }
    status.classList.remove('form-error');
    where.value = shown.path;
    draw(sel);
  };
  const open = (e) => {
    if (e.folder) { go(full(e.name)); return; }
    toast(`Opening ${e.name}…`);
    request('fs.open', { path: full(e.name), ...onServer(server) }).catch((err) => toast(err.message, true));
  };
  const up = () => { if (shown?.parent) go(shown.parent, shown.path.replace(/[\\/]+$/, '').split(/[\\/]/).pop()); };
  const draw = (sel) => {
    const entries = shown.entries.filter((e) => showHidden || !e.hidden);
    list.replaceChildren(...entries.map((e) => {
      const row = h('div', { class: 'fb-item' + (e.hidden ? ' hidden-entry' : ''), title: e.name, onclick: () => choose(row), ondblclick: () => open(e) },
        iconEl(e.folder ? 'folder' : 'file'), h('span', { class: 'name' }, e.name), !e.folder && h('span', { class: 'size' }, sizeText(e.size)));
      row._entry = e;
      return row;
    }));
    if (!entries.length) list.append(h('div', { class: 'fb-empty' }, 'This folder is empty.'));
    const folders = entries.filter((e) => e.folder).length;
    const hiddenCount = shown.entries.length - entries.length;
    const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
    status.textContent = `${plural(folders, 'folder')}, ${plural(entries.length - folders, 'file')}` +
      (hiddenCount ? ` (${hiddenCount} hidden)` : '') + (shown.more ? ', and more not listed' : '');
    choose(rows().find((r) => r._entry.name === sel) ?? null);
    if (!sel) list.scrollTop = 0;
  };
  where.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); go(where.value); } });
  list.addEventListener('keydown', (e) => {
    const all = rows();
    const at = all.findIndex((r) => r.classList.contains('selected'));
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      choose(all[Math.max(0, Math.min(all.length - 1, at + (e.key === 'ArrowDown' ? 1 : -1)))]);
    } else if (e.key === 'Enter' && at >= 0) {
      e.preventDefault(); e.stopPropagation();
      open(all[at]._entry);
    } else if (e.key === 'Backspace') {
      e.preventDefault();
      up();
    }
  });
  const hiddenBox = h('input', { type: 'checkbox', checked: showHidden, onchange: () => { showHidden = hiddenBox.checked; store.set('browser.hidden', showHidden); if (shown) draw(selectedEntry()?.name); } });
  go(path, select);
  return h('div', { class: 'file-browser' },
    h('div', { class: 'fb-bar' },
      h('button', { class: 'icon-btn', title: 'Up a folder (Backspace)', onclick: up }, iconEl('up')),
      h('button', { class: 'icon-btn', title: 'Home folder', onclick: () => go('~') }, iconEl('folder')),
      where),
    list,
    h('div', { class: 'fb-foot' }, status, h('label', { class: 'fb-hidden' }, hiddenBox, 'Show hidden'), pick));
}

/** A size in bytes, as KB, MB or GB. */
function sizeText(n) {
  if (n == null) return '';
  if (n < 1024) return `${n} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let v = n / 1024, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v < 10 ? v.toFixed(1) : Math.round(v)} ${units[i]}`;
}

/** A server's folder, in a dialog of its own: what "open the folder" does for a window showing a server's sessions. */
function browseServerFolder(path, select, server = null) {
  const browser = serverBrowser({ path, select, server });
  const c = server && servers.get(server);
  openModal(`Files on ${c ? c.machine || c.target : server || machine?.machine || machine?.remote || 'the server'}`, [browser], [
    h('button', { class: 'btn', onclick: () => navigator.clipboard.writeText(browser.querySelector('.fb-path').value) }, 'Copy path'),
    h('div', { class: 'spacer' }),
    h('button', { class: 'btn primary', onclick: closeModal }, 'Close'),
  ]);
  $('#modalHost .modal').classList.add('wide');
  setTimeout(() => browser.querySelector('.fb-list').focus(), 30);
}

function onHostFailed(m = {}) {
  if (m.remote) {
    openModal(`Couldn’t reach ${m.remote}`, [
      h('p', {}, 'LlamaTree couldn’t connect to the server’s sessions.'),
      m.message && h('pre', { class: 'host-error' }, m.message),
      h('p', { class: 'help' }, 'It signs in with ssh using your keys or ssh-agent, without asking for a password, and needs LlamaTree installed on the server (the llamatree command). Check that ssh ' + m.remote + ' works from a terminal.'),
    ], [h('div', { class: 'spacer' }), h('button', { class: 'btn', onclick: closeModal }, 'Close'),
      h('button', { class: 'btn primary', onclick: () => { closeModal(); request('app.reconnect').catch(() => {}); } }, 'Try again')]);
    return;
  }
  openModal('LlamaTree couldn’t start', [
    h('p', {}, 'The part of LlamaTree that runs your sessions didn’t start, so no sessions can be shown.'),
    h('p', {}, 'Close LlamaTree and open it again. If it keeps happening, restart your computer.'),
  ], [h('div', { class: 'spacer' }), h('button', { class: 'btn primary', onclick: closeModal }, 'OK')]);
}

function versionMenu(x, y) {
  showMenu(x, y, [
    // A server's LlamaTree is updated on the server, as its other packages are.
    ...(machine ? [] : [
      { icon: 'refresh', label: 'Check for updates', action: () => checkForUpdates(true) },
      { icon: 'down', label: 'Check for updates automatically', checked: updates.auto, action: () => {
        updates.auto = !updates.auto;
        store.set('autoUpdateCheck', updates.auto);
        if (updates.auto) checkForUpdates();
      } },
    ]),
    ...(canShareThisComputer() ? [{ icon: 'server', label: 'Let my other computers connect…', action: showRemoteAccessDialog }, '-'] : []),
    { icon: 'bulb', label: 'Show tips and offers', checked: notices.tips, action: () => setShowTips(!notices.tips) },
    { icon: 'gift', label: 'Tips and offers…', action: showNoticesDialog },
    '-',
    { icon: 'info', label: 'Count me among the users', checked: count.enabled === true, action: () => setCount(count.enabled !== true) },
    { label: [h('span', { class: 'menu-indent' }), 'What the count sends…'], action: showCountDialog },
    '-',
    { header: 'Closing the window with sessions running' },
    ...[['ask', 'Ask each time'], ['keep', 'Keep them running'], ['stop', 'Stop them']].map(([value, label]) =>
      ({ label, checked: closeChoice() === value, action: () => store.set('closeChoice', value) })),
    { label: machine ? `Quit and stop all sessions on ${machine.remote}` : 'Quit and stop all sessions', action: () => request('app.close', { stop: true }) },
    '-',
    // The app's log: what went wrong that nobody was told (see AppLog), for when something seems off.
    { icon: 'folder', label: machine ? `Open the log folder on ${machine.remote}` : 'Open the log folder', action: () => request('logs.openFolder') },
    { icon: 'info', label: `LlamaTree ${updates.current ?? ''}`.trim() + ' · release notes',
      action: () => request('openUrl', { uri: 'https://github.com/LlamachantTechnology/LlamaTree/releases' }) },
  ]);
}

/**
 * A theme's swatch for the menu: its terminal's background with a dot of its accent (or its text colour). System is
 * half Light and half Dark.
 */
function themeSwatch(t) {
  if (t === 'system') return h('span', { class: 'theme-swatch', style: 'background:linear-gradient(135deg,#ffffff 50%,#181818 50%)' });
  const [bg, dot] = t === 'light' ? ['#ffffff', '#1e1e1e'] : t === 'dark' ? ['#181818', '#cccccc']
    : themeSwatchColours(t);
  return h('span', { class: 'theme-swatch', style: `background:${bg}` }, h('i', { style: `background:${dot}` }));
}

/** A theme's background and accent, a monochrome one's as built from your accent colour (see monochromeLook). */
function themeSwatchColours(t) {
  const made = t.monochrome ? monochromeLook(accentColour(), t.base === 'light') : {};
  const terminal = { ...made.terminal, ...t.terminal }, colors = { ...made.colors, ...t.colors };
  const fallback = t.base === 'light' ? ['#ffffff', '#1e1e1e'] : ['#181818', '#cccccc'];
  return [terminal.background ?? colors.chrome ?? fallback[0], (t.accent ?? made.accent)?.line ?? terminal.foreground ?? fallback[1]];
}

/** The theme menu (next to the version number): System, Light, Dark and the themes, and the accent colour. */
function themeMenu(x, y) {
  const c = accent.choice;
  const colour = c === 'windows' ? accent.palette?.[3] ?? '#7160e8' : c === 'purple' ? '#7160e8' : c;
  const own = currentTheme();   // a theme with an accent of its own (not a monochrome one: that's built from yours)
  const suggested = suggestedAccents();
  const named = suggested.find((a) => a.color === String(c).toLowerCase());
  showMenu(x, y, [
    { header: 'Theme' },
    { icon: themeSwatch('system'), label: 'System (follows Windows)', checked: theme.pref === 'system', action: () => setThemePref('system') },
    { icon: themeSwatch('light'), label: 'Light', checked: theme.pref === 'light', action: () => setThemePref('light') },
    { icon: themeSwatch('dark'), label: 'Dark', checked: theme.pref === 'dark', action: () => setThemePref('dark') },
    ...themes.list.map((t) => ({ icon: themeSwatch(t), label: t.name, checked: theme.pref === t.id, kbd: t.builtIn ? '' : 'Yours', action: () => setThemePref(t.id) })),
    themes.problems.length > 0 && { icon: 'warn', label: `${themes.problems.length} theme${themes.problems.length === 1 ? '' : 's'} not loaded`, kbd: 'Why?', action: () => toast(themes.problems.join('\n'), true) },
    '~',
    { icon: 'save', label: 'Save as a new theme…', action: showSaveThemeDialog },
    { icon: 'bag', label: 'Get themes…', action: () => openGallery('themes') },
    { icon: 'folder', label: 'Open the themes folder', action: () => request('themes.openFolder') },
    '-',
    { header: 'Accent colour' },
    own?.accent
      ? { icon: h('span', { class: 'accent-dot', style: `background:${own.accent.line}` }), label: `${own.name}'s own`,
        action: () => toast(`${own.name} has an accent colour of its own. Choose another theme to change it.`) }
      : { icon: h('span', { class: 'accent-dot', style: `background:${colour}` }),   // one the theme suggests goes by its name
        label: named ? named.name : c === 'windows' ? 'Windows accent' : c === 'purple' ? 'Original purple' : 'Custom',
        kbd: 'Change…', action: showAccentDialog },
    '-',
    { header: 'Status dots' },
    { icon: h('span', { class: 'menu-dot', 'data-dots': dotStyle.id }, h('span', { class: 'dot attention' })),   // the style in use, waiting for you
      label: DOT_STYLES.find((d) => d.id === dotStyle.id).name, kbd: 'Change…', action: showDotStyleDialog },
    '-',
    ...navLayoutItems(),
    '-',
    { header: 'Plugins' },
    ...plugins.list.flatMap((p) => [
      { icon: pluginIcon(p.id), label: p.title, kbd: pluginPlacement(p) === 'panel' ? 'Side panel' : '', action: () => openPlugin(p.id) },
      ...p.menu.filter((m) => m.where.includes('palette')).map((m) => ({ label: [h('span', { class: 'menu-indent' }), m.label], action: () => openPlugin(p.id, m.command, { menu: 'palette' }) })),
      p.toolbar && hiddenPluginButtons.has(p.id) && { label: [h('span', { class: 'menu-indent' }), 'Show its title bar button'], action: () => setPluginButton(p.id, true) },
    ]),
    plugins.list.length > 0 && '~',   // the plugins, then what's done about them (as under Theme)
    relayWaiting() > 0
      ? { icon: 'shield', label: 'Permissions', kbd: `${relayWaiting()} waiting for your answer`, action: () => openPermissions('session') }
      : { icon: 'shield', label: 'Permissions', kbd: 'Plugins and sessions', action: () => openPermissions() },
    plugins.problems.length > 0 && { icon: 'warn', label: `${plugins.problems.length} couldn't be loaded`, kbd: 'Why?', action: () => toast(plugins.problems.join('\n'), true) },
    { icon: 'bag', label: 'Get plugins…', action: () => openGallery('plugins') },
    { icon: 'folder', label: 'Open the plugins folder', action: () => request('plugins.openFolder') },
  ]);
}

/** The accent colours the theme on suggests ({ name, color }), offered first in Accent colour, unless it has an accent of its own. */
const suggestedAccents = () => { const t = currentTheme(); return t?.accent ? [] : t?.accents ?? []; };

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
    ...(look?.grid ? { grid: true } : {}),
    ...(look?.rain ? { rain: true } : {}),
    ...(look?.cursor ? { cursor: look.cursor } : {}),
    ...(veilOwn(look) !== null ? { terminalBackground: Math.round(veilAmount()) } : {}),   // as you have it now
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
  // The theme's own suggestions, by name, first.
  const theirs = suggestedAccents().map((a) => h('button', { class: 'swatch-auto', title: a.color, 'data-colour': a.color, onclick: () => pick(a.color) },
    h('span', { class: 'accent-dot', style: cssColor(a.color) && `background:${a.color}` }), a.name));
  const note = h('div', { class: 'field help' });
  custom.addEventListener('input', () => pick(custom.value));

  function pick(c) {
    accent.choice = c;
    accentChanged();   // preview on the whole app
    windowsBtn.classList.toggle('selected', c === 'windows');
    purpleBtn.classList.toggle('selected', c === 'purple');
    for (const sw of swatches) sw.classList.toggle('selected', sw.title === c);
    for (const b of theirs) b.classList.toggle('selected', b.dataset.colour === String(c).toLowerCase());
    const isCustom = /^#/.test(c) && !PROJECT_COLORS.includes(c) && !theirs.some((b) => b.dataset.colour === c.toLowerCase());
    customBtn.classList.toggle('selected', isCustom);
    customBtn.style.setProperty('--swatch', isCustom ? c : 'transparent');
    if (/^#/.test(c)) custom.value = c;
    note.textContent = c === 'windows'
      ? 'Changes when you change the accent colour in Windows Settings.'
      : 'Very light or dark colours are shaded where needed, so text on them stays readable in both themes.';
  }
  let saved = false;
  openModal('Accent colour', [
    ...(theirs.length ? [h('div', { class: 'field help' }, `Suggested by ${currentTheme().name}`), h('div', { class: 'swatch-row' }, theirs)] : []),
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
// Soon after start (not during it), then every 15 minutes; never when turned off.
setTimeout(() => { if (updates.auto) checkForUpdates(); }, 15_000);
setInterval(() => { if (updates.auto) checkForUpdates(); }, UPDATE_EVERY_MS);
setTimeout(checkNotices, 5_000);
setInterval(checkNotices, NOTICES_EVERY_MS);
setTimeout(loadCount, 3_000);   // the question about the count, if it hasn't been answered, before the first tip

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
 * puts it there. `fixed(id)` items stay where they are: only dragged out, and never a place to drop beside. `axis: 'y'`
 * is for a list down the page (the bar down the left) rather than a row across it.
 *
 * Built on pointer events rather than HTML5 drag and drop: in WebView2 an HTML5 drag runs through the operating
 * system's drag loop, which AllowExternalDrop = false (set so dropped files can't navigate the page) also blocks.
 */
function enableTabDrag(row, { item, canDrag = () => true, onDrop, outside = null, fixed = () => false, axis = 'x' }) {
  const THRESHOLD = 8;   // pixels the pointer must move before a press becomes a drag (a hurried click can move ~5)
  let press = null;      // { id, el, x, y, pointerId } from pointerdown until pointerup
  let dragging = false;
  let place = null;      // where outside.over says a drop below the row lands
  const down = axis === 'y';
  const along = (e) => (down ? e.clientY : e.clientX);                         // the pointer's place along the row
  const span = (r) => (down ? [r.top, r.bottom] : [r.left, r.right]);         // a box's extent along it

  const clear = () => row.querySelectorAll('.drop-before, .drop-after').forEach((el) => el.classList.remove('drop-before', 'drop-after'));

  /** The item nearest the pointer (other than the dragged one), and which side of it the drop lands on. */
  const targetAt = (at) => {
    if (fixed(press.id)) return null;
    let best = null;
    for (const el of row.querySelectorAll(item)) {
      if (el.dataset.id === press.id || fixed(el.dataset.id)) continue;
      const [lo, hi] = span(el.getBoundingClientRect());
      const d = at < lo ? lo - at : at > hi ? at - hi : 0;
      if (!best || d < best.d) best = { el, before: at < (lo + hi) / 2, d };
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
      const moved = outside ? Math.hypot(e.clientX - press.x, e.clientY - press.y) : Math.abs(along(e) - (down ? press.y : press.x));
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
    const [lo, hi] = span(r);
    const scroll = down ? 'scrollTop' : 'scrollLeft';
    if (along(e) < lo + 30) row[scroll] -= 12;
    else if (along(e) > hi - 30) row[scroll] += 12;
    const t = targetAt(along(e));
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
    const t = targetAt(along(e));
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
 * (https://<id>.plugin.local/), so it can't reach the app, its files or terminals, or another plugin, and sees sessions only if
 * you allowed it (see pluginRequest).
 *
 * Messages, each { type: 'llamatree:<name>', … }. To a plugin: theme { theme: 'light' | 'dark', id, name, colors,
 * font, glow } (the theme file on, if one is, and the page's colours by name so the plugin can match them; also ?theme=
 * and ?look= on its address) whenever it changes, and command { command, context } when one of its menu items (or its title bar
 * button, see renderPluginButtons) is picked, context
 * saying what was clicked (see pluginMenuItems): a name and folder to go on, not a way into the session. From a plugin, about
 * its own tab and nothing else: badge { count } or { dot: true } ({ count: 0 } clears it), title { title } (null puts
 * its name back), and open { placement, state } to be moved to a document tab or the side panel for now (see
 * movePlugin; its page starts again there and gets state { placement, state } back); and request, for what its
 * "access" lets it see (see pluginRequest). Anything else it sends is ignored.
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
// A plugin can ask to be moved to the other placement for a while (an "open wide" button in a side panel plugin, say):
// where each is now when not where its plugin.json says, and the state it sent along to get back once its page is up.
const pluginPlaces = new Map();
const pluginHandover = new Map();
const pluginPlacement = (p) => pluginPlaces.get(p.id) ?? p.placement;

/** Reads the plugins folder again (a plugin added since shows up without restarting). */
async function loadPlugins() {
  try {
    const r = await request('plugins.list');
    Object.assign(plugins, { list: r.plugins, problems: r.problems, folder: r.folder, loaded: true });
    renderPluginButtons();
    seatPanelPlugins();
  }
  catch (e) { console.error('The plugins could not be read', e); }
}

// A side panel plugin takes a tab in the side panel on its own, so it's there without being opened first; one you
// closed stays away until you open it again (kept per person).
const closedPanelPlugins = new Set(store.get('panelPluginsClosed', []));
const rememberClosedPanelPlugins = () => store.set('panelPluginsClosed', [...closedPanelPlugins]);
function seatPanelPlugins() {
  let seated = false;
  for (const p of plugins.list) {
    const t = 'plugin:' + p.id;
    if (pluginPlacement(p) !== 'panel' || closedPanelPlugins.has(p.id) || groupOfTab(t) || !dock.groups.length) continue;
    dock.groups[dock.groups.length - 1].tabs.push(t);
    seated = true;
  }
  if (seated) { saveDock(); renderGit(); }
}

/** Opens a plugin where it shows; a command from one of its menu items reaches it once its page has loaded. */
function openPlugin(id, command = null, context = null) {
  const p = pluginById(id);
  if (!p) return;
  if (command != null) sendToPlugin(p, { type: 'llamatree:command', command, context });
  if (pluginPlacement(p) === 'panel') {
    const t = 'plugin:' + id;
    if (closedPanelPlugins.delete(id)) rememberClosedPanelPlugins();   // opened again: it seats itself from now on
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
 *
 * Only a plugin that may see what was clicked has items there: one that asks for access you haven't refused (see
 * pluginMenuSight). Until you've answered, its items are there but say only which menu they're on, so choosing one can
 * bring up the question without the plugin learning the session first. `scopeKey` is a project's key (see scopeKeyOf).
 */
function pluginMenuItems(where, context, scopeKey = null) {
  const s = context.sessionId ? state.byId.get(context.sessionId) : null;
  const items = plugins.list.flatMap((p) => {
    const sight = pluginMenuSight(p, s, scopeKey);
    if (sight === 'none') return [];
    const sent = sight === 'sees' ? { menu: where, ...context } : { menu: where };
    return p.menu.filter((m) => m.where.includes(where))
      .map((m) => ({ icon: pluginIcon(p.id), label: m.label, kbd: p.title, action: () => openPlugin(p.id, m.command, sent) }));
  });
  return items.length ? ['-', { header: 'Plugins' }, ...items] : [];
}

/**
 * Whether plugin `p` may see session `s` (or the project `scopeKey`), for its menu items and title bar button: 'sees'
 * when you've allowed it access and the session is in its projects and not hidden from it; 'asks' while you haven't
 * answered; 'none' when it asks for no access, you refused all it asks for, or the session or project is kept from it.
 */
function pluginMenuSight(p, s, scopeKey = null) {
  if (!p.access.length || p.access.every((k) => pluginAccessState(p.id, k) === 'deny')) return 'none';
  if (!p.access.some((k) => pluginAccessState(p.id, k) === 'allow')) return 'asks';
  if (s) return pluginSeesSession(p.id, s.id) ? 'sees' : 'none';
  return scopeKey && pluginScope[p.id] && !pluginScope[p.id].projects.includes(scopeKey) ? 'none' : 'sees';
}

/**
 * The plugin's frame, made on first use and kept until its tab closes, so switching tabs doesn't reload it. (Moving it
 * to another place in the page does: dragging its side panel tab to another group starts it again.)
 *
 * Only the plugin's own page speaks for it. The frame can navigate itself (a link followed, say), and the page it lands
 * on would otherwise get the plugin's messages and grants: the app page's frame-src keeps the frame on plugin addresses
 * (see index.html and LocalSite), and this keeps it on the page it was opened at. A page we load into the frame is
 * trusted from its load on (what it sends before then is held until that load, see pluginMessage). A later load in
 * the same window that we didn't start is a page the frame went to by itself: what it sent is dropped, nothing more is
 * sent to it, and the plugin's page is put back, starting again. (A frame put back into the page, moved to another
 * group, gets a new window, which loads the plugin's page afresh.) A plugin is one page: following a link to another
 * of its own pages also brings it back to its page.
 */
function pluginFrame(p) {
  let f = pluginFrames.get(p.id);
  if (f) return f;
  const url = new URL(p.url);
  url.searchParams.set('theme', isLight() ? 'light' : 'dark');
  if (themeLook()?.id) url.searchParams.set('look', themeLook().id);
  url.searchParams.set('placement', pluginPlacement(p));
  // No allow-same-origin: the page gets an opaque origin, so it can't reach this page, and has no storage to share.
  const frame = h('iframe', { class: 'plugin-frame', sandbox: 'allow-scripts allow-forms allow-modals', referrerpolicy: 'no-referrer', title: p.title, src: url.href });
  // window: the frame's window when its page last loaded; opened: a load of its page is on its way (we started it);
  // page: counts the page's loads, so an answer for a page that has gone isn't given to the next; early: what it sent
  // before its load; strikes: times in a row it left its page straight after loading.
  f = { frame, url: url.href, ready: false, queue: [], badge: null, title: null,
    window: null, opened: true, page: 0, early: [], loadedAt: 0, strikes: 0, stopped: false };
  const entry = f;
  frame.addEventListener('load', () => {
    if (entry.stopped) return;
    const w = frame.contentWindow;
    if (!entry.opened && w === entry.window) { pluginLeftPage(p, entry); return; }
    entry.opened = false;
    entry.window = w;
    entry.page++;
    entry.loadedAt = performance.now();
    entry.ready = true;
    postPluginTheme(frame);
    if (pluginHandover.has(p.id)) {   // it was just moved: the state it sent along, back to it
      const state = pluginHandover.get(p.id);
      pluginHandover.delete(p.id);
      postToPlugin(frame, { type: 'llamatree:state', placement: pluginPlacement(p), state });
    }
    for (const m of entry.queue.splice(0)) postToPlugin(frame, m);
    for (const data of entry.early.splice(0)) if (pluginFrames.get(p.id) === entry && entry.ready) pluginMessage(p.id, entry, data);
  });
  pluginFrames.set(p.id, f);
  return f;
}

/** Starts the plugin's page again in its frame (a load we started, so the page is trusted once it has loaded). */
function restartPlugin(f) {
  f.ready = false;
  f.stopped = false;
  f.opened = true;
  f.early.length = 0;
  f.frame.src = f.url;
}

/**
 * The plugin's frame loaded a page we didn't open: it went somewhere by itself. What it sent is dropped and its page
 * put back. One that keeps leaving its page as soon as it's back is stopped until its tab is opened again.
 */
function pluginLeftPage(p, f) {
  f.ready = false;
  f.early.length = 0;
  f.strikes = performance.now() - f.loadedAt < 3000 ? f.strikes + 1 : 1;
  if (f.strikes > 3) {
    f.stopped = true;
    f.frame.src = 'about:blank';
    toast(`${p.title} kept leaving its page, so it has been stopped. Close its tab and open it again to start it again.`, true);
    return;
  }
  restartPlugin(f);
}

function sendToPlugin(p, message) {
  const f = pluginFrame(p);
  if (f.ready) postToPlugin(f.frame, message);
  else f.queue.push(message);
}

// Plugins written before the rename listen for 'claude-terminals:<name>': each message also goes under that
// name, and what they send under it is taken as 'llamatree:<name>'.
const OLD_PLUGIN_PREFIX = 'claude-terminals:';
// To '*': the frame is sandboxed without allow-same-origin, so its page's origin is opaque ("null"), which postMessage
// can't name. What makes it the plugin's page is that it's only sent once that page has loaded (see pluginFrame).
function postToPlugin(frame, message) {
  const w = frame.contentWindow;
  if (!w) return;
  w.postMessage(message, '*');
  if (message.type?.startsWith('llamatree:')) w.postMessage({ ...message, type: OLD_PLUGIN_PREFIX + message.type.slice('llamatree:'.length) }, '*');
}

/** Tells a plugin the theme: light or dark, and which theme file is on (its id and name), if one is, so it can dress to match. */
// The page's colours a plugin is told, by their names in app.css, so it can paint itself to match whatever theme is on.
const PLUGIN_THEME_COLORS = ['chrome', 'chrome-2', 'panel', 'panel-2', 'hover', 'hover-strong', 'border', 'border-soft', 'border-strong',
  'text', 'text-dim', 'text-faint', 'text-strong', 'accent', 'accent-strong', 'focus', 'select', 'terminal-bg', 'code-bg', 'code-fg',
  'ok', 'working', 'attention', 'asking', 'danger', 'link'];
const postPluginTheme = (frame) => {
  const look = themeLook();
  const css = getComputedStyle(document.documentElement);
  const colors = Object.fromEntries(PLUGIN_THEME_COLORS.map((n) => [n, css.getPropertyValue('--' + n).trim()]).filter(([, v]) => v));
  postToPlugin(frame, { type: 'llamatree:theme', theme: isLight() ? 'light' : 'dark', id: look?.id ?? null, name: look?.name ?? null,
    colors, font: look?.font ?? null, glow: !!look?.glow });
};

/** Its page goes when its tab closes; so do the caption and badge it set, which start afresh next time, and it goes back to where its plugin.json puts it. */
function closePluginFrame(id) {
  pluginFrames.get(id)?.frame.remove();
  pluginFrames.delete(id);
  pluginPlaces.delete(id);
}

/**
 * Moves a plugin's page to the other placement, the document area ('tab') or the side panel ('panel'), at its own
 * request: its page starts again there and gets back the state it sent along (the state message), and it returns to
 * its plugin.json placement when that tab closes.
 */
function movePlugin(id, placement, state) {
  const p = pluginById(id);
  if (!p) return;
  if ((placement !== 'tab' && placement !== 'panel') || placement === pluginPlacement(p)) { openPlugin(id); return; }
  let handover = null;
  try { const s = JSON.stringify(state ?? null); if (s.length <= 1_000_000) handover = JSON.parse(s); } catch { handover = null; }
  if (docs.tabs.some((t) => t.key === pluginKey(id))) closeDoc(pluginKey(id));
  if (groupOfTab('plugin:' + id)) closeDockPlugin('plugin:' + id, false);   // moved, not closed: it comes back to the panel when the tab closes
  pluginPlaces.set(id, placement);
  pluginHandover.set(id, handover);
  openPlugin(id);
}

/** The plugin's tab caption: what it set, or its title. */
const pluginCaption = (id) => pluginFrames.get(id)?.title ?? pluginById(id)?.title ?? id;
/** A plugin's icon: the image its plugin.json names (or its title bar button's), else the app's plugin icon. */
const pluginIcon = (id) => { const src = pluginById(id)?.icon; return src ? h('img', { class: 'ic plugin-ic', src, alt: '', draggable: 'false' }) : iconEl('plug'); };

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
  const pf = pluginFrame(p), { frame } = pf;
  if (frame.parentNode !== host) { pf.ready = false; host.append(frame); }   // put in the page, it loads its page afresh
  for (const f of host.children) f.hidden = f !== frame;
  frame.focus();
}

/** A side panel plugin, open in its group. */
function showDockPlugin(g, t) {
  const pf = pluginFrame(pluginById(pluginOfTab(t))), { frame } = pf;
  if (frame.parentNode !== g.frames) { pf.ready = false; g.frames.append(frame); }
  for (const f of g.frames.children) f.hidden = f !== frame;
}

/** Closes a side panel plugin's tab; closed by you (`byYou`), it stays out of the panel until you open it again. */
function closeDockPlugin(t, byYou = true) {
  const g = groupOfTab(t);
  if (!g) return;
  g.tabs.splice(g.tabs.indexOf(t), 1);
  if (!g.tabs.length) removeGroup(g);
  closePluginFrame(pluginOfTab(t));
  if (byYou) { closedPanelPlugins.add(pluginOfTab(t)); rememberClosedPanelPlugins(); }
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
 * or just { menu: 'toolbar' } with none open, or one it may not see: see pluginMenuSight). It shows the plugin's badge
 * too, while the plugin is open.
 */
function renderPluginButtons() {
  fill($('#pluginButtons'), plugins.list.filter((p) => p.toolbar && !hiddenPluginButtons.has(p.id)).map((p) => {
    const tip = `${p.title} (plugin)${p.toolbar.tooltip ? ': ' + p.toolbar.tooltip : ''}`;
    return h('button', {
      class: 'tb-btn plugin-btn', title: tip, 'aria-label': tip,
      onclick: () => {
        const s = state.byId.get(state.expandedId);
        openPlugin(p.id, p.toolbar.command, { menu: 'toolbar', ...(s && pluginMenuSight(p, s) === 'sees' ? pluginSessionContext(s) : {}) });
      },
      oncontextmenu: (e) => { e.preventDefault(); showMenu(e.clientX, e.clientY, [{ header: p.title }, { label: 'Hide this button', action: () => setPluginButton(p.id, false) }]); },
    }, h('img', { src: p.toolbar.icon, alt: '', draggable: 'false' }), pluginBadge(p.id, 'plugin-btn-count'));
  }));
}

// ---- access: what a plugin can see beyond its own page

/**
 * Each kind of access a plugin can ask for (PluginCatalog.AccessKinds): what the dialog says it would let the plugin do,
 * and how the Permissions view names and explains it. A request names the kind it needs in pluginRequest's NEEDS.
 */
const PLUGIN_ACCESS = {
  sessions: {
    verb: 'see your sessions', title: 'See your sessions',
    about: 'Their names, projects and folders, whether each is working or waiting, what Claude did in each, and how many files their folders hold. Read only. Only the projects you choose, and you can hide single sessions from it too.',
    dialog: (p) => [
      h('p', {}, `${p.title} is a plugin, from ${plugins.folder}\\${p.id}. It asks to see:`),
      h('ul', {},
        h('li', {}, 'your sessions: their names, projects and folders, and whether each is working or waiting for you;'),
        h('li', {}, 'what Claude did in each: your prompts, the files it read and edited, the commands it ran, the questions it asked and the tokens each reply used;'),
        h('li', {}, 'how many files each top-level folder of their repositories holds.')),
      h('p', {}, 'It can’t type into a session or change a file. But like any web page it could send what it sees to a website, so allow only a plugin you trust. You can change your answer any time in Permissions, on the palette menu.'),
    ],
  },
  message: {
    verb: 'message your sessions', title: 'Message your sessions',
    about: 'Send messages to the Claude and Codex sessions in the projects you choose, and read their answers' +
      ' (and, if it takes messages, ones they send it first). Its messages reach an agent as from a plugin, not from you.',
    dialog: (p) => [
      h('p', {}, `${p.title} is a plugin, from ${plugins.folder}\\${p.id}. It asks to message your sessions:`),
      h('ul', {},
        h('li', {}, 'it can send a message to the Claude or Codex session in a project you choose here, which that agent reads as a message from a plugin, not as your prompt, and may act on;'),
        h('li', {}, 'it reads the answers they send back' + (p.inbox ? ', and messages they send it first (it takes messages);' : ';')),
        h('li', {}, 'every message is in Permissions › Messages.')),
      h('p', {}, 'A message can ask an agent to do things, so allow only a plugin you trust. You can change your answer any time in Permissions, on the palette menu.'),
    ],
  },
  ask: {
    verb: 'ask Claude about your sessions', title: 'Ask Claude about your sessions',
    about: 'Put questions about the sessions it sees to Claude, run once each time with your own sign-in, over their conversations. Claude runs with no tools, so it can’t read files, run commands or change anything. Without Claude Code installed, Codex answers instead, and Codex can read your files. The plugin sees the answers. Needs “See your sessions” too.',
    dialog: (p) => [
      h('p', {}, `${p.title} is a plugin, from ${plugins.folder}\\${p.id}. It asks to put questions about your sessions to Claude:`),
      h('ul', {},
        h('li', {}, 'each question runs Claude Code once (Codex sessions too), headless, with your own sign-in, the way Suggest a commit message does, and counts against your usage;'),
        h('li', {}, 'the question goes with the sessions’ conversations: your prompts, what the agent answered and the tools it used;'),
        h('li', {}, 'Claude runs with no tools or MCP servers and saves no conversation: it sees only what is sent to it, and can’t read a file, run a command or change anything;'),
        h('li', {}, h('strong', {}, 'without Claude Code installed, Codex answers instead. '),
          'It runs read-only, so it can’t change anything, but it can read any file you can (your SSH keys, say), and a plugin could ask it to and see what it read.')),
      h('p', {}, 'The plugin sees the answers. Allow only a plugin you trust. You can change your answer any time in Permissions, on the palette menu.'),
    ],
  },
};

/**
 * What you decided for each plugin, as the host keeps it (PluginGrants, in plugin-grants.json), by plugin id:
 * - pluginAccess / pluginDenied: the kinds you allowed and refused; a kind in neither is asked about the first time the
 *   plugin needs it, and only kinds its plugin.json's "access" names;
 * - pluginScope: the projects whose sessions it sees ({ projects: [project id, or NO_PROJECT] }); none there means every
 *   project, including ones you add later (what a plugin allowed before projects could be chosen keeps);
 * - pluginHidden: sessions you hid from it one by one, on top of that;
 * - pluginChanged: plugins whose plugin.json changed since you allowed them, so what you allowed is asked again.
 * A session it doesn't see is left out of its list, and asking about one is answered as if there were no such session.
 * Changes here go to the host, which sends back what it kept (plugins.grants); the Permissions view shows all of it.
 */
const NO_PROJECT = 'none';
const pluginAccess = {}, pluginDenied = {}, pluginScope = {}, pluginHidden = {};
const pluginChanged = new Set();
const pluginAsking = new Map();    // 'id|kind' → the answer, while you're being asked

/** The host's answers about plugins, in place of what was here. */
function applyGrants(grants) {
  for (const map of [pluginAccess, pluginDenied, pluginScope, pluginHidden]) for (const k of Object.keys(map)) delete map[k];
  pluginChanged.clear();
  for (const [id, g] of Object.entries(grants ?? {})) {
    if (g.allowed?.length) pluginAccess[id] = g.allowed;
    if (g.denied?.length) pluginDenied[id] = g.denied;
    if (g.projects) pluginScope[id] = { projects: g.projects };
    if (g.hidden?.length) pluginHidden[id] = g.hidden;
    if (g.changed) pluginChanged.add(id);
  }
  checkPluginSight();
  permissionsChanged();
}

/**
 * Read once at start, before any plugin is answered. Answers this page kept itself (before the host kept them) are
 * handed to the host the first time, and then forgotten here.
 */
const grantsReady = (async () => {
  const OLD = ['pluginAccess', 'pluginDenied', 'pluginScope', 'pluginHidden'];
  try {
    const m = await request('plugins.grants');
    const old = OLD.map((k) => store.get(k, null));
    if (old.some(Boolean)) {
      if (!Object.keys(m.grants ?? {}).length) {
        const [allowed, denied, scope, hidden] = old.map((x) => x ?? {});
        const ids = new Set([allowed, denied, scope, hidden].flatMap(Object.keys));
        await request('plugins.grants.import', { grants: Object.fromEntries([...ids].map((id) => [id,
          { allowed: allowed[id] ?? [], denied: denied[id] ?? [], projects: scope[id]?.projects ?? null, hidden: hidden[id] ?? [] }])) });
        Object.assign(m, await request('plugins.grants'));
      }
      for (const k of OLD) { try { localStorage.removeItem('ct.' + k); } catch { /* gone anyway */ } }
    }
    applyGrants(m.grants);
  } catch (e) { console.error('Your answers about plugins could not be read', e); }
})();

/** 'allow', 'deny' or 'ask' (not decided yet). By id, so a plugin no longer installed still has its answers. */
const pluginAccessState = (id, kind) => pluginAccess[id]?.includes(kind) ? 'allow' : pluginDenied[id]?.includes(kind) ? 'deny' : 'ask';
const pluginMayHave = (p, kind) => p.access.includes(kind) && pluginAccessState(p.id, kind) === 'allow';

/** Tells the host; what it kept comes back as plugins.grants. */
const sendGrant = (name, args) => request(name, args).catch((e) => toast(e.message, true));

/** Sets what plugin id may do with kind: 'allow', 'deny' or 'ask' (forget the answer, so it's asked again). */
function setPluginAccess(id, kind, to) {
  const was = pluginAccessState(id, kind);
  const put = (map, on) => {
    const kinds = new Set(map[id] ?? []);
    if (on) kinds.add(kind); else kinds.delete(kind);
    if (kinds.size) map[id] = [...kinds]; else delete map[id];
  };
  put(pluginAccess, to === 'allow');
  put(pluginDenied, to === 'deny');
  if (to === 'allow') pluginChanged.delete(id);
  sendGrant('plugins.grant', { id, kind, state: to });
  // Taken away: the page starts over, so it no longer holds what it was shown.
  const f = pluginFrames.get(id);
  if (was === 'allow' && to !== 'allow' && f) restartPlugin(f);
  checkPluginSight();
  permissionsChanged();
}

/** Forgets every answer about a plugin: one no longer installed, or one you reset (it asks again when it next needs to). */
function forgetPlugin(id) {
  const had = !!pluginAccess[id]?.length;
  for (const map of [pluginAccess, pluginDenied, pluginScope, pluginHidden]) delete map[id];
  pluginChanged.delete(id);
  sendGrant('plugins.forget', { id });
  // Taken away: the page starts over, so it no longer holds what it was shown.
  const f = pluginFrames.get(id);
  if (had && f) restartPlugin(f);
  checkPluginSight();
  permissionsChanged();
}

/** True if you've answered anything about a plugin: what it may do, its projects or the sessions hidden from it. */
const pluginAnswered = (id) => [pluginAccess, pluginDenied, pluginScope, pluginHidden].some((map) => map[id]);

function confirmResetPlugin(p) {
  confirmDialog(`Reset ${p.title}`,
    `Forget everything you answered for ${p.title}: what it may do, its projects, and the sessions hidden from it? It asks you again the next time it needs something.`,
    'Reset', false, () => forgetPlugin(p.id));
}

/** A session's own messaging rules (not All sessions' ones). */
const ownRelayRules = (s) => relay.rules.filter((r) => r.from === s.id);

function confirmResetSession(s) {
  const rules = ownRelayRules(s);
  confirmDialog(`Reset ${s.name}`,
    `Clear ${s.name}’s ${rules.length} messaging rule${rules.length === 1 ? '' : 's'}? It then follows All sessions’ rules, and you’re asked when those don’t say. Plugins it’s hidden from stay as they are.`,
    'Reset', false, () => { for (const r of rules) setRelayRule(r.from, r.to, 'ask'); });
}

/** The project key a session counts under for a plugin's projects: its project's id, or NO_PROJECT. */
const scopeKeyOf = (s) => (groupOf(s) === UNKNOWN ? NO_PROJECT : s.project);
const pluginInScope = (id, s) => !pluginScope[id] || pluginScope[id].projects.includes(scopeKeyOf(s));
const pluginSeesSession = (id, sessionId) => {
  const s = state.byId.get(sessionId);
  return !!s && pluginInScope(id, s) && !pluginHidden[id]?.includes(sessionId);
};

/** The sessions each plugin sees as last checked, to tell when one it saw no longer is (see checkPluginSight). */
const pluginSaw = new Map();

/**
 * After anything that changes which sessions plugins see (an answer, a project chosen, a session hidden or moved to
 * another project): a plugin allowed to see sessions that no longer sees one it did has its page reloaded, so it no
 * longer holds what it was shown.
 */
function checkPluginSight() {
  for (const p of plugins.list) {
    if (!pluginMayHave(p, 'sessions')) { pluginSaw.delete(p.id); continue; }
    const now = new Set(state.sessions.filter((s) => pluginSeesSession(p.id, s.id)).map((s) => s.id));
    const before = pluginSaw.get(p.id);
    pluginSaw.set(p.id, now);
    const f = pluginFrames.get(p.id);
    if (before && f && [...before].some((sid) => !now.has(sid))) restartPlugin(f);
  }
}

/** Hides a session from a plugin, or shows it again. */
function setPluginSeesSession(id, sessionId, sees) {
  const hidden = new Set(pluginHidden[id] ?? []);
  if (sees) hidden.delete(sessionId); else hidden.add(sessionId);
  if (hidden.size) pluginHidden[id] = [...hidden]; else delete pluginHidden[id];
  sendGrant('plugins.hidden', { id, sessionId, hidden: !sees });
  checkPluginSight();
  permissionsChanged();
}

/** Sets the projects a plugin sees: a list of project ids (and NO_PROJECT), or null for every project. */
function setPluginScope(id, projects) {
  const list = projects ? [...new Set(projects)] : null;
  if (list) pluginScope[id] = { projects: list }; else delete pluginScope[id];
  sendGrant('plugins.projects', { id, projects: list });
  checkPluginSight();
  permissionsChanged();
}

/** Sessions or projects came or went (the host drops those gone from its answers): check who no longer sees what. */
function prunePluginHidden() {
  checkPluginSight();
}

/** In words: "Billing, Docs" or "every project". */
function scopeWords(id) {
  const scope = pluginScope[id];
  if (!scope) return 'every project';
  if (!scope.projects.length) return 'no project yet';
  return scope.projects.map((k) => k === NO_PROJECT ? 'sessions with no project' : state.projects.find((pr) => pr.id === k)?.name).filter(Boolean).join(', ');
}

/**
 * The projects picker, for the dialog asking to see your sessions and for changing it later: "Only these projects"
 * (ticked boxes, one per project and one for sessions with no project) or "All sessions". It starts on what the plugin
 * has, or, the first time, on only the open session's project. ok(enabled) is told when there's something to allow.
 * Returns { el, value() → project keys, or null for all }.
 */
function scopePicker(id, ok = () => {}) {
  const had = pluginScope[id];
  const open = state.byId.get(state.expandedId) ?? state.byId.get(state.currentId);
  const ticked = new Set(had ? had.projects : open ? [scopeKeyOf(open)] : []);
  let all = !!(pluginAccess[id]?.length && !had);   // allowed before projects could be chosen: every project
  const keys = [...state.projects.map((pr) => ({ key: pr.id, name: pr.name, color: projectColor(pr) })),
    ...(state.sessions.some((s) => scopeKeyOf(s) === NO_PROJECT) || ticked.has(NO_PROJECT) ? [{ key: NO_PROJECT, name: 'Sessions with no project', color: null }] : [])];
  const boxes = h('div', { class: 'scope-boxes' }, keys.map((k) => h('label', { class: 'perms-radio' },
    h('input', { type: 'checkbox', checked: ticked.has(k.key), onchange: (e) => { if (e.target.checked) ticked.add(k.key); else ticked.delete(k.key); sync(); } }),
    k.color ? h('span', { class: 'proj-tag', style: `--proj:${k.color}` }, k.name) : k.name,
    h('span', { class: 'dim' }, `${state.sessions.filter((s) => scopeKeyOf(s) === k.key).length} sessions`))));
  const radio = (value, label) => h('label', { class: 'perms-radio' },
    h('input', { type: 'radio', name: 'pluginScope-' + id, checked: value === all, onchange: () => { all = value; sync(); } }), label);
  const sync = () => {
    for (const box of boxes.querySelectorAll('input')) box.disabled = all;
    boxes.classList.toggle('off', all);
    ok(all || ticked.size > 0);
  };
  const el = h('div', { class: 'perms-radios' }, h('b', {}, 'Which sessions'),
    radio(false, 'Only sessions in these projects'), boxes,
    radio(true, 'All sessions, including projects you add later'));
  setTimeout(sync, 0);
  return { el, value: () => (all ? null : keys.map((k) => k.key).filter((k) => ticked.has(k))) };
}

/** Changing a plugin's projects from the Permissions view. */
function changePluginScope(p) {
  let okBtn;
  const picker = scopePicker(p.id, (enabled) => { if (okBtn) okBtn.disabled = !enabled; });
  okBtn = h('button', { class: 'btn primary', onclick: () => { setPluginScope(p.id, picker.value()); closeModal(); } }, 'Save');
  openModal(`Sessions ${p.title} may see`, h('div', {},
    h('p', {}, `${p.title} sees the sessions in the projects you tick, as they come and go, apart from any you hide from it one by one.`),
    picker.el), [
    h('button', { class: 'btn', 'data-autofocus': true, onclick: closeModal }, 'Cancel'), okBtn,
  ]);
}

/** Whether the plugin may use kind: allowed before, or you allow it now (asked once at a time, and not again once refused). */
async function pluginAllowed(p, kind) {
  if (!p.access.includes(kind)) return false;
  await grantsReady;   // the host's answers, not an empty start
  const st = pluginAccessState(p.id, kind);
  if (st !== 'ask') return st === 'allow';
  const key = p.id + '|' + kind;
  if (!pluginAsking.has(key)) pluginAsking.set(key, askPluginAccess(p, kind).finally(() => pluginAsking.delete(key)));
  return pluginAsking.get(key);
}

function askPluginAccess(p, kind) {
  return new Promise((resolve) => {
    let answered = false;
    const answer = (yes) => {
      if (answered) return;
      answered = true;
      // Seeing sessions comes with the projects you picked (set before access, so it never sees more for a moment).
      if (yes && picker) setPluginScope(p.id, picker.value());
      setPluginAccess(p.id, kind, yes ? 'allow' : 'deny');
      resolve(yes);
    };
    let allowBtn;
    const picker = kind === 'sessions' || kind === 'message' ? scopePicker(p.id, (enabled) => { if (allowBtn) allowBtn.disabled = !enabled; }) : null;
    allowBtn = h('button', { class: 'btn primary', onclick: () => { answer(true); closeModal(); } }, 'Allow');
    openModal(`Let ${p.title} ${PLUGIN_ACCESS[kind].verb}?`, h('div', {}, ...PLUGIN_ACCESS[kind].dialog(p), picker?.el), [
      h('button', { class: 'btn', 'data-autofocus': true, onclick: () => { answer(false); closeModal(); } }, 'Don’t allow'),
      allowBtn,
    ]);
    onModalClose = () => answer(false);   // closed any other way
  });
}

// ---- the Permissions view: what each plugin may do, by plugin or by session

// ------------------------------------------------------------------ Get plugins and Get themes
// The plugins and themes the LlamaTree-Plugins and LlamaTree-Themes repositories publish, in a tab like Permissions. The
// host reads their catalogs only when this tab asks (see GalleryService), and installing puts a plugin in the plugins
// folder, or a theme in the themes folder, as if you'd put it there yourself: a plugin still asks before it sees anything.

const GALLERY_KEY = 'gallery';
function isGalleryTab(t) { return t?.kind === 'gallery'; }   // a declaration: tab code above may run first
/** by: 'plugins' or 'themes'; each catalog as the host last answered, those being read, why one couldn't be, and the ids being installed or removed. */
const gallery = { by: store.get('galleryBy', 'plugins'), plugins: null, themes: null, loading: new Set(), error: {}, busy: new Set() };

/** Opens Get plugins (by 'plugins') or Get themes ('themes'). */
function openGallery(by = null) {
  if (by) { gallery.by = by; store.set('galleryBy', by); }
  if (!docs.tabs.some((t) => t.key === GALLERY_KEY)) docs.tabs.push({ key: GALLERY_KEY, kind: 'gallery', preview: false, scrollTop: null });
  activateDoc(GALLERY_KEY);
  if (!gallery[gallery.by]) loadGallery(gallery.by);
  else galleryChanged();
}

function showGallery(tab) {
  renderGallery();
  if (tab.scrollTop != null) $('#galleryBody').scrollTop = tab.scrollTop;
}

const galleryChanged = () => { if (isGalleryTab(shownDoc())) renderGallery(); };

function setGalleryBy(by) {
  gallery.by = by;
  store.set('galleryBy', by);
  if (!gallery[by]) loadGallery(by);
  galleryChanged();
  renderDocTabs();   // its tab says which
}

/** Asks the host for a catalog and where each of its entries stands here; `refresh` reads it from GitHub again. */
async function loadGallery(by, refresh = false) {
  if (gallery.loading.has(by)) return;
  gallery.loading.add(by);
  delete gallery.error[by];
  galleryChanged();
  try { gallery[by] = await request(by === 'plugins' ? 'gallery.plugins' : 'gallery.themes', { refresh }); }
  catch (e) { gallery.error[by] = e.message; }
  finally { gallery.loading.delete(by); galleryChanged(); }
}

/** Runs an install or removal for `id`, with its card's buttons off meanwhile, then reads where everything stands again. */
async function galleryDo(by, id, work) {
  if (gallery.busy.has(id)) return;
  gallery.busy.add(id);
  galleryChanged();
  try { await work(); }
  catch (e) { toast(e.message, true); }
  finally {
    gallery.busy.delete(id);
    await loadGallery(by);
  }
}

/** A plugin's page closes before its folder is replaced or removed (its files can't be swapped under it); says whether it had a tab open. */
function closePluginEverywhere(id) {
  const hadTab = docs.tabs.some((t) => t.key === pluginKey(id));
  if (hadTab) closeDoc(pluginKey(id));
  if (groupOfTab('plugin:' + id)) closeDockPlugin('plugin:' + id, false);
  closePluginFrame(id);
  return hadTab;
}

function installPlugin(e) {
  const updating = e.state === 'update';
  galleryDo('plugins', e.id, async () => {
    const reopen = updating && closePluginEverywhere(e.id);
    await request('gallery.installPlugin', { id: e.id });
    closedPanelPlugins.delete(e.id);   // a side panel plugin takes its place there, even if you'd closed an older copy
    rememberClosedPanelPlugins();
    await loadPlugins();
    if (reopen) openPlugin(e.id);
    toast(updating ? `${e.title} is updated to ${e.version}.`
      : `${e.title} is installed: it's under Plugins in the palette menu${e.placement === 'panel' ? ', and in the side panel' : ''}.${e.access.length ? ' It asks you before it sees anything.' : ''}`);
  });
}

function removePlugin(e) {
  confirmDialog(`Remove ${e.title}?`, 'Its folder is deleted from your plugins folder, and what you allowed it is forgotten. You can install it again from here.', 'Remove', true,
    () => galleryDo('plugins', e.id, async () => {
      closePluginEverywhere(e.id);
      await request('gallery.removePlugin', { id: e.id });
      await loadPlugins();
      toast(`${e.title} is removed.`);
    }));
}

/** Tries on a theme from Get themes before it's installed: it's on screen until you install it, stop, pick a theme or close the tab. */
function previewTheme(e) {
  themePreview = { ...e.theme, previewOf: e.id };
  applyTheme();
  galleryChanged();
}

function stopThemePreview() {
  if (!themePreview) return;
  themePreview = null;
  applyTheme();
  galleryChanged();
}

function installTheme(e) {
  const updating = e.state === 'update';
  galleryDo('themes', e.id, async () => {
    await request('gallery.installTheme', { id: e.id });
    themePreview = null;
    await loadThemes();
    if (!updating || theme.pref === e.id) setThemePref(e.id);
    toast(updating ? `${e.theme.name} is updated to ${e.version}.` : `${e.theme.name} is installed and on. Switch back any time from the palette menu.`);
  });
}

function removeTheme(e) {
  confirmDialog(`Remove ${e.theme.name}?`, `${e.id}.json is deleted from your themes folder.${theme.pref === e.id ? ' The app goes back to System.' : ''} You can install it again from here.`, 'Remove', true,
    () => galleryDo('themes', e.id, async () => {
      await request('gallery.removeTheme', { id: e.id });
      await loadThemes();
      toast(`${e.theme.name} is removed.`);
    }));
}

function renderGallery() {
  const by = gallery.by;
  fill($('#galleryBy'), [['plugins', 'Plugins'], ['themes', 'Themes']].map(([b, label]) =>
    h('button', { class: 'seg-btn' + (by === b ? ' active' : ''), role: 'tab', 'aria-selected': String(by === b), onclick: () => setGalleryBy(b) }, label)));
  const previewing = themePreview && gallery.themes?.themes.find((e) => e.id === themePreview.previewOf);
  fill($('#galleryTools'),
    previewing && h('span', { class: 'gallery-previewing' }, iconEl('theme'), `Trying on ${previewing.theme.name}`),
    previewing && h('button', { class: 'tb-btn text', onclick: stopThemePreview }, 'Stop'),
    h('button', { class: 'tb-btn text', title: `Open your ${by} folder`, onclick: () => request(by === 'plugins' ? 'plugins.openFolder' : 'themes.openFolder') }, 'Open the folder'),
    h('button', { class: 'icon-btn', title: 'Read the catalog from GitHub again', disabled: gallery.loading.has(by), onclick: () => loadGallery(by, true) }, iconEl('refresh')));

  const data = gallery[by];
  const repo = data?.repository ?? `https://github.com/LlamachantTechnology/LlamaTree-${by === 'plugins' ? 'Plugins' : 'Themes'}`;
  const intro = h('p', { class: 'gallery-intro dim' },
    by === 'plugins'
      ? 'Plugins add tabs, side panels, menu items and buttons. Each runs sandboxed on an address of its own, and asks you before it sees anything beyond its own page. '
      : 'A theme is colours and effects, only data: it can’t run anything. Try one on before you install it. ',
    h('button', { class: 'link-btn', onclick: () => request('openUrl', { uri: repo }) }, by === 'plugins' ? 'LlamaTree-Plugins' : 'LlamaTree-Themes'),
    ' on GitHub has their source, and how to add your own.');

  let body;
  if (gallery.error[by] && !gallery.loading.has(by)) {
    body = h('div', { class: 'perms-empty' }, h('p', {}, `The catalog couldn't be read: ${gallery.error[by]}`),
      h('button', { class: 'btn', onclick: () => loadGallery(by, true) }, 'Try again'));
  } else if (!data) {
    body = h('p', { class: 'perms-empty' }, 'Reading the catalog…');
  } else {
    const list = by === 'plugins' ? data.plugins : data.themes;
    body = !list.length ? h('p', { class: 'perms-empty' }, 'Nothing has been published yet.')
      : h('div', { class: 'gallery-grid' }, list.map(by === 'plugins' ? galleryPluginCard : galleryThemeCard));
  }
  fill($('#galleryBody'), intro, body,
    data?.problems?.length > 0 && h('p', { class: 'gallery-problems dim' }, `${data.problems.length} left out: ${data.problems.join(' ')}`));
}

/** The words on a card for what can't be done with it, or null when its buttons say it. */
function galleryNote(e, what) {
  if (e.state === 'needs-app') return `Needs LlamaTree ${e.minAppVersion} or later: update LlamaTree first.`;
  if (e.state === 'yours') return `A ${what} of your own is called ${e.id}${what === 'theme' ? '.json' : ''}, so this one can't be installed beside it.`;
  return null;
}

function galleryPluginCard(e) {
  const busy = gallery.busy.has(e.id);
  const have = e.state === 'installed' || e.state === 'update';
  const asks = e.access.map((k) => PLUGIN_ACCESS[k]?.title ?? k);
  const note = galleryNote(e, 'plugin');
  return h('section', { class: 'gallery-card' + (have ? ' have' : '') },
    h('header', {},
      e.icon ? h('img', { class: 'gallery-icon', src: e.icon, alt: '', draggable: 'false' }) : h('span', { class: 'gallery-icon' }, iconEl('plug')),
      h('div', { class: 'gallery-name' }, h('b', {}, e.title),
        h('span', { class: 'dim' }, [e.version, e.author && `by ${e.author}`].filter(Boolean).join(' · '))),
      have && h('span', { class: 'chip gallery-have', title: `Version ${e.installed} is in your plugins folder` }, iconEl('markAllow'), e.state === 'update' ? `${e.installed} installed` : 'Installed')),
    h('p', { class: 'gallery-about' }, e.description || 'No description.'),
    h('ul', { class: 'gallery-facts dim' },
      h('li', {}, e.placement === 'panel' ? 'Shows in the side panel' : 'Shows in a tab'),
      h('li', { title: asks.length ? 'It asks you before it’s given any of these' : '' },
        asks.length ? `Asks to ${asks.map((a) => a.toLowerCase()).join(', ')}` : 'Asks for nothing beyond its own page')),
    note && h('p', { class: 'gallery-note' }, note),
    h('footer', {},
      (e.state === 'install' || e.state === 'update') && h('button', { class: 'btn primary', disabled: busy, onclick: () => installPlugin(e) },
        busy ? 'Installing…' : e.state === 'update' ? `Update to ${e.version}` : 'Install'),
      have && pluginById(e.id) && h('button', { class: 'btn', disabled: busy, onclick: () => openPlugin(e.id) }, 'Open'),
      have && h('button', { class: 'btn', disabled: busy, onclick: () => removePlugin(e) }, 'Remove'),
      h('span', { class: 'spacer' }),
      h('button', { class: 'link-btn', onclick: () => request('openUrl', { uri: `${gallery.plugins.repository}/tree/main/plugins/${e.id}` }) }, 'Source')));
}

/**
 * A small picture of a theme: its tabs, its status dots and a terminal in some of its colours. A monochrome theme is
 * drawn in your accent colour, as it would be.
 */
function themeThumb(t) {
  const made = t.monochrome ? monochromeLook(accentColour(), t.base === 'light') : {};
  const c = { ...made.colors, ...t.colors }, term = { ...made.terminal, ...t.terminal };
  const dark = t.base !== 'light';
  const accentLine = (t.accent ?? made.accent)?.line ?? accentColour();
  const chrome = c.chrome ?? (dark ? '#1f1f1f' : '#f3f3f3');
  const text = c.text ?? (dark ? '#cccccc' : '#1e1e1e');
  const bg = term.background ?? c['terminal-bg'] ?? (dark ? '#181818' : '#ffffff');
  const fg = term.foreground ?? text;
  const DOTS = { ok: '#3fb950', working: '#2f81f7', attention: '#d29922', asking: '#a371f7' };
  const line = (...parts) => h('div', {}, parts.map(([s, color]) => h('span', { style: `color:${color}` }, s)));
  return h('div', { class: 'theme-thumb', style: `background:${chrome};color:${text}` + (t.font ? `;font-family:'${t.font}'` : '') },
    h('div', { class: 'tt-bar' },
      h('span', { class: 'tt-tab', style: `border-color:${accentLine};color:${c['text-strong'] ?? text}` }, 'All sessions'),
      h('span', { class: 'tt-tab', style: `color:${c['text-dim'] ?? text}` }, 'Git Changes'),
      h('span', { class: 'spacer' }),
      Object.keys(DOTS).map((s) => h('i', { class: 'tt-dot', style: `background:${c[s] ?? DOTS[s]}` }))),
    h('div', { class: 'tt-term', style: `background:${bg};color:${fg}` },
      line(['❯ ', term.green ?? DOTS.ok], ['git status', fg]),
      line(['  modified: ', term.yellow ?? DOTS.attention], ['app.js', term.red ?? '#f85149']),
      line(['● ', accentLine], ['Claude ', fg], ['is working…', term.cyan ?? '#39c5cf']),
      line(['▍', term.cursor ?? fg])));
}

function galleryThemeCard(e) {
  const busy = gallery.busy.has(e.id);
  const t = e.theme;
  const have = e.state === 'installed' || e.state === 'update' || e.state === 'built-in';
  const on = theme.pref === e.id && !themePreview;
  const trying = themePreview?.previewOf === e.id;
  const note = galleryNote(e, 'theme');
  const effects = [t.glow && 'a glow', t.grid && 'the Grid', t.rain && 'digital rain', t.cursor === 'light-cycle' && 'a light cycle', t.monochrome && 'your accent colour throughout'].filter(Boolean);
  return h('section', { class: 'gallery-card' + (have ? ' have' : '') + (trying ? ' trying' : '') },
    themeThumb(t),
    h('header', {},
      h('div', { class: 'gallery-name' }, h('b', {}, t.name),
        h('span', { class: 'dim' }, [e.version, t.base === 'light' ? 'light' : 'dark', e.author && `by ${e.author}`].filter(Boolean).join(' · '))),
      on ? h('span', { class: 'chip gallery-have' }, iconEl('markAllow'), 'On')
        : e.state === 'built-in' ? h('span', { class: 'chip' }, 'Comes with LlamaTree')
        : have && h('span', { class: 'chip gallery-have', title: `Version ${e.installed} is in your themes folder` }, iconEl('markAllow'), e.state === 'update' ? `${e.installed} installed` : 'Installed')),
    e.description && h('p', { class: 'gallery-about' }, e.description),
    effects.length > 0 && h('ul', { class: 'gallery-facts dim' }, h('li', {}, 'With ' + effects.join(', '))),
    note && h('p', { class: 'gallery-note' }, note),
    h('footer', {},
      (e.state === 'install' || e.state === 'update') && h('button', { class: 'btn primary', disabled: busy, onclick: () => installTheme(e) },
        busy ? 'Installing…' : e.state === 'update' ? `Update to ${e.version}` : 'Install and use'),
      e.state === 'install' && h('button', { class: 'btn', disabled: busy, onclick: () => trying ? stopThemePreview() : previewTheme(e) }, trying ? 'Stop trying' : 'Try it on'),
      have && !on && h('button', { class: 'btn', disabled: busy, onclick: () => { themePreview = null; setThemePref(e.id); galleryChanged(); } }, 'Use'),
      (e.state === 'installed' || e.state === 'update') && h('button', { class: 'btn', disabled: busy, onclick: () => removeTheme(e) }, 'Remove'),
      h('span', { class: 'spacer' }),
      h('button', { class: 'link-btn', onclick: () => request('openUrl', { uri: `${gallery.themes.repository}/blob/main/themes/${e.id}.json` }) }, 'Source')));
}

const PERMS_KEY = 'permissions';
function isPermsTab(t) { return t?.kind === 'permissions'; }   // a declaration: tab code above may run first
/**
 * by: 'plugin', 'session' or 'messages'; open: sessions whose messaging rows are unfolded; the Messages tab's log as last
 * read (and the relay.logVersion it was read at), the session it's showing (null: all) and the messages opened in it.
 */
const permsView = { by: store.get('permissionsBy', 'plugin'), open: new Set(), log: null, logVersion: -1, logLoading: false, logFor: null, logOpen: new Set() };

/** Opens the Permissions view, a tab like a plugin's, beside whichever session is open. */
async function openPermissions(by = null, sessionId = null) {
  if (by) setPermsBy(by);
  if (sessionId && by === 'session') permsView.open.add(sessionId);   // its messaging rows unfolded
  if (sessionId && by === 'messages') permsView.logFor = sessionId;
  if (!docs.tabs.some((t) => t.key === PERMS_KEY)) docs.tabs.push({ key: PERMS_KEY, kind: 'permissions', preview: false, scrollTop: null });
  activateDoc(PERMS_KEY);
  await loadPlugins();   // a plugin added since shows up
  permissionsChanged();
  if (sessionId && by === 'session') $(`#permsBody [data-session="${sessionId}"]`)?.scrollIntoView({ block: 'start' });
}

/**
 * A session tile's mark: its unread messages (opens them in Permissions › Messages), or a link when it may message a
 * session by a rule of its own (opens its rules). Reads the relay state directly: tiles are drawn before the
 * Permissions code below has run.
 */
function relayTileMark(s) {
  const unread = relay.unread[s.id] ?? 0;
  if (unread) {
    return h('button', { class: 'chip relay-tile', title: `${unread} unread message${unread === 1 ? '' : 's'} from other sessions: show them`, onclick: () => openPermissions('messages', s.id) },
      iconEl('mail'), String(unread));
  }
  const allows = relay.rules.filter((r) => r.from === s.id && r.decision === 'allow').length;
  if (!allows) return null;
  return h('button', { class: 'icon-btn relay-tile', title: 'It may message other sessions by rules of its own: show them', onclick: () => openPermissions('session', s.id) }, iconEl('link'));
}

/** Messages between sessions waiting for your answer (counted on Permissions in the palette menu). */
const relayWaiting = () => relay.waiting.reduce((n, w) => n + w.count, 0);

function setPermsBy(by) {
  permsView.by = by;
  store.set('permissionsBy', by);
  permissionsChanged();
}

/** An answer or the sessions changed: redraw the view if it's showing. */
function permissionsChanged() {
  if (isPermsTab(shownDoc())) renderPermissions();
}

function showPermissions(tab) {
  renderPermissions();
  if (tab.scrollTop != null) $('#permsBody').scrollTop = tab.scrollTop;
}

/** Allowed / Asks first / Not allowed, for one plugin and kind. */
function permsChoice(id, kind, name) {
  const st = pluginAccessState(id, kind);
  const opt = (to, label, tip) => h('button', {
    class: 'seg-btn' + (st === to ? ' active' : ''), role: 'radio', 'aria-checked': String(st === to), title: tip,
    onclick: () => { if (st !== to) setPluginAccess(id, kind, to); },
  }, label);
  return h('div', { class: 'seg perms-seg', role: 'radiogroup', 'aria-label': `${name}: ${PLUGIN_ACCESS[kind]?.title ?? kind}` },
    opt('allow', 'Allowed', 'It may, without asking'),
    opt('ask', 'Ask first', 'You’re asked the first time it needs to'),
    opt('deny', 'Not allowed', 'It’s refused, without asking'));
}

function renderPermissions() {
  fill($('#permsBy'), [['plugin', 'By plugin'], ['session', 'By session'], ['messages', 'Messages'], ['agents', 'Agents']].map(([by, label]) =>
    h('button', { class: 'seg-btn' + (permsView.by === by ? ' active' : ''), role: 'tab', 'aria-selected': String(permsView.by === by), onclick: () => setPermsBy(by) }, label)));
  fill($('#permsBody'), permsView.by === 'session' ? permsBySession() : permsView.by === 'messages' ? permsMessages() : permsView.by === 'agents' ? permsAgents() : permsByPlugin());
}

// ---- the Messages tab: what sessions sent each other, and what became of each message

const LOG_STATUS = {
  waiting: { word: 'Waiting for your answer', mark: 'ask' }, delivered: { word: 'Unread', mark: 'allow' }, read: { word: 'Read', mark: 'allow' },
  refused: { word: 'Not allowed', mark: 'deny' }, dropped: { word: 'Dropped', mark: 'deny' },
};

/** Reads the log again when it has changed since it was last read (relay.logVersion), then redraws. */
async function loadRelayLog() {
  if (permsView.logLoading || permsView.logVersion === relay.logVersion) return;
  permsView.logLoading = true;
  try {
    const version = relay.logVersion;
    permsView.log = await request('relay.log');
    permsView.logVersion = version;
  } catch (e) { toast(e.message, true); }
  finally { permsView.logLoading = false; }
  permissionsChanged();
}

/** The log, newest first, for every session or the one picked; a message opens to its whole text. */
function permsMessages() {
  if (permsView.logVersion !== relay.logVersion) loadRelayLog();
  const name = relayName;
  const pick = permsView.logFor;
  const entries = (permsView.log ?? []).filter((e) => !pick || e.from === pick || e.to === pick);
  const agents = state.sessions.filter((s) => agentOf(s.command));
  return [
    h('div', { class: 'log-bar' },
      h('select', { class: 'log-filter', 'aria-label': 'Show messages of', onchange: (e) => { permsView.logFor = e.target.value || null; permissionsChanged(); } },
        h('option', { value: '' }, 'Every session'),
        agents.map((s) => h('option', { value: s.id, selected: s.id === pick }, s.name))),
      h('span', { class: 'dim' }, `${entries.length} message${entries.length === 1 ? '' : 's'}, the latest 500 are kept`),
      h('span', { class: 'spacer' }),
      h('button', { class: 'btn', title: 'Forget what was read, refused or dropped (unread and waiting messages stay)', onclick: () => request('relay.clearLog').catch((e) => toast(e.message, true)) }, 'Clear')),
    !entries.length ? h('p', { class: 'perms-empty' }, permsView.log ? 'No messages yet.' : 'Loading…')
    : h('section', { class: 'perms-card log-list' }, entries.map((e) => {
      const st = LOG_STATUS[e.status] ?? LOG_STATUS.dropped;
      const first = e.text.split('\n')[0];
      return h('details', { class: 'log-row ' + e.status, open: permsView.logOpen.has(e.id), ontoggle: (ev) => { if (ev.target.open) permsView.logOpen.add(e.id); else permsView.logOpen.delete(e.id); } },
        h('summary', {},
          decisionMark(st.mark, st.word),
          h('span', { class: 'log-time', title: new Date(e.sent).toLocaleString() }, new Date(e.sent).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })),
          h('b', {}, name(e.from)), h('span', { class: 'dim' }, '→'), h('b', {}, name(e.to)),
          h('span', { class: 'log-text' }, first.length > 160 ? first.slice(0, 160) + '…' : first),
          e.turn > 1 && h('span', { class: 'chip', title: `Message ${e.turn} of this exchange (it stops at 12)` }, `reply ${e.turn - 1}`),
          e.status === 'waiting'
            ? h('button', { class: 'chip perms-waiting', title: 'Answer now', onclick: (ev) => { ev.preventDefault(); askRelay(e.from, e.to); } }, st.word)
            : h('span', { class: 'chip' }, st.word)),
        h('pre', { class: 'log-full' }, e.text));
    })),
  ];
}

// ---- the Agents tab: what sessions did to the sessions they started (SessionControl's log)

const CONTROL_ACTION = { start: 'started', prompt: 'sent a prompt to', approve: 'approved a prompt of', decline: 'declined a prompt of', interrupt: 'interrupted', stop: 'stopped' };

async function loadControlLog() {
  if (permsView.ctlLoading || permsView.ctlVersion === control.logVersion) return;
  permsView.ctlLoading = true;
  try {
    const version = control.logVersion;
    permsView.ctlLog = await request('control.log');
    permsView.ctlVersion = version;
  } catch (e) { toast(e.message, true); }
  finally { permsView.ctlLoading = false; }
  permissionsChanged();
}

function permsAgents() {
  if (permsView.ctlVersion !== control.logVersion) loadControlLog();
  const entries = permsView.ctlLog ?? [];
  return [
    h('div', { class: 'log-bar' },
      h('span', { class: 'dim' }, `${entries.length} action${entries.length === 1 ? '' : 's'}, the latest 300 are kept (until LlamaTree closes)`),
      h('span', { class: 'spacer' }),
      h('button', { class: 'btn', onclick: () => request('control.clearLog').catch((e) => toast(e.message, true)) }, 'Clear')),
    !entries.length ? h('p', { class: 'perms-empty' }, permsView.ctlLog ? 'No session has started or driven another yet.' : 'Loading…')
    : h('section', { class: 'perms-card log-list' }, entries.map((e) => h('details', { class: 'log-row' },
      h('summary', {},
        decisionMark(e.ok ? 'allow' : 'deny', e.ok ? 'Done' : 'Refused or failed'),
        h('span', { class: 'log-time', title: new Date(e.at).toLocaleString() }, new Date(e.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })),
        h('b', {}, relayName(e.from)), h('span', { class: 'dim' }, e.ok ? CONTROL_ACTION[e.action] ?? e.action : `tried to ${e.action === 'start' ? 'start a session' : e.action}`),
        e.target && h('b', {}, relayName(e.target)),
        h('span', { class: 'log-text' }, e.detail.split('\n')[0].slice(0, 160))),
      h('pre', { class: 'log-full' }, e.detail)))),
  ];
}

function permsByPlugin() {
  const asking = plugins.list.filter((p) => p.access.length);
  const plain = plugins.list.filter((p) => !p.access.length);
  const gone = [...new Set([...Object.keys(pluginAccess), ...Object.keys(pluginDenied)])].filter((id) => !pluginById(id));
  if (!asking.length && !gone.length) {
    return h('div', { class: 'perms-empty' },
      h('p', {}, plugins.list.length ? 'None of your plugins asks for anything beyond its own page.' : 'You have no plugins.'),
      h('p', { class: 'dim' }, 'A plugin names what it wants in its plugin.json’s "access"; it shows here, and asks you the first time it needs it.'),
      h('button', { class: 'btn primary', onclick: () => openGallery('plugins') }, 'Get plugins…'), ' ',
      h('button', { class: 'btn', onclick: () => request('plugins.openFolder') }, 'Open the plugins folder'));
  }
  return [
    ...asking.map((p) => h('section', { class: 'perms-card' },
      h('header', {}, pluginIcon(p.id), h('b', {}, p.title), h('span', { class: 'dim', title: 'Its folder' }, `${plugins.folder}\\${p.id}`),
        pluginChanged.has(p.id) && h('span', { class: 'chip perms-waiting', title: 'What you allowed is asked again: its plugin.json changed since' }, 'Changed since you allowed it'),
        pluginAnswered(p.id) && h('button', { class: 'link-btn perms-reset', title: 'Forget everything you answered for it, so it asks again', onclick: () => confirmResetPlugin(p) }, 'Reset')),
      p.access.map((kind) => {
        const hidden = kind === 'sessions' ? (pluginHidden[p.id] ?? []).map((id) => state.byId.get(id)).filter(Boolean) : [];
        return h('div', { class: 'perms-row' },
          h('div', { class: 'perms-what' }, h('b', {}, PLUGIN_ACCESS[kind]?.title ?? kind), h('span', { class: 'dim' }, PLUGIN_ACCESS[kind]?.about ?? ''),
            // Which projects: shown once it's allowed (asking picks them; refused, it sees none anyway).
            kind === p.access.find((k) => (k === 'sessions' || k === 'message') && pluginAccessState(p.id, k) === 'allow') && h('span', { class: 'perms-hidden' },
              h('span', { class: 'decision-mark scope', 'aria-hidden': 'true' }, iconEl('folder')),
              pluginScope[p.id] ? `Only ${scopeWords(p.id)}` : 'Every project, including ones you add later',
              h('button', { class: 'link-btn', title: 'Choose the projects whose sessions it sees', onclick: () => changePluginScope(p) }, 'Change projects')),
            hidden.length > 0 && h('span', { class: 'perms-hidden' }, decisionMark('deny', 'Hidden from it'),
              `Except ${hidden.map((s) => s.name).join(', ')}`,
              h('button', { class: 'link-btn', title: 'Let it see every session again', onclick: () => { for (const s of hidden) setPluginSeesSession(p.id, s.id, true); } }, 'Show all'),
              h('button', { class: 'link-btn', title: 'Change which sessions it sees', onclick: () => setPermsBy('session') }, 'By session'))),
          permsChoice(p.id, kind, p.title));
      }))),
    gone.length > 0 && h('section', { class: 'perms-card' },
      h('header', {}, h('b', {}, 'No longer installed')),
      gone.map((id) => h('div', { class: 'perms-row' },
        h('div', { class: 'perms-what' }, h('b', {}, id),
          h('span', { class: 'dim' }, [['allowed', pluginAccess[id]], ['not allowed', pluginDenied[id]]].filter(([, k]) => k?.length)
            .map(([word, k]) => `${word}: ${k.map((x) => PLUGIN_ACCESS[x]?.title.toLowerCase() ?? x).join(', ')}`).join(' · '))),
        h('button', { class: 'btn', title: 'Forget your answers, so it asks again if it comes back', onclick: () => forgetPlugin(id) }, 'Forget')))),
    plain.length > 0 && h('p', { class: 'perms-note dim' }, `${plain.map((p) => p.title).join(', ')} ${plain.length === 1 ? 'asks' : 'ask'} for nothing beyond ${plain.length === 1 ? 'its' : 'their'} own page.`),
  ];
}

/** A row of buttons picking one of options ([value, label, tooltip]); onPick gets the value picked. */
function segChoice(value, options, onPick, label) {
  return h('div', { class: 'seg perms-seg', role: 'radiogroup', 'aria-label': label }, options.map(([v, text, tip]) => h('button', {
    class: 'seg-btn' + (value === v ? ' active' : ''), role: 'radio', 'aria-checked': String(value === v), title: tip,
    onclick: () => { if (value !== v) onPick(v); },
  }, text)));
}

const RELAY_WORDS = { allow: 'allowed', ask: 'asks you first', deny: 'not allowed' };

/** "Default: allowed, as …": what a row set to Default comes to, from the rule that decides it then. */
const relayDefault = (d) => d.label ? `Default follows ${d.label}.` : 'Default asks you first: nothing after this is set.';

const RELAY_CHOICE = { allow: 'Allowed', ask: 'Ask first', deny: 'Not allowed' };

/** What a message from one session to another gets now, at a glance: a tick, a question mark or a no-entry sign, coloured. */
function relayMark(decision, from, to) {
  return decisionMark(decision, !to ? RELAY_CHOICE[decision]
    : { allow: `“${from}” may message “${to}”`, ask: `You’re asked before “${from}” messages “${to}”`, deny: `“${from}” may not message “${to}”` }[decision]);
}

/** A decision as a coloured mark ('allow', 'ask' or 'deny'), saying tip on hover and to a screen reader. */
const decisionMark = (decision, tip) =>
  h('span', { class: 'decision-mark ' + decision, title: tip, role: 'img', 'aria-label': tip }, iconEl({ allow: 'markAllow', ask: 'markAsk', deny: 'markDeny' }[decision]));

/**
 * The plugins that ask to see sessions, as chips saying whether each sees this one: clicking hides the session from it,
 * or shows it again. A plugin not allowed to see any session goes to By plugin instead, where that's changed.
 */
function pluginChips(s) {
  const asking = plugins.list.filter((p) => p.access.includes('sessions'));
  if (!asking.length) return h('span', { class: 'dim' }, 'No plugin asks to see sessions');
  return asking.map((p) => {
    const global = pluginAccessState(p.id, 'sessions');
    // Why it doesn't see this one, if it doesn't: its projects leave this one out, or you hid it one by one.
    const outside = !pluginInScope(p.id, s);
    const hidden = !!pluginHidden[p.id]?.includes(s.id);
    const st = global === 'deny' || outside || hidden ? 'deny' : global;
    const project = scopeKeyOf(s) === NO_PROJECT ? 'sessions with no project' : projectOf(s)?.name;
    const tip = global === 'deny' ? `${p.title} may see no session. Click to change that under By plugin.`
      : outside ? `${p.title} sees only ${scopeWords(p.id)}, not ${project}. Click to change its projects.`
      : hidden ? `Hidden from ${p.title}. Click to let it see “${s.name}” again.`
      : global === 'allow' ? `${p.title} may see “${s.name}”. Click to hide it from ${p.title}.`
      : `${p.title} asks before it sees your sessions; you pick its projects then. Click to hide “${s.name}” from it whatever you pick.`;
    return h('button', {
      class: 'chip plugin-chip ' + st, title: tip, 'aria-pressed': String(st !== 'deny'),
      onclick: () => (global === 'deny' ? setPermsBy('plugin') : outside ? changePluginScope(p) : setPluginSeesSession(p.id, s.id, hidden)),
    }, decisionMark(st, tip), p.title,
    global !== 'deny' && (outside ? h('span', { class: 'dim' }, ' · not in its projects') : hidden ? h('span', { class: 'dim' }, ' · hidden') : null));
  });
}
/** A Default button's label: the word, and under it what it comes to now. */
const defaultLabel = (d) => [h('span', {}, 'Default'), h('span', { class: 'seg-now' }, RELAY_CHOICE[d.decision])];

/** One messaging rule's row: a name, what Default comes to (if it has one), and its choice. */
function relayRuleRow(from, to, title, fallback, label) {
  const d = fallback && relayFallback(fallback);
  return h('div', { class: 'perms-row' },
    h('div', { class: 'perms-what' }, h('b', {}, title), d && h('span', { class: 'dim' }, relayDefault(d))),
    segChoice(relayRule(from, to), [['allow', 'Allowed', 'Messages go through without asking'], fallback ? ['ask', defaultLabel(d), 'As the rules after this one say'] : ['ask', 'Ask first', 'You’re asked the first time'],
      ['deny', 'Not allowed', 'Messages are refused']], (v) => setRelayRule(from, to, v), label));
}

/** The global rules, for every session: they decide whatever a session's own rules leave to Default. */
function permsGlobal() {
  return h('section', { class: 'perms-card' },
    h('header', {}, h('b', {}, 'All sessions'), h('span', { class: 'dim' }, 'For every Claude and Codex session, unless its own rules below say otherwise')),
    // The fallback first, then the narrower rule that can override it, as on each session's card.
    relayRuleRow('*', '*', 'Messaging any other session', null, 'All sessions: messaging any other session'),
    relayRuleRow('*', 'project', 'Messaging a session in the same project', [['*', '*']], 'All sessions: messaging a session in the same project'));
}

/** A session's messaging rows: anyone, its project, then each other agent session, with what each comes to. */
function permsMessaging(s) {
  const from = s.id;
  const others = state.sessions.filter((t) => t.id !== from && agentOf(t.command));
  const project = projectOf(s);
  const rows = [
    // Default for a session's "any" depends on the target's project, so it reads as the global rules for one outside it.
    relayRuleRow(from, '*', 'Any other session', [['*', '*']], `${s.name}: messaging any other session`),
    project && relayRuleRow(from, 'project', `Sessions in ${project.name}`, relayLevels(from, true).slice(1), `${s.name}: messaging sessions in ${project.name}`),
  ];
  for (const t of others) {
    const d = relayDecide(from, t.id);
    const waiting = relay.waiting.find((w) => w.from === from && w.to === t.id);
    // What Default would come to, whether or not it's the choice now (it's shown on the button either way).
    const fallback = relayFallback(relayLevels(from, !!s.project && s.project === t.project));
    const why = d.by === 'pair' ? null : relayDefault(fallback);
    rows.push(h('div', { class: 'perms-row perms-to ' + d.decision },
      h('div', { class: 'perms-what' },
        h('span', { class: 'perms-session' }, relayMark(d.decision, s.name, t.name), h('i', { class: 'dot ' + statusClass(t) }), h('b', {}, t.name), projectTag(t),
          waiting && h('button', { class: 'chip perms-waiting', title: 'Messages waiting for your answer: click to answer', onclick: () => { askRelay(from, t.id); } }, `${waiting.count} waiting`)),
        why && h('span', { class: 'dim' }, why)),
      segChoice(d.by === 'pair' ? d.decision : 'ask', [['allow', 'Allowed', `It may message ${t.name}`], ['ask', defaultLabel(fallback), 'As the rules above say'], ['deny', 'Not allowed', `It may not message ${t.name}`]],
        (v) => setRelayRule(from, t.id, v), `${s.name}: messaging ${t.name}`)));
  }
  if (!others.length) rows.push(h('div', { class: 'perms-row dim' }, 'There’s no other agent session for it to message yet.'));
  const counts = others.reduce((c, t) => (c[relayDecide(from, t.id).decision]++, c), { allow: 0, ask: 0, deny: 0 });
  const summary = others.length
    ? ['allow', 'ask', 'deny'].filter((k) => counts[k]).map((k) => h('span', { class: 'relay-count ' + k }, relayMark(k), `${counts[k]} ${RELAY_CHOICE[k].toLowerCase()}`))
    : h('span', { class: 'dim' }, 'none yet');
  const open = permsView.open.has(from) || relay.waiting.some((w) => w.from === from);
  return h('details', { class: 'perms-sub', open, ontoggle: (e) => { if (e.target.open) permsView.open.add(from); else permsView.open.delete(from); } },
    h('summary', {}, h('b', {}, 'Messages it may send'), summary), rows);
}

/** Each session: which sessions it may message (agents only), and which plugins may see it. */
function permsBySession() {
  const sessions = state.sessions;
  if (!sessions.length) return h('div', { class: 'perms-empty' }, h('p', {}, 'You have no sessions.'));
  return [
    h('p', { class: 'perms-note dim' }, 'Rules go one way: one session may message another without the other being able to answer, except to reply to a message it was sent (for an hour). A message reaches the other agent as a message from a session, not as your prompt. The most specific rule decides; one set to Default leaves it to the next.'),
    permsGlobal(),
    permsControlGlobal(),
    ...sessions.map((s) => {
      const agent = agentOf(s.command);
      const unread = relay.unread[s.id] ?? 0;
      return h('section', { class: 'perms-card', 'data-session': s.id },
        h('header', {}, h('i', { class: 'dot ' + statusClass(s) }), h('b', {}, s.name), projectTag(s), agent && h('span', { class: 'dim' }, agent.name),
          unread > 0 && h('span', { class: 'chip', title: 'Messages it hasn’t read yet' }, `${unread} unread`),
          ownRelayRules(s).length > 0 && h('button', { class: 'link-btn perms-reset', title: 'Clear its own messaging rules, so All sessions’ rules decide', onclick: () => confirmResetSession(s) }, 'Reset')),
        agent ? permsMessaging(s) : h('div', { class: 'perms-row dim' }, 'Not a coding agent, so it can’t send or read messages.'),
        agent && permsControl(s),
        h('div', { class: 'perms-row' }, h('div', { class: 'perms-what' }, h('b', {}, 'Plugins'), h('span', { class: 'dim' }, 'Click one to hide this session from it, or show it again.')),
          h('div', { class: 'perms-who' }, pluginChips(s))));
    }),
  ];
}

// ---- messages between sessions: the rules, your answers, and telling a session it has messages

/** A session's name, or a plugin's ("plugin:<id>") with "(plugin)" after it. */
function relayName(id) {
  if (id?.startsWith('plugin:')) { const pid = id.slice('plugin:'.length); return `${pluginById(pid)?.title ?? pid} (plugin)`; }
  return state.byId.get(id)?.name ?? 'a closed session';
}

/** The decision of the rule from → to ('ask' when there's none). */
const relayRule = (from, to) => relay.rules.find((r) => r.from === from && r.to === to)?.decision ?? 'ask';

/**
 * The rules after a session's rule for one other session, in the order RelayPolicy tries them: its own for its project
 * (when the other session is in it) and for anyone, then the global ones (from '*') the same way. [from, to] each.
 */
const relayLevels = (from, sameProject) =>
  [sameProject && [from, 'project'], [from, '*'], sameProject && ['*', 'project'], ['*', '*']].filter(Boolean);

/** How a rule level reads in "as … says". */
function relayLevelName([from, to]) {
  const scope = to === 'project' ? 'sessions in the same project' : 'any other session';
  return from === '*' ? `All sessions’ “${scope}”` : `its own “${to === 'project' ? `sessions in ${projectOf(state.byId.get(from))?.name ?? 'its project'}` : scope}”`;
}

/** The first of levels with a rule: { decision, label }, or asking with no label when none has one. */
function relayFallback(levels) {
  for (const level of levels) {
    const r = relay.rules.find((x) => x.from === level[0] && x.to === level[1]);
    if (r) return { decision: r.decision, label: relayLevelName(level) };
  }
  return { decision: 'ask', label: null };
}

/** What a message from → to gets, as RelayPolicy decides it: { decision, by: 'pair' or null, label }. */
function relayDecide(from, to) {
  if (relay.paused) return { decision: 'deny', by: null, label: 'Pause agents' };
  const pair = relay.rules.find((r) => r.from === from && r.to === to);
  if (pair) return { decision: pair.decision, by: 'pair', label: null };
  const a = state.byId.get(from), b = state.byId.get(to);
  return { ...relayFallback(relayLevels(from, !!a?.project && a.project === b?.project)), by: null };
}

function setRelayRule(from, to, decision) {
  request('relay.set', { from, to, decision }).catch((e) => toast(e.message, true));
}

function setRelayPaused(paused) {
  request('relay.pause', { paused }).catch((e) => toast(e.message, true));
}

/** The host's relay state changed: redraw, close a question already answered, and tell idle sessions of new messages. */
function relayChanged(st) {
  if (!st) return;
  Object.assign(relay, st);
  if (relay.asking && !relay.waiting.some((w) => w.from === relay.asking.from && w.to === relay.asking.to)) closeModal();
  renderPermsPause();
  for (const s of state.sessions) renderTileHeader(s);   // their marks
  permissionsChanged();
  for (const s of state.sessions) relayNotify(s);
}

function onRelayMessage(m) {
  if (m.to.startsWith('plugin:')) {
    const f = pluginFrames.get(m.to.slice('plugin:'.length));
    const note = { type: 'llamatree:message', unread: m.unread, from: m.from, fromName: relayName(m.from) };
    if (f?.ready) postToPlugin(f.frame, note); else f?.queue.push(note);
    permissionsChanged();
    return;
  }
  relay.unread[m.to] = m.unread;
  (relay.from[m.to] ??= new Set()).add(m.from);
  const s = state.byId.get(m.to);
  if (s) { renderTileHeader(s); relayNotify(s); }
  permissionsChanged();
}

/**
 * Tells a session's agent it has messages, by typing a line into it and sending it, as you would: only while it's idle
 * (not working, not asking you something) and you haven't typed into it in the last few seconds, so it never lands in
 * a prompt you're writing. The messages themselves reach it only through read_messages. Each new message is told once.
 * A session brought back with the app that hasn't been opened yet is started by its message, so another agent's
 * question doesn't wait for you to open it; it's told once its agent is up (its MCP connection, or 20 s on).
 */
function relayNotify(s) {
  const n = relay.unread[s.id] ?? 0;
  if (!n) { delete relay.told[s.id]; relay.from[s.id]?.clear(); return; }
  if (n <= (relay.told[s.id] ?? 0)) return;
  if (s.startsOnOpen && agentOf(s.command) && !serverDown(s)) { s.wokenAt = performance.now(); startSession(s); return; }
  if (!s.running || s.startsOnOpen || !agentOf(s.command) || !['running', 'attention'].includes(statusClass(s))) return;
  if (performance.now() - s.act.lastIn < 5000) return;   // you're typing in it: the next check tries again
  if (s.wokenAt) {
    // Typed while the agent is still starting, the line can stay in its prompt unsent.
    const now = performance.now();
    if (s.readyAt > s.wokenAt ? now - s.readyAt < 2000 : now - s.wokenAt < 20000) return;
    s.wokenAt = null;
  }
  relay.told[s.id] = n;
  const names = [...(relay.from[s.id] ?? [])].map((id) => `"${relayName(id)}"`);
  const text = `[LlamaTree] ${n === 1 ? 'A message has' : `${n} messages have`} come from ${names.length ? names.join(' and ') : 'another session'}. Read ${n === 1 ? 'it' : 'them'} with the read_messages tool, and answer with send_message: the sender doesn't see what you write here.`;
  s.term.paste(text);
  enterAfterTyping(s, () => noticeInPrompt(s));
}

const isCodex = (s) => agentOf(s.command)?.id === 'codex';

/**
 * Presses Enter to send what was just typed into a session's prompt, and again (a few times at most) if the agent
 * missed it, unless you've typed into the session since (then what's in the prompt is yours to send).
 *
 * Claude Code takes a paste in one piece, so Enter can follow at once; it can still miss one while redrawing or
 * starting, which `inPrompt` (text still in its prompt) tells. Codex on Windows gets no bracketed paste: it guesses a
 * paste from keys arriving close together, and an Enter within ~120ms of the last one becomes a newline in its prompt
 * rather than sending it. ConPTY hands a paste over key by key, so Codex gets longer, and Enter again while it hasn't
 * started work: a newline at the end of the prompt changes nothing, and Enter on an empty prompt does nothing.
 */
function enterAfterTyping(s, inPrompt) {
  const typedAt = s.act.lastIn;
  const codex = isCodex(s);
  setTimeout(() => pressEnter(s), codex ? 600 : 150);
  const unsent = codex ? () => !s.asking && s.act.state !== 'working' : inPrompt;
  if (unsent) setTimeout(() => resendEnter(s, typedAt, unsent, 3), 1500);
}

function resendEnter(s, typedAt, unsent, tries) {
  if (!s.running || s.act.lastIn !== typedAt || !unsent()) return;
  pressEnter(s);
  if (tries > 1) setTimeout(() => resendEnter(s, typedAt, unsent, tries - 1), 1500);
}

/** Whether Claude's prompt (the box between the last two rules on the screen) still holds a notice. */
function noticeInPrompt(s) {
  const b = s.term.buffer.active;
  const text = (i) => b.getLine(i)?.translateToString(true).trim() ?? '';
  const rule = (i) => /^─{20,}$/.test(text(i));
  let bottom = b.length - 1;
  while (bottom > 0 && !text(bottom)) bottom--;   // a tall terminal has empty rows below Claude's screen
  const from = Math.max(0, bottom - 40);
  while (bottom >= from && !rule(bottom)) bottom--;
  let top = bottom - 1;
  while (top >= from && !rule(top)) top--;
  if (top < from) return false;
  for (let i = top + 1; i < bottom; i++) if (b.getLine(i)?.translateToString(true).includes('[LlamaTree] ')) return true;
  return false;
}

/** A session asks to message another, with no rule to say: you're asked, one question at a time. */
function askRelay(from, to) {
  if (relay.asking?.from === from && relay.asking?.to === to) return;
  if (!relay.asks.some((a) => a.from === from && a.to === to)) relay.asks.push({ from, to });
  showRelayAsk();
}

function showRelayAsk() {
  if (relay.asking || !$('#modalHost').hidden) return;   // after the dialog that's open (see the check below)
  let next;
  // Only what's still waiting: one answered meanwhile (here or in Permissions) is skipped.
  while ((next = relay.asks.shift()) && !relay.waiting.some((w) => w.from === next.from && w.to === next.to)) { /* answered */ }
  if (!next) return;
  const a = state.byId.get(next.from), b = state.byId.get(next.to);
  if (!a || !b) { showRelayAsk(); return; }
  relay.asking = next;
  const project = a.project && a.project === b.project ? projectOf(a) : null;
  let scope = 'session';
  const choice = (value, label, tip) => h('label', { class: 'perms-radio', title: tip },
    h('input', { type: 'radio', name: 'relayScope', value, checked: value === scope, onchange: () => { scope = value; } }), label);
  const answer = (allow) => {
    request('relay.answer', { from: next.from, to: next.to, allow, scope }).catch((e) => toast(e.message, true));
    closeModal();
  };
  const agentName = (s) => agentOf(s.command)?.name ?? 'the agent';
  openModal(`Let “${a.name}” message “${b.name}”?`, h('div', {},
    h('p', {}, `${agentName(a)} in “${a.name}” wants to send a message to ${agentName(b)} in “${b.name}”. It waits until you answer.`),
    h('p', {}, `The message reaches ${b.name}’s agent as a message from another session, not as your prompt, and it’s told to read it. A message can still ask it to do things, so allow only sessions you’d let work together. ${b.name} can always reply to a message it’s sent.`),
    h('div', { class: 'perms-radios' }, h('b', {}, 'Your answer is for'),
      choice('once', 'This time only', 'What it sent now is delivered or dropped; you’re asked again next time'),
      choice('session', `“${a.name}” messaging “${b.name}”`),
      project && choice('project', `“${a.name}” messaging any session in ${project.name}`),
      choice('*', `“${a.name}” messaging any session`),
      h('b', { class: 'perms-radios-sub' }, 'Or for every session'),
      project && choice('all-project', 'Any session messaging a session in the same project', 'A rule under All sessions in Permissions'),
      choice('all', 'Any session messaging any other session', 'A rule under All sessions in Permissions')),
    h('p', { class: 'dim' }, 'You can change it any time in Permissions, on the palette menu.')), [
    h('button', { class: 'btn', 'data-autofocus': true, onclick: () => answer(false) }, 'Don’t allow'),
    h('button', { class: 'btn primary', onclick: () => answer(true) }, 'Allow'),
  ]);
  // Closed without an answer: it stays waiting (Permissions shows it), and isn't asked again until it messages again.
  onModalClose = () => { relay.asking = null; setTimeout(showRelayAsk, 300); };
}

// A question waits for any dialog that's open, and a session that was busy is told of its messages once it's idle.
setInterval(() => {
  showRelayAsk();
  for (const s of state.sessions) relayNotify(s);
}, 3000);

// ---- sessions starting and driving other sessions (SessionControl on the host)

/** The host's control state changed: redraw, and ask about any request to start a session not asked yet (after a reload, say). */
function controlChanged(st) {
  if (!st) return;
  Object.assign(control, st);
  for (const r of control.pending) askSpawn(r);
  for (const s of state.sessions) renderTileHeader(s);
  permissionsChanged();
}

/** The decision of the control rule for ability and scope ('ask' when there's none). */
const controlRule = (ability, scope) => control.rules.find((r) => r.ability === ability && r.scope === scope)?.decision ?? 'ask';

/** What a session gets for an ability, as ControlPolicy decides it: { decision, by: 'session' | 'project' | 'all' | null }. */
function controlDecide(ability, s) {
  const fallback = ability === 'spawn' ? 'ask' : 'allow';
  if (relay.paused) return { decision: 'deny', by: 'paused' };
  const own = control.rules.find((r) => r.ability === ability && r.scope === s.id);
  if (own) return { decision: own.decision, by: 'session' };
  // A session another started goes by its own rule for starting sessions, so they don't multiply unasked.
  if (ability === 'spawn' && s.startedBy) return { decision: fallback, by: null };
  const project = s.project && control.rules.find((r) => r.ability === ability && r.scope === 'project:' + s.project);
  if (project) return { decision: project.decision, by: 'project' };
  const all = control.rules.find((r) => r.ability === ability && r.scope === '*');
  return all ? { decision: all.decision, by: 'all' } : { decision: fallback, by: null };
}

function setControlRule(ability, scope, decision) {
  request('control.set', { ability, scope, decision }).catch((e) => toast(e.message, true));
}

/** Tells the host each session's state when it changes, for wait_for_session and the rest. */
function syncControlStatus(s) {
  const st = statusClass(s);
  if (control.told[s.id] === st) return;
  control.told[s.id] = st;
  post({ type: 'control.status', id: s.id, status: st });
}
setInterval(() => { for (const s of state.sessions) syncControlStatus(s); }, 1000);

/**
 * Types a prompt a session sent this one (send_prompt) and sends it, as relayNotify does: not while you're typing in
 * it (it waits for a pause of a few seconds, for at most half a minute).
 */
function controlPrompt(id, text, tries = 0) {
  const s = state.byId.get(id);
  if (!s?.running || !text) return;
  if (performance.now() - s.act.lastIn < 5000) {
    // Never over what you're typing: after five minutes of it, the prompt is dropped instead.
    if (tries < 300) setTimeout(() => controlPrompt(id, text, tries + 1), 1000);
    else toast(`A prompt from “${relayName(s.startedBy)}” to “${s.name}” was dropped: you were typing in it.`, true);
    return;
  }
  // The host refuses control characters already; this keeps a paste from ever carrying keys of its own.
  text = typeable(text, '\n\t');
  s.term.paste(text);
  enterAfterTyping(s);
}

/** The tile's mark for a session another started: who, opening the starter. */
function startedByMark(s) {
  if (!s.startedBy) return null;
  const by = state.byId.get(s.startedBy);
  return h('button', {
    class: 'chip started-by', title: by ? `Started by “${by.name}”, whose agent can send it prompts, answer its prompts and stop it: show “${by.name}”` : 'Started by a session since removed',
    onclick: () => by && (state.expandedId ? expand(by.id) : showGit(by.id)),
  }, iconEl('agent'), by ? by.name : 'removed session');
}

/** A session asks to start another, and no rule says: you're asked, one at a time; it waits for your answer. */
function askSpawn(r) {
  if (!r || control.asking?.id === r.id || (control.queue ??= []).some((q) => q.id === r.id)) return;
  control.queue.push(r);
  showSpawnAsk();
}

function spawnSettled(id) {
  control.queue = (control.queue ?? []).filter((q) => q.id !== id);
  if (control.asking?.id === id) { control.asking = null; closeModal(); }
}

function showSpawnAsk() {
  if (control.asking || !$('#modalHost').hidden) return;   // after the dialog that's open (see the interval below)
  const r = control.queue?.shift();
  if (!r) return;
  const a = state.byId.get(r.from);
  if (!a) { showSpawnAsk(); return; }
  control.asking = r;
  const project = projectOf(a);
  let scope = 'once';
  const choice = (value, label, tip) => h('label', { class: 'perms-radio', title: tip },
    h('input', { type: 'radio', name: 'spawnScope', value, checked: value === scope, onchange: () => { scope = value; } }), label);
  let answered = false;
  const answer = (allow) => {
    answered = true;
    request('control.answer', { id: r.id, allow, scope }).catch((e) => toast(e.message, true));
    control.asking = null;
    closeModal();
  };
  const agent = agentOf(a.command)?.name ?? 'The agent';
  openModal(`Let “${a.name}” start a session?`, h('div', {},
    h('p', {}, `${agent} in “${a.name}” wants to start a new session, which it can then send prompts to and stop. It waits for your answer.`),
    controlDecide('answer', a).decision === 'allow' && h('p', { class: 'spawn-warn' },
      `It can also approve the new session’s permission prompts (editing files, running commands) without you. To keep those for yourself, set “Answer its sessions’ permission prompts” to Not allowed in Permissions.`),
    h('div', { class: 'spawn-ask' },
      h('span', { class: 'k' }, 'Name'), h('span', {}, r.name),
      h('span', { class: 'k' }, 'Folder'), h('span', {}, r.directory),
      h('span', { class: 'k' }, 'Runs'), h('code', {}, ownOptionsOnly(a.command))),
    h('div', { class: 'k dim' }, 'Its first prompt'),
    h('pre', { class: 'spawn-prompt' }, r.prompt),
    h('div', { class: 'perms-radios' },
      choice('once', 'Just this once'),
      choice('session', `Whenever “${a.name}” starts a session`, 'A rule on its card in Permissions'),
      project && !a.startedBy && choice('project', `Whenever a session in ${project.name} starts one`, 'A rule for the project in Permissions; sessions started by agents still ask')),
    h('p', { class: 'dim' }, 'Pause agents in Permissions stops every session starting or driving others.')), [
    h('button', { class: 'btn', 'data-autofocus': true, onclick: () => answer(false) }, 'Don’t allow'),
    h('button', { class: 'btn primary', onclick: () => answer(true) }, 'Allow'),
  ]);
  // Closed without an answer: that's a no, this once; the agent is waiting on it.
  onModalClose = () => {
    if (!answered) request('control.answer', { id: r.id, allow: false, scope: 'once' }).catch(() => {});
    control.asking = null;
    setTimeout(showSpawnAsk, 300);
  };
}
setInterval(showSpawnAsk, 1000);

/** Each session card's rows: whether it may start sessions, and answer their permission prompts. */
function permsControl(s) {
  const row = (ability, what, help) => {
    const d = controlDecide(ability, s);
    const own = controlRule(ability, s.id);
    const by = { project: `as ${projectOf(s)?.name ?? 'its project'}’s rule says`, all: 'as All sessions’ rule says', paused: 'agents are paused' }[d.by];
    const fallbackWord = ability === 'spawn' ? 'Ask first' : 'Allowed';
    return h('div', { class: 'perms-row ' + d.decision },
      h('div', { class: 'perms-what' }, h('b', {}, what), h('span', { class: 'dim' }, by ? `${help} Now: ${RELAY_CHOICE[d.decision].toLowerCase()}, ${by}.` : help)),
      segChoice(own, [['allow', 'Allowed', ''], ['ask', own === 'ask' && d.by === null ? fallbackWord : 'Default', 'As the rules above say'], ['deny', 'Not allowed', '']],
        (v) => setControlRule(ability, s.id, v), `${s.name}: ${what.toLowerCase()}`));
  };
  const children = state.sessions.filter((c) => c.startedBy === s.id);
  return [
    row('spawn', 'Start sessions', s.startedBy ? 'It was started by another session, so it goes only by this rule: with none, you’re asked.' : 'With no rule, you’re asked each time.'),
    row('answer', 'Answer its sessions’ permission prompts', 'Approve or decline what the sessions it started ask to do. With no rule, it may.'),
    row('close', 'Close its sessions', 'Remove the sessions it started once their work is done (their conversations are kept, and a worktree made for one stays). With no rule, it may.'),
    children.length > 0 && h('div', { class: 'perms-row dim' }, `It started: ${children.map((c) => c.name).join(', ')}`),
  ];
}

/** All sessions' and each project's rules for starting sessions and answering their prompts. */
function permsControlGlobal() {
  const row = (ability, scope, what, fallback) => h('div', { class: 'perms-row ' + (controlRule(ability, scope) === 'ask' ? fallback : controlRule(ability, scope)) },
    h('div', { class: 'perms-what' }, h('b', {}, what)),
    segChoice(controlRule(ability, scope), [['allow', 'Allowed', ''], ['ask', fallback === 'ask' ? 'Ask first' : 'Default', ''], ['deny', 'Not allowed', '']],
      (v) => setControlRule(ability, scope, v), what));
  return h('section', { class: 'perms-card' },
    h('header', {}, h('b', {}, 'Agents starting sessions'), h('span', { class: 'dim' }, 'An agent can start sessions of its own and drive them. Its own rule (on its card) comes first, then its project’s, then these.')),
    row('spawn', '*', 'All sessions: starting sessions', 'ask'),
    row('answer', '*', 'All sessions: answering their sessions’ permission prompts', 'allow'),
    row('close', '*', 'All sessions: closing the sessions they started', 'allow'),
    ...state.projects.flatMap((p) => [
      row('spawn', 'project:' + p.id, `${p.name}: starting sessions`, controlRule('spawn', '*') === 'ask' ? 'ask' : controlRule('spawn', '*')),
      row('answer', 'project:' + p.id, `${p.name}: answering their sessions’ prompts`, controlRule('answer', '*') === 'ask' ? 'allow' : controlRule('answer', '*')),
      row('close', 'project:' + p.id, `${p.name}: closing the sessions they started`, controlRule('close', '*') === 'ask' ? 'allow' : controlRule('close', '*')),
    ]));
}

/** The Pause button on the Permissions view's toolbar. */
function renderPermsPause() {
  const b = $('#permsPause');
  if (!b) return;
  b.classList.toggle('active', relay.paused);
  b.setAttribute('aria-pressed', String(relay.paused));
  b.textContent = relay.paused ? 'Agents paused: resume' : 'Pause agents';
  b.title = relay.paused ? 'Sessions can’t message, start or drive each other until you resume' : 'Stop every session messaging, starting or driving another, whatever the rules say, until you resume';
}
$('#permsPause')?.addEventListener('click', () => setRelayPaused(!relay.paused));

/** A session as plugins allowed to see sessions are told it. status is statusClass's: exited, asking (needs your answer), attention (finished, waiting for you), working or running (idle). */
const pluginSessionInfo = (s) => ({
  id: s.id, name: s.name, project: groupOf(s) === UNKNOWN ? null : projectName(groupOf(s)), directory: s.directory,
  worktree: s.git?.isRepo ? s.git.root : null, status: statusClass(s), open: s.id === state.expandedId,
});

/**
 * A plugin's request { requestId, name, … }; the answer is response { requestId, result } or { requestId, error }.
 * With "sessions" access:
 *   sessions            → every session (see pluginSessionInfo)
 *   activity { ids, have } → what Claude did in each: [{ id, found, conversation, version, from, events }], events
 *                          from `from` on ({ t, k, file, text, ok, q, ctx, out }; see ActivityReader). have: id → { conversation,
 *                          version } that the plugin already has, so only what's new comes back
 *   folders { sessionId } → { root, folders: [{ name, files }] }, the session's repository's top-level folders, or null
 *   showSession { sessionId } → opens the session, as clicking its tab would
 *   showPrompt { sessionId, at, index, text } → opens the session's Conversation tab at the prompt sent at `at` (ISO time;
 *                          `index`, its number among the prompts, is the fallback): Claude's full-screen mode keeps no
 *                          terminal scrollback, so the conversation is where a prompt can be jumped to. { index }
 * With "ask" access (and "sessions"):
 *   ask { question, ids, history } → { answer, writer, sessions }: Claude run headless with no tools (or Codex read-only
 *                          when Claude isn't installed; writer names it) answers the question from the conversations of the sessions named; history is earlier
 *                          [{ question, answer }] of the same chat, so a follow-up makes sense
 * Nothing lets a plugin type into a session: what it has to say reaches an agent as a message from it ("message").
 */
async function pluginRequest(p, m) {
  // A session hidden from the plugin is, to it, a session that doesn't exist.
  const sees = (id) => state.byId.has(id) && pluginSeesSession(p.id, id);
  const sessionArg = () => {
    if (!sees(m.sessionId)) throw new Error('There is no such session.');
    return state.byId.get(m.sessionId);
  };
  const NEEDS = { sessions: 'sessions', activity: 'sessions', folders: 'sessions', showSession: 'sessions', showPrompt: 'sessions', messageTargets: 'message', sendMessage: 'message', readMessages: 'message', ask: 'ask' };
  const kind = NEEDS[m.name];
  if (!kind) throw new Error(`There is no request "${m.name}".`);
  if (!p.access.includes(kind)) throw new Error(`Its plugin.json doesn't ask for "${kind}" access.`);
  if (!await pluginAllowed(p, kind)) throw new Error(`It isn't allowed to ${PLUGIN_ACCESS[kind].verb}.`);
  switch (m.name) {
    case 'sessions': return state.sessions.filter((s) => sees(s.id)).map(pluginSessionInfo);
    case 'activity': {
      const ids = (Array.isArray(m.ids) ? m.ids : []).filter((id) => typeof id === 'string' && sees(id)).slice(0, 100);
      const have = m.have && typeof m.have === 'object' ? m.have : {};
      return request('plugins.activity', { ids, have: Object.fromEntries(ids.filter((id) => have[id]).map((id) => [id, { conversation: String(have[id].conversation ?? ''), version: Number(have[id].version) || 0 }])) });
    }
    case 'folders': {
      const s = sessionArg();
      return s.git?.isRepo ? request('plugins.folders', { id: s.id }) : null;
    }
    case 'showSession': {
      const s = sessionArg();
      showTerminalDoc();
      expand(s.id);
      return null;
    }
    // Messages, through the relay as "plugin:<its id>" (the host checks again that it may, and which sessions it sees).
    case 'messageTargets': return request('relay.pluginTargets', { plugin: p.id });
    case 'sendMessage': {
      const text = typeof m.text === 'string' ? m.text : '';
      const to = typeof m.to === 'string' ? m.to : '';
      if (!state.byId.has(to) || !pluginSeesSession(p.id, to)) throw new Error('There is no such session.');
      return request('relay.pluginSend', { plugin: p.id, to, text, replyTo: typeof m.replyTo === 'string' ? m.replyTo : null });
    }
    case 'readMessages': return request('relay.pluginRead', { plugin: p.id });
    case 'showPrompt': {
      // Claude's full-screen mode keeps no terminal scrollback, so the prompt is shown in the session's Conversation tab,
      // found by when it was sent (the plugin's time comes from the same transcript), else by its number.
      const s = sessionArg();
      const at = typeof m.at === 'string' ? Date.parse(m.at) : NaN;
      if (!s.convFull) await loadConversation(s, true);
      const turns = s.convFull?.turns ?? [];
      if (!turns.length) throw new Error('There is no conversation to show for that session.');
      let i = Number.isFinite(at) ? turns.findIndex((t) => t.time && Math.abs(Date.parse(t.time) - at) < 5000) : -1;
      if (i < 0 && Number.isInteger(m.index) && turns[m.index]) i = m.index;
      if (i < 0) i = turns.length - 1;
      openConversation(s, i);
      return { index: i };
    }
    case 'ask': {
      if (!p.access.includes('sessions') || !await pluginAllowed(p, 'sessions')) throw new Error('Asking about sessions needs "sessions" access as well.');
      const question = typeof m.question === 'string' ? m.question.trim().slice(0, 4000) : '';
      if (!question) throw new Error('There is no question.');
      const ids = (Array.isArray(m.ids) ? m.ids : []).filter((id) => typeof id === 'string' && sees(id)).slice(0, 50);
      if (!ids.length) throw new Error('There are no sessions to ask about.');
      const history = (Array.isArray(m.history) ? m.history : []).slice(-8)
        .filter((x) => x && typeof x.question === 'string' && typeof x.answer === 'string')
        .map((x) => ({ question: x.question.slice(0, 4000), answer: x.answer.slice(0, 4000) }));
      const about = ids.map((id) => { const i = pluginSessionInfo(state.byId.get(id)); return { id, name: i.name, project: i.project, status: i.status }; });
      return request('plugins.ask', { question, history, sessions: about });
    }
  }
}

// What a plugin asks for its own tab, and its requests. Only its own frame is listened to, so it can't speak for
// another plugin (and the host keeps each plugin's frame on its own address), and only while its own page is in it
// (see pluginFrame): what comes before its page has loaded waits for that load, and goes if the load isn't its page.
addEventListener('message', (e) => {
  const id = [...pluginFrames].find(([, f]) => f.frame.contentWindow === e.source)?.[0];
  if (!id || typeof e.data?.type !== 'string') return;
  const f = pluginFrames.get(id);
  if (f.stopped) return;
  if (!f.ready) { if (f.early.length < 100) f.early.push(e.data); return; }
  pluginMessage(id, f, e.data);
});

function pluginMessage(id, f, data) {
  const old = data.type.startsWith(OLD_PLUGIN_PREFIX);
  const m = old ? { ...data, type: 'llamatree:' + data.type.slice(OLD_PLUGIN_PREFIX.length) } : data;
  if (m.type === 'llamatree:request') {
    const p = pluginById(id), frame = f.frame, page = f.page;
    // Answered under the name it asked by, once, and only to the page that asked: not one loaded since.
    const reply = (answer) => {
      if (pluginFrames.get(id) !== f || !f.ready || f.page !== page) return;
      frame.contentWindow?.postMessage({ type: (old ? OLD_PLUGIN_PREFIX : 'llamatree:') + 'response', requestId: m.requestId, ...answer }, '*');
    };
    pluginRequest(p, m).then((result) => reply({ result: result ?? null }), (err) => reply({ error: err.message }));
    return;
  }
  if (m.type === 'llamatree:open') {
    movePlugin(id, m.placement, m.state);
    return;
  }
  if (m.type === 'llamatree:badge') {
    const n = Math.floor(Number(m.count));
    f.badge = n > 0 ? { count: Math.min(n, 999) } : m.dot === true ? { dot: true } : null;
  } else if (m.type === 'llamatree:title') {
    const t = typeof m.title === 'string' ? m.title.replace(/\s+/g, ' ').trim().slice(0, 60) : '';
    f.title = t || null;
  } else return;
  renderPluginTab(id);
}

// Side panel plugins kept from last time show once the folder has been read.
loadPlugins().then(() => renderGit());
request('relay.state').then(relayChanged).catch(() => {});
request('control.state').then(controlChanged).catch(() => {});
loadThemes();

// ------------------------------------------------------------------ boot

$('#btnGrid').innerHTML = icon('grid');
$('#btnGit').innerHTML = icon('git');
$('#btnGitMenu').innerHTML = icon('chevD');
$('#btnAdd').innerHTML = icon('plus') + '<span>New Session</span>';
$('#gitRefresh').innerHTML = icon('refresh');
$('#gitHide').innerHTML = icon('close');
$('#diffPrev').innerHTML = icon('up');
$('#diffNext').innerHTML = icon('down');
$('#diffWs').innerHTML = icon('whitespace');
$('#diffOpenFile').innerHTML = icon('openFile');

$('#btnGrid').addEventListener('click', () => { showTerminalDoc(); collapse(); });
$('#btnGit').addEventListener('click', toggleGit);
$('#btnGitMenu').addEventListener('click', sidePanelMenu);
// Not addEventListener('click', showAddDialog): the click event would arrive as its `mode` argument.
$('#btnAdd').addEventListener('click', () => showAddDialog());
$('#btnAddEmpty').addEventListener('click', () => showAddDialog());

makeScrollable($('#projectTabs'));
makeScrollable($('#sessionTabs'));
makeScrollable($('#docTabs'), 'doc');
$('#docTabs').parentElement.append(h('div', { id: 'docTools' }));   // the title bar's buttons, in Left (see applyNavLayout)

/** Moves `id` before or after `targetId` in a list of ids. */
function moveId(ids, id, targetId, before) {
  const rest = ids.filter((x) => x !== id);
  const at = rest.indexOf(targetId);
  rest.splice(before ? at : at + 1, 0, id);
  return rest;
}

// Session tabs: dropping into another project's group moves the session into that project, at that spot.
const sessionDrag = {
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
};

// Project tabs; Unknown isn't draggable and stays last, so a drop beside it goes at the end of the real projects.
const projectDrag = {
  item: '.project-tab',
  canDrag: (id) => id !== UNKNOWN,
  onDrop: (id, targetId, before) => {
    const ids = state.projects.map((p) => p.id);
    const ordered = targetId === UNKNOWN ? moveId(ids, id, ids[ids.length - 1], false) : moveId(ids, id, targetId, before);
    request('projects.reorder', { ids: ordered }).catch((e) => toast(e.message, true));
  },
};
enableTabDrag($('#sessionTabs'), sessionDrag);
enableTabDrag($('#projectTabs'), projectDrag);
// The same down the left: each looks only for its own kind of row, so the two share the list.
enableTabDrag($('#navList'), { ...sessionDrag, axis: 'y' });
enableTabDrag($('#navList'), { ...projectDrag, axis: 'y' });

// ------------------------------------------------------------------ where projects and sessions go

const NAV_LAYOUTS = [
  { id: 'top', label: 'Projects and sessions across the top' },
  { id: 'hybrid', label: 'Projects on the left, sessions across the top' },
  { id: 'left', label: 'Projects and sessions on the left' },
];

/**
 * Applies the layout: Top keeps the two rows of tabs; Hybrid drops the projects row for the bar down the left (see
 * renderNav), and the buttons that row held join the title bar, All sessions first and the version and theme last.
 * Left drops the title bar too, with no session tabs left in it: All sessions and New Session go to the bar down the
 * left (renderNav puts them there), and the rest to the end of the tab row above the terminals (#docTools).
 */
function applyNavLayout(render = true) {
  const top = state.navLayout === 'top', left = state.navLayout === 'left';
  document.body.dataset.nav = state.navLayout;
  document.body.classList.toggle('no-nav-branches', !state.navBranches);
  // In Left the tab row runs across the side panel too (a grid row of #main), so these stay put as it opens and closes.
  const docRow = $('#docTools').parentElement;
  if (left) $('#main').prepend(docRow);
  else $('#stage').prepend(docRow);
  if (left) $('#docTools').append($('#btnUpdate'), $('#pluginButtons'), $('#appVersion'), $('#btnGit').parentElement, $('#btnTheme'));
  else {
    $('#titlebar').append($('#btnUpdate'), $('#btnAdd'), $('#btnGit').parentElement, $('#pluginButtons'));   // as index.html has them
    const row = top ? $('#projectBar') : $('#titlebar');
    row.prepend($('#btnGrid'));
    row.append($('#appVersion'), $('#btnTheme'));
  }
  $('#navBar').style.width = state.navWidth + 'px';
  if (render) applyProjectFilter();   // redraws both bars, and the empty grid's hint says where the projects are
}

function setNavLayout(id) {
  state.navLayout = id;
  store.set('navLayout', id);
  applyNavLayout();
}

const navLayoutItems = () => [
  { header: 'Layout' },
  ...NAV_LAYOUTS.map((l) => ({ label: l.label, checked: state.navLayout === l.id, action: () => setNavLayout(l.id) })),
  // With the sessions down the left, their branches can go (a row each, rather than two).
  // Across the top, projects can be tabs (a click shows only that one) rather than filters to tick.
  state.navLayout === 'top' && '-',
  state.navLayout === 'top' && { label: 'Projects as tabs', checked: state.projectTabs, action: () => setProjectTabs(!state.projectTabs) },
  state.navLayout === 'left' && '-',
  state.navLayout === 'left' && { label: 'Show branches', checked: state.navBranches, action: () => setNavBranches(!state.navBranches) },
].filter(Boolean);

function setNavBranches(on) {
  state.navBranches = on;
  store.set('navBranches', on);
  document.body.classList.toggle('no-nav-branches', !on);
}

applyNavLayout(false);   // the bars are filled once the sessions arrive

// Right-click on a bar's free space offers the layouts (a tab's own right-click has its menu).
for (const bar of [$('#projectBar'), $('#titlebar'), $('#navBar')]) {
  bar.addEventListener('contextmenu', (e) => {
    if (e.target.closest('button, .project-tab, .session-tab')) return;
    e.preventDefault();
    showMenu(e.clientX, e.clientY, navLayoutItems());
  });
}

// The bar down the left's width: dragged like the side panel's (its terminals refit as #termPane resizes).
(() => {
  const sp = $('#navSplitter');
  sp.addEventListener('mousedown', (e) => {
    e.preventDefault();
    sp.classList.add('dragging');
    const startX = e.clientX, startW = state.navWidth;
    const move = (ev) => {
      state.navWidth = Math.max(160, Math.min(480, innerWidth - 400, startW + (ev.clientX - startX)));
      $('#navBar').style.width = state.navWidth + 'px';
    };
    const up = () => {
      sp.classList.remove('dragging');
      store.set('navWidth', state.navWidth);
      removeEventListener('mousemove', move);
      removeEventListener('mouseup', up);
    };
    addEventListener('mousemove', move);
    addEventListener('mouseup', up);
  });
})();

// Diff tabs. The Terminal tab stays first, but can be dragged to a side of the area like them.
enableTabDrag($('#docTabs'), {
  item: '.doc-tab',
  fixed: (key) => key === 'terminal',
  onDrop: (key, targetKey, before) => {
    const order = moveId(docs.tabs.map((t) => t.key), key, targetKey, before);
    docs.tabs.sort((a, b) => order.indexOf(a.key) - order.indexOf(b.key));
    renderDocTabs();
  },
  outside: {
    over: (key, x, y) => { if (panelHint(key, x, y)) { dockHint(null); return 'panel'; } return dockHint(key, x, y); },
    drop: (key, dir) => (dir === 'panel' ? movePlugin(docs.tabs.find((t) => t.key === key).pluginId, 'panel', null)
      : key === 'terminal' ? splitTerminal(dir) : splitDoc(key, dir || null)),
  },
});

/** Whether a plugin's tab dragged to (x, y) is over the side panel, where it can go instead; shows that, or hides it. */
const panelDropHint = h('div', { class: 'dock-hint labelled', hidden: true }, 'In the side panel');
document.body.append(panelDropHint);
function panelHint(key, x, y) {
  const t = key && docs.tabs.find((d) => d.key === key);
  const r = $('#dockGroups').getBoundingClientRect();
  const over = isPluginTab(t) && state.gitVisible && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
  panelDropHint.hidden = !over;
  if (over) Object.assign(panelDropHint.style, { left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
  return over;
}

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
    isPluginTab(t) && { label: 'Move to the side panel', action: () => movePlugin(t.pluginId, 'panel', null) },
    isPluginTab(t) && '-',
    { label: 'Close', kbd: 'Ctrl+W', action: () => closeDoc(t.key) },
  ]);
});

// While split, the side you click or tab into has your attention (see attend).
for (const id of ['#diffDoc', '#convDoc', '#pluginDoc', '#permsDoc', '#galleryDoc']) {
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
