// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import { assetTelemetryText, Hud } from '../ui/hud';
import type { SceneAsset } from '../assets/sceneFrame';
import { AssetDomain } from '../assets/types';
import type { DroneState } from '../types';

function installHudFixture(doc: Document): void {
  doc.body.innerHTML = `
    <span id="conn-dot" class="conn-dot"></span>
    <span id="conn-label"></span>
    <div id="hud-count-v1" class="hud-stat hud-count-branch" data-hud-mode="legacy">
      <span id="drone-count">0</span>
    </div>
    <div id="hud-count-v2" class="hud-stat hud-count-branch hud-stat-assets" data-hud-mode="v2">
      <span id="asset-count">0</span>
      <span id="air-count">0</span>
      <span id="ground-count">0</span>
      <span id="surface-count">0</span>
    </div>
    <span id="fps">--</span>
    <span id="sim-time">0.0s</span>
    <div id="hud-battery-stat" title="Fleet battery average">
      <div id="battery-fill"></div>
      <span id="battery-pct">--%</span>
    </div>
    <div id="hud-selected-drone" class="hud-selected hidden">
      <span id="hud-selected-asset"></span>
    </div>
    <div id="a11y-telemetry">sentinel</div>
  `;
}

function asset(id: string, domain: number, percentRemaining: number | null): SceneAsset {
  return {
    view: { id, displayName: id, domain },
    descriptor: { assetId: id, domain },
    state: { power: { percentRemaining } },
  } as unknown as SceneAsset;
}

function drone(id: string, battery?: number): DroneState {
  return {
    id,
    pos: [0, 0, 0],
    rot: [0, 0, 0, 1],
    vel: [0, 0, 0],
    ...(battery === undefined ? {} : { battery }),
  };
}

function value(doc: Document, id: string): string | null {
  return doc.getElementById(id)?.textContent ?? null;
}

beforeEach(() => installHudFixture(document));

describe('v2 mixed-domain HUD', () => {
  it('counts the complete inventory and averages only reported Air power', () => {
    const hud = new Hud(document);
    const assets = [
      asset('air-1', AssetDomain.Air, 80),
      asset('air-2', AssetDomain.Air, 100),
      asset('ground-1', AssetDomain.Ground, 5),
      asset('ground-2', AssetDomain.Ground, 5),
      asset('ground-3', AssetDomain.Ground, 5),
      asset('surface-1', AssetDomain.Surface, 5),
    ];

    const summary = hud.updateAssets(assets);
    hud.updateTime(18.24);

    expect(summary).toEqual({ total: 6, air: 2, ground: 3, surface: 1 });
    expect(value(document, 'asset-count')).toBe('6');
    expect(value(document, 'air-count')).toBe('2');
    expect(value(document, 'ground-count')).toBe('3');
    expect(value(document, 'surface-count')).toBe('1');
    expect(value(document, 'battery-pct')).toBe('90%');
    expect(value(document, 'sim-time')).toBe('18.2s');
  });

  it('keeps fixed and future domains in total without misclassifying them', () => {
    const hud = new Hud(document);

    const summary = hud.updateAssets([
      asset('air-1', AssetDomain.Air, 50),
      asset('fixed-1', AssetDomain.Fixed, 1),
      asset('future-1', 999, 1),
    ]);

    expect(summary).toEqual({ total: 3, air: 1, ground: 0, surface: 0 });
  });

  it('ignores null Air power but includes a reported zero', () => {
    const hud = new Hud(document);

    hud.updateAssets([
      asset('air-unmetered', AssetDomain.Air, null),
      asset('air-empty', AssetDomain.Air, 0),
      asset('surface-low', AssetDomain.Surface, 90),
    ]);

    expect(value(document, 'battery-pct')).toBe('0%');
    expect(document.getElementById('battery-fill')?.style.width).toBe('0%');
    expect(document.getElementById('battery-fill')?.className).toBe('crit');
  });

  it('renders unmetered Air power as unknown without implying a full battery', () => {
    const hud = new Hud(document);

    hud.updateAssets([
      asset('air-unmetered', AssetDomain.Air, null),
      asset('ground-metered', AssetDomain.Ground, 100),
    ]);

    expect(value(document, 'battery-pct')).toBe('--%');
    expect(document.getElementById('battery-fill')?.style.width).toBe('0%');
    expect(document.getElementById('battery-fill')?.className).toBe('');
  });

  it('returns one reused summary record rather than allocating per frame', () => {
    const hud = new Hud(document);
    const first = hud.updateAssets([asset('air-1', AssetDomain.Air, 50)]);
    const second = hud.updateAssets([asset('ground-1', AssetDomain.Ground, 50)]);

    expect(second).toBe(first);
    expect(second).toEqual({ total: 1, air: 0, ground: 1, surface: 0 });
  });

  it('does not write the app-owned live region', () => {
    const hud = new Hud(document);
    hud.updateAssets([asset('air-1', AssetDomain.Air, 50)]);

    expect(value(document, 'a11y-telemetry')).toBe('sentinel');
  });
});

