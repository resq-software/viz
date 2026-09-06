/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

// Measures the built client and enforces the entry-bundle byte ceilings.
//
// This replaces an inline `stat -c%s wwwroot/assets/index-*.js` block that
// lived only in .github/workflows/ci.yml, where it could not be run before
// pushing. The selection rule is deliberately identical to that shell glob —
// name starts with `index-`, ends with `.js` or `.css` — so the numbers this
// prints are the numbers CI used to print, and a local run answers the same
// question a CI run does.
//
// Only the two entry files are gated. Vite emits every lazily-imported chunk
// beside them, and those are listed as context: a chunk moving out of the
// entry and into a lazy import is a real improvement, and a report that only
// showed the entry would make it look like bytes had vanished.
//
// stdout is the Markdown report, so CI can pipe it straight into
// $GITHUB_STEP_SUMMARY. Diagnostics and ::error::/::notice:: annotations go to
// stderr, which keeps workflow-command syntax out of the rendered summary.

import { readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

/** Default ceiling for the entry JS chunk, in bytes (800 KB). */
const DEFAULT_JS_BUDGET_BYTES = 819_200;

/** Default ceiling for the entry CSS chunk, in bytes (52 KB). */
const DEFAULT_CSS_BUDGET_BYTES = 53_248;

const BYTES_PER_KB = 1024;

/** Built asset directory, resolved from this file so the cwd does not matter. */
const assetsDir = fileURLToPath(new URL('../wwwroot/assets/', import.meta.url));

/**
 * Reads a byte budget from the environment.
 *
 * An unset variable takes the default. A set-but-unparseable one is an error
 * rather than a silent fallback: `BUNDLE_JS_BUDGET_BYTES=800KB` meaning
 * "800 KB" would otherwise quietly restore the default and report a pass.
 *
 * @param {string} name Environment variable name.
 * @param {number} fallback Budget to use when the variable is unset.
 * @returns {number} Budget in bytes.
 */
function budgetFromEnvironment(name, fallback) {
  const raw = process.env[name];
  if (raw === undefined || raw === '') {
    return fallback;
  }
  if (!/^[0-9]+$/.test(raw)) {
    fail(`${name}="${raw}" is not a whole number of bytes`);
  }
  const parsed = Number(raw);
  if (!Number.isSafeInteger(parsed) || parsed < 1) {
    fail(`${name}="${raw}" is not a positive byte count`);
  }
  return parsed;
}

/**
 * Prints a fatal diagnostic and exits nonzero.
 *
 * @param {string} message Reason the check cannot produce a verdict.
 * @returns {never}
 */
function fail(message) {
  process.stderr.write(`::error::bundle check: ${message}\n`);
  process.exit(1);
}

/**
 * @param {number} value
 * @returns {string} Digit-grouped decimal, e.g. `576,486`.
 */
function grouped(value) {
  return value.toLocaleString('en-US');
}

/**
 * Whole kibibytes, truncated.
 *
 * Truncating rather than rounding matches the shell `$((bytes / 1024))` this
 * replaced, so the KB column did not shift when the check moved.
 *
 * @param {number} bytes
 * @returns {number}
 */
function kilobytes(bytes) {
  return Math.floor(bytes / BYTES_PER_KB);
}

/**
 * Percentage of a budget consumed, truncated — same reason as {@link kilobytes}.
 *
 * @param {number} bytes
 * @param {number} budget
 * @returns {number}
 */
function percentOf(bytes, budget) {
  return Math.floor((bytes * 100) / budget);
}

/**
 * Finds the single entry chunk for an extension.
 *
 * Vite hashes the entry as `index-<hash>.js`; nothing else in this build is
 * named `index-`. Two matches means a stale chunk survived `emptyOutDir`, or a
 * second entry appeared — either way the previous `stat -c%s index-*.js` would
 * have expanded to two paths and produced a shell error nobody could read, so
 * ambiguity is reported here rather than guessed at.
 *
 * @param {readonly {name: string, bytes: number}[]} files
 * @param {string} extension `.js` or `.css`.
 * @returns {{name: string, bytes: number}}
 */
function requireSingleEntry(files, extension) {
  const matches = files.filter(
    (file) => file.name.startsWith('index-') && file.name.endsWith(extension),
  );
  if (matches.length === 0) {
    fail(
      `no wwwroot/assets/index-*${extension} entry chunk. `
      + 'Run `npm run build` before `npm run bundle:check`.',
    );
  }
  if (matches.length > 1) {
    fail(
      `${matches.length} files match wwwroot/assets/index-*${extension} `
      + `(${matches.map((file) => file.name).join(', ')}); `
      + 'the entry chunk is ambiguous.',
    );
  }
  return /** @type {{name: string, bytes: number}} */ (matches[0]);
}

let entries;
try {
  entries = readdirSync(assetsDir, { withFileTypes: true });
} catch (error) {
  fail(
    `cannot read ${assetsDir} (${/** @type {Error} */ (error).message}). `
    + 'Run `npm run build` first.',
  );
}

const measured = entries
  .filter((entry) => entry.isFile())
  .filter((entry) => entry.name.endsWith('.js') || entry.name.endsWith('.css'))
  .map((entry) => ({ name: entry.name, bytes: statSync(join(assetsDir, entry.name)).size }))
  .sort((a, b) => b.bytes - a.bytes || a.name.localeCompare(b.name));

const jsBudget = budgetFromEnvironment('BUNDLE_JS_BUDGET_BYTES', DEFAULT_JS_BUDGET_BYTES);
const cssBudget = budgetFromEnvironment('BUNDLE_CSS_BUDGET_BYTES', DEFAULT_CSS_BUDGET_BYTES);

const jsEntry = requireSingleEntry(measured, '.js');
const cssEntry = requireSingleEntry(measured, '.css');

const gated = [
  { file: jsEntry, kind: 'JS', budget: jsBudget },
  { file: cssEntry, kind: 'CSS', budget: cssBudget },
];

const over = gated.filter(({ file, budget }) => file.bytes > budget);

const report = [];
report.push('## Bundle size');
report.push('');
report.push(
  'Entry chunks are gated. Lazy chunks are listed as context: they load on '
  + 'demand and count against no ceiling, and code moving from the entry into '
  + 'one of them is a real improvement rather than bytes going missing.',
);
report.push('');
report.push('| Asset | Kind | Bytes | KB | Budget KB | Used |');
report.push('|---|---|---:|---:|---:|---:|');

for (const file of measured) {
  const gate = gated.find((candidate) => candidate.file.name === file.name);
  const isCss = file.name.endsWith('.css');
  const kind = `${isCss ? 'CSS' : 'JS'} ${gate === undefined ? 'lazy' : 'entry'}`;
  const budgetCell = gate === undefined ? '—' : grouped(kilobytes(gate.budget));
  const usedCell = gate === undefined
    ? '—'
    : `${percentOf(file.bytes, gate.budget)}%${file.bytes > gate.budget ? ' **OVER**' : ''}`;
  report.push(
    `| ${file.name} | ${kind} | ${grouped(file.bytes)} | ${grouped(kilobytes(file.bytes))} `
    + `| ${budgetCell} | ${usedCell} |`,
  );
}

/**
 * @param {string} extension `.js` or `.css`.
 * @returns {number} Summed bytes of every emitted file with that extension.
 */
function totalFor(extension) {
  return measured
    .filter((file) => file.name.endsWith(extension))
    .reduce((sum, file) => sum + file.bytes, 0);
}
const jsTotal = totalFor('.js');
const cssTotal = totalFor('.css');
const jsCount = measured.filter((file) => file.name.endsWith('.js')).length;
const cssCount = measured.length - jsCount;

report.push('');
report.push(
  `Totals across all emitted chunks, **not gated**: `
  + `${jsCount} JS files ${grouped(jsTotal)} bytes (${grouped(kilobytes(jsTotal))} KB) · `
  + `${cssCount} CSS files ${grouped(cssTotal)} bytes (${grouped(kilobytes(cssTotal))} KB).`,
);

if (over.length > 0) {
  report.push('');
  for (const { file, kind, budget } of over) {
    report.push(
      `**FAIL** — ${kind} entry \`${file.name}\` is ${grouped(file.bytes)} bytes, `
      + `over the ${grouped(budget)}-byte budget by ${grouped(file.bytes - budget)}.`,
    );
  }
}

report.push('');
process.stdout.write(`${report.join('\n')}\n`);

for (const { file, kind, budget } of over) {
  process.stderr.write(
    `::error::${kind} bundle ${file.bytes} bytes exceeds ${budget} budget `
    + `(${file.name})\n`,
  );
}

if (over.length > 0) {
  process.exit(1);
}

process.stderr.write(
  `::notice::JS ${percentOf(jsEntry.bytes, jsBudget)}% of budget · `
  + `CSS ${percentOf(cssEntry.bytes, cssBudget)}% of budget\n`,
);
