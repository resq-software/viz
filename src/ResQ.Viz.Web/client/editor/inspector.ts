// ResQ Viz - Editor inspector panel (selection-driven, schema-based)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import { hazardKey } from './keys';
import type { Selection, SelectionKind, SelectionStore } from './selection';
import type {
    Vec3,
    Quat,
    DroneState,
    HazardState,
    DetectionState,
} from '../types';
import { formatAge } from '../assets/assetView';
import {
    domainLabel,
    enumLabel,
    freshnessLabel,
    operationalStateLabel,
    vehicleClassLabel,
} from '../assets/AssetFilter';
import { normaliseDeg } from '../assets/panelCards';
import type { SceneAsset, SceneFrame } from '../assets/sceneFrame';
import { assetById, trackById } from '../assets/sceneFrame';
import type { ExternalTrackState } from '../assets/types';
import {
    ComponentHealthStatus,
    LinkLossBehavior,
    LinkTransport,
    MissionExecutionState,
    TrackClassification,
    TrackSourceKind,
    TransponderKind,
    isAirDomainState,
    isGroundDomainState,
    isSurfaceDomainState,
} from '../assets/types';

/** Placeholder rendered for any absent/empty field value. */
const EMPTY = '—'; // em dash

/** Drone command buttons surfaced in the inspector (match server cmd types). */
const DRONE_COMMANDS: ReadonlyArray<{ label: string; cmd: string; title?: string }> = [
    { label: 'Hover', cmd: 'hover', title: 'Stop and hold position' },
    { label: 'RTL', cmd: 'rtl', title: 'Return to launch point' },
    { label: 'Land', cmd: 'land', title: 'Descend and land' },
    { label: 'Auto', cmd: 'auto', title: 'Resume autonomous swarm flight' },
];

// ─── Pure formatters (exported for unit tests) ──────────────────────────────

/** Format a Vec3 as "x · y · z" to `digits` decimals, or EMPTY if absent. */
export function fmtVec(v: Vec3 | undefined, digits = 1): string {
    if (!v || v.length < 3) return EMPTY;
    return `${v[0].toFixed(digits)} · ${v[1].toFixed(digits)} · ${v[2].toFixed(digits)}`;
}

/** Euclidean magnitude of a Vec3 (e.g. speed from a velocity), or EMPTY. */
export function fmtMag(v: Vec3 | undefined, digits = 1): string {
    if (!v || v.length < 3) return EMPTY;
    return Math.hypot(v[0], v[1], v[2]).toFixed(digits);
}

/** Format a quaternion's four components compactly, or EMPTY. */
export function fmtQuat(q: Quat | undefined, digits = 2): string {
    if (!q || q.length < 4) return EMPTY;
    return q.map(n => n.toFixed(digits)).join(' · ');
}

/** Whole-number percent with a trailing %, or EMPTY when undefined. */
export function fmtPct(n: number | undefined): string {
    return n === undefined ? EMPTY : `${Math.round(n)}%`;
}

/** A non-empty string passthrough, or EMPTY. */
export function fmtStr(s: string | undefined): string {
    return s && s.length > 0 ? s : EMPTY;
}

/** A boolean as yes/no, or EMPTY when undefined. */
export function fmtBool(b: boolean | undefined): string {
    return b === undefined ? EMPTY : b ? 'yes' : 'no';
}

// ─── Schemas ────────────────────────────────────────────────────────────────

interface Field<T> {
    readonly label: string;
    readonly value: (entity: T) => string;
}

interface KindSchema {
    readonly title: string;
    readonly resolve: (id: string, frame: SceneFrame | null) => unknown;
    readonly fields: ReadonlyArray<Field<unknown>>;
}

/**
 * Typed schema builder — keeps each kind's field accessors strongly typed
 * against its entity type while erasing to a uniform `unknown` registry entry
 * the Inspector can iterate without per-kind branching.
 *
 * The frame is a {@link SceneFrame}: a `VizFrame` plus the v2 asset and track
 * lists. Widening here rather than adding a second frame type is what the
 * mechanism was built for — every existing v1 caller still satisfies the
 * parameter, and the two new kinds resolve out of the same argument as the old
 * three, with no per-kind branch anywhere in the panel itself.
 */
function defineSchema<T>(s: {
    title: string;
    resolve: (id: string, frame: SceneFrame | null) => T | null;
    fields: ReadonlyArray<Field<T>>;
}): KindSchema {
    return s as unknown as KindSchema;
}

