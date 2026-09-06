// ResQ Viz - Declarative scene config (export / import)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import { getLogger } from '../log';
import type { ApiFailure, Result } from '../api';
import { liveGate, type MutationGate } from '../operator/interactionMode';
import type {
    OperatorMode,
    ScenarioReplacementContext,
    ScenarioStartResponse,
} from '../operator/types';
import {
    requestScenarioStart,
    startLegacyScenario,
    type ScenarioRequestRuntime,
} from '../operator/consoleApi';

const log = getLogger('scene-config');

/** Bumped when the descriptor shape changes; `parseSceneConfig` can migrate. */
export const SCENE_CONFIG_VERSION = 1;

/**
 * A shareable, reproducible scene descriptor — the AirSim `settings.json`
 * analog. Captures the parts of a sim setup that define "what scene is
 * running": the terrain preset and the active scenario. (Camera/editor prefs
 * are a future extension.)
 */
export interface SceneConfig {
    version: number;
    terrain: string;
    scenario: string | null;
}

/** Serialize a config to pretty JSON. Pure. */
export function serializeSceneConfig(config: SceneConfig): string {
    return JSON.stringify(config, null, 2);
}

/**
 * Validate + normalise an unknown value into a SceneConfig, or null if it isn't
 * a recognisable descriptor. Pure — unit-tested. Structural only; the caller
 * validates that `terrain`/`scenario` name real presets/scenarios before applying.
 */
export function parseSceneConfig(raw: unknown): SceneConfig | null {
    if (typeof raw !== 'object' || raw === null) return null;
    const o = raw as Record<string, unknown>;
    if (typeof o['version'] !== 'number') return null;
    if (typeof o['terrain'] !== 'string') return null;
    const scenario = o['scenario'];
    if (scenario !== null && scenario !== undefined && typeof scenario !== 'string') return null;
    return {
        version: o['version'],
        terrain: o['terrain'],
        scenario: typeof scenario === 'string' ? scenario : null,
    };
}

export interface SceneConfigDeps {
    /** Current terrain preset key. */
    getTerrain: () => string;
    /** Active scenario name, or null if none explicitly started. */
    getScenario: () => string | null;
    /** Apply a terrain preset (caller validates the key). */
    applyTerrain: (key: string) => void;
    /** Whether the imported terrain can be applied without partially importing the scene. */
    readonly canApplyTerrain?: (key: string) => boolean;
    /** Run a scenario by name (no-op for null) and return displayable refusal details. */
    applyScenario: (
        name: string | null,
    ) => void | SceneScenarioApplyResult | Promise<void | SceneScenarioApplyResult>;
    /** Shared live/replay gate. Import writes the world and is gated; export
     *  only reads what is already on screen and never is. */
    readonly gate?: MutationGate;
    /** Where the control mounts. Defaults to the body so a standalone
     *  construction behaves as it always did; the Editor workspace passes its
     *  own header, which is what keeps the pair inside one reachable surface
     *  instead of floating over the scene with nothing owning it. */
    readonly mount?: HTMLElement;
}

export type SceneScenarioApplyResult =
    | { readonly success: true }
    | { readonly success: false; readonly code: string; readonly detail: string };

/** Mode-specific scenario dependencies kept outside the editor surface. */
export interface SceneScenarioModeDependencies {
    readonly mode: () => OperatorMode;
    /** Canonical server catalog names, or null until the resource is available. */
    readonly v2ScenarioNames: () => readonly string[] | null;
    readonly v2Session: () => ScenarioReplacementContext;
    readonly confirmV2Replace: (name: string) => boolean;
    readonly runtime?: ScenarioRequestRuntime;
    readonly startV2?: (
        name: string,
    ) => Promise<Result<ScenarioStartResponse, ApiFailure>>;
    readonly legacyScenarioNames?: () => readonly string[];
    readonly startLegacy?: (name: string) => Promise<boolean>;
    readonly onLegacyStarted?: (name: string) => void;
}

