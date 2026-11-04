'use strict';

// Messenger: a sample plugin with "message" access and "inbox": true. It lists the sessions it may message
// (messageTargets), sends one a message (sendMessage) and shows what comes back (readMessages), whenever the app says
// a message has come for it (llamatree:message). The first request asks you whether to let it, and which
// projects' sessions it may message.

const $ = (id) => document.getElementById(id);
const pending = new Map();
let nextRequest = 1;

/** A request to the app; resolves with its result, or rejects with the reason it was refused. */
function request(name, args = {}) {
  const requestId = nextRequest++;
  return new Promise((resolve, reject) => {
    pending.set(requestId, { resolve, reject });
    parent.postMessage({ type: 'llamatree:request', requestId, name, ...args }, '*');
  });
}

function show(text, who, you = false) {
  const log = $('log');
  log.querySelector('.note')?.remove();
  const el = document.createElement('div');
  el.className = 'msg' + (you ? ' you' : '');
  const w = document.createElement('div');
  w.className = 'who';
  w.textContent = who;
  el.append(w, document.createTextNode(text));
  log.append(el);
  log.scrollTop = log.scrollHeight;
}

function note(text) {
  const p = document.createElement('p');
  p.className = 'note';
  p.textContent = text;
  $('log').append(p);
}

/** The sessions it may message, in the picker (keeping the one picked, or picking `want`). */
async function loadTargets(want = null) {
  const keep = want ?? $('to').value;
  try {
    const targets = await request('messageTargets');
    $('to').replaceChildren(...targets.map((t) => {
      const o = document.createElement('option');
      o.value = t.id;
      o.textContent = `${t.name}${t.project ? ` · ${t.project}` : ''} (${t.agent}${t.running ? '' : ', stopped'})`;
      return o;
    }));
    if (targets.some((t) => t.id === keep)) $('to').value = keep;
    if (!targets.length) note('There is no session it may message. Permissions › By plugin chooses its projects.');
  } catch (e) {
    note(e.message);
  }
  $('send').disabled = !$('to').value;
}

let lastSent = null;   // the id of the last message sent, so a reply to it can be shown as one

async function send() {
  const text = $('text').value.trim();
  const to = $('to').value;
  if (!text || !to) return;
  $('send').disabled = true;
  try {
    const r = await request('sendMessage', { to, text, replyTo: null });
    show(text, `You → ${$('to').selectedOptions[0]?.textContent ?? to}`, true);
    $('text').value = '';
    lastSent = r.id;
    if (r.outcome === 'denied') note('Not sent: it may not message that session.');
    else note('Delivered. The agent is told it has a message once it’s idle.');
  } catch (e) {
    note(e.message);
  } finally {
    $('send').disabled = !$('to').value;
  }
}

async function readAll() {
  try {
    for (const m of await request('readMessages')) show(m.text, `${m.fromName ?? m.from}${m.replyTo ? (m.replyTo === lastSent ? ', answering you' : ', replying') : ''}`);
  } catch (e) {
    note(e.message);
  }
}

addEventListener('message', (e) => {
  if (e.source !== parent) return;
  const m = e.data;
  if (m?.type === 'llamatree:response') {
    const p = pending.get(m.requestId);
    if (!p) return;
    pending.delete(m.requestId);
    if ('error' in m) p.reject(new Error(m.error)); else p.resolve(m.result);
  } else if (m?.type === 'llamatree:theme') {
    document.documentElement.dataset.theme = m.theme;
  } else if (m?.type === 'llamatree:message') {
    readAll();
  } else if (m?.type === 'llamatree:command' && m.command === 'to' && m.context?.sessionId) {
    loadTargets(m.context.sessionId);
  }
});

document.documentElement.dataset.theme = new URLSearchParams(location.search).get('theme') ?? 'dark';
$('send').addEventListener('click', send);
$('refresh').addEventListener('click', () => loadTargets());
$('text').addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.ctrlKey) send(); });
loadTargets().then(readAll);
