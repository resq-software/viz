// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the scene-descriptor (de)serialization. The SceneConfigPanel
// DOM/export/import is covered by E2E; this pins the validation + round-trip
// that decides whether an imported file is accepted and what it means.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
    applyScenarioForMode,
    parseSceneConfig,
    SceneConfigPanel,
    serializeSceneConfig,
    SCENE_CONFIG_VERSION,
    type SceneConfig,
    type SceneScenarioModeDependencies,
} from '../editor/sceneConfig';
import type { ApiFailure } from '../api';
import { ScenarioRuntime } from '../operator/ScenarioRuntime';

beforeEach(() => document.body.replaceChildren());
afterEach(() => vi.unstubAllGlobals());

describe('parseSceneConfig', () => {
    it('accepts a well-formed descriptor', () => {
        const cfg = parseSceneConfig({ version: 1, terrain: 'alpine', scenario: 'swarm-5' });
        expect(cfg).toEqual({ version: 1, terrain: 'alpine', scenario: 'swarm-5' });
    });

    it('normalises a missing/undefined scenario to null', () => {
        expect(parseSceneConfig({ version: 1, terrain: 'coastal' })?.scenario).toBeNull();
        expect(parseSceneConfig({ version: 1, terrain: 'coastal', scenario: null })?.scenario).toBeNull();
    });

    it('rejects non-objects', () => {
        for (const bad of [null, undefined, 42, 'x', []]) {
            expect(parseSceneConfig(bad)).toBeNull();
        }
    });

    it('rejects missing/typewrong required fields', () => {
        expect(parseSceneConfig({ terrain: 'alpine' })).toBeNull(); // no version
        expect(parseSceneConfig({ version: 1 })).toBeNull(); // no terrain
        expect(parseSceneConfig({ version: '1', terrain: 'alpine' })).toBeNull(); // version not number
        expect(parseSceneConfig({ version: 1, terrain: 5 })).toBeNull(); // terrain not string
        expect(parseSceneConfig({ version: 1, terrain: 'alpine', scenario: 7 })).toBeNull(); // scenario wrong type
    });
});

describe('serializeSceneConfig', () => {
    it('round-trips through parse', () => {
        const cfg: SceneConfig = { version: SCENE_CONFIG_VERSION, terrain: 'canyon', scenario: 'sar' };
        const json = serializeSceneConfig(cfg);
        expect(parseSceneConfig(JSON.parse(json))).toEqual(cfg);
    });

    it('emits pretty (indented) JSON', () => {
        const json = serializeSceneConfig({ version: 1, terrain: 'alpine', scenario: null });
        expect(json).toContain('\n');
    });
});

function modeDeps(overrides: Partial<SceneScenarioModeDependencies> = {}): SceneScenarioModeDependencies {
    return {
        mode: () => 'v2',
        v2ScenarioNames: () => ['single', 'flood-response'],
        v2Session: () => ({ assetCount: 0, tick: 0 }),
        confirmV2Replace: vi.fn().mockReturnValue(true),
        legacyScenarioNames: () => ['single', 'swarm-5'],
        startV2: vi.fn().mockResolvedValue({
            success: true,
            value: {
                current: { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 2 },
            },
        }),
        startLegacy: vi.fn().mockResolvedValue(true),
        onLegacyStarted: vi.fn(),
        ...overrides,
    };
}

