// ResQ Viz - Top HUD bar module
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from '../types';
import type { SceneAsset } from '../assets/sceneFrame';
import { AssetDomain } from '../assets/types';
import type { OperatorMode } from '../operator/types';

export interface AssetHudSummary {
    readonly total: number;
    readonly air: number;
    readonly ground: number;
    readonly surface: number;
}

/** Text for the app-owned polite live region. This function performs no DOM writes. */
export function assetTelemetryText(summary: AssetHudSummary, simTime: number): string {
    if (summary.total === 0) return 'No active assets.';
    const assetNoun = summary.total === 1 ? 'asset' : 'assets';
    const roundedTime = Math.round(simTime);
    const secondNoun = roundedTime === 1 ? 'second' : 'seconds';
    return `${summary.total} ${assetNoun} total: ${summary.air} air, `
        + `${summary.ground} ground, ${summary.surface} surface. `
        + `Simulation time ${roundedTime} ${secondNoun}.`;
}

function required<T extends HTMLElement>(doc: Document, id: string): T {
    const element = doc.getElementById(id) as T | null;
    if (!element) throw new Error(`Required DOM element #${id} not found`);
    return element;
}

function setText(element: Node, value: string): void {
    if (element.textContent !== value) element.textContent = value;
}

function setAttribute(element: Element, name: string, value: string): void {
    if (element.getAttribute(name) !== value) element.setAttribute(name, value);
}

export class Hud {
    private readonly _dot: HTMLElement;
    private readonly _label: HTMLElement;
    private readonly _legacyCountBranch: HTMLElement;
    private readonly _assetCountBranch: HTMLElement;
    private readonly _droneCount: HTMLElement;
    private readonly _assetCount: HTMLElement;
    private readonly _airCount: HTMLElement;
    private readonly _groundCount: HTMLElement;
    private readonly _surfaceCount: HTMLElement;
    private readonly _fps: HTMLElement;
    private readonly _time: HTMLElement;
    private readonly _batteryStat: HTMLElement;
    private readonly _fill: HTMLElement;
    private readonly _pct: HTMLElement;
    private readonly _selChip: HTMLElement;
    private readonly _selText: HTMLElement;
    /** Reused at stream rate so returning the visible counts creates no frame garbage. */
    private readonly _assetSummary = { total: 0, air: 0, ground: 0, surface: 0 };

    private _mode: OperatorMode | null = null;
    private _selectedId: string | null = null;
    private _selectedKind: 'asset' | 'drone' | null = null;
    private _lastTime: number | null = null;
    private _batteryAverage: number | null = null;
    private _batteryLegacyUnknown = false;
    private _batteryReady = false;

    constructor(doc: Document = document) {
        this._dot = required(doc, 'conn-dot');
        this._label = required(doc, 'conn-label');
        this._legacyCountBranch = required(doc, 'hud-count-v1');
        this._assetCountBranch = required(doc, 'hud-count-v2');
        this._droneCount = required(doc, 'drone-count');
        this._assetCount = required(doc, 'asset-count');
        this._airCount = required(doc, 'air-count');
        this._groundCount = required(doc, 'ground-count');
        this._surfaceCount = required(doc, 'surface-count');
        this._fps = required(doc, 'fps');
        this._time = required(doc, 'sim-time');
        this._batteryStat = required(doc, 'hud-battery-stat');
        this._fill = required(doc, 'battery-fill');
        this._pct = required(doc, 'battery-pct');
        this._selChip = required(doc, 'hud-selected-drone');
        this._selText = required(doc, 'hud-selected-asset');
        this.setMode('booting');
    }

    /** Exposes only the count branch whose negotiated schema is authoritative. */
    setMode(mode: OperatorMode): void {
        if (this._mode === mode) return;
        this._mode = mode;
        this._setCountBranch(this._legacyCountBranch, mode === 'legacy');
        this._setCountBranch(this._assetCountBranch, mode === 'v2');
        if (mode === 'v2') setAttribute(this._batteryStat, 'title', 'Air asset battery average');
        if (mode === 'legacy') setAttribute(this._batteryStat, 'title', 'Fleet battery average');
        this._renderSelection();
    }

    setStatus(state: 'connected' | 'reconnecting' | 'disconnected'): void {
        this._dot.className = 'conn-dot';
        switch (state) {
            case 'connected':
                this._dot.classList.add('connected');
                this._label.textContent = 'Connected';
                break;
            case 'reconnecting':
                this._dot.classList.add('reconnecting');
                this._label.textContent = 'Reconnecting…';
                break;
            case 'disconnected':
                this._label.textContent = 'Disconnected';
                break;
        }
    }

