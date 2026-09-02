// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { OperatorShell, OperatorShellSetupError } from '../operator/OperatorShell';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

function withoutCssComments(css: string): string {
  return css.replace(/\/\*[\s\S]*?\*\//g, '');
}

function cssRule(css: string, selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const start = new RegExp(`^\\s*${escaped}\\s*\\{`, 'm').exec(css)?.index ?? -1;
  if (start < 0) return '';
  const open = css.indexOf('{', start);
  const close = css.indexOf('}', open);
  return open < 0 || close < 0 ? '' : css.slice(open + 1, close);
}

function layerValue(tokenCss: string, name: string): number {
  const match = new RegExp(`--layer-${name}:\\s*(\\d+)`).exec(tokenCss);
  return Number(match?.[1] ?? Number.NaN);
}

function mockEditorMedia(initiallyCompact: boolean): { setCompact(value: boolean): void } {
  let compact = initiallyCompact;
  const listeners: Array<() => void> = [];
  vi.spyOn(window, 'matchMedia').mockImplementation(query => ({
    get matches() { return query === '(max-width: 759px)' && compact; },
    media: query,
    onchange: null,
    addEventListener: (_type: string, listener: EventListenerOrEventListenerObject) => {
      if (typeof listener === 'function') listeners.push(() => listener(new Event('change')));
    },
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }) as unknown as MediaQueryList);
  return {
    setCompact(value: boolean): void {
      compact = value;
      for (const listener of listeners) listener();
    },
  };
}

function installFixture(): void {
  document.body.innerHTML = `
    <header id="hud-top">
      <button id="btn-sidebar-toggle" type="button"></button>
      <button id="btn-editor-toggle" type="button" aria-describedby="editor-unavailable-note"></button>
      <span id="editor-unavailable-note">Desktop workspace required</span>
    </header>
    <aside id="sidebar">
      <section id="operator-boot">
        <span>Operator console</span>
        <div id="operator-boot-status" role="status" aria-live="polite" aria-atomic="true" data-state="connecting">
          <strong id="operator-boot-title">Establishing simulation link…</strong>
          <p id="operator-boot-detail">Negotiating live simulation streams.</p>
        </div>
      </section>
      <section id="operator-v2-console">
        <div id="operator-mission"></div>
        <div id="fleet-filter"></div>
        <h2 id="fleet-heading" tabindex="-1">Fleet</h2>
        <button id="fleet-action" type="button">Fleet action</button>
        <div id="fleet-roster"></div>
        <details id="advanced-safety"><summary>Advanced / Safety</summary></details>
        <button id="btn-spawn-asset" type="button"></button>
        <button id="btn-environment" type="button"></button>
      </section>
      <section id="legacy-console"><button id="legacy-action" type="button">Legacy action</button></section>
    </aside>
    <div id="operator-context-layer"></div>
    <div id="operator-modal-layer"></div>
    <div id="operator-editor-layer"><button id="editor-child">Editor child</button></div>
  `;
}

function expectBranch(id: string, active: boolean): void {
  const branch = document.getElementById(id) as HTMLElement;
  expect(branch.hidden).toBe(!active);
  expect(branch.hasAttribute('inert')).toBe(!active);
  expect(branch.getAttribute('aria-hidden')).toBe(String(!active));
}

beforeEach(installFixture);
afterEach(() => vi.restoreAllMocks());

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

  it('owns accessible connecting and error presentation inside the boot branch', () => {
    const shell = new OperatorShell(document);
    const boot = document.getElementById('operator-boot') as HTMLElement;
    const status = document.getElementById('operator-boot-status') as HTMLElement;
    const title = document.getElementById('operator-boot-title') as HTMLElement;
    const detail = document.getElementById('operator-boot-detail') as HTMLElement;

    expect(shell.bootStatus).toBe('connecting');
    expect(status.getAttribute('role')).toBe('status');
    expect(status.getAttribute('aria-live')).toBe('polite');

    shell.setBootStatus('error');
    expect(shell.bootStatus).toBe('error');
    expect(boot.dataset['state']).toBe('error');
    expect(status.dataset['state']).toBe('error');
    expect(status.getAttribute('role')).toBe('alert');
    expect(status.getAttribute('aria-live')).toBe('assertive');
    expect(title.textContent).toBe('Simulation link unavailable');
    expect(detail.textContent).toBe(
      'Check the simulation host and network connection. Retrying automatically.',
    );

    shell.setBootStatus('connecting');
    expect(shell.bootStatus).toBe('connecting');
    expect(boot.dataset['state']).toBe('connecting');
    expect(status.getAttribute('role')).toBe('status');
    expect(status.getAttribute('aria-live')).toBe('polite');
    expect(title.textContent).toBe('Establishing simulation link…');
    expect(detail.textContent).toBe('Negotiating live simulation streams.');
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

  it('moves legacy focus to the v2 fleet heading before retiring legacy', () => {
    const shell = new OperatorShell(document);
    shell.setMode('legacy');
    (document.getElementById('legacy-action') as HTMLButtonElement).focus();

    shell.setMode('v2');

    expect(document.activeElement?.id).toBe('fleet-heading');
    expect(document.activeElement?.closest('[hidden], [inert]')).toBeNull();
  });

  it('moves v2 focus to the external rail toggle before retiring v2', () => {
    const shell = new OperatorShell(document);
    shell.setMode('v2');
    (document.getElementById('fleet-action') as HTMLButtonElement).focus();

    shell.setMode('legacy');

    expect(document.activeElement?.id).toBe('btn-sidebar-toggle');
    expect(document.activeElement?.closest('[hidden], [inert]')).toBeNull();
  });

  it('evacuates focus to the rail toggle before closing the rail', () => {
    const shell = new OperatorShell(document);
    shell.setMode('legacy');
    (document.getElementById('legacy-action') as HTMLButtonElement).focus();

    shell.setRailOpen(false);

    expect(document.activeElement?.id).toBe('btn-sidebar-toggle');
    expect(document.activeElement?.closest('[hidden], [inert]')).toBeNull();
  });

  it('does not steal focus already outside the rail when closing it', () => {
    const shell = new OperatorShell(document);
    const editorToggle = document.getElementById('btn-editor-toggle') as HTMLButtonElement;
    editorToggle.focus();

    shell.setRailOpen(false);

    expect(document.activeElement).toBe(editorToggle);
  });

  it('disables Editor below 760px with an accessible explanation', () => {
    mockEditorMedia(true);
    const shell = new OperatorShell(document);
    const toggle = document.getElementById('btn-editor-toggle') as HTMLButtonElement;
    const layer = document.getElementById('operator-editor-layer') as HTMLElement;

    expect(toggle.disabled).toBe(false);
    expect(toggle.getAttribute('aria-disabled')).toBe('true');
    expect(toggle.getAttribute('aria-describedby')).toBe('editor-unavailable-note');
    expect(toggle.title).toBe('Desktop workspace required');
    shell.setEditorOpen(true);
    expect(shell.editorOpen).toBe(false);
    expect(layer.hidden).toBe(true);
    toggle.click();
    expect(shell.editorOpen).toBe(false);
  });

  it('evacuates editor focus before close and before becoming unavailable', () => {
    const media = mockEditorMedia(false);
    const shell = new OperatorShell(document);
    const toggle = document.getElementById('btn-editor-toggle') as HTMLButtonElement;
    const child = document.getElementById('editor-child') as HTMLButtonElement;
    shell.setEditorOpen(true);
    child.focus();

    shell.setEditorOpen(false);
    expect(document.activeElement).toBe(toggle);

    shell.setEditorOpen(true);
    child.focus();
    media.setCompact(true);
    expect(document.activeElement).toBe(toggle);
    expect(toggle.disabled).toBe(false);
    expect(toggle.getAttribute('aria-disabled')).toBe('true');
  });

  it('keeps the focused Editor toggle announced across breakpoints and opens when available', () => {
    const media = mockEditorMedia(true);
    const shell = new OperatorShell(document);
    const toggle = document.getElementById('btn-editor-toggle') as HTMLButtonElement;
    toggle.focus();

    media.setCompact(false);
    expect(document.activeElement).toBe(toggle);
    expect(toggle.getAttribute('aria-disabled')).toBe('false');
    toggle.click();
    expect(shell.editorOpen).toBe(true);
  });

  it('suppresses actual body-level editor chrome and restores the requested workspace', async () => {
    mockEditorMedia(false);
    const shell = new OperatorShell(document);
    const context = shell.mounts.context;
    const layer = shell.mounts.editor;
    const dock = document.createElement('div');
    dock.className = 'resq-dock';
    dock.innerHTML = '<button id="body-editor-control">Author</button>';
    const dvr = document.createElement('div');
    dvr.className = 'resq-dvr';
    document.body.append(dock, dvr);
    shell.setEditorOpen(true);
    (document.getElementById('body-editor-control') as HTMLButtonElement).focus();

    shell.setInvestorSuppressed(true);

    expect(shell.editorOpen).toBe(false);
    expect(layer.hidden).toBe(true);
    expect(layer.hasAttribute('inert')).toBe(true);
    for (const surface of [context, dock, dvr]) {
      expect(surface.hidden).toBe(false);
      expect(surface.hasAttribute('inert')).toBe(true);
      expect(surface.getAttribute('aria-hidden')).toBeNull();
      expect(surface.hasAttribute('data-investor-suppressed')).toBe(true);
    }
    expect(dock.contains(document.activeElement)).toBe(false);

    const lateSceneConfig = document.createElement('div');
    lateSceneConfig.className = 'resq-scenecfg';
    document.body.append(lateSceneConfig);
    await Promise.resolve();
    expect(lateSceneConfig.hidden).toBe(false);
    expect(lateSceneConfig.hasAttribute('inert')).toBe(true);
    expect(lateSceneConfig.hasAttribute('data-investor-suppressed')).toBe(true);

    shell.setInvestorSuppressed(false);

    expect(shell.editorOpen).toBe(true);
    expect(layer.hidden).toBe(false);
    for (const surface of [context, dock, dvr, lateSceneConfig]) {
      expect(surface.hidden).toBe(false);
      expect(surface.hasAttribute('inert')).toBe(false);
      expect(surface.hasAttribute('data-investor-suppressed')).toBe(false);
    }
  });

  it('revalidates Editor availability instead of restoring stale state after Investor', () => {
    const media = mockEditorMedia(false);
    const shell = new OperatorShell(document);
    const layer = shell.mounts.editor;
    const toggle = document.getElementById('btn-editor-toggle') as HTMLButtonElement;
    shell.setEditorOpen(true);

    shell.setInvestorSuppressed(true);
    media.setCompact(true);
    shell.setInvestorSuppressed(false);

    expect(shell.editorOpen).toBe(false);
    expect(layer.hidden).toBe(true);
    expect(layer.hasAttribute('inert')).toBe(true);
    expect(layer.getAttribute('aria-hidden')).toBe('true');
    expect(toggle.getAttribute('aria-disabled')).toBe('true');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
  });
});

describe('the shipped operator shell contract', () => {
  it('ships boot visible and both console branches isolated before JavaScript', () => {
    const page = new DOMParser().parseFromString(read('../index.html'), 'text/html');
    const boot = page.getElementById('operator-boot') as HTMLElement;
    const v2 = page.getElementById('operator-v2-console') as HTMLElement;
    const legacy = page.getElementById('legacy-console') as HTMLElement;

    expect(boot.hidden).toBe(false);
    expect(boot.hasAttribute('inert')).toBe(false);
    expect(boot.getAttribute('aria-hidden')).toBe('false');
    const status = page.getElementById('operator-boot-status') as HTMLElement;
    expect(boot.dataset['state']).toBe('connecting');
    expect(status.dataset['state']).toBe('connecting');
    expect(status.getAttribute('role')).toBe('status');
    expect(status.getAttribute('aria-live')).toBe('polite');
    expect(status.getAttribute('aria-atomic')).toBe('true');
    expect(page.getElementById('operator-boot-title')?.textContent?.trim())
      .toBe('Establishing simulation link…');
    expect(page.getElementById('operator-boot-detail')?.textContent?.trim())
      .toBe('Negotiating live simulation streams.');
    for (const branch of [v2, legacy]) {
      expect(branch.hidden).toBe(true);
      expect(branch.hasAttribute('inert')).toBe(true);
      expect(branch.getAttribute('aria-hidden')).toBe('true');
    }
  });

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
    expect(page.getElementById('editor-unavailable-note')?.textContent?.trim())
      .toBe('Desktop workspace required');
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

  it('ships an explicit, text-labeled legacy compatibility notice', () => {
    const page = new DOMParser().parseFromString(read('../index.html'), 'text/html');
    const notice = page.getElementById('legacy-mode-notice');
    const operatorCss = read('../styles/operator.css');

    expect(notice?.closest('#legacy-console')?.id).toBe('legacy-console');
    expect(notice?.getAttribute('role')).toBe('status');
    expect(notice?.textContent?.trim()).toBe('Legacy mode: v2 unavailable');
    expect(operatorCss).toMatch(/\.legacy-mode-notice[\s\S]*?color:\s*var\(--warning\)/);
  });

  it('leaves the fleet filter wrapper unlabeled for its mounted control', () => {
    const page = new DOMParser().parseFromString(read('../index.html'), 'text/html');
    expect(page.getElementById('fleet-filter')?.hasAttribute('aria-label')).toBe(false);
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

  it('styles the boot error with a semantic token as well as explicit text', () => {
    const operatorCss = read('../styles/operator.css');
    expect(operatorCss).toMatch(
      /\.operator-boot\[data-state=['"]error['"]\][\s\S]*?border-color:\s*var\(--primary-text\)/,
    );
  });

  it('constructs the shell before legacy controls and delegates stream modes to startup', () => {
    const app = read('../app.ts');
    const shellAt = app.indexOf('new OperatorShell(document)');
    const startupAt = app.indexOf('new StartupCoordinator({');
    const controlsAt = app.indexOf("new ControlPanel(document.getElementById('legacy-console')!)");

    expect(shellAt).toBeGreaterThanOrEqual(0);
    expect(startupAt).toBeGreaterThan(shellAt);
    expect(controlsAt).toBeGreaterThan(startupAt);
    expect(app).toMatch(/new StartupCoordinator\(\{[\s\S]*?setMode:\s*mode\s*=>\s*\{[\s\S]*?operatorShell\.setMode\(mode\)/);

    const ingest = app.slice(
      app.indexOf('function _ingestSnapshot'),
      app.indexOf('function _onDeltaGap'),
    );
    const leave = app.slice(
      app.indexOf('function _leaveV2'),
      app.indexOf('function _subscribeSnapshots'),
    );
    expect(ingest).not.toContain('operatorShell.setMode');
    expect(leave).not.toContain('operatorShell.setMode');
  });

  it('keeps every operative global z-index inside the shared scale', () => {
    const sources = [
      '../styles/main.css',
      '../styles/operator.css',
      '../styles/assets.css',
      '../styles/editor.css',
      '../ui/cockpit.css',
    ];
    const offenders: string[] = [];

    for (const source of sources) {
      const css = withoutCssComments(read(source));
      for (const match of css.matchAll(/z-index:\s*(\d+)/g)) {
        const value = Number(match[1]);
        if (value > 400) offenders.push(`${source}:${value}`);
      }
    }

    expect(offenders).toEqual([]);
  });

  it('maps named global surfaces to semantic layer variables', () => {
    const main = read('../styles/main.css');
    const operator = read('../styles/operator.css');
    const assets = read('../styles/assets.css');
    const editor = read('../styles/editor.css');
    const cockpit = read('../ui/cockpit.css');
    const mappings: ReadonlyArray<readonly [string, string, string]> = [
      [main, '#scene-container', '--layer-scene'],
      [main, '#sidebar', '--layer-rail'],
      [main, '.settings-panel', '--layer-context'],
      [assets, '.asset-panel', '--layer-context'],
      [operator, '.operator-editor-layer', '--layer-editor'],
      [editor, '.resq-dock', '--layer-editor'],
      [main, '#hud-top', '--layer-hud'],
      [main, '.mission-chrome', '--layer-hud'],
      [main, '.partition-banner', '--layer-hud'],
      [main, '.telemetry-strip', '--layer-hud'],
      [editor, '.resq-dvr', '--layer-hud'],
      [cockpit, '.cockpit', '--layer-hud'],
      [main, '#key-hints', '--layer-popover'],
      [main, '.scenario-intro', '--layer-modal'],
      [operator, '.operator-modal-layer', '--layer-modal'],
      [main, '.loading-overlay', '--layer-blocking'],
    ];

    for (const [css, selector, layer] of mappings) {
      expect(cssRule(css, selector), selector).toContain(`z-index: var(${layer})`);
    }
  });

  it('keeps the blocking layer above every other shared layer', () => {
    const tokens = read('../styles/tokens.css');
    const names = ['scene', 'rail', 'context', 'editor', 'hud', 'popover', 'modal'] as const;
    const blocking = layerValue(tokens, 'blocking');

    expect(Number.isFinite(blocking)).toBe(true);
    for (const name of names) expect(blocking).toBeGreaterThan(layerValue(tokens, name));
  });

  it('defines the approved compact phone interaction and HUD contract', () => {
    const operator = read('../styles/operator.css');

    expect(operator).toMatch(/@media \(max-width: 759px\)[\s\S]*?\.operator-primary-actions \.btn[\s\S]*?min-height:\s*44px/);
    expect(operator).toMatch(/@media \(max-width: 759px\)[\s\S]*?#sidebar button[\s\S]*?\.operator-context-layer button[\s\S]*?min-height:\s*44px/);
    expect(operator).toMatch(/@media \(max-width: 759px\)[\s\S]*?#btn-editor-toggle[\s\S]*?#btn-sidebar-toggle[\s\S]*?min-height:\s*44px/);
    expect(operator).toMatch(/@media \(max-width: 759px\)[\s\S]*?\.hud-zone-center[\s\S]*?display:\s*none/);
    for (const id of ['hud-cockpit-toggle', 'hud-hints-toggle', 'hud-settings-toggle']) {
      expect(operator).toMatch(new RegExp(`@media \\(max-width: 759px\\)[\\s\\S]*?#${id}[\\s\\S]*?display:\\s*none`));
    }
    expect(operator).toMatch(/#btn-editor-toggle\[aria-disabled="true"\][\s\S]*?cursor:\s*not-allowed/);
    expect(operator).toMatch(/@media \(max-width: 759px\)[\s\S]*?#hud-top[\s\S]*?overflow:\s*hidden/);
  });

  it('uses the matching logical safe-area inset on each compact HUD edge', () => {
    const operator = read('../styles/operator.css');

    expect(operator).toMatch(/padding-inline-start:\s*max\(8px, env\(safe-area-inset-left\)\)/);
    expect(operator).toMatch(/padding-inline-end:\s*max\(8px, env\(safe-area-inset-right\)\)/);
    expect(operator).not.toMatch(/padding-inline:\s*max\([^;]*safe-area-inset-left/);
  });

  it('defines exact desktop, medium, and compact shell cascades', () => {
    const operator = read('../styles/operator.css');
    const main = read('../styles/main.css');
    const editor = read('../styles/editor.css');
    const assets = read('../styles/assets.css');

    expect(operator).toMatch(/@media \(min-width: 1100px\)[\s\S]*?#sidebar[\s\S]*?\.operator-context-layer/);
    expect(operator).toMatch(/@media \(min-width: 760px\) and \(max-width: 1099px\)[\s\S]*?#sidebar[\s\S]*?transform:\s*translateX\(-100%\)[\s\S]*?\.operator-context-layer[\s\S]*?\.operator-editor-layer/);
    expect(main).toMatch(/@media \(max-width: 1099px\)[\s\S]*?#scene-container[\s\S]*?left:\s*0/);
    expect(editor).toMatch(/@media \(max-width: 1099px\)[\s\S]*?\.resq-dvr[\s\S]*?left:\s*0/);
    expect(editor).toMatch(/@media \(max-width: 1099px\)[\s\S]*?\.resq-dock[\s\S]*?display:\s*none/);
    expect(assets).toMatch(/@media \(min-width: 760px\) and \(max-width: 1099px\)[\s\S]*?\.asset-panel[\s\S]*?left:/);
  });

  it('reserves effective safe-area HUD and DVR extents throughout the shell', () => {
    const tokens = read('../styles/tokens.css');
    const operator = read('../styles/operator.css');
    const editor = read('../styles/editor.css');
    const main = read('../styles/main.css');

    expect(tokens).toContain('--effective-hud-h: calc(var(--hud-h) + env(safe-area-inset-top))');
    expect(tokens).toContain('--effective-dvr-h: calc(var(--dvr-h) + env(safe-area-inset-bottom))');
    expect(main).toMatch(/#hud-top[\s\S]*?height:\s*var\(--effective-hud-h\)[\s\S]*?padding-block-start:\s*env\(safe-area-inset-top\)/);
    expect(operator).toMatch(/#sidebar[\s\S]*?top:\s*var\(--effective-hud-h\)[\s\S]*?bottom:\s*var\(--effective-dvr-h\)/);
    expect(operator).toContain('var(--effective-hud-h)');
    expect(operator).toContain('var(--effective-dvr-h)');
    expect(editor).toMatch(/\.resq-dvr[\s\S]*?height:\s*var\(--effective-dvr-h\)[\s\S]*?padding-block-end:\s*env\(safe-area-inset-bottom\)/);
  });

  it('covers every compact sidebar, context, and DVR native target with 44px hit areas', () => {
    const operator = read('../styles/operator.css');

    for (const selector of [
      '#sidebar button', '#sidebar select', '#sidebar input[type="text"]',
      '#sidebar input[type="number"]', '#sidebar input[type="range"]', '#sidebar summary',
      '#sidebar a[href]', '.operator-context-layer button', '.operator-context-layer select',
      '.operator-context-layer input', '.operator-context-layer summary',
      '.operator-context-layer a[href]', '.resq-dvr button', '.resq-dvr input[type="range"]',
      '#sidebar label:has(input[type="checkbox"], input[type="radio"])',
    ]) expect(operator, selector).toContain(selector);
    expect(operator).toMatch(/@media \(max-width: 759px\)[\s\S]*?min-height:\s*44px/);
    expect(operator).toMatch(/\.resq-dvr button[\s\S]*?min-width:\s*44px[\s\S]*?height:\s*44px/);
  });
});
