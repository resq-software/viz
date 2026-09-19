// SPDX-License-Identifier: Apache-2.0
//
// The console's chrome is a frame around the 3D scene: a HUD across the top, a
// rail down the left, a transport bar across the bottom. Each is `position:
// fixed` and each is inset to clear its neighbours — which is fine until two of
// them clear EACH OTHER. Then the corner where they meet belongs to neither and
// the raw WebGL canvas shows through a notch in the middle of the chrome.
//
// That shipped. At width >= 1100 `#sidebar` carried `bottom: var(--effective-dvr-h)`
// (operator.css) while `.resq-dvr` carried `left: var(--sidebar-w)`
// (operator-overlays.css) — two files, each correct in isolation, agreeing to
// leave a 320x46 hole at the bottom-left. `elementsFromPoint(8, height - 8)`
// returned CANVAS with chrome directly above it and to its right.
//
// Nothing caught it, because every existing test asked whether a rule SAID the
// right thing, and both rules did. The invariant is about the PAIR: two surfaces
// sharing a corner may not both inset away from it.

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/** Load order matters: the last matching declaration wins. */
const SHEETS = [
  'styles/tokens.css',
  'styles/main.css',
  'styles/operator.css',
  'styles/operator-overlays.css',
  'styles/assets.css',
  'ui/cockpit.css',
];

function allCss(): string {
  // Resolved with node:path rather than `new URL(rel, import.meta.url)` — see the
  // note in breakpointSystem.test.ts for why the URL form silently misresolves.
  const clientDir = join(dirname(fileURLToPath(import.meta.url)), '..');
  const parts: string[] = [];
  for (const name of SHEETS) parts.push(readFileSync(join(clientDir, name), 'utf8'));
  if (parts.every((p) => p.trim().length === 0)) {
    throw new Error('every stylesheet read empty — the check would pass vacuously');
  }
  return parts.join('\n');
}

/**
 * Does `prelude` match `width`?
 *
 * Deliberately strict: an unrecognised query THROWS rather than returning a
 * default. A media matcher that quietly answers "yes" to a query it could not
 * read turns this whole file green for the wrong reason.
 */
function mediaMatches(prelude: string, width: number): boolean {
  const q = prelude.replace(/^@media/, '').trim().replace(/\s+/g, ' ');
  if (q === '' || q === 'all' || q === 'screen') return true;
  // Ignore non-width features rather than failing on them.
  if (/prefers-|orientation|hover|pointer|forced-colors|resolution|display-mode/.test(q)) return true;
  if (/height/.test(q) && !/width/.test(q)) return true;

  // (A <= width < B)
  let m = /^\(\s*(\d+(?:\.\d+)?)px\s*<=\s*width\s*<\s*(\d+(?:\.\d+)?)px\s*\)$/.exec(q);
  if (m) return width >= Number(m[1]) && width < Number(m[2]);
  // (width < N) / (width <= N) / (width >= N) / (width > N)
  m = /^\(\s*width\s*(<=|>=|<|>)\s*(\d+(?:\.\d+)?)px\s*\)$/.exec(q);
  if (m) {
    const n = Number(m[2]);
    return m[1] === '<' ? width < n : m[1] === '<=' ? width <= n : m[1] === '>' ? width > n : width >= n;
  }
  // (max-width: N) / (min-width: N)
  m = /^\(\s*(max|min)-width:\s*(\d+(?:\.\d+)?)px\s*\)$/.exec(q);
  if (m) return m[1] === 'max' ? width <= Number(m[2]) : width >= Number(m[2]);

  throw new Error(`mediaMatches cannot read "${prelude}" — teach it rather than defaulting`);
}

/**
 * The winning value of `property` for a rule whose selector is EXACTLY
 * `selector`, at `width`. Exact-match on purpose: a state rule such as
 * `body:has(#sidebar.collapsed) .resq-dvr` describes a transient pose, not the
 * band's resting layout, and folding it in here would mask the resting bug.
 *
 * @returns The declared value, or undefined when no rule declares it.
 */
