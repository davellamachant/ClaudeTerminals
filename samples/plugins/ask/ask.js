'use strict';
// Ask: a chat beside your sessions. Ask what a session is doing or waiting on, which one touched a file, or for a
// message to send one; an agent run headless by the app answers from the sessions' conversations. Each answer names
// the sessions it drew on (click one to open it) and can be sent to any session as a message from Ask, whose answer
// comes back here. The composer's left-hand box picks where what you write goes: to the assistant, or to a session.
//
// It's a Claude Terminals plugin with "sessions", "ask" and "message" access (see plugin.json): the app asks you once
// for each before the plugin gets it. It never types into a session: a session's agent reads what Ask sends as a
// message from a plugin, not as your prompt. The plugin keeps this chat only while its tab is open.

const REFRESH_MS = 3000, MAX_HISTORY = 8;
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

// ---- talking to Claude Terminals

let nextRequest = 1;
const pending = new Map();

/** A request to the app; it answers with what this plugin is allowed to do, or an error saying why not. */
function ask(name, args = {}) {
  const requestId = nextRequest++;
  return new Promise((resolve, reject) => {
    pending.set(requestId, { resolve, reject });
    parent.postMessage({ type: 'claude-terminals:request', requestId, name, ...args }, '*');
  });
}
const tell = (type, body) => parent.postMessage({ type: 'claude-terminals:' + type, ...body }, '*');

addEventListener('message', (e) => {
  if (e.source !== parent) return;
  const m = e.data;
  if (typeof m?.type !== 'string') return;
  if (m.type === 'claude-terminals:response') {
    const p = pending.get(m.requestId);
    if (!p) return;
    pending.delete(m.requestId);
    if ('error' in m) p.reject(new Error(m.error)); else p.resolve(m.result);
  } else if (m.type === 'claude-terminals:message') {
    readReplies();   // a session answered (or messaged Ask)
  } else if (m.type === 'claude-terminals:theme') {
    dress(m);
  } else if (m.type === 'claude-terminals:state') {
    // Just moved between the side panel and a document tab: the chat it sent along comes back.
    placed(m.placement);
    const s = m.state;
    if (s && typeof s === 'object') {
      if (Array.isArray(s.chat)) st.chat = s.chat.filter((x) => x && typeof x.text === 'string').slice(-200);
      if (typeof s.scope === 'string') st.wanted = s.scope;
      if (typeof s.to === 'string') st.to = s.to;
      if (typeof s.input === 'string') $('#input').value = s.input;
      applyWanted();
      renderChat();
      renderHeader();
      renderComposer();
    }
  } else if (m.type === 'claude-terminals:command') {
    // Opened from a session's or a project's menu, or the title bar button: ask about that.
    const c = m.context ?? {};
    if (c.sessionId) st.wanted = 'session:' + c.sessionId;
    else if ('project' in c) st.wanted = 'project:' + (c.project ?? NO_PROJECT);
    applyWanted();
    $('#input').focus();
  }
});

// ---- where the page is: the side panel (its usual place) or, on request, a wide document tab for big tables and long code

let placement = new URLSearchParams(location.search).get('placement') === 'tab' ? 'tab' : 'panel';
function placed(where) {
  placement = where === 'tab' ? 'tab' : 'panel';
  const b = $('#place');
  b.textContent = placement === 'panel' ? 'Open wide' : 'Back to panel';
  b.title = placement === 'panel' ? 'Open this chat as a wide document tab, for big tables and long code; the chat comes along'
    : 'Put this chat back in the side panel; the chat comes along';
}
$('#place').addEventListener('click', () => {
  // The app starts the page again in the other place and hands this back (see the state message).
  const state = { chat: st.chat.filter((m) => !m.pending), scope: st.scope, to: st.to, input: $('#input').value };
  parent.postMessage({ type: 'claude-terminals:open', placement: placement === 'panel' ? 'tab' : 'panel', state }, '*');
});
placed(placement);

// ---- the look: the app's theme down to its colours and font, so this page matches whatever theme is on

