// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The fleet filter: the pure narrowing and counting first, then the control that
// wraps them. Behavioural throughout — what a selection lets through, what a
// facet offers, what the live region would say — rather than markup-exact.

import { describe, expect, it, vi } from 'vitest';

import {
  AssetFilter,
  applyFilter,
  computeFacets,
  domainLabel,
  emptySelection,
  filterableFromV2,
  filterableFromView,
  fleetSummaryText,
  humanise,
  loadSelection,
  matchesFilter,
  saveSelection,
} from '../assets/AssetFilter';
import type { FilterableAsset, SelectionStorage } from '../assets/AssetFilter';
import type { AssetView } from '../assets/assetView';
import { AssetDomain, DataFreshness, OperationalState, VehicleClass } from '../assets/types';

function asset(id: string, over: Partial<FilterableAsset> = {}): FilterableAsset {
  return {
    id,
    displayName: id,
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    agencyId: 'coastguard',
    fleetId: 'alpha',
    operationalState: OperationalState.Active,
    freshness: DataFreshness.Fresh,
    ...over,
  };
}

const MIXED: FilterableAsset[] = [
  asset('air-1'),
  asset('air-2', { freshness: DataFreshness.Stale }),
  asset('rover-1', {
    domain: AssetDomain.Ground,
    vehicleClass: VehicleClass.AckermannRover,
    agencyId: 'fire',
    fleetId: null,
  }),
  asset('vessel-1', {
    domain: AssetDomain.Surface,
    vehicleClass: VehicleClass.SurfaceVessel,
    operationalState: OperationalState.Emergency,
  }),
];

function memoryStorage(): SelectionStorage & { readonly data: Map<string, string> } {
  const data = new Map<string, string>();
  return {
    data,
    getItem: (k: string) => data.get(k) ?? null,
    setItem: (k: string, v: string) => { data.set(k, v); },
  };
}

describe('humanise', () => {
  it('spaces PascalCase wire names', () => {
    expect(humanise('AckermannRover')).toBe('Ackermann rover');
    expect(humanise('SmallUnmannedAircraft')).toBe('Small unmanned aircraft');
  });

  // The command kinds are camelCase, and labelling them this way is what lets
  // AssetPanel drop its own label table: a kind the server adds tomorrow reads
  // correctly today, with no second catalog to drift.
  it('spaces camelCase command kinds', () => {
    expect(humanise('resumeAutonomy')).toBe('Resume autonomy');
    expect(humanise('emergencyStop')).toBe('Emergency stop');
    expect(humanise('goTo')).toBe('Go to');
  });
});

describe('domainLabel', () => {
  it('names every implemented domain', () => {
    expect(domainLabel(AssetDomain.Air)).toBe('Air');
    expect(domainLabel(AssetDomain.Ground)).toBe('Ground');
    expect(domainLabel(AssetDomain.Surface)).toBe('Surface');
  });

  it('does not invent a name for a value it does not know', () => {
    expect(domainLabel(99)).toBe('Unknown (99)');
  });
});

describe('matchesFilter / applyFilter', () => {
  it('treats an empty facet as unconstrained rather than as matching nothing', () => {
    expect(applyFilter(MIXED, emptySelection())).toHaveLength(MIXED.length);
  });

  it('narrows within a facet by union', () => {
    const selection = { ...emptySelection(), domain: ['ground', 'surface'] };
    expect(applyFilter(MIXED, selection).map((a) => a.id)).toEqual(['rover-1', 'vessel-1']);
  });

  it('narrows across facets by intersection', () => {
    const selection = { ...emptySelection(), domain: ['air'], freshness: ['stale'] };
    expect(applyFilter(MIXED, selection).map((a) => a.id)).toEqual(['air-2']);
  });

  it('files an unreported agency under the unassigned token, not under a made-up id', () => {
    const selection = { ...emptySelection(), fleet: ['(unassigned)'] };
    expect(applyFilter(MIXED, selection).map((a) => a.id)).toEqual(['rover-1']);
    expect(matchesFilter(asset('x', { fleetId: null }), selection)).toBe(true);
  });
});

describe('computeFacets', () => {
  it('offers every value present in the fleet', () => {
    const facets = computeFacets(MIXED, emptySelection());
    const domain = facets.find((f) => f.key === 'domain');
    expect(domain?.values.map((v) => v.token)).toEqual(['air', 'ground', 'surface']);
    expect(domain?.values.map((v) => v.count)).toEqual([2, 1, 1]);
  });

  // Counting a facet against itself would show 0 beside every unticked value and
  // make the control look broken the moment one box is checked.
  it('counts a facet against the other facets, not against itself', () => {
    const selection = { ...emptySelection(), domain: ['air'] };
    const domain = computeFacets(MIXED, selection).find((f) => f.key === 'domain');
    expect(domain?.values.find((v) => v.token === 'ground')?.count).toBe(1);
    expect(domain?.values.find((v) => v.token === 'air')?.selected).toBe(true);
  });

  it('keeps a selected value on offer after its last asset leaves', () => {
    const selection = { ...emptySelection(), domain: ['subsurface'] };
    const domain = computeFacets(MIXED, selection).find((f) => f.key === 'domain');
    const orphan = domain?.values.find((v) => v.token === 'subsurface');
    expect(orphan).toBeDefined();
    expect(orphan?.count).toBe(0);
    expect(orphan?.selected).toBe(true);
  });
});