describe('mode-aware imported scenarios', () => {
    it('validates against canonical server names and starts v2 without a legacy event', async () => {
        const deps = modeDeps();

        const result = await applyScenarioForMode('FLOOD-RESPONSE', deps);

        expect(result).toEqual({ success: true });
        expect(deps.startV2).toHaveBeenCalledWith('flood-response');
        expect(deps.startLegacy).not.toHaveBeenCalled();
        expect(deps.onLegacyStarted).not.toHaveBeenCalled();
    });

    it('refuses an unknown or unavailable v2 catalog without falling back to legacy', async () => {
        const startV2 = vi.fn();
        const missing = await applyScenarioForMode('not-configured', modeDeps({ startV2 }));
        const unavailable = await applyScenarioForMode('single', modeDeps({
            startV2,
            v2ScenarioNames: () => null,
        }));

        expect(missing).toMatchObject({ success: false, code: 'scenario.notFound' });
        expect(unavailable).toMatchObject({ success: false, code: 'scenario.catalogUnavailable' });
        expect(startV2).not.toHaveBeenCalled();
    });

    it.each([
        { label: 'populated', assetCount: 1, tick: 0 },
        { label: 'progressed', assetCount: 0, tick: 1 },
    ])('cancels a $label v2 replacement without issuing its POST', async ({ assetCount, tick }) => {
        const startV2 = vi.fn();
        const confirmV2Replace = vi.fn().mockReturnValue(false);

        const result = await applyScenarioForMode('flood-response', modeDeps({
            startV2,
            v2Session: () => ({ assetCount, tick }),
            confirmV2Replace,
        }));

        expect(confirmV2Replace).toHaveBeenCalledWith('flood-response');
        expect(startV2).not.toHaveBeenCalled();
        expect(result).toMatchObject({ success: false, code: 'scenario.cancelled' });
    });

    it('does not confirm an empty, unprogressed v2 room', async () => {
        const confirmV2Replace = vi.fn().mockReturnValue(false);
        const startV2 = vi.fn().mockResolvedValue({
            success: true,
            value: {
                current: { name: 'single', startedAtSimulationSeconds: 0, revision: 2 },
            },
        });

        expect(await applyScenarioForMode('single', modeDeps({ startV2, confirmV2Replace })))
            .toEqual({ success: true });
        expect(confirmV2Replace).not.toHaveBeenCalled();
        expect(startV2).toHaveBeenCalledOnce();
    });

    it('does not POST when v2 ownership changes while confirmation is open', async () => {
        let mode: 'v2' | 'legacy' = 'v2';
        const fetchMock = vi.fn();
        vi.stubGlobal('fetch', fetchMock);
        const runtime = new ScenarioRuntime({ onPresent: vi.fn() });

        const result = await applyScenarioForMode('flood-response', modeDeps({
            mode: () => mode,
            runtime,
            startV2: undefined,
            v2Session: () => ({ assetCount: 1, tick: 0 }),
            confirmV2Replace: () => {
                mode = 'legacy';
                return true;
            },
        }));

        expect(fetchMock).not.toHaveBeenCalled();
        expect(runtime.requestInFlight).toBe(false);
        expect(result).toMatchObject({
            success: false,
            code: 'scenario.consoleUnavailable',
        });
    });

    it('retains typed v2 code and detail for accessible presentation', async () => {
        const failure: ApiFailure = {
            kind: 'problem',
            problem: {
                status: 409,
                code: 'scenario.replacementFailed',
                reasonCode: 'scenario.populationChanged',
                title: 'Not started',
                detail: 'The current session was preserved.',
                traceId: null,
                errors: [],
            },
        };

        const result = await applyScenarioForMode('flood-response', modeDeps({
            startV2: vi.fn().mockResolvedValue({ success: false, error: failure }),
        }));

        expect(result).toEqual({
            success: false,
            code: 'scenario.populationChanged',
            detail: 'The current session was preserved.',
        });
    });

    it('keeps legacy validation and dispatches its event only after v1 success', async () => {
        const onLegacyStarted = vi.fn();
        const startLegacy = vi.fn()
            .mockResolvedValueOnce(false)
            .mockResolvedValueOnce(true);
        const deps = modeDeps({ mode: () => 'legacy', startLegacy, onLegacyStarted });

        expect(await applyScenarioForMode('swarm-5', deps)).toMatchObject({ success: false });
        expect(onLegacyStarted).not.toHaveBeenCalled();
        expect(await applyScenarioForMode('swarm-5', deps)).toEqual({ success: true });
        expect(onLegacyStarted).toHaveBeenCalledWith('swarm-5');
    });

    it.each(['v2', 'booting'] as const)(
        'does not publish a legacy start when %s takes ownership before POST returns',
        async nextMode => {
            let mode: 'legacy' | 'v2' | 'booting' = 'legacy';
            let resolve!: (value: boolean) => void;
            const response = new Promise<boolean>(done => { resolve = done; });
            const onLegacyStarted = vi.fn();
            const pending = applyScenarioForMode('swarm-5', modeDeps({
                mode: () => mode,
                startLegacy: vi.fn(() => response),
                onLegacyStarted,
            }));

            mode = nextMode;
            resolve(true);
            const result = await pending;

            expect(onLegacyStarted).not.toHaveBeenCalled();
            expect(result).toMatchObject({
                success: false,
                code: 'scenario.consoleUnavailable',
            });
        },
    );

    it('renders a returned scenario failure in an accessible panel status', async () => {
        const panel = new SceneConfigPanel({
            getTerrain: () => 'alpine',
            getScenario: () => 'single',
            applyTerrain: vi.fn(),
            applyScenario: vi.fn().mockResolvedValue({
                success: false,
                code: 'scenario.replacementFailed',
                detail: 'The current session was preserved.',
            }),
        });
        const input = document.querySelector<HTMLInputElement>('.resq-scenecfg input[type="file"]')!;
        Object.defineProperty(input, 'files', {
            configurable: true,
            value: [new File([
                JSON.stringify({ version: 1, terrain: 'alpine', scenario: 'flood-response' }),
            ], 'scene.json', { type: 'application/json' })],
        });

        input.dispatchEvent(new Event('change'));

        await vi.waitFor(() => {
            const status = document.querySelector<HTMLElement>('.scfg-status')!;
            expect(status.getAttribute('role')).toBe('alert');
            expect(status.classList.contains('operator-resource-error')).toBe(true);
            expect(status.textContent).toContain('scenario.replacementFailed');
            expect(status.textContent).toContain('The current session was preserved.');
        });
    });
});
