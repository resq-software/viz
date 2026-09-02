// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { beforeEach, describe, expect, it } from 'vitest';

import { OperatorShell, OperatorShellSetupError } from '../operator/OperatorShell';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

function installFixture(): void {
  document.body.innerHTML = `
    <header id="hud-top">
      <button id="btn-sidebar-toggle" type="button"></button>
      <button id="btn-editor-toggle" type="button"></button>
    </header>
    <aside id="sidebar">
      <section id="operator-boot">Connecting</section>
      <section id="operator-v2-console">
        <div id="operator-mission"></div>
        <div id="fleet-filter"></div>
        <h2 id="fleet-heading" tabindex="-1">Fleet</h2>
        <div id="fleet-roster"></div>
        <details id="advanced-safety"><summary>Advanced / Safety</summary></details>
        <button id="btn-spawn-asset" type="button"></button>
        <button id="btn-environment" type="button"></button>
      </section>
      <section id="legacy-console">Legacy</section>
    </aside>
    <div id="operator-context-layer"></div>
    <div id="operator-modal-layer"></div>
    <div id="operator-editor-layer"></div>
  `;
}

function expectBranch(id: string, active: boolean): void {
  const branch = document.getElementById(id) as HTMLElement;
  expect(branch.hidden).toBe(!active);
  expect(branch.hasAttribute('inert')).toBe(!active);
  expect(branch.getAttribute('aria-hidden')).toBe(String(!active));
}

beforeEach(installFixture);

describe('OperatorShell', () => {
  it('starts in booting mode with both consoles isolated', () => {
    const shell = new OperatorShell(document);

    expect(shell.mode).toBe('booting');
    expectBranch('operator-boot', true);
    expectBranch('operator-v2-console', false);
    expectBranch('legacy-console', false);
  });

  it('keeps exactly one mode branch active', () => {
    const shell = new OperatorShell(document);

    shell.setMode('v2');
    expectBranch('operator-boot', false);
    expectBranch('operator-v2-console', true);
    expectBranch('legacy-console', false);

    shell.setMode('legacy');
    expectBranch('operator-boot', false);
    expectBranch('operator-v2-console', false);
    expectBranch('legacy-console', true);
  });

  it('resolves stable mounts outside the translated sidebar where required', () => {
    const shell = new OperatorShell(document);

    expect(shell.mounts.mission.id).toBe('operator-mission');
    expect(shell.mounts.filter.id).toBe('fleet-filter');
    expect(shell.mounts.roster.id).toBe('fleet-roster');
    expect(shell.mounts.advancedSafety.id).toBe('advanced-safety');
    expect(shell.mounts.context.id).toBe('operator-context-layer');
    expect(shell.mounts.modal.id).toBe('operator-modal-layer');
    expect(shell.mounts.editor.id).toBe('operator-editor-layer');
    expect(shell.mounts.context.closest('#sidebar')).toBeNull();
  });

  it('synchronizes rail visibility, inertness, class, and toggle state', () => {
    const shell = new OperatorShell(document);
    const sidebar = document.getElementById('sidebar') as HTMLElement;
    const toggle = document.getElementById('btn-sidebar-toggle') as HTMLButtonElement;

    shell.setRailOpen(false);
    expect(sidebar.classList.contains('collapsed')).toBe(true);
    expect(sidebar.hidden).toBe(true);
    expect(sidebar.hasAttribute('inert')).toBe(true);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.getAttribute('aria-controls')).toBe('sidebar');
    expect(toggle.closest('#sidebar')).toBeNull();

    shell.setRailOpen(true);
    expect(sidebar.classList.contains('collapsed')).toBe(false);
    expect(sidebar.hidden).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
  });

  it('owns editor visibility and expanded state', () => {
    const shell = new OperatorShell(document);
    const layer = document.getElementById('operator-editor-layer') as HTMLElement;
    const toggle = document.getElementById('btn-editor-toggle') as HTMLButtonElement;

    expect(shell.editorOpen).toBe(false);
    expect(layer.hidden).toBe(true);
    expect(layer.hasAttribute('inert')).toBe(true);

    shell.setEditorOpen(true);
    expect(shell.editorOpen).toBe(true);
    expect(layer.hidden).toBe(false);
    expect(layer.hasAttribute('inert')).toBe(false);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.getAttribute('aria-controls')).toBe('operator-editor-layer');
  });

  it('moves focus to the fleet heading', () => {
    const shell = new OperatorShell(document);
    shell.setMode('v2');

    shell.focusFleetHeading();

    expect(document.activeElement?.id).toBe('fleet-heading');
  });

  it('names a missing required mount in its setup error', () => {
    document.getElementById('operator-mission')?.remove();

    expect(() => new OperatorShell(document)).toThrowError(OperatorShellSetupError);
    expect(() => new OperatorShell(document)).toThrowError(/operator-mission/);
  });
});

