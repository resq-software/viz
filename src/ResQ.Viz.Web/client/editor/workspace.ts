// ResQ Viz - Editor workspace (the one owner of every authoring surface)
// SPDX-License-Identifier: Apache-2.0

import type { SceneFrame } from '../assets/sceneFrame';
import type { MutationGate } from '../operator/interactionMode';
import type { EditorDock } from './dock';
import type { TransformGizmo, GizmoOptions } from './gizmo';
import type { Inspector } from './inspector';
import type { Outliner, OutlinerSelectFn } from './outliner';
import type { SceneConfigDeps, SceneConfigPanel } from './sceneConfig';
import type { SelectionStore } from './selection';

/** Narrowest viewport that can host the authoring workspace at all. */
export const EDITOR_MIN_WIDTH = 760;
/** Narrowest viewport that hosts it beside the console instead of over it. */
export const EDITOR_DOCK_MIN_WIDTH = 1100;

/**
 * How the Editor presents at a given viewport width.
 *
 * `unavailable` is a first-class answer rather than "open it and let CSS hide
 * it": a toggle that opens content nothing can reach is the failure this whole
 * task exists to remove.
 */
export type EditorLayout = 'dock' | 'fullscreen' | 'unavailable';

/** Layout for a viewport width. Pure — unit-tested. */
export function editorLayoutFor(width: number): EditorLayout {
    if (width >= EDITOR_DOCK_MIN_WIDTH) return 'dock';
    if (width >= EDITOR_MIN_WIDTH) return 'fullscreen';
    return 'unavailable';
}

/**
 * The shell surfaces the workspace drives. Deliberately functions over an
 * object: `OperatorShell` remains the sole owner of visibility state, and the
 * workspace reads it back through `isOpen` rather than keeping a second flag
 * that could disagree with the one the toggle's `aria-expanded` is drawn from.
 */
export interface EditorWorkspacePorts {
    /** The shell's editor layer — every authoring surface mounts inside it. */
    readonly mount: HTMLElement;
    /** Top-bar Editor control; the last-resort focus target when closing. */
    readonly toggle: HTMLElement;
    readonly rail: HTMLElement;
    readonly context: HTMLElement;
    readonly isOpen: () => boolean;
    readonly setOpen: (open: boolean) => void;
    readonly isRailOpen: () => boolean;
    readonly setRailOpen: (open: boolean) => void;
    readonly isContextOpen: () => boolean;
    readonly setContextOpen: (open: boolean) => void;
    readonly viewportWidth: () => number;
}

/** World wiring the app owns and the authoring surfaces need. */
export interface EditorAuthoringPorts {
    readonly selection: SelectionStore;
    /** Shared live/replay gate — handed to the two surfaces that mutate. */
    readonly gate: MutationGate;
    readonly getFrame: () => SceneFrame | null;
    /** An outliner row was chosen. */
    readonly onSelect: OutlinerSelectFn;
    /** The inspector's close button — the app's unified deselect. */
    readonly onDeselect: () => void;
    readonly onCommand: (droneId: string, cmd: string) => void;
    /** Scene/camera wiring for the transform handles, minus what this owns. */
    readonly gizmo: Omit<GizmoOptions, 'store' | 'gate'>;
    /** Terrain/scenario wiring for import-export, minus what this owns. */
    readonly sceneConfig: Omit<SceneConfigDeps, 'gate' | 'mount'>;
    /** Handed the surfaces once they exist, so the app can keep its handles. */
    readonly onReady?: (surfaces: EditorAuthoringSurfaces) => void;
    readonly onError?: (error: unknown) => void;
}

/** Everything the first Editor open brings into existence. */
export interface EditorAuthoringSurfaces {
    readonly dock: EditorDock;
    readonly outliner: Outliner;
    readonly inspector: Inspector;
    readonly gizmo: TransformGizmo;
    readonly sceneConfig: SceneConfigPanel;
}

/**
 * One owner for the dock, hierarchy, inspector, transform handles and scene
 * import/export.
 *
 * Three invariants:
 *
 *  * **Nothing it moves becomes unreachable.** Every surface is a descendant of
 *    the shell's editor layer, so the layer's own `hidden`/`inert` is the only
 *    thing that can withhold it, and it is withheld only by controls that say so
 *    — the labelled top-bar toggle and this workspace's own Close button — or by
 *    the two automatic writers named below. Below {@link EDITOR_MIN_WIDTH} the
 *    toggle reports unavailable instead of opening content the layout cannot
 *    show.
 *  * **Leaving it leaves nothing behind.** The medium-width branch closes and
 *    inerts the rail and the context layer on the way in; the prior rail state
 *    is restored on *every* exit, including the ones nobody clicked — cinematic
 *    mode withdrawing the workspace, or the viewport growing past the dock
 *    threshold. That is why the open signal is the layer's own `hidden`
 *    attribute rather than the toggle's click: `hidden` is what every writer
 *    goes through, and a click is only one of them.
 *  * **It stores no open state and no preferences.** The shell is asked; the
 *    session starts closed; nothing is persisted.
 */
