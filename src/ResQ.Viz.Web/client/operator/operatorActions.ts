// ResQ Viz - the gated operator command surface app.ts mutates the world through
// SPDX-License-Identifier: Apache-2.0
//
// `app.ts` is the integration root: it owns the renderer, the SignalR
// connection and every pointer/keyboard handler, and it cannot be imported
// under a test runner. That made it the one place where a mutation could
// quietly skip a gate, because nothing could drive it.
//
// This is the seam that fixes that. Each method is one operator action; each
// consults {@link MutationGate} immediately before invoking the injected
// effect, and returns the refusal when it does not. `app.ts` supplies the
// effects — the actual POSTs, the actual dialog openers — so the handlers up
// there become one-line calls that a source-level test can check, and the
// behaviour they share is tested here against injected spies.
//
// Effects are deliberately `void`-returning commands. A gate answers "may
// this happen", not "what did the server say", and a method whose result mixed
// the two would push a local refusal into call sites typed on `ApiFailure`,
// where it would have to be disguised as a server response to fit.

import type { Result } from '../api';
import { getLogger } from '../log';
import type { InteractionRefusal, MutationGate } from './interactionMode';

const log = getLogger('operator-actions');

/** A decoded DEM on its way to the physics engine. */
export interface HeightmapUpload {
    readonly rows: number;
    readonly cols: number;
    readonly width: number;
    readonly depth: number;
    readonly cells: readonly number[];
}

/** A v1 air-domain drone command body (`goto`, `hover`, `rtl`, `land`, …). */
export interface DroneCommandBody {
    readonly type: string;
    readonly target?: readonly [number, number, number];
    readonly yaw?: number;
}

/**
 * What each action actually does once the gate has allowed it.
 *
 * Every member has a caller in `app.ts`. An effect with no call site is a
 * policy nobody enforces, so this interface grows only alongside a real one.
 */
export interface OperatorEffects {
    /** Pause or resume the running simulation. */
    readonly setPaused: (paused: boolean) => void;
    /** Advance the paused simulation by a single frame. */
    readonly step: () => void;
    /** Set the run-speed multiplier. */
    readonly setSpeed: (factor: number) => void;
    /** Reset the simulation, in whichever mode owns the console. */
    readonly reset: () => void;
    /** Offer the scenario catalog. Starting one replaces world state, so the
     *  surface that offers it is gated exactly like the start itself. */
    readonly startScenario: () => void;
    /** Offer the multi-domain spawn form. */
    readonly spawnAsset: () => void;
    /** Apply a terrain preset to the scene and the physics engine. */
    readonly applyTerrain: (key: string) => void;
    /** Offer the environment form (terrain + weather). */
    readonly applyWeather: () => void;
    /** Ship a decoded heightmap to the backend. */
    readonly uploadHeightmap: (upload: HeightmapUpload) => void;
    /** Flip the simulated backhaul uplink. */
    readonly setBackhaulKilled: (killed: boolean) => void;
    /** Send one v1 drone command — click-to-goto, WASD nudge, gizmo release and
     *  the Inspector buttons all land here, so those four paths cannot drift
     *  apart about when a command is allowed. */
    readonly commandDrone: (droneId: string, command: DroneCommandBody) => void;
}

/** What an attempted action reports back: nothing on success, the local
 *  refusal when the gate said no. */
export type OperatorActionResult = Result<void, InteractionRefusal>;

/** The gated operator actions. One instance per page, over one gate. */
export class OperatorActions {
    private readonly _gate: MutationGate;
    private readonly _effects: OperatorEffects;

    constructor(gate: MutationGate, effects: OperatorEffects) {
        this._gate = gate;
        this._effects = effects;
    }

    setPaused(paused: boolean): OperatorActionResult {
        return this._run('transport.pause', () => this._effects.setPaused(paused));
    }

    step(): OperatorActionResult {
        return this._run('transport.step', () => this._effects.step());
    }

    setSpeed(factor: number): OperatorActionResult {
        return this._run('transport.speed', () => this._effects.setSpeed(factor));
    }

    reset(): OperatorActionResult {
        return this._run('transport.reset', () => this._effects.reset());
    }

    startScenario(): OperatorActionResult {
        return this._run('scenario.start', () => this._effects.startScenario());
    }

    spawnAsset(): OperatorActionResult {
        return this._run('asset.spawn', () => this._effects.spawnAsset());
    }

    applyTerrain(key: string): OperatorActionResult {
        return this._run('environment.terrain', () => this._effects.applyTerrain(key));
    }

    applyWeather(): OperatorActionResult {
        return this._run('environment.weather', () => this._effects.applyWeather());
    }

    uploadHeightmap(upload: HeightmapUpload): OperatorActionResult {
        return this._run('environment.heightmap', () => this._effects.uploadHeightmap(upload));
    }

    setBackhaulKilled(killed: boolean): OperatorActionResult {
        return this._run('mesh.backhaul', () => this._effects.setBackhaulKilled(killed));
    }

    commandDrone(droneId: string, command: DroneCommandBody): OperatorActionResult {
        return this._run('drone.command', () => this._effects.commandDrone(droneId, command));
    }

    /** Ask the gate, then act. The gate is read here rather than cached at
     *  construction, so an instance built at boot follows the mode for the life
     *  of the page. */
    private _run(action: string, effect: () => void): OperatorActionResult {
        const allowed = this._gate(action);
        if (!allowed.success) {
            log.info('operator action refused away from the live edge', { action });
            return allowed;
        }
        effect();
        return allowed;
    }
}
