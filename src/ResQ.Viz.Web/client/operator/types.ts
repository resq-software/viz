// ResQ Viz - operator shell and REST presentation contracts
// SPDX-License-Identifier: Apache-2.0

import type { WireQuat, WireVec3 } from '../types';
import type {
  AssetDescriptor,
  AssetDomain,
  CoordinateFrame,
  GeoPosition,
  VehicleClass,
} from '../assets/types';

/** Which mutually exclusive shell branch is visible. */
export type OperatorMode = 'booting' | 'v2' | 'legacy';

/** What the boot branch tells the operator about the current connection attempt. */
export type OperatorBootStatus = 'connecting' | 'error';

/** Stable mount points consumed by lazy operator surfaces. */
export interface OperatorMounts {
  readonly mission: HTMLElement;
  readonly filter: HTMLElement;
  readonly roster: HTMLElement;
  readonly advancedSafety: HTMLElement;
  readonly context: HTMLElement;
  readonly modal: HTMLElement;
  readonly editor: HTMLElement;
}

/** Asset totals carried for every supported scenario domain, including zeroes. */
export interface ScenarioDomainCounts {
  readonly air: number;
  readonly ground: number;
  readonly surface: number;
}

/** One validated configured scenario exactly as published by the v2 catalog. */
export interface ScenarioSummary {
  readonly name: string;
  readonly assetCount: number;
  readonly domainCounts: ScenarioDomainCounts;
  readonly vehicleClassCounts: Readonly<Record<string, number>>;
}

/** Complete deployment scenario catalog. */
export interface ScenarioCatalogResponse {
  readonly scenarios: readonly ScenarioSummary[];
}

/** Raw room facts that decide whether replacing a scenario is destructive. */
export interface ScenarioReplacementContext {
  readonly assetCount: number;
  readonly tick: number;
}

/** Result returned after a room commits a scenario replacement. */
export interface ScenarioStartResponse {
  readonly current: import('../assets/types').ScenarioSessionState;
}

/** One vehicle profile this deployment's v2 endpoint can spawn. */
export interface AssetSpawnProfile {
  readonly vehicleClass: VehicleClass;
  readonly domain: AssetDomain;
  readonly displayName: string;
  readonly headingApplies: boolean;
}

/** Complete deployment-derived spawn profile catalog. */
export interface AssetProfileCatalogResponse {
  readonly profiles: readonly AssetSpawnProfile[];
}

/** Frame-qualified pose accepted when spawning an asset. */
export interface AssetSpawnPose {
  readonly frame: CoordinateFrame;
  readonly originId: string | null;
  readonly position: WireVec3;
  readonly orientation: WireQuat;
  readonly covariance?: readonly number[] | null;
  readonly geo?: GeoPosition | null;
}

/** Exact request body accepted by POST /api/v2/sim/assets. */
export interface AssetSpawnRequest {
  readonly vehicleClass: VehicleClass;
  readonly pose: AssetSpawnPose;
  readonly assetId?: string | null;
  readonly displayName?: string | null;
  readonly vendor?: string | null;
  readonly model?: string | null;
  readonly agencyId?: string | null;
  readonly fleetId?: string | null;
}

/** Created asset identity and descriptor returned by the spawn endpoint. */
export interface AssetSpawnResponse {
  readonly assetId: string;
  readonly descriptor: AssetDescriptor;
}
