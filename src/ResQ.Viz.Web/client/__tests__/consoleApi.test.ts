// SPDX-License-Identifier: Apache-2.0

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiFailure, Result } from '../api';
import { CoordinateFrame, VehicleClass } from '../assets/types';
import type { ScenarioSessionState } from '../assets/types';
import {
  getAssetProfiles,
  getScenarioCatalog,
  requestScenarioStart,
  spawnAsset,
  startLegacyScenario,
  startScenario,
} from '../operator/consoleApi';
import { ScenarioRuntime } from '../operator/ScenarioRuntime';
import type { ScenarioStartResponse } from '../operator/types';

const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => vi.unstubAllGlobals());

function scenario(name: string, revision: number): ScenarioSessionState {
  return { name, revision, startedAtSimulationSeconds: revision };
}

function deferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

describe('consoleApi routes', () => {
  it('loads the exact scenario and profile catalog routes', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response('{"scenarios":[]}', { status: 200 }))
      .mockResolvedValueOnce(new Response('{"profiles":[]}', { status: 200 }));

    await getScenarioCatalog();
    await getAssetProfiles();

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/v2/sim/scenarios');
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBeUndefined();
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/v2/sim/asset-profiles');
  });

  it('encodes a scenario as one path segment and never sends a request body', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({
      current: scenario('coastal/search #1', 2),
    }), { status: 200 }));

    await startScenario('coastal/search #1');

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v2/sim/scenarios/coastal%2Fsearch%20%231/start',
      expect.objectContaining({ method: 'POST' }),
    );
    const init = fetchMock.mock.calls[0]?.[1];
    expect(init?.body).toBeUndefined();
    expect(init?.headers).toBeUndefined();
  });

  it('keeps legacy scenario starts on their encoded v1 route', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 200 }));

    const result = await startLegacyScenario('legacy/name #1');

    expect(result).toBe(true);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/sim/scenario/legacy%2Fname%20%231',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('posts an exact typed spawn body to the asset collection', async () => {
    fetchMock.mockResolvedValueOnce(new Response('{"assetId":"air-1","descriptor":{}}', {
      status: 201,
    }));
    const request = {
      vehicleClass: VehicleClass.Multirotor,
      pose: {
        frame: CoordinateFrame.LocalEus,
        originId: null,
        position: { x: 1, y: 2, z: 3 },
        orientation: { x: 0, y: 0, z: 0, w: 0 },
      },
      assetId: 'air-1',
      vendor: null,
    };

    await spawnAsset(request);

    expect(fetchMock).toHaveBeenCalledWith('/api/v2/sim/assets', expect.objectContaining({
      method: 'POST',
      body: JSON.stringify(request),
    }));
  });
});

describe('requestScenarioStart', () => {
  it('starts the runtime generation before POST and stays authoritative if stream wins the race', async () => {
    const runtime = new ScenarioRuntime({ onPresent: vi.fn() });
    runtime.apply(scenario('single', 2), 1, 'live');
    const response = deferred<Result<ScenarioStartResponse, ApiFailure>>();
    const post = vi.fn(() => response.promise);

    const pending = requestScenarioStart(runtime, 'flood-response', post);
    expect(runtime.view).toMatchObject({
      kind: 'pending', pendingName: 'flood-response', requestStage: 'requesting',
    });
    expect(post).toHaveBeenCalledWith('flood-response');

    runtime.apply(scenario('flood-response', 4), 8, 'live');
    response.resolve({
      success: true,
      value: { current: scenario('flood-response', 4) },
    });
    await pending;

    expect(runtime.view).toMatchObject({
      kind: 'active', name: 'flood-response', revision: 4,
    });
  });

  it('passes the returned revision to acceptance and waits for that streamed revision', async () => {
    const runtime = new ScenarioRuntime({ onPresent: vi.fn() });
    runtime.apply(scenario('single', 2), 1, 'live');

    await requestScenarioStart(runtime, 'flood-response', async () => ({
      success: true,
      value: { current: scenario('flood-response', 6) },
    }));
    runtime.apply(scenario('flood-response', 4), 8, 'live');
    expect(runtime.view).toMatchObject({ kind: 'pending', revision: 4 });

    runtime.apply(scenario('flood-response', 6), 8, 'live');
    expect(runtime.view).toMatchObject({ kind: 'active', revision: 6 });
  });

  it('fails the matching generation without changing its authoritative mission', async () => {
    const runtime = new ScenarioRuntime({ onPresent: vi.fn() });
    runtime.apply(scenario('single', 2), 1, 'live');
    const failure: ApiFailure = { kind: 'timeout', message: 'Request timed out' };

    const result = await requestScenarioStart(runtime, 'flood-response', async () => ({
      success: false, error: failure,
    }));

    expect(result).toEqual({ success: false, error: failure });
    expect(runtime.view).toMatchObject({ kind: 'active', name: 'single', revision: 2 });
  });

  it('does not issue a second POST while another scenario request is unresolved', async () => {
    const runtime = new ScenarioRuntime({ onPresent: vi.fn() });
    const response = deferred<Result<ScenarioStartResponse, ApiFailure>>();
    const post = vi.fn(() => response.promise);
    const first = requestScenarioStart(runtime, 'single', post);

    const second = await requestScenarioStart(runtime, 'flood-response', post);

    expect(post).toHaveBeenCalledOnce();
    expect(second).toMatchObject({
      success: false,
      error: { kind: 'problem', problem: { code: 'scenario.requestInFlight' } },
    });
    response.resolve({ success: false, error: { kind: 'network', message: 'offline' } });
    await first;
  });

  it('does not claim a runtime generation after v2 became unavailable during lazy load', async () => {
    const runtime = new ScenarioRuntime({ onPresent: vi.fn() });
    runtime.apply(scenario('single', 2), 1, 'live');
    const post = vi.fn();

    const result = await requestScenarioStart(runtime, 'flood-response', post, () => false);

    expect(post).not.toHaveBeenCalled();
    expect(runtime.requestInFlight).toBe(false);
    expect(runtime.view).toMatchObject({ kind: 'active', name: 'single' });
    expect(result).toMatchObject({
      success: false,
      error: { kind: 'problem', problem: { code: 'scenario.consoleUnavailable' } },
    });
  });
});
