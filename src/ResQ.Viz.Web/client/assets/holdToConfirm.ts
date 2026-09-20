// ResQ Viz — press-and-hold confirmation for destructive commands
// SPDX-License-Identifier: Apache-2.0
//
// `design/VEHICLE_DASHBOARDS.md` §4.7:
//
//   "Guarded actions. Destructive commands (disarm in flight, E-stop) use
//    hold-to-confirm rather than a dialog — a dialog trains people to click
//    through."
//
// So this is deliberately NOT a confirm dialog. A dialog becomes muscle memory;
// a hold cannot, because the operator's hand has to stay down for the whole
// duration and any release abandons it.
//
// The guarded control keeps its own `click` handler OFF — see `AssetPanel`. That
// is the point: a single press must not be able to issue an emergency stop, and
// leaving the click path attached "just in case" would defeat the whole guard.

/** How long the press must be held. Long enough to be deliberate, short enough
 *  that an operator who means it is not fighting the UI in an emergency. */
export const HOLD_MS = 800;

/** Keys that activate a `<button>`, and which therefore have to be intercepted:
 *  left alone, the browser turns them into a plain click. */
const ACTIVATION_KEYS = new Set([' ', 'Spacebar', 'Enter']);

export interface HoldOptions {
    /** Fired once, when the hold completes. */
    readonly onConfirm: () => void;
    /** 0..1 as the hold advances; called with 0 on cancel. Drives the fill. */
    readonly onProgress?: (fraction: number) => void;
    /** Fired when a started hold is abandoned before completing. */
    readonly onCancel?: () => void;
    /** Fired when a hold begins, for announcing "keep holding". */
    readonly onStart?: () => void;
    /** Overridable so tests do not have to wait 800ms. */
    readonly holdMs?: number;
}

export interface HoldHandle {
    /** Detaches every listener and cancels any hold in flight. */
    destroy(): void;
    /**
     * Abandons a hold in flight, as though the operator had released.
     *
     * For the caller that knows something the button cannot see — the emergency
     * stop learns from its selection store the instant its target changes, and
     * must not let the operator keep pressing against an asset it is no longer
     * willing to stop.
     */
    cancel(): void;
    /** True while a hold is running — exposed for tests and re-render guards. */
    readonly holding: boolean;
}

/**
 * Makes `button` require a sustained press before `onConfirm` fires.
 *
 * Works for pointer and keyboard alike, because §4.7 also requires E-stop to be
 * keyboard-addressable; a guard that only understood the mouse would put the
 * keyboard operator back on a single-keystroke emergency stop.
 */
export function holdToConfirm(button: HTMLElement, options: HoldOptions): HoldHandle {
    const holdMs = options.holdMs ?? HOLD_MS;
    const view = button.ownerDocument.defaultView;

    let raf = 0;
    let startedAt = 0;
    let holding = false;
    let keyHeld = false;

    function now(): number {
        return view?.performance?.now() ?? 0;
    }

    function stop(cancelled: boolean): void {
        if (!holding) return;
        holding = false;
        keyHeld = false;
        if (raf && view) view.cancelAnimationFrame(raf);
        raf = 0;
        button.removeAttribute('data-holding');
        options.onProgress?.(0);
        if (cancelled) options.onCancel?.();
    }

    function tick(at: number): void {
        if (!holding) return;
        const fraction = Math.min(1, (at - startedAt) / holdMs);
        options.onProgress?.(fraction);
        if (fraction >= 1) {
            // Clear state BEFORE the callback: onConfirm re-renders the command
            // list, and a stale `holding` would leave the fill stuck.
            stop(false);
            options.onConfirm();
            return;
        }
        raf = view?.requestAnimationFrame(tick) ?? 0;
    }

    /** A control that is refusing may not be armed. */
    function refusing(): boolean {
        return button.getAttribute('aria-disabled') === 'true';
    }

    function start(): void {
        if (holding) return;
        // Checked at ARM time, not only at confirm. Both callers re-check before
        // acting, but availability can flip from blocked to allowed during the
        // hold — the asset panel recomputes it on every stream frame — and the
        // confirm-time check then passes for a hold the operator began against a
        // control that was visibly refusing. Whatever they were agreeing to, it
        // was not this.
        if (refusing()) return;
        holding = true;
        startedAt = now();
        button.setAttribute('data-holding', 'true');
        options.onStart?.();
        raf = view?.requestAnimationFrame(tick) ?? 0;
    }

    const onPointerDown = (event: Event): void => {
        const pointer = event as PointerEvent;
        // Secondary buttons do not arm a destructive command.
        if (pointer.button !== undefined && pointer.button !== 0) return;
        // Keep receiving this pointer's events even if the finger slides off, so
        // `pointerup` outside still cancels rather than stranding the hold.
        const target = button as Element & { setPointerCapture?: (id: number) => void };
        if (typeof target.setPointerCapture === 'function' && pointer.pointerId !== undefined) {
            try { target.setPointerCapture(pointer.pointerId); } catch { /* not capturable */ }
        }
        start();
    };

    const onPointerEnd = (): void => stop(true);

    const onKeyDown = (event: Event): void => {
        const key = event as KeyboardEvent;
        if (!ACTIVATION_KEYS.has(key.key)) return;
        // Stops the browser synthesising a click from Space/Enter, which would
        // bypass the hold entirely.
        event.preventDefault();
        if (key.repeat) return;
        keyHeld = true;
        start();
    };

    const onKeyUp = (event: Event): void => {
        if (!ACTIVATION_KEYS.has((event as KeyboardEvent).key)) return;
        event.preventDefault();
        if (keyHeld) stop(true);
    };

    /** Losing focus mid-hold abandons it: the operator is no longer on the control. */
    const onBlur = (): void => stop(true);

    // And if the control starts refusing DURING a hold, the hold dies with it.
    // Without this the operator keeps pressing against a button that has already
    // changed its mind, and finds out only when the confirm is refused.
    const watchDisabled = view && typeof view.MutationObserver === 'function'
        ? new view.MutationObserver(() => { if (holding && refusing()) stop(true); })
        : null;
    watchDisabled?.observe(button, { attributes: true, attributeFilter: ['aria-disabled'] });

    button.addEventListener('pointerdown', onPointerDown);
    button.addEventListener('pointerup', onPointerEnd);
    button.addEventListener('pointercancel', onPointerEnd);
    button.addEventListener('pointerleave', onPointerEnd);
    button.addEventListener('keydown', onKeyDown);
    button.addEventListener('keyup', onKeyUp);
    button.addEventListener('blur', onBlur);

    return {
        get holding(): boolean { return holding; },
        cancel(): void { stop(true); },
        destroy(): void {
            stop(false);
            watchDisabled?.disconnect();
            button.removeEventListener('pointerdown', onPointerDown);
            button.removeEventListener('pointerup', onPointerEnd);
            button.removeEventListener('pointercancel', onPointerEnd);
            button.removeEventListener('pointerleave', onPointerEnd);
            button.removeEventListener('keydown', onKeyDown);
            button.removeEventListener('keyup', onKeyUp);
            button.removeEventListener('blur', onBlur);
        },
    };
}