describe('HUD mode and compatibility paths', () => {
  it('starts with no count branch exposed and ignores boot-time v1 frame visibility', () => {
    const hud = new Hud(document);
    const legacy = document.getElementById('hud-count-v1')!;
    const v2 = document.getElementById('hud-count-v2')!;

    hud.updateDrones(1, 2, [drone('legacy-1', 80)]);

    expect(legacy.hidden).toBe(true);
    expect(v2.hidden).toBe(true);
    expect(legacy.getAttribute('aria-hidden')).toBe('true');
    expect(v2.getAttribute('aria-hidden')).toBe('true');
  });

  it.each([
    { mode: 'v2' as const, shown: 'hud-count-v2', hidden: 'hud-count-v1' },
    { mode: 'legacy' as const, shown: 'hud-count-v1', hidden: 'hud-count-v2' },
  ])('shows only the $mode count branch', ({ mode, shown, hidden }) => {
    const hud = new Hud(document);

    hud.setMode(mode);

    expect(document.getElementById(shown)?.hidden).toBe(false);
    expect(document.getElementById(shown)?.getAttribute('aria-hidden')).toBe('false');
    expect(document.getElementById(hidden)?.hidden).toBe(true);
    expect(document.getElementById(hidden)?.getAttribute('aria-hidden')).toBe('true');
  });

  it('retains legacy drone count, time, and battery semantics', () => {
    const hud = new Hud(document);
    hud.setMode('legacy');

    hud.updateDrones(2, 12.34, [drone('reported-zero', 0), drone('missing')]);

    expect(value(document, 'drone-count')).toBe('2');
    expect(value(document, 'sim-time')).toBe('12.3s');
    expect(value(document, 'battery-pct')).toBe('50%');
    expect(document.getElementById('battery-fill')?.style.width).toBe('50%');

    hud.updateDrones(0, 0, []);
    expect(value(document, 'battery-pct')).toBe('--%');
    expect(document.getElementById('battery-fill')?.style.width).toBe('100%');
  });

  it('uses domain-neutral selected copy in v2 and keeps the piloting hint legacy-only', () => {
    const hud = new Hud(document);
    const chip = document.getElementById('hud-selected-drone')!;

    hud.setMode('v2');
    hud.selectAsset('ground-1');
    expect(value(document, 'hud-selected-asset')).toBe('Asset · ground-1');
    expect(chip.title).toBe('Selected asset');
    expect(chip.classList.contains('hidden')).toBe(false);

    hud.setSelectedDrone('air-1');
    expect(value(document, 'hud-selected-asset')).toBe('Asset · air-1');
    expect(chip.title).toBe('Selected asset');

    hud.setMode('legacy');
    expect(value(document, 'hud-selected-asset')).toBe('◎ air-1');
    expect(chip.title).toContain('WASD/QE to nudge');

    hud.setSelectedDrone(null);
    expect(value(document, 'hud-selected-asset')).toBe('');
    expect(chip.title).toBe('');
    expect(chip.classList.contains('hidden')).toBe(true);
  });

  it('resolves and updates only the injected Document', () => {
    const isolated = document.implementation.createHTMLDocument('isolated HUD');
    installHudFixture(isolated);
    const hud = new Hud(isolated);

    hud.setMode('legacy');
    hud.updateDrones(4, 3, [drone('isolated', 25)]);

    expect(value(isolated, 'drone-count')).toBe('4');
    expect(value(isolated, 'battery-pct')).toBe('25%');
    expect(value(document, 'drone-count')).toBe('0');
    expect(value(document, 'battery-pct')).toBe('--%');
  });

  it('performs no DOM writes for an identical 10 Hz update', async () => {
    const hud = new Hud(document);
    const assets = [asset('air-1', AssetDomain.Air, 80)];
    hud.setMode('v2');
    hud.updateAssets(assets);
    hud.updateTime(4);
    hud.selectAsset('air-1');
    const mutations: MutationRecord[] = [];
    const observer = new MutationObserver(records => mutations.push(...records));
    observer.observe(document.body, {
      attributes: true,
      childList: true,
      characterData: true,
      subtree: true,
    });

    hud.setMode('v2');
    hud.updateAssets(assets);
    hud.updateTime(4);
    hud.selectAsset('air-1');
    await Promise.resolve();
    observer.disconnect();

    expect(mutations).toEqual([]);
  });
});

describe('asset telemetry accessibility copy', () => {
  it('names total and each supported domain', () => {
    expect(assetTelemetryText({ total: 6, air: 2, ground: 3, surface: 1 }, 18.2))
      .toBe('6 assets total: 2 air, 3 ground, 1 surface. Simulation time 18 seconds.');
    expect(assetTelemetryText({ total: 1, air: 1, ground: 0, surface: 0 }, 1))
      .toBe('1 asset total: 1 air, 0 ground, 0 surface. Simulation time 1 second.');
    expect(assetTelemetryText({ total: 0, air: 0, ground: 0, surface: 0 }, 0))
      .toBe('No active assets.');
  });

  it('ships stable grouped markup, full screen-reader labels, and hidden-wins CSS', () => {
    const index = readFileSync(resolve(process.cwd(), 'client/index.html'), 'utf8');
    const css = readFileSync(resolve(process.cwd(), 'client/styles/main.css'), 'utf8');

    for (const id of ['asset-count', 'air-count', 'ground-count', 'surface-count']) {
      expect(index).toContain(`id="${id}"`);
    }
    for (const label of ['Total assets:', 'Air assets:', 'Ground assets:', 'Surface assets:']) {
      expect(index).toContain(`<span class="sr-only">${label}</span>`);
    }
    expect(index).toMatch(/id="hud-count-v2"[^>]*class="[^"]*hud-stat-assets/);
    expect(index).toMatch(/id="hud-count-v1"[^>]*data-hud-mode="legacy"[^>]*hidden/);
    expect(index).toMatch(/id="hud-count-v2"[^>]*data-hud-mode="v2"[^>]*hidden/);
    expect(css).toMatch(/\.hud-count-branch\[hidden\]\s*\{\s*display:\s*none\s*!important;/);
  });
});
