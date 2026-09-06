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
export type CameraPresetKey = 'survey' | 'overview' | 'tactical' | 'cockpit' | 'ground';

/**
 * Atmospheric class. The three.js `Sky` shader is a clear-sky Preetham model and
 * cannot produce genuine overcast — no amount of turbidity gives a flat grey
 * ceiling, it gives a milky white-out with the sun disc still burning through. A
 * proper gradient dome is deferred; these are the best approximation reachable
 * with knobs that already exist, which is the boundary of this phase.
 */
export type SkyModel = 'clear' | 'overcast' | 'smoke' | 'dust';

// Type-only, and it has to stay that way: a value import would drag the
// precipitation module (and its shaders) into the entry bundle, defeating the
// on-demand load that makes a clear-sky scenario free.
import type { PrecipitationKind } from './precipitation';

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
    /**
     * What falls out of this sky, and how hard.
     *
     * Optional, and absent means clear — which matters for cost as much as for
     * looks: the module that draws it is imported on demand, so a scenario that
     * declares nothing here never fetches it.
     *
     * Deliberately a separate axis from {@link skyModel}. The two are related
     * but not the same: a wildfire sky is thick with smoke and drops ash, a
     * hurricane sky is merely overcast and drops a great deal of rain, and an
     * alpine whiteout is a clear-model sky full of snow. Deriving one from the
     * other would force those three to share a look.
     */
    readonly precipitation?: {
        readonly kind: PrecipitationKind;
        /** 0–1 scale on particle count and opacity. */
        readonly intensity: number;
    };
}

/**
 * The shipped disaster and multi-domain environments.
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
        defaultCameraPreset: 'survey',
        precipitation: { kind: 'ash', intensity: 0.85 },
    },
    'hurricane-melissa': {
        key: 'hurricane-melissa',
        terrainPreset: 'coastal',
        sunElevationDeg: 6,
        sunAzimuthDeg: 200,
        skyModel: 'overcast',
        fogColor: 0x5c6d78,
        fogDensity: 0.00035,
        toneMappingExposure: 1.15,
        waterLevel: 6,          // storm surge
        defaultCameraPreset: 'survey',
        precipitation: { kind: 'rain', intensity: 1.0 },
    },
    'flood-riverine': {
        key: 'flood-riverine',
        terrainPreset: 'alpine',
        sunElevationDeg: 35,
        sunAzimuthDeg: 140,
        skyModel: 'clear',
        fogColor: 0x9aa8ae,
        fogDensity: 0.00012,
        toneMappingExposure: 1.0,
        waterLevel: 18,         // risen; turbidity deferred (new shader feature)
        defaultCameraPreset: 'survey',
        precipitation: { kind: 'rain', intensity: 0.55 },
    },
    'urban-collapse': {
        key: 'urban-collapse',
        terrainPreset: 'canyon',
        sunElevationDeg: 20,
        sunAzimuthDeg: 95,
        skyModel: 'dust',
        fogColor: 0xa89c8e,
        fogDensity: 0.00028,
        toneMappingExposure: 1.05,
        waterLevel: -60,
        defaultCameraPreset: 'survey',
        precipitation: { kind: 'ash', intensity: 0.45 },
    },
    'alpine-sar': {
        key: 'alpine-sar',
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
        defaultCameraPreset: 'survey',
        precipitation: { kind: 'snow', intensity: 0.8 },
    },
    'canyon-sar': {
        key: 'canyon-sar',
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
        defaultCameraPreset: 'survey',
    },
    // ── Multi-domain presets ────────────────────────────────────────────
    //
    // These had no environment at all, so starting one left whatever look the
    // previous scenario had applied — a flood inheriting a wildfire's orange
    // smoke sky, or a coastal transit running under alpine sun, depending only
    // on what had been selected before it.
    //
    // None of them overrides `waterLevel`. Their asset positions were surveyed
    // against each preset's own water height — the flood ferries work in 10 m of
    // water and the coastal column holds a channel that never shoals below
    // 5.5 m — so moving the surface under them would strand or sink the fleet
    // the scenario exists to show.
    'flood-response': {
        key: 'flood-response',
        terrainPreset: 'alpine',
        sunElevationDeg: 28,
        sunAzimuthDeg: 155,
        skyModel: 'overcast',
        fogColor: 0x8e9aa2,
        fogDensity: 0.00016,
        toneMappingExposure: 1.05,
        defaultCameraPreset: 'survey',
        precipitation: { kind: 'rain', intensity: 0.5 },
    },
    'coastal-search': {
        key: 'coastal-search',
        terrainPreset: 'coastal',
        sunElevationDeg: 30,
        sunAzimuthDeg: 210,
        skyModel: 'overcast',
        fogColor: 0x7f8f9a,
        fogDensity: 0.00020,
        toneMappingExposure: 1.05,
        defaultCameraPreset: 'survey',
        precipitation: { kind: 'rain', intensity: 0.35 },
    },
    'coastal-transit': {
        key: 'coastal-transit',
        terrainPreset: 'coastal',
        sunElevationDeg: 44,
        sunAzimuthDeg: 175,
        skyModel: 'clear',
        fogColor: 0xa8bcc6,
        fogDensity: 0.00010,
        toneMappingExposure: 1.0,
        defaultCameraPreset: 'survey',
    },
    'port-incident': {
        key: 'port-incident',
        terrainPreset: 'coastal',
        sunElevationDeg: 16,
        sunAzimuthDeg: 240,
        skyModel: 'smoke',
        fogColor: 0xb08c6a,
        fogDensity: 0.00026,
        toneMappingExposure: 0.98,
        defaultCameraPreset: 'survey',
        precipitation: { kind: 'ash', intensity: 0.35 },
    },
    'ground-convoy': {
        key: 'ground-convoy',
        terrainPreset: 'alpine',
        sunElevationDeg: 38,
        sunAzimuthDeg: 130,
        skyModel: 'clear',
        fogColor: 0xc2d0d8,
        fogDensity: 0.00009,
        toneMappingExposure: 1.0,
        defaultCameraPreset: 'ground',
    },
    'mixed-ground': {
        key: 'mixed-ground',
        terrainPreset: 'alpine',
        sunElevationDeg: 34,
        sunAzimuthDeg: 145,
        skyModel: 'clear',
        fogColor: 0xbecdd6,
        fogDensity: 0.00010,
        toneMappingExposure: 1.0,
        defaultCameraPreset: 'survey',
    },
};

/** Sky + sun-intensity settings for an environment's atmospheric class. */
export function skyProfileFor(env: ScenarioEnvironment): SkyProfile {
    return SKY_PROFILES[env.skyModel];
}

/** Lookup by scenario id. Returns null for dev fixtures, which have no environment. */
export function environmentFor(scenarioKey: string): ScenarioEnvironment | null {
    return Object.prototype.hasOwnProperty.call(SCENARIO_ENVIRONMENTS, scenarioKey)
        ? SCENARIO_ENVIRONMENTS[scenarioKey]!
        : null;
}

/** Everything the outer orchestrator needs, injected so this module stays testable. */
export interface EnvironmentDeps {
    /** Inner seam — applies sun, sky, fog, exposure. */
    applyScene: (env: ScenarioEnvironment) => void;
    /** Rebuild terrain for a preset, with an optional water-level override. */
    switchPreset: (key: PresetKey, waterLevel?: number) => void;
    /** Jump to a named camera framing. */
    setCamera: (preset: CameraPresetKey, env: ScenarioEnvironment) => void;
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
    deps.setCamera(env.defaultCameraPreset, env);
    return true;
}
