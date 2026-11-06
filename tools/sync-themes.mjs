#!/usr/bin/env node
// The themes LlamaTree comes with (src/LlamaTree/wwwroot/themes: Matrix, Nerds, Tron) are published in LlamaTree-Themes,
// which is where they're changed. This brings the app's copies into line with what that repository publishes: each
// theme as Get themes would install it (the catalog's theme, with its version).
//
//   node tools/sync-themes.mjs           write the app's copies from the catalog
//   node tools/sync-themes.mjs --check   fail if any differs from the catalog (the release pipeline runs this)
//
// LLAMATREE_THEME_CATALOG_URL reads another catalog (a file path or URL), as the app does. No dependencies.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const folder = path.join(root, 'src', 'LlamaTree', 'wwwroot', 'themes');
const source = process.env.LLAMATREE_THEME_CATALOG_URL || 'https://github.com/LlamachantTechnology/LlamaTree-Themes/releases/download/catalog/catalog.json';
const check = process.argv.includes('--check');

const text = /^https?:\/\//.test(source)
  ? await (async () => { const r = await fetch(source); if (!r.ok) throw new Error(`${source}: ${r.status}`); return r.text(); })()
  : fs.readFileSync(source, 'utf8');
const catalog = JSON.parse(text);
const offered = new Map((catalog.themes ?? []).map((t) => [t.id, t]));

// As GalleryService installs one: its "theme" with "version" after "name".
const asFile = (t) => {
  const { name, ...rest } = t.theme;
  return JSON.stringify({ name, version: t.version, ...rest }, null, 2) + '\n';
};
// The same theme, whatever order its keys are written in.
const canonical = (v) => Array.isArray(v) ? v.map(canonical)
  : v && typeof v === 'object' ? Object.fromEntries(Object.keys(v).sort().map((k) => [k, canonical(v[k])])) : v;
const same = (a, b) => JSON.stringify(canonical(JSON.parse(a))) === JSON.stringify(canonical(JSON.parse(b)));

const problems = [];
for (const file of fs.readdirSync(folder).filter((f) => f.endsWith('.json')).sort()) {
  const id = file.slice(0, -5), t = offered.get(id);
  if (!t) { problems.push(`${id}: LlamaTree-Themes doesn't publish it`); continue; }
  const want = asFile(t), have = fs.readFileSync(path.join(folder, file), 'utf8');
  if (same(want, have)) { console.log(`${id} ${t.version}: as published`); continue; }
  if (check) problems.push(`${id}: differs from LlamaTree-Themes' ${t.version}; run  node tools/sync-themes.mjs  and commit it`);
  else { fs.writeFileSync(path.join(folder, file), want); console.log(`${id} ${t.version}: written`); }
}
if (problems.length) {
  for (const p of problems) console.error(p);
  process.exit(1);
}
