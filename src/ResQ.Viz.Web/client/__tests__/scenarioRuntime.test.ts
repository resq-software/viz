// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it, vi } from 'vitest';

import {
  ScenarioRuntime,
  type ScenarioRequestToken,
} from '../operator/ScenarioRuntime';
import type { ScenarioSessionState } from '../assets/types';

function scenario(
  name: string,
  revision: number,
  startedAtSimulationSeconds = 0,
): ScenarioSessionState {
  return { name, revision, startedAtSimulationSeconds };
}

describe('ScenarioRuntime authoritative revisions', () => {
  it('publishes one Live transition per increasing revision', () => {
    const starts: string[] = [];
    const runtime = new ScenarioRuntime({ onPresent: state => starts.push(state.name) });
    const flood = scenario('flood-response', 1);

    runtime.apply(flood, 8, 'live');
    runtime.apply(flood, 8, 'live');

    expect(runtime.view).toMatchObject({ kind: 'active', name: 'flood-response', revision: 1 });
    expect(starts).toEqual(['flood-response']);
  });

  it('ignores lower revisions but presents a same-name higher revision as a restart', () => {
    const onPresent = vi.fn();
    const runtime = new ScenarioRuntime({ onPresent });

    runtime.apply(scenario('flood-response', 4), 8, 'live');
    runtime.apply(scenario('coastal-search', 3), 8, 'live');
    runtime.apply(scenario('flood-response', 6, 12), 8, 'live');

    expect(runtime.view).toMatchObject({
      kind: 'active', name: 'flood-response', revision: 6,
      startedAtSimulationSeconds: 12,
    });
    expect(onPresent.mock.calls.map(([state]) => state.revision)).toEqual([4, 6]);
  });

  it('distinguishes unknown, none, and custom without inventing a scenario', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });

    runtime.apply(undefined, 0, 'live');
    expect(runtime.view).toEqual({ kind: 'unknown', pendingName: null });
    runtime.apply(null, 0, 'live');
    expect(runtime.view).toEqual({ kind: 'none', pendingName: null });
    runtime.apply(null, 2, 'live');
    expect(runtime.view).toEqual({ kind: 'custom', pendingName: null });
  });

  it('defers replay presentation and flushes the latest state once on resume', () => {
    const starts: string[] = [];
    const runtime = new ScenarioRuntime({ onPresent: state => starts.push(state.name) });

    runtime.apply(scenario('coastal-search', 2, 4), 4, 'replay');
    runtime.apply(scenario('flood-response', 4, 8), 8, 'replay');
    expect(starts).toEqual([]);
    expect(runtime.view.kind).toBe('unknown');

    runtime.resumeLive();
    runtime.resumeLive();

    expect(starts).toEqual(['flood-response']);
    expect(runtime.view).toMatchObject({ kind: 'active', name: 'flood-response', revision: 4 });
  });

  it('flushes a deferred transition when the equal revision arrives Live', () => {
    const onPresent = vi.fn();
    const runtime = new ScenarioRuntime({ onPresent });
    const flood = scenario('flood-response', 2);

    runtime.apply(flood, 8, 'replay');
    runtime.apply(flood, 8, 'live');
    runtime.resumeLive();

    expect(onPresent).toHaveBeenCalledOnce();
    expect(runtime.view).toMatchObject({ kind: 'active', name: 'flood-response' });
  });
});

