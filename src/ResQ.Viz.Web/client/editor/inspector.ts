// ResQ Viz - Editor inspector panel (selection-driven, schema-based)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import { hazardKey } from './keys';
import type { Selection, SelectionKind, SelectionStore } from './selection';
import type {
    VizFrame,
    Vec3,
    Quat,
    DroneState,
    HazardState,
    DetectionState,
} from '../types';

/** Placeholder rendered for any absent/empty field value. */
const EMPTY = '—'; // em dash

/** Drone command buttons surfaced in the inspector (match server cmd types). */
const DRONE_COMMANDS: ReadonlyArray<{ label: string; cmd: string }> = [
    { label: 'Hover', cmd: 'hover' },
    { label: 'RTL', cmd: 'rtl' },
    { label: 'Land', cmd: 'land' },
];

// ─── Pure formatters (exported for unit tests) ──────────────────────────────

/** Format a Vec3 as "x · y · z" to `digits` decimals, or EMPTY if absent. */
export function fmtVec(v: Vec3 | undefined, digits = 1): string {
    if (!v || v.length < 3) return EMPTY;
    return `${v[0].toFixed(digits)} · ${v[1].toFixed(digits)} · ${v[2].toFixed(digits)}`;
}

/** Euclidean magnitude of a Vec3 (e.g. speed from a velocity), or EMPTY. */
export function fmtMag(v: Vec3 | undefined, digits = 1): string {
    if (!v || v.length < 3) return EMPTY;
    return Math.hypot(v[0], v[1], v[2]).toFixed(digits);
}

/** Format a quaternion's four components compactly, or EMPTY. */
export function fmtQuat(q: Quat | undefined, digits = 2): string {
    if (!q || q.length < 4) return EMPTY;
    return q.map(n => n.toFixed(digits)).join(' · ');
}

/** Whole-number percent with a trailing %, or EMPTY when undefined. */
export function fmtPct(n: number | undefined): string {
    return n === undefined ? EMPTY : `${Math.round(n)}%`;
}

/** A non-empty string passthrough, or EMPTY. */
export function fmtStr(s: string | undefined): string {
    return s && s.length > 0 ? s : EMPTY;
}

/** A boolean as yes/no, or EMPTY when undefined. */
export function fmtBool(b: boolean | undefined): string {
    return b === undefined ? EMPTY : b ? 'yes' : 'no';
}

// ─── Schemas ────────────────────────────────────────────────────────────────

interface Field<T> {
    readonly label: string;
    readonly value: (entity: T) => string;
}

interface KindSchema {
    readonly title: string;
    readonly resolve: (id: string, frame: VizFrame | null) => unknown;
    readonly fields: ReadonlyArray<Field<unknown>>;
}

/**
 * Typed schema builder — keeps each kind's field accessors strongly typed
 * against its entity type while erasing to a uniform `unknown` registry entry
 * the Inspector can iterate without per-kind branching.
 */
function defineSchema<T>(s: {
    title: string;
    resolve: (id: string, frame: VizFrame | null) => T | null;
    fields: ReadonlyArray<Field<T>>;
}): KindSchema {
    return s as unknown as KindSchema;
}

/**
 * Per-kind inspector schemas. Drones are wired to selection today; hazard and
 * detection schemas prove the store + Inspector generalise to every VizFrame
 * entity and are ready for the outliner / pickable hazards in later steps.
 */
