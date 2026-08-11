// ResQ Viz - Declarative scene config (export / import)
// SPDX-License-Identifier: Apache-2.0

import { getLogger } from '../log';

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
    /** Run a scenario by name (no-op for null). */
    applyScenario: (name: string | null) => void;
}

/**
 * Top-left control with Export/Import buttons that round-trip the scene
 * descriptor: Export downloads the current config as `resq-scene.json`; Import
 * reads a file, validates it, and applies the terrain + scenario so the setup
 * is reproduced.
 */
export class SceneConfigPanel {
    private readonly _d: SceneConfigDeps;
    private readonly _fileInput: HTMLInputElement;

    constructor(deps: SceneConfigDeps) {
        this._d = deps;
        const built = this._build();
        this._fileInput = built.fileInput;
        built.exportBtn.addEventListener('click', () => this._export());
        built.importBtn.addEventListener('click', () => this._fileInput.click());
        this._fileInput.addEventListener('change', () => void this._import());
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

    private async _import(): Promise<void> {
        const file = this._fileInput.files?.[0];
        this._fileInput.value = ''; // allow re-importing the same file
        if (!file) return;
        let raw: unknown;
        try {
            raw = JSON.parse(await file.text());
        } catch {
            log.warn('scene import failed — not valid JSON');
            return;
        }
        const config = parseSceneConfig(raw);
        if (!config) {
            log.warn('scene import failed — not a recognisable scene descriptor');
            return;
        }
        this._d.applyTerrain(config.terrain);
        this._d.applyScenario(config.scenario);
        log.info('scene imported', { terrain: config.terrain, scenario: config.scenario });
    }

    private _build(): { exportBtn: HTMLButtonElement; importBtn: HTMLButtonElement; fileInput: HTMLInputElement } {
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

        root.append(exportBtn, importBtn, fileInput);
        document.body.appendChild(root);
        return { exportBtn, importBtn, fileInput };
    }
}
