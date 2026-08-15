// ResQ Viz - Settings with localStorage persistence
// SPDX-License-Identifier: Apache-2.0

export interface SettingsData {
    bloomStrength:      number;   // 0.0–1.0
    bloomEnabled:       boolean;
    fogDensity:         number;   // 0.00005–0.0008
    flySpeed:           number;   // 5–200 units/s
    fov:                number;   // 40–100 degrees
    labelMode:          'always' | 'hover' | 'off';
    trailLength:        number;   // seconds: 0, 1, 3, 5, 10
    batteryWarnPct:     number;   // 5–40 %
    detectionRingShow:  boolean;
    shadowsEnabled:     boolean;
    showVelocity:       boolean;
    ssaoEnabled:        boolean;   // GTAO ambient occlusion
    antiTile:           boolean;   // seamless stochastic terrain albedo (heavier)
    contactShadowEnabled: boolean; // soft blob shadow under each drone
    downwashEnabled:    boolean;   // rotor downwash dust + water ripples
    erosionEnabled:     boolean;   // server-baked hydraulic-erosion terrain
}

const DEFAULTS: SettingsData = {
    bloomStrength:      0.55,
    bloomEnabled:       false,
    fogDensity:         0.00010,
    flySpeed:           20,
    fov:                60,
    labelMode:          'always',
    trailLength:        3,
    batteryWarnPct:     20,
    detectionRingShow:  false,
    shadowsEnabled:     true,
    showVelocity:       false,
    ssaoEnabled:        true,
    antiTile:           false,   // opt-in: ~9× albedo taps
    contactShadowEnabled: true,
    downwashEnabled:    true,
    erosionEnabled:     true,
};

const KEY = 'resq-viz-settings';
/** Bumped when a persisted default changes; drives the one-time migration below. */
const SCHEMA_VERSION = 2;

export class Settings {
    private _data: SettingsData;
    private readonly _listeners = new Map<keyof SettingsData, Array<(v: unknown) => void>>();

    constructor() {
        this._data = { ...DEFAULTS };
        try {
            const raw = localStorage.getItem(KEY);
            if (raw) {
                const { _v, ...stored } = JSON.parse(raw) as Partial<SettingsData> & { _v?: number };
                Object.assign(this._data, stored);
                // v2 migration: the X/Y/Z velocity vectors now default OFF (they're
                // an analysis overlay, not everyday chrome). Clear a stale persisted
                // `showVelocity: true` once, then respect the user's choice again.
                if ((_v ?? 1) < SCHEMA_VERSION) {
                    this._data.showVelocity = DEFAULTS.showVelocity;
                    this._persist();
                }
            }
        } catch { /* ignore */ }
    }

    get<K extends keyof SettingsData>(key: K): SettingsData[K] {
        return this._data[key];
    }

    set<K extends keyof SettingsData>(key: K, value: SettingsData[K]): void {
        this._data[key] = value;
        this._persist();
        const cbs = this._listeners.get(key);
        cbs?.forEach(cb => cb(value));
    }

    on<K extends keyof SettingsData>(key: K, cb: (v: SettingsData[K]) => void): void {
        if (!this._listeners.has(key)) this._listeners.set(key, []);
        this._listeners.get(key)!.push(cb as (v: unknown) => void);
    }

    private _persist(): void {
        try {
            localStorage.setItem(KEY, JSON.stringify({ ...this._data, _v: SCHEMA_VERSION }));
        } catch { /* ignore */ }
    }
}