/** Validates and starts an imported scenario without crossing mode authority. */
export async function applyScenarioForMode(
    requestedName: string,
    dependencies: SceneScenarioModeDependencies,
): Promise<SceneScenarioApplyResult> {
    const mode = dependencies.mode();
    if (mode === 'v2') {
        const names = dependencies.v2ScenarioNames();
        if (names === null) {
            return failure(
                'scenario.catalogUnavailable',
                'The scenario catalog is unavailable. Retry it before importing this scenario.',
            );
        }
        const canonical = canonicalName(requestedName, names);
        if (canonical === null) {
            return failure('scenario.notFound', `Scenario '${requestedName}' is not in this catalog.`);
        }
        const session = dependencies.v2Session();
        if ((session.assetCount > 0 || session.tick > 0)
            && !dependencies.confirmV2Replace(canonical)) {
            return failure('scenario.cancelled', 'Scenario import was cancelled.');
        }
        try {
            const start = dependencies.startV2
                ?? (dependencies.runtime
                    ? (name: string) => requestScenarioStart(
                        dependencies.runtime!, name, undefined, () => dependencies.mode() === 'v2',
                    )
                    : null);
            if (start === null) {
                return failure('scenario.consoleUnavailable', 'Scenario request state is unavailable.');
            }
            const result = await start(canonical);
            return result.success ? { success: true } : apiFailure(result.error);
        } catch (error: unknown) {
            return failure('network', error instanceof Error ? error.message : String(error));
        }
    }

    if (mode === 'legacy') {
        const legacyNames = dependencies.legacyScenarioNames?.() ?? Array.from(
            document.querySelectorAll<HTMLElement>('.scenario-card[data-scenario]'),
            element => element.dataset['scenario'] ?? '',
        ).filter(Boolean);
        const canonical = canonicalName(requestedName, legacyNames);
        if (canonical === null) {
            return failure('scenario.notFound', `Scenario '${requestedName}' is not available in legacy mode.`);
        }
        try {
            if (!await (dependencies.startLegacy ?? startLegacyScenario)(canonical)) {
                return failure('scenario.startFailed', 'The legacy scenario did not start.');
            }
        } catch (error: unknown) {
            return failure('network', error instanceof Error ? error.message : String(error));
        }
        if (dependencies.mode() !== 'legacy') {
            return failure(
                'scenario.consoleUnavailable',
                'Legacy mode changed before the scenario response arrived.',
            );
        }
        (dependencies.onLegacyStarted ?? publishLegacyStart)(canonical);
        return { success: true };
    }

    return failure('scenario.consoleUnavailable', 'Wait for simulation stream negotiation to finish.');
}

/**
 * Top-left control with Export/Import buttons that round-trip the scene
 * descriptor: Export downloads the current config as `resq-scene.json`; Import
 * reads a file, validates it, and applies the terrain + scenario so the setup
 * is reproduced.
 */
export class SceneConfigPanel {
    private readonly _d: SceneConfigDeps;
    private readonly _gate: MutationGate;
    private readonly _fileInput: HTMLInputElement;
    private readonly _status: HTMLElement;
    private readonly _root: HTMLElement;
    private readonly _importButton: HTMLButtonElement;
    private _importInFlight = false;
    private _importGeneration = 0;

    constructor(deps: SceneConfigDeps) {
        this._d = deps;
        this._gate = deps.gate ?? liveGate;
        const built = this._build();
        this._fileInput = built.fileInput;
        this._status = built.status;
        this._root = built.root;
        this._importButton = built.importBtn;
        built.exportBtn.addEventListener('click', () => this._export());
        built.importBtn.addEventListener('click', () => this._fileInput.click());
        this._fileInput.addEventListener('change', () => {
            const file = this._fileInput.files?.[0];
            this._fileInput.value = ''; // allow re-importing the same file
            if (!file || this._importInFlight) return;
            void this._import(file);
        });
    }

