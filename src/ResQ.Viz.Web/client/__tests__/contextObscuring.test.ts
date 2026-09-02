// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { setContextObscured } from '../ui/contextObscuring';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

describe('settings context obscuring', () => {
  it('evacuates focus and remains inert through repeated panel rendering', () => {
    document.body.innerHTML = `
      <aside class="asset-panel"><button id="asset-action">Command</button></aside>
      <button id="settings-close">Close settings</button>
    `;
    const panel = document.querySelector<HTMLElement>('.asset-panel')!;
    const close = document.getElementById('settings-close') as HTMLButtonElement;
    (document.getElementById('asset-action') as HTMLButtonElement).focus();

    setContextObscured(panel, true, close);

    expect(document.activeElement).toBe(close);
    expect(panel.hasAttribute('inert')).toBe(true);
    expect(panel.getAttribute('aria-hidden')).toBe('true');
    expect(panel.style.pointerEvents).toBe('none');

    // A 10 Hz AssetPanel.render() makes the panel visible again, but must not
    // restore interaction while Settings still owns the context layer.
    panel.hidden = false;
    expect(panel.hasAttribute('inert')).toBe(true);
    expect(panel.getAttribute('aria-hidden')).toBe('true');
    expect(panel.style.pointerEvents).toBe('none');

    setContextObscured(panel, false, close);
    expect(panel.hasAttribute('inert')).toBe(false);
    expect(panel.getAttribute('aria-hidden')).toBe('false');
    expect(panel.style.pointerEvents).toBe('');

    panel.hidden = true;
    setContextObscured(panel, true, close);
    setContextObscured(panel, false, close);
    expect(panel.getAttribute('aria-hidden')).toBe('true');
  });

  it('is wired into Settings and hidden by the Settings-open CSS rule', () => {
    const app = read('../app.ts');
    const css = read('../styles/main.css');

    expect(app).toContain("import { setContextObscured } from './ui/contextObscuring'");
    expect(app).toMatch(/function _setSettingsVisible[\s\S]*?setContextObscured\([\s\S]*?\.asset-panel/);
    expect(css).toMatch(/body:has\(#settings-panel\.open\)[\s\S]*?\.asset-panel[\s\S]*?display:\s*none\s*!important/);
  });
});
