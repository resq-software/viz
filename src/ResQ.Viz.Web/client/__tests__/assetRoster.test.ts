// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { beforeEach, describe, expect, it, vi } from 'vitest';

import { AssetRoster } from '../operator/AssetRoster';
import type { RosterInput } from '../operator/AssetRoster';
import type { SceneAsset } from '../assets/sceneFrame';
import type { ExternalTrackState } from '../assets/types';
import {
  AssetDomain,
  ComponentHealthStatus,
  DataFreshness,
  OperationalState,
  TrackClassification,
  TrackSourceKind,
  VehicleClass,
} from '../assets/types';
import { emptySelection } from '../assets/AssetFilter';

function rosterAsset(
  id: string,
  domain: AssetDomain,
  over: {
    displayName?: string;
    vendor?: string | null;
    agencyId?: string | null;
    fleetId?: string | null;
    operationalState?: OperationalState;
    health?: ComponentHealthStatus;
    healthSummary?: string;
    position?: [number, number, number];
  } = {},
): SceneAsset {
  const vehicleClass = domain === AssetDomain.Air ? VehicleClass.Multirotor
    : domain === AssetDomain.Ground ? VehicleClass.AckermannRover
      : VehicleClass.SurfaceVessel;
  const operationalState = over.operationalState ?? OperationalState.Active;
  return {
    view: {
      id,
      displayName: over.displayName ?? id,
      domain,
      vehicleClass,
      position: over.position ?? [0, 0, 0],
      operationalState,
      freshness: DataFreshness.Fresh,
    },
    descriptor: {
      assetId: id,
      displayName: over.displayName ?? id,
      domain,
      vehicleClass,
      vendor: over.vendor ?? null,
      agencyId: over.agencyId ?? null,
      fleetId: over.fleetId ?? null,
    },
    state: {
      assetId: id,
      operationalState,
      freshness: DataFreshness.Fresh,
      health: {
        overall: over.health ?? ComponentHealthStatus.Nominal,
        summary: over.healthSummary ?? 'Nominal',
        faults: [],
      },
    },
  } as unknown as SceneAsset;
}

function rosterTrack(
  id: string,
  over: {
    label?: string | null;
    sourceId?: string;
    sourceKind?: TrackSourceKind;
    classification?: TrackClassification;
  } = {},
): ExternalTrackState {
  return {
    trackId: id,
    label: over.label ?? id,
    classification: over.classification ?? TrackClassification.Vessel,
    freshness: DataFreshness.Fresh,
    sources: [{
      sourceId: over.sourceId ?? 'radar-1',
      kind: over.sourceKind ?? TrackSourceKind.Radar,
      observedAt: '2026-09-01T00:00:00Z',
      quality: 1,
    }],
  } as unknown as ExternalTrackState;
}

interface QueuedFrameScheduler {
  readonly schedule: ReturnType<typeof vi.fn<(callback: () => void) => number>>;
  readonly cancel: ReturnType<typeof vi.fn<(handle: number) => void>>;
  flush(): void;
}

function queuedFrames(): QueuedFrameScheduler {
  let next = 1;
  const callbacks = new Map<number, () => void>();
  const schedule = vi.fn((callback: () => void) => {
    const handle = next++;
    callbacks.set(handle, callback);
    return handle;
  });
  const cancel = vi.fn((handle: number) => { callbacks.delete(handle); });
  return {
    schedule,
    cancel,
    flush: () => {
      const pending = [...callbacks.values()];
      callbacks.clear();
      for (const callback of pending) callback();
    },
  };
}

const ASSETS = [
  rosterAsset('shared', AssetDomain.Air, {
    displayName: 'Air One', vendor: 'Anzu', agencyId: 'agency-1', fleetId: 'alpha',
  }),
  rosterAsset('ground-1', AssetDomain.Ground, {
    displayName: 'Ground One', operationalState: OperationalState.Holding,
    health: ComponentHealthStatus.Warning, healthSummary: 'Wheel motor warning',
  }),
  rosterAsset('surface-1', AssetDomain.Surface),
];
const CONTACTS = [rosterTrack('shared', {
  label: 'Contact Alpha', sourceId: 'coastal-radar', classification: TrackClassification.Vessel,
})];

function input(over: Partial<RosterInput> = {}): RosterInput {
  return {
    assets: ASSETS,
    contacts: CONTACTS,
    assetFilter: emptySelection(),
    query: '',
    selected: null,
    ...over,
  };
}