    private _export(): void {
        const config: SceneConfig = {
            version: SCENE_CONFIG_VERSION,
            terrain: this._d.getTerrain(),
            scenario: this._d.getScenario(),
        };
        const blob = new Blob([serializeSceneConfig(config)], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'resq-scene.json';
        a.click();
        URL.revokeObjectURL(url);
        log.info('scene exported', { terrain: config.terrain, scenario: config.scenario });
    }

    private async _import(file: File): Promise<void> {
        // Before anything is read or any busy state is claimed, so a refusal
        // leaves the control exactly as it found it and the operator can retry
        // the moment they return to Live.
        if (!this._gate('scene.import').success) {
            this._showFailure(
                'interaction.replay',
                'Return to Live to import a scene into the running simulation.',
            );
            return;
        }
        const generation = ++this._importGeneration;
        this._importInFlight = true;
        this._setImportBusy(true);
        this._showFailure(null, '');
        try {
            let raw: unknown;
            try {
                raw = JSON.parse(await file.text());
            } catch {
                log.warn('scene import failed — not valid JSON');
                this._showFailure('scene.invalidJson', 'The selected file is not valid JSON.');
                return;
            }
            if (generation !== this._importGeneration) return;

            const config = parseSceneConfig(raw);
            if (!config) {
                log.warn('scene import failed — not a recognisable scene descriptor');
                this._showFailure('scene.invalidConfig', 'The selected file is not a recognized scene descriptor.');
                return;
            }
            if (this._d.canApplyTerrain && !this._d.canApplyTerrain(config.terrain)) {
                this._showFailure(
                    'scene.terrainNotFound',
                    `Terrain '${config.terrain}' is not available in this viewer.`,
                );
                return;
            }

            let outcome: void | SceneScenarioApplyResult;
            try {
                outcome = await this._d.applyScenario(config.scenario);
            } catch (error: unknown) {
                this._showFailure(
                    'network',
                    error instanceof Error ? error.message : String(error),
                );
                return;
            }
            if (generation !== this._importGeneration) return;
            if (outcome && !outcome.success) {
                this._showFailure(outcome.code, outcome.detail);
                return;
            }

            this._d.applyTerrain(config.terrain);
            this._showFailure(null, '');
            log.info('scene imported', { terrain: config.terrain, scenario: config.scenario });
        } finally {
            if (generation === this._importGeneration) {
                this._importInFlight = false;
                this._setImportBusy(false);
            }
        }
    }

    private _setImportBusy(busy: boolean): void {
        this._root.setAttribute('aria-busy', String(busy));
        this._importButton.disabled = busy;
        this._importButton.setAttribute('aria-disabled', String(busy));
        this._fileInput.disabled = busy;
    }

    private _showFailure(code: string | null, detail: string): void {
        this._status.hidden = code === null;
        this._status.textContent = code === null ? '' : `${code} · ${detail}`;
    }

    private _build(): {
        root: HTMLElement;
        exportBtn: HTMLButtonElement;
        importBtn: HTMLButtonElement;
        fileInput: HTMLInputElement;
        status: HTMLElement;
    } {
        const root = document.createElement('div');
        root.className = 'resq-scenecfg';
        root.setAttribute('role', 'group');
        root.setAttribute('aria-label', 'Scene config');

        const mk = (label: string, glyph: string): HTMLButtonElement => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'scfg-btn';
            b.setAttribute('aria-label', label);
            b.title = label;
            b.textContent = glyph;
            return b;
        };
        const exportBtn = mk('Export scene', '⤓');
        const importBtn = mk('Import scene', '⤒');

        const fileInput = document.createElement('input');
        fileInput.type = 'file';
        fileInput.accept = 'application/json,.json';
        fileInput.hidden = true;

        const status = document.createElement('span');
        status.className = 'scfg-status operator-resource-error';
        status.setAttribute('role', 'alert');
        status.hidden = true;

        root.append(exportBtn, importBtn, fileInput, status);
        (this._d.mount ?? document.body).appendChild(root);
        return { root, exportBtn, importBtn, fileInput, status };
    }
}

function canonicalName(requested: string, names: readonly string[]): string | null {
    const folded = requested.toLocaleLowerCase();
    return names.find(name => name.toLocaleLowerCase() === folded) ?? null;
}

function apiFailure(value: ApiFailure): SceneScenarioApplyResult {
    return value.kind === 'problem'
        ? failure(value.problem.reasonCode ?? value.problem.code, value.problem.detail)
        : failure(value.kind, value.message);
}

function failure(code: string, detail: string): SceneScenarioApplyResult {
    return { success: false, code, detail };
}

function publishLegacyStart(name: string): void {
    document.dispatchEvent(new CustomEvent('resq:scenario-start', { detail: { name } }));
}
