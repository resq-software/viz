// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { setHintsVisibleState } from '../ui/hintsVisibility';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

describe('keyboard hints visibility', () => {
  it('owns focus and accessibility state across button, outside, and Escape closes', () => {
    document.body.innerHTML = `
      <button id="hud-hints-toggle" aria-pressed="false">Hints</button>
      <div id="key-hints" class="hidden" hidden inert aria-hidden="true">
        <button id="key-hints-close">Close</button>
        <a id="hint-link" href="#help">Help</a>
      </div>
      <button id="outside">Outside</button>
    `;
    const panel = document.getElementById('key-hints')!;
    const toggle = document.getElementById('hud-hints-toggle')!;
    const close = document.getElementById('key-hints-close') as HTMLButtonElement;
    const link = document.getElementById('hint-link') as HTMLAnchorElement;
    const outside = document.getElementById('outside') as HTMLButtonElement;
    let visible = false;
    const setVisible = (next: boolean): void => {
      visible = next;
      setHintsVisibleState(panel, toggle, next);
    };
    toggle.addEventListener('click', () => setVisible(!visible));
    close.addEventListener('click', () => setVisible(false));
    document.addEventListener('click', (event) => {
      if (!visible || panel.contains(event.target as Node) || toggle.contains(event.target as Node)) return;
      setVisible(false);
    });
    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape' && visible) setVisible(false);
    });

    toggle.click();
    expect(panel.hidden).toBe(false);
    expect(panel.hasAttribute('inert')).toBe(false);
    expect(panel.getAttribute('aria-hidden')).toBe('false');
    expect(panel.classList.contains('hidden')).toBe(false);

    link.focus();
    close.click();
    expect(document.activeElement).toBe(toggle);
    expect(panel.hidden).toBe(true);
    expect(panel.hasAttribute('inert')).toBe(true);
    expect(panel.getAttribute('aria-hidden')).toBe('true');

    toggle.click();
    link.focus();
    outside.click();
    expect(document.activeElement).toBe(toggle);
    expect(panel.hidden).toBe(true);

    toggle.click();
    link.focus();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(document.activeElement).toBe(toggle);
    expect(panel.hidden).toBe(true);

    toggle.click();
    outside.focus();
    setVisible(false);
    expect(document.activeElement).toBe(outside);
  });

  it('routes every production dismissal through the shared state owner', () => {
    const app = read('../app.ts');
    const keyboard = app.slice(app.indexOf("window.addEventListener('keydown'"));

    expect(app).toContain("import { setHintsVisibleState } from './ui/hintsVisibility'");
    expect(app).toMatch(/function _setHintsVisible[\s\S]*?setHintsVisibleState\(keyHints, hintsToggle, v\)/);
    expect(app).toMatch(/hintsToggle\?\.addEventListener\('click'[\s\S]*?_setHintsVisible\(!hintsVisible\)/);
    expect(app).toMatch(/hintsClose\?\.addEventListener\('click'[\s\S]*?_setHintsVisible\(false\)/);
    expect(app).toMatch(/document\.addEventListener\('click'[\s\S]*?_setHintsVisible\(false\)/);
    expect(app).toMatch(/e\.key === 'Escape'[\s\S]*?_setHintsVisible\(false\)/);
    expect(keyboard.indexOf("e.key === 'Escape'")).toBeLessThan(
      keyboard.indexOf('shouldIgnoreGlobalShortcut(e)'),
    );
  });
});
