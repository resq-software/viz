// ResQ Viz - independently retryable operator-console resources
// SPDX-License-Identifier: Apache-2.0

import type { ApiFailure, Result } from '../api';
import type { AssetProfileCatalogResponse, ScenarioCatalogResponse } from './types';

// Compatibility re-exports keep existing Task 8 consumers source-stable while
// operator/types.ts remains the one transcription of the wire DTOs.
export type { AssetProfileCatalogResponse, ScenarioCatalogResponse } from './types';

export type ResourceState<T> =
  | { readonly status: 'idle' | 'loading' }
  | { readonly status: 'ready'; readonly value: T }
  | { readonly status: 'error'; readonly failure: ApiFailure };

export type ConsoleResourceKind = 'catalog' | 'profiles';

export interface ConsoleResourceSnapshot {
  readonly catalog: ResourceState<ScenarioCatalogResponse>;
  readonly profiles: ResourceState<AssetProfileCatalogResponse>;
}

export interface ConsoleResourceDependencies {
  readonly loadCatalog: () => Promise<Result<ScenarioCatalogResponse, ApiFailure>>;
  readonly loadProfiles: () => Promise<Result<AssetProfileCatalogResponse, ApiFailure>>;
}

export type ConsoleResourceListener = (state: ConsoleResourceSnapshot) => void;

/** Holds catalog and deployment-profile fetches without coupling their failures. */
export class ConsoleResources {
  private _catalog: ResourceState<ScenarioCatalogResponse> = { status: 'idle' };
  private _profiles: ResourceState<AssetProfileCatalogResponse> = { status: 'idle' };
  private _catalogGeneration = 0;
  private _profileGeneration = 0;
  private _catalogInFlight: Promise<void> | null = null;
  private _profilesInFlight: Promise<void> | null = null;
  private readonly _listeners = new Set<ConsoleResourceListener>();

  constructor(private readonly _dependencies: ConsoleResourceDependencies) {}

  get catalog(): ResourceState<ScenarioCatalogResponse> {
    return this._catalog;
  }

  get profiles(): ResourceState<AssetProfileCatalogResponse> {
    return this._profiles;
  }

  subscribe(listener: ConsoleResourceListener): () => void {
    this._listeners.add(listener);
    listener(this._snapshot());
    return () => this._listeners.delete(listener);
  }

  async loadMissing(): Promise<void> {
    const loads: Promise<void>[] = [];
    if (this._catalog.status !== 'ready') loads.push(this._load('catalog'));
    if (this._profiles.status !== 'ready') loads.push(this._load('profiles'));
    await Promise.all(loads);
  }

  async retry(kind: ConsoleResourceKind): Promise<void> {
    const state = kind === 'catalog' ? this._catalog : this._profiles;
    if (state.status === 'ready') return;
    await this._load(kind);
  }

  onReconnect(): Promise<void> {
    return this.loadMissing();
  }

  onVisibilityReturn(): Promise<void> {
    return this.loadMissing();
  }

  private _load(kind: ConsoleResourceKind): Promise<void> {
    return kind === 'catalog' ? this._loadCatalog() : this._loadProfiles();
  }

  private _loadCatalog(): Promise<void> {
    if (this._catalogInFlight !== null) return this._catalogInFlight;
    const generation = ++this._catalogGeneration;
    this._catalog = { status: 'loading' };
    const operation = settle(this._dependencies.loadCatalog)
      .then(state => {
        if (generation !== this._catalogGeneration) return;
        this._catalog = state;
        this._emit();
      })
      .finally(() => {
        if (generation === this._catalogGeneration) this._catalogInFlight = null;
      });
    this._catalogInFlight = operation;
    this._emit();
    return operation;
  }

  private _loadProfiles(): Promise<void> {
    if (this._profilesInFlight !== null) return this._profilesInFlight;
    const generation = ++this._profileGeneration;
    this._profiles = { status: 'loading' };
    const operation = settle(this._dependencies.loadProfiles)
      .then(state => {
        if (generation !== this._profileGeneration) return;
        this._profiles = state;
        this._emit();
      })
      .finally(() => {
        if (generation === this._profileGeneration) this._profilesInFlight = null;
      });
    this._profilesInFlight = operation;
    this._emit();
    return operation;
  }

  private _snapshot(): ConsoleResourceSnapshot {
    return { catalog: this._catalog, profiles: this._profiles };
  }

  private _emit(): void {
    const snapshot = this._snapshot();
    for (const listener of this._listeners) listener(snapshot);
  }
}

async function settle<T>(
  load: () => Promise<Result<T, ApiFailure>>,
): Promise<ResourceState<T>> {
  try {
    const result = await load();
    return result.success
      ? { status: 'ready', value: result.value }
      : { status: 'error', failure: result.error };
  } catch (error: unknown) {
    return {
      status: 'error',
      failure: {
        kind: 'network',
        message: error instanceof Error ? error.message : String(error),
      },
    };
  }
}
