// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the gizmo's pure altitude-clamp and for the one place the
// handles turn into a server mutation.
//
// The clamp decides *what* target is commanded — that a handle dragged below
// ground still yields a valid above-ground go-to. The gate decides *whether*
// one is commanded at all: away from the live edge a drag is a local gesture
// over a recording and must not reach the drone. Both are driven here; the
// WebGL rendering of the handles is still covered by the visual run pass.

import { describe, expect, it, vi } from 'vitest';
import * as THREE from 'three';

import { clampGotoAltitude, GIZMO_LAYER, MIN_GOTO_ALTITUDE, TransformGizmo } from '../editor/gizmo';
import { SelectionStore } from '../editor/selection';
import type { MutationGate } from '../operator/interactionMode';

describe('clampGotoAltitude', () => {
    it('passes through x and z unchanged', () => {
        const [x, , z] = clampGotoAltitude({ x: 12.5, y: 40, z: -7.5 });
        expect(x).toBe(12.5);
        expect(z).toBe(-7.5);
    });

    it('keeps altitude above the floor', () => {
        expect(clampGotoAltitude({ x: 0, y: 40, z: 0 })[1]).toBe(40);
    });

    it('floors a below-ground altitude to the minimum', () => {
        expect(clampGotoAltitude({ x: 0, y: -5, z: 0 })[1]).toBe(MIN_GOTO_ALTITUDE);
        expect(clampGotoAltitude({ x: 0, y: 0, z: 0 })[1]).toBe(MIN_GOTO_ALTITUDE);
    });

    it('honours a custom minimum', () => {
        expect(clampGotoAltitude({ x: 0, y: 2, z: 0 }, 10)[1]).toBe(10);
        expect(clampGotoAltitude({ x: 0, y: 25, z: 0 }, 10)[1]).toBe(25);
    });
});

const REPLAY_GATE: MutationGate = (action) => ({
    success: false,
    error: { kind: 'replay', code: 'interaction.replay', action },
});

interface Harness {
    readonly gizmo: TransformGizmo;
    readonly store: SelectionStore;
    readonly sendGoto: ReturnType<typeof vi.fn>;
    readonly cameraEnabled: () => boolean;
    /** Replays the three events a real handle drag emits, in order. */
    readonly drag: () => void;
}

function harness(gate?: MutationGate): Harness {
    const store = new SelectionStore();
    const sendGoto = vi.fn();
    let cameraEnabled = true;
    const gizmo = new TransformGizmo({
        scene: new THREE.Scene(),
        camera: new THREE.PerspectiveCamera(),
        domElement: document.createElement('div'),
        store,
        setCameraEnabled: (enabled) => { cameraEnabled = enabled; },
        getDronePosition: () => new THREE.Vector3(10, 20, 30),
        sendGoto,
        addTick: () => {},
        ...(gate === undefined ? {} : { gate }),
    });
    // TransformControls is private to the gizmo on purpose — nothing outside it
    // should attach handles — so the drag is replayed through the same control
    // the real pointer events reach.
    const control = (gizmo as unknown as { _control: THREE.EventDispatcher })._control;
    return {
        gizmo,
        store,
        sendGoto,
        cameraEnabled: () => cameraEnabled,
        drag: () => {
            control.dispatchEvent({ type: 'mouseDown' } as never);
            control.dispatchEvent({ type: 'objectChange' } as never);
            control.dispatchEvent({ type: 'mouseUp' } as never);
        },
    };
}

describe('TransformGizmo camera layer', () => {
    it('enables its own helper layer on the camera it was handed', () => {
        // The handles live on a dedicated layer so the FPV picture-in-picture
        // (layer 0 only) never renders them. That used to be switched on beside
        // the construction site in app.ts, which stopped being possible when the
        // gizmo moved behind the lazily-loaded Editor workspace: app.ts would
        // have had to import GIZMO_LAYER statically and drag TransformControls
        // into the entry chunk. Handles rendered by no camera are handles that
        // do not exist.
        const camera = new THREE.PerspectiveCamera();
        expect(camera.layers.isEnabled(GIZMO_LAYER)).toBe(false);

        new TransformGizmo({
            scene: new THREE.Scene(),
            camera,
            domElement: document.createElement('div'),
            store: new SelectionStore(),
            setCameraEnabled: () => {},
            getDronePosition: () => null,
            sendGoto: vi.fn(),
            addTick: () => {},
        });

        expect(camera.layers.isEnabled(GIZMO_LAYER)).toBe(true);
    });
});

describe('TransformGizmo replay gate', () => {
    it('commands a go-to on release at the live edge', () => {
        const h = harness();
        h.store.set('drone', 'uav-1');
        h.gizmo.toggleMoveMode();

        h.drag();

        expect(h.sendGoto).toHaveBeenCalledOnce();
        expect(h.sendGoto.mock.calls[0]?.[0]).toEqual([10, 20, 30]);
    });

    it('sends no command for a drag performed while replaying', () => {
        const h = harness(REPLAY_GATE);
        h.store.set('drone', 'uav-1');
        h.gizmo.setMoveMode(true);

        h.drag();

        expect(h.sendGoto).not.toHaveBeenCalled();
    });

    it('hands the orbit camera back even when the release is refused', () => {
        // A refusal that left the camera disabled would strand the operator in a
        // scene they cannot look around — the whole point of staying in replay.
        const h = harness(REPLAY_GATE);
        h.store.set('drone', 'uav-1');
        h.gizmo.setMoveMode(true);

        h.drag();

        expect(h.cameraEnabled()).toBe(true);
        expect(h.gizmo.isInteracting).toBe(false);
    });

    it('does not offer move handles it would refuse to act on', () => {
        const h = harness(REPLAY_GATE);
        h.store.set('drone', 'uav-1');

        expect(h.gizmo.toggleMoveMode()).toBe(false);
        h.gizmo.setMoveMode(true);
        expect(h.gizmo.isMoveMode).toBe(false);
    });

    it('leaves selection changes working while replaying', () => {
        const h = harness(REPLAY_GATE);
        h.store.set('drone', 'uav-1');
        h.store.set('drone', 'uav-2');

        expect(h.store.current).toEqual({ kind: 'drone', id: 'uav-2' });
    });
});
