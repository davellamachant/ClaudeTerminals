#!/usr/bin/env node
// Checks every plugin in this folder (plugins/<id>/) the way LlamaTree reads it (see PluginCatalog in the app), and a few things the
// repository asks for on top: a README and a CHANGELOG per plugin, no symlinks, and, with --base <ref>, that a plugin whose
// files changed since that ref has a new version. No dependencies; the JSON Schema in schema/ says the same rules for editors.
//
//   node plugins/tools/validate.mjs              every plugin
//   node plugins/tools/validate.mjs --base origin/main
//   node plugins/tools/validate.mjs messenger    one plugin
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const pluginsDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');   // plugins/
const repoRoot = path.resolve(pluginsDir, '..');
const ID = /^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$/;
const SEMVER = /^\d+\.\d+\.\d+$/;
const ACCESS = ['sessions', 'message', 'ask'];
const MENUS = ['palette', 'project', 'session', 'sessionTab'];
const PLACEMENTS = ['tab', 'panel'];
const ICON_TYPES = ['.svg', '.png', '.ico', '.gif', '.jpg', '.jpeg', '.webp'];
const MAX_ICON = 64 * 1024;

const args = process.argv.slice(2);
const base = args.includes('--base') ? args[args.indexOf('--base') + 1] : null;
const only = args.filter((a, i) => !a.startsWith('--') && args[i - 1] !== '--base');

const problems = [];
const problem = (id, text) => problems.push(`${id}: ${text}`);

/** A path given in plugin.json, resolved inside the plugin's folder, or null when it points outside or doesn't exist. */
function inside(dir, file) {
  if (typeof file !== 'string' || !file || file.includes('\\') || file.startsWith('/') || path.isAbsolute(file)) return null;
  const full = path.resolve(dir, file);
  if (!full.startsWith(dir + path.sep)) return null;
  return fs.existsSync(full) && fs.statSync(full).isFile() ? full : null;
}

function checkImage(id, dir, file, what) {
  const full = inside(dir, file);
  if (!full || !ICON_TYPES.includes(path.extname(full).toLowerCase())) return problem(id, `${what} must be an image file inside the folder (${ICON_TYPES.join(', ')}): ${JSON.stringify(file)}`);
  if (fs.statSync(full).size > MAX_ICON) problem(id, `${what} is over ${MAX_ICON / 1024} KB`);
}

export function readManifest(dir) {
  const bytes = fs.readFileSync(path.join(dir, 'plugin.json'));
  const bom = bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
  return { manifest: JSON.parse(bytes.subarray(bom ? 3 : 0).toString('utf8')), bom };
}

