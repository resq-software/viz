// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it, vi } from 'vitest';

import { FleetUi } from '../assets/fleetUi';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

interface Rule {
  readonly selectors: readonly string[];
  readonly body: string;
}

/**
 * Splits a selector prelude on its TOP-LEVEL commas only.
 *
 * `prelude.split(',')` shredded `a :is(.x, .y)` into `a :is(.x` and `.y)`, so a
 * rule written with `:is()`, `:where()` or a multi-argument `:not()` silently
 * matched nothing and every assertion against it passed vacuously — the same
 * "green because of the bug" shape these tests exist to catch.
 */
function splitSelectorList(prelude: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < prelude.length; i++) {
    const ch = prelude[i];
    if (ch === '(' || ch === '[') depth++;
    else if (ch === ')' || ch === ']') depth--;
    else if (ch === ',' && depth === 0) {
      parts.push(prelude.slice(start, i).trim().replace(/\s+/g, ' '));
      start = i + 1;
    }
  }
  parts.push(prelude.slice(start).trim().replace(/\s+/g, ' '));
  return parts.filter((part) => part.length > 0);
}

function rulesAtWidth(css: string, width: number): Rule[] {
  const source = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const found: Rule[] = [];

  function visit(text: string): void {
    let cursor = 0;
    while (cursor < text.length) {
      const open = text.indexOf('{', cursor);
      if (open < 0) return;
      const prelude = text.slice(cursor, open).trim();
      let depth = 1;
      let close = open + 1;
      while (close < text.length && depth > 0) {
        if (text[close] === '{') depth++;
        else if (text[close] === '}') depth--;
        close++;
      }
      const body = text.slice(open + 1, close - 1);
      if (prelude.startsWith('@media') && mediaMatches(prelude, width)) visit(body);
      else if (!prelude.startsWith('@')) {
        found.push({ selectors: splitSelectorList(prelude), body });
      }
      cursor = close;
    }
  }

  visit(source);
  return found;
}

function mediaMatches(query: string, width: number): boolean {
  const condition = query.replace(/^@media/, '').trim();
  // Preference queries (reduced-motion, forced-colors, prefers-contrast) place
  // no constraint on width, so they always apply for this helper's purposes.
  if (!/width/.test(condition)) return true;

  let matches = true;
  let understood = false;

  const min = /min-width:\s*(\d+)px/.exec(condition)?.[1];
  const max = /max-width:\s*(\d+)px/.exec(condition)?.[1];
  if (min !== undefined) { understood = true; matches &&= width >= Number(min); }
  if (max !== undefined) { understood = true; matches &&= width <= Number(max); }

  // Range syntax, which the stylesheets now use so that complementary
  // breakpoints partition the number line with no fractional gap between them:
  //   (width < 760px)   (width >= 1100px)   (760px <= width < 1100px)
  for (const m of condition.matchAll(/width\s*(<=|>=|<|>)\s*(\d+)px/g)) {
    understood = true;
    const bound = Number(m[2]);
    matches &&=
      m[1] === '<' ? width < bound
      : m[1] === '<=' ? width <= bound
      : m[1] === '>' ? width > bound
      : width >= bound;
  }
  for (const m of condition.matchAll(/(\d+)px\s*(<=|<)\s*width/g)) {
    understood = true;
    const bound = Number(m[1]);
    matches &&= m[2] === '<=' ? bound <= width : bound < width;
  }

  // Never default to "applies". Before range syntax existed this function
  // returned true for any query it could not parse, so a converted block would
  // have been treated as unconditional at EVERY width and the assertions built
  // on it would have been quietly meaningless rather than failing.
  if (!understood) throw new Error(`mediaMatches: unparsed width condition ${JSON.stringify(condition)}`);
  return matches;
}

function effectivePadding(
  css: string,
  selector: string,
  side: 'top' | 'bottom',
  width: number,
): string | undefined {
  let value: string | undefined;
  for (const rule of rulesAtWidth(css, width)) {
    if (!rule.selectors.includes(selector)) continue;
    for (const declaration of rule.body.split(';')) {
      const split = declaration.indexOf(':');
      if (split < 0) continue;
      const property = declaration.slice(0, split).trim();
      const next = declaration.slice(split + 1).trim();
      if (property === 'padding') {
        const values = next.split(/\s+/);
        value = side === 'top' ? values[0] : (values.length < 3 ? values[0] : values[2]);
      } else if (property === 'padding-block') {
        const values = next.split(/\s+/);
        value = side === 'top' ? values[0] : (values[1] ?? values[0]);
      } else if (property === `padding-${side}` || property === `padding-block-${side === 'top' ? 'start' : 'end'}`) {
        value = next;
      }
    }
  }
  return value;
}