function winningValue(css: string, selector: string, property: string, width: number): string | undefined {
  const source = css.replace(/\/\*[\s\S]*?\*\//g, '');
  let value: string | undefined;

  const visit = (text: string): void => {
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
      if (prelude.startsWith('@media')) {
        if (mediaMatches(prelude, width)) visit(body);
      } else if (!prelude.startsWith('@')) {
        const selectors = prelude.split(',').map((s) => s.trim().replace(/\s+/g, ' '));
        if (selectors.includes(selector)) {
          for (const declaration of body.split(';')) {
            const split = declaration.indexOf(':');
            if (split < 0) continue;
            if (declaration.slice(0, split).trim() !== property) continue;
            value = declaration.slice(split + 1).trim();
          }
        }
      }
      cursor = close;
    }
  };

  visit(source);
  return value;
}

/** True when the inset actually holds the surface away from that edge. */
function insets(value: string | undefined): boolean {
  if (value === undefined) return false;
  return !/^0(px|rem|em|%)?$/.test(value.trim());
}

/** Ordinary widths, the two band edges, and the fractional points between them. */
const WIDTHS = [
  320, 390, 480, 560, 600, 759, 759.5, 760, 820, 900, 960, 1024, 1080,
  1099, 1099.5, 1100, 1120, 1200, 1280, 1366, 1440, 1512, 1600, 1920,
];

describe('chrome tiling', () => {
  // The rail and the transport bar share the bottom-left corner. Whichever of
  // them is inset away from it, the OTHER has to reach it.
  it('never lets the rail and the transport bar both clear the corner they share', () => {
    const css = allCss();
    const offenders: string[] = [];

    for (const width of WIDTHS) {
      const railBottom = winningValue(css, '#sidebar', 'bottom', width);
      const barLeft = winningValue(css, '.resq-dvr', 'left', width);
      if (insets(railBottom) && insets(barLeft)) {
        offenders.push(
          `${width}px: #sidebar{bottom:${railBottom}} and .resq-dvr{left:${barLeft}} `
          + '— both inset, so the bottom-left corner is bare scene',
        );
      }
    }

    expect(offenders, offenders.join('\n')).toEqual([]);
  });

  // Guards the check itself: if the parser stopped finding these declarations it
  // would report "no offenders" for the wrong reason, which is the failure mode
  // this whole file exists to prevent.
  it('actually resolves the declarations it is judging', () => {
    const css = allCss();
    // Wide: rail owns its column to the floor, bar starts after it.
    expect(winningValue(css, '#sidebar', 'bottom', 1440), '#sidebar bottom at 1440').toBe('0');
    expect(insets(winningValue(css, '.resq-dvr', 'left', 1440)), '.resq-dvr left at 1440').toBe(true);
    // Narrow: bar spans full width, rail is a drawer that must clear it.
    expect(winningValue(css, '.resq-dvr', 'left', 900), '.resq-dvr left at 900').toBe('0');
    expect(insets(winningValue(css, '#sidebar', 'bottom', 900)), '#sidebar bottom at 900').toBe(true);
  });

  it('refuses to judge a media query it cannot read', () => {
    expect(() => mediaMatches('@media (width: potato)', 800)).toThrow(/cannot read/);
  });
});

/**
 * The body of the first `@media` block whose prelude contains `needle`.
 *
 * Returns null when there is no such block, so a test can say "the block I am
 * judging has gone" rather than passing because it found nothing to object to.
 */
function bandBody(css: string, needle: string): string | null {
  const source = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const at = source.indexOf(needle);
  if (at < 0) return null;
  const open = source.indexOf('{', at);
  if (open < 0) return null;
  let depth = 1;
  let i = open + 1;
  while (i < source.length && depth > 0) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') depth--;
    i++;
  }
  return source.slice(open + 1, i - 1);
}