/** This page's colours by the app's names for them (see the theme message in the app's README). */
const ASK_COLORS = { bg: 'chrome', panel: 'panel', border: 'border', 'border-soft': 'border-soft', hover: 'hover-strong', text: 'text', 'text-dim': 'text-dim', 'text-faint': 'text-faint',
  code: 'code-bg', focus: 'focus', attention: 'attention', asking: 'asking', ok: 'ok', working: 'working', accent: 'accent-strong', danger: 'danger', bubble: 'panel', 'bubble-border': 'border-soft' };

/** Dresses the page for the app's theme: its colours and font when the app sends them, else the dark or light set in ask.css. */
function dress(m) {
  const root = document.documentElement;
  root.dataset.theme = m.theme === 'light' ? 'light' : 'dark';
  const c = m.colors ?? {};
  for (const [mine, theirs] of Object.entries(ASK_COLORS)) { if (c[theirs]) root.style.setProperty('--' + mine, c[theirs]); else root.style.removeProperty('--' + mine); }
  if (c.accent && c.chrome) {   // your own messages: a wash of the accent over the floor
    root.style.setProperty('--you', `color-mix(in srgb, ${c.accent} 16%, ${c.chrome})`);
    root.style.setProperty('--you-border', `color-mix(in srgb, ${c.accent} 38%, ${c.chrome})`);
  } else { root.style.removeProperty('--you'); root.style.removeProperty('--you-border'); }
  document.body.style.fontFamily = m.font ? `"${m.font}", "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif` : '';
  if (m.glow) root.dataset.glow = ''; else delete root.dataset.glow;
}

// ---- state

const st = {
  sessions: [],       // what the app says about each session
  scope: 'all',       // 'all' | 'project:<name>' | 'session:<id>': the sessions a question is about
  wanted: null,       // a scope asked for by a menu command, applied once the sessions are known
  to: 'ask',          // where the composer's text goes: 'ask' (the assistant) or a session id (messaged)
  chat: [],           // { role: 'you' | 'assistant' | 'reply' | 'note', text, to, from, question, pending, error, sessions, writer }
  unread: 0,          // answers that arrived while the tab was hidden (shown as its badge)
  denied: null,       // why the sessions can't be read, if they can't
};

const byId = (id) => st.sessions.find((s) => s.id === id);
const projectKey = (s) => s.project ?? NO_PROJECT;
const projectName = (key) => key === NO_PROJECT ? 'No project' : key;

/** The sessions the scope names (stopped ones too: their saved conversations can be read). */
function scoped() {
  if (st.scope.startsWith('session:')) return st.sessions.filter((s) => s.id === st.scope.slice(8));
  if (st.scope.startsWith('project:')) return st.sessions.filter((s) => projectKey(s) === st.scope.slice(8));
  return st.sessions;
}

function applyWanted() {
  if (!st.wanted || !st.sessions.length) return;
  const [kind, key] = [st.wanted.slice(0, st.wanted.indexOf(':')), st.wanted.slice(st.wanted.indexOf(':') + 1)];
  const exists = kind === 'session' ? !!byId(key) : st.sessions.some((s) => projectKey(s) === key);
  if (exists) { st.scope = st.wanted; renderHeader(); }
  st.wanted = null;
}

// ---- keeping up with the sessions

let timer = null;
async function refresh() {
  clearTimeout(timer);
  try {
    st.sessions = await ask('sessions');
    st.denied = null;
  } catch (e) {
    st.denied = e.message;
  }
  applyWanted();
  renderHeader();
  if (!document.hidden) timer = setTimeout(refresh, REFRESH_MS);
}

document.addEventListener('visibilitychange', () => {
  if (document.hidden) return;
  st.unread = 0;
  tell('badge', { count: 0 });
  refresh();
});

// ---- the header: scope, status, title

