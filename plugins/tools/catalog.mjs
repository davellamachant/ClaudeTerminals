#!/usr/bin/env node
// Builds catalog.json, what LlamaTree will read to offer the plugins, from every plugins/<id>/plugin.json, and the table
// of plugins in plugins/README.md between its markers. The release workflow runs it after each release; nobody edits the catalog
// by hand.
//
//   node plugins/tools/catalog.mjs --readme            rewrite the README table from the manifests
//   node plugins/tools/catalog.mjs --readme --check    fail if the README table is out of date (the validate workflow)
//   node plugins/tools/catalog.mjs --out catalog.json --sha-dir <dir>
//       write the catalog; <dir> holds <id>-<version>.zip.sha256 files downloaded from the releases, and a plugin without
//       one is left out of the catalog (it has no release yet).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { pluginIds, readManifest } from './validate.mjs';

const pluginsDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');   // plugins/
const REPO = 'LlamachantTechnology/LlamaTree';
const args = process.argv.slice(2);
const opt = (name) => (args.includes(name) ? args[args.indexOf(name) + 1] : null);

export const tagOf = (id, version) => `${id}-v${version}`;
export const zipName = (id, version) => `${id}-${version}.zip`;
export const zipUrl = (id, version) => `https://github.com/${REPO}/releases/download/${tagOf(id, version)}/${zipName(id, version)}`;

function entries() {
  return pluginIds().map((id) => {
    const { manifest: m } = readManifest(path.join(pluginsDir, id));
    return {
      id, title: m.title, description: m.description ?? '', version: m.version, minAppVersion: m.minAppVersion ?? null,
      placement: m.placement ?? 'tab', access: m.access ?? [], inbox: m.inbox === true,
    };
  });
}

function readmeTable(list) {
  const lines = ['| Plugin | What it does | Version | Shows in | Asks to see |', '|---|---|---|---|---|'];
  for (const e of list) {
    const access = e.access.length ? e.access.join(', ') : 'nothing';
    lines.push(`| [${e.title}](${e.id}) | ${e.description} | [${e.version}](https://github.com/${REPO}/releases/tag/${tagOf(e.id, e.version)}) | ${e.placement === 'panel' ? 'the side panel' : 'a tab'} | ${access} |`);
  }
  return lines.join('\n');
}

if (args.includes('--readme')) {
  const file = path.join(pluginsDir, 'README.md');
  const text = fs.readFileSync(file, 'utf8');
  const start = '<!-- plugins:start -->', end = '<!-- plugins:end -->';
  const a = text.indexOf(start), b = text.indexOf(end);
  if (a < 0 || b < 0 || b < a) { console.error('README.md needs the markers <!-- plugins:start --> and <!-- plugins:end -->'); process.exit(1); }
  const nl = text.includes('\r\n') ? '\r\n' : '\n';   // the file's own line endings (a Windows checkout has CRLF)
  const next = text.slice(0, a + start.length) + nl + readmeTable(entries()).replace(/\n/g, nl) + nl + text.slice(b);
  if (args.includes('--check')) {
    if (next !== text) { console.error('README.md\'s table of plugins is out of date: run  node plugins/tools/catalog.mjs --readme  and commit it.'); process.exit(1); }
    console.log('README.md\'s table of plugins is up to date.');
  } else {
    fs.writeFileSync(file, next);
    console.log('README.md\'s table of plugins rewritten.');
  }
}

if (opt('--out')) {
  const shaDir = opt('--sha-dir');
  const plugins = [];
  for (const e of entries()) {
    const shaFile = shaDir && path.join(shaDir, zipName(e.id, e.version) + '.sha256');
    if (!shaFile || !fs.existsSync(shaFile)) { console.error(`${e.id} ${e.version}: no release yet (no ${zipName(e.id, e.version)}.sha256), left out`); continue; }
    const sha256 = fs.readFileSync(shaFile, 'utf8').trim().split(/\s+/)[0].toLowerCase();
    if (!/^[0-9a-f]{64}$/.test(sha256)) { console.error(`${e.id}: ${shaFile} doesn't hold a SHA-256`); process.exit(1); }
    plugins.push({ ...e, zip: zipUrl(e.id, e.version), sha256 });
  }
  const catalog = { schema: 1, generated: new Date().toISOString(), repository: `https://github.com/${REPO}`, plugins };
  fs.writeFileSync(opt('--out'), JSON.stringify(catalog, null, 2) + '\n');
  console.log(`${opt('--out')}: ${plugins.length} plugin${plugins.length === 1 ? '' : 's'}.`);
}