describe('the shipped operator shell contract', () => {
  it('provides one valid hierarchy with stable mounts and no duplicate ids', () => {
    const page = new DOMParser().parseFromString(read('../index.html'), 'text/html');

    expect(() => new OperatorShell(page)).not.toThrow();
    const ids = Array.from(page.querySelectorAll<HTMLElement>('[id]'), element => element.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect(page.getElementById('btn-start')?.closest('#legacy-console')?.id).toBe('legacy-console');
    expect(page.getElementById('btn-weather')?.closest('#legacy-console')?.id).toBe('legacy-console');
    expect(page.getElementById('operator-context-layer')?.closest('#sidebar')).toBeNull();
    expect(page.getElementById('operator-modal-layer')?.closest('#sidebar')).toBeNull();
    expect(page.getElementById('operator-editor-layer')?.closest('#sidebar')).toBeNull();
    expect(page.getElementById('btn-sidebar-toggle')?.closest('#sidebar')).toBeNull();
    expect(page.getElementById('btn-editor-toggle')?.textContent?.trim()).toBe('Editor');
  });

  it('uses asset language and no longer advertises Tab as a sidebar shortcut', () => {
    const page = new DOMParser().parseFromString(read('../index.html'), 'text/html');
    const emptyCopy = page.getElementById('empty-state')?.textContent ?? '';
    const hints = page.getElementById('key-hints')?.textContent ?? '';
    const sidebarTitle = page.getElementById('btn-sidebar-toggle')?.getAttribute('title') ?? '';

    expect(emptyCopy).toContain('No active assets');
    expect(emptyCopy).not.toContain('drone');
    expect(hints).not.toContain('Tab');
    expect(sidebarTitle).not.toContain('Tab');
  });

  it('declares the approved layer scale and operator responsive stylesheet', () => {
    const tokenCss = read('../styles/tokens.css');
    const mainCss = read('../styles/main.css');
    const app = read('../app.ts');
    const operatorCss = read('../styles/operator.css');

    for (const declaration of [
      '--layer-scene: 0',
      '--layer-rail: 100',
      '--layer-context: 150',
      '--layer-editor: 180',
      '--layer-hud: 200',
      '--layer-popover: 240',
      '--layer-modal: 300',
      '--layer-blocking: 400',
    ]) expect(tokenCss).toContain(declaration);
    expect(app).toContain("import './styles/operator.css'");
    expect(mainCss).toMatch(/#scene-container[\s\S]*?z-index:\s*var\(--layer-scene\)/);
    expect(mainCss).toMatch(/#hud-top[\s\S]*?z-index:\s*var\(--layer-hud\)/);
    expect(mainCss).toMatch(/#sidebar[\s\S]*?z-index:\s*var\(--layer-rail\)/);
    expect(operatorCss).toContain('[hidden]');
    expect(operatorCss).toContain('100dvh');
    expect(operatorCss).toContain('prefers-reduced-motion');
    expect(operatorCss).toContain(':focus-visible');
  });

  it('constructs the shell before legacy controls and switches existing stream hooks', () => {
    const app = read('../app.ts');
    const shellAt = app.indexOf('new OperatorShell(document)');
    const controlsAt = app.indexOf("new ControlPanel(document.getElementById('legacy-console')!)");

    expect(shellAt).toBeGreaterThanOrEqual(0);
    expect(controlsAt).toBeGreaterThan(shellAt);
    expect(app).toMatch(/function _ingestSnapshot[\s\S]*?operatorShell\.setMode\('v2'\)/);
    expect(app).toMatch(/function _leaveV2[\s\S]*?operatorShell\.setMode\('legacy'\)/);
  });
});
