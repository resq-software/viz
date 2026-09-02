// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  StartupCoordinator,
  type StartupCoordinatorDependencies,
} from '../operator/StartupCoordinator';
import type { OperatorMode } from '../operator/types';

interface Harness {
  readonly coordinator: StartupCoordinator;
  readonly modes: OperatorMode[];
  readonly v1Starts: string[];
  readonly v2Starts: string[];
  readonly schedule: ReturnType<typeof vi.fn<StartupCoordinatorDependencies['schedule']>>;
  readonly cancel: ReturnType<typeof vi.fn<StartupCoordinatorDependencies['cancel']>>;
}

function harness(overrides: Partial<StartupCoordinatorDependencies> = {}): Harness {
  const modes: OperatorMode[] = [];
  const v1Starts: string[] = [];
  const v2Starts: string[] = [];
  const schedule = vi.fn<StartupCoordinatorDependencies['schedule']>(
    (callback, ms) => window.setTimeout(callback, ms),
  );
  const cancel = vi.fn<StartupCoordinatorDependencies['cancel']>(
    id => window.clearTimeout(id),
  );
  const coordinator = new StartupCoordinator({
    setMode: mode => modes.push(mode),
    startLegacyScenario: async name => {
      v1Starts.push(name);
      return true;
    },
    startV2Scenario: async name => {
      v2Starts.push(name);
      return {
        success: true,
        value: { current: { name, startedAtSimulationSeconds: 0, revision: 1 } },
      };
    },
    schedule,
    cancel,
    ...overrides,
  });
  return { coordinator, modes, v1Starts, v2Starts, schedule, cancel };
}

function lastMode(h: Harness): OperatorMode | undefined {
  return h.modes[h.modes.length - 1];
}

afterEach(() => {
  vi.useRealTimers();
});

describe('v2 startup default', () => {
  it('starts Flood Response once for an empty hydrated room', async () => {
    const h = harness();

    await h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });
    await h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });

    expect(h.modes).toEqual(['v2']);
    expect(h.v2Starts).toEqual(['flood-response']);
    expect(h.v1Starts).toEqual([]);
  });

  it.each([
    { label: 'unknown scenario state', assetCount: 0, scenario: undefined },
    { label: 'ground-only inventory', assetCount: 1, scenario: null },
    { label: 'surface-only inventory', assetCount: 1, scenario: null },
    {
      label: 'active named scenario',
      assetCount: 3,
      scenario: { name: 'custom', startedAtSimulationSeconds: 0, revision: 1 },
    },
  ])('does not replace a room with $label', async ({ assetCount, scenario }) => {
    const h = harness();

    await h.coordinator.onV2Snapshot({ assetCount, scenario });

    expect(h.v2Starts).toEqual([]);
  });

  it('waits for unknown scenario state to hydrate before starting Flood Response', async () => {
    const h = harness();

    await h.coordinator.onV2Snapshot({ assetCount: 0, scenario: undefined });
    await h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });

    expect(h.v2Starts).toEqual(['flood-response']);
  });

  it('permanently decides against a default after the first populated inventory', async () => {
    const h = harness();

    await h.coordinator.onV2Snapshot({ assetCount: 1, scenario: null });
    await h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });

    expect(h.v2Starts).toEqual([]);
  });

  it('claims the default before awaiting its POST', async () => {
    let release!: () => void;
    const pending = new Promise<void>(resolve => { release = resolve; });
    const startV2Scenario = vi.fn(async () => {
      await pending;
      return { success: true as const, value: { current: null } };
    });
    const h = harness({ startV2Scenario });

    const first = h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });
    const second = h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });

    expect(startV2Scenario).toHaveBeenCalledTimes(1);
    release();
    await Promise.all([first, second]);
  });
});

describe('legacy fallback', () => {
  it('enters viable legacy after five seconds and starts Single once', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.coordinator.startNegotiation();
    h.coordinator.onV1Frame(0);
    await vi.advanceTimersByTimeAsync(5_000);

    expect(lastMode(h)).toBe('legacy');
    expect(h.v1Starts).toEqual(['single']);
    expect(h.v2Starts).toEqual([]);
  });

  it('does not claim legacy without a v1 frame', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.coordinator.startNegotiation();
    await vi.advanceTimersByTimeAsync(5_000);

    expect(h.modes).toEqual([]);
  });

  it('enters legacy when the first v1 frame arrives after the timeout', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.coordinator.startNegotiation();
    await vi.advanceTimersByTimeAsync(5_000);
    h.coordinator.onV1Frame(0);

    expect(lastMode(h)).toBe('legacy');
    expect(h.v1Starts).toEqual(['single']);
  });

  it('enters legacy without starting Single for a populated room', () => {
    const h = harness();

    h.coordinator.onV1Frame(2);
    h.coordinator.onV2Rejected();

    expect(lastMode(h)).toBe('legacy');
    expect(h.v1Starts).toEqual([]);
  });

  it('does not start Single when a populated legacy room later becomes empty', () => {
    const h = harness();

    h.coordinator.onV1Frame(2);
    h.coordinator.onV2Rejected();
    h.coordinator.onV1Frame(0);

    expect(h.v1Starts).toEqual([]);
  });

  it('waits for a v1 frame when rejection arrives first', () => {
    const h = harness();

    h.coordinator.onV2Rejected();
    expect(h.modes).toEqual([]);
    h.coordinator.onV1Frame(0);

    expect(lastMode(h)).toBe('legacy');
    expect(h.v1Starts).toEqual(['single']);
  });
});