function renderHeader() {
  const scope = $('#scope');
  const groups = new Map();
  for (const s of st.sessions) groups.set(projectKey(s), (groups.get(projectKey(s)) ?? 0) + 1);
  const options = [h('option', { value: 'all' }, `All sessions (${st.sessions.length})`)];
  if (groups.size > 1) for (const [key, n] of [...groups].sort((a, b) => (a[0] === NO_PROJECT) - (b[0] === NO_PROJECT) || a[0].localeCompare(b[0])))
    options.push(h('option', { value: 'project:' + key }, `${projectName(key)} (${n})`));
  const byProject = [...st.sessions].sort((a, b) => projectName(projectKey(a)).localeCompare(projectName(projectKey(b))) || a.name.localeCompare(b.name));
  if (byProject.length) {
    const group = h('optgroup', { label: 'One session' });
    for (const s of byProject) group.append(h('option', { value: 'session:' + s.id }, groups.size > 1 && s.project ? `${s.name} · ${s.project}` : s.name));
    options.push(group);
  }
  if (!options.some((o) => o.value === st.scope) && !byProject.some((s) => 'session:' + s.id === st.scope)) st.scope = 'all';
  scope.replaceChildren(...options);
  scope.value = st.scope;

  const counts = { working: 0, asking: 0, attention: 0, running: 0, exited: 0 };
  for (const s of scoped()) counts[s.status in counts ? s.status : 'running']++;
  $('#status').replaceChildren(...[
    counts.working > 0 && h('span', { class: 'w' }, `${counts.working} working`),
    counts.asking > 0 && h('span', { class: 'a' }, `${counts.asking} need${counts.asking === 1 ? 's' : ''} your answer`),
    counts.attention > 0 && h('span', { class: 'n' }, `${counts.attention} waiting for you`),
    counts.running > 0 && h('span', { class: 'i' }, `${counts.running} idle`),
    counts.exited > 0 && h('span', { class: 'x' }, `${counts.exited} not running`),
  ].filter(Boolean));

  const one = st.scope.startsWith('session:') ? byId(st.scope.slice(8)) : null;
  tell('title', { title: one ? `Ask: ${one.name}` : null });

  // Where what you write goes: the assistant, or a session, as a message (a stopped one reads it when it next runs).
  const to = $('#to');
  const targets = [...st.sessions].sort((a, b) => a.name.localeCompare(b.name));
  if (st.to !== 'ask' && !targets.some((s) => s.id === st.to)) st.to = 'ask';
  to.replaceChildren(h('option', { value: 'ask' }, 'Ask the assistant'), ...targets.map((s) => h('option', { value: s.id }, `Message ${s.name}`)));
  to.value = st.to;
  to.disabled = targets.length === 0;
  renderComposer();

  const message = $('#message');
  if (st.denied) {
    message.hidden = false;
    message.replaceChildren(h('b', {}, 'Ask can’t see your sessions yet.'), h('br'), h('span', {}, st.denied + ' Allow it under the palette button › Plugins › Ask, and this page tries again.'));
  } else if (!st.sessions.length) {
    message.hidden = false;
    message.replaceChildren(h('b', {}, 'No sessions yet.'), h('br'), 'Start one, then ask about it here.');
  } else message.hidden = st.chat.length > 0;
  if (!st.denied && !st.chat.length && st.sessions.length) message.replaceChildren(
    h('b', {}, 'Ask about your sessions.'), h('br'),
    '“What is each session doing?” · “Which one touched the import?” · “What is Billing waiting on?” · “Write a message for Checkout: stop and commit what you have.”', h('br'), h('br'),
    'The answer is written by Claude, run once with your own sign-in, from the sessions’ conversations. Pick where it goes with the box on the left.');
}

function renderComposer() {
  const toSession = st.to !== 'ask' ? byId(st.to) : null;
  $('#go').textContent = toSession ? 'Send' : 'Ask';
  $('#go').title = toSession
    ? `Send this to ${toSession.name} as a message from Ask; its answer comes back here (Enter does the same)`
    : 'Send the question to Claude, which answers from the sessions’ conversations (Enter does the same)';
  $('#input').placeholder = toSession
    ? `Message ${toSession.name}… (Enter sends, Shift+Enter for a new line)`
    : 'Ask about your sessions… (Enter sends, Shift+Enter for a new line)';
}

$('#scope').addEventListener('change', (e) => { st.scope = e.target.value; renderHeader(); $('#input').focus(); });
$('#to').addEventListener('change', (e) => { st.to = e.target.value; renderComposer(); $('#input').focus(); });
$('#clear').addEventListener('click', () => { st.chat = []; renderChat(); renderHeader(); $('#input').focus(); });

