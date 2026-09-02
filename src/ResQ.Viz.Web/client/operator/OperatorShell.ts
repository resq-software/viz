// ResQ Viz - operator shell branch and mount ownership
// SPDX-License-Identifier: Apache-2.0

import type { OperatorMode, OperatorMounts } from './types';
import { ManagedLayerVisibility } from '../ui/managedLayerVisibility';

const EDITOR_CHROME_SELECTOR = [
  '.resq-dock',
  '.resq-dock-toggle',
  '.resq-scenecfg',
  '.resq-dvr',
  '.resq-transport',
  '.resq-pip',
  '.fpv-osd',
  '.cam-mode-pill',
].join(',');

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
  'editor-unavailable-note',
] as const;

type RequiredId = (typeof REQUIRED_IDS)[number];

/** Owns presentation state for the mutually exclusive operator shell branches. */
export class OperatorShell {
  readonly mounts: OperatorMounts;

  private readonly _elements: ShellElements;
  private readonly _doc: Document;
  private readonly _investorLayers: ManagedLayerVisibility;
  private _mode: OperatorMode = 'booting';
  private _railOpen = true;
  private _editorOpen = false;
  private _editorRequestedOpen = false;
  private _editorAvailable = true;
  private _investorSuppressed = false;
  private _investorObserver: MutationObserver | null = null;

  constructor(doc: Document) {
    this._doc = doc;
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
    this._investorLayers = new ManagedLayerVisibility([this.mounts.context]);

    this._elements.railToggle.addEventListener('click', () => this.setRailOpen(!this._railOpen));
    this._elements.editorToggle.addEventListener(
      'click', () => this.setEditorOpen(!this._editorRequestedOpen),
    );
    this.setMode('booting');
    this.setRailOpen(true);
    this.setEditorOpen(false);

    const compactEditor = doc.defaultView?.matchMedia('(max-width: 759px)');
    if (compactEditor) {
      const applyEditorAvailability = (): void => this._setEditorAvailable(!compactEditor.matches);
      applyEditorAvailability();
      compactEditor.addEventListener('change', applyEditorAvailability);
    }
  }

  get mode(): OperatorMode {
    return this._mode;
  }

  get editorOpen(): boolean {
    return this._editorOpen;
  }

  setMode(mode: OperatorMode): void {
    const previous = this._branchFor(this._mode);
    const active = previous.ownerDocument.activeElement;
    const evacuate = mode !== this._mode
      && active instanceof Element
      && previous.contains(active);

    if (evacuate && mode === 'v2') {
      // A hidden/inert target cannot receive focus. Activate it first, focus its
      // stable heading, then retire the other branches below.
      this._setBranchActive(this._elements.v2, true);
      this._elements.fleetHeading.focus();
    } else if (evacuate) {
      // The rail toggle is outside every branch, so it remains operable while
      // the incoming legacy/boot branch becomes available.
      this._elements.railToggle.focus();
    }

    this._mode = mode;
    this._setBranchActive(this._elements.boot, mode === 'booting');
    this._setBranchActive(this._elements.v2, mode === 'v2');
    this._setBranchActive(this._elements.legacy, mode === 'legacy');
  }

  setRailOpen(open: boolean): void {
    this._railOpen = open;
    const { sidebar, railToggle } = this._elements;
    const active = sidebar.ownerDocument.activeElement;
    if (!open && active instanceof Element && sidebar.contains(active)) {
      railToggle.focus();
    }
    sidebar.classList.toggle('collapsed', !open);
    sidebar.hidden = !open;
    sidebar.setAttribute('aria-hidden', String(!open));
    this._setInert(sidebar, !open);
    railToggle.setAttribute('aria-expanded', String(open));
    railToggle.setAttribute('aria-controls', 'sidebar');
  }

  setEditorOpen(open: boolean): void {
    this._editorRequestedOpen = open && this._editorAvailable;
    this._syncEditorOpen();
  }

  /** Suppresses cinematic chrome while retaining only still-valid Editor intent. */
  setInvestorSuppressed(suppressed: boolean): void {
    if (suppressed === this._investorSuppressed) return;
    this._investorSuppressed = suppressed;
    this._syncEditorOpen();

    if (suppressed) {
      this._investorLayers.addLayers(this._editorChrome());
      this._investorLayers.setSuppressed(true);
      const Observer = this._doc.defaultView?.MutationObserver;
      if (Observer && this._doc.body) {
        this._investorObserver = new Observer(() => {
          this._investorLayers.addLayers(this._editorChrome());
        });
        this._investorObserver.observe(this._doc.body, { childList: true, subtree: true });
      }
      return;
    }

    this._investorObserver?.disconnect();
    this._investorObserver = null;
    this._investorLayers.setSuppressed(false);
    // Recompute from the current media-query state. A viewport that crossed
    // below 760px while cinematic mode was active must not reopen stale UI.
    this._syncEditorOpen();
  }

  private _syncEditorOpen(): void {
    const next = this._editorRequestedOpen
      && this._editorAvailable
      && !this._investorSuppressed;
    const { editorLayer, editorToggle } = this._elements;
    const active = editorLayer.ownerDocument.activeElement;
    if (!next && active instanceof Element && editorLayer.contains(active)) {
      editorToggle.focus();
    }
    this._editorOpen = next;
    editorLayer.hidden = !next;
    editorLayer.setAttribute('aria-hidden', String(!next));
    this._setInert(editorLayer, !next);
    editorToggle.setAttribute('aria-expanded', String(next));
    editorToggle.setAttribute('aria-controls', 'operator-editor-layer');
  }

  private _editorChrome(): NodeListOf<HTMLElement> {
    return this._doc.querySelectorAll<HTMLElement>(EDITOR_CHROME_SELECTOR);
  }

  focusFleetHeading(): void {
    this._elements.fleetHeading.focus();
  }

  private _setBranchActive(branch: HTMLElement, active: boolean): void {
    branch.hidden = !active;
    branch.setAttribute('aria-hidden', String(!active));
    this._setInert(branch, !active);
  }

  private _branchFor(mode: OperatorMode): HTMLElement {
    if (mode === 'v2') return this._elements.v2;
    if (mode === 'legacy') return this._elements.legacy;
    return this._elements.boot;
  }

  private _setEditorAvailable(available: boolean): void {
    const toggle = this._elements.editorToggle;
    // Close while the old available state still permits focus evacuation.
    if (!available) this.setEditorOpen(false);
    this._editorAvailable = available;
    toggle.disabled = false;
    toggle.setAttribute('aria-disabled', String(!available));
    toggle.title = available ? 'Editor workspace' : 'Desktop workspace required';
    if (available) toggle.removeAttribute('aria-describedby');
    else toggle.setAttribute('aria-describedby', 'editor-unavailable-note');
  }

  private _setInert(element: HTMLElement, inert: boolean): void {
    if (inert) element.setAttribute('inert', '');
    else element.removeAttribute('inert');
  }
}