export const SCHEMAS: Readonly<Record<SelectionKind, KindSchema>> = {
    drone: defineSchema<DroneState>({
        title: 'Drone',
        resolve: (id, frame) => frame?.drones?.find(d => d.id === id) ?? null,
        fields: [
            { label: 'status', value: d => fmtStr(d.status) },
            { label: 'armed', value: d => fmtBool(d.armed) },
            { label: 'battery', value: d => fmtPct(d.battery) },
            { label: 'vendor', value: d => fmtStr(d.vendor) },
            { label: 'position', value: d => fmtVec(d.pos) },
            { label: 'velocity', value: d => fmtVec(d.vel) },
            { label: 'speed', value: d => fmtMag(d.vel) },
            { label: 'rotation', value: d => fmtQuat(d.rot) },
        ],
    }),
    hazard: defineSchema<HazardState>({
        title: 'Hazard',
        resolve: (id, frame) => frame?.hazards?.find(h => hazardKey(h) === id) ?? null,
        fields: [
            { label: 'type', value: h => fmtStr(h.type) },
            { label: 'centre', value: h => fmtVec(h.center) },
            { label: 'radius', value: h => (h.radius === undefined ? EMPTY : h.radius.toFixed(1)) },
        ],
    }),
    detection: defineSchema<DetectionState>({
        title: 'Detection',
        resolve: (id, frame) => frame?.detections?.find(d => d.id === id) ?? null,
        fields: [
            { label: 'type', value: d => fmtStr(d.type) },
            { label: 'drone', value: d => fmtStr(d.droneId) },
            { label: 'confidence', value: d => `${Math.round(d.confidence * 100)}%` },
            { label: 'position', value: d => fmtVec(d.pos) },
        ],
    }),
};

// ─── Inspector panel ────────────────────────────────────────────────────────

/**
 * Selection-driven property panel — the editor layer's read surface.
 *
 * Subscribes to a {@link SelectionStore}: on selection it renders the matching
 * schema's rows once, then {@link Inspector.update} refreshes only the text
 * each frame (no DOM rebuild). Distinct in role from the operator-facing
 * `DronePanel` — this is the raw, all-fields, any-entity inspector that future
 * editable fields and gizmo bindings hang off.
 */
export class Inspector {
    private readonly _root: HTMLElement;
    private readonly _kindEl: HTMLElement;
    private readonly _idEl: HTMLElement;
    private readonly _fieldsEl: HTMLElement;
    private readonly _actionsEl: HTMLElement;
    private readonly _getFrame: () => VizFrame | null;
    private _sel: Selection | null = null;
    private _onCloseFn: (() => void) | null = null;
    private _onCommandFn: ((id: string, cmd: string) => void) | null = null;
    private _onMoveFn: ((id: string) => void) | null = null;
    private _moveBtn: HTMLButtonElement | null = null;
    private _visible = false;
    /** Live cell refs so per-frame updates touch text only, not the tree. */
    private _cells: Array<{ field: Field<unknown>; el: HTMLElement }> = [];

    constructor(store: SelectionStore, getFrame: () => VizFrame | null, parent: HTMLElement) {
        this._getFrame = getFrame;
        const built = this._build(parent);
        this._root = built.root;
        this._kindEl = built.kindEl;
        this._idEl = built.idEl;
        this._fieldsEl = built.fieldsEl;
        this._actionsEl = built.actionsEl;
        built.closeBtn.addEventListener('click', () => {
            // Prefer the app-wired unified deselect (keeps legacy HUD surfaces
            // in sync); fall back to clearing the store directly.
            if (this._onCloseFn) this._onCloseFn();
            else store.clear();
        });
        store.subscribe(sel => this._onSelection(sel));
    }

    /** Route the close button to the app's unified deselect path. */
    onClose(fn: () => void): void {
        this._onCloseFn = fn;
    }

    /** Wire the drone command buttons (Hover/RTL/Land) to a command sender. */
    onCommand(fn: (id: string, cmd: string) => void): void {
        this._onCommandFn = fn;
    }

    /** Wire the "Move" button to a move-mode toggle (the reposition gizmo). */
    onMove(fn: (id: string) => void): void {
        this._onMoveFn = fn;
    }

    /** Reflect move-mode state on the Move button (app is the source of truth). */
    setMoveActive(on: boolean): void {
        this._moveBtn?.setAttribute('aria-pressed', String(on));
        this._moveBtn?.classList.toggle('is-active', on);
    }

    /**
     * Refresh field values from the latest frame. Cheap — text only. Hides the
     * panel if the selected entity has dropped out of the frame (mirrors how
     * DronePanel handles a vanished drone).
     */
    update(frame: VizFrame | null): void {
        if (!this._sel) return;
        const schema = SCHEMAS[this._sel.kind];
        const entity = schema.resolve(this._sel.id, frame);
        if (entity === null) {
            this._hide();
            return;
        }
        for (const { field, el } of this._cells) {
            const v = field.value(entity);
            el.textContent = v;
            el.classList.toggle('is-empty', v === EMPTY);
        }
        // update() owns final visibility: show iff the entity is present, so a
        // drone that drops out then reappears re-shows without a re-select.
        this._show();
    }