// The ? button shows how Ask works, at any time; Close or ? again puts it away.
function showHelp(on) {
  $('#helpPanel').hidden = !on;
  $('#help').classList.toggle('on', on);
  if (on) $('#log').scrollTop = 0; else $('#input').focus();
}
$('#help').addEventListener('click', () => showHelp($('#helpPanel').hidden));
$('#helpClose').addEventListener('click', () => showHelp(false));
addEventListener('keydown', (e) => { if (e.key === 'Escape' && !$('#helpPanel').hidden) showHelp(false); });

// ---- the chat

/** Earlier questions and answers, so a follow-up ("and Billing?") makes sense to the agent. */
const history = () => st.chat.filter((m) => m.role === 'assistant' && !m.pending && !m.error).slice(-MAX_HISTORY).map((m) => ({ question: m.question, answer: m.text }));

async function submit() {
  const input = $('#input');
  const text = input.value.trim();
  if (!text) return;
  input.value = '';
  autosize();
  if (st.to === 'ask') await question(text);
  else await messageTo(st.to, text);
  input.focus();
}

async function question(text) {
  const about = scoped();
  if (!about.length) { note('There are no sessions to ask about.', true); return; }
  st.chat.push({ role: 'you', text });
  const answer = { role: 'assistant', text: '', question: text, pending: true, error: null, sessions: about.map((s) => s.id), writer: null, history: history() };
  st.chat.push(answer);
  renderChat();
  await answerOf(answer);
}

async function answerOf(answer) {
  answer.pending = true;
  answer.error = null;
  renderChat();
  try {
    const r = await ask('ask', { question: answer.question, ids: answer.sessions, history: answer.history });
    answer.text = r.answer;
    answer.writer = r.writer;
    answer.sessions = r.sessions;
  } catch (e) {
    answer.error = e.message;
  }
  answer.pending = false;
  if (document.hidden) tell('badge', { count: ++st.unread });
  renderChat();
}

/** Sends text to a session as a message from Ask (the app asks you the first time); its answer comes back as a reply. */
async function messageTo(id, text) {
  const s = byId(id);
  if (!s) { note('That session is gone.', true); return; }
  try {
    const r = await ask('sendMessage', { to: id, text, replyTo: null });
    if (r?.outcome === 'denied') { note(`Ask isn’t allowed to message ${s.name}.`, true); return; }
    st.chat.push({ role: 'you', text, to: s.name });
    note(s.status === 'exited' ? `Sent. ${s.name} reads it when it next runs; its answer comes back here.` : `Sent to ${s.name}. Its answer comes back here.`);
  } catch (e) {
    note(`Couldn’t message ${s.name}: ${e.message}`, true);
  }
}

/** What sessions sent Ask: their answers, shown in the chat. */
async function readReplies() {
  try {
    const got = await ask('readMessages');
    if (!got.length) return;
    for (const m of got) st.chat.push({ role: 'reply', text: m.text, from: m.fromName ?? m.from });
    if (document.hidden) tell('badge', { count: st.unread += got.length });
    renderChat();
  } catch (e) {
    note(`Couldn’t read the sessions’ answers: ${e.message}`, true);
  }
}

function note(text, error = false) {
  st.chat.push({ role: 'note', text, error });
  renderChat();
}

function renderChat() {
  const log = $('#log'), chat = $('#chat');
  const stuck = log.scrollTop + log.clientHeight >= log.scrollHeight - 40;
  chat.replaceChildren(...st.chat.map(renderMessage));
  if (st.chat.length) $('#message').hidden = true; else renderHeader();   // the welcome comes back once the chat is cleared
  if (stuck) log.scrollTop = log.scrollHeight;
}