// ─── v2 field helpers ───────────────────────────────────────────────────────

/** Radians clockwise from true north as whole degrees, folded into [0, 360).
 *
 *  Rejects only an absent or non-finite angle. That is the whole check it can
 *  do: a bearing derived from a vanishing vector is a perfectly finite number
 *  and indistinguishable here from a measured one, so anything *derived* must
 *  come through {@link fmtCourse} instead. */
function fmtBearing(rad: number | null | undefined): string {
    if (rad === null || rad === undefined || !Number.isFinite(rad)) return EMPTY;
    return `${Math.round(normaliseDeg((rad * 180) / Math.PI))}°`;
}

/** Speed below which a direction of travel is not a measurement. Matches the
 *  velocity-leader threshold in `TrackOverlay`, so the plot and the panel agree
 *  on when a course exists at all. */
const MIN_COURSE_SPEED_MPS = 0.1;

/**
 * Direction of travel, which exists only when there is travel.
 *
 * `Math.atan2(0, -0)` is π, so a stationary body whose course comes from its
 * velocity renders a confident "180°" — due south — directly beneath a speed of
 * 0.0 m/s. By the time the angle exists it is indistinguishable from a real
 * southward course, so the speed it was derived from is the only thing that can
 * tell them apart. A stationary contact reads as having no course, which is the
 * truth, rather than as heading south, which is a fabrication.
 */
function fmtCourse(
    rad: number | null | undefined,
    speedMps: number | null | undefined,
): string {
    if (speedMps === null || speedMps === undefined || !Number.isFinite(speedMps)) return EMPTY;
    // Absolute: a reversing rover has a negative ground speed and a real course.
    if (Math.abs(speedMps) < MIN_COURSE_SPEED_MPS) return EMPTY;
    return fmtBearing(rad);
}

/** A metre quantity to one decimal, or EMPTY when unreported. Absent is never
 *  rendered as `0.0` — an unmeasured clearance and a zero clearance are
 *  opposite facts. */
function fmtMetres(v: number | null | undefined, digits = 1): string {
    if (v === null || v === undefined || !Number.isFinite(v)) return EMPTY;
    return `${v.toFixed(digits)} m`;
}

/** A speed to one decimal, or EMPTY when unreported. */
function fmtSpeed(v: number | null | undefined): string {
    if (v === null || v === undefined || !Number.isFinite(v)) return EMPTY;
    return `${v.toFixed(1)} m/s`;
}

/** Freshness plus the explicit age behind it. The age is the half of the cue
 *  that survives a screenshot and a colour-blind reader, so it is never dropped
 *  in favour of the word alone. */
function fmtFreshness(asset: SceneAsset): string {
    const word = freshnessLabel(asset.view.freshness);
    const age = asset.view.ageSeconds;
    return age === null ? word : `${word} · ${formatAge(age)}`;
}

/**
 * One line summarising whatever the asset's domain actually reports.
 *
 * Deliberately one row rather than a union of every domain's fields laid out
 * flat: an air asset has no under-keel clearance and a vessel has no rollover
 * risk, and eleven em dashes beside two real numbers is not a readable panel.
 * The `domain` row above already names which domain is speaking, so this row can
 * be read in that context. Full per-domain cards live in the asset panel.
 */
function fmtDomainDetail(asset: SceneAsset): string {
    const d = asset.view.domainState;
    if (d === null) return EMPTY;
    if (isAirDomainState(d)) {
        const parts = [
            d.isAirborne ? 'airborne' : 'on the ground',
            `AGL ${d.altitudeAboveGroundM.toFixed(1)} m`,
            `MSL ${d.altitudeMslM.toFixed(1)} m`,
            `climb ${d.climbRateMps >= 0 ? '+' : ''}${d.climbRateMps.toFixed(1)} m/s`,
        ];
        if (!d.isWithinGeofence) parts.push('outside geofence');
        return parts.join(' · ');
    }
    if (isGroundDomainState(d)) {
        const parts = [
            d.isImmobilised ? `immobilised (${d.immobilisationReason ?? 'reason unreported'})`
                : d.isMoving ? 'moving' : 'stopped',
            d.surfaceType,
            `slope ${Math.round((d.slopeRad * 180) / Math.PI)}°`,
            `traction ${d.tractionCoefficient.toFixed(2)}`,
            // Advisory only — decision support, never a stability guarantee.
            `rollover risk ${Math.round(d.rolloverRisk * 100)}% (advisory)`,
        ];
        return parts.join(' · ');
    }
    if (isSurfaceDomainState(d)) {
        const parts = [
            `depth ${d.waterDepthM.toFixed(1)} m`,
            `draft ${d.draftM.toFixed(1)} m`,
            `UKC ${d.underKeelClearanceM.toFixed(1)} m${d.hasUnsafeUnderKeelClearance ? ' (advisory: unsafe)' : ''}`,
            `current ${d.currentSpeedMps.toFixed(1)} m/s toward ${fmtCourse(d.currentDirectionRad, d.currentSpeedMps)}`,
        ];
        if (!d.isInsideWaterMask) parts.push('outside navigable water');
        if (d.stationKeep?.isEngaged) {
            parts.push(d.stationKeep.isDegraded
                ? `station-keeping degraded (${d.stationKeep.degradedReason ?? 'reason unreported'})`
                : 'station-keeping');
        }
        return parts.join(' · ');
    }
    return EMPTY;
}

