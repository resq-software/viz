// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it, vi } from 'vitest';

import type { ApiFailure, Result } from '../api';
import {
  ConsoleResources,
  type AssetProfileCatalogResponse,
  type ScenarioCatalogResponse,
} from '../operator/ConsoleResources';

const unavailable: ApiFailure = {
  kind: 'problem',
  problem: {
    status: 503,
    code: 'catalog.unavailable',
    reasonCode: null,
    title: 'Unavailable',
    detail: 'Retry later',
    traceId: null,
    errors: [],
  },
};

const emptyCatalog: ScenarioCatalogResponse = { scenarios: [] };
const emptyProfiles: AssetProfileCatalogResponse = { profiles: [] };

function success<T>(value: T): Result<T, ApiFailure> {
  return { success: true, value };
}

function deferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

describe('ConsoleResources', () => {
  it('loads resources independently and retries only the failed one', async () => {
    const loadCatalog = vi.fn().mockResolvedValue(success(emptyCatalog));
    const loadProfiles = vi.fn()
      .mockResolvedValueOnce({ success: false, error: unavailable })
      .mockResolvedValueOnce(success(emptyProfiles));
    const resources = new ConsoleResources({ loadCatalog, loadProfiles });

    await resources.loadMissing();

    expect(resources.catalog).toEqual({ status: 'ready', value: emptyCatalog });
    expect(resources.profiles).toEqual({ status: 'error', failure: unavailable });

    await resources.retry('profiles');
    expect(resources.profiles).toEqual({ status: 'ready', value: emptyProfiles });
    expect(loadCatalog).toHaveBeenCalledTimes(1);
    expect(loadProfiles).toHaveBeenCalledTimes(2);

    await resources.onVisibilityReturn();
    await resources.onReconnect();
    expect(loadCatalog).toHaveBeenCalledTimes(1);
    expect(loadProfiles).toHaveBeenCalledTimes(2);
  });

  it('starts catalog and profile loads in parallel', async () => {
    const catalog = deferred<Result<ScenarioCatalogResponse, ApiFailure>>();
    const profiles = deferred<Result<AssetProfileCatalogResponse, ApiFailure>>();
    const loadCatalog = vi.fn(() => catalog.promise);
    const loadProfiles = vi.fn(() => profiles.promise);
    const resources = new ConsoleResources({ loadCatalog, loadProfiles });

    const loading = resources.loadMissing();
    await Promise.resolve();

    expect(loadCatalog).toHaveBeenCalledOnce();
    expect(loadProfiles).toHaveBeenCalledOnce();
    expect(resources.catalog.status).toBe('loading');
    expect(resources.profiles.status).toBe('loading');

    catalog.resolve(success(emptyCatalog));
    profiles.resolve(success(emptyProfiles));
    await loading;
  });

  it('deduplicates concurrent automatic and manual retries per resource', async () => {
    const catalog = deferred<Result<ScenarioCatalogResponse, ApiFailure>>();
    const profiles = deferred<Result<AssetProfileCatalogResponse, ApiFailure>>();
    const loadCatalog = vi.fn(() => catalog.promise);
    const loadProfiles = vi.fn(() => profiles.promise);
    const resources = new ConsoleResources({ loadCatalog, loadProfiles });

    const calls = [
      resources.loadMissing(),
      resources.onReconnect(),
      resources.onVisibilityReturn(),
      resources.retry('catalog'),
      resources.retry('profiles'),
    ];
    await Promise.resolve();

    expect(loadCatalog).toHaveBeenCalledOnce();
    expect(loadProfiles).toHaveBeenCalledOnce();
    catalog.resolve(success(emptyCatalog));
    profiles.resolve(success(emptyProfiles));
    await Promise.all(calls);
  });

  it('does not let one rejected loader strand or disturb the other resource', async () => {
    const loadCatalog = vi.fn().mockRejectedValue(new Error('socket closed'));
    const loadProfiles = vi.fn().mockResolvedValue(success(emptyProfiles));
    const resources = new ConsoleResources({ loadCatalog, loadProfiles });

    await expect(resources.loadMissing()).resolves.toBeUndefined();

    expect(resources.catalog).toEqual({
      status: 'error',
      failure: { kind: 'network', message: 'socket closed' },
    });
    expect(resources.profiles).toEqual({ status: 'ready', value: emptyProfiles });
  });

  it('notifies subscribers with isolated loading and completion states', async () => {
    const catalog = deferred<Result<ScenarioCatalogResponse, ApiFailure>>();
    const loadCatalog = vi.fn(() => catalog.promise);
    const loadProfiles = vi.fn().mockResolvedValue(success(emptyProfiles));
    const resources = new ConsoleResources({ loadCatalog, loadProfiles });
    const states: string[] = [];
    resources.subscribe(state => states.push(`${state.catalog.status}/${state.profiles.status}`));

    const loading = resources.loadMissing();
    await Promise.resolve();
    catalog.resolve(success(emptyCatalog));
    await loading;

    expect(states[0]).toBe('idle/idle');
    expect(states).toContain('loading/idle');
    expect(states).toContain('loading/loading');
    expect(states[states.length - 1]).toBe('ready/ready');
  });
});
