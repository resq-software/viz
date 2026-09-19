// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// Explicit list rather than a directory scan, so the set is deterministic and
// reviewable. A sheet added later must be added here — the dead-zone check
// below is only as complete as this list, which is why sheets() refuses to
// return an empty set rather than letting the check pass vacuously.
const SHEETS = [
  'styles/tokens.css',
  'styles/main.css',
  'styles/operator.css',
  'styles/operator-overlays.css',
  'styles/operator-dialogs.css',
  'styles/editor.css',
  'styles/assets.css',
  'styles/advancedSafety.css',
  'ui/cockpit.css',
];

function sheets(): { name: string; css: string }[] {
  // Resolve with node:path, NOT `new URL(rel, import.meta.url)`. Under the
  // happy-dom environment the global URL constructor is happy-dom's own, and its
  // two-argument form ignores a file: base — it resolves against the simulated
  // document instead, so that expression yields
  // "http://localhost:3000/styles/tokens.css" and readFileSync gets a path
  // ending in "undefined". fileURLToPath on the bare import.meta.url string is
  // unaffected because it never constructs a relative URL.
  const clientDir = join(dirname(fileURLToPath(import.meta.url)), '..');
  const out: { name: string; css: string }[] = [];
  const errors: string[] = [];
  for (const name of SHEETS) {
    try {
      out.push({ name, css: readFileSync(join(clientDir, name), 'utf8') });
    } catch (err) {
      errors.push(`${name}: ${(err as Error).message}`);
    }
  }
  if (out.length === 0) throw new Error(`no stylesheets readable — the check would vacuously pass:\n${errors.join('\n')}`);
  return out;
}

/** Every `@media` prelude in the file, comments stripped. */
function mediaPreludes(css: string): string[] {
  const source = css.replace(/\/\*[\s\S]*?\*\//g, '');
  return [...source.matchAll(/@media([^{]+)\{/g)].map((m) => (m[1] ?? '').trim());
}

describe('breakpoint system', () => {
  // A `max-width: N` rule and a `min-width: N + 1` rule look complementary but
  // leave the open interval (N, N+1) matching NEITHER. Fractional viewport
  // widths are ordinary — browser zoom and OS display scaling both produce them
  // — and at 1099.5px the old pairs dropped the sidebar's positioning, the
  // context layer's mode and the DVR's inset at once. Three such gaps existed:
  // (759,760), (1080,1081) and (1099,1100).
  it('has no complementary max-width / min-width pair that leaves a fractional dead zone', () => {
    const maxima = new Map<number, string[]>();
    const minima = new Map<number, string[]>();

    for (const { name, css } of sheets()) {
      for (const prelude of mediaPreludes(css)) {
        for (const m of prelude.matchAll(/max-width:\s*(\d+)px/g)) {
          const px = Number(m[1] ?? NaN);
          maxima.set(px, [...(maxima.get(px) ?? []), `${name}  @media${prelude}`]);
        }
        for (const m of prelude.matchAll(/min-width:\s*(\d+)px/g)) {
          const px = Number(m[1] ?? NaN);
          minima.set(px, [...(minima.get(px) ?? []), `${name}  @media${prelude}`]);
        }
      }
    }

    // Range-form lower bounds count as minima too: `max-width: 1080px` in one
    // sheet against `(width >= 1081px)` in another leaves exactly the same
    // (1080, 1081) hole as a legacy pair would. Converting only one side of a
    // pair therefore does not fix it, and that mixed case is easy to miss by
    // eye — cockpit.css and main.css were in precisely that state.
    for (const { name, css } of sheets()) {
      for (const prelude of mediaPreludes(css)) {
        for (const m of prelude.matchAll(/width\s*>=\s*(\d+)px/g)) {
          const px = Number(m[1] ?? NaN);
          minima.set(px, [...(minima.get(px) ?? []), `${name}  @media${prelude}`]);
        }
        for (const m of prelude.matchAll(/(\d+)px\s*<=\s*width/g)) {
          const px = Number(m[1] ?? NaN);
          minima.set(px, [...(minima.get(px) ?? []), `${name}  @media${prelude}`]);
        }
      }
    }

    const gaps: string[] = [];
    for (const [px, whereMax] of maxima) {
      const whereMin = minima.get(px + 1);
      if (!whereMin) continue;
      gaps.push(
        `dead zone (${px}, ${px + 1}) — nothing matches a width between them:\n` +
          whereMax.map((w) => `      max: ${w}`).join('\n') +
          '\n' +
          whereMin.map((w) => `      min: ${w}`).join('\n'),
      );
    }

    expect(gaps, `Use half-open range syntax instead, e.g. (width < ${'{N}'}px) and (width >= ${'{N}'}px).\n${gaps.join('\n')}`)
      .toEqual([]);
  });

  it('states every width threshold in range syntax', () => {
    const legacy: string[] = [];
    for (const { name, css } of sheets()) {
      for (const prelude of mediaPreludes(css)) {
        // Preference queries carry no width and are unaffected.
        if (!/width/.test(prelude)) continue;
        if (/(max-width|min-width)/.test(prelude)) legacy.push(`${name}  @media${prelude}`);
      }
    }
    // Thresholds with no complement are not dead-zone bugs, so they are allowed
    // to remain in the legacy form; this records which ones still do, so the
    // list can only shrink.
    expect(legacy.sort()).toMatchInlineSnapshot(`
      [
        "styles/editor.css  @media(max-width: 1280px)",
        "styles/main.css  @media(max-width: 1280px)",
        "styles/main.css  @media(max-width: 560px)",
        "styles/main.css  @media(max-width: 900px)",
        "styles/main.css  @media(max-width: 900px)",
        "styles/main.css  @media(max-width: 960px)",
        "styles/operator-overlays.css  @media(max-width: 560px)",
        "ui/cockpit.css  @media(max-width: 1280px)",
        "ui/cockpit.css  @media(max-width: 720px)",
      ]
    `);
  });
});
