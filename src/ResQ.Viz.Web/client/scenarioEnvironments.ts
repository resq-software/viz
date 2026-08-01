// ResQ Viz - Scenario → environment binding
// SPDX-License-Identifier: Apache-2.0
//
// Before this module a scenario controlled drone count and vendor mix and
// nothing else: terrain preset, sun angle, fog and camera framing were picked
// independently from the sidebar and persisted in localStorage, so a hurricane
// rendered on sunlit desert dunes. Every scenario looked the same because the
// scenario controlled nothing about how it looked. A `ScenarioEnvironment`
// binds a disaster to its full environmental presentation; scenario load drives
// it, and the sidebar terrain picker becomes a development override.
//
// Layering: `Scene.applyEnvironment` is the INNER seam and owns sun, sky, fog
// and exposure. `applyScenarioEnvironment` below is the OUTER orchestrator and
// adds terrain preset, water level and camera framing — layers Scene has no
// business knowing about. Different names, deliberately, so the two cannot
// drift apart under one identifier.

import type { PresetKey } from './terrainPresets';
import type { SceneEnvironment } from './scene';

/** Named camera framings bound in app.ts to Shift+1..6. */
export type CameraPresetKey = 'overview' | 'tactical' | 'cockpit' | 'ground';

/**
 * Atmospheric class. The three.js `Sky` shader is a clear-sky Preetham model and
 * cannot produce genuine overcast — no amount of turbidity gives a flat grey
 * ceiling, it gives a milky white-out with the sun disc still burning through. A
 * proper gradient dome is deferred; these are the best approximation reachable
 * with knobs that already exist, which is the boundary of this phase.
 */
export type SkyModel = 'clear' | 'overcast' | 'smoke' | 'dust';

/** Sky shader + sun-intensity settings per atmospheric class. */
export interface SkyProfile {
    readonly turbidity:    number;
    readonly rayleigh:     number;
    /**
     * Mie directional G — aureole tightness. Low values spread the sun's glow
     * into broad haze instead of a hard disc. This matters most at low sun: at
     * 6° elevation a tight aureole reads unmistakably as *sunset*, not as
     * *storm*, which fails scenario distinctness outright.
     */
    readonly mieG:         number;
    /** Directional-light multiplier. Overcast kills direct sun almost entirely. */
    readonly sunIntensity: number;
}

const SKY_PROFILES: Readonly<Record<SkyModel, SkyProfile>> = {
    clear:    { turbidity:  3.2, rayleigh: 1.60, mieG: 0.86, sunIntensity: 1.00 },
    // Wide aureole + crushed rayleigh: the closest a Preetham sky gets to a
    // featureless ceiling. Sun intensity down hard so shadows go soft and weak.
    overcast: { turbidity: 10.0, rayleigh: 0.40, mieG: 0.72, sunIntensity: 0.35 },
    // Smoke scatters forward strongly and reddens; keep some direct sun so a
    // fire front still casts.
    smoke:    { turbidity:  8.0, rayleigh: 0.80, mieG: 0.70, sunIntensity: 0.55 },
    // Dust is coarser than smoke: less forward scatter, more uniform extinction.
    dust:     { turbidity:  6.0, rayleigh: 0.50, mieG: 0.80, sunIntensity: 0.70 },
};

/** Full environmental presentation bound to one disaster scenario. */
export interface ScenarioEnvironment extends SceneEnvironment {
    /** Scenario id — must match the key in appsettings.json `Scenarios`. */
    readonly key: string;
    /** Operator-facing label for the intro card and mission chrome. */
    readonly displayName: string;
    readonly terrainPreset: PresetKey;
    readonly skyModel: SkyModel;
    /**
     * Water plane height override, metres. `undefined` keeps the preset's own
     * `waterLevel`. Passed as an override rather than mutating the preset: the
     * `PRESETS` table is frozen and its `cacheKey` contract assumes the height
     * function is the only thing that varies. Water is a separate mesh
     * (`terrain.ts:749`), so terrain geometry still hits the cache.
     */
    readonly waterLevel?: number;
    readonly defaultCameraPreset: CameraPresetKey;
}

/**
 * The six shipped disaster environments.
 *
 * On fog densities: these were authored against *uniform* extinction, and since
 * height fog is written but unwired, uniform is exactly what they get — so the
 * values apply as-authored today. When height fog lands they will each need
 * cutting by roughly 30–50 %, because vertical falloff redistributes extinction
 * rather than removing it and the same number reads far thicker at ground level.
 * Do not port these numbers across that change unexamined.
 */