/** Speed over ground as the asset's own domain reports it. Air and ground call
 *  it ground speed; a vessel distinguishes speed over ground from speed through
 *  water, and the seabed-referenced one is what matches the position beside it. */
function domainSpeedMps(asset: SceneAsset): number | null {
    const d = asset.view.domainState;
    if (d === null) return null;
    return isSurfaceDomainState(d) ? d.speedOverGroundMps : d.groundSpeedMps;
}

function fmtDomainSpeed(asset: SceneAsset): string {
    return fmtSpeed(domainSpeedMps(asset));
}

/** What the asset does when it loses its command link. Load-bearing per domain:
 *  air must do *something*, ground can simply stop, surface drifts. */
function fmtLinkLoss(asset: SceneAsset): string {
    const d = asset.view.domainState;
    return d === null ? EMPTY : enumLabel(LinkLossBehavior, d.linkLossBehavior);
}

/** Transport plus whether the bearer is up. Independent of freshness: a link can
 *  be up while telemetry has stalled, which is why both rows exist. */
function fmtLink(asset: SceneAsset): string {
    const link = asset.state.link;
    const transport = enumLabel(LinkTransport, link.transport);
    const parts = [`${transport} · ${link.isConnected ? 'connected' : 'down'}`];
    if (link.latencyMs !== null) parts.push(`${Math.round(link.latencyMs)} ms`);
    if (link.packetLossRatio !== null) parts.push(`${(link.packetLossRatio * 100).toFixed(1)}% loss`);
    return parts.join(' · ');
}

/** Overall health, with the count of raised faults when there are any. */
function fmtHealth(asset: SceneAsset): string {
    const health = asset.state.health;
    const word = enumLabel(ComponentHealthStatus, health.overall);
    const faults = health.faults.length;
    return faults === 0 ? word : `${word} · ${faults} fault${faults === 1 ? '' : 's'}`;
}

/** Mission execution and how far through it the asset is. */
function fmtMission(asset: SceneAsset): string {
    const mission = asset.state.mission;
    if (mission === null) return EMPTY;
    const word = enumLabel(MissionExecutionState, mission.execution);
    const pct = `${Math.round(mission.progressFraction * 100)}%`;
    return mission.routeName === null ? `${word} · ${pct}` : `${word} · ${mission.routeName} · ${pct}`;
}

/** The sources that contributed to a track, most recently updated first. */
function fmtTrackSources(track: ExternalTrackState): string {
    if (track.sources.length === 0) return EMPTY;
    return track.sources.map((s) => enumLabel(TrackSourceKind, s.kind)).join(', ');
}

/** Cooperative broadcast identity, or EMPTY when the contact carries none. */
function fmtTransponder(track: ExternalTrackState): string {
    const t = track.transponder;
    if (t === null) return EMPTY;
    const kind = enumLabel(TransponderKind, t.kind);
    return t.callSign === null ? `${kind} ${t.identifier}` : `${kind} ${t.identifier} · ${t.callSign}`;
}

/**
 * Per-kind inspector schemas.
 *
 * `drone`, `hazard` and `detection` read the v1 frame and are unchanged.
 * `asset` and `track` read the v2 lists, so a rover, a vessel and a transponder
 * contact all inspect through the same panel as a drone — no per-kind branch in
 * the Inspector itself, which is the property `defineSchema` exists to keep.
 */
