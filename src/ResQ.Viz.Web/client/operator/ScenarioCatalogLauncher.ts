// ResQ Viz - lazy scenario-browser lifecycle
// SPDX-License-Identifier: Apache-2.0

import { OperatorModalHost } from './OperatorModalHost';
import type { ScenarioRequestRuntime } from './consoleApi';
import type { ScenarioCatalogSession } from './ScenarioCatalog';
import type { ScenarioCatalogResponse } from './types';

interface ScenarioRuntimeSource extends ScenarioRequestRuntime {
  subscribe(listener: () => void): () => void;
}

interface ScenarioCatalogModule {
  openScenarioCatalog: typeof import('./ScenarioCatalogLoader').openScenarioCatalog;
}

export interface ScenarioCatalogLauncherOptions {
  readonly mode: () => 'booting' | 'v2' | 'legacy';
  readonly catalog: () => ScenarioCatalogResponse | null;
  readonly mount: HTMLElement;
  readonly trigger: HTMLButtonElement;
  readonly fallbackFocus: HTMLElement;
  readonly runtime: ScenarioRuntimeSource;
  readonly getSession: () => ScenarioCatalogSession;
  readonly onFailure: (message: string | null) => void;
}

/** Owns import retry, mode generations, and the shared modal host outside app.ts. */
export class ScenarioCatalogLauncher {
  readonly modalHost = new OperatorModalHost();
  private _generation = 0;
  private _loading: { readonly generation: number; readonly modalGeneration: number } | null = null;
  private _disposed = false;
  private readonly _unsubscribe: () => void;

  constructor(
    private readonly _options: ScenarioCatalogLauncherOptions,
    private readonly _load: () => Promise<ScenarioCatalogModule> = () =>
      import('./ScenarioCatalogLoader'),
  ) {
    this._unsubscribe = _options.runtime.subscribe(() => this.modalHost.refresh());
  }

  open(): void {
    if (this._disposed || this._options.mode() !== 'v2' || this._options.catalog() === null) return;
    if (this._loading !== null && this.modalHost.isCurrent(this._loading.modalGeneration)) return;
    const generation = ++this._generation;
    const modalGeneration = this.modalHost.begin();
    this._loading = { generation, modalGeneration };
    void this._load().then(module => {
      const scenarios = this._options.catalog();
      if (generation !== this._generation || !this.modalHost.isCurrent(modalGeneration)
        || this._options.mode() !== 'v2' || scenarios === null) return;
      module.openScenarioCatalog(this.modalHost, modalGeneration, {
        mount: this._options.mount,
        trigger: this._options.trigger,
        fallbackFocus: this._options.fallbackFocus,
        scenarios,
        runtime: this._options.runtime,
        getSession: this._options.getSession,
      });
      this._options.onFailure(null);
    }).catch(() => {
      if (generation === this._generation && this.modalHost.isCurrent(modalGeneration)
        && this._options.mode() === 'v2') {
        this._options.onFailure('The scenario browser could not load.');
      }
    }).finally(() => {
      if (this._loading?.generation === generation) this._loading = null;
    });
  }

  invalidate(): void {
    this._generation++;
    this._loading = null;
    this.modalHost.invalidate();
  }

  dispose(): void {
    if (this._disposed) return;
    this._disposed = true;
    this._unsubscribe();
    this.invalidate();
  }
}
