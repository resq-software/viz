// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The Editor workspace is the single owner of every authoring surface: the
// dock, the hierarchy, the inspector, the transform handles and scene
// import/export. Two properties matter more than any individual control here.
//
// 1. Nothing it moves becomes unreachable. A surface hidden behind a toggle is
//    fine; a surface with no toggle, no keyboard route, or one that a viewport
//    change strands off-screen is a control the operator cannot get back.
// 2. Leaving the workspace leaves nothing behind — no inert rail, no listener,
//    no half-mounted panel. The medium-width branch inerts the rail and the
//    context layer on the way in, and every exit path has to undo exactly that.
//
// The recording surfaces (DVR, camera mode, FPV OSD, onboard PiP) are NOT
// authoring and must keep initialising after paint whether or not the Editor is
// ever opened, which is why their stylesheet is asserted to be a separate file.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import * as THREE from 'three';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EDITOR_DOCK_MIN_WIDTH, EDITOR_MIN_WIDTH, EditorWorkspace, editorLayoutFor } from '../editor/workspace';
import type { EditorAuthoringPorts, EditorWorkspacePorts } from '../editor/workspace';
import { OperatorShell } from '../operator/OperatorShell';
import { SelectionStore } from '../editor/selection';
import { liveGate, type MutationGate } from '../operator/interactionMode';
import type { SceneFrame } from '../assets/sceneFrame';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

const REPLAY_GATE: MutationGate = action => ({
  success: false,
  error: { kind: 'replay', code: 'interaction.replay', action },
});

const FRAME = {
  time: 0,
  drones: [{ id: 'd1', pos: [0, 10, 0], vel: [0, 0, 0], status: 'flying', armed: true }],
} as unknown as SceneFrame;

/** One knob drives both `innerWidth` and the shell's own `(max-width: 759px)`. */
let viewportWidth = 1200;

function installViewport(): void {
  Object.defineProperty(window, 'innerWidth', {
    configurable: true,
    get: () => viewportWidth,
  });
  vi.spyOn(window, 'matchMedia').mockImplementation(query => {
    const listeners: Array<() => void> = [];
    const list = {
      get matches() {
        const max = /\(max-width:\s*(\d+)px\)/.exec(query);
        return max ? viewportWidth <= Number(max[1]) : false;
      },
      media: query,
      onchange: null,
      addEventListener: (_t: string, l: EventListenerOrEventListenerObject) => {
        if (typeof l === 'function') listeners.push(() => l(new Event('change')));
      },
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    };
    mediaLists.push({ list, notify: () => listeners.forEach(fn => fn()) });
    return list as unknown as MediaQueryList;
  });
}

const mediaLists: Array<{ list: unknown; notify: () => void }> = [];

/** Moves the viewport and fires every listener that depends on it. */
function setViewport(width: number): void {
  viewportWidth = width;
  for (const entry of mediaLists) entry.notify();
  window.dispatchEvent(new Event('resize'));
}

function installFixture(): void {
  document.body.innerHTML = `
    <header id="hud-top">
      <button id="btn-sidebar-toggle" type="button"></button>
      <button id="btn-editor-toggle" type="button" aria-describedby="editor-unavailable-note">Editor</button>
      <span id="editor-unavailable-note">Desktop workspace required</span>
    </header>
    <aside id="sidebar">
      <section id="operator-boot">
        <div id="operator-boot-status"><strong id="operator-boot-title"></strong><p id="operator-boot-detail"></p></div>
      </section>
      <section id="operator-v2-console">
        <div id="operator-mission"></div>
        <div id="fleet-filter"></div>
        <h2 id="fleet-heading" tabindex="-1">Fleet</h2>
        <div id="fleet-roster"></div>
        <details id="advanced-safety"><summary>Advanced / Safety</summary></details>
        <button id="btn-spawn-asset" type="button"></button>
        <button id="btn-environment" type="button"></button>
        <button id="rail-control" type="button">Rail control</button>
      </section>
      <section id="legacy-console"></section>
    </aside>
    <div id="operator-context-layer"><button id="context-control" type="button">Context</button></div>
    <div id="operator-modal-layer"></div>
    <div id="operator-editor-layer" class="operator-layer operator-editor-layer" hidden inert aria-hidden="true"></div>
  `;
}

