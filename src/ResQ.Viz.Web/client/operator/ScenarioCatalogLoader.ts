// ResQ Viz - lazy scenario-catalog orchestration factory
// SPDX-License-Identifier: Apache-2.0

import { ScenarioCatalog } from './ScenarioCatalog';
import { requestScenarioStart, type ScenarioRequestRuntime } from './consoleApi';
import { scenarioPresentation } from './scenarioPresentation';
import type { ScenarioCatalogSession } from './ScenarioCatalog';
import type { ScenarioCatalogResponse } from './types';
import type { OperatorModalHost } from './OperatorModalHost';

export { OperatorModalHost } from './OperatorModalHost';

export interface ScenarioCatalogFactoryOptions {
  readonly mount: HTMLElement;
  readonly trigger: HTMLButtonElement;
  readonly fallbackFocus: HTMLElement;
  readonly scenarios: ScenarioCatalogResponse;
  readonly runtime: ScenarioRequestRuntime;
  readonly getSession: () => ScenarioCatalogSession;
  readonly onClose: (catalog: ScenarioCatalog) => void;
}

const catalogs = new WeakMap<OperatorModalHost, ScenarioCatalog>();

/** Keeps all catalog-specific construction and copy out of the entry chunk. */
export function createScenarioCatalog(options: ScenarioCatalogFactoryOptions): ScenarioCatalog {
  let catalog: ScenarioCatalog;
  catalog = new ScenarioCatalog({
    mount: options.mount,
    trigger: options.trigger,
    fallbackFocus: options.fallbackFocus,
    scenarios: options.scenarios,
    presentation: scenarioPresentation,
    getSession: options.getSession,
    startScenario: name => requestScenarioStart(options.runtime, name),
    confirmReplace: name => window.confirm(
      `Start ${scenarioPresentation(name).displayName}? This replaces the current simulation state.`,
    ),
    onClose: () => options.onClose(catalog),
  });
  return catalog;
}

/** Opens or reuses the catalog under the page's shared modal owner. */
export function openScenarioCatalog(
  owner: OperatorModalHost,
  generation: number,
  options: Omit<ScenarioCatalogFactoryOptions, 'onClose'>,
): void {
  let catalog = catalogs.get(owner);
  if (catalog === undefined) {
    catalog = createScenarioCatalog({
      ...options,
      onClose: closed => owner.release(closed),
    });
    catalogs.set(owner, catalog);
  }
  if (owner.activate(generation, catalog)) catalog.open();
}