    private _onSelection(sel: Selection | null): void {
        this._sel = sel;
        if (!sel) {
            this._hide();
            return;
        }
        const schema = SCHEMAS[sel.kind];
        this._kindEl.textContent = schema.title;
        this._idEl.textContent = sel.id;
        this._idEl.title = sel.id;
        this._renderFields(schema);
        this._renderActions(sel);
        this.update(this._getFrame()); // fills values + shows iff entity present
    }

    private _renderFields(schema: KindSchema): void {
        this._fieldsEl.replaceChildren();
        this._cells = [];
        for (const field of schema.fields) {
            const dt = document.createElement('dt');
            dt.className = 'ri-key';
            dt.textContent = field.label;
            const dd = document.createElement('dd');
            dd.className = 'ri-val';
            this._fieldsEl.append(dt, dd);
            this._cells.push({ field, el: dd });
        }
    }

    /** Per-kind action buttons below the fields — drone commands + Move toggle. */
    private _renderActions(sel: Selection): void {
        this._actionsEl.replaceChildren();
        this._moveBtn = null;
        if (sel.kind !== 'drone') return;
        for (const { label, cmd } of DRONE_COMMANDS) {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'ri-cmd';
            b.textContent = label;
            b.addEventListener('click', () => this._onCommandFn?.(sel.id, cmd));
            this._actionsEl.append(b);
        }
        // "Move" toggles the reposition gizmo. App owns the on/off state
        // (setMoveActive) so the M key and this button stay in sync; rebuilding
        // on each selection resets it to off.
        //
        // That reset is only correct because TransformGizmo clears _moveMode on
        // EVERY selection change, drone-to-drone included. It used to clear only
        // when the new selection was not a drone, so switching between drones
        // left the gizmo live while this button rendered "off" — and the next
        // click read inverted. Keep the two in step if either side changes.
        const move = document.createElement('button');
        move.type = 'button';
        move.className = 'ri-cmd ri-move';
        move.textContent = 'Move';
        move.setAttribute('aria-pressed', 'false');
        move.addEventListener('click', () => this._onMoveFn?.(sel.id));
        this._actionsEl.append(move);
        this._moveBtn = move;
    }

    private _show(): void {
        if (this._visible) return;
        this._visible = true;
        this._root.hidden = false;
        // Per WAI-ARIA, expose by removing aria-hidden, not setting it "false".
        this._root.removeAttribute('aria-hidden');
    }

    private _hide(): void {
        if (!this._visible) return;
        this._visible = false;
        this._root.hidden = true;
        this._root.setAttribute('aria-hidden', 'true');
    }

    private _build(parent: HTMLElement): {
        root: HTMLElement;
        kindEl: HTMLElement;
        idEl: HTMLElement;
        fieldsEl: HTMLElement;
        actionsEl: HTMLElement;
        closeBtn: HTMLElement;
    } {
        const root = document.createElement('aside');
        root.className = 'resq-inspector';
        root.hidden = true;
        root.setAttribute('aria-label', 'Selection inspector');
        root.setAttribute('aria-hidden', 'true');

        const head = document.createElement('header');
        head.className = 'ri-head';
        const kindEl = document.createElement('span');
        kindEl.className = 'ri-kind';
        const idEl = document.createElement('span');
        idEl.className = 'ri-id';
        const closeBtn = document.createElement('button');
        closeBtn.className = 'ri-close';
        closeBtn.type = 'button';
        closeBtn.setAttribute('aria-label', 'Clear selection');
        closeBtn.textContent = '×'; // ×
        head.append(kindEl, idEl, closeBtn);

        const fieldsEl = document.createElement('dl');
        fieldsEl.className = 'ri-fields';

        const actionsEl = document.createElement('div');
        actionsEl.className = 'ri-actions';

        root.append(head, fieldsEl, actionsEl);
        parent.appendChild(root);
        return { root, kindEl, idEl, fieldsEl, actionsEl, closeBtn };
    }
}