function effectiveInlinePadding(
  css: string,
  selector: string,
  side: 'start' | 'end',
  width: number,
): string | undefined {
  let value: string | undefined;
  for (const rule of rulesAtWidth(css, width)) {
    if (!rule.selectors.includes(selector)) continue;
    for (const declaration of rule.body.split(';')) {
      const split = declaration.indexOf(':');
      if (split < 0) continue;
      const property = declaration.slice(0, split).trim();
      const next = declaration.slice(split + 1).trim();
      if (property === 'padding') {
        const values = next.split(/\s+/);
        value = side === 'start'
          ? (values.length < 2 ? values[0] : values[3] ?? values[1])
          : (values.length < 2 ? values[0] : values[1]);
      } else if (property === 'padding-inline') {
        const values = next.split(/\s+/);
        value = side === 'start' ? values[0] : (values[1] ?? values[0]);
      } else if (property === `padding-inline-${side}`) {
        value = next;
      }
    }
  }
  return value;
}

function effectiveProperty(
  css: string,
  selector: string,
  property: string,
  width: number,
): string | undefined {
  let value: string | undefined;
  for (const rule of rulesAtWidth(css, width)) {
    if (!rule.selectors.includes(selector)) continue;
    for (const declaration of rule.body.split(';')) {
      const split = declaration.indexOf(':');
      if (split < 0 || declaration.slice(0, split).trim() !== property) continue;
      value = declaration.slice(split + 1).trim();
    }
  }
  return value;
}

/**
 * Height the DVR scrubber actually renders at, under the real stylesheets.
 *
 * Loads main.css and operator-overlays.css together — the cross-file pair whose
 * specificity contest is the whole point — and builds the same element
 * `editor/dvr.ts` does: an `<input type="range" class="dvr-scrub">`.
 *
 * @returns The computed height, e.g. `"24px"`.
 */
function renderedScrubHeight(): string {
  const style = document.createElement('style');
  style.textContent = `${read('../styles/main.css')}\n${read('../styles/operator-overlays.css')}`;
  const input = document.createElement('input');
  input.type = 'range';
  input.className = 'dvr-scrub';

  document.head.appendChild(style);
  document.body.appendChild(input);
  try {
    return getComputedStyle(input).height;
  } finally {
    style.remove();
    input.remove();
  }
}

