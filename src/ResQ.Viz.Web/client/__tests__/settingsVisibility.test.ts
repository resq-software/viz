// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { setSettingsVisibleState } from '../ui/settingsVisibility';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

describe('settings visibility', () => {
  it('ships closed and inert before app startup', () => {
    const html = read('../index.html');
    const tag = html.match(/<div id="settings-panel"[^>]*>/)?.[0] ?? '';

    expect(tag).toContain('aria-hidden="true"');
    expect(tag).toMatch(/\sinert(?:\s|>)/);
  });

  it('evacuates focused settings controls before closing and restores interactivity on reopen', () => {
    document.body.innerHTML = `
      <button id="hud-settings-toggle" aria-expanded="false">Settings</button>
      <div id="settings-panel" aria-hidden="true" inert>
        <button id="settings-close">Close</button>
        <input id="setting-control">
      </div>
      <button id="outside">Outside</button>
    `;
    const panel = document.getElementById('settings-panel')!;
    const toggle = document.getElementById('hud-settings-toggle')!;
    const control = document.getElementById('setting-control') as HTMLInputElement;
    const close = document.getElementById('settings-close') as HTMLButtonElement;
    const outside = document.getElementById('outside') as HTMLButtonElement;
    toggle.addEventListener('click', () => {
      setSettingsVisibleState(panel, toggle, !panel.classList.contains('open'));
    });
    close.addEventListener('click', () => setSettingsVisibleState(panel, toggle, false));
    document.addEventListener('click', (event) => {
      if (!panel.classList.contains('open')) return;
      if (panel.contains(event.target as Node) || toggle.contains(event.target as Node)) return;
      setSettingsVisibleState(panel, toggle, false);
    });

    toggle.click();
    expect(panel.classList.contains('open')).toBe(true);
    expect(panel.hasAttribute('inert')).toBe(false);
    expect(panel.getAttribute('aria-hidden')).toBe('false');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    control.focus();
    close.click();
    expect(document.activeElement).toBe(toggle);
    expect(panel.classList.contains('open')).toBe(false);
    expect(panel.hasAttribute('inert')).toBe(true);
    expect(panel.getAttribute('aria-hidden')).toBe('true');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    toggle.click();
    expect(panel.hasAttribute('inert')).toBe(false);
    control.focus();
    outside.click();
    expect(document.activeElement).toBe(toggle);
    expect(panel.hasAttribute('inert')).toBe(true);

    toggle.click();
    control.focus();
    toggle.click();
    expect(document.activeElement).toBe(toggle);
    expect(panel.hasAttribute('inert')).toBe(true);

    toggle.click();
    outside.focus();
    setSettingsVisibleState(panel, toggle, false);
    expect(document.activeElement).toBe(outside);
  });

  it('routes toggle, close, and outside-click paths through the same state owner', () => {
    const app = read('../app.ts');

    expect(app).toContain("import { setSettingsVisibleState } from './ui/settingsVisibility'");
    expect(app).toMatch(/function _setSettingsVisible[\s\S]*?setSettingsVisibleState\(settingsPanel, settingsToggle, v\)/);
    expect(app).toMatch(/settingsToggle\?\.addEventListener\('click'[\s\S]*?_setSettingsVisible\(!settingsPanel\?\.classList\.contains\('open'\)\)/);
    expect(app).toMatch(/settingsClose\?\.addEventListener\('click'[\s\S]*?_setSettingsVisible\(false\)/);
    expect(app).toMatch(/document\.addEventListener\('click'[\s\S]*?_setSettingsVisible\(false\)/);
  });
});
