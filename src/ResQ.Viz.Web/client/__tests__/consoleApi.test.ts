// SPDX-License-Identifier: Apache-2.0

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiFailure, Result } from '../api';
import { CoordinateFrame, VehicleClass } from '../assets/types';
import type { ScenarioSessionState } from '../assets/types';
import {
  acquireControl,
  getAssetLink,
  getAssetProfiles,
  getCommandAudit,
  getControlHolder,
  getControlMode,
  getScenarioCatalog,
  preemptControl,
  releaseControl,
  renewControl,
  reportTrack,
  requestScenarioStart,
  setAssetLink,
  spawnAsset,
  startLegacyScenario,
  startScenario,
} from '../operator/consoleApi';
import { ScenarioRuntime } from '../operator/ScenarioRuntime';
import { TrackClassification, TrackSourceKind } from '../assets/types';
import { ControlRole } from '../operator/types';
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

describe('control authority routes', () => {
  // Each of these is a route the controller actually publishes. A path this
  // client got wrong would not fail here as a typo — it would fail in front of
  // an operator, as an asset that cannot be taken control of.
  it('reads mode and holder, and encodes the asset as one path segment', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(
        '{"mode":"simulationOnly","liveControlAvailable":false,"detail":"x"}',
        { status: 200 },
      ))
      .mockResolvedValueOnce(new Response(
        '{"assetId":"uav/1","isControlled":false,"lease":null}',
        { status: 200 },
      ));

    await getControlMode();
    await getControlHolder('uav/1');

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/v2/sim/control/mode');
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBeUndefined();
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/v2/sim/assets/uav%2F1/control');
  });

  it('posts every lease mutation to its own route with the holder in the body', async () => {
    const body = '{"lease":null}';
    for (let i = 0; i < 4; i++) {
      fetchMock.mockResolvedValueOnce(new Response(body, { status: 200 }));
    }

    await acquireControl('uav-1', {
      holderId: 'room-1:tab-7',
      role: ControlRole.Operator,
      durationSeconds: 300,
    });
    await renewControl('uav-1', {
      holderId: 'room-1:tab-7',
      leaseId: 'lease-7',
      durationSeconds: 300,
    });
    await releaseControl('uav-1', { holderId: 'room-1:tab-7', leaseId: 'lease-7' });
    await preemptControl('uav-1', {
      holderId: 'room-1:tab-7',
      role: ControlRole.Emergency,
      justification: 'Casualty located.',
    });

    expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
      '/api/v2/sim/assets/uav-1/control',
      '/api/v2/sim/assets/uav-1/control/renew',
      '/api/v2/sim/assets/uav-1/control/release',
      '/api/v2/sim/assets/uav-1/control/preempt',
    ]);
    for (const call of fetchMock.mock.calls) {
      expect(call[1]?.method).toBe('POST');
      expect(JSON.parse(String(call[1]?.body))).toMatchObject({ holderId: 'room-1:tab-7' });
    }
    // A preemption that could not say why is refused by the server, and a
    // justification dropped on this side would look like a client bug there.
    expect(JSON.parse(String(fetchMock.mock.calls[3]?.[1]?.body)))
      .toMatchObject({ role: ControlRole.Emergency, justification: 'Casualty located.' });
  });
});

describe('link, track and audit routes', () => {
  // Same reason as the lease routes above: a wrong path is not a typo an
  // operator can diagnose. It is an asset whose link cannot be restored.
  it('reads and writes the command link on one asset-scoped route', async () => {
    const body = '{"assetId":"uav 1","isAvailable":false,"changed":true}';
    fetchMock
      .mockResolvedValueOnce(new Response(body, { status: 200 }))
      .mockResolvedValueOnce(new Response(body, { status: 200 }));

    await getAssetLink('uav 1');
    await setAssetLink('uav 1', {
      available: false, issuerId: 'room-1:tab-7', reason: 'Loss-of-link drill',
    });

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/v2/sim/assets/uav%201/link');
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBeUndefined();
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/v2/sim/assets/uav%201/link');
    expect(fetchMock.mock.calls[1]?.[1]?.method).toBe('POST');
    expect(JSON.parse(String(fetchMock.mock.calls[1]?.[1]?.body))).toEqual({
      available: false, issuerId: 'room-1:tab-7', reason: 'Loss-of-link drill',
    });
  });

  it('posts a track report and reads the audit trail on their own routes', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response('{"trackId":"t1"}', { status: 201 }))
      .mockResolvedValueOnce(new Response(
        '{"decisions":[],"leases":[],"droppedDecisionCount":0,"droppedLeaseCount":0}',
        { status: 200 },
      ));

    await reportTrack({
      trackId: 't1',
      pose: {
        frame: CoordinateFrame.LocalEus,
        originId: null,
        position: { x: 1, y: 2, z: 3 },
        orientation: { x: 0, y: 0, z: 0, w: 0 },
      },
      twist: null,
      classification: TrackClassification.Vessel,
      sourceId: 'operator-console',
      sourceKind: TrackSourceKind.OperatorEntered,
      sourceQuality: 0.9,
      confidence: 0.9,
      observedAtSimulationTimeSeconds: 42.5,
      positionAccuracyM: null,
      velocityAccuracyMps: null,
      label: null,
      transponder: null,
    });
    await getCommandAudit();

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/v2/sim/tracks');
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBe('POST');
    // Nulls are sent, not dropped: absent and zero are different claims about
    // an accuracy, and the server distinguishes them.
    expect(JSON.parse(String(fetchMock.mock.calls[0]?.[1]?.body))).toMatchObject({
      twist: null, positionAccuracyM: null, velocityAccuracyMps: null, transponder: null,
    });
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/v2/sim/control/audit');
    expect(fetchMock.mock.calls[1]?.[1]?.method).toBeUndefined();
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
