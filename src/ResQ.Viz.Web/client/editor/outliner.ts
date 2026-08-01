// ResQ Viz - Editor outliner (live scene hierarchy)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import { hazardKey } from './keys';
import { kindLabel } from './selection';
import type { Selection, SelectionKind, SelectionStore } from './selection';
import type { VizFrame } from '../types';

/** Callback invoked when an outliner row is chosen. */
export type OutlinerSelectFn = (kind: SelectionKind, id: string) => void;

/** One row's data: a primary id and a short secondary tag (status/type). */
export interface OutlineItem {
    readonly id: string;
    readonly sub: string;
}

/** A titled group of rows for one entity kind. */
export interface OutlineGroup {
    readonly kind: SelectionKind;
    readonly title: string;
    readonly items: OutlineItem[];
}

/** Placeholder for a drone with no reported status. */
const NO_STATUS = '—';

/**
 * Pure projection of a frame into the outliner's grouped view-model. Exported
 * so the grouping/keying is unit-tested without touching the DOM.
 */
export function buildHierarchy(frame: VizFrame | null): OutlineGroup[] {
    const drones = frame?.drones ?? [];
    const hazards = frame?.hazards ?? [];
    const detections = frame?.detections ?? [];
    return [
        {
            kind: 'drone',
            title: kindLabel('drone'),
            items: drones.map(d => ({ id: d.id, sub: d.status ?? NO_STATUS })),
        },
        {
            kind: 'hazard',
            title: kindLabel('hazard'),
            items: hazards.map(h => ({ id: hazardKey(h), sub: h.type })),
        },
        {
            kind: 'detection',
            title: kindLabel('detection'),
            items: detections.map(d => ({ id: d.id, sub: d.type })),
        },
    ];
}

interface GroupEls {
    readonly kind: SelectionKind;
    readonly sectionEl: HTMLElement;
    readonly countEl: HTMLElement;
    readonly listEl: HTMLElement;
    readonly rows: Map<string, { row: HTMLButtonElement; subEl: HTMLElement }>;
    /** Signature of the last-rendered id set — skips rebuilds when unchanged. */
    sig: string;
}

const KIND_ORDER: ReadonlyArray<{ kind: SelectionKind; title: string }> = [
    { kind: 'drone', title: kindLabel('drone') },
    { kind: 'hazard', title: kindLabel('hazard') },
    { kind: 'detection', title: kindLabel('detection') },
];

/**
 * Scene hierarchy panel — a Unity-style outliner over the live frame.
 *
 * Rebuilds a kind's rows only when its id-set changes (preserving scroll and
 * avoiding 10 Hz DOM churn); refreshes secondary tags in place each frame.
 * Rows publish selection via {@link Outliner.onSelect}; the active row tracks
 * the {@link SelectionStore}, so picking from any surface highlights here.
 */
export class Outliner {
    private readonly _root: HTMLElement;
    private readonly _groups: Record<SelectionKind, GroupEls>;
    private _onSelectFn: OutlinerSelectFn | null = null;
    private _selected: Selection | null = null;
    private _visible = false;

    constructor(store: SelectionStore, parent: HTMLElement) {
        const built = this._build(parent);
        this._root = built.root;
        this._groups = built.groups;
        store.subscribe(sel => {
            this._selected = sel;
            this._applyHighlight();
        });
    }

    onSelect(fn: OutlinerSelectFn): void {
        this._onSelectFn = fn;
    }

    /** Reconcile the tree with the latest frame. */
    update(frame: VizFrame | null): void {
        const groups = buildHierarchy(frame);
        let total = 0;
        for (const g of groups) {
            total += g.items.length;
            this._syncGroup(g);
        }
        if (total === 0) this._hide();
        else this._show();
        this._applyHighlight();
    }

    private _syncGroup(group: OutlineGroup): void {
        const els = this._groups[group.kind];
        els.countEl.textContent = String(group.items.length);
        els.sectionEl.hidden = group.items.length === 0;

        const sig = group.items.map(i => i.id).join('|');
        if (sig !== els.sig) {
            els.sig = sig;
            this._rebuildGroup(els, group.items);
            return;
        }
        // Id-set unchanged — refresh only the mutable secondary tags in place.
        for (const item of group.items) {
            const cell = els.rows.get(item.id);
            if (cell) cell.subEl.textContent = item.sub;
        }
    }

    private _rebuildGroup(els: GroupEls, items: OutlineItem[]): void {
        els.listEl.replaceChildren();
        els.rows.clear();
        for (const item of items) {
            const row = document.createElement('button');
            row.type = 'button';
            row.className = 'ro-row';
            row.setAttribute('aria-pressed', 'false');

            const nameEl = document.createElement('span');
            nameEl.className = 'ro-name';
            nameEl.textContent = item.id;
            nameEl.title = item.id;

            const subEl = document.createElement('span');
            subEl.className = 'ro-sub';
            subEl.textContent = item.sub;

            row.append(nameEl, subEl);
            row.addEventListener('click', () => this._onSelectFn?.(els.kind, item.id));
            els.listEl.append(row);
            els.rows.set(item.id, { row, subEl });
        }
    }

    private _applyHighlight(): void {
        for (const { kind } of KIND_ORDER) {
            const els = this._groups[kind];
            for (const [id, cell] of els.rows) {
                const active = this._selected?.kind === kind && this._selected.id === id;
                cell.row.classList.toggle('is-selected', active);
                cell.row.setAttribute('aria-pressed', String(active));
            }
        }
    }

    private _show(): void {
        if (this._visible) return;
        this._visible = true;
        this._root.hidden = false;
        this._root.removeAttribute('aria-hidden');
    }

    private _hide(): void {
        if (!this._visible) return;
        this._visible = false;
        this._root.hidden = true;
        this._root.setAttribute('aria-hidden', 'true');
    }

    private _build(parent: HTMLElement): { root: HTMLElement; groups: Record<SelectionKind, GroupEls> } {
        const root = document.createElement('aside');
        root.className = 'resq-outliner';
        root.hidden = true;
        root.setAttribute('aria-label', 'Scene hierarchy');
        root.setAttribute('aria-hidden', 'true');

        const head = document.createElement('header');
        head.className = 'ro-head';
        head.textContent = 'Hierarchy';

        const body = document.createElement('div');
        body.className = 'ro-body';

        const groups = {} as Record<SelectionKind, GroupEls>;
        for (const def of KIND_ORDER) {
            const sectionEl = document.createElement('section');
            sectionEl.className = 'ro-group';
            sectionEl.hidden = true;

            const titleEl = document.createElement('h3');
            titleEl.className = 'ro-group-title';
            const titleText = document.createElement('span');
            titleText.textContent = def.title;
            const countEl = document.createElement('span');
            countEl.className = 'ro-count';
            countEl.textContent = '0';
            titleEl.append(titleText, countEl);

            const listEl = document.createElement('div');
            listEl.className = 'ro-list';

            sectionEl.append(titleEl, listEl);
            body.append(sectionEl);
            groups[def.kind] = { kind: def.kind, sectionEl, countEl, listEl, rows: new Map(), sig: '' };
        }

        root.append(head, body);
        parent.appendChild(root);
        return { root, groups };
    }
}
