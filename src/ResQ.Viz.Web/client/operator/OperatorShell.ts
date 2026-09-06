// ResQ Viz - operator shell branch and mount ownership
// SPDX-License-Identifier: Apache-2.0

import type { OperatorBootStatus, OperatorMode, OperatorMounts } from './types';
import { ManagedLayerVisibility } from '../ui/managedLayerVisibility';

const EDITOR_CHROME_SELECTOR = [
  '.resq-dock',
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
  readonly bootStatus: HTMLElement;
  readonly bootTitle: HTMLElement;
  readonly bootDetail: HTMLElement;
  readonly v2: HTMLElement;
  readonly legacy: HTMLElement;
  readonly fleetHeading: HTMLElement;
  readonly railToggle: HTMLButtonElement;
  readonly editorToggle: HTMLButtonElement;
  readonly editorLayer: HTMLElement;
  readonly contextLayer: HTMLElement;
  readonly advanced: HTMLDetailsElement;
}

const REQUIRED_IDS = [
  'sidebar',
  'operator-boot',
  'operator-boot-status',
  'operator-boot-title',
  'operator-boot-detail',
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
  private _bootStatus: OperatorBootStatus = 'connecting';
  private _railOpen = true;
  private _contextOpen = false;
  private _railBeforeContext: boolean | null = null;
  private _editorOpen = false;
  private _editorRequestedOpen = false;
  private _editorAvailable = true;
  private _investorSuppressed = false;
  private _investorObserver: MutationObserver | null = null;
  /** Advanced/Safety disclosure state. The trigger is static markup because it
   *  has to exist before the module it loads does; the shell owns whether it is
   *  open and fires the first-expansion callback exactly once. */
  private _advancedExpanded = false;
  private _advancedRequested = false;
  private _advancedLoad: (() => void) | null = null;

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
    const contextLayer = get<HTMLElement>('operator-context-layer');
    this._elements = {
      sidebar: get('sidebar'),
      boot: get('operator-boot'),
      bootStatus: get('operator-boot-status'),
      bootTitle: get('operator-boot-title'),
      bootDetail: get('operator-boot-detail'),
      v2: get('operator-v2-console'),
      legacy: get('legacy-console'),
      fleetHeading: get('fleet-heading'),
      railToggle: get<HTMLButtonElement>('btn-sidebar-toggle'),
      editorToggle: get<HTMLButtonElement>('btn-editor-toggle'),
      editorLayer,
      contextLayer,
      advanced: get<HTMLDetailsElement>('advanced-safety'),
    };
    this.mounts = {
      mission: get('operator-mission'),
      filter: get('fleet-filter'),
      roster: get('fleet-roster'),
      advancedSafety: get('advanced-safety'),
      context: contextLayer,
      modal: get('operator-modal-layer'),
      editor: editorLayer,
    };
    this._investorLayers = new ManagedLayerVisibility([this.mounts.context]);

    this._elements.railToggle.addEventListener('click', () => {
      if (this._contextOpen && this._usesContextDrawer()) {
        this.setContextOpen(false);
        this.setRailOpen(true);
        return;
      }
      this.setRailOpen(!this._railOpen);
    });
    this._elements.editorToggle.addEventListener(
      'click', () => this.setEditorOpen(!this._editorRequestedOpen),
    );
    this._elements.advanced.addEventListener('toggle', () => {
      this._advancedExpanded = this._elements.advanced.open;
      if (this._advancedExpanded) this._loadAdvancedSafety();
    });
    this._advancedExpanded = this._elements.advanced.open;
    this.setBootStatus('connecting');
    this.setMode('booting');
    this.setRailOpen(true);
    this.setContextOpen(false);
    this.setEditorOpen(false);

    const compactEditor = doc.defaultView?.matchMedia('(max-width: 759px)');
    if (compactEditor) {
      const applyEditorAvailability = (): void => this._setEditorAvailable(!compactEditor.matches);
      applyEditorAvailability();
      compactEditor.addEventListener('change', applyEditorAvailability);
    }
    doc.defaultView?.addEventListener('resize', () => this._syncContextViewport());
  }

  get mode(): OperatorMode {
    return this._mode;
  }

  get bootStatus(): OperatorBootStatus {
    return this._bootStatus;
  }

  get editorOpen(): boolean {
    return this._editorOpen;
  }

  get contextOpen(): boolean {
    return this._contextOpen;
  }

  /** Whether the operator has opened the Advanced/Safety disclosure. */
  get advancedSafetyExpanded(): boolean {
    return this._advancedExpanded;
  }

  /**
   * Registers what loads the Advanced/Safety workspace on first expansion.
   *
   * Fires immediately when the disclosure is already open — markup can start it
   * open, and a callback registered a beat later must not miss that. It fires
   * at most once per successful load: `retryAdvancedSafety` is how a failed
   * chunk is asked for again, so a failure does not silently consume the one
   * chance to load it.
   */
  onAdvancedSafetyExpand(load: () => void): void {
    this._advancedLoad = load;
    if (this._advancedExpanded) this._loadAdvancedSafety();
  }

  /** Re-runs the registered loader after a failed chunk fetch. */
  retryAdvancedSafety(): void {
    this._advancedRequested = false;
    if (this._advancedExpanded) this._loadAdvancedSafety();
  }

  private _loadAdvancedSafety(): void {
    if (this._advancedRequested || this._advancedLoad === null) return;
    this._advancedRequested = true;
    this._advancedLoad();
  }

  setMode(mode: OperatorMode): void {
    if (mode !== 'v2' && this._contextOpen) this.setContextOpen(false);
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

  /** Updates the single accessible status surface inside the boot branch. */
  setBootStatus(status: OperatorBootStatus): void {
    this._bootStatus = status;
    const { boot, bootStatus, bootTitle, bootDetail } = this._elements;
    boot.dataset['state'] = status;
    bootStatus.dataset['state'] = status;
    bootStatus.setAttribute('aria-atomic', 'true');

    if (status === 'error') {
      bootStatus.setAttribute('role', 'alert');
      bootStatus.setAttribute('aria-live', 'assertive');
      bootTitle.textContent = 'Simulation link unavailable';
      bootDetail.textContent =
        'Check the simulation host and network connection. Retrying automatically.';
      return;
    }

    bootStatus.setAttribute('role', 'status');
    bootStatus.setAttribute('aria-live', 'polite');
    bootTitle.textContent = 'Establishing simulation link…';
    bootDetail.textContent = 'Negotiating live simulation streams.';
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

  /** Owns the body-level selection context and compact drawer exclusivity. */
  setContextOpen(open: boolean): void {
    if (open && this._mode !== 'v2') return;
    if (open === this._contextOpen) {
      this._syncContextLayer();
      return;
    }

    if (!open) {
      const active = this._doc.activeElement;
      if (active instanceof Element && this._elements.contextLayer.contains(active)) {
        this._elements.railToggle.focus();
      }
    }

    this._contextOpen = open;
    if (open && this._usesContextDrawer()) {
      this._railBeforeContext = this._railOpen;
      this.setRailOpen(false);
    } else if (!open && this._railBeforeContext !== null) {
      const restore = this._railBeforeContext;
      this._railBeforeContext = null;
      this.setRailOpen(restore);
    }
    this._syncContextLayer();
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
    this._syncContextLayer();
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

  focusFleetHeading(): boolean {
    if (this._mode !== 'v2') return false;
    if (this._contextOpen && this._usesContextDrawer()) this.setContextOpen(false);
    if (!this._railOpen) this.setRailOpen(true);
    this._elements.fleetHeading.focus();
    return this._doc.activeElement === this._elements.fleetHeading;
  }

  private _setBranchActive(branch: HTMLElement, active: boolean): void {
    branch.hidden = !active;
    branch.setAttribute('aria-hidden', String(!active));
    this._setInert(branch, !active);
  }

  private _syncContextLayer(): void {
    if (this._investorSuppressed) return;
    const hidden = !this._contextOpen;
    const layer = this._elements.contextLayer;
    layer.hidden = hidden;
    layer.setAttribute('aria-hidden', String(hidden));
    this._setInert(layer, hidden);
  }

  private _usesContextDrawer(): boolean {
    return (this._doc.defaultView?.innerWidth ?? 1100) < 1100;
  }

  private _syncContextViewport(): void {
    if (!this._contextOpen) return;
    if (this._usesContextDrawer()) {
      if (this._railBeforeContext === null) {
        this._railBeforeContext = this._railOpen;
        this.setRailOpen(false);
      }
      return;
    }
    if (this._railBeforeContext === null) return;
    const restore = this._railBeforeContext;
    this._railBeforeContext = null;
    this.setRailOpen(restore);
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
