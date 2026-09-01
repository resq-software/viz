// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The composed fleet surface. `AssetPanel` and `AssetFilter` each have their own
// tests; what is asserted here is the contract `app.ts` leans on and that neither
// widget can state alone:
//
//   * `update` takes EVERY asset and returns the visible ones, so filtering is
//     one decision made in one place — the scene, the plot, the outliner, the
//     panel and keyboard cycling all consume the same subset;
//   * `visibleIds` is what `[` / `]` walks, so a filtered-out asset cannot be
//     reached by a keyboard operator who cannot see it;
//   * the spoken summary names domains and counts what the filter is hiding,
//     because "nothing in view" and "six assets, all filtered out" call for
//     different actions from someone who cannot see the scene;
//   * a contact renders with no command affordance at all.

import { beforeEach, describe, expect, it } from 'vitest';

import { FleetUi } from '../assets/fleetUi';
import type { SceneAsset } from '../assets/sceneFrame';
import type { AssetDescriptor, AssetState, ExternalTrackState } from '../assets/types';
import {
  AssetDomain,
  CoordinateFrame,
  DataFreshness,
  LinkTransport,
  OperationalState,
  TrackClassification,
  VehicleClass,
} from '../assets/types';

const T0 = '2026-08-30T12:00:00.000Z';

function sceneAsset(
  id: string,
  domain: AssetDomain,
  over: { operationalState?: OperationalState; freshness?: DataFreshness } = {},
): SceneAsset {
  const vehicleClass = domain === AssetDomain.Air ? VehicleClass.Multirotor
    : domain === AssetDomain.Ground ? VehicleClass.AckermannRover
      : VehicleClass.SurfaceVessel;

  const descriptor: AssetDescriptor = {
    assetId: id,
    displayName: id,
    domain,
    vehicleClass,
    mobilityModel: 'test',
    agencyId: null,
    fleetId: null,
    vendor: null,
    model: null,
    capabilities: 0,
    dimensions: { lengthM: 1, widthM: 1, heightM: 1, massKg: 1, footprintRadiusM: 1 },
    motion: {
      minSpeedMps: 0,
      maxSpeedMps: 10,
      minTurnRadiusM: 0,
      canStationKeep: true,
      passiveDriftMps: 0,
      stationKeepCostW: 0,
    },
    visualProfile: '',
    revision: 1,
  };
  const state: AssetState = {
    assetId: id,
    sourceTime: T0,
    receiveTime: T0,
    sequenceNumber: 1,
    freshness: over.freshness ?? DataFreshness.Fresh,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 0, y: 0, z: 0 },
      orientation: { x: 0, y: 0, z: 0, w: 1 },
      covariance: null,
      geo: null,
    },
    twist: {
      frame: CoordinateFrame.LocalEus,
      linear: { x: 0, y: 0, z: 0 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    operationalState: over.operationalState ?? OperationalState.Active,
    mode: 'test',
    power: {
      sources: [],
      percentRemaining: 50,
      remainingEnergyWh: null,
      remainingTime: null,
      isExternallyPowered: false,
      isCharging: false,
    },
    health: { overall: 1, components: [], faults: [], summary: 'ok' },
    link: {
      transport: LinkTransport.Loopback,
      isConnected: true,
      latencyMs: null,
      packetLossRatio: null,
      signalDbm: null,
      signalQuality: null,
      meshPath: null,
      lastHeardAt: null,
    },
    mission: null,
    domainState: null,
  };
  return {
    descriptor,
    state,
    view: {
      id,
      displayName: id,
      domain,
      vehicleClass,
      visualProfile: '',
      capabilities: 0,
      position: [0, 0, 0],
      orientation: null,
      velocity: [0, 0, 0],
      operationalState: state.operationalState,
      mode: 'test',
      freshness: state.freshness,
      ageSeconds: 0,
      powerPercent: 50,
      vendor: null,
      domainState: null,
    },
  };
}

const track: ExternalTrackState = {
  trackId: 'trk-1',
  classification: TrackClassification.Vessel,
  pose: {
    frame: CoordinateFrame.LocalEus,
    originId: null,
    position: { x: 10, y: 0, z: 10 },
    orientation: { x: 0, y: 0, z: 0, w: 0 },
    covariance: null,
    geo: null,
  },
  twist: {
    frame: CoordinateFrame.LocalEus,
    linear: { x: 1, y: 0, z: 0 },
    angular: { x: 0, y: 0, z: 0 },
    originId: null,
    covariance: null,
  },
  sources: [],
  quality: {
    confidence: 0.7,
    positionAccuracyM: null,
    velocityAccuracyMps: null,
    updateCount: 2,
    isFused: false,
  },
  lastUpdateTime: T0,
  freshness: DataFreshness.Fresh,
  label: 'Contact Alpha',
  transponder: null,
};