function makeRoster() {
  const mount = document.createElement('div');
  const scheduler = queuedFrames();
  const selectAsset = vi.fn();
  const selectTrack = vi.fn();
  const onQueryChange = vi.fn();
  const onClearFilters = vi.fn();
  const onFocusFallback = vi.fn();
  document.body.appendChild(mount);
  const roster = new AssetRoster({
    mount,
    selectAsset,
    selectTrack,
    onQueryChange,
    onClearFilters,
    onFocusFallback,
    scheduleFrame: scheduler.schedule,
    cancelFrame: scheduler.cancel,
  });
  return {
    mount, roster, scheduler, selectAsset, selectTrack,
    onQueryChange, onClearFilters, onFocusFallback,
  };
}

beforeEach(() => document.body.replaceChildren());

describe('AssetRoster keyed reconciliation', () => {
  it('creates its subtree through the explicit mount owner document', () => {
    const foreign = document.implementation.createHTMLDocument('roster');
    const mount = foreign.createElement('div');
    const create = vi.spyOn(foreign, 'createElement');
    const roster = new AssetRoster({
      mount,
      selectAsset: vi.fn(),
      selectTrack: vi.fn(),
      onQueryChange: vi.fn(),
      onClearFilters: vi.fn(),
      scheduleFrame: callback => { callback(); return 0; },
      cancelFrame: vi.fn(),
    });

    expect(create).toHaveBeenCalled();
    expect(roster.element.ownerDocument).toBe(foreign);
    roster.dispose();
  });

  it('keeps asset and track identifier spaces distinct and routes only the matching callback', () => {
    const h = makeRoster();
    h.roster.update(input());
    h.scheduler.flush();

    const asset = h.roster.rowFor('asset', 'shared');
    const track = h.roster.rowFor('track', 'shared');
    expect(asset).not.toBeNull();
    expect(track).not.toBeNull();
    expect(asset).not.toBe(track);
    expect([...h.mount.querySelectorAll('.ar-group-heading')].map(node => node.textContent))
      .toEqual(['Assets3 matching', 'Observed contacts1 matching']);

    asset!.click();
    expect(h.selectAsset).toHaveBeenCalledWith('shared');
    expect(h.selectTrack).not.toHaveBeenCalled();
    track!.click();
    expect(h.selectTrack).toHaveBeenCalledWith('shared');
    expect(h.selectAsset).toHaveBeenCalledTimes(1);
  });

  it('coalesces writes to the newest input while retaining row identity, focus, and scroll', () => {
    const h = makeRoster();
    h.roster.update(input());
    h.scheduler.flush();
    const row = h.roster.rowFor('asset', 'shared')!;
    const scroll = h.mount.querySelector<HTMLElement>('.ar-scroll')!;
    row.focus();
    scroll.scrollTop = 37;

    h.roster.update(input({
      assets: ASSETS.map(asset => asset.view.id === 'shared'
        ? rosterAsset('shared', AssetDomain.Air, {
          displayName: 'Intermediate', position: [1, 2, 3],
        })
        : asset),
    }));
    h.roster.update(input({
      assets: ASSETS.map(asset => asset.view.id === 'shared'
        ? rosterAsset('shared', AssetDomain.Air, {
          displayName: 'Latest name', position: [9, 8, 7],
        })
        : asset),
    }));
    expect(h.scheduler.schedule).toHaveBeenCalledTimes(2);
    h.scheduler.flush();

    expect(h.roster.rowFor('asset', 'shared')).toBe(row);
    expect(row.textContent).toContain('Latest name');
    expect(row.textContent).not.toContain('Intermediate');
    expect(document.activeElement).toBe(row);
    expect(scroll.scrollTop).toBe(37);
  });

  it('removes only identifiers absent from the complete input', () => {
    const h = makeRoster();
    h.roster.update(input());
    h.scheduler.flush();
    const retained = h.roster.rowFor('asset', 'shared');

    h.roster.update(input({ assets: [ASSETS[0]!, ASSETS[2]!] }));
    h.scheduler.flush();

    expect(h.roster.rowFor('asset', 'shared')).toBe(retained);
    expect(h.roster.rowFor('asset', 'ground-1')).toBeNull();
    expect(h.roster.rowFor('asset', 'surface-1')).not.toBeNull();
  });

  it('performs no DOM writes when only unrendered telemetry changes', async () => {
    const h = makeRoster();
    h.roster.update(input());
    h.scheduler.flush();
    const records: MutationRecord[] = [];
    const observer = new MutationObserver(batch => records.push(...batch));
    observer.observe(h.roster.element, {
      subtree: true, childList: true, characterData: true, attributes: true,
    });

    h.roster.update(input({
      assets: ASSETS.map(asset => asset.view.id === 'shared'
        ? { ...asset, view: { ...asset.view, position: [50, 20, 10] as [number, number, number] } }
        : asset),
    }));
    h.scheduler.flush();
    await Promise.resolve();

    expect(records).toEqual([]);
    observer.disconnect();
  });

  it('recovers focus when a retained row becomes hidden by matching state', () => {
    const h = makeRoster();
    h.roster.update(input());
    h.scheduler.flush();
    const row = h.roster.rowFor('asset', 'shared')!;
    row.focus();

    h.roster.update(input({ query: 'ground one' }));
    h.scheduler.flush();

    expect(row.hidden).toBe(true);
    expect(h.onFocusFallback).toHaveBeenCalledOnce();
  });
});