export class EditorWorkspace {
    private readonly _ports: EditorWorkspacePorts;
    private readonly _authoring: EditorAuthoringPorts;
    private readonly _observer: MutationObserver | null;
    private readonly _onResize: () => void;
    private _root: HTMLElement | null = null;
    private _head: HTMLElement | null = null;
    private _body: HTMLElement | null = null;
    private _status: HTMLElement | null = null;
    private _retry: HTMLButtonElement | null = null;
    private _close: HTMLButtonElement | null = null;
    private _surfaces: EditorAuthoringSurfaces | null = null;
    private _loading: Promise<void> | null = null;
    /** Rail state captured on the way into the full-screen branch, or null when
     *  the branch does not currently hold the rail. */
    private _railBefore: boolean | null = null;

    constructor(ports: EditorWorkspacePorts, authoring: EditorAuthoringPorts) {
        this._ports = ports;
        this._authoring = authoring;
        this._onResize = () => this.sync();
        ports.mount.ownerDocument.defaultView?.addEventListener('resize', this._onResize);

        const Observer = ports.mount.ownerDocument.defaultView?.MutationObserver;
        this._observer = Observer ? new Observer(() => this.sync()) : null;
        this._observer?.observe(ports.mount, { attributes: true, attributeFilter: ['hidden'] });
        this.sync();
    }

    /** Presentation the current viewport supports. */
    get layout(): EditorLayout {
        return editorLayoutFor(this._ports.viewportWidth());
    }

    /** The shell's answer, never a mirror of it. */
    get isOpen(): boolean {
        return this._ports.isOpen();
    }

    /** The authoring surfaces, or null until the Editor has been opened once. */
    get surfaces(): EditorAuthoringSurfaces | null {
        return this._surfaces;
    }

    /** Reveals the workspace, subject to the shell's availability rules. */
    open(): void {
        this._ports.setOpen(true);
    }

    /** Withdraws the workspace. */
    close(): void {
        this._ports.setOpen(false);
    }

    /** Resolves once any in-flight authoring load has settled. */
    async ready(): Promise<void> {
        await this._loading;
    }

    /** Re-reads the shell and the viewport and applies both. */
    sync(): void {
        const open = this.isOpen;
        if (open) this._ensureChrome();
        this._applyLayout(open);
        this._applyRailLock(open && this.layout === 'fullscreen');
        if (open) {
            void this._ensureAuthoring();
            this._focusEntry();
            return;
        }
        // Move handles are scene objects, not editor DOM: closing the workspace
        // would otherwise leave grab handles floating over the world with the
        // panel that owns their on/off state withdrawn.
        this._surfaces?.gizmo.setMoveMode(false);
        this._surfaces?.inspector.setMoveActive(false);
        this._restoreFocus();
    }

    /** Re-attempts an authoring load that failed. */
    retry(): void {
        this._loading = null;
        if (this.isOpen) void this._ensureAuthoring();
    }

