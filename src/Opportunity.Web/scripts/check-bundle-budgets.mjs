// Bundle-size gate (E15-T04, ADR-018 §14): gzip size of the initial JavaScript and of every lazy chunk against
// perf-budgets.json, and proof from the esbuild metafile that feature code (viewer, admin) is lazy-loaded.
// Needs the production build with its metafile: `npx ng build --stats-json`, then `npm run budget:bundle`.
import { existsSync, readFileSync, appendFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const root = resolve(fileURLToPath(new URL('.', import.meta.url)), '..');
const dist = join(root, 'dist/opportunity-web');
const browser = join(dist, 'browser');
const statsFile = join(dist, 'browser-stats.json');
const { bundle: budget } = JSON.parse(readFileSync(join(root, 'perf-budgets.json'), 'utf8'));

if (!existsSync(statsFile)) {
  console.error(`::error::${statsFile} not found; build with \`npx ng build --stats-json\` first.`);
  process.exit(1);
}
const { outputs } = JSON.parse(readFileSync(statsFile, 'utf8'));

// Only emitted JavaScript counts (component stylesheets appear in the metafile but are inlined).
const js = Object.entries(outputs).filter(
  ([file]) => file.endsWith('.js') && existsSync(join(browser, file)),
);
const entry = js.find(([, o]) => o.entryPoint === 'src/main.ts');
if (!entry) {
  console.error('::error::No output with entry point src/main.ts in the metafile.');
  process.exit(1);
}

/** Initial = the entry plus everything it reaches through static imports; the rest is lazy. */
const initial = new Set();
const pending = [entry[0]];
while (pending.length) {
  const file = pending.pop();
  if (initial.has(file)) continue;
  initial.add(file);
  for (const imp of outputs[file]?.imports ?? []) {
    if (imp.kind === 'import-statement' && !imp.external) pending.push(imp.path);
  }
}

const gzipKb = (file) => gzipSync(readFileSync(join(browser, file))).length / 1024;
const fmt = (kb) => `${kb.toFixed(1)} kB`;
const failures = [];
const rows = [];

let initialKb = 0;
for (const file of initial) initialKb += gzipKb(file);
rows.push([
  'Initial JavaScript',
  [...initial].join(', '),
  fmt(initialKb),
  `${budget.initialJsGzipKb} kB`,
]);
if (initialKb > budget.initialJsGzipKb) {
  failures.push(
    `Initial JavaScript is ${fmt(initialKb)} gzip, budget ${budget.initialJsGzipKb} kB.`,
  );
}

for (const [file, output] of js) {
  if (initial.has(file)) continue;
  const kb = gzipKb(file);
  rows.push([
    'Lazy chunk',
    `${file} (${output.entryPoint ?? 'shared'})`,
    fmt(kb),
    `${budget.lazyChunkGzipKb} kB`,
  ]);
  if (kb > budget.lazyChunkGzipKb) {
    failures.push(`Lazy chunk ${file} is ${fmt(kb)} gzip, budget ${budget.lazyChunkGzipKb} kB.`);
  }
}

for (const file of initial) {
  for (const input of Object.keys(outputs[file].inputs)) {
    const rule = budget.mustBeLazy.find((prefix) => input.startsWith(prefix));
    if (rule)
      failures.push(
        `${input} is in the initial bundle (${file}); code under ${rule} must be lazy-loaded.`,
      );
  }
}

const table = [
  '| Bundle | Files | Gzip | Budget |',
  '|---|---|---|---|',
  ...rows.map((r) => `| ${r.join(' | ')} |`),
].join('\n');
console.log(table);
if (process.env.GITHUB_STEP_SUMMARY) {
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, `## Bundle budgets\n\n${table}\n\n`);
}
for (const f of failures) console.error(`::error::${f}`);
process.exit(failures.length ? 1 : 0);