describe('AssetRoster filtering and search', () => {
  it('searches only the approved asset and contact fields, trimmed and case-insensitively', () => {
    const h = makeRoster();
    for (const query of [
      ' shared ', 'AIR ONE', 'anzu', 'multirotor', 'agency-1', 'alpha', 'active',
    ]) {
      h.roster.update(input({ query }));
      h.scheduler.flush();
      expect(h.roster.rowFor('asset', 'shared')!.hidden, query).toBe(false);
    }
    for (const query of ['shared', 'coastal-radar', 'radar', 'vessel']) {
      h.roster.update(input({ query }));
      h.scheduler.flush();
      expect(h.roster.rowFor('track', 'shared')!.hidden, query).toBe(false);
    }

    h.roster.update(input({ query: 'Contact Alpha' }));
    h.scheduler.flush();
    expect(h.roster.rowFor('track', 'shared')!.hidden).toBe(true);
  });

  it('never applies asset facets to contacts', () => {
    const h = makeRoster();
    h.roster.update(input({
      assetFilter: { ...emptySelection(), domain: ['ground'] },
    }));
    h.scheduler.flush();

    expect(h.roster.rowFor('asset', 'shared')!.hidden).toBe(true);
    expect(h.roster.rowFor('asset', 'ground-1')!.hidden).toBe(false);
    expect(h.roster.rowFor('track', 'shared')!.hidden).toBe(false);
    expect(h.roster.counts).toEqual({ assetsMatching: 1, contactsMatching: 1 });
  });

  it('pins a selected nonmatch first without counting it as a match', () => {
    const h = makeRoster();
    h.roster.update(input({
      assetFilter: { ...emptySelection(), domain: ['ground'] },
      query: 'ground',
      selected: { kind: 'asset', id: 'surface-1' },
    }));
    h.scheduler.flush();

    const selected = h.roster.rowFor('asset', 'surface-1')!;
    const rows = [...h.mount.querySelectorAll<HTMLButtonElement>('[data-roster-kind="asset"]')]
      .filter(row => !row.hidden);
    expect(rows[0]).toBe(selected);
    expect(selected.textContent).toContain('Outside filters');
    expect(selected.getAttribute('aria-current')).toBe('true');
    expect(h.roster.counts.assetsMatching).toBe(1);

    h.roster.update(input({ query: 'nothing', selected: { kind: 'track', id: 'shared' } }));
    h.scheduler.flush();
    const track = h.roster.rowFor('track', 'shared')!;
    expect(track.hidden).toBe(false);
    expect(track.textContent).toContain('Outside filters');
    expect(h.roster.counts.contactsMatching).toBe(0);
  });

  it('renders literal server text and offers labelled search and recovery controls', () => {
    const h = makeRoster();
    const hostile = rosterAsset('asset\"] .victim', AssetDomain.Air, {
      displayName: '<img src=x onerror=alert(1)>',
    });
    h.roster.update(input({ assets: [hostile], contacts: [], query: 'no match' }));
    h.scheduler.flush();

    expect(h.mount.querySelector('img')).toBeNull();
    expect(h.roster.rowFor('asset', hostile.view.id)!.textContent)
      .toContain('<img src=x onerror=alert(1)>');
    const search = h.mount.querySelector<HTMLInputElement>('input[type="search"]')!;
    expect(search.labels?.[0]?.textContent).toContain('Search fleet');
    search.value = 'agency';
    search.dispatchEvent(new Event('input', { bubbles: true }));
    expect(h.onQueryChange).toHaveBeenCalledWith('agency');
    expect(h.mount.textContent).toContain('No matching assets');
    h.mount.querySelector<HTMLButtonElement>('[data-action="clear-filters"]')!.click();
    expect(h.onClearFilters).toHaveBeenCalledOnce();
  });
});

describe('AssetRoster lifecycle', () => {
  it('cancels a pending visual write and never recreates detached content', () => {
    const h = makeRoster();
    h.roster.update(input());
    h.roster.dispose();
    h.scheduler.flush();

    expect(h.scheduler.cancel).toHaveBeenCalledOnce();
    expect(h.mount.querySelector('.asset-roster')).toBeNull();
  });
});
