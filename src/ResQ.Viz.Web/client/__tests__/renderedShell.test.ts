// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { FleetUi } from '../assets/fleetUi';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

interface Rule {
  readonly selectors: readonly string[];
  readonly body: string;
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
        found.push({ selectors: prelude.split(',').map((part) => part.trim()), body });
      }
      cursor = close;
    }
  }

  visit(source);
  return found;
}

function mediaMatches(query: string, width: number): boolean {
  const min = /min-width:\s*(\d+)px/.exec(query)?.[1];
  const max = /max-width:\s*(\d+)px/.exec(query)?.[1];
  return (min === undefined || width >= Number(min)) && (max === undefined || width <= Number(max));
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

describe('rendered shell contracts', () => {
  it('preserves safe-area block padding after every matching responsive override', () => {
    const main = read('../styles/main.css');
    const editor = read('../styles/editor.css');

    for (const width of [390, 700, 900]) {
      expect(effectivePadding(main, '#hud-top', 'top', width), `HUD at ${width}px`)
        .toBe('env(safe-area-inset-top)');
      expect(effectivePadding(editor, '.resq-dvr', 'bottom', width), `DVR at ${width}px`)
        .toBe('env(safe-area-inset-bottom)');
    }
  });

  it('fits the compact DVR core controls and a flexible scrubber within 390px', () => {
    const editor = read('../styles/editor.css');
    const operator = read('../styles/operator.css');

    expect(effectiveProperty(editor, '.dvr-scrub', 'min-width', 390)).toBe('0');
    for (const width of [390, 700]) {
      for (const lowPriority of ['.dvr-rec', '.dvr-tostart', '.dvr-speed']) {
        expect(effectiveProperty(editor, lowPriority, 'display', width), `${lowPriority} at ${width}px`)
          .toBe('none');
      }
      for (const core of ['.dvr-play', '.dvr-step', '.dvr-reset', '.dvr-time', '.dvr-live']) {
        expect(effectiveProperty(editor, core, 'display', width), `${core} at ${width}px`)
          .not.toBe('none');
      }
    }

    expect(effectiveProperty(operator, '.resq-dvr button', 'min-width', 390)).toBe('44px');
    expect(effectiveProperty(operator, '.resq-dvr button', 'height', 390)).toBe('44px');

    // 8px inline padding + three 8px root gaps + three 44px transport buttons
    // with two 2px group gaps + 78px clock + 44px LIVE leaves 92px to scrub.
    const fixedWidth = 16 + (3 * 8) + (3 * 44) + (2 * 2) + 78 + 44;
    expect(390 - fixedWidth).toBeGreaterThanOrEqual(44);
  });

  it('retires intersecting HUD overlays while a responsive asset sheet is visible', () => {
    const main = read('../styles/main.css');
    const prefix = 'body:has(.asset-panel:not([hidden])) ';

    for (const surface of [
      '.event-log', '.minimap', '#wind-compass', '.sensor-stats-overlay',
      '.telemetry-strip', '.cockpit', '.resq-pip', '.cam-mode-pill',
    ]) {
      expect(effectiveProperty(main, `${prefix}${surface}`, 'display', 1000), surface).toBe('none');
      expect(effectiveProperty(main, `${prefix}${surface}`, 'display', 1200), surface).not.toBe('none');
    }
  });

  it('mounts the active asset panel in the context layer with compact target coverage', () => {
    document.body.innerHTML = '<div id="context"></div><div id="filter"></div>';
    const context = document.getElementById('context')!;
    const ui = new FleetUi({
      panelMount: context,
      filterMount: document.getElementById('filter')!,
      filterStorage: null,
    });
    const app = read('../app.ts');
    const operator = read('../styles/operator.css');

    expect(ui.panel.element.parentElement).toBe(context);
    expect(app).toMatch(/new m\.FleetUi\(\{[\s\S]*?panelMount:\s*operatorShell\.mounts\.context/);
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