export const SCHEMAS: Readonly<Record<SelectionKind, KindSchema>> = {
    drone: defineSchema<DroneState>({
        title: 'Drone',
        resolve: (id, frame) => frame?.drones?.find(d => d.id === id) ?? null,
        fields: [
            { label: 'status', value: d => fmtStr(d.status) },
            { label: 'armed', value: d => fmtBool(d.armed) },
            { label: 'battery', value: d => fmtPct(d.battery) },
            { label: 'vendor', value: d => fmtStr(d.vendor) },
            { label: 'position', value: d => fmtVec(d.pos) },
            { label: 'velocity', value: d => fmtVec(d.vel) },
            { label: 'speed', value: d => fmtMag(d.vel) },
            { label: 'rotation', value: d => fmtQuat(d.rot) },
        ],
    }),
    hazard: defineSchema<HazardState>({
        title: 'Hazard',
        resolve: (id, frame) => frame?.hazards?.find(h => hazardKey(h) === id) ?? null,
        fields: [
            { label: 'type', value: h => fmtStr(h.type) },
            { label: 'centre', value: h => fmtVec(h.center) },
            { label: 'radius', value: h => (h.radius === undefined ? EMPTY : h.radius.toFixed(1)) },
        ],
    }),
    detection: defineSchema<DetectionState>({
        title: 'Detection',
        resolve: (id, frame) => frame?.detections?.find(d => d.id === id) ?? null,
        fields: [
            { label: 'type', value: d => fmtStr(d.type) },
            // Labelled "source" rather than "drone": v2 detections name a
            // `sourceAssetId` precisely because any domain detects, and a rover's
            // find must not read as a drone's.
            { label: 'source', value: d => fmtStr(d.droneId) },
            { label: 'confidence', value: d => `${Math.round(d.confidence * 100)}%` },
            { label: 'position', value: d => fmtVec(d.pos) },
        ],
    }),
    asset: defineSchema<SceneAsset>({
        title: 'Asset',
        resolve: (id, frame) => assetById(frame?.assets, id),
        fields: [
            { label: 'domain', value: a => domainLabel(a.view.domain) },
            { label: 'class', value: a => vehicleClassLabel(a.view.vehicleClass) },
            { label: 'agency', value: a => fmtStr(a.descriptor.agencyId ?? undefined) },
            { label: 'fleet', value: a => fmtStr(a.descriptor.fleetId ?? undefined) },
            { label: 'state', value: a => operationalStateLabel(a.view.operationalState) },
            { label: 'mode', value: a => fmtStr(a.view.mode) },
            { label: 'freshness', value: a => fmtFreshness(a) },
            // Null power is an unmetered supply — a tether, shore power — and is
            // shown as absent rather than as a flat pack.
            { label: 'power', value: a => fmtPct(a.view.powerPercent ?? undefined) },
            { label: 'health', value: a => fmtHealth(a) },
            { label: 'link', value: a => fmtLink(a) },
            { label: 'on link loss', value: a => fmtLinkLoss(a) },
            { label: 'mission', value: a => fmtMission(a) },
            { label: 'position', value: a => fmtVec(a.view.position) },
            { label: 'velocity', value: a => fmtVec(a.view.velocity) },
            { label: 'speed', value: a => fmtMag(a.view.velocity) },
            // Heading and course over ground are separate rows because they
            // genuinely diverge — in wind, in a cross-current, and whenever a
            // rover is slipping. Collapsing them is the modelling error the wire
            // contract was written to prevent.
            { label: 'heading', value: a => fmtBearing(a.view.domainState?.headingRad) },
            { label: 'course', value: a => fmtCourse(a.view.domainState?.courseOverGroundRad, domainSpeedMps(a)) },
            { label: 'over ground', value: a => fmtDomainSpeed(a) },
            { label: 'domain detail', value: a => fmtDomainDetail(a) },
        ],
    }),
    track: defineSchema<ExternalTrackState>({
        title: 'Contact',
        resolve: (id, frame) => trackById(frame?.tracks, id),
        // No command affordance is reachable from here: `_renderActions` renders
        // buttons for the `drone` kind alone, and a track has no capabilities to
        // generate any from. That is the whole difference between a contact and
        // an asset, and it is enforced by the absence rather than by a check.
        fields: [
            { label: 'classification', value: t => enumLabel(TrackClassification, t.classification) },
            { label: 'label', value: t => fmtStr(t.label ?? undefined) },
            { label: 'identity', value: t => fmtTransponder(t) },
            { label: 'position', value: t => fmtVec([t.pose.position.x, t.pose.position.y, t.pose.position.z]) },
            { label: 'speed', value: t => fmtSpeed(Math.hypot(t.twist.linear.x, t.twist.linear.z)) },
            // Derived, not reported: the wire carries a twist even for a
            // stationary contact, so this is the default case rather than an
            // edge one, and `atan2(0, -0)` is due south.
            { label: 'course', value: t => fmtCourse(
                Math.atan2(t.twist.linear.x, -t.twist.linear.z),
                Math.hypot(t.twist.linear.x, t.twist.linear.z),
            ) },
            { label: 'freshness', value: t => freshnessLabel(t.freshness) },
            { label: 'confidence', value: t => `${Math.round(t.quality.confidence * 100)}%` },
            // Null accuracy is no accuracy statistic, not a perfect fix: rendered
            // absent so nobody draws a point where a circle belongs.
            { label: 'accuracy', value: t => fmtMetres(t.quality.positionAccuracyM) },
            { label: 'sources', value: t => fmtTrackSources(t) },
            { label: 'observations', value: t => `${t.quality.updateCount}${t.quality.isFused ? ' · fused' : ''}` },
        ],
    }),
};

