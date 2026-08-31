// ResQ Viz - deferred registration of the ground and surface renderers
// SPDX-License-Identifier: Apache-2.0
//
// The two `import()` calls that keep the rover and vessel geometry out of the
// entry chunk. Same idiom as `../postfxDeferred.ts`: the call site stays
// synchronous, the chunk is fetched only when something actually needs it, and a
// chunk that never arrives degrades the picture rather than breaking it.
//
// The difference from postfx is *when* the fetch starts. Post-processing is
// wanted on every page load, so its import fires during module evaluation. A
// rover is not: most sessions only ever fly drones, and those sessions must not
// pay for a renderer they will never call. `AssetRegistry.registerDomainLazy`
// therefore holds the loader unfetched until the first asset of that domain
// appears in a frame — the registration itself costs one closure.
//
// Air is deliberately absent. It is registered eagerly in `../drones.ts` because
// every session has drones and because the `AirRenderer` constructor starts the
// shared glTF fetch that the whole page load wants in flight as early as
// possible.
//
// **Failure is not fatal and not silent.** The registry returns the fallback
// marker synchronously for any asset whose renderer has not landed, so an asset
// whose chunk 404s on a half-rolled deploy stays visible and selectable on a
// deliberately domain-less silhouette rather than vanishing. The registry clears
// the memo on failure, so the next spawn of that domain retries — bounded by
// operator action, not by frame rate.

import { getLogger } from '../log';
import type { AssetRegistry } from './AssetRegistry';
import type { IAssetRenderer } from './IAssetRenderer';
import { AssetDomain } from './types';

const log = getLogger('assetRenderers');

/** Loads the ground renderer's chunk. Separated from the registration below so a
 *  test can substitute one without a bundler. */
export async function loadGroundRenderer(): Promise<IAssetRenderer> {
  const { GroundRenderer } = await import('./renderers/GroundRenderer');
  return new GroundRenderer();
}

/** Loads the surface renderer's chunk. */
export async function loadSurfaceRenderer(): Promise<IAssetRenderer> {
  const { createSurfaceRenderer } = await import('./renderers/SurfaceRenderer');
  return createSurfaceRenderer();
}

/** Loaders keyed by the domain they draw. Overridable so a test can assert the
 *  registration wiring without pulling in three.js geometry. */
export interface DomainRendererLoaders {
  readonly ground?: () => Promise<IAssetRenderer>;
  readonly surface?: () => Promise<IAssetRenderer>;
}

/**
 * Registers the chunked domain renderers on `registry`.
 *
 * Idempotent: registering twice replaces the loader for a domain rather than
 * queueing a second fetch, so a hot reload or a rebuilt scene does not double-
 * load a chunk.
 *
 * Nothing is fetched here, and the fetch-free signature is the point — a caller
 * that awaited registration would be waiting on nothing, and one that awaited the
 * *chunks* would have reintroduced the blocking load this module exists to
 * remove.
 */
export function registerDomainRenderers(
  registry: AssetRegistry,
  loaders: DomainRendererLoaders = {},
): void {
  const ground = loaders.ground ?? loadGroundRenderer;
  const surface = loaders.surface ?? loadSurfaceRenderer;

  registry.registerDomainLazy(AssetDomain.Ground, () => {
    log.info('loading the ground renderer chunk');
    return ground();
  });
  registry.registerDomainLazy(AssetDomain.Surface, () => {
    log.info('loading the surface renderer chunk');
    return surface();
  });
}
