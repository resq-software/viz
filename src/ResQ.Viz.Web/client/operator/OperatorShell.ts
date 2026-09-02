// ResQ Viz - operator shell branch and mount ownership
// SPDX-License-Identifier: Apache-2.0

import type { OperatorMode, OperatorMounts } from './types';

/** Raised when the static page does not provide the shell contract. */
export class OperatorShellSetupError extends Error {
  constructor(readonly missingIds: readonly string[]) {
    super(`Operator shell setup is missing required element${missingIds.length === 1 ? '' : 's'}: ${
      missingIds.map(id => `#${id}`).join(', ')
    }`);
    this.name = 'OperatorShellSetupError';
  }
}

interface ShellElements {
  readonly sidebar: HTMLElement;
  readonly boot: HTMLElement;
  readonly v2: HTMLElement;
  readonly legacy: HTMLElement;
  readonly fleetHeading: HTMLElement;
  readonly railToggle: HTMLButtonElement;
  readonly editorToggle: HTMLButtonElement;
  readonly editorLayer: HTMLElement;
}

const REQUIRED_IDS = [
  'sidebar',
  'operator-boot',
  'operator-v2-console',
  'legacy-console',
  'operator-mission',
  'fleet-filter',
  'fleet-heading',
  'fleet-roster',
  'advanced-safety',
  'btn-spawn-asset',
  'btn-environment',
  'operator-context-layer',
  'operator-modal-layer',
  'operator-editor-layer',
  'btn-sidebar-toggle',
  'btn-editor-toggle',
] as const;

type RequiredId = (typeof REQUIRED_IDS)[number];

/** Owns presentation state for the mutually exclusive operator shell branches. */
export class OperatorShell {
  readonly mounts: OperatorMounts;

  private readonly _elements: ShellElements;
  private _mode: OperatorMode = 'booting';
  private _railOpen = true;
  private _editorOpen = false;

  constructor(doc: Document) {
    const found = new Map<RequiredId, HTMLElement>();
    const missing: string[] = [];
    for (const id of REQUIRED_IDS) {
      const element = doc.getElementById(id);
      if (element === null) missing.push(id);
      else found.set(id, element);
    }
    if (missing.length > 0) throw new OperatorShellSetupError(missing);

    const get = <T extends HTMLElement>(id: RequiredId): T => found.get(id) as T;
    const editorLayer = get<HTMLElement>('operator-editor-layer');
    this._elements = {
      sidebar: get('sidebar'),
      boot: get('operator-boot'),
      v2: get('operator-v2-console'),
      legacy: get('legacy-console'),
      fleetHeading: get('fleet-heading'),
      railToggle: get<HTMLButtonElement>('btn-sidebar-toggle'),
      editorToggle: get<HTMLButtonElement>('btn-editor-toggle'),
      editorLayer,
    };
    this.mounts = {
      mission: get('operator-mission'),
      filter: get('fleet-filter'),
      roster: get('fleet-roster'),
      advancedSafety: get('advanced-safety'),
      context: get('operator-context-layer'),
      modal: get('operator-modal-layer'),
      editor: editorLayer,
    };

    this._elements.railToggle.addEventListener('click', () => this.setRailOpen(!this._railOpen));
    this._elements.editorToggle.addEventListener(
      'click', () => this.setEditorOpen(!this._editorOpen),
    );
    this.setMode('booting');
    this.setRailOpen(true);
    this.setEditorOpen(false);
  }

  get mode(): OperatorMode {
    return this._mode;
  }

  get editorOpen(): boolean {
    return this._editorOpen;
  }

  setMode(mode: OperatorMode): void {
    this._mode = mode;
    this._setBranchActive(this._elements.boot, mode === 'booting');
    this._setBranchActive(this._elements.v2, mode === 'v2');
    this._setBranchActive(this._elements.legacy, mode === 'legacy');
  }

  setRailOpen(open: boolean): void {
    this._railOpen = open;
    const { sidebar, railToggle } = this._elements;
    sidebar.classList.toggle('collapsed', !open);
    sidebar.hidden = !open;
    sidebar.setAttribute('aria-hidden', String(!open));
    this._setInert(sidebar, !open);
    railToggle.setAttribute('aria-expanded', String(open));
    railToggle.setAttribute('aria-controls', 'sidebar');
  }

  setEditorOpen(open: boolean): void {
    this._editorOpen = open;
    const { editorLayer, editorToggle } = this._elements;
    editorLayer.hidden = !open;
    editorLayer.setAttribute('aria-hidden', String(!open));
    this._setInert(editorLayer, !open);
    editorToggle.setAttribute('aria-expanded', String(open));
    editorToggle.setAttribute('aria-controls', 'operator-editor-layer');
  }

  focusFleetHeading(): void {
    this._elements.fleetHeading.focus();
  }

  private _setBranchActive(branch: HTMLElement, active: boolean): void {
    branch.hidden = !active;
    branch.setAttribute('aria-hidden', String(!active));
    this._setInert(branch, !active);
  }

  private _setInert(element: HTMLElement, inert: boolean): void {
    if (inert) element.setAttribute('inert', '');
    else element.removeAttribute('inert');
  }
}