// ─── Inspector panel ────────────────────────────────────────────────────────

/**
 * Selection-driven property panel — the editor layer's read surface.
 *
 * Subscribes to a {@link SelectionStore}: on selection it renders the matching
 * schema's rows once, then {@link Inspector.update} refreshes only the text
 * each frame (no DOM rebuild). Distinct in role from the operator-facing
 * `DronePanel` — this is the raw, all-fields, any-entity inspector that future
 * editable fields and gizmo bindings hang off.
 */
export class Inspector {
    private readonly _root: HTMLElement;
    private readonly _kindEl: HTMLElement;
    private readonly _idEl: HTMLElement;
    private readonly _fieldsEl: HTMLElement;
    private readonly _actionsEl: HTMLElement;
    private readonly _getFrame: () => SceneFrame | null;
    private _sel: Selection | null = null;
    private _onCloseFn: (() => void) | null = null;
    private _onCommandFn: ((id: string, cmd: string) => void) | null = null;
    private _onMoveFn: ((id: string) => void) | null = null;
    private _moveBtn: HTMLButtonElement | null = null;
    private _visible = false;
    /** Live cell refs so per-frame updates touch text only, not the tree. */
    private _cells: Array<{ field: Field<unknown>; el: HTMLElement }> = [];

    constructor(store: SelectionStore, getFrame: () => SceneFrame | null, parent: HTMLElement) {
        this._getFrame = getFrame;
        const built = this._build(parent);
        this._root = built.root;
        this._kindEl = built.kindEl;
        this._idEl = built.idEl;
        this._fieldsEl = built.fieldsEl;
        this._actionsEl = built.actionsEl;
        built.closeBtn.addEventListener('click', () => {
            // Prefer the app-wired unified deselect (keeps legacy HUD surfaces
            // in sync); fall back to clearing the store directly.
            if (this._onCloseFn) this._onCloseFn();
            else store.clear();
        });
        store.subscribe(sel => this._onSelection(sel));
    }

    /** Route the close button to the app's unified deselect path. */
    onClose(fn: () => void): void {
        this._onCloseFn = fn;
    }

    /** Wire the drone command buttons (Hover/RTL/Land) to a command sender. */
    onCommand(fn: (id: string, cmd: string) => void): void {
        this._onCommandFn = fn;
    }

    /** Wire the "Move" button to a move-mode toggle (the reposition gizmo). */
    onMove(fn: (id: string) => void): void {
        this._onMoveFn = fn;
    }

    /** Reflect move-mode state on the Move button (app is the source of truth). */
    setMoveActive(on: boolean): void {
        this._moveBtn?.setAttribute('aria-pressed', String(on));
        this._moveBtn?.classList.toggle('is-active', on);
    }

