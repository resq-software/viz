// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';

import { InvestorMode } from '../investorMode';
import type { UnityCamera } from '../cameraControl';
import { setContextObscured } from '../ui/contextObscuring';
import { setHintsVisibleState } from '../ui/hintsVisibility';
import { ManagedLayerVisibility } from '../ui/managedLayerVisibility';
import { setSettingsVisibleState } from '../ui/settingsVisibility';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

describe('investor panel closing', () => {
  it('uses the injected panel owner and does not reveal stale panels on exit', () => {
    document.body.innerHTML = `
      <button id="settings-toggle" aria-expanded="true">Settings</button>
      <div id="settings" class="open" aria-hidden="false"><button id="settings-control">Control</button></div>
      <button id="hints-toggle" aria-pressed="true">Hints</button>
      <div id="hints" aria-hidden="false"><button>Hint control</button></div>
      <aside id="asset" data-context-visible="true"><button>Asset command</button></aside>
    `;
    const settings = document.getElementById('settings')!;
    const settingsToggle = document.getElementById('settings-toggle')!;
    const hints = document.getElementById('hints')!;
    const hintsToggle = document.getElementById('hints-toggle')!;
    const asset = document.getElementById('asset')!;
    setContextObscured(asset, true, document.getElementById('settings-control'));

    const closeOpenPanels = vi.fn(() => {
      setSettingsVisibleState(settings, settingsToggle, false);
      setContextObscured(asset, false, settingsToggle);
      setHintsVisibleState(hints, hintsToggle, false);
    });
    const camera = { setScripted: vi.fn() } as unknown as UnityCamera;
    const investor = new InvestorMode(camera, closeOpenPanels);

    investor.toggle(() => new THREE.Vector3());

    expect(closeOpenPanels).toHaveBeenCalledTimes(1);
    expect(settings.classList.contains('open')).toBe(false);
    expect(settings.hasAttribute('inert')).toBe(true);
    expect(settings.getAttribute('aria-hidden')).toBe('true');
    expect(settingsToggle.getAttribute('aria-expanded')).toBe('false');
    expect(asset.hasAttribute('data-context-obscured')).toBe(false);
    expect(asset.hasAttribute('inert')).toBe(false);
    expect(asset.hidden).toBe(false);
    expect(hints.hidden).toBe(true);
    expect(hints.hasAttribute('inert')).toBe(true);
    expect(hints.getAttribute('aria-hidden')).toBe('true');
    expect(hintsToggle.getAttribute('aria-pressed')).toBe('false');

    investor.toggle(() => new THREE.Vector3());
    expect(closeOpenPanels).toHaveBeenCalledTimes(1);
    expect(settings.classList.contains('open')).toBe(false);
    expect(hints.hidden).toBe(true);
  });

  it('suppresses managed context and editor layers without clearing the selected panel', () => {
    document.body.innerHTML = `
      <button id="settings-toggle" aria-expanded="true">Settings</button>
      <div id="settings" class="open" aria-hidden="false"><button>Setting</button></div>
      <button id="hints-toggle" aria-pressed="true">Hints</button>
      <div id="hints" aria-hidden="false"><button>Hint</button></div>
      <div id="context" class="operator-context-layer" aria-hidden="false">
        <aside id="asset" data-context-visible="true"><button id="asset-command">Command</button></aside>
      </div>
      <div id="editor" class="operator-editor-layer" hidden inert aria-hidden="true"><button>Editor</button></div>
    `;
    const settings = document.getElementById('settings')!;
    const settingsToggle = document.getElementById('settings-toggle')!;
    const hints = document.getElementById('hints')!;
    const hintsToggle = document.getElementById('hints-toggle')!;
    const context = document.getElementById('context')!;
    const editor = document.getElementById('editor')!;
    const asset = document.getElementById('asset')!;
    const layers = new ManagedLayerVisibility([context, editor]);
    (document.getElementById('asset-command') as HTMLButtonElement).focus();

    const camera = { setScripted: vi.fn() } as unknown as UnityCamera;
    const investor = new InvestorMode(
      camera,
      () => {
        setSettingsVisibleState(settings, settingsToggle, false);
        setHintsVisibleState(hints, hintsToggle, false);
      },
      (suppressed) => layers.setSuppressed(suppressed),
    );

    investor.toggle(() => new THREE.Vector3());

    for (const layer of [context, editor]) {
      expect(layer.hidden).toBe(true);
      expect(layer.hasAttribute('inert')).toBe(true);
      expect(layer.getAttribute('aria-hidden')).toBe('true');
      expect(layer.hasAttribute('data-investor-suppressed')).toBe(true);
      expect(layer.contains(document.activeElement)).toBe(false);
    }
    expect(asset.hidden).toBe(false);
    expect(asset.getAttribute('data-context-visible')).toBe('true');
    expect(settings.classList.contains('open')).toBe(false);
    expect(hints.hidden).toBe(true);

    investor.toggle(() => new THREE.Vector3());

    expect(context.hidden).toBe(false);
    expect(context.hasAttribute('inert')).toBe(false);
    expect(context.getAttribute('aria-hidden')).toBe('false');
    expect(editor.hidden).toBe(true);
    expect(editor.hasAttribute('inert')).toBe(true);
    expect(editor.getAttribute('aria-hidden')).toBe('true');
    expect(asset.hidden).toBe(false);
    expect(settings.classList.contains('open')).toBe(false);
    expect(hints.hidden).toBe(true);
  });

  it('wires app ownership and contains no direct panel DOM mutation', () => {
    const app = read('../app.ts');
    const investor = read('../investorMode.ts');
    const operator = read('../styles/operator.css');

    expect(app).toMatch(/new InvestorMode\(\s*viz\.cameraController,\s*\(\) => \{[\s\S]*?_setSettingsVisible\(false\)[\s\S]*?_setHintsVisible\(false\)/);
    expect(app).toMatch(/new ManagedLayerVisibility\([\s\S]*?operatorShell\.mounts\.context[\s\S]*?operatorShell\.mounts\.editor/);
    expect(app).toMatch(/new InvestorMode\([\s\S]*?\(suppressed\) => investorLayers\.setSuppressed\(suppressed\)/);
    expect(investor).not.toContain("getElementById('settings-panel')");
    expect(investor).not.toContain("getElementById('shortcuts-panel')");
    expect(operator).toMatch(/body\.investor-mode \.operator-context-layer,[\s\S]*?body\.investor-mode \.operator-editor-layer[\s\S]*?display:\s*none\s*!important[\s\S]*?pointer-events:\s*none/);
  });
});