interface Harness {
  readonly shell: OperatorShell;
  readonly workspace: EditorWorkspace;
  readonly selection: SelectionStore;
  readonly toggle: HTMLButtonElement;
  readonly rail: HTMLElement;
  readonly context: HTMLElement;
  readonly mount: HTMLElement;
  readonly sendGoto: ReturnType<typeof vi.fn>;
  readonly applyTerrain: ReturnType<typeof vi.fn>;
  readonly applyScenario: ReturnType<typeof vi.fn>;
  /** Opens through the real top-bar control, then settles the lazy load. */
  clickToggle(): Promise<void>;
  settle(): Promise<void>;
}

/** Flushes the mutation-observer microtask plus the authoring import. */
async function flush(workspace?: EditorWorkspace): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  await workspace?.ready();
  await new Promise(resolve => setTimeout(resolve, 0));
}

function harness(gate: MutationGate = liveGate): Harness {
  installFixture();
  const shell = new OperatorShell(document);
  shell.setMode('v2');
  const selection = new SelectionStore();
  const rail = document.getElementById('sidebar') as HTMLElement;
  const context = document.getElementById('operator-context-layer') as HTMLElement;
  const toggle = document.getElementById('btn-editor-toggle') as HTMLButtonElement;
  const sendGoto = vi.fn();
  const applyTerrain = vi.fn();
  const applyScenario = vi.fn(() => ({ success: true as const }));

  const ports: EditorWorkspacePorts = {
    mount: shell.mounts.editor,
    toggle,
    rail,
    context,
    isOpen: () => shell.editorOpen,
    setOpen: open => shell.setEditorOpen(open),
    isRailOpen: () => !rail.hidden,
    setRailOpen: open => shell.setRailOpen(open),
    isContextOpen: () => shell.contextOpen,
    setContextOpen: open => shell.setContextOpen(open),
    viewportWidth: () => window.innerWidth,
  };
  const authoring: EditorAuthoringPorts = {
    selection,
    gate,
    getFrame: () => FRAME,
    onSelect: vi.fn(),
    onDeselect: () => selection.clear(),
    onCommand: vi.fn(),
    gizmo: {
      scene: new THREE.Scene(),
      camera: new THREE.PerspectiveCamera(),
      domElement: document.createElement('canvas'),
      setCameraEnabled: vi.fn(),
      getDronePosition: () => new THREE.Vector3(0, 10, 0),
      sendGoto,
      addTick: vi.fn(),
    },
    sceneConfig: {
      getTerrain: () => 'alpine',
      getScenario: () => null,
      applyTerrain,
      applyScenario,
    },
  };
  const workspace = new EditorWorkspace(ports, authoring);
  return {
    shell, workspace, selection, toggle, rail, context,
    mount: shell.mounts.editor,
    sendGoto, applyTerrain, applyScenario,
    async clickToggle() {
      toggle.click();
      await flush(workspace);
    },
    async settle() {
      await flush(workspace);
    },
  };
}

let active: Harness | null = null;

beforeEach(() => {
  viewportWidth = 1200;
  mediaLists.length = 0;
  installViewport();
});