    /**
     * Refresh field values from the latest frame. Cheap — text only. Hides the
     * panel if the selected entity has dropped out of the frame (mirrors how
     * DronePanel handles a vanished drone).
     */
    update(frame: SceneFrame | null): void {
        if (!this._sel) return;
        const schema = SCHEMAS[this._sel.kind];
        const entity = schema.resolve(this._sel.id, frame);
        if (entity === null) {
            this._hide();
            return;
        }
        for (const { field, el } of this._cells) {
            const v = field.value(entity);
            el.textContent = v;
            el.classList.toggle('is-empty', v === EMPTY);
        }
        // update() owns final visibility: show iff the entity is present, so a
        // drone that drops out then reappears re-shows without a re-select.
        this._show();
    }

    private _onSelection(sel: Selection | null): void {
        this._sel = sel;
        if (!sel) {
            this._hide();
            return;
        }
        const schema = SCHEMAS[sel.kind];
        this._kindEl.textContent = schema.title;
        this._idEl.textContent = sel.id;
        this._idEl.title = sel.id;
        this._renderFields(schema);
        this._renderActions(sel);
        this.update(this._getFrame()); // fills values + shows iff entity present
    }

    private _renderFields(schema: KindSchema): void {
        this._fieldsEl.replaceChildren();
        this._cells = [];
        for (const field of schema.fields) {
            const dt = document.createElement('dt');
            dt.className = 'ri-key';
            dt.textContent = field.label;
            const dd = document.createElement('dd');
            dd.className = 'ri-val';
            this._fieldsEl.append(dt, dd);
            this._cells.push({ field, el: dd });
        }
    }

    /** Per-kind action buttons below the fields — drone commands + Move toggle. */
    private _renderActions(sel: Selection): void {
        this._actionsEl.replaceChildren();
        this._moveBtn = null;
        if (sel.kind !== 'drone') return;
        for (const { label, cmd, title } of DRONE_COMMANDS) {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'ri-cmd';
            b.textContent = label;
            if (title) b.title = title;
            b.addEventListener('click', () => this._onCommandFn?.(sel.id, cmd));
            this._actionsEl.append(b);
        }
        // "Move" toggles the reposition gizmo. App owns the on/off state
        // (setMoveActive) so the M key and this button stay in sync; rebuilding
        // on each selection resets it to off.
        //
        // That reset is only correct because TransformGizmo clears _moveMode on
        // EVERY selection change, drone-to-drone included. It used to clear only
        // when the new selection was not a drone, so switching between drones
        // left the gizmo live while this button rendered "off" — and the next
        // click read inverted. Keep the two in step if either side changes.
        const move = document.createElement('button');
        move.type = 'button';
        move.className = 'ri-cmd ri-move';
        move.textContent = 'Move';
        move.setAttribute('aria-pressed', 'false');
        move.addEventListener('click', () => this._onMoveFn?.(sel.id));
        this._actionsEl.append(move);
        this._moveBtn = move;
    }

    private _show(): void {
        if (this._visible) return;
        this._visible = true;
        this._root.hidden = false;
        // Per WAI-ARIA, expose by removing aria-hidden, not setting it "false".
        this._root.removeAttribute('aria-hidden');
    }

    private _hide(): void {
        if (!this._visible) return;
        this._visible = false;
        this._root.hidden = true;
        this._root.setAttribute('aria-hidden', 'true');
    }

    private _build(parent: HTMLElement): {
        root: HTMLElement;
        kindEl: HTMLElement;
        idEl: HTMLElement;
        fieldsEl: HTMLElement;
        actionsEl: HTMLElement;
        closeBtn: HTMLElement;
    } {
        const root = document.createElement('aside');
        root.className = 'resq-inspector';
        root.hidden = true;
        root.setAttribute('aria-label', 'Selection inspector');
        root.setAttribute('aria-hidden', 'true');

        const head = document.createElement('header');
        head.className = 'ri-head';
        const kindEl = document.createElement('span');
        kindEl.className = 'ri-kind';
        const idEl = document.createElement('span');
        idEl.className = 'ri-id';
        const closeBtn = document.createElement('button');
        closeBtn.className = 'ri-close';
        closeBtn.type = 'button';
        closeBtn.setAttribute('aria-label', 'Clear selection');
        closeBtn.textContent = '×'; // ×
        head.append(kindEl, idEl, closeBtn);

        const fieldsEl = document.createElement('dl');
        fieldsEl.className = 'ri-fields';

        const actionsEl = document.createElement('div');
        actionsEl.className = 'ri-actions';

        root.append(head, fieldsEl, actionsEl);
        parent.appendChild(root);
        return { root, kindEl, idEl, fieldsEl, actionsEl, closeBtn };
    }
}