describe('rendered shell contracts', () => {
  it('keeps interactive roster churn out of live regions', () => {
    const page = new DOMParser().parseFromString(read('../index.html'), 'text/html');
    const roster = page.getElementById('fleet-roster');
    const telemetry = page.getElementById('a11y-telemetry');

    expect(roster?.hasAttribute('aria-live')).toBe(false);
    expect(roster?.getAttribute('role')).toBeNull();
    expect(telemetry?.getAttribute('aria-live')).toBe('polite');
    expect(telemetry?.getAttribute('aria-atomic')).toBe('true');
  });

  it('preserves safe-area block padding after every matching responsive override', () => {
    const main = read('../styles/main.css');
    // The timeline bar is an always-on operator overlay, not an authoring
    // surface: it moved out of the Editor's stylesheet when the Editor
    // became a lazily-loaded workspace, so its rules are read from there.
    const overlays = read('../styles/operator-overlays.css');

    for (const width of [390, 700, 900]) {
      expect(effectivePadding(main, '#hud-top', 'top', width), `HUD at ${width}px`)
        .toBe('env(safe-area-inset-top)');
      expect(effectivePadding(overlays, '.resq-dvr', 'bottom', width), `DVR at ${width}px`)
        .toBe('env(safe-area-inset-bottom)');
    }
  });

  it('keeps every remaining managed sheet inside the final safe-area cascade', () => {
    const main = read('../styles/main.css');
    // The timeline bar is an always-on operator overlay, not an authoring
    // surface: it moved out of the Editor's stylesheet when the Editor
    // became a lazily-loaded workspace, so its rules are read from there.
    const overlays = read('../styles/operator-overlays.css');
    const operator = read('../styles/operator.css');
    const assets = read('../styles/assets.css');

    expect(effectiveProperty(main, '.settings-panel', 'top', 1200)).toBe('var(--effective-hud-h)');
    const settingsHeight = effectiveProperty(main, '.settings-panel', 'max-height', 1200) ?? '';
    expect(settingsHeight).toContain('100dvh');
    expect(settingsHeight).toContain('var(--effective-hud-h)');
    expect(settingsHeight).toContain('var(--effective-dvr-h)');
    expect(effectiveProperty(main, '.settings-panel', 'padding-block-end', 1200))
      .toContain('env(safe-area-inset-bottom)');

    for (const width of [390, 900, 1200]) {
      expect(effectiveProperty(main, '#key-hints', 'top', width), `hints top at ${width}px`)
        .toBe('calc(var(--effective-hud-h) + 8px)');
      expect(effectiveProperty(main, '#key-hints', 'inset-inline-end', width), `hints end at ${width}px`)
        .toContain('env(safe-area-inset-right)');
    }
    for (const width of [390, 900]) {
      expect(effectiveProperty(main, '#key-hints', 'inset-inline-start', width), `hints start at ${width}px`)
        .toContain('env(safe-area-inset-left)');
      expect(effectiveProperty(overlays, '.resq-dvr', 'padding-inline-start', width), `DVR start at ${width}px`)
        .toContain('env(safe-area-inset-left)');
      expect(effectiveProperty(overlays, '.resq-dvr', 'padding-inline-end', width), `DVR end at ${width}px`)
        .toContain('env(safe-area-inset-right)');
      expect(effectiveProperty(operator, '.operator-context-layer', 'padding-inline-start', width))
        .toContain('env(safe-area-inset-left)');
      expect(effectiveProperty(operator, '.operator-context-layer', 'padding-inline-end', width))
        .toContain('env(safe-area-inset-right)');
      expect(effectiveProperty(assets, '.asset-panel', 'left', width))
        .toContain('env(safe-area-inset-left)');
      expect(effectiveProperty(assets, '.asset-panel', 'right', width))
        .toContain('env(safe-area-inset-right)');
    }
  });

  it('keeps medium and desktop chrome inside both inline safe edges', () => {
    const main = read('../styles/main.css');
    // The timeline bar is an always-on operator overlay, not an authoring
    // surface: it moved out of the Editor's stylesheet when the Editor
    // became a lazily-loaded workspace, so its rules are read from there.
    const overlays = read('../styles/operator-overlays.css');
    const assets = read('../styles/assets.css');

    for (const width of [900, 1200]) {
      expect(effectiveInlinePadding(main, '#hud-top', 'start', width), `HUD start at ${width}px`)
        .toContain('env(safe-area-inset-left)');
      expect(effectiveInlinePadding(main, '#hud-top', 'end', width), `HUD end at ${width}px`)
        .toContain('env(safe-area-inset-right)');
      expect(effectiveProperty(main, '.settings-panel', 'inset-inline-end', width)).toBe('0');
      const settingsWidth = effectiveProperty(main, '.settings-panel', 'width', width) ?? '';
      expect(settingsWidth).toContain('100vw');
      expect(settingsWidth).toContain('env(safe-area-inset-left)');
      expect(settingsWidth).toContain('env(safe-area-inset-right)');
      expect(effectiveInlinePadding(main, '.settings-panel', 'end', width))
        .toContain('env(safe-area-inset-right)');
      expect(effectiveProperty(main, '.settings-panel', 'box-sizing', width)).toBe('border-box');
      expect(effectiveInlinePadding(main, '#sidebar', 'start', width), `sidebar start at ${width}px`)
        .toContain('env(safe-area-inset-left)');
      expect(effectiveProperty(assets, '.asset-panel', 'right', width), `asset end at ${width}px`)
        .toContain('env(safe-area-inset-right)');
      expect(effectiveInlinePadding(overlays, '.resq-dvr', 'end', width), `DVR end at ${width}px`)
        .toContain('env(safe-area-inset-right)');
    }

    const desktopAssetHeight = effectiveProperty(assets, '.asset-panel', 'max-height', 1200) ?? '';
    expect(effectiveProperty(assets, '.asset-panel', 'bottom', 1200))
      .toContain('var(--effective-dvr-h)');
    expect(desktopAssetHeight).toContain('100dvh');
    expect(desktopAssetHeight).toContain('var(--effective-hud-h)');
    expect(desktopAssetHeight).toContain('var(--effective-dvr-h)');
  });

  it('never lets a responsive tier retire the emergency stop', () => {
    const main = read('../styles/main.css');
    const operator = read('../styles/operator.css');

    // VEHICLE_DASHBOARDS.md §7: "E-stop and alerts never degrade. That is the
    // rule that decides ties." Every other HUD element has a tier that sheds it
    // — battery at 1320, sim clock at 1120, FPS at 1020, the toggle group at 960
    // — so this asserts the one surface that must survive all of them, at every
    // width the console supports including the narrowest.
    for (const width of [2560, 1440, 1320, 1120, 1020, 960, 900, 760, 560, 400, 320]) {
      expect(
        effectiveProperty(main, '.hud-estop', 'display', width),
        `.hud-estop display at ${width}px`,
      ).not.toBe('none');
      expect(
        effectiveProperty(main, '#hud-top .hud-estop', 'display', width),
        `#hud-top .hud-estop display at ${width}px`,
      ).toBe('inline-flex');
      expect(
        effectiveProperty(operator, '.hud-estop', 'display', width),
        `.hud-estop must not be shed by operator.css at ${width}px`,
      ).not.toBe('none');
    }
  });

  it('truncates event-log messages on the child that can actually show an ellipsis', () => {
    const main = read('../styles/main.css');
    const W = 1440;

    // `.el-row` is `display: flex`. `text-overflow` only applies to a block
    // container clipping its OWN inline content, so declaring it on a flex
    // parent renders nothing — the row hard-cuts mid-word instead. Measured
    // before the fix: a row with scrollWidth 357 vs clientWidth 298 showed
    // "surface o" with no ellipsis at all, because the text lives in .el-msg
    // whose right edge sat past the row's clip.
    expect(effectiveProperty(main, '.el-row', 'display', W)).toBe('flex');
    expect(effectiveProperty(main, '.el-row', 'text-overflow', W)).toBeUndefined();

    // The truncation has to live on the text-bearing child, and `min-width: 0`
    // is what lets a flex item shrink below its content instead of overhanging.
    expect(effectiveProperty(main, '.el-msg', 'text-overflow', W)).toBe('ellipsis');
    expect(effectiveProperty(main, '.el-msg', 'overflow', W)).toBe('hidden');
    expect(effectiveProperty(main, '.el-msg', 'min-width', W)).toBe('0');

    // The fixed-width furniture must not be the part that gives.
    expect(effectiveProperty(main, '.el-time', 'flex', W)).toBe('0 0 auto');
    expect(effectiveProperty(main, '.el-tag', 'flex', W)).toBe('0 0 auto');
  });

  it('lets the HUD lane measure itself instead of guessing from the viewport', () => {
    const main = read('../styles/main.css');

    // The three sheddable stats used to key on viewport tiers (1320/1120/1020)
    // that had to be re-derived every time content changed, because viewport
    // width is only a proxy for the lane's width — the real quantity is
    // `viewport - left zone - right zone - gaps`, and the right zone grows when
    // an asset is selected. Measured at 1320px WITH a selection, the lane was
    // 476px against 537px of stats and #hud-comms rendered 16px of its 69.
    //
    // `effectiveProperty` models @media only, so the behaviour is proved by the
    // browser sweep. What is guarded here is the mechanism: the container exists,
    // its precondition holds, and the ranks are declared in order.
    expect(main).toMatch(/\.hud-zone-center\s*\{[^}]*container:\s*hudlane\s*\/\s*inline-size/);
    // Containment is only safe because the lane's size comes from the grid track,
    // never from its children — which is what these two declarations encode.
    expect(effectiveProperty(main, '.hud-zone-center', 'justify-self', 1440)).toBe('stretch');
    expect(effectiveProperty(main, '.hud-zone-center', 'overflow', 1440)).toBe('hidden');

    // Ranks, least-valuable first, at the measured content widths.
    const ranks = [...main.matchAll(/@container hudlane \(width < (\d+)px\)\s*\{\s*\[data-shed='(l\d)'\]/g)]
      .map((m) => ({ px: Number(m[1]), rank: m[2] }));
    expect(ranks.map((r) => r.rank)).toEqual(['l1', 'l2', 'l3']);
    // Strictly descending: a wider lane must never shed more than a narrow one.
    expect(ranks.map((r) => r.px)).toEqual([...ranks.map((r) => r.px)].sort((a, b) => b - a));

    // LINK is deliberately unranked — it is the comms state, mirrored nowhere
    // else in the chrome, and it is what the lane exists to protect.
    expect(main).not.toMatch(/data-shed=['"]l\d['"][^}]*#hud-comms/);
  });

  it('gives every shed rule the specificity to beat the base rule it fights', () => {
    const main = read('../styles/main.css');

    // Twice now a shed rule has been written above the base rule for the same
    // element and silently lost on source order at equal specificity: first
    // `.hud-stat-bat { display: none }` (battery meter visible 400px past its
    // tier), then `#hud-selected-drone` (measured — the lane was 184px against
    // 243px of stats at 960 because the chip it claims to retire was still
    // mounted). The `html ` prefix makes placement irrelevant.
    for (const id of ['#conn-label', '#hud-selected-drone']) {
      const bare = new RegExp(`(?<!html )\\${id}\\s*\\{[^}]*display:\\s*none`);
      expect(main, `${id} shed rule must be written as \`html ${id}\``).not.toMatch(bare);
      expect(main).toMatch(new RegExp(`html\\s+\\${id}\\s*\\{[^}]*display:\\s*none`));
    }
  });

  it('fits the compact DVR core controls and a flexible scrubber within 390px', () => {
    // The timeline bar is an always-on operator overlay, not an authoring
    // surface: it moved out of the Editor's stylesheet when the Editor
    // became a lazily-loaded workspace, so its rules are read from there.
    const overlays = read('../styles/operator-overlays.css');
    const operator = read('../styles/operator.css');

    const dvrSource = read('../editor/dvr.ts');
    const scrub = "input[type='range'].dvr-scrub";
    expect(effectiveProperty(overlays, scrub, 'min-width', 390)).toBe('0');

    // The height is asserted through the RENDERED cascade, not by matching the
    // declaration text.
    //
    // `effectiveProperty` compares selector strings exactly against one
    // stylesheet; it computes no specificity and does not see main.css at all.
    // The bug it was added to catch was precisely a cross-file specificity loss
    // — bare `.dvr-scrub` (0,1,0) losing to main.css's `input[type='range']`
    // (0,1,1), so the control hit-tested as a 4 px band while its own rule said
    // 18 px and called that grabbable. A helper that cannot see the competing
    // rule cannot detect that, so the guard could not fail for the reason it
    // existed. Build the real element under both stylesheets and read what the
    // cascade actually resolves.
    expect(renderedScrubHeight()).toBe('24px');
    for (const width of [390, 700]) {
      for (const lowPriority of ['.dvr-rec', '.dvr-tostart', '.dvr-speed']) {
        expect(effectiveProperty(overlays, lowPriority, 'display', width), `${lowPriority} at ${width}px`)
          .toBe('none');
      }
      for (const core of ['.dvr-play', '.dvr-step', '.dvr-reset', '.dvr-time', '.dvr-live']) {
        // Two parts, and the second one used to be the whole assertion.
        //
        // `effectiveProperty` returns undefined when no rule matches, and none of these five
        // selectors declares `display` anywhere — .dvr-step has no CSS rule at all. So
        // `.not.toBe('none')` was `expect(undefined).not.toBe('none')`, ten times. It does still
        // catch someone hiding a core control, which is the regression it was written for, but
        // it cannot tell "visible" from "this selector does not exist" — so renaming .dvr-play
        // in dvr.ts left it green while the hidden-controls loop above would have failed on the
        // same rename.
        //
        // Pinning existence against dvr.ts, where these classes are actually created, is what
        // closes that. CSS is the wrong place to look: .dvr-step is unstyled by design.
        // Matched inside a string literal rather than as an exact quoted token: .dvr-live
        // is assigned as 'dvr-live is-live', so requiring `'dvr-live'` verbatim fails on
        // correct code. Requiring it inside quotes keeps a passing mention in a comment
        // from standing in for the real thing.
        expect(dvrSource, `${core} is created in dvr.ts`)
          .toMatch(new RegExp(`['\"\`][^'\"\`]*\\b${core.slice(1)}\\b`));
        expect(effectiveProperty(overlays, core, 'display', width), `${core} at ${width}px`)
          .not.toBe('none');
      }
    }

    expect(effectiveProperty(operator, '.resq-dvr button', 'min-width', 390)).toBe('44px');
    // Height comes from --control-min, which the height ladder lowers with the bar
    // it sits in — pinned at 44px these buttons hung 5px below the viewport floor
    // on a 380px-tall screen. designTokens.test.ts pins the token's values.
    expect(effectiveProperty(operator, '.resq-dvr button', 'height', 390)).toBe('var(--control-min)');

    // 8px inline padding + three 8px root gaps + three 44px transport buttons
    // with two 2px group gaps + 78px clock + 44px LIVE leaves 92px to scrub.
    const fixedWidth = 16 + (3 * 8) + (3 * 44) + (2 * 2) + 78 + 44;
    expect(390 - fixedWidth).toBeGreaterThanOrEqual(44);
  });

  it('retires intersecting HUD overlays while a responsive asset sheet is visible', () => {
    const main = read('../styles/main.css');
    const SHEET = 'body:has(.asset-panel:not([hidden]))';

    /** The sheet-scoped retire rule in force at `width`, or null if there is none. */
    const retireList = (width: number): string | null => {
      let list: string | null = null;
      for (const rule of rulesAtWidth(main, width)) {
        for (const selector of rule.selectors) {
          if (!selector.startsWith(SHEET)) continue;
          if (!/display\s*:\s*none/.test(rule.body)) continue;
          list = selector;
        }
      }
      return list;
    };

    const inForce = retireList(1000);
    expect(inForce, 'a sheet-scoped retire rule applies at 1000px').not.toBeNull();
    expect(retireList(1200), 'and none applies at 1200px').toBeNull();

    // Membership of the single `:is()` list — one prefix, eight surfaces. Drop
    // any one of them from the stylesheet and this fails.
    for (const surface of [
      '.event-log', '.minimap', '#wind-compass', '.sensor-stats-overlay',
      '.telemetry-strip', '.cockpit', '.resq-pip', '.cam-mode-pill',
    ]) {
      expect(inForce, surface).toContain(surface);
    }
  });

  it('mounts the active asset panel in the context layer with compact target coverage', () => {
    document.body.innerHTML = '<div id="context"></div><div id="filter"></div><div id="roster"></div>';
    const context = document.getElementById('context')!;
    const ui = new FleetUi({
      panelMount: context,
      filterMount: document.getElementById('filter')!,
      rosterMount: document.getElementById('roster')!,
      selectAsset: vi.fn(),
      selectTrack: vi.fn(),
      onQueryChange: vi.fn(),
      filterStorage: null,
    });
    const app = read('../app.ts');
    const operator = read('../styles/operator.css');

    expect(ui.panel.element.parentElement).toBe(context);
    expect(ui.filter.element.parentElement?.id).toBe('filter');
    expect(ui.roster.element.parentElement?.id).toBe('roster');
    expect(app).toMatch(/new m\.FleetUi\(\{[\s\S]*?panelMount:\s*operatorShell\.mounts\.context/);
    expect(app).toMatch(/filterMount:\s*operatorShell\.mounts\.filter/);
    expect(app).toMatch(/rosterMount:\s*operatorShell\.mounts\.roster/);
    for (const selector of [
      '.operator-context-layer button',
      '.operator-context-layer select',
      '.operator-context-layer input',
    ]) expect(operator).toContain(selector);
    ui.dispose();
  });

  it('uses a safe-aware compact context height rather than a viewport percentage', () => {
    const assets = read('../styles/assets.css');
    const maxHeight = effectiveProperty(assets, '.asset-panel', 'max-height', 390) ?? '';

    expect(maxHeight).toContain('100dvh');
    expect(maxHeight).toContain('var(--effective-hud-h)');
    expect(maxHeight).toContain('var(--effective-dvr-h)');
    expect(maxHeight).not.toContain('55vh');
  });

  it('ships no dead drone-panel controls and no closed-hints tab stops', () => {
    const html = read('../index.html');
    const main = read('../styles/main.css');
    const page = new DOMParser().parseFromString(html, 'text/html');
    const hints = page.getElementById('key-hints');

    expect(page.getElementById('drone-panel')).toBeNull();
    expect(main).not.toMatch(/#(?:drone-panel|dp-)/);
    expect(hints?.hidden).toBe(true);
    expect(hints?.hasAttribute('inert')).toBe(true);
    expect(hints?.getAttribute('aria-hidden')).toBe('true');
    expect(main).toMatch(/#key-hints\[hidden\][\s\S]*?display:\s*none/);
    expect(page.querySelectorAll(
      '#key-hints:not([hidden]) button, #key-hints:not([hidden]) input, #drone-panel button, #drone-panel input',
    )).toHaveLength(0);
  });
});
