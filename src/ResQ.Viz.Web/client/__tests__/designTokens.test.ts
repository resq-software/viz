// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * Drift guard against the ResQ design system.
 *
 * `@resq-systems/constants` is the canonical source of truth for the shared
 * tokens — its own `tokens.sync.test.ts` says so, and keeps `@resq-systems/ui`'s
 * `globals.css` honest against it. viz sits outside that chain: it VENDORS the
 * values into `styles/tokens.css` because the ui package's stylesheet is Tailwind
 * v4 (`@import "tailwindcss"`, `@theme inline`) and viz does not use Tailwind.
 *
 * Vendoring is therefore the right call, but it was unguarded, which is how
 * values drift silently. These are the canonical values as published in
 * `@resq-systems/constants/tokens.css` (checked against v0.5.0 in the monorepo).
 * If upstream changes, this test fails and the update becomes a deliberate act
 * rather than an accident.
 *
 * NOTE: `design/STYLE_GUIDE.md` is NOT the source of truth and is already stale —
 * its table lists `background` as oklch(16.04% 0.0152 272.20), which is actually
 * the LIGHT theme's `foreground`. Trust the constants package, not the markdown.
 */
const CANONICAL_COLORS: ReadonlyArray<readonly [string, string]> = [
  ['background', 'oklch(16.63% 0.0262 269.37)'],
  ['surface', 'oklch(19.72% 0.0231 268.80)'],
  ['border', 'oklch(26.45% 0.0386 270.81)'],
  ['foreground', 'oklch(96.19% 0.0109 274.89)'],
  ['muted-foreground', 'oklch(64.00% 0.0535 266.82)'],
  ['primary', 'oklch(58.50% 0.1877 24.72)'],
];

/** The canonical radius scale: --resq-radius-sm/md/lg/xl/full. */
const CANONICAL_RADII: ReadonlyArray<readonly [string, string]> = [
  ['radius-token', '3px'],
  ['radius-control', '4px'],
  ['radius-panel', '6px'],
  ['radius-panel-lg', '10px'],
];

function tokensCss(): string {
  return readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'styles', 'tokens.css'), 'utf8');
}

/** The declared value of one custom property in the :root block. */
function declared(css: string, name: string): string | undefined {
  const root = css.slice(css.indexOf(':root'));
  const m = new RegExp(`--${name}\\s*:\\s*([^;]+);`).exec(root);
  return m?.[1]?.trim().replace(/\s+/g, ' ');
}

/** oklch(L% C H) → numeric tuple, so formatting differences do not fail. */
function oklch(value: string): [number, number, number] | null {
  const m = /oklch\(\s*([\d.]+)%\s+([\d.]+)\s+([\d.]+)/.exec(value);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

describe('design tokens track the canonical ResQ source', () => {
  it.each(CANONICAL_COLORS)('--%s matches @resq-systems/constants', (name, expected) => {
    const actual = declared(tokensCss(), name);
    expect(actual, `--${name} is not declared in tokens.css`).toBeDefined();
    const got = oklch(actual as string);
    const want = oklch(expected);
    expect(got, `--${name} is not an oklch literal: ${actual}`).not.toBeNull();
    // Compared numerically: "64%" and "64.00%" are the same colour.
    expect(got).toEqual(want);
  });

  it.each(CANONICAL_RADII)('--%s matches the canonical radius scale', (name, expected) => {
    expect(declared(tokensCss(), name)).toBe(expected);
  });

  it('keeps the three brand font families in their canonical roles', () => {
    const css = tokensCss();
    expect(declared(css, 'font-display')).toMatch(/^"?Syne"?/);
    expect(declared(css, 'font-body')).toMatch(/^"DM Sans"/);
    expect(declared(css, 'font-mono')).toMatch(/^"DM Mono"/);
  });

  // The touch floor used to be 44px written into operator.css. It had to become a
  // token because it outgrew the bars it lives in: at 390x380 the transport
  // buttons hung 5px below the viewport floor and the HUD toggles 7px past the
  // HUD. It now tracks the height ladder — which is only safe while every tier
  // stays above WCAG 2.2 SC 2.5.8's 24px minimum, which is what this pins.
  it('keeps the control floor above the 24px target-size minimum at every tier', () => {
    const css = tokensCss();
    const main = readFileSync(
      join(dirname(fileURLToPath(import.meta.url)), '..', 'styles/main.css'), 'utf8');

    const base = /--control-min:\s*(\d+)px/.exec(css);
    expect(base, '--control-min is declared in tokens.css').not.toBeNull();
    expect(Number(base?.[1]), 'the default tier keeps the full 44px target').toBe(44);

    const tiers = [...main.matchAll(/--control-min:\s*(\d+)px/g)].map((m) => Number(m[1]));
    expect(tiers.length, 'the height ladder lowers the floor').toBeGreaterThan(0);
    for (const value of [Number(base?.[1]), ...tiers]) {
      expect(value, `control floor ${value}px clears SC 2.5.8`).toBeGreaterThanOrEqual(24);
    }
    // And it must actually shrink, or the token bought nothing.
    expect(Math.min(...tiers), 'short tiers lower it below the default').toBeLessThan(44);
  });

  it('documents every deliberate divergence from the canonical values', () => {
    const css = tokensCss();
    // viz raises three text tiers above the shared values because its panels are
    // translucent over a bright 3D scene, which the shared system never has to
    // survive. Each carries its measurement in a comment; this asserts the
    // divergence stays intentional rather than becoming folklore.
    for (const name of ['hint', 'primary-text', 'info-text']) {
      const value = declared(css, name);
      expect(value, `--${name} missing`).toBeDefined();
      const idx = css.indexOf(`--${name}:`);
      const preceding = css.slice(Math.max(0, idx - 700), idx);
      expect(
        /\*\//.test(preceding),
        `--${name} diverges from the shared palette and must carry a comment saying why`,
      ).toBe(true);
    }
  });
});
