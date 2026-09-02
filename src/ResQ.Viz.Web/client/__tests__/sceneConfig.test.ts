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

function selectSceneFile(input: HTMLInputElement, contents: string): void {
    Object.defineProperty(input, 'files', {
        configurable: true,
        value: [new File([contents], 'scene.json', { type: 'application/json' })],
    });
    input.dispatchEvent(new Event('change'));
}

function sceneJson(terrain: string, scenario: string | null = 'flood-response'): string {
    return JSON.stringify({ version: 1, terrain, scenario });
}

describe('SceneConfigPanel import transaction', () => {
    function panelHarness(overrides: Partial<ConstructorParameters<typeof SceneConfigPanel>[0]> = {}) {
        const applyTerrain = vi.fn();
        const applyScenario = vi.fn().mockResolvedValue({ success: true });
        const panel = new SceneConfigPanel({
            getTerrain: () => 'alpine',
            getScenario: () => 'single',
            canApplyTerrain: () => true,
            applyTerrain,
            applyScenario,
            ...overrides,
        });
        const root = document.querySelector<HTMLElement>('.resq-scenecfg')!;
        const input = root.querySelector<HTMLInputElement>('input[type="file"]')!;
        const importButton = root.querySelector<HTMLButtonElement>('[aria-label="Import scene"]')!;
        return { panel, root, input, importButton, applyTerrain, applyScenario };
    }

    it('preflights the scenario before applying terrain and resets busy state after success', async () => {
        let resolve!: (value: { readonly success: true }) => void;
        const scenario = new Promise<{ readonly success: true }>(done => { resolve = done; });
        const calls: string[] = [];
        const h = panelHarness({
            applyScenario: vi.fn(() => {
                calls.push('scenario');
                return scenario;
            }),
            applyTerrain: vi.fn(terrain => { calls.push(`terrain:${terrain}`); }),
        });

        selectSceneFile(h.input, sceneJson('coastal'));
        await vi.waitFor(() => expect(calls).toEqual(['scenario']));
        expect(h.importButton.disabled).toBe(true);
        expect(h.importButton.getAttribute('aria-disabled')).toBe('true');
        expect(h.root.getAttribute('aria-busy')).toBe('true');

        resolve({ success: true });
        await vi.waitFor(() => expect(calls).toEqual(['scenario', 'terrain:coastal']));
        expect(h.importButton.disabled).toBe(false);
        expect(h.importButton.getAttribute('aria-disabled')).toBe('false');
        expect(h.root.getAttribute('aria-busy')).toBe('false');
    });

    it.each([
        'scenario.cancelled',
        'scenario.catalogUnavailable',
        'scenario.consoleUnavailable',
        'scenario.replacementFailed',
    ])('leaves terrain untouched after %s', async code => {
        const h = panelHarness({
            applyScenario: vi.fn().mockResolvedValue({
                success: false,
                code,
                detail: 'The imported scenario was not applied.',
            }),
        });

        selectSceneFile(h.input, sceneJson('coastal'));

        await vi.waitFor(() => expect(h.root.textContent).toContain(code));
        expect(h.applyTerrain).not.toHaveBeenCalled();
        expect(h.importButton.disabled).toBe(false);
        expect(h.root.getAttribute('aria-busy')).toBe('false');
    });

    it.each([
        ['invalid JSON', '{'],
        ['invalid descriptor', JSON.stringify({ version: 1, scenario: 'single' })],
    ])('leaves terrain and scenario untouched for %s', async (_label, contents) => {
        const h = panelHarness();

        selectSceneFile(h.input, contents);

        await vi.waitFor(() => expect(
            h.root.querySelector<HTMLElement>('.scfg-status')?.hidden,
        ).toBe(false));
        expect(h.applyScenario).not.toHaveBeenCalled();
        expect(h.applyTerrain).not.toHaveBeenCalled();
        expect(h.importButton.disabled).toBe(false);
    });

    it.each(['unknown', 'toString', 'constructor', '__proto__'])(
        'rejects unavailable terrain %s before starting its scenario',
        async terrain => {
            const available = { alpine: true };
            const h = panelHarness({
                canApplyTerrain: key => Object.prototype.hasOwnProperty.call(available, key),
            });

            selectSceneFile(h.input, sceneJson(terrain));

            await vi.waitFor(() => expect(h.root.textContent).toContain('scene.terrainNotFound'));
            expect(h.applyScenario).not.toHaveBeenCalled();
            expect(h.applyTerrain).not.toHaveBeenCalled();
        },
    );

    it('ignores a rapid second selection and permits it after the first run resets', async () => {
        let resolve!: (value: { readonly success: true }) => void;
        const first = new Promise<{ readonly success: true }>(done => { resolve = done; });
        const applyScenario = vi.fn()
            .mockImplementationOnce(() => first)
            .mockResolvedValue({ success: true });
        const h = panelHarness({ applyScenario });

        selectSceneFile(h.input, sceneJson('alpine'));
        await vi.waitFor(() => expect(applyScenario).toHaveBeenCalledOnce());
        selectSceneFile(h.input, sceneJson('coastal'));
        expect(applyScenario).toHaveBeenCalledOnce();

        resolve({ success: true });
        await vi.waitFor(() => expect(h.applyTerrain).toHaveBeenCalledWith('alpine'));
        expect(h.applyTerrain).not.toHaveBeenCalledWith('coastal');

        selectSceneFile(h.input, sceneJson('coastal'));
        await vi.waitFor(() => expect(applyScenario).toHaveBeenCalledTimes(2));
        await vi.waitFor(() => expect(h.applyTerrain).toHaveBeenCalledWith('coastal'));
    });
});