function renderMessage(m) {
  if (m.role === 'you') return h('div', { class: 'msg you' }, h('div', { class: 'bubble' }, m.text), m.to && h('div', { class: 'meta' }, `To ${m.to}`));
  if (m.role === 'reply') return h('div', { class: 'msg assistant' }, markdown(m.text), h('div', { class: 'meta' }, h('span', { class: 'writer' }, `${m.from} answered`)));
  if (m.role === 'note') return h('div', { class: 'msg note' + (m.error ? ' error' : '') }, m.text);
  const bubble = m.pending ? h('div', { class: 'bubble pending' }, 'Reading the sessions…')
    : m.error ? h('div', { class: 'bubble error' }, m.error)
    : markdown(m.text);
  const chips = m.sessions.map((id) => byId(id)).filter(Boolean).map((s) =>
    h('button', { class: 'chip dot ' + s.status, title: `Open ${s.name}`, onclick: () => ask('showSession', { sessionId: s.id }).catch((e) => note(e.message, true)) }, s.name));
  const sendTo = !m.pending && !m.error && st.sessions.length > 0 && h('select', { title: 'Send this answer to a session, as a message from Ask', onchange: (e) => {
    const id = e.target.value;
    e.target.value = '';
    if (id) messageTo(id, m.text);
  } }, h('option', { value: '' }, 'Send to…'), ...st.sessions.map((s) => h('option', { value: s.id }, s.name)));
  const retry = m.error && h('button', { class: 'chip', onclick: () => answerOf(m) }, 'Try again');
  return h('div', { class: 'msg assistant' }, bubble,
    h('div', { class: 'meta' }, chips, sendTo, retry, m.writer && h('span', { class: 'writer' }, `${m.writer} answered`)));
}

/** The answer's light Markdown as elements: paragraphs, lists, fenced code, inline code and bold. Text is never put in as HTML. */
function markdown(text) {
  const out = h('div', { class: 'bubble' });
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  let i = 0;
  const inline = (s) => {
    const frag = document.createDocumentFragment();
    const re = /(`[^`]+`)|(\*\*[^*]+\*\*)/g;
    let last = 0, m;
    while ((m = re.exec(s))) {
      if (m.index > last) frag.append(s.slice(last, m.index));
      if (m[1]) frag.append(h('code', {}, m[1].slice(1, -1)));
      else frag.append(h('b', {}, m[2].slice(2, -2)));
      last = m.index + m[0].length;
    }
    if (last < s.length) frag.append(s.slice(last));
    return frag;
  };
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
    // A table: a header row of cells between pipes, a rule of dashes, then rows. It scrolls sideways in the side panel.
    const row = /^\s*\|.*\|\s*$/, rule = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/;
    if (row.test(line) && i + 1 < lines.length && rule.test(lines[i + 1])) {
      const cells = (l) => l.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim());
      const table = h('table', {}, h('thead', {}, h('tr', {}, ...cells(line).map((c) => h('th', {}, inline(c))))));
      const body = h('tbody');
      for (i += 2; i < lines.length && row.test(lines[i]); i++) body.append(h('tr', {}, ...cells(lines[i]).map((c) => h('td', {}, inline(c)))));
      table.append(body);
      out.append(h('div', { class: 'table' }, table));
      continue;
    }
    const bullet = /^\s*[-*•]\s+/, numbered = /^\s*\d+[.)]\s+/;
    if (bullet.test(line) || numbered.test(line)) {
      const ordered = numbered.test(line);
      const list = h(ordered ? 'ol' : 'ul');
      while (i < lines.length && (ordered ? numbered : bullet).test(lines[i])) {
        let item = lines[i].replace(ordered ? numbered : bullet, '');
        for (i++; i < lines.length && /^\s{2,}\S/.test(lines[i]) && !bullet.test(lines[i]) && !numbered.test(lines[i]); i++) item += ' ' + lines[i].trim();
        list.append(h('li', {}, inline(item)));
      }
      out.append(list);
      continue;
    }
    const para = [];
    while (i < lines.length && lines[i].trim() !== '' && !lines[i].startsWith('```') && !bullet.test(lines[i]) && !numbered.test(lines[i]) && !(row.test(lines[i]) && rule.test(lines[i + 1] ?? ''))) para.push(lines[i++]);
    const p = h('p', {}, inline(para[0].replace(/^#+\s*/, '')));
    for (const l of para.slice(1)) p.append(h('br'), inline(l));
    out.append(p);
  }
  return out;
}

// ---- the composer

function autosize() {
  const input = $('#input');
  input.style.height = 'auto';
  input.style.height = Math.min(160, input.scrollHeight) + 'px';
}
$('#input').addEventListener('input', autosize);
$('#input').addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); submit(); }
});
$('#go').addEventListener('click', submit);

refresh();
