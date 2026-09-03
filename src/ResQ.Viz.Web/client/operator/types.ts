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

// ── Control authority ───────────────────────────────────────────────────────
//
// Transcribed from `Models/ControlLease.cs`, `Models/CommandAudit.cs` and
// `Models/AssetCommand.cs`. Numeric enums are part of the wire contract and are
// append-only there, so they are written as const objects rather than TypeScript
// `enum`s: the values have to match the server's, and a `const enum`-free literal
// map makes a mismatch visible in one place.
//
// Authority is an **issuer**-level fact. None of it belongs in an asset's
// capability report: what an asset can do is a fact about the asset, and a report
// that shrank for whoever does not hold the lease would make the advertised
// command set differ from the accepted one for every other caller.

/** Authority a caller presents when taking control. */
export const ControlRole = { Unspecified: 0, Operator: 1, Emergency: 2 } as const;
export type ControlRole = (typeof ControlRole)[keyof typeof ControlRole];

/** Why a lease stopped conferring authority. */
export const ControlLeaseEndReason = {
  Unspecified: 0, Released: 1, Expired: 2, Preempted: 3,
  AssetRemoved: 4, AuthorityReset: 5,
} as const;
export type ControlLeaseEndReason =
  (typeof ControlLeaseEndReason)[keyof typeof ControlLeaseEndReason];

/** One console's claim over one asset, live or ended. */
export interface ControlLease {
  readonly leaseId: string;
  readonly assetId: string;
  readonly assetInstanceId: string;
  readonly holderId: string;
  readonly role: ControlRole;
  readonly issuedAt: string;
  readonly expiresAt: string;
  readonly lastRenewedAt: string | null;
  readonly endedAt: string | null;
  readonly endReason: ControlLeaseEndReason | null;
}

/** Who commands one asset. An uncontrolled asset is a normal 200, not a 404. */
export interface ControlHolderResponse {
  readonly assetId: string;
  readonly isControlled: boolean;
  readonly lease: ControlLease | null;
}

/** A lease operation that succeeded, and what policy did to the request.
 *  Renew against `grantedDurationSeconds`, never against what was asked for. */
export interface ControlLeaseResponse {
  readonly lease: ControlLease;
  readonly requestedDurationSeconds: number;
  readonly grantedDurationSeconds: number;
  readonly durationClamped: boolean;
}

/** Which control path this deployment runs. Stated, never inferred. */
export interface ControlModeStatus {
  readonly mode: string;
  readonly liveControlAvailable: boolean;
  readonly detail: string;
}

/** Lifecycle position of one issued command. */
export const CommandState = {
  Requested: 0, Accepted: 1, Rejected: 2, InProgress: 3,
  Succeeded: 4, Failed: 5, Cancelled: 6, TimedOut: 7,
} as const;
export type CommandState = (typeof CommandState)[keyof typeof CommandState];

/** Status of a command as reported back to whoever issued it. Acceptance is
 *  transport-level: it says the asset was told, not that it arrived. */
export interface CommandResult {
  readonly commandId: string;
  readonly state: CommandState;
  readonly acceptedAt: string | null;
  readonly progressPercent: number;
  readonly message: string | null;
  readonly reasonCode: string | null;
}

/** One decision the authority layer made on the command path. */
export interface CommandAuditRecord {
  readonly sequence: number;
  readonly decision: number;
  readonly at: string;
  readonly correlationId: string;
  readonly assetId: string;
  readonly commandId: string | null;
  readonly kind: string | null;
  readonly issuerId: string;
  readonly leaseId: string | null;
  readonly reasonCode: string | null;
  readonly detail: string | null;
}

/** One entry in the control authority's own lease trail. `at` is when it
 *  happened and `observedAt` when the server noticed; an expiry separates them. */
export interface ControlAuditRecord {
  readonly sequence: number;
  readonly kind: number;
  readonly at: string;
  readonly observedAt: string;
  readonly assetId: string;
  readonly leaseId: string | null;
  readonly holderId: string | null;
  readonly actorId: string | null;
  readonly endReason: ControlLeaseEndReason | null;
  readonly denialCode: string | null;
  readonly justification: string | null;
  readonly assetInstanceId?: string | null;
}

/** The session's bounded authority trail, with what each half has dropped. */
export interface CommandAuditResponse {
  readonly decisions: readonly CommandAuditRecord[];
  readonly leases: readonly ControlAuditRecord[];
  readonly droppedDecisionCount: number;
  readonly droppedLeaseCount: number;
}

/** Request body for taking control of an asset. */
export interface ControlLeaseRequest {
  readonly holderId: string;
  readonly role: ControlRole;
  readonly durationSeconds?: number | null;
}

/** Request body for pushing a live lease's expiry out. The lease is named
 *  explicitly so a renewal cannot silently renew a replacement lease. */
export interface ControlLeaseRenewRequest {
  readonly holderId: string;
  readonly leaseId: string;
  readonly durationSeconds?: number | null;
}

/** Request body for handing a lease back. */
export interface ControlLeaseReleaseRequest {
  readonly holderId: string;
  readonly leaseId: string;
}

/** Request body for taking an asset from its current holder, on the record. */
export interface ControlPreemptRequest {
  readonly holderId: string;
  readonly role: ControlRole;
  readonly justification: string;
  readonly durationSeconds?: number | null;
}
