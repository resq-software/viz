// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Dismissal, tested on both halves of the mechanism.
//
// `AssetPanel` hides itself and its retry control with the `hidden` DOM
// property. That property only removes an element from the page because the UA
// stylesheet says `[hidden] { display: none }` — a rule with *no* specificity,
// which therefore loses to any author `display` on the same element. This file
// sets `display: flex` on `.asset-panel`, and `.btn` (which the retry control
// wears) sets `display: inline-flex`, so both hide paths were being overridden:
// with nothing selected the page carried an empty panel shell its own close
// button could not dismiss, offering a RETRY with no failed command behind it.
//
// The JS half is asserted as behaviour; the CSS half cannot be, because
// happy-dom's parser does not accept these stylesheets whole, so it is asserted
// against the rule text instead. Both halves are needed: either one alone
// passes while the panel stays on screen.

import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

import { AssetPanel } from '../assets/AssetPanel';

/** The stylesheet's own text. Read from disk rather than imported: vitest is
 *  configured not to process CSS, so `?raw` yields an empty string here, and an
 *  empty string would make every assertion below pass vacuously. The walk up
 *  keeps the test independent of which directory vitest was started in. */
function readAssetsCss(): string {
  for (let dir = process.cwd(); ; dir = dirname(dir)) {
    for (const rel of ['client/styles/assets.css', 'styles/assets.css']) {
      const candidate = resolve(dir, rel);
      if (existsSync(candidate)) return readFileSync(candidate, 'utf8');
    }
    if (dirname(dir) === dir) throw new Error('styles/assets.css not found');
  }
}

const assetsCss = readAssetsCss();

/** Rules in `assetsCss` as `[selectorList, body]`, comments removed. */
function rules(css: string): Array<readonly [string, string]> {
  const out: Array<readonly [string, string]> = [];
  const flat = css.replace(/\/\*[\s\S]*?\*\//g, '');
  for (const m of flat.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    out.push([(m[1] ?? '').trim(), (m[2] ?? '').trim()] as const);
  }
  return out;
}

/** Selectors of every rule that sets `display: none`. */
function displayNoneSelectors(css: string): string[] {
  return rules(css)
    .filter(([, body]) => /(^|;)\s*display\s*:\s*none\s*(;|$)/.test(body))
    .flatMap(([selectors]) => selectors.split(',').map((s) => s.trim()));
}

describe('the detail panel can actually be dismissed', () => {
  it('marks itself hidden when nothing is selected, and offers no retry', () => {
    const mount = document.createElement('div');
    document.body.appendChild(mount);
    const panel = new AssetPanel({ mount });

    const root = mount.querySelector('.asset-panel');
    const retry = mount.querySelector('.ap-cmd-retry');
    expect(root).not.toBeNull();
    expect(retry).not.toBeNull();

    // Nothing has been rendered, and nothing has failed.
    expect((root as HTMLElement).hidden).toBe(true);
    expect((retry as HTMLElement).hidden).toBe(true);

    // Dismissing an empty panel is still a dismissal, not a no-op.
    panel.render(null);
    expect((root as HTMLElement).hidden).toBe(true);
    expect(panel.subjectId).toBeNull();

    panel.dispose();
  });

  it('neutralises display for hidden elements in both widgets', () => {
    const guarded = displayNoneSelectors(assetsCss);

    // The widgets themselves, and anything they hide inside — the retry button
    // reaches `display: inline-flex` through `.btn`, which lives in another
    // file, so the descendant form is what actually covers it.
    for (const selector of [
      '.asset-panel[hidden]',
      '.asset-filter[hidden]',
      '.asset-roster[hidden]',
      '.asset-panel [hidden]',
      '.asset-filter [hidden]',
      '.asset-roster [hidden]',
    ]) {
      expect(guarded).toContain(selector);
    }
  });

  it('leaves no display rule in this file outranking hidden unguarded', () => {
    // Every element that carries a `display` from this stylesheet must be
    // covered by one of the guards above — i.e. it must sit inside one of the
    // two widgets. A new top-level `display` rule here would escape them.
    const topLevelWithDisplay = rules(assetsCss)
      .filter(([, body]) => /(^|;)\s*display\s*:\s*(?!none)/.test(body))
      .flatMap(([selectors]) => selectors.split(',').map((s) => s.trim()))
      .filter((s) => !s.startsWith('@') && !s.includes('%'))
      // Anything scoped to the panel or the filter is already covered.
      .filter((s) => !/(^|\s|\.)(asset-panel|asset-filter|asset-roster|ap-|af-|ar-)/.test(s));

    expect(topLevelWithDisplay).toEqual([]);
  });

  it('bounds the roster and gives its compact native controls full-size targets', () => {
    expect(assetsCss).toMatch(/\.ar-scroll\s*\{[\s\S]*?overflow-y:\s*auto/);
    expect(assetsCss).toMatch(
      /@media\s*\(max-width:\s*759px\)[\s\S]*?\.af-domain-tab[\s\S]*?\.ar-row[\s\S]*?\.ar-search[\s\S]*?min-height:\s*44px/,
    );
    expect(assetsCss).not.toMatch(/\.(?:asset-roster|ar-[^{\s,]+)[^{]*\{[^}]*z-index\s*:/);
  });
});