afterEach(() => {
  active?.workspace.dispose();
  active = null;
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

describe('editorLayoutFor', () => {
  it('agrees with the shell and the stylesheet about where the thresholds are', () => {
    // Three owners read this ladder: the workspace picks the layout, the shell
    // disables the toggle, and the stylesheet withholds the column. If they
    // drift apart the console shows an Editor that is open and empty, or a
    // toggle that opens nothing.
    expect(EDITOR_MIN_WIDTH).toBe(760);
    expect(EDITOR_DOCK_MIN_WIDTH).toBe(1100);
    expect(read('../operator/OperatorShell.ts')).toContain('(max-width: 759px)');
    expect(read('../styles/editor.css'))
      .toMatch(/@media \(max-width: 759px\)[\s\S]*?\.resq-editor[\s\S]*?display:\s*none/);
    expect(read('../styles/operator.css'))
      .toMatch(/@media \(min-width: 760px\) and \(max-width: 1099px\)[\s\S]*?\.operator-editor-layer/);
  });

  it('maps the three shell widths onto the three Editor layouts', () => {
    expect(editorLayoutFor(1440)).toBe('dock');
    expect(editorLayoutFor(1100)).toBe('dock');
    expect(editorLayoutFor(1099)).toBe('fullscreen');
    expect(editorLayoutFor(760)).toBe('fullscreen');
    expect(editorLayoutFor(759)).toBe('unavailable');
    expect(editorLayoutFor(320)).toBe('unavailable');
  });
});

describe('EditorWorkspace at desktop width', () => {
  it('starts closed and loads no authoring surface until asked', async () => {
    const h = (active = harness());
    await h.settle();

    expect(h.shell.editorOpen).toBe(false);
    expect(h.mount.hidden).toBe(true);
    expect(h.workspace.surfaces).toBeNull();
    expect(h.mount.querySelector('.resq-dock')).toBeNull();
    expect(h.toggle.getAttribute('aria-expanded')).toBe('false');
  });

  it('opens as a dock, mounting every authoring surface inside the editor layer', async () => {
    const h = (active = harness());
    await h.clickToggle();

    expect(h.shell.editorOpen).toBe(true);
    expect(h.workspace.layout).toBe('dock');
    expect(h.mount.querySelector('.resq-editor')?.getAttribute('data-layout')).toBe('dock');
    // Reachable by mouse and keyboard: every surface is a descendant of the
    // non-inert editor layer, not a stray body-level overlay.
    for (const selector of ['.resq-dock', '.resq-outliner', '.resq-inspector', '.resq-scenecfg']) {
      expect(h.mount.querySelector(selector), selector).not.toBeNull();
      expect(document.querySelectorAll(selector).length, selector).toBe(1);
    }
    expect(h.mount.hasAttribute('inert')).toBe(false);
    // Desktop dock leaves the rail and the context layer operable.
    expect(h.rail.hidden).toBe(false);
    expect(h.rail.hasAttribute('inert')).toBe(false);
  });

  it('carries the shared selection into the surfaces it mounts', async () => {
    const h = (active = harness());
    h.selection.set('drone', 'd1');
    await h.clickToggle();

    const inspector = h.mount.querySelector('.resq-inspector') as HTMLElement;
    expect(inspector.hidden).toBe(false);
    expect(inspector.querySelector('.ri-id')?.textContent).toBe('d1');
    expect(h.mount.querySelector('.ro-row.is-selected')?.textContent).toContain('d1');
  });

  it('retires the move handles it opened rather than leaving them in the scene', async () => {
    const h = (active = harness());
    h.selection.set('drone', 'd1');
    await h.clickToggle();
    const surfaces = h.workspace.surfaces!;
    expect(surfaces.gizmo.toggleMoveMode()).toBe(true);

    await h.clickToggle();
    expect(h.shell.editorOpen).toBe(false);
    // The handles are scene objects, not editor DOM — closing the panel that
    // owns their on/off state has to turn them off too.
    expect(surfaces.gizmo.isMoveMode).toBe(false);
    expect(h.mount.querySelector('.ri-move')?.getAttribute('aria-pressed')).toBe('false');
  });

  it('is dismissable from its own header, not only from the top bar', async () => {
    // The workspace covers the console at medium width. A surface that can only
    // be left through the control that opened it is a surface an operator can
    // be stuck inside once focus is anywhere else.
    const h = (active = harness());
    await h.clickToggle();
    expect(h.shell.editorOpen).toBe(true);

    (h.mount.querySelector('.resq-editor-close') as HTMLButtonElement).click();
    await h.settle();

    expect(h.shell.editorOpen).toBe(false);
    expect((h.mount.querySelector('.resq-editor') as HTMLElement).hidden).toBe(true);
    expect(document.activeElement).toBe(h.toggle);
  });

  it('closes back to the toggle and keeps page-session panel state', async () => {
    const h = (active = harness());
    h.selection.set('drone', 'd1');
    await h.clickToggle();
    const first = h.workspace.surfaces;
    const inspector = h.mount.querySelector('.resq-inspector') as HTMLElement;
    (h.mount.querySelector('.ri-close') as HTMLButtonElement | null)?.blur();
    (h.mount.querySelector('.scfg-btn') as HTMLButtonElement).focus();

    await h.clickToggle();
    expect(h.shell.editorOpen).toBe(false);
    expect(document.activeElement).toBe(h.toggle);
    // Hidden, not destroyed.
    expect(h.mount.querySelector('.resq-inspector')).toBe(inspector);

    await h.clickToggle();
    expect(h.workspace.surfaces).toBe(first);
    expect(h.mount.querySelector('.resq-inspector')).toBe(inspector);
    expect(inspector.querySelector('.ri-id')?.textContent).toBe('d1');
  });
});

describe('EditorWorkspace at medium width', () => {
  it('takes the screen, inerts the rail and context, and focuses its close control', async () => {
    viewportWidth = 900;
    const h = (active = harness());
    h.shell.setContextOpen(true);
    await h.clickToggle();

    expect(h.workspace.layout).toBe('fullscreen');
    expect(h.mount.querySelector('.resq-editor')?.getAttribute('data-layout')).toBe('fullscreen');
    expect(h.rail.hidden).toBe(true);
    expect(h.rail.hasAttribute('inert')).toBe(true);
    expect(h.rail.getAttribute('aria-hidden')).toBe('true');
    expect(h.shell.contextOpen).toBe(false);
    expect(h.context.hasAttribute('inert')).toBe(true);
    expect(h.context.getAttribute('aria-hidden')).toBe('true');
    expect(document.activeElement).toBe(h.mount.querySelector('.resq-editor-close'));
  });

  it('restores the prior rail state and the toggle focus on the way out', async () => {
    viewportWidth = 900;
    const h = (active = harness());
    h.shell.setRailOpen(false);
    await h.clickToggle();
    await h.clickToggle();

    expect(h.shell.editorOpen).toBe(false);
    expect(h.rail.hidden).toBe(true);          // it was closed before; stays closed
    expect(document.activeElement).toBe(h.toggle);

    h.shell.setRailOpen(true);
    await h.clickToggle();
    expect(h.rail.hidden).toBe(true);
    await h.clickToggle();
    expect(h.rail.hidden).toBe(false);         // it was open before; comes back
    expect(h.rail.hasAttribute('inert')).toBe(false);
  });

  it('gives the rail back when the Editor is closed by something other than its toggle', async () => {
    viewportWidth = 900;
    const h = (active = harness());
    await h.clickToggle();
    expect(h.rail.hidden).toBe(true);

    // Cinematic mode withdraws the workspace without a click. A rail left inert
    // here is a console with no visible controls and no way to get them back.
    h.shell.setInvestorSuppressed(true);
    await h.settle();
    expect(h.shell.editorOpen).toBe(false);
    expect(h.rail.hidden).toBe(false);
    expect(h.rail.hasAttribute('inert')).toBe(false);
  });

  it('drops the medium-width rail lock when the viewport grows back to dock width', async () => {
    viewportWidth = 900;
    const h = (active = harness());
    await h.clickToggle();
    expect(h.rail.hidden).toBe(true);

    setViewport(1400);
    await h.settle();
    expect(h.workspace.layout).toBe('dock');
    expect(h.rail.hidden).toBe(false);
    expect(h.rail.hasAttribute('inert')).toBe(false);
    expect(h.shell.editorOpen).toBe(true);
  });
});

describe('EditorWorkspace below the desktop threshold', () => {
  it('reports the workspace unavailable rather than opening hidden content', async () => {
    viewportWidth = 759;
    const h = (active = harness());
    await h.settle();

    expect(h.workspace.layout).toBe('unavailable');
    expect(h.toggle.getAttribute('aria-disabled')).toBe('true');
    expect(h.toggle.getAttribute('aria-describedby')).toBe('editor-unavailable-note');
    expect(document.getElementById('editor-unavailable-note')?.textContent)
      .toBe('Desktop workspace required');

    await h.clickToggle();
    expect(h.shell.editorOpen).toBe(false);
    expect(h.mount.hidden).toBe(true);
    expect(h.workspace.surfaces).toBeNull();
    expect(h.rail.hidden).toBe(false);
  });
});

describe('EditorWorkspace during replay', () => {
  it('withdraws the mutations and keeps the local reads', async () => {
    const h = (active = harness(REPLAY_GATE));
    h.selection.set('drone', 'd1');
    await h.clickToggle();
    const surfaces = h.workspace.surfaces!;

    // Transform handles command a drone: refused, and refused visibly rather
    // than by silently sending nothing.
    expect(surfaces.gizmo.toggleMoveMode()).toBe(false);
    expect(surfaces.gizmo.isMoveMode).toBe(false);
    expect(h.sendGoto).not.toHaveBeenCalled();

    // Scene import writes the running world: refused with the reason on screen.
    const file = new File(['{"version":1,"terrain":"alpine","scenario":null}'], 'scene.json');
    const input = h.mount.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { configurable: true, value: [file] });
    input.dispatchEvent(new Event('change'));
    await h.settle();
    const status = h.mount.querySelector('.scfg-status') as HTMLElement;
    expect(status.hidden).toBe(false);
    expect(status.textContent).toContain('interaction.replay');
    expect(h.applyTerrain).not.toHaveBeenCalled();
    expect(h.applyScenario).not.toHaveBeenCalled();

    // Export reads what is already on screen, and the inspector reads the frame.
    const exportBtn = h.mount.querySelector('[aria-label="Export scene"]') as HTMLButtonElement;
    expect(exportBtn.disabled).toBe(false);
    expect((h.mount.querySelector('.resq-inspector') as HTMLElement).hidden).toBe(false);
  });
});

describe('Editor workspace module boundaries', () => {
  const workspaceSrc = read('../editor/workspace.ts');

  it('keeps every authoring module and the authoring stylesheet out of its own chunk', () => {
    for (const specifier of [
      '../styles/editor.css', './dock', './outliner', './inspector', './gizmo', './sceneConfig',
    ]) {
      const staticImport = new RegExp(
        `^import\\s+(?!type\\b)[^;]*'${specifier.replace(/[./]/g, '\\$&')}'`, 'm',
      );
      expect(staticImport.test(workspaceSrc), specifier).toBe(false);
    }
    // …and does load them, dynamically, when the workspace is first opened.
    for (const specifier of ['./dock', './outliner', './inspector', './gizmo', './sceneConfig']) {
      expect(workspaceSrc).toContain(`import('${specifier}')`);
    }
  });

  it('stores no workspace state outside the shell', () => {
    // The Editor is closed on every newly opened app session, and the dock no
    // longer remembers a collapse across reloads.
    expect(workspaceSrc).not.toContain('localStorage');
    expect(read('../editor/dock.ts')).not.toContain('localStorage');
    // No second open flag: the shell is asked, never mirrored.
    expect(workspaceSrc).not.toMatch(/_open\s*(:|=)\s*(true|false)/);
  });

  it('separates the always-on overlays from the authoring stylesheet', () => {
    for (const module of [
      '../editor/dvr.ts', '../cameraMode.ts', '../sensors/fpvOsd.ts', '../sensors/onboardPip.ts',
    ]) {
      const src = read(module);
      expect(src, module).toContain('operator-overlays.css');
      expect(src, module).not.toContain('styles/editor.css');
    }
    const overlays = read('../styles/operator-overlays.css');
    for (const selector of ['.resq-dvr', '.cam-mode-pill', '.fpv-osd', '.resq-pip']) {
      expect(overlays, selector).toContain(selector);
    }
    const editorCss = read('../styles/editor.css');
    for (const selector of ['.resq-dvr {', '.cam-mode-pill {', '.fpv-osd {', '.resq-pip {']) {
      expect(editorCss, selector).not.toContain(selector);
    }
    // The authoring sheet keeps the authoring surfaces.
    for (const selector of ['.resq-dock', '.resq-outliner', '.resq-inspector', '.resq-scenecfg']) {
      expect(editorCss, selector).toContain(selector);
    }
  });

  it('pairs the full-screen layout with a hidden rule the UA sheet cannot lose', () => {
    const editorCss = read('../styles/editor.css');
    expect(editorCss).toMatch(/\.resq-editor\[hidden\]/);
    expect(editorCss).toMatch(/\[data-layout=['"]fullscreen['"]\]/);
  });
});
