// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { beforeEach, describe, expect, it } from 'vitest';

import { shouldIgnoreGlobalShortcut } from '../ui/hotkeys';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

function keydown(
  target: Element,
  init: KeyboardEventInit = {},
): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { code: 'KeyR', cancelable: true, ...init });
  target.dispatchEvent(event);
  return event;
}

beforeEach(() => {
  document.body.innerHTML = `
    <button id="button"><span id="button-child">Reset</span></button>
    <input id="input">
    <select id="select"></select>
    <textarea id="textarea"></textarea>
    <div id="editable" contenteditable="true"><span id="editable-child">Text</span></div>
    <div id="plain"></div>
  `;
});

describe('shouldIgnoreGlobalShortcut', () => {
  it.each(['button', 'button-child', 'input', 'select', 'textarea', 'editable-child'])(
    'ignores keyboard events from interactive target %s',
    id => {
      const target = document.getElementById(id)!;
      const event = keydown(target);

      expect(shouldIgnoreGlobalShortcut(event)).toBe(true);
    },
  );

  it('ignores an event another owner already handled', () => {
    const event = new KeyboardEvent('keydown', { code: 'KeyR', cancelable: true });
    event.preventDefault();

    expect(shouldIgnoreGlobalShortcut(event)).toBe(true);
  });

  it.each([
    { ctrlKey: true },
    { metaKey: true },
    { altKey: true },
  ])('ignores reserved modifier chords by default', modifiers => {
    const event = keydown(document.getElementById('plain')!, modifiers);

    expect(shouldIgnoreGlobalShortcut(event)).toBe(true);
  });

  it('allows an ordinary body shortcut and a Shift-only shortcut', () => {
    const ordinary = keydown(document.body);
    const shifted = keydown(document.body, { shiftKey: true });

    expect(shouldIgnoreGlobalShortcut(ordinary)).toBe(false);
    expect(shouldIgnoreGlobalShortcut(shifted)).toBe(false);
  });

  it('allows Ctrl only for a caller that explicitly owns that modifier', () => {
    const chord = keydown(document.body, { ctrlKey: true, shiftKey: true });

    expect(shouldIgnoreGlobalShortcut(chord, { allowCtrl: true })).toBe(false);
    expect(shouldIgnoreGlobalShortcut(chord)).toBe(true);
  });

  it('is wired into controls, app dispatch, and camera key tracking', () => {
    const controls = read('../controls.ts');
    const app = read('../app.ts');
    const camera = read('../cameraControl.ts');

    expect(controls).toContain("import { shouldIgnoreGlobalShortcut } from './ui/hotkeys'");
    expect(controls).toMatch(/addEventListener\('keydown'[\s\S]*?shouldIgnoreGlobalShortcut\(e\)/);
    expect(app).toContain("import { shouldIgnoreGlobalShortcut } from './ui/hotkeys'");
    expect(app).toMatch(/Ctrl\+Shift\+R[\s\S]*?allowCtrl: true[\s\S]*?investorMode\.toggle/);
    expect(app).toMatch(/window\.addEventListener\('keydown'[\s\S]*?shouldIgnoreGlobalShortcut\(e\)[\s\S]*?Shift\+1\.\.8/);
    expect(camera).toContain("import { shouldIgnoreGlobalShortcut } from './ui/hotkeys'");
    expect(camera).toMatch(/addEventListener\('keydown'[\s\S]*?shouldIgnoreGlobalShortcut\(e\)/);
  });
});