    /** Drops the listeners this owns. Surfaces are page-session and stay put. */
    dispose(): void {
        this._observer?.disconnect();
        this._ports.mount.ownerDocument.defaultView
            ?.removeEventListener('resize', this._onResize);
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private _applyLayout(open: boolean): void {
        const root = this._root;
        if (!root) return;
        const layout = this.layout;
        root.dataset['layout'] = layout === 'unavailable' ? 'fullscreen' : layout;
        root.hidden = !open;
    }

    /**
     * Holds or hands back the rail. Guarded on `_railBefore` rather than on the
     * caller's argument so a repeated sync — the resize listener and the
     * attribute observer both fire on a breakpoint change — cannot overwrite
     * the captured state with the closed one it just wrote.
     */
    private _applyRailLock(locked: boolean): void {
        if (locked) {
            if (this._railBefore !== null) return;
            // Closed first: at medium width the shell restores the pre-context
            // rail state when the context drawer closes, so capturing before
            // this would record the drawer's temporary closure as the operator's.
            this._ports.setContextOpen(false);
            this._railBefore = this._ports.isRailOpen();
            this._ports.setRailOpen(false);
            return;
        }
        if (this._railBefore === null) return;
        const restore = this._railBefore;
        this._railBefore = null;
        this._ports.setRailOpen(restore);
    }

    // ── Focus ────────────────────────────────────────────────────────────────

    private _focusEntry(): void {
        if (this.layout !== 'fullscreen') return;
        const doc = this._ports.mount.ownerDocument;
        const active = doc.activeElement;
        if (active instanceof Element && this._ports.mount.contains(active)) return;
        this._close?.focus();
    }

    /**
     * The shell returns focus to the toggle whenever it retires a layer that
     * held it. Cinematic mode and a breakpoint change can retire it while focus
     * sits elsewhere entirely, and `<body>` is not somewhere an operator can
     * act from — so the labelled toggle is the floor.
     */
    private _restoreFocus(): void {
        const doc = this._ports.mount.ownerDocument;
        const active = doc.activeElement;
        if (active !== null && active !== doc.body) return;
        if (this._surfaces === null) return;
        this._ports.toggle.focus();
    }

    // ── Chrome + authoring surfaces ──────────────────────────────────────────

    private _ensureChrome(): void {
        if (this._root) return;
        const doc = this._ports.mount.ownerDocument;
        const root = doc.createElement('section');
        root.className = 'resq-editor';
        root.setAttribute('aria-label', 'Editor workspace');

        const head = doc.createElement('header');
        head.className = 'resq-editor-head';
        const title = doc.createElement('h2');
        title.className = 'resq-editor-title';
        title.textContent = 'Editor';
        const close = doc.createElement('button');
        close.type = 'button';
        close.className = 'resq-editor-close';
        close.textContent = 'Close editor';
        close.addEventListener('click', () => this.close());
        head.append(title, close);

        const body = doc.createElement('div');
        body.className = 'resq-editor-body';
        const status = doc.createElement('p');
        status.className = 'resq-editor-status';
        status.setAttribute('role', 'status');
        status.hidden = true;
        const retry = doc.createElement('button');
        retry.type = 'button';
        retry.className = 'btn resq-editor-retry';
        retry.textContent = 'Retry editor tools';
        retry.hidden = true;
        retry.addEventListener('click', () => this.retry());
        body.append(status, retry);

        root.append(head, body);
        this._ports.mount.appendChild(root);
        this._root = root;
        this._head = head;
        this._body = body;
        this._status = status;
        this._retry = retry;
        this._close = close;
    }

    private _ensureAuthoring(): Promise<void> {
        if (this._surfaces !== null) return Promise.resolve();
        if (this._loading !== null) return this._loading;
        this._setStatus('Loading editor tools…', false);
        this._loading = this._loadAuthoring();
        return this._loading;
    }

    private async _loadAuthoring(): Promise<void> {
        try {
            const [dockModule, outlinerModule, inspectorModule, gizmoModule, configModule] =
                await Promise.all([
                    import('./dock'),
                    import('./outliner'),
                    import('./inspector'),
                    import('./gizmo'),
                    import('./sceneConfig'),
                ]);
            this._mountAuthoring(
                dockModule, outlinerModule, inspectorModule, gizmoModule, configModule,
            );
        } catch (error: unknown) {
            this._setStatus('Editor tools could not be loaded.', true);
            this._authoring.onError?.(error);
        }
    }

    private _mountAuthoring(
        dockModule: typeof import('./dock'),
        outlinerModule: typeof import('./outliner'),
        inspectorModule: typeof import('./inspector'),
        gizmoModule: typeof import('./gizmo'),
        configModule: typeof import('./sceneConfig'),
    ): void {
        this._ensureChrome();
        const host = this._body!;
        const ports = this._authoring;
        const dock = new dockModule.EditorDock(host);
        const outliner = new outlinerModule.Outliner(ports.selection, dock.host());
        outliner.onSelect(ports.onSelect);
        const inspector = new inspectorModule.Inspector(
            ports.selection, ports.getFrame, dock.host(),
        );
        inspector.onClose(ports.onDeselect);
        inspector.onCommand(ports.onCommand);
        const gizmo = new gizmoModule.TransformGizmo({
            ...ports.gizmo,
            store: ports.selection,
            gate: ports.gate,
        });
        // The gizmo owns move-mode truth, so the button reflects what it decided
        // rather than what was asked for — a refused toggle must not light up.
        inspector.onMove(() => { inspector.setMoveActive(gizmo.toggleMoveMode()); });
        const sceneConfig = new configModule.SceneConfigPanel({
            ...ports.sceneConfig,
            gate: ports.gate,
            mount: this._head!,
        });

        // Prime the hierarchy from the frame the app is already holding. The
        // inspector renders itself from the immediate selection callback, but
        // the outliner is fed per frame, so without this an Editor opened
        // between ticks presents an empty scene it does not have.
        outliner.update(ports.getFrame());

        this._surfaces = { dock, outliner, inspector, gizmo, sceneConfig };
        this._setStatus(null, false);
        ports.onReady?.(this._surfaces);
        this._focusEntry();
    }

    private _setStatus(message: string | null, retryable: boolean): void {
        const status = this._status;
        const retry = this._retry;
        if (!status || !retry) return;
        status.hidden = message === null;
        status.textContent = message ?? '';
        retry.hidden = !retryable;
    }
}
