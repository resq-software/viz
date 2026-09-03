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
  AssetLinkRequest,
  AssetLinkResponse,
  AssetProfileCatalogResponse,
  AssetSpawnRequest,
  AssetSpawnResponse,
  CommandAuditResponse,
  ControlHolderResponse,
  ControlLeaseReleaseRequest,
  ControlLeaseRenewRequest,
  ControlLeaseRequest,
  ControlLeaseResponse,
  ControlModeStatus,
  ControlPreemptRequest,
  ScenarioCatalogResponse,
  ScenarioStartResponse,
  TrackReportRequest,
  TrackReportResponse,
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

// ── Control authority ───────────────────────────────────────────────────────
//
// The wire half of `ControlAuthorityStore`. Reads use the shared GET retry
// policy; every lease mutation is a POST and is never retried, because a timed
// out acquire may well have been granted and asking twice would either take an
// asset the operator no longer wanted or renew a lease they had released.

/** Which control path this deployment runs. Constant for the process. */
export function getControlMode(options: ApiGetOptions = {}) {
  return apiGetJson<ControlModeStatus>(`${ROOT}/control/mode`, options);
}

/** Who currently commands one asset. Uncontrolled answers 200, not 404. */
export function getControlHolder(assetId: string, options: ApiGetOptions = {}) {
  return apiGetJson<ControlHolderResponse>(
    `${ROOT}/assets/${encodeURIComponent(assetId)}/control`,
    options,
  );
}

/** Takes control of an asset nobody else holds. The grant may be shorter than
 *  the request: renew against `grantedDurationSeconds`. */
export function acquireControl(
  assetId: string,
  request: ControlLeaseRequest,
  options: ApiOptions = {},
) {
  return apiPostJson<ControlLeaseResponse>(
    `${ROOT}/assets/${encodeURIComponent(assetId)}/control`,
    request,
    options,
  );
}

/** Pushes a live lease's expiry out. The holder only. */
export function renewControl(
  assetId: string,
  request: ControlLeaseRenewRequest,
  options: ApiOptions = {},
) {
  return apiPostJson<ControlLeaseResponse>(
    `${ROOT}/assets/${encodeURIComponent(assetId)}/control/renew`,
    request,
    options,
  );
}

/** Hands a lease back. Answers with the asset uncontrolled and the ended lease. */
export function releaseControl(
  assetId: string,
  request: ControlLeaseReleaseRequest,
  options: ApiOptions = {},
) {
  return apiPostJson<ControlHolderResponse>(
    `${ROOT}/assets/${encodeURIComponent(assetId)}/control/release`,
    request,
    options,
  );
}

/** Takes an asset from its current holder, on emergency authority, on the
 *  record. Refused without a justification. */
export function preemptControl(
  assetId: string,
  request: ControlPreemptRequest,
  options: ApiOptions = {},
) {
  return apiPostJson<ControlLeaseResponse>(
    `${ROOT}/assets/${encodeURIComponent(assetId)}/control/preempt`,
    request,
    options,
  );
}

// ── Command link ────────────────────────────────────────────────────────────
//
// The one pair of routes that can make an asset unreachable and put it back.
// The read retries like any GET; the change does not, and does not need to —
// the server treats it as idempotent, answering `changed: false` for a request
// that asked for the state the link was already in, so a manual retry after a
// lost response neither fails nor re-triggers a link-loss fallback.

/** Reports whether an asset's command link is currently up. */
export function getAssetLink(assetId: string, options: ApiGetOptions = {}) {
  return apiGetJson<AssetLinkResponse>(
    `${ROOT}/assets/${encodeURIComponent(assetId)}/link`,
    options,
  );
}

/** Holds an asset's command link down, or brings it back up.
 *
 *  Cutting issues no command: the world's safe-action sweep notices the silence
 *  and applies that asset's own declared link-loss behaviour, so the outcome is
 *  read off the asset's published state rather than off this response. A
 *  deployment reporting a live control path refuses the cut and never the
 *  restore — recovery paths do not get safety gates. */
export function setAssetLink(
  assetId: string,
  request: AssetLinkRequest,
  options: ApiOptions = {},
) {
  return apiPostJson<AssetLinkResponse>(
    `${ROOT}/assets/${encodeURIComponent(assetId)}/link`,
    request,
    options,
  );
}

// ── External tracks and the authority trail ─────────────────────────────────

/** Injects one simulated observation of a contact this session does not
 *  control. Never retried: the store fuses by observation time, and a second
 *  copy of the same report is a second observation the session did not make. */
export function reportTrack(request: TrackReportRequest, options: ApiOptions = {}) {
  return apiPostJson<TrackReportResponse>(`${ROOT}/tracks`, request, options);
}

/** Reads the session's bounded command-decision and lease windows, each with
 *  what it has dropped. Read-only, and the only route here that stays available
 *  while the console is away from the live edge. */
export function getCommandAudit(options: ApiGetOptions = {}) {
  return apiGetJson<CommandAuditResponse>(`${ROOT}/control/audit`, options);
}
