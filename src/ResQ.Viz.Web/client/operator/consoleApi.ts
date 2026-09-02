// ResQ Viz - typed v2 operator-console REST surface
// SPDX-License-Identifier: Apache-2.0

import {
  apiGetJson,
  apiPost,
  apiPostJson,
  type ApiFailure,
  type ApiGetOptions,
  type ApiOptions,
  type Result,
} from '../api';
import type { ScenarioRequestToken } from './ScenarioRuntime';
import type {
  AssetProfileCatalogResponse,
  AssetSpawnRequest,
  AssetSpawnResponse,
  ScenarioCatalogResponse,
  ScenarioStartResponse,
} from './types';

const ROOT = '/api/v2/sim';

/** Minimal authoritative request seam implemented by ScenarioRuntime. */
export interface ScenarioRequestRuntime {
  readonly requestInFlight: boolean;
  requested(name: string): ScenarioRequestToken;
  requestAccepted(token: ScenarioRequestToken, current: ScenarioStartResponse['current']): void;
  requestFailed(token: ScenarioRequestToken): void;
}

/** Fetches the complete validated scenario catalog. */
export function getScenarioCatalog(options: ApiGetOptions = {}) {
  return apiGetJson<ScenarioCatalogResponse>(`${ROOT}/scenarios`, options);
}

/** Replaces the room with one named scenario. Mutations are never retried. */
export function startScenario(name: string, options: ApiOptions = {}) {
  return apiPostJson<ScenarioStartResponse>(
    `${ROOT}/scenarios/${encodeURIComponent(name)}/start`,
    undefined,
    options,
  );
}

/** Preserves the v1 compatibility route for imported legacy scene configs. */
export async function startLegacyScenario(name: string, options: ApiOptions = {}): Promise<boolean> {
  return (await apiPost(`/api/sim/scenario/${encodeURIComponent(name)}`, undefined, options)).success;
}

/** Fetches only the asset profiles this deployment can spawn. */
export function getAssetProfiles(options: ApiGetOptions = {}) {
  return apiGetJson<AssetProfileCatalogResponse>(`${ROOT}/asset-profiles`, options);
}

/** Spawns one asset through the typed multi-domain endpoint. */
export function spawnAsset(request: AssetSpawnRequest, options: ApiOptions = {}) {
  return apiPostJson<AssetSpawnResponse>(`${ROOT}/assets`, request, options);
}

/**
 * Runs the shared request lifecycle around one scenario POST.
 *
 * The token is claimed synchronously before the first await. HTTP success only
 * records the returned revision; streamed state remains the activation source.
 */
export async function requestScenarioStart(
  runtime: ScenarioRequestRuntime,
  name: string,
  post: (name: string) => Promise<Result<ScenarioStartResponse, ApiFailure>> = startScenario,
  available: () => boolean = () => true,
): Promise<Result<ScenarioStartResponse, ApiFailure>> {
  if (!available()) {
    return scenarioFailure(
      'scenario.consoleUnavailable',
      'Scenario controls are unavailable outside v2 mode.',
    );
  }
  if (runtime.requestInFlight) return requestAlreadyPending();
  const token = runtime.requested(name);
  let result: Result<ScenarioStartResponse, ApiFailure>;
  try {
    result = await post(name);
  } catch (error: unknown) {
    result = {
      success: false,
      error: {
        kind: 'network',
        message: error instanceof Error ? error.message : String(error),
      },
    };
  }
  if (result.success) runtime.requestAccepted(token, result.value.current);
  else runtime.requestFailed(token);
  return result;
}

function requestAlreadyPending(): Result<ScenarioStartResponse, ApiFailure> {
  return scenarioFailure(
    'scenario.requestInFlight',
    'Wait for the current scenario request to settle.',
  );
}

function scenarioFailure(
  code: string,
  detail: string,
): Result<ScenarioStartResponse, ApiFailure> {
  return {
    success: false,
    error: {
      kind: 'problem',
      problem: {
        status: 409,
        code,
        reasonCode: null,
        title: 'Scenario request unavailable',
        detail,
        traceId: null,
        errors: [],
      },
    },
  };
}