function checkPlugin(id) {
  const dir = path.join(pluginsDir, id);
  if (!ID.test(id)) return problem(id, 'the folder\'s name must be letters, digits and hyphens, 40 at most, not starting or ending with a hyphen');
  if (!fs.existsSync(path.join(dir, 'plugin.json'))) return problem(id, 'no plugin.json');
  let m;
  try {
    const r = readManifest(dir);
    m = r.manifest;
    if (r.bom) problem(id, 'plugin.json starts with a byte order mark; save it as UTF-8 without one');
  } catch (e) { return problem(id, `plugin.json can't be read: ${e.message}`); }
  if (typeof m !== 'object' || m === null || Array.isArray(m)) return problem(id, 'plugin.json must hold an object');

  const known = ['title', 'description', 'version', 'minAppVersion', 'entry', 'icon', 'placement', 'access', 'inbox', 'menu', 'toolbar'];
  for (const k of Object.keys(m)) if (!known.includes(k)) problem(id, `plugin.json has "${k}", which the app doesn't read`);
  if (typeof m.title !== 'string' || !m.title.trim()) problem(id, '"title" is needed');
  else if (m.title.length > 60) problem(id, '"title" is over 60 characters');
  if (m.description !== undefined && (typeof m.description !== 'string' || m.description.length > 300)) problem(id, '"description" must be a string of 300 characters at most');
  if (typeof m.version !== 'string' || !SEMVER.test(m.version)) problem(id, '"version" is needed, as major.minor.patch');
  if (m.minAppVersion !== undefined && (typeof m.minAppVersion !== 'string' || !SEMVER.test(m.minAppVersion))) problem(id, '"minAppVersion" must be major.minor.patch');

  const entry = m.entry ?? 'index.html';
  if (!inside(dir, entry)) problem(id, `"entry" must be a file inside the folder: ${JSON.stringify(entry)}`);
  if (m.icon !== undefined) checkImage(id, dir, m.icon, '"icon"');
  if (m.placement !== undefined && !PLACEMENTS.includes(m.placement)) problem(id, '"placement" must be "tab" or "panel"');
  if (m.access !== undefined) {
    if (!Array.isArray(m.access) || m.access.some((a) => !ACCESS.includes(a))) problem(id, `"access" must list only ${ACCESS.join(', ')}`);
    else if (new Set(m.access).size !== m.access.length) problem(id, '"access" lists something twice');
  }
  if (m.inbox !== undefined && typeof m.inbox !== 'boolean') problem(id, '"inbox" must be true or false');
  if (m.inbox === true && !(m.access ?? []).includes('message')) problem(id, '"inbox": true needs "message" in "access"');
  if (m.menu !== undefined) {
    if (!Array.isArray(m.menu)) problem(id, '"menu" must be a list');
    else m.menu.forEach((item, i) => {
      if (typeof item !== 'object' || item === null) return problem(id, `menu item ${i + 1} must be an object`);
      if (typeof item.label !== 'string' || !item.label.trim()) problem(id, `menu item ${i + 1} needs a "label"`);
      if (typeof item.command !== 'string') problem(id, `menu item ${i + 1} needs a "command"`);
      const where = item.where === undefined ? ['palette'] : typeof item.where === 'string' ? [item.where] : item.where;
      if (!Array.isArray(where) || !where.length || where.some((w) => !MENUS.includes(w)) || new Set(where).size !== where.length)
        problem(id, `menu item ${i + 1}: "where" must name menus from ${MENUS.join(', ')}, each once`);
      for (const k of Object.keys(item)) if (!['label', 'command', 'where'].includes(k)) problem(id, `menu item ${i + 1} has "${k}", which the app doesn't read`);
    });
  }
  if (m.toolbar !== undefined) {
    if (typeof m.toolbar !== 'object' || m.toolbar === null) problem(id, '"toolbar" must be an object');
    else {
      checkImage(id, dir, m.toolbar.icon, '"toolbar.icon"');
      if (m.toolbar.tooltip !== undefined && (typeof m.toolbar.tooltip !== 'string' || m.toolbar.tooltip.length > 120)) problem(id, '"toolbar.tooltip" must be a string of 120 characters at most');
      if (m.toolbar.command !== undefined && typeof m.toolbar.command !== 'string') problem(id, '"toolbar.command" must be a string');
      for (const k of Object.keys(m.toolbar)) if (!['icon', 'tooltip', 'command'].includes(k)) problem(id, `"toolbar" has "${k}", which the app doesn't read`);
    }
  }

  // The repository's own asks.
  for (const f of ['README.md', 'CHANGELOG.md']) if (!fs.existsSync(path.join(dir, f))) problem(id, `no ${f}`);
  if (fs.existsSync(path.join(dir, 'CHANGELOG.md')) && m.version && !fs.readFileSync(path.join(dir, 'CHANGELOG.md'), 'utf8').includes(`## ${m.version}`))
    problem(id, `CHANGELOG.md has no "## ${m.version}" section for the version in plugin.json`);
  for (const f of walk(dir)) if (fs.lstatSync(f).isSymbolicLink()) problem(id, `symlink: ${path.relative(dir, f)}`);

  // A changed plugin needs a new version, so releases and the catalog can tell them apart.
  if (base && m.version) {
    const changed = git(['diff', '--name-only', `${base}...HEAD`, '--', `plugins/${id}/`]).trim();
    if (changed) {
      let before = null;
      try { before = JSON.parse(git(['show', `${base}:plugins/${id}/plugin.json`])).version; } catch { /* new plugin */ }
      if (before === m.version) problem(id, `files changed since ${base} but "version" is still ${m.version}; bump it and add a CHANGELOG section`);
    }
  }
}

function* walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    yield p;
    if (e.isDirectory() && !e.isSymbolicLink()) yield* walk(p);
  }
}

function git(a) { return execFileSync('git', a, { cwd: repoRoot, encoding: 'utf8' }); }

export function pluginIds() {
  if (!fs.existsSync(pluginsDir)) return [];
  // Each folder holding a plugin.json; schema/ and tools/ live here too.
  return fs.readdirSync(pluginsDir, { withFileTypes: true }).filter((e) => e.isDirectory() && fs.existsSync(path.join(pluginsDir, e.name, 'plugin.json'))).map((e) => e.name).sort();
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const ids = only.length ? only : pluginIds();
  for (const id of ids) checkPlugin(id);
  if (problems.length) {
    console.error(problems.map((p) => '  ' + p).join('\n'));
    console.error(`\n${problems.length} problem${problems.length === 1 ? '' : 's'} in ${ids.length} plugin${ids.length === 1 ? '' : 's'}.`);
    process.exit(1);
  }
  console.log(`${ids.length} plugin${ids.length === 1 ? '' : 's'} checked: ${ids.join(', ') || 'none'}. All fine.`);
}