describe('ScenarioRuntime request confirmation', () => {
  function accepted(
    runtime: ScenarioRuntime,
    token: ScenarioRequestToken,
    current: ScenarioSessionState,
  ): void {
    runtime.requestAccepted(token, current);
  }

  it('shows an accepted start as pending until a matching streamed revision arrives', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    runtime.apply(scenario('single', 2), 1, 'live');

    const token = runtime.requested('flood-response');
    accepted(runtime, token, scenario('flood-response', 4));

    expect(runtime.view).toMatchObject({
      kind: 'pending', name: 'single', pendingName: 'flood-response', pendingKind: 'scenario',
    });

    runtime.apply(scenario('single', 2), 1, 'live');
    expect(runtime.view.kind).toBe('pending');
    runtime.apply(scenario('flood-response', 4), 8, 'live');
    expect(runtime.view).toMatchObject({ kind: 'active', name: 'flood-response', revision: 4 });
  });

  it('does not regress to pending when the confirming stream beats the HTTP response', () => {
    const onPresent = vi.fn();
    const runtime = new ScenarioRuntime({ onPresent });
    runtime.apply(scenario('single', 2), 1, 'live');

    const token = runtime.requested('flood-response');
    runtime.apply(scenario('flood-response', 4), 8, 'live');
    accepted(runtime, token, scenario('flood-response', 4));

    expect(runtime.view).toMatchObject({ kind: 'active', name: 'flood-response', revision: 4 });
    expect(onPresent.mock.calls.map(([state]) => state.revision)).toEqual([2, 4]);
  });

  it('does not treat a lower pre-response match as confirmation of the returned revision', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    runtime.apply(scenario('single', 2), 1, 'live');
    const token = runtime.requested('flood-response');

    // Another console happened to publish the same target name first.
    runtime.apply(scenario('flood-response', 3), 8, 'live');
    accepted(runtime, token, scenario('flood-response', 4));

    expect(runtime.view).toMatchObject({
      kind: 'pending', baseKind: 'active', name: 'flood-response',
      pendingName: 'flood-response', revision: 3,
    });

    runtime.apply(scenario('flood-response', 4), 8, 'live');
    expect(runtime.view).toMatchObject({ kind: 'active', revision: 4 });
  });

  it('accepts an expected matching revision that reached the stream before the response', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    runtime.apply(scenario('single', 2), 1, 'live');
    const token = runtime.requested('flood-response');

    runtime.apply(scenario('flood-response', 4), 8, 'live');
    accepted(runtime, token, scenario('flood-response', 4));

    expect(runtime.view).toMatchObject({ kind: 'active', revision: 4 });
  });

  it('lets a newer remote revision supersede an accepted request', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    runtime.apply(scenario('single', 2), 1, 'live');
    const token = runtime.requested('flood-response');
    accepted(runtime, token, scenario('flood-response', 4));

    runtime.apply(scenario('coastal-search', 6), 8, 'live');

    expect(runtime.view).toMatchObject({ kind: 'active', name: 'coastal-search', revision: 6 });
  });

  it('lets a newer same-name revision supersede an accepted request', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    runtime.apply(scenario('single', 2), 1, 'live');
    const token = runtime.requested('flood-response');
    accepted(runtime, token, scenario('flood-response', 4));

    runtime.apply(scenario('flood-response', 6), 8, 'live');

    expect(runtime.view).toMatchObject({ kind: 'active', name: 'flood-response', revision: 6 });
  });

  it.each([
    { assetCount: 0, expectedBase: 'none' as const },
    { assetCount: 2, expectedBase: 'custom' as const },
  ])('retains the $expectedBase base while a scenario start is pending', ({ assetCount, expectedBase }) => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    runtime.apply(null, assetCount, 'live');

    const token = runtime.requested('flood-response');
    accepted(runtime, token, scenario('flood-response', 2));

    expect(runtime.view).toMatchObject({
      kind: 'pending', baseKind: expectedBase,
      name: 'flood-response', pendingName: 'flood-response',
    });
  });

  it('retains Custom session while an accepted reset awaits an empty clear', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    runtime.apply(null, 2, 'live');

    const token = runtime.requested(null);
    runtime.requestAccepted(token);

    expect(runtime.view).toMatchObject({
      kind: 'pending', baseKind: 'custom', name: null,
      pendingName: null, pendingKind: 'reset',
    });
  });

  it('ignores a stale acceptance after a newer request generation', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    const first = runtime.requested('flood-response');
    const second = runtime.requested('coastal-search');

    accepted(runtime, first, scenario('flood-response', 2));
    accepted(runtime, second, scenario('coastal-search', 4));

    expect(runtime.view).toMatchObject({
      kind: 'pending', name: 'coastal-search', pendingName: 'coastal-search',
    });
  });

  it('preserves the prior mission and presentation when a request fails', () => {
    const onPresent = vi.fn();
    const runtime = new ScenarioRuntime({ onPresent });
    runtime.apply(scenario('single', 2), 1, 'live');
    const token = runtime.requested('flood-response');

    runtime.requestFailed(token);

    expect(runtime.view).toMatchObject({ kind: 'active', name: 'single', revision: 2 });
    expect(onPresent).toHaveBeenCalledOnce();
  });

  it('keeps reset pending until a later authoritative clear and empty inventory', () => {
    const runtime = new ScenarioRuntime({ onPresent: () => undefined });
    runtime.apply(scenario('flood-response', 2), 8, 'live');
    const token = runtime.requested(null);
    runtime.requestAccepted(token);

    expect(runtime.view).toMatchObject({
      kind: 'pending', name: 'flood-response', pendingName: null, pendingKind: 'reset',
    });

    runtime.apply(null, 2, 'live');
    expect(runtime.view.kind).toBe('pending');
    runtime.apply(null, 0, 'live');
    expect(runtime.view).toEqual({ kind: 'none', pendingName: null });
  });
});
