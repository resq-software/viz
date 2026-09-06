// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';

import { EditorDock } from '../editor/dock';
import { SelectionStore } from '../editor/selection';
import { OnboardPip } from '../sensors/onboardPip';
import { SensorStatsOverlay } from '../sensorStatsOverlay';
import { GLOBAL_SHORTCUTS } from '../ui/globalShortcuts';

function press(target: Element, code: string, init: KeyboardEventInit = {}): KeyboardEvent {
  const event = new KeyboardEvent('keydown', {
    code, bubbles: true, cancelable: true, ...init,
  });
  target.dispatchEvent(event);
  return event;
}

describe('standalone global shortcut owners', () => {
  it('protects native controls and gives each body shortcut one owner', () => {
    localStorage.clear();
    document.body.innerHTML = `
      <button id="button"><span id="button-child">Button</span></button>
      <textarea id="textarea"></textarea>
      <div contenteditable="true"><span id="editable-child">Text</span></div>
      <details><summary id="summary"><span id="summary-child">Details</span></summary></details>
      <a id="link" href="#target"><span id="link-child">Link</span></a>
    `;
    const store = new SelectionStore();
    store.set('drone', 'd1');
    new OnboardPip({
      scene: new THREE.Scene(),
      renderer: { domElement: document.createElement('canvas') } as unknown as THREE.WebGLRenderer,
      store,
      getSelectedGroup: () => new THREE.Object3D(),
      getSelectedId: () => 'd1',
      addPostRender: vi.fn(),
    });
    new EditorDock();
    new SensorStatsOverlay();
    const pip = document.querySelector<HTMLElement>('.resq-pip')!;
    const pipLabel = pip.querySelector<HTMLElement>('.pip-label')!;
    const stats = document.querySelector<HTMLElement>('.sensor-stats-overlay')!;
    expect(pip.hidden).toBe(false);
    expect(pipLabel.textContent).toContain('FPV');
    expect(stats.hidden).toBe(true);
    expect(document.body.classList.contains('editor-collapsed')).toBe(false);

    const ownerCodes = [
      GLOBAL_SHORTCUTS.onboardPip,
      GLOBAL_SHORTCUTS.onboardPipMode,
      GLOBAL_SHORTCUTS.editorDock,
      GLOBAL_SHORTCUTS.sensorStats,
    ];
    for (const id of [
      'button', 'button-child', 'textarea', 'editable-child',
      'summary', 'summary-child', 'link', 'link-child',
    ]) {
      for (const code of ownerCodes) {
        expect(press(document.getElementById(id)!, code).defaultPrevented, `${id}:${code}`)
          .toBe(false);
      }
    }
    for (const modifiers of [{ ctrlKey: true }, { metaKey: true }, { altKey: true }]) {
      for (const code of ownerCodes) {
        expect(press(document.body, code, modifiers).defaultPrevented).toBe(false);
      }
    }
    for (const code of ownerCodes) {
      const handled = new KeyboardEvent('keydown', {
        code, bubbles: true, cancelable: true,
      });
      handled.preventDefault();
      document.body.dispatchEvent(handled);
    }
    expect(pip.hidden).toBe(false);
    expect(pipLabel.textContent).toContain('FPV');
    expect(stats.hidden).toBe(true);
    expect(document.body.classList.contains('editor-collapsed')).toBe(false);

    expect(press(document.body, 'KeyI').defaultPrevented).toBe(false);
    expect(stats.hidden).toBe(true);

    expect(press(document.body, GLOBAL_SHORTCUTS.onboardPip).defaultPrevented).toBe(true);
    expect(pip.hidden).toBe(true);
    press(document.body, GLOBAL_SHORTCUTS.onboardPip);
    expect(pip.hidden).toBe(false);

    expect(press(document.body, GLOBAL_SHORTCUTS.onboardPipMode).defaultPrevented).toBe(true);
    expect(pipLabel.textContent).toContain('DEPTH');

    expect(press(document.body, GLOBAL_SHORTCUTS.editorDock).defaultPrevented).toBe(true);
    expect(document.body.classList.contains('editor-collapsed')).toBe(true);

    expect(press(document.body, GLOBAL_SHORTCUTS.sensorStats).defaultPrevented).toBe(true);
    expect(stats.hidden).toBe(false);
  });
});
