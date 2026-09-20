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
    /** The asset the CURRENT hold was begun against, or null when not holding. */
    let armed: string | null = null;
    let busy = false;

    function applySelection(): void {
        const current = selection.current;
        const next = current && current.kind === 'asset' ? current.id : null;
        // If a hold is armed against something else, end it HERE — the instant
        // the target moves — not at the far end of the hold. The subscriber knows
        // immediately; letting the operator keep pressing for the rest of the
        // 800ms while believing they are stopping a vehicle is the part of this
        // defect that actually costs time, and on this control time is the whole
        // point. Announced now, so they can re-arm against what they meant.
        if (armed !== null && next !== armed) {
            const wasArmed = armed;
            armed = null;
            hold.cancel();
            // Two different situations, and only one of them can be acted on by
            // holding again. With nothing selected this control is aria-disabled
            // and the arm-time guard refuses to start, so "hold again" would be
            // an instruction the console will not honour.
            announce(
                next === null
                    ? `Emergency stop cancelled: ${wasArmed} is no longer selected. `
                      + 'Select an asset, then hold again.'
                    : `Emergency stop cancelled: selection moved off ${wasArmed} while you were `
                      + `holding. Hold again to stop ${next}.`,
            );
        }
        target = next;
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

    async function fire(assetId: string): Promise<void> {
        if (busy) return;
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
            // Bind the target to the hold, not to the clock. `target` is mutable
            // and `selection.subscribe` rewrites it, so read only at confirm an
            // operator who armed this against drone A would stop drone B.
            //
            // It takes a CONCURRENT second input, not a stream frame: the v2
            // reconcile only re-kinds the same id or clears the selection, and
            // clearing drives aria-disabled true, which confirm already refuses
            // on. What reaches it is a second finger tapping a roster row while
            // the first holds the button (pointer capture suppresses nothing
            // there), or the bracket-cycle shortcut during a mouse hold on
            // browsers where mousedown does not focus a button. Narrow, and this
            // is the control where narrow is not an argument.
            armed = target;
            if (armed) announce('Hold to confirm emergency stop.');
        },
        onCancel: () => {
            armed = null;
            announce('Emergency stop cancelled.');
        },
        onProgress: (fraction) => button.style.setProperty('--hold-progress', String(fraction)),
        onConfirm: () => {
            const assetId = armed;
            armed = null;
            if (button.getAttribute('aria-disabled') === 'true') {
                announce(`Emergency stop unavailable: ${(target ? 'in flight' : NO_TARGET).toLowerCase()}.`);
                return;
            }
            if (assetId === null) {
                announce(`Emergency stop unavailable: ${NO_TARGET.toLowerCase()}.`);
                return;
            }
            // Refuse rather than guess. Acting on the armed asset would command
            // something the control no longer names; acting on the current one
            // would command something the operator never armed. Neither is what
            // they asked for, so it says what happened and makes them re-arm.
            // Backstop. `applySelection` cancels the hold the moment the target
            // moves, so reaching here means a swap the subscriber did not see.
            if (assetId !== target) {
                announce('Emergency stop cancelled: the selection changed while you were holding.');
                return;
            }
            void fire(assetId);
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
            armed = null;
            hold.destroy();
            unsubscribe();
            doc.removeEventListener('keydown', onKey);
        },
    };
}
