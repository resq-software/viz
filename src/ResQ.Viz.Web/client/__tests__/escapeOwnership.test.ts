// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it, vi } from 'vitest';

import { handleOwnedEscape } from '../ui/escapeOwnership';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

function escape(init: KeyboardEventInit = {}): KeyboardEvent {
  return new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', cancelable: true, ...init });
}

describe('early Escape ownership', () => {
  it('ignores prevented and Ctrl/Meta/Alt modified Escape events without mutation', () => {
    const cancelTarget = vi.fn();
    const closeHints = vi.fn();
    const closePanel = vi.fn();
    const prevented = escape();
    prevented.preventDefault();

    for (const event of [prevented, escape({ ctrlKey: true }), escape({ metaKey: true }), escape({ altKey: true })]) {
      expect(handleOwnedEscape(event, true, true, true, cancelTarget, closeHints, closePanel)).toBe(false);
    }
    expect(cancelTarget).not.toHaveBeenCalled();
    expect(closeHints).not.toHaveBeenCalled();
    expect(closePanel).not.toHaveBeenCalled();
  });

  it('prevents plain Escape and dismisses exactly the highest-priority owned surface', () => {
    const cancelTarget = vi.fn();
    const closeHints = vi.fn();
    const closePanel = vi.fn();
    const targetEscape = escape();

    expect(handleOwnedEscape(
      targetEscape, true, true, true, cancelTarget, closeHints, closePanel,
    )).toBe(true);
    expect(targetEscape.defaultPrevented).toBe(true);
    expect(cancelTarget).toHaveBeenCalledTimes(1);
    expect(closeHints).not.toHaveBeenCalled();
    expect(closePanel).not.toHaveBeenCalled();

    const hintsEscape = escape();
    expect(handleOwnedEscape(
      hintsEscape, false, true, true, cancelTarget, closeHints, closePanel,
    )).toBe(true);
    expect(hintsEscape.defaultPrevented).toBe(true);
    expect(closeHints).toHaveBeenCalledTimes(1);
    expect(closePanel).not.toHaveBeenCalled();

    const panelEscape = escape();
    expect(handleOwnedEscape(
      panelEscape, false, false, true, cancelTarget, closeHints, closePanel,
    )).toBe(true);
    expect(panelEscape.defaultPrevented).toBe(true);
    expect(closePanel).toHaveBeenCalledTimes(1);
  });

  it('cancels targeting first without clearing selection, then closes the panel', () => {
    let targeting = true;
    let selected = true;
    const closeHints = vi.fn();
    const cancelTarget = vi.fn(() => { targeting = false; });
    const closePanel = vi.fn(() => { selected = false; });

    expect(handleOwnedEscape(
      escape(), targeting, false, selected, cancelTarget, closeHints, closePanel,
    )).toBe(true);
    expect(targeting).toBe(false);
    expect(selected).toBe(true);
    expect(closePanel).not.toHaveBeenCalled();

    expect(handleOwnedEscape(
      escape(), targeting, false, selected, cancelTarget, closeHints, closePanel,
    )).toBe(true);
    expect(selected).toBe(false);
    expect(closePanel).toHaveBeenCalledTimes(1);
  });

  it('closes from a native control when unclaimed, but leaves ownerless Escape alone', () => {
    document.body.innerHTML = '<button id="native">Close</button>';
    const button = document.getElementById('native') as HTMLButtonElement;
    const closeHints = vi.fn();
    button.addEventListener('keydown', (event) => {
      handleOwnedEscape(event, false, true, false, vi.fn(), closeHints, vi.fn());
    });
    button.focus();
    const fromButton = escape({ bubbles: true });
    button.dispatchEvent(fromButton);
    expect(fromButton.defaultPrevented).toBe(true);
    expect(closeHints).toHaveBeenCalledTimes(1);

    const ownerless = escape();
    expect(handleOwnedEscape(
      ownerless, false, false, false, vi.fn(), vi.fn(), vi.fn(),
    )).toBe(false);
    expect(ownerless.defaultPrevented).toBe(false);
  });

  it('runs before the shared global-shortcut guard in app', () => {
    const app = read('../app.ts');
    const keyboard = app.slice(app.indexOf("window.addEventListener('keydown'"));

    expect(app).toContain("import { handleOwnedEscape } from './ui/escapeOwnership'");
    expect(keyboard.indexOf('handleOwnedEscape(')).toBeGreaterThanOrEqual(0);
    expect(keyboard.indexOf('handleOwnedEscape(')).toBeLessThan(
      keyboard.indexOf('shouldIgnoreGlobalShortcut(e)'),
    );
    expect(keyboard).toMatch(/handleOwnedEscape\([\s\S]*?fleetUi\?\.subjectId[\s\S]*?_deselectAll/);
    expect(read('../assets/AssetPanel.ts')).not.toMatch(
      /addEventListener\(['"]keydown['"][\s\S]{0,160}?Escape/,
    );
  });
});
