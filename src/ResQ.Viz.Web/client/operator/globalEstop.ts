// ResQ Viz — the always-mounted emergency stop
// SPDX-License-Identifier: Apache-2.0
//
// `design/VEHICLE_DASHBOARDS.md` §4.7:
//
//   "E-stop is always mounted, always reachable, never behind a tab, and
//    keyboard-addressable from anywhere in the shell."
//
// viz's only emergency stop used to live inside the selected-asset panel, which
// fails every clause of that: it is behind a selection, it sits in a panel the
// responsive rules retire, and it is not reachable by keyboard from the rest of
// the shell.
//
// SCOPE — this control commands the SELECTED asset. It is not a fleet-wide
// stop-all: viz's command API is per-asset (`POST /api/v2/sim/assets/{id}/
// commands`), a bulk stop would need partial-failure semantics that do not exist
// yet, and inventing those on the most dangerous control in the application is
// not something to do quietly. With nothing selected the control stays mounted
// and visible but refuses, naming the reason — the same discipline the asset
// panel's own gates use, rather than hiding and reappearing.
//
// KEYBOARD — the shortcut moves FOCUS to the control; it does not fire it. A
// single keystroke that stops a vehicle would undo the hold-to-confirm guard
// that §4.7 requires in the same breath.

import { holdToConfirm, type HoldHandle } from '../assets/holdToConfirm';
import { newIdempotencyKey, postAssetCommand, type CommandIssuer } from '../assets/panelCommands';
import type { SelectionStore } from '../editor/selection';
import { getLogger } from '../log';

const log = getLogger('estop');

/** Shown when there is nothing to stop. Names the fact, not the rule. */
const NO_TARGET = 'No asset selected';

export interface GlobalEstopOptions {
    readonly button: HTMLButtonElement;
    readonly selection: SelectionStore;
    /** Injectable so a test needs no server. */
    readonly issue?: CommandIssuer;
    /** Routed to an existing live region rather than creating another one. */
    readonly announce?: (message: string) => void;
    /** Overridable so a test need not wait out the real guard. */
    readonly holdMs?: number;
    /** The document the shortcut binds on. Defaults to the button's own. */
    readonly doc?: Document;
}

export interface GlobalEstopHandle {
    destroy(): void;
    /** The asset the control would act on, or null. Exposed for tests. */
    readonly target: string | null;
}

/**
 * Mounts the shell-level emergency stop.
 *
 * Returns a handle so the host can tear it down; the shortcut listener is on the
 * document, so leaking it would outlive a re-render.
 */
export function mountGlobalEstop(options: GlobalEstopOptions): GlobalEstopHandle {
    const { button, selection } = options;
    const issue = options.issue ?? postAssetCommand;
    const doc = options.doc ?? button.ownerDocument;
    const announce = options.announce ?? ((message: string): void => { log.info(message); });

    let target: string | null = null;
    let busy = false;

    function applySelection(): void {
        const current = selection.current;
        target = current && current.kind === 'asset' ? current.id : null;
        const enabled = target !== null && !busy;
        // aria-disabled rather than `disabled`: the control keeps its place in the
        // tab order, so a keyboard operator reaching for it in an emergency finds
        // it where it always is, and hears why it refuses.
        button.setAttribute('aria-disabled', enabled ? 'false' : 'true');
        button.classList.toggle('is-blocked', !enabled);
        const reason = busy ? 'Emergency stop in flight' : NO_TARGET;
        button.title = enabled ? `Emergency stop ${target}` : reason;
        button.setAttribute(
            'aria-label',
            enabled
                ? `Emergency stop ${target} — hold to confirm`
                : `Emergency stop — ${reason.toLowerCase()}`,
        );
    }

    async function fire(): Promise<void> {
        if (busy) return;
        const assetId = target;
        if (!assetId) {
            announce(`Emergency stop unavailable: ${NO_TARGET.toLowerCase()}.`);
            return;
        }
        busy = true;
        applySelection();
        try {
            const outcome = await issue(assetId, {
                kind: 'emergencyStop',
                idempotencyKey: newIdempotencyKey(),
            });
            // Reports what the server said, never "stopped": transport acceptance
            // is not physical completion, and the asset's motion comes from the
            // stream. Same wording discipline as the asset panel.
            announce(outcome.message);
        } catch (err: unknown) {
            log.error('emergency stop failed to send', err);
            announce('Emergency stop failed to send.');
        } finally {
            busy = false;
            applySelection();
        }
    }

    const hold: HoldHandle = holdToConfirm(button, {
        holdMs: options.holdMs,
        onStart: () => {
            if (target) announce('Hold to confirm emergency stop.');
        },
        onCancel: () => announce('Emergency stop cancelled.'),
        onProgress: (fraction) => button.style.setProperty('--hold-progress', String(fraction)),
        onConfirm: () => {
            if (button.getAttribute('aria-disabled') === 'true') {
                announce(`Emergency stop unavailable: ${(target ? 'in flight' : NO_TARGET).toLowerCase()}.`);
                return;
            }
            void fire();
        },
    });

    /** Shift+Escape moves focus here from anywhere. It deliberately does not fire. */
    const onKey = (event: Event): void => {
        const key = event as KeyboardEvent;
        if (key.key !== 'Escape' || !key.shiftKey) return;
        event.preventDefault();
        button.focus();
        announce(
            target
                ? `Emergency stop focused for ${target}. Hold to confirm.`
                : `Emergency stop focused. ${NO_TARGET}.`,
        );
    };

    doc.addEventListener('keydown', onKey);
    const unsubscribe = selection.subscribe(() => applySelection());
    applySelection();

    return {
        get target(): string | null { return target; },
        destroy(): void {
            hold.destroy();
            unsubscribe();
            doc.removeEventListener('keydown', onKey);
        },
    };
}