describe('promotion and race ownership', () => {
  it('promotes legacy to a populated v2 room without a second default', async () => {
    const h = harness();

    h.coordinator.onV1Frame(0);
    h.coordinator.onV2Rejected();
    await h.coordinator.onV2Snapshot({ assetCount: 3, scenario: null });

    expect(lastMode(h)).toBe('v2');
    expect(h.v1Starts).toEqual(['single']);
    expect(h.v2Starts).toEqual([]);
  });

  it('cancels fallback when v2 arrives before the timer', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.coordinator.startNegotiation();
    h.coordinator.onV1Frame(0);
    await h.coordinator.onV2Snapshot({ assetCount: 2, scenario: null });
    await vi.advanceTimersByTimeAsync(5_000);

    expect(lastMode(h)).toBe('v2');
    expect(h.v1Starts).toEqual([]);
  });

  it('does not race Flood Response after legacy has claimed Single', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.coordinator.startNegotiation();
    h.coordinator.onV1Frame(0);
    await vi.advanceTimersByTimeAsync(5_000);
    await h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });

    expect(h.v1Starts).toEqual(['single']);
    expect(h.v2Starts).toEqual([]);
    expect(lastMode(h)).toBe('v2');
  });

  it('does not race Single after v2 has claimed Flood Response', async () => {
    let release!: () => void;
    const pending = new Promise<void>(resolve => { release = resolve; });
    const startV2Scenario = vi.fn(async () => {
      await pending;
      return { success: true as const, value: { current: null } };
    });
    const h = harness({ startV2Scenario });

    const start = h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });
    h.coordinator.onV1Frame(0);
    h.coordinator.onV2Rejected();

    expect(h.v1Starts).toEqual([]);
    release();
    await start;
  });

  it('lets a later readable v2 snapshot win after fallback', async () => {
    const h = harness();

    h.coordinator.onV1Frame(2);
    h.coordinator.onV2Rejected();
    await h.coordinator.onV2Snapshot({ assetCount: 2, scenario: null });

    expect(h.modes).toEqual(['legacy', 'v2']);
  });
});

describe('negotiation lifecycle', () => {
  it('schedules only one fallback while negotiation is unresolved', () => {
    vi.useFakeTimers();
    const h = harness();

    h.coordinator.startNegotiation();
    h.coordinator.startNegotiation();

    expect(h.schedule).toHaveBeenCalledTimes(1);
  });

  it('connection failure cancels fallback and a later negotiation can restart it', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.coordinator.startNegotiation();
    h.coordinator.onV1Frame(0);
    h.coordinator.onConnectionFailed();
    await vi.advanceTimersByTimeAsync(5_000);
    expect(h.modes).toEqual([]);
    expect(h.v1Starts).toEqual([]);

    h.coordinator.startNegotiation();
    h.coordinator.onV1Frame(0);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(lastMode(h)).toBe('legacy');
    expect(h.v1Starts).toEqual(['single']);
  });

  it('renegotiates stream viability after a previously readable v2 connection reconnects', async () => {
    vi.useFakeTimers();
    const h = harness();

    await h.coordinator.onV2Snapshot({ assetCount: 2, scenario: null });
    h.coordinator.onConnectionFailed();
    h.coordinator.startNegotiation();
    h.coordinator.onV1Frame(0);
    await vi.advanceTimersByTimeAsync(5_000);

    expect(h.modes).toEqual(['v2', 'legacy']);
    expect(h.v1Starts).toEqual([]);
    expect(h.v2Starts).toEqual([]);
  });

  it('dispose cancels fallback permanently', async () => {
    vi.useFakeTimers();
    const h = harness();

    h.coordinator.startNegotiation();
    h.coordinator.onV1Frame(0);
    h.coordinator.dispose();
    await vi.advanceTimersByTimeAsync(5_000);
    h.coordinator.startNegotiation();

    expect(h.modes).toEqual([]);
    expect(h.v1Starts).toEqual([]);
    expect(h.cancel).toHaveBeenCalledTimes(1);
    expect(h.schedule).toHaveBeenCalledTimes(1);
  });
});