/** A fleet UI with no server and no persistence behind it: capabilities resolve
 *  to null, which the panel renders as "no commands offered" rather than guessing
 *  a set, and the facet selection starts unconstrained every time. */
function makeUi(): FleetUi {
  return new FleetUi({ loadCapabilities: async () => null, filterStorage: null });
}

const FLEET = [
  sceneAsset('air-1', AssetDomain.Air),
  sceneAsset('rover-1', AssetDomain.Ground),
  sceneAsset('usv-1', AssetDomain.Surface),
];

describe('FleetUi', () => {
  beforeEach(() => {
    // Both widgets append themselves to the body; without this a `querySelectorAll`
    // would find the previous test's panel as well as this one's.
    document.body.replaceChildren();
  });

  it('returns every asset when nothing is filtered', () => {
    const ui = makeUi();
    expect(ui.update(FLEET).map((a) => a.view.id)).toEqual(['air-1', 'rover-1', 'usv-1']);
    ui.dispose();
  });

  it('narrows the fleet to the selected domains, in publication order', () => {
    const ui = makeUi();
    ui.update(FLEET);
    ui.filter.setSelection({ domain: ['ground', 'surface'] });
    const visible = ui.update(FLEET);

    expect(visible.map((a) => a.view.id)).toEqual(['rover-1', 'usv-1']);
    expect(ui.isVisible('air-1')).toBe(false);
    expect(ui.isVisible('rover-1')).toBe(true);
    ui.dispose();
  });

  it('walks only the visible assets, so cycling cannot reach what is hidden', () => {
    const ui = makeUi();
    ui.update(FLEET);
    ui.filter.setSelection({ domain: ['air'] });
    ui.update(FLEET);

    expect(ui.visibleIds()).toEqual(['air-1']);
    ui.dispose();
  });

  it('names every domain present in the spoken summary', () => {
    const ui = makeUi();
    ui.update(FLEET);
    const text = ui.summaryText();

    expect(text).toContain('3 assets');
    expect(text).toContain('air');
    expect(text).toContain('ground');
    expect(text).toContain('surface');
    ui.dispose();
  });

  it('says how many the filter is holding back, not merely how many are shown', () => {
    const ui = makeUi();
    ui.update(FLEET);
    ui.filter.setSelection({ domain: ['air'] });
    ui.update(FLEET);

    expect(ui.summaryText()).toContain('2 hidden by the fleet filter');
    ui.dispose();
  });

  it('distinguishes an empty fleet from a fully filtered one', () => {
    const ui = makeUi();
    ui.update([]);
    expect(ui.summaryText()).toBe('No assets in view.');

    ui.update(FLEET);
    ui.filter.setSelection({ domain: ['fixed'] });
    ui.update(FLEET);
    expect(ui.summaryText()).toContain('3 hidden by the fleet filter');
    ui.dispose();
  });

  it('counts assets needing attention', () => {
    const ui = makeUi();
    ui.update([
      sceneAsset('air-1', AssetDomain.Air),
      sceneAsset('rover-1', AssetDomain.Ground, {
        operationalState: OperationalState.Emergency,
      }),
    ]);
    expect(ui.summaryText()).toContain('1 needing attention');
    ui.dispose();
  });

  it('renders a contact with no command affordance whatsoever', () => {
    const ui = makeUi();
    ui.renderSubject({ kind: 'track', track }, Date.parse(T0));

    expect(ui.subjectId).toBe('trk-1');
    expect(ui.panel.element.querySelectorAll('.ap-cmd')).toHaveLength(0);
    expect(ui.panel.element.querySelectorAll('.ap-cmd-btn')).toHaveLength(0);
    expect(ui.panel.element.textContent).toContain('not commandable');
    ui.dispose();
  });

  it('hides the panel when nothing is selected', () => {
    const ui = makeUi();
    ui.renderSubject({ kind: 'track', track }, Date.parse(T0));
    ui.renderSubject(null);
    expect(ui.subjectId).toBeNull();
    expect(ui.panel.element.hidden).toBe(true);
    ui.dispose();
  });
});