/**
 * Every body-level state that hides the rail, read out of the stylesheets.
 *
 * Returns the class names from rules like `body.fpv-mode #sidebar { display: none }`.
 */
function statesThatHideTheRail(css: string): string[] {
  const source = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const found = new Set<string>();
  const RULE = /([^{}]+)\{([^{}]*)\}/g;
  let m: RegExpExecArray | null;
  while ((m = RULE.exec(source)) !== null) {
    const prelude = m[1] ?? '';
    const body = m[2] ?? '';
    if (!/display:\s*none/.test(body)) continue;
    for (const selector of prelude.split(',')) {
      const trimmed = selector.trim().replace(/\s+/g, ' ');
      if (!/#sidebar\b/.test(trimmed)) continue;
      const state = /^body\.([A-Za-z0-9_-]+)\s+#sidebar\b/.exec(trimmed);
      if (state?.[1]) found.add(state[1]);
    }
  }
  return [...found].sort();
}

/** The selector list of the rule that returns the left column to the transport bar. */
function reclaimSelectors(css: string): string {
  const source = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const RULE = /([^{}]+)\{([^{}]*)\}/g;
  let m: RegExpExecArray | null;
  let out = '';
  while ((m = RULE.exec(source)) !== null) {
    const prelude = (m[1] ?? '').replace(/\s+/g, ' ').trim();
    const body = m[2] ?? '';
    if (!/\.resq-dvr/.test(prelude)) continue;
    if (!/left:\s*0/.test(body)) continue;
    if (/@media/.test(prelude)) continue;
    out = prelude;
  }
  return out;
}

// The rail and the bar agree to split the bottom-left corner: the bar insets by
// --sidebar-w and the rail covers what is left. So every way the rail can STOP
// holding that column has to hand it back, or the corner is bare WebGL canvas.
// Keyed on `.collapsed` alone it missed both immersive modes — measured at
// 1440x900 with body.fpv-mode and with body.investor-mode, #sidebar computed
// display:none while .resq-dvr stayed at left:320px and elementsFromPoint(8, 892)
// returned CANVAS. This enumerates the hiding rules so a fourth cannot be added
// without the bar being told.
describe('rail states and the transport bar', () => {
  function chromeCss(): string {
    const clientDir = join(dirname(fileURLToPath(import.meta.url)), '..');
    return ['styles/main.css', 'styles/operator.css', 'styles/operator-overlays.css']
      .map((n) => readFileSync(join(clientDir, n), 'utf8')).join('\n');
  }

  it('hands the column back for every body state that hides the rail', () => {
    const css = chromeCss();
    const states = statesThatHideTheRail(css);
    // If this finds nothing the test below would pass vacuously.
    expect(states.length, 'found body-level states that hide #sidebar').toBeGreaterThan(0);

    const reclaim = reclaimSelectors(css);
    expect(reclaim, 'a rule resets .resq-dvr left to 0').not.toBe('');

    const missing = states.filter((state) => !reclaim.includes(`body.${state}`));
    expect(missing, `these hide the rail but never return its column: ${missing.join(', ')}`)
      .toEqual([]);
  });

  it('still reclaims on the collapse path', () => {
    expect(reclaimSelectors(chromeCss())).toContain('#sidebar.collapsed');
  });
});

// Short viewports are where the right-hand column runs out of room, and two
// surfaces landed on top of the context sheet there. Both were measured on a
// fresh page at rest, not inferred:
//   .cam-mode-pill [1319,56,1424,83] over .asset-panel [1064,58,1424,466] — 105x25,
//     directly over the close button, because the short-height max-height released
//     the pill's 52px reservation while the pill itself stayed on screen.
//   #wind-compass  [1100,354,1200,466] entirely INSIDE that same panel — same
//     z-index (150), both pointer-events:auto, so paint order arbitrated.
describe('short-viewport context sheet', () => {
  function assetsCss(): string {
    const clientDir = join(dirname(fileURLToPath(import.meta.url)), '..');
    const css = readFileSync(join(clientDir, 'styles/assets.css'), 'utf8');
    if (css.trim().length === 0) throw new Error('assets.css read empty — the check would pass vacuously');
    return css;
  }

  it('keeps reserving the camera-mode pill while the pill is still on screen', () => {
    const band = bandBody(assetsCss(), '@media (height < 560px)');
    expect(band, 'the height < 560px band still exists in assets.css').not.toBeNull();

    const maxHeight = /\.asset-panel\s*\{[^}]*max-height:\s*([^;]+);/.exec(band ?? '');
    expect(maxHeight, '.asset-panel declares a max-height in that band').not.toBeNull();
    // The pill is not shed at this height, so its space has to stay reserved.
    expect(maxHeight?.[1], 'max-height reserves the camera-mode pill')
      .toMatch(/-\s*52px/);
  });

  it('retires the wind compass while the context sheet is open', () => {
    const band = bandBody(assetsCss(), '@media (height < 560px)');
    expect(band).not.toBeNull();
    expect(band ?? '', 'compass retired only while the sheet is open')
      .toMatch(/body:has\(\.asset-panel:not\(\[hidden\]\)\)\s*#wind-compass\s*\{[^}]*display:\s*none/);
  });

  // Guards the guard: a typo'd needle would make both tests above pass by
  // finding nothing, which is the failure mode this file was written against.
  it('is reading a band that really exists', () => {
    expect(bandBody(assetsCss(), '@media (height < 560px)')).toContain('.asset-panel');
    expect(bandBody(assetsCss(), '@media (height < 9999px)')).toBeNull();
  });
});

// A height-capped flex column has to say who shrinks, or the browser picks the
// answer nobody wants. `.asset-panel` has a max-height; `.ap-body` is the
// scroller; `.ap-foot` holds the commands. With neither child allowed to shrink
// below its content (`min-height: auto` is the flex default) the footer was
// pushed straight out of the panel, and `overflow: visible` let it paint there:
// measured at 1280x800, eight descendants outside the panel, "Set altitude" by
// 42px and the capability footer by 73px, over the transport bar.
//
// Clipping alone is not the fix either — that was tried, and it deleted six
// commands at 1440x620 instead of misplacing them. Both children must shrink AND
// scroll, so every control stays reachable at every height.
describe('context sheet containment', () => {
  function assetsCss(): string {
    const clientDir = join(dirname(fileURLToPath(import.meta.url)), '..');
    const css = readFileSync(join(clientDir, 'styles/assets.css'), 'utf8');
    if (css.trim().length === 0) throw new Error('assets.css read empty — the check would pass vacuously');
    return css;
  }

  /**
   * Every declaration block for `selector`, joined.
   *
   * All of them, not the last one: `.asset-panel` is re-declared inside several
   * media queries that adjust only its box, so taking the last match asked a
   * responsive override whether it clips — which it never mentions.
   */
  function ruleBody(css: string, selector: string): string | null {
    const source = css.replace(/\/\*[\s\S]*?\*\//g, '');
    const RULE = /([^{}]+)\{([^{}]*)\}/g;
    let m: RegExpExecArray | null;
    const bodies: string[] = [];
    while ((m = RULE.exec(source)) !== null) {
      const prelude = (m[1] ?? '').replace(/\s+/g, ' ').trim();
      if (prelude.split(',').map((x) => x.trim()).includes(selector)) bodies.push(m[2] ?? '');
    }
    return bodies.length > 0 ? bodies.join(';') : null;
  }

  it('clips the panel so nothing paints outside the surface', () => {
    const body = ruleBody(assetsCss(), '.asset-panel');
    expect(body, '.asset-panel rule exists').not.toBeNull();
    expect(body ?? '', '.asset-panel clips its children').toMatch(/overflow:\s*hidden/);
  });

  it('lets both panes shrink below their content', () => {
    for (const selector of ['.ap-body', '.ap-foot']) {
      const body = ruleBody(assetsCss(), selector);
      expect(body, `${selector} rule exists`).not.toBeNull();
      // Without this a flex item refuses to shrink and pushes its sibling out.
      expect(body ?? '', `${selector} sets min-height: 0`).toMatch(/min-height:\s*0/);
    }
  });

  it('gives the commands their own scroller rather than clipping them away', () => {
    const foot = ruleBody(assetsCss(), '.ap-foot');
    expect(foot ?? '', '.ap-foot scrolls').toMatch(/overflow-y:\s*auto/);
    // `flex: none` here is what clipped six commands at 1440x620.
    expect(foot ?? '', '.ap-foot must be allowed to shrink').not.toMatch(/flex:\s*none/);
  });
});

// The editor workspace is the one surface that can own the whole viewport, and
// three separate defects came from chrome not knowing that.
describe('editor workspace layering', () => {
  function css(name: string): string {
    const clientDir = join(dirname(fileURLToPath(import.meta.url)), '..');
    const text = readFileSync(join(clientDir, name), 'utf8');
    if (text.trim().length === 0) throw new Error(`${name} read empty — the check would pass vacuously`);
    return text;
  }

  /** Numeric value of a `--layer-*` token. */
  function layer(tokens: string, name: string): number {
    const m = new RegExp(`--layer-${name}:\\s*(\\d+)`).exec(tokens);
    if (!m) throw new Error(`--layer-${name} not found in tokens.css`);
    return Number(m[1]);
  }

  // The element persists in the DOM once created, so matching on data-layout
  // alone kept matching after it closed: measured at 1440x900, open the dock and
  // close it again and the event log never returned for the rest of the session.
  it('retires chrome only while the workspace is actually open', () => {
    // Comments stripped first: the block above this rule QUOTES the buggy
    // selector to explain it, and scanning raw text flagged the explanation.
    const main = css('styles/main.css').replace(/\/\*[\s\S]*?\*\//g, '');
    const retiring = main.split('\n').filter((line) => /body:has\(\.resq-editor/.test(line));
    expect(retiring.length, 'rules that retire chrome for the editor').toBeGreaterThan(0);
    for (const line of retiring) {
      expect(line, `"${line.trim()}" must require the workspace to be open`)
        .toMatch(/:not\(\[hidden\]\)/);
    }
  });

  // Fullscreen covers the viewport at --layer-editor while these sit at
  // --layer-hud, so each one paints over the workspace unless retired.
  it('retires every floating readout while the workspace is fullscreen', () => {
    const main = css('styles/main.css').replace(/\/\*[\s\S]*?\*\//g, '');
    const at = main.indexOf("data-layout='fullscreen'");
    expect(at, 'a fullscreen-scoped retire rule exists').toBeGreaterThan(-1);
    const block = main.slice(at, main.indexOf('}', at));
    for (const surface of ['.event-log', '.minimap', '#wind-compass', '.cam-mode-pill']) {
      expect(block, `${surface} retired in fullscreen`).toContain(surface);
    }
  });

  // At --layer-context the panel opened INSIDE the opaque workspace: laid out,
  // marked open, and never visible.
  it('opens Settings above the workspace, not underneath it', () => {
    const tokens = css('styles/tokens.css');
    const main = css('styles/main.css').replace(/\/\*[\s\S]*?\*\//g, '');
    const rule = /\.settings-panel\s*\{[^}]*\}/.exec(main);
    expect(rule, '.settings-panel rule exists').not.toBeNull();
    const declared = /z-index:\s*var\(--layer-([a-z]+)\)/.exec(rule?.[0] ?? '');
    expect(declared, '.settings-panel declares a layer token').not.toBeNull();
    expect(
      layer(tokens, declared?.[1] ?? 'context'),
      `.settings-panel is on --layer-${declared?.[1]}, which must outrank --layer-editor`,
    ).toBeGreaterThan(layer(tokens, 'editor'));
  });
});
