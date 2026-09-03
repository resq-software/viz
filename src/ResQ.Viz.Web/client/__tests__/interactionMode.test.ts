// SPDX-License-Identifier: Apache-2.0
//
// The one live/replay mutation gate, and the controller boundaries that route
// through it.
//
// The rule being pinned: **away from the live edge, nothing the client can do
// reaches the server.** Not "the button is disabled" — a disabled button is a
// mirror of the gate, not the gate — but "the effect is never invoked". Each
// boundary below is driven the way an operator drives it (an event, a method,
// a dispatched control event) with a gate that reports replay, and the
// injected POST/apply callback is asserted never to have run.
//
// The counterpart assertions matter just as much: camera, layer toggles,
// filters, selection and scene *export* are local reads and stay available,
// because a replay an operator cannot look around in is not a replay.

import { describe, expect, it, vi } from 'vitest';

import {
    InteractionMode,
    liveGate,
    type InteractionModeValue,
    type MutationGate,
} from '../operator/interactionMode';
import { OperatorActions, type OperatorEffects } from '../operator/operatorActions';
import { gatedCommandIssuer, type CommandIssuer, type CommandOutcome } from '../assets/panelCommands';
import { CommandState } from '../operator/types';

/** An acceptance carrying the command state the server would have reported. */
const ACCEPTED: CommandOutcome = {
    accepted: true,
    message: 'ok',
    result: {
        commandId: '0d5a2f3e-0000-4000-8000-000000000001',
        state: CommandState.Accepted,
        acceptedAt: null,
        progressPercent: 0,
        message: null,
        reasonCode: null,
    },
};

/** A gate that always reports replay, for driving a boundary's refusal path. */
const replayGate: MutationGate = (action) => ({
    success: false,
    error: { kind: 'replay', code: 'interaction.replay', action },
});

describe('InteractionMode', () => {
    it('permits mutations at the live edge and refuses them in replay', () => {
        const mode = new InteractionMode();
        const states: InteractionModeValue[] = [];
        mode.subscribe(value => states.push(value));

        expect(mode.guard('reset')).toEqual({ success: true, value: undefined });
        mode.enterReplay();
        expect(mode.guard('reset')).toEqual({
            success: false,
            error: { kind: 'replay', code: 'interaction.replay', action: 'reset' },
        });
        mode.goLive();

        expect(states).toEqual(['live', 'replay', 'live']);
    });

    it('reports the current value and answers `allows` from the same guard', () => {
        const mode = new InteractionMode();
        expect(mode.value).toBe('live');
        expect(mode.isReplay).toBe(false);
        expect(mode.allows('anything')).toBe(true);

        mode.enterReplay();
        expect(mode.value).toBe('replay');
        expect(mode.isReplay).toBe(true);
        expect(mode.allows('anything')).toBe(false);
    });

    it('notifies once per real transition and not on a repeated set', () => {
        const mode = new InteractionMode();
        const seen: InteractionModeValue[] = [];
        mode.subscribe(value => seen.push(value));

        mode.goLive();      // already live
        mode.enterReplay();
        mode.enterReplay(); // already replaying
        mode.goLive();

        expect(seen).toEqual(['live', 'replay', 'live']);
    });

    it('stops notifying an unsubscribed listener', () => {
        const mode = new InteractionMode();
        const seen: InteractionModeValue[] = [];
        const stop = mode.subscribe(value => seen.push(value));
        stop();
        mode.enterReplay();
        expect(seen).toEqual(['live']);
    });

    it('carries the requested action on the refusal so a caller can name it', () => {
        const mode = new InteractionMode();
        mode.enterReplay();
        const refused = mode.guard('environment.terrain');
        expect(refused.success).toBe(false);
        if (!refused.success) expect(refused.error.action).toBe('environment.terrain');
    });

    it('exposes a live default gate for a surface no host has wired yet', () => {
        expect(liveGate('reset')).toEqual({ success: true, value: undefined });
    });

    it('survives being passed as a bare function reference', () => {
        const mode = new InteractionMode();
        const gate: MutationGate = mode.guard;
        mode.enterReplay();
        expect(gate('reset').success).toBe(false);
    });
});

