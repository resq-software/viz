// ResQ Viz - Editor dock (managed panel column inside the Editor workspace)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import { GLOBAL_SHORTCUTS } from '../ui/globalShortcuts';
import { shouldIgnoreGlobalShortcut } from '../ui/hotkeys';

/**
 * Hosts the editor panels (Outliner, Inspector) in one managed column inside
 * the Editor workspace, so they stop hand-positioning themselves with `fixed`
 * offsets. Provides a single mount target via {@link EditorDock.host}.
 *
 * The column carries a section disclosure — a labelled button in its own header
 * plus the `\` key — that folds the panels away without touching their
 * show/hide-on-selection logic. It is a *section* control: the header and its
 * button stay visible while collapsed, so the fold is always reversible by
 * mouse as well as by key. The whole-workspace show/hide belongs to the one
 * top-bar Editor toggle and lives nowhere else, and nothing here is persisted —
 * every newly opened app session starts expanded.
 */
export class EditorDock {
    private readonly _root: HTMLElement;
    private readonly _panels: HTMLElement;
    private readonly _toggle: HTMLButtonElement;
    private _collapsed = false;

    constructor(mount: HTMLElement = document.body) {
        const built = this._build(mount);
        this._root = built.root;
        this._panels = built.panels;
        this._toggle = built.toggle;
        this._toggle.addEventListener('click', () => this.setCollapsed(!this._collapsed));
        this._bindKeyboard();
        this._apply();
    }

    /** Mount target for a column editor panel (Outliner, Inspector …). */
    host(): HTMLElement {
        return this._panels;
    }

    /** The column element, so the workspace can place it. */
    get element(): HTMLElement {
        return this._root;
    }

    /** Whether the panel section is folded away. */
    get isCollapsed(): boolean {
        return this._collapsed;
    }

    /** Fold or unfold the panel section. */
    setCollapsed(collapsed: boolean): void {
        this._collapsed = collapsed;
        this._apply();
    }

    private _apply(): void {
        document.body.classList.toggle('editor-collapsed', this._collapsed);
        this._panels.hidden = this._collapsed;
        this._toggle.setAttribute('aria-pressed', String(!this._collapsed));
        this._toggle.setAttribute('aria-expanded', String(!this._collapsed));
        this._toggle.setAttribute(
            'aria-label', this._collapsed ? 'Show editor panels' : 'Hide editor panels',
        );
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', (e: KeyboardEvent) => {
            if (shouldIgnoreGlobalShortcut(e)) return;
            if (e.code === GLOBAL_SHORTCUTS.editorDock) {
                e.preventDefault();
                this.setCollapsed(!this._collapsed);
            }
        });
    }

    private _build(mount: HTMLElement): {
        root: HTMLElement;
        panels: HTMLElement;
        toggle: HTMLButtonElement;
    } {
        const doc = mount.ownerDocument;
        const root = doc.createElement('div');
        root.className = 'resq-dock resq-dock--left';

        const head = doc.createElement('div');
        head.className = 'resq-dock-head';
        const label = doc.createElement('span');
        label.className = 'resq-dock-label';
        label.textContent = 'Panels';
        const toggle = doc.createElement('button');
        toggle.type = 'button';
        toggle.className = 'resq-dock-collapse';
        toggle.textContent = '☰';
        head.append(label, toggle);

        const panels = doc.createElement('div');
        panels.className = 'resq-dock-panels';

        root.append(head, panels);
        mount.appendChild(root);
        return { root, panels, toggle };
    }
}