describe('fleetSummaryText', () => {
  it('counts by domain rather than announcing drones', () => {
    const text = fleetSummaryText(MIXED);
    expect(text).toContain('4 assets');
    expect(text).toContain('2 air');
    expect(text).toContain('1 ground');
    expect(text).toContain('1 surface');
    expect(text).not.toContain('drone');
  });

  it('calls out assets needing attention and degraded telemetry', () => {
    const text = fleetSummaryText(MIXED);
    expect(text).toContain('1 needing attention');
    expect(text).toContain('1 with degraded telemetry');
  });

  it('says so when a filter is hiding everything', () => {
    expect(fleetSummaryText([], 4)).toBe('No assets shown; 4 hidden by the fleet filter.');
    expect(fleetSummaryText([])).toBe('No assets in view.');
  });
});

describe('projections', () => {
  it('takes agency and fleet from the descriptor', () => {
    const descriptor = {
      assetId: 'v1', displayName: 'Vessel One', domain: AssetDomain.Surface,
      vehicleClass: VehicleClass.SurfaceVessel, mobilityModel: 'displacement-hull',
      agencyId: 'port', fleetId: 'harbour', vendor: null, model: null, capabilities: 0,
      dimensions: { lengthM: 8, widthM: 3, heightM: 2, massKg: 900, footprintRadiusM: 4 },
      motion: {
        minSpeedMps: 0.5, maxSpeedMps: 9, minTurnRadiusM: 12,
        canStationKeep: true, passiveDriftMps: 0.3, stationKeepCostW: 400,
      },
      visualProfile: 'usv', revision: 1,
    };
    const state = {
      assetId: 'v1', operationalState: OperationalState.Holding, freshness: DataFreshness.Stale,
    } as unknown as Parameters<typeof filterableFromV2>[1];

    const projected = filterableFromV2(descriptor, state);
    expect(projected.agencyId).toBe('port');
    expect(projected.fleetId).toBe('harbour');
    expect(projected.operationalState).toBe(OperationalState.Holding);
  });

  // The v1 stream has no descriptor at all, so agency and fleet must come back
  // null — never a fabricated default that would file every drone under one fleet.
  it('reports agency and fleet as unknown when there is no descriptor', () => {
    const view = {
      id: 'd1', displayName: 'd1', domain: AssetDomain.Air,
      vehicleClass: VehicleClass.Multirotor, operationalState: OperationalState.Active,
      freshness: DataFreshness.Fresh,
    } as AssetView;
    const projected = filterableFromView(view);
    expect(projected.agencyId).toBeNull();
    expect(projected.fleetId).toBeNull();
  });
});

describe('persistence', () => {
  it('round-trips a selection', () => {
    const storage = memoryStorage();
    saveSelection({ ...emptySelection(), domain: ['ground'] }, storage);
    expect(loadSelection(storage).domain).toEqual(['ground']);
  });

  it('discards a payload from an older schema instead of half-reading it', () => {
    const storage = memoryStorage();
    storage.setItem('resq-viz-asset-filter', JSON.stringify({ domain: ['ground'], _v: 0 }));
    expect(loadSelection(storage).domain).toEqual([]);
  });

  it('survives unreadable storage', () => {
    const storage: SelectionStorage = {
      getItem: () => { throw new Error('blocked'); },
      setItem: () => { throw new Error('blocked'); },
    };
    expect(() => loadSelection(storage)).not.toThrow();
    expect(() => saveSelection(emptySelection(), storage)).not.toThrow();
  });
});

describe('AssetFilter control', () => {
  it('renders one checkbox per offered value and filters on toggle', () => {
    const mount = document.createElement('div');
    const filter = new AssetFilter({ mount, storage: null });
    const onChange = vi.fn();
    filter.onChange(onChange);
    filter.update(MIXED);

    const domainBoxes = mount.querySelectorAll<HTMLInputElement>(
      '[data-facet="domain"] .af-chip-input',
    );
    expect(Array.from(domainBoxes, (b) => b.value)).toEqual(['air', 'ground', 'surface']);

    const ground = Array.from(domainBoxes).find((b) => b.value === 'ground');
    ground!.checked = true;
    ground!.dispatchEvent(new Event('change'));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(filter.apply(MIXED).map((a) => a.id)).toEqual(['rover-1']);
    filter.dispose();
  });

  it('hides a facet that offers no choice, but not one carrying a constraint', () => {
    const mount = document.createElement('div');
    const filter = new AssetFilter({ mount, storage: null });
    filter.update([asset('air-1'), asset('air-2')]);

    const domain = mount.querySelector<HTMLElement>('[data-facet="domain"]');
    expect(domain?.hidden).toBe(true);

    filter.setSelection({ domain: ['air'] });
    filter.update([asset('air-1'), asset('air-2')]);
    expect(domain?.hidden).toBe(false);
    filter.dispose();
  });

  it('does not notify listeners just because a frame arrived', () => {
    const mount = document.createElement('div');
    const filter = new AssetFilter({ mount, storage: null });
    const onChange = vi.fn();
    filter.onChange(onChange);
    filter.update(MIXED);
    filter.update(MIXED);
    expect(onChange).not.toHaveBeenCalled();
    filter.dispose();
  });
});