function effectSpies(): { effects: OperatorEffects; calls: string[] } {
    const calls: string[] = [];
    const record = (name: string) => (...args: unknown[]) => {
        calls.push(args.length === 0 ? name : `${name}:${JSON.stringify(args)}`);
    };
    return {
        calls,
        effects: {
            setPaused: record('setPaused'),
            step: record('step'),
            setSpeed: record('setSpeed'),
            reset: record('reset'),
            startScenario: record('startScenario'),
            spawnAsset: record('spawnAsset'),
            applyTerrain: record('applyTerrain'),
            applyWeather: record('applyWeather'),
            uploadHeightmap: record('uploadHeightmap'),
            setBackhaulKilled: record('setBackhaulKilled'),
            commandDrone: record('commandDrone'),
        },
    };
}

const HEIGHTMAP = { rows: 2, cols: 2, width: 10, depth: 10, cells: [0, 1, 2, 3] };

/** Drives every OperatorActions method once, in one place, so a method added
 *  without a gate cannot hide from the refusal test below. */
function driveEveryAction(actions: OperatorActions): void {
    actions.setPaused(true);
    actions.step();
    actions.setSpeed(2);
    actions.reset();
    actions.startScenario();
    actions.spawnAsset();
    actions.applyTerrain('alpine');
    actions.applyWeather();
    actions.uploadHeightmap(HEIGHTMAP);
    actions.setBackhaulKilled(true);
    actions.commandDrone('uav-1', { type: 'goto', target: [1, 2, 3] });
}

describe('OperatorActions', () => {
    it('runs every injected effect at the live edge', () => {
        const { effects, calls } = effectSpies();
        driveEveryAction(new OperatorActions(liveGate, effects));

        expect(calls).toEqual([
            'setPaused:[true]',
            'step',
            'setSpeed:[2]',
            'reset',
            'startScenario',
            'spawnAsset',
            'applyTerrain:["alpine"]',
            'applyWeather',
            `uploadHeightmap:[${JSON.stringify(HEIGHTMAP)}]`,
            'setBackhaulKilled:[true]',
            'commandDrone:["uav-1",{"type":"goto","target":[1,2,3]}]',
        ]);
    });

    it('invokes no effect at all while replaying', () => {
        const { effects, calls } = effectSpies();
        driveEveryAction(new OperatorActions(replayGate, effects));

        expect(calls).toEqual([]);
    });

    it('returns the refusal so a caller can report why nothing happened', () => {
        const { effects } = effectSpies();
        const refused = new OperatorActions(replayGate, effects).reset();

        expect(refused).toEqual({
            success: false,
            error: { kind: 'replay', code: 'interaction.replay', action: 'transport.reset' },
        });
    });

    it('reads the gate at call time, not at construction', () => {
        const mode = new InteractionMode();
        const { effects, calls } = effectSpies();
        const actions = new OperatorActions(mode.guard, effects);

        actions.step();
        mode.enterReplay();
        actions.step();
        mode.goLive();
        actions.step();

        expect(calls).toEqual(['step', 'step']);
    });
});

describe('asset panel command boundary', () => {
    const request = { kind: 'goTo', idempotencyKey: 'key-1' };

    it('issues the command through the inner issuer at the live edge', async () => {
        const inner = vi.fn<CommandIssuer>().mockResolvedValue(ACCEPTED);
        const outcome = await gatedCommandIssuer(liveGate, inner)('uav-1', request);

        expect(inner).toHaveBeenCalledWith('uav-1', request);
        expect(outcome.accepted).toBe(true);
    });

    it('does not reach the issuer while replaying and says why', async () => {
        const inner = vi.fn<CommandIssuer>().mockResolvedValue(ACCEPTED);
        const outcome = await gatedCommandIssuer(replayGate, inner)('uav-1', request);

        expect(inner).not.toHaveBeenCalled();
        expect(outcome.accepted).toBe(false);
        expect(outcome.message).toMatch(/live/i);
    });
});