    updateFps(fps: number): void {
        setText(this._fps, String(fps));
    }

    updateTime(time: number): void {
        if (this._lastTime === time) return;
        this._lastTime = time;
        setText(this._time, `${time.toFixed(1)}s`);
    }

    /** Updates total/domain counts and Air power from the complete projected inventory. */
    updateAssets(allAssets: readonly SceneAsset[]): AssetHudSummary {
        let air = 0;
        let ground = 0;
        let surface = 0;
        let airPowerTotal = 0;
        let airPowerCount = 0;

        for (const asset of allAssets) {
            switch (asset.descriptor.domain) {
                case AssetDomain.Air: {
                    air++;
                    const percent = asset.state.power.percentRemaining;
                    if (percent !== null) {
                        airPowerTotal += percent;
                        airPowerCount++;
                    }
                    break;
                }
                case AssetDomain.Ground:
                    ground++;
                    break;
                case AssetDomain.Surface:
                    surface++;
                    break;
            }
        }

        const summary = this._assetSummary;
        const total = allAssets.length;
        if (summary.total !== total) {
            summary.total = total;
            setText(this._assetCount, String(total));
        }
        if (summary.air !== air) {
            summary.air = air;
            setText(this._airCount, String(air));
        }
        if (summary.ground !== ground) {
            summary.ground = ground;
            setText(this._groundCount, String(ground));
        }
        if (summary.surface !== surface) {
            summary.surface = surface;
            setText(this._surfaceCount, String(surface));
        }
        this._setBattery(airPowerCount === 0 ? null : airPowerTotal / airPowerCount, false);
        return summary;
    }

    updateDrones(count: number, time: number, drones: DroneState[]): void {
        setText(this._droneCount, String(count));
        this.updateTime(time);
        this._updateDroneBattery(drones);
    }

    selectAsset(id: string | null): void {
        this._setSelection(id, id === null ? null : 'asset');
    }

    setSelectedDrone(id: string | null): void {
        this._setSelection(id, id === null ? null : 'drone');
    }

    private _updateDroneBattery(drones: DroneState[]): void {
        if (drones.length === 0) {
            this._setBattery(null, true);
            return;
        }
        const avg = drones.reduce((s, d) => s + (d.battery ?? 100), 0) / drones.length;
        this._setBattery(avg, true);
    }

    private _setBattery(average: number | null, legacyUnknown: boolean): void {
        if (this._batteryReady
            && Object.is(this._batteryAverage, average)
            && this._batteryLegacyUnknown === legacyUnknown) return;
        this._batteryReady = true;
        this._batteryAverage = average;
        this._batteryLegacyUnknown = legacyUnknown;
        const text = average === null ? '--%' : `${average.toFixed(0)}%`;
        const width = average === null ? (legacyUnknown ? '100%' : '0%') : `${average}%`;
        const className = average === null ? '' : average < 20 ? 'crit' : average < 40 ? 'warn' : '';
        setText(this._pct, text);
        if (this._fill.style.width !== width) this._fill.style.width = width;
        if (this._fill.className !== className) this._fill.className = className;
    }

    private _setCountBranch(branch: HTMLElement, visible: boolean): void {
        const hidden = !visible;
        if (branch.hidden !== hidden) branch.hidden = hidden;
        setAttribute(branch, 'aria-hidden', String(hidden));
    }

    private _setSelection(id: string | null, kind: 'asset' | 'drone' | null): void {
        this._selectedId = id;
        this._selectedKind = kind;
        this._renderSelection();
    }

    private _renderSelection(): void {
        const id = this._selectedId;
        if (this._mode === 'booting' || id === null) {
            setText(this._selText, '');
            setAttribute(this._selChip, 'title', '');
            if (!this._selChip.classList.contains('hidden')) this._selChip.classList.add('hidden');
            return;
        }

        const legacyDrone = this._mode === 'legacy' && this._selectedKind === 'drone';
        setText(this._selText, legacyDrone ? `◎ ${id}` : `Asset · ${id}`);
        setAttribute(
            this._selChip,
            'title',
            legacyDrone
                ? 'Selected drone — WASD/QE to nudge, click terrain to move'
                : 'Selected asset',
        );
        if (this._selChip.classList.contains('hidden')) this._selChip.classList.remove('hidden');
    }
}
