'use strict';
// Ask: a chat beside your sessions. Ask what a session is doing or waiting on, which one touched a file, or for a
// message to send one; an agent run headless by the app answers from the sessions' conversations. Each answer names
// the sessions it drew on (click one to open it) and can be typed into any running session, for you to send or sent
// straight away. The composer's left-hand box picks where what you type goes: to the assistant, or into a session.
//
// It's a Claude Terminals plugin with "sessions", "ask" and "type" access (see plugin.json): the app asks you once
// for each before the plugin gets it. The plugin keeps this chat only while its tab is open.

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
  } else if (m.type === 'claude-terminals:theme') {
    document.documentElement.dataset.theme = m.theme === 'light' ? 'light' : 'dark';
  } else if (m.type === 'claude-terminals:command') {
    // Opened from a session's or a project's menu, or the title bar button: ask about that.
    const c = m.context ?? {};
    if (c.sessionId) st.wanted = 'session:' + c.sessionId;
    else if ('project' in c) st.wanted = 'project:' + (c.project ?? NO_PROJECT);
    applyWanted();
    $('#input').focus();
  }
});

// ---- state

const st = {
  sessions: [],       // what the app says about each session
  scope: 'all',       // 'all' | 'project:<name>' | 'session:<id>': the sessions a question is about
  wanted: null,       // a scope asked for by a menu command, applied once the sessions are known
  to: 'ask',          // where the composer's text goes: 'ask' (the assistant) or a session id (typed into it)
  chat: [],           // { role: 'you' | 'assistant' | 'note', text, question, pending, error, sessions, writer }
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

  // Where what you type goes: the assistant, or a running session's terminal.
  const to = $('#to');
  const running = st.sessions.filter((s) => s.status !== 'exited').sort((a, b) => a.name.localeCompare(b.name));
  if (st.to !== 'ask' && !running.some((s) => s.id === st.to)) st.to = 'ask';
  to.replaceChildren(h('option', { value: 'ask' }, 'Ask the assistant'), ...running.map((s) => h('option', { value: s.id }, `Type into ${s.name}`)));
  to.value = st.to;
  to.disabled = running.length === 0;
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
  $('#go').textContent = toSession ? 'Type' : 'Ask';
  $('#go').title = toSession
    ? `Type this into ${toSession.name}'s terminal, as if pasted (Enter does the same)`
    : 'Send the question to Claude, which answers from the sessions’ conversations (Enter does the same)';
  $('#sendRow').hidden = !toSession;
  $('#input').placeholder = toSession
    ? `Type into ${toSession.name}… (Enter types it, Shift+Enter for a new line)`
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
  else await typeInto(st.to, text, $('#send').checked);
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

async function typeInto(id, text, send) {
  const s = byId(id);
  if (!s) { note('That session is gone.', true); return; }
  try {
    await ask('type', { sessionId: id, text, send });
    note(send ? `Sent to ${s.name}.` : `Typed into ${s.name}, for you to send.`);
  } catch (e) {
    note(`Couldn’t type into ${s.name}: ${e.message}`, true);
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
  if (m.role === 'you') return h('div', { class: 'msg you' }, h('div', { class: 'bubble' }, m.text));
  if (m.role === 'note') return h('div', { class: 'msg note' + (m.error ? ' error' : '') }, m.text);
  const bubble = m.pending ? h('div', { class: 'bubble pending' }, 'Reading the sessions…')
    : m.error ? h('div', { class: 'bubble error' }, m.error)
    : markdown(m.text);
  const chips = m.sessions.map((id) => byId(id)).filter(Boolean).map((s) =>
    h('button', { class: 'chip dot ' + s.status, title: `Open ${s.name}`, onclick: () => ask('showSession', { sessionId: s.id }).catch((e) => note(e.message, true)) }, s.name));
  const running = st.sessions.filter((s) => s.status !== 'exited');
  const typeTo = !m.pending && !m.error && running.length > 0 && h('select', { title: 'Type this answer into a session, for you to send', onchange: (e) => {
    const id = e.target.value;
    e.target.value = '';
    if (id) typeInto(id, m.text, false);
  } }, h('option', { value: '' }, 'Type into…'), ...running.map((s) => h('option', { value: s.id }, s.name)));
  const retry = m.error && h('button', { class: 'chip', onclick: () => answerOf(m) }, 'Try again');
  return h('div', { class: 'msg assistant' }, bubble,
    h('div', { class: 'meta' }, chips, typeTo, retry, m.writer && h('span', { class: 'writer' }, `${m.writer} answered`)));
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
    while (i < lines.length && lines[i].trim() !== '' && !lines[i].startsWith('```') && !bullet.test(lines[i]) && !numbered.test(lines[i])) para.push(lines[i++]);
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
