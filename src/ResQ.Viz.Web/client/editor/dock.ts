// ResQ Viz - Editor dock (managed, collapsible panel column)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import { GLOBAL_SHORTCUTS } from '../ui/globalShortcuts';
import { shouldIgnoreGlobalShortcut } from '../ui/hotkeys';

/** localStorage key persisting the collapsed state across reloads. */
const COLLAPSE_KEY = 'resq-viz-editor-collapsed';

/**
 * Hosts the editor panels (Outliner, Inspector) in one managed left column so
 * they stop hand-positioning themselves with `fixed` offsets. Provides a single
 * mount target via {@link EditorDock.host} and a collapse toggle (button +
 * `\` key, persisted) that hides the whole column without touching the panels'
 * own show/hide-on-selection logic.
 *
 * The column is a transparent flex container (`pointer-events: none`); each
 * mounted panel re-enables pointer events and carries its own surface styling.
 */
export class EditorDock {
    private readonly _leftHost: HTMLElement;
    private readonly _toggle: HTMLButtonElement;
    private _collapsed: boolean;

    constructor() {
        this._collapsed = localStorage.getItem(COLLAPSE_KEY) === 'true';
        const built = this._build();
        this._leftHost = built.leftHost;
        this._toggle = built.toggle;
        this._toggle.addEventListener('click', () => this._setCollapsed(!this._collapsed));
        this._bindKeyboard();
        this._apply();
    }

    /** Mount target for a left-column editor panel (Outliner, Inspector …). */
    host(): HTMLElement {
        return this._leftHost;
    }

    private _setCollapsed(v: boolean): void {
        this._collapsed = v;
        localStorage.setItem(COLLAPSE_KEY, String(v));
        this._apply();
    }

    private _apply(): void {
        document.body.classList.toggle('editor-collapsed', this._collapsed);
        this._toggle.setAttribute('aria-pressed', String(!this._collapsed));
        this._toggle.setAttribute('aria-label', this._collapsed ? 'Show editor panels' : 'Hide editor panels');
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', (e: KeyboardEvent) => {
            if (shouldIgnoreGlobalShortcut(e)) return;
            if (e.code === GLOBAL_SHORTCUTS.editorDock) {
                e.preventDefault();
                this._setCollapsed(!this._collapsed);
            }
        });
    }

    private _build(): { toggle: HTMLButtonElement; leftHost: HTMLElement } {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'resq-dock-toggle';
        toggle.textContent = '☰';
        document.body.appendChild(toggle);

        const leftHost = document.createElement('div');
        leftHost.className = 'resq-dock resq-dock--left';
        document.body.appendChild(leftHost);

        return { toggle, leftHost };
    }
}
