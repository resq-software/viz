// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it, vi } from 'vitest';

import { ScenarioRuntime } from '../operator/ScenarioRuntime';
import { ScenarioCatalogLauncher } from '../operator/ScenarioCatalogLauncher';
import type { ScenarioCatalogResponse } from '../operator/types';

const catalog: ScenarioCatalogResponse = { scenarios: [] };
type Loader = NonNullable<ConstructorParameters<typeof ScenarioCatalogLauncher>[1]>;
type LoadedModule = Awaited<ReturnType<Loader>>;

function deferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
  readonly reject: (reason: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

function harness(load: Loader) {
  let mode: 'v2' | 'legacy' = 'v2';
  const onFailure = vi.fn();
  const launcher = new ScenarioCatalogLauncher({
    mode: () => mode,
    catalog: () => catalog,
    mount: {} as HTMLElement,
    trigger: {} as HTMLButtonElement,
    fallbackFocus: {} as HTMLElement,
    runtime: new ScenarioRuntime({ onPresent: vi.fn() }),
    getSession: () => ({ assetCount: 0, tick: 0, activeName: null }),
    onFailure,
  }, load);
  return { launcher, onFailure, setMode: (value: 'v2' | 'legacy') => { mode = value; } };
}

describe('ScenarioCatalogLauncher', () => {
  it('refreshes the active modal from streamed runtime changes, including clears', () => {
    const runtime = new ScenarioRuntime({ onPresent: vi.fn() });
    const refresh = vi.fn();
    const launcher = new ScenarioCatalogLauncher({
      mode: () => 'v2',
      catalog: () => catalog,
      mount: {} as HTMLElement,
      trigger: {} as HTMLButtonElement,
      fallbackFocus: {} as HTMLElement,
      runtime,
      getSession: () => ({ assetCount: 0, tick: 0, activeName: runtime.currentName }),
      onFailure: vi.fn(),
    }, vi.fn());
    const surface = { invalidate: vi.fn(), refresh };
    launcher.modalHost.activate(launcher.modalHost.begin(), surface);

    runtime.apply({ name: 'single', revision: 1, startedAtSimulationSeconds: 0 }, 1, 'live');
    runtime.apply(null, 0, 'live');

    expect(refresh).toHaveBeenCalledTimes(2);
  });

  it('disposes its runtime subscription and rejects later opens', () => {
    const runtime = new ScenarioRuntime({ onPresent: vi.fn() });
    const load = vi.fn();
    const launcher = new ScenarioCatalogLauncher({
      mode: () => 'v2',
      catalog: () => catalog,
      mount: {} as HTMLElement,
      trigger: {} as HTMLButtonElement,
      fallbackFocus: {} as HTMLElement,
      runtime,
      getSession: () => ({ assetCount: 0, tick: 0, activeName: runtime.currentName }),
      onFailure: vi.fn(),
    }, load);
    const refresh = vi.fn();
    launcher.modalHost.activate(launcher.modalHost.begin(), { invalidate: vi.fn(), refresh });

    launcher.dispose();
    runtime.apply({ name: 'single', revision: 1, startedAtSimulationSeconds: 0 }, 1, 'live');
    launcher.open();

    expect(refresh).not.toHaveBeenCalled();
    expect(load).not.toHaveBeenCalled();
    expect(launcher.modalHost.active).toBeNull();
  });

  it('does not open when a lazy import resolves after mode invalidation', async () => {
    const module = deferred<LoadedModule>();
    const h = harness(() => module.promise);
    h.launcher.open();
    h.setMode('legacy');
    h.launcher.invalidate();
    const openScenarioCatalog = vi.fn<LoadedModule['openScenarioCatalog']>();

    module.resolve({ openScenarioCatalog });
    await module.promise;
    await Promise.resolve();

    expect(openScenarioCatalog).not.toHaveBeenCalled();
  });

  it('does not steal the layer from a competing modal while its import is pending', async () => {
    const module = deferred<LoadedModule>();
    const h = harness(() => module.promise);
    h.launcher.open();
    const competing = { invalidate: vi.fn() };
    const competingGeneration = h.launcher.modalHost.begin();
    h.launcher.modalHost.activate(competingGeneration, competing);
    const openScenarioCatalog = vi.fn<LoadedModule['openScenarioCatalog']>();

    module.resolve({ openScenarioCatalog });
    await module.promise;
    await Promise.resolve();

    expect(openScenarioCatalog).not.toHaveBeenCalled();
    expect(competing.invalidate).not.toHaveBeenCalled();
    expect(h.launcher.modalHost.active).toBe(competing);
  });

  it('does not repaint a chunk error after a competing modal claimed the layer', async () => {
    const module = deferred<LoadedModule>();
    const h = harness(() => module.promise);
    h.launcher.open();
    const competing = { invalidate: vi.fn() };
    h.launcher.modalHost.activate(h.launcher.modalHost.begin(), competing);

    module.reject(new Error('late failure'));
    await expect(module.promise).rejects.toThrow('late failure');
    await Promise.resolve();

    expect(h.onFailure).not.toHaveBeenCalled();
    expect(competing.invalidate).not.toHaveBeenCalled();
    expect(h.launcher.modalHost.active).toBe(competing);
  });

  it('clears a rejected load and retries from the same stable trigger', async () => {
    const first = deferred<LoadedModule>();
    const second = deferred<LoadedModule>();
    const load = vi.fn()
      .mockImplementationOnce(() => first.promise)
      .mockImplementationOnce(() => second.promise);
    const h = harness(load);
    h.launcher.open();
    first.reject(new Error('chunk missing'));
    await expect(first.promise).rejects.toThrow('chunk missing');
    await Promise.resolve();

    expect(h.onFailure).toHaveBeenCalledWith('The scenario browser could not load.');
    h.launcher.open();
    const openScenarioCatalog = vi.fn<LoadedModule['openScenarioCatalog']>();
    second.resolve({ openScenarioCatalog });
    await second.promise;
    await Promise.resolve();

    expect(load).toHaveBeenCalledTimes(2);
    expect(openScenarioCatalog).toHaveBeenCalledOnce();
    expect(h.onFailure).toHaveBeenLastCalledWith(null);
  });

  it('starts a new load after invalidation while the obsolete import is still pending', async () => {
    const first = deferred<LoadedModule>();
    const second = deferred<LoadedModule>();
    const load = vi.fn()
      .mockImplementationOnce(() => first.promise)
      .mockImplementationOnce(() => second.promise);
    const h = harness(load);
    const firstOpen = vi.fn<LoadedModule['openScenarioCatalog']>();
    const secondOpen = vi.fn<LoadedModule['openScenarioCatalog']>();

    h.launcher.open();
    h.launcher.invalidate();
    h.launcher.open();
    expect(load).toHaveBeenCalledTimes(2);

    second.resolve({ openScenarioCatalog: secondOpen });
    await second.promise;
    await Promise.resolve();
    expect(secondOpen).toHaveBeenCalledOnce();

    first.resolve({ openScenarioCatalog: firstOpen });
    await first.promise;
    await Promise.resolve();
    expect(firstOpen).not.toHaveBeenCalled();
    expect(secondOpen).toHaveBeenCalledOnce();
  });

  it('does not let an obsolete finally clear a newer pending load', async () => {
    const first = deferred<LoadedModule>();
    const second = deferred<LoadedModule>();
    const load = vi.fn()
      .mockImplementationOnce(() => first.promise)
      .mockImplementationOnce(() => second.promise);
    const h = harness(load);
    h.launcher.open();
    h.launcher.invalidate();
    h.launcher.open();

    first.resolve({ openScenarioCatalog: vi.fn() });
    await first.promise;
    await Promise.resolve();
    h.launcher.open();

    expect(load).toHaveBeenCalledTimes(2);
    second.resolve({ openScenarioCatalog: vi.fn() });
    await second.promise;
  });
});