export const SCENARIO_ENVIRONMENTS: Readonly<Record<string, ScenarioEnvironment>> = {
    'wildfire-interface': {
        key: 'wildfire-interface',
        displayName: 'WILDFIRE — WUI INTERFACE',
        terrainPreset: 'ridgeline',
        sunElevationDeg: 12,
        sunAzimuthDeg: 285,
        skyModel: 'smoke',
        fogColor: 0xd98a55,
        fogDensity: 0.00022,
        toneMappingExposure: 0.95,
        waterLevel: -15,
        // Framed away from the 285° sun: low sun straight down the barrel
        // silhouettes the ridge and hides the relief the scenario is about.
        defaultCameraPreset: 'tactical',
    },
    'hurricane-melissa': {
        key: 'hurricane-melissa',
        displayName: 'HURRICANE MELISSA — LANDFALL',
        terrainPreset: 'coastal',
        sunElevationDeg: 6,
        sunAzimuthDeg: 200,
        skyModel: 'overcast',
        fogColor: 0x5c6d78,
        fogDensity: 0.00035,
        toneMappingExposure: 1.15,
        waterLevel: 6,          // storm surge
        defaultCameraPreset: 'tactical',
    },
    'flood-riverine': {
        key: 'flood-riverine',
        displayName: 'RIVERINE FLOOD — VALLEY INUNDATION',
        terrainPreset: 'alpine',
        sunElevationDeg: 35,
        sunAzimuthDeg: 140,
        skyModel: 'clear',
        fogColor: 0x9aa8ae,
        fogDensity: 0.00012,
        toneMappingExposure: 1.0,
        waterLevel: 18,         // risen; turbidity deferred (new shader feature)
        defaultCameraPreset: 'overview',
    },
    'urban-collapse': {
        key: 'urban-collapse',
        displayName: 'URBAN COLLAPSE — STRUCTURE SEARCH',
        terrainPreset: 'canyon',
        sunElevationDeg: 20,
        sunAzimuthDeg: 95,
        skyModel: 'dust',
        fogColor: 0xa89c8e,
        fogDensity: 0.00028,
        toneMappingExposure: 1.05,
        waterLevel: -60,
        defaultCameraPreset: 'tactical',
    },
    'alpine-sar': {
        key: 'alpine-sar',
        displayName: 'ALPINE SAR — AVALANCHE RESPONSE',
        terrainPreset: 'alpine',
        sunElevationDeg: 22,
        sunAzimuthDeg: 160,
        skyModel: 'clear',
        fogColor: 0xc9dbe8,
        fogDensity: 0.00009,
        // Deliberately under 1.0: high-albedo snow blows out to flat white under
        // ACES at 1.0, destroying exactly the relief this scenario exists to show.
        toneMappingExposure: 0.85,
        waterLevel: -3,
        defaultCameraPreset: 'overview',
    },
    'canyon-sar': {
        key: 'canyon-sar',
        displayName: 'CANYON SAR — SLOT GORGE',
        terrainPreset: 'canyon',
        // High sun is the point: it drives hard, high-contrast shadow into the
        // gorge, and deep occlusion is what makes the WebGPU line-of-sight
        // primitive visible as mesh links drop on descent.
        sunElevationDeg: 68,
        sunAzimuthDeg: 180,
        skyModel: 'clear',
        fogColor: 0xd9b98a,
        fogDensity: 0.00010,
        toneMappingExposure: 1.0,
        waterLevel: -60,
        defaultCameraPreset: 'overview',
    },
};

/** Sky + sun-intensity settings for an environment's atmospheric class. */
export function skyProfileFor(env: ScenarioEnvironment): SkyProfile {
    return SKY_PROFILES[env.skyModel];
}

/** Lookup by scenario id. Returns null for dev fixtures, which have no environment. */
export function environmentFor(scenarioKey: string): ScenarioEnvironment | null {
    return SCENARIO_ENVIRONMENTS[scenarioKey] ?? null;
}

/** Everything the outer orchestrator needs, injected so this module stays testable. */
export interface EnvironmentDeps {
    /** Inner seam — applies sun, sky, fog, exposure. */
    applyScene: (env: ScenarioEnvironment) => void;
    /** Rebuild terrain for a preset, with an optional water-level override. */
    switchPreset: (key: PresetKey, waterLevel?: number) => void;
    /** Jump to a named camera framing. */
    setCamera: (preset: CameraPresetKey) => void;
    /** True when the operator has manually overridden terrain from the sidebar. */
    isTerrainOverridden: () => boolean;
}

/**
 * Apply a full scenario environment.
 *
 * Called from the `resq:scenario-start` handler — deliberately, not earlier.
 * `scenarioIntro.ts:73` listens on the same event and raises a title card, so
 * the terrain rebuild happens behind it and the hitch is masked for free. Do NOT
 * "optimise" this to fire sooner; that trades a hidden rebuild for a visible one.
 *
 * Returns false when no environment is bound to the scenario (the drone-count
 * dev fixtures), so callers can leave the current look alone.
 */
export function applyScenarioEnvironment(deps: EnvironmentDeps, scenarioKey: string): boolean {
    const env = environmentFor(scenarioKey);
    if (!env) return false;

    // Terrain first: it is the long pole, and the operator's manual sidebar
    // choice outranks the scenario's.
    if (!deps.isTerrainOverridden()) {
        deps.switchPreset(env.terrainPreset, env.waterLevel);
    }
    deps.applyScene(env);
    deps.setCamera(env.defaultCameraPreset);
    return true;
}
