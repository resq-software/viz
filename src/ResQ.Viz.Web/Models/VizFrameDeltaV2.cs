/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Text.Json.Serialization;

namespace ResQ.Viz.Web.Models;

/// <summary>
/// The volatile per-capture core of an asset the differ elided from
/// <see cref="VizDeltaV2.Assets"/> because nothing observable about it changed.
/// </summary>
/// <remarks>
/// This record exists so that a carried-forward asset is <b>stamped, never invented</b>. Every
/// field here advances on every capture even for a bolted-down asset — a sequence counter ticks,
/// a receive time is taken, a link is re-stamped as heard — so including them in the
/// change test would report every asset as changed on every frame and the delta would be a full
/// frame plus overhead. Excluding them without re-sending them would be worse: the client would
/// have to re-date the record from the frame envelope, which is the client asserting freshness
/// on the server's behalf. The failure that produces is not hypothetical — a server that stops
/// <i>capturing</i> an asset rather than marking it <see cref="DataFreshness.Lost"/> would leave
/// a client rendering it as eternally fresh, and the operator-visible symptom is an asset that
/// reads "Fresh" beside a climbing age.
/// <para>
/// So the volatile core rides its own cheap channel. It is not free — three ISO-8601 timestamps
/// and their property names dominate it, putting a stamp in the low hundreds of bytes against the
/// roughly one kilobyte a whole <see cref="AssetState"/> serialises to — so an elided asset costs
/// something like a fifth of what sending it whole would, rather than nothing. That is the price
/// of not fabricating a safety-relevant timestamp, and it is worth paying.
/// </para>
/// <para>
/// <b>The channel is what makes eliding a field legitimate, not an afterthought to it.</b>
/// <paramref name="Power"/> was added for exactly that reason: a battery percentage is recomputed
/// from a draining integrator on every capture, so comparing it bit-exact reported every asset in
/// every domain as changed on every frame and left this channel empty at rest and in motion alike.
/// Relaxing the comparison alone would have been the wrong half of the fix — the client's figure
/// would have frozen at whatever it held when it joined. The rule
/// <c>VizSnapshotDiffer.Budget</c> states, and the rule this record exists to serve, is that a
/// field leaves the change test only alongside something that re-delivers it in full.
/// </para>
/// <para>
/// An asset the server stops capturing is then conspicuously absent from
/// <see cref="VizDeltaV2.Assets"/>, <see cref="VizDeltaV2.Carried"/> and
/// <see cref="VizDeltaV2.RemovedAssetIds"/> alike, which is a wire invariant a test can assert
/// rather than a cross-tier convention nobody enforces.
/// </para>
/// </remarks>
/// <param name="AssetId">Identifier of the asset being carried forward unchanged.</param>
/// <param name="SourceTime">Replaces <see cref="AssetState.SourceTime"/> on the carried record.</param>
/// <param name="ReceiveTime">Replaces <see cref="AssetState.ReceiveTime"/> on the carried record.</param>
/// <param name="SequenceNumber">Replaces <see cref="AssetState.SequenceNumber"/> on the carried record.</param>
/// <param name="Freshness">
/// Replaces <see cref="AssetState.Freshness"/> on the carried record. Carried here rather than
/// tested for change so that a transition to <see cref="DataFreshness.Stale"/> or
/// <see cref="DataFreshness.Lost"/> costs a stamp instead of a whole asset state, while still
/// always being transmitted explicitly.
/// </param>
/// <param name="LinkLastHeardAt">
/// Replaces <see cref="LinkState.LastHeardAt"/> on the carried record's
/// <see cref="AssetState.Link"/>. It is here for the same reason the timestamps are: every
/// domain stamps it with the capture's receive time on every capture, so it is a per-capture
/// observation timestamp rather than a state change. Keeping the value real — instead of
/// letting the client leave a stale one in place or synthesise a new one — is what makes
/// "when did we last hear from this asset" answerable off a delta stream.
/// </param>
/// <param name="Power">
/// Replaces <see cref="AssetState.Power"/> on the carried record, or null when the asset's energy
/// state is bit-identical to the base frame's and the client's copy is already correct.
/// <para>
/// Carried in full rather than as a delta or a rounded figure: it is the whole
/// <see cref="PowerState"/> the capture produced, so applying a stamp reconstructs the encoded
/// frame field for field. That exactness is the property the whole scheme rests on — the
/// broadcaster advances its baseline to the frame it published, so a stamp that delivered
/// anything less than the exact value would leave the server comparing against a frame no client
/// holds, and a slowly draining asset would diverge without bound over a session while every
/// round-trip check still passed.
/// </para>
/// <para>
/// It is the largest field on this record — the aggregate figures plus one entry per energy
/// source, a couple of hundred bytes against the roughly one kilobyte a whole
/// <see cref="AssetState"/> costs — and it is null on any frame where the asset drew no energy, so
/// a parked or externally powered asset pays a null rather than a payload. Null is written
/// explicitly rather than omitted, as every other optional member on this wire is: absent and null
/// are distinguishable in this model and the shape should not vary by field.
/// </para>
/// </param>
public sealed record CarriedAssetStamp(
    string AssetId,
    DateTimeOffset SourceTime,
    DateTimeOffset ReceiveTime,
    ulong SequenceNumber,
    DataFreshness Freshness,
    DateTimeOffset? LinkLastHeardAt,
    PowerState? Power = null);

/// <summary>
/// The change between one <see cref="VizSnapshotV2"/> and the next, at entity granularity.
/// </summary>
/// <remarks>
/// A delta describes exactly one transition: apply it to the snapshot it names in
/// <see cref="BaseFrameId"/> and the result is the snapshot it was computed from. It is not a
/// patch language and it is not composable out of order — there is one chain per room, deltas
/// apply in sequence, and a client that cannot place a delta on the frame it holds asks for a
/// keyframe rather than guessing.
/// <para>
/// <b>Entity granularity, not field granularity.</b> A changed asset ships its whole
/// <see cref="AssetState"/>. Field-level patching would need a presence bitmap and a patch type
/// per nested record, plus a merge kept in lockstep with the model on both sides, to save bytes
/// on an asset that changed only its battery percentage — while at stream rate a <i>moving</i>
/// asset changes pose, twist and both timestamps every frame anyway. The saving worth having is
/// omitting assets that did not move at all, and that is an entity-level decision.
/// </para>
/// <para>
/// <b>Omission never means removal.</b> Every collection here is an upsert list paired with an
/// explicit removal list, because an absent entry already means "unchanged" and one wire value
/// cannot carry two meanings. An asset that disappears is named in
/// <see cref="RemovedAssetIds"/>; an asset that is simply unchanged is named in
/// <see cref="Carried"/>; an asset that is in neither and not in <see cref="Assets"/> is a
/// producer bug, and <c>VizSnapshotDiffer.Apply</c> refuses it rather than quietly holding a
/// stale record forever.
/// </para>
/// <para>
/// <b>What a client does with a delta that changes nothing.</b> A delta with no upserts, no
/// removals and no envelope changes is still a real frame and is still applied: it advances
/// <see cref="Tick"/>, <see cref="SimulationTimeSeconds"/> and <see cref="ServerTime"/>, it
/// re-stamps every asset through <see cref="Carried"/>, and it advances the client's held
/// sequence to <see cref="StreamSequence"/>. It is emphatically not a no-op to be discarded —
/// discarding it desynchronises the chain, because the next delta will name it as its base.
/// That applies to the producer as much as to the client. The producer's backpressure works at a
/// coarser grain and one step earlier: a room holds one broadcast slot per stream family, each
/// claimed at the top of a broadcast tick, and a tick that cannot claim one publishes nothing at
/// all on that family and counts a drop under its stream tag — before the chain has moved.
/// Nothing anywhere looks at what a delta <i>contains</i> to decide whether to send it, so
/// <see cref="HasStateChanges"/> describes such a frame and never gates one.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">
/// Stamped from <see cref="VizSnapshotV2.CurrentSchemaVersion"/> so a delta and the keyframes it
/// interleaves with can never claim different schemas. Clients compare the major component only.
/// </param>
/// <param name="FrameId">Unique id for this delta, for correlating logs and client traces.</param>
/// <param name="BaseFrameId">
/// <see cref="VizSnapshotV2.FrameId"/> of the frame this delta applies to. It is the
/// <i>correlation</i> key, not the chain key: a <see cref="Guid"/> has no order, so it can prove
/// a mismatch but cannot say how far apart two frames are. It is carried because it makes the
/// base an assertable invariant on the server and in tests — <c>VizSnapshotDiffer.Apply</c>
/// refuses a baseline whose id does not match — and because every existing log on this path
/// already keys on frame id.
/// </param>
/// <param name="StreamSequence">
/// Position of this delta in its room's chain. Increments once per frame actually handed to the
/// transport, so it counts frames a client could have received rather than ticks the server ran.
/// It is deliberately <b>not</b> deterministic: backpressure drops and subscriber transitions
/// move it. Anything asserting determinism keys on <see cref="Tick"/>.
/// </param>
/// <param name="BaseSequence">
/// <see cref="StreamSequence"/> of the frame this delta applies to. This is the chain key a
/// client tests: accept iff it equals the sequence currently held. <see cref="Tick"/> cannot
/// serve — it does not advance while paused although the broadcast does, so two consecutive
/// paused frames share a tick.
/// </param>
/// <param name="ServerTime">Wall-clock time this delta was assembled.</param>
/// <param name="SimulationTimeSeconds">Simulated time of the frame this delta reconstructs.</param>
/// <param name="Tick">Simulation tick of the frame this delta reconstructs.</param>
/// <param name="Transport">
/// Replacement transport state, or null when it is unchanged apart from its tick. On null a
/// client rebuilds it as the held transport with <see cref="Tick"/> substituted — never by
/// leaving the held tick in place, which would freeze the transport bar mid-stream.
/// </param>
/// <param name="Descriptors">
/// Descriptors whose <see cref="AssetDescriptor.Revision"/> advanced, plus descriptors for
/// newly-appeared assets. Upsert by <see cref="AssetDescriptor.AssetId"/>. Eliding unchanged
/// descriptors is the single largest saving in the format and it is the seam
/// <see cref="VizSnapshotV2"/> was already built around.
/// </param>
/// <param name="RemovedDescriptorIds">
/// Descriptors to drop, by asset id. Kept separate from <see cref="RemovedAssetIds"/> rather
/// than conflated with it: the two collections are near-always identical, but a descriptor
/// published without a matching state — or retained one frame past its state — would be
/// silently mis-merged by a shared list, and the cost of the distinction is an empty array.
/// </param>
/// <param name="Assets">
/// Assets whose observable state changed, plus assets that appeared this frame, as whole
/// records. Upsert by <see cref="AssetState.AssetId"/>.
/// </param>
/// <param name="Carried">
/// Volatile-core stamps for every asset present in both frames that is <i>not</i> in
/// <paramref name="Assets"/>. See <see cref="CarriedAssetStamp"/> for why this channel exists.
/// </param>
/// <param name="RemovedAssetIds">
/// Assets no longer present, by id. Explicit because omission already means unchanged.
/// <para>
/// Correctness here depends on the baseline being the last frame actually <i>sent</i>. If a
/// dropped frame ever advanced the baseline, an asset that spawned and despawned across the gap
/// would never be mentioned in any delta. That rule belongs to the broadcaster; this record only
/// records that it is load-bearing rather than an optimisation.
/// </para>
/// </param>
/// <param name="Tracks">External tracks that changed or appeared, as whole records, keyed by <see cref="ExternalTrackState.TrackId"/>.</param>
/// <param name="RemovedTrackIds">Tracks that expired or were dropped, by id.</param>
/// <param name="Detections">
/// <b>The complete detection list for this frame, never a diff.</b> Detections are per-frame
/// observations recomputed from current poses, not persistent entities: the list is typically
/// under twenty entries, and diffing it would force every client to hold and reconcile a set for
/// no measurable gain. A client replaces its detection list wholesale. This is the first thing to
/// revisit if a deployment's detection list ever grows large.
/// </param>
/// <param name="DetectionsChanged">
/// True when <paramref name="Detections"/> differs from the base frame's list. The list itself is
/// sent whole either way, so this changes nothing a client does — it replaces its detection list
/// unconditionally. It is on the record because "the detections moved" and "the detections are
/// the same standing contacts as last frame" are otherwise indistinguishable downstream: a
/// scenario holding one persistent detection would look like continuous detection churn to every
/// reader of a delta, and the elision assertions built on <see cref="HasStateChanges"/> would hold
/// on no frame of it. Diffing the list to recover that would cost more than the flag.
/// </param>
/// <param name="Hazards">Hazard zones that changed or appeared, keyed by <see cref="HazardV2State.HazardId"/>.</param>
/// <param name="RemovedHazardIds">Hazard zones no longer in effect, by id.</param>
/// <param name="Network">
/// Replacement mesh state, or null when unchanged. Mesh state is replaced whole rather than
/// diffed because <see cref="NetworkState.Partitions"/> is only meaningful against a complete
/// link set — a client that recomputed components from a partial one would report cut-off assets
/// that are not cut off.
/// </param>
/// <param name="NetworkCleared">
/// True when the frame genuinely has no network state, i.e. it went from present to absent.
/// Without this flag <paramref name="Network"/> could not express "the session stopped modelling
/// comms" distinctly from "comms are unchanged", and a client would keep drawing a mesh that no
/// longer exists.
/// </param>
/// <param name="EnvironmentRevision">
/// Replacement environment revision, or null when unchanged. A non-null value means the client's
/// cached terrain and weather are stale; it refetches. Never parse it; compare it.
/// </param>
/// <param name="CommandResults">
/// Command acknowledgements that changed since the base frame, or null when the producer does not
/// push them. This is a fast path and not the record of truth: <c>AssetCommandLog</c> retains
/// results and <c>GET /api/v2/sim/commands/{commandId}</c> polls them, so a lost push costs a
/// round trip and never an outcome. Populated by the broadcaster, not by the differ — a command
/// result is not a function of two snapshots.
/// </param>
/// <param name="EventHighWater">
/// Highest asset-event sequence included in or superseded by this frame. A client stores it; the
/// next delta carries events above it. Zero when the producer publishes no event channel.
/// </param>
/// <param name="DroppedEventCount">
/// Running count of asset events the room's bounded buffer discarded. It advances when a gap
/// outlives the retained buffer, and a client renders the hole rather than presenting a truncated
/// log as continuous. Bounded memory was the requirement; completeness is not promised.
/// </param>
/// <param name="Scenario">Replacement active scenario, or null when unchanged or cleared.</param>
/// <param name="ScenarioCleared">
/// True when a previously active scenario was cleared. Required because null already means
/// unchanged for the replacement field.
/// </param>
public sealed record VizDeltaV2(
    string SchemaVersion,
    Guid FrameId,
    Guid BaseFrameId,
    long StreamSequence,
    long BaseSequence,
    DateTimeOffset ServerTime,
    double SimulationTimeSeconds,
    long Tick,
    TransportState? Transport,
    IReadOnlyList<AssetDescriptor> Descriptors,
    IReadOnlyList<string> RemovedDescriptorIds,
    IReadOnlyList<AssetState> Assets,
    IReadOnlyList<CarriedAssetStamp> Carried,
    IReadOnlyList<string> RemovedAssetIds,
    IReadOnlyList<ExternalTrackState> Tracks,
    IReadOnlyList<string> RemovedTrackIds,
    IReadOnlyList<DetectionV2State> Detections,
    bool DetectionsChanged,
    IReadOnlyList<HazardV2State> Hazards,
    IReadOnlyList<string> RemovedHazardIds,
    NetworkState? Network,
    bool NetworkCleared = false,
    string? EnvironmentRevision = null,
    IReadOnlyList<CommandResult>? CommandResults = null,
    long EventHighWater = 0,
    long DroppedEventCount = 0,
    ScenarioSessionState? Scenario = null,
    bool ScenarioCleared = false)
{
    /// <summary>
    /// True when this delta changes something a viewer could see beyond the clock advancing.
    /// A test-only predicate: no production code path reads it, on either side of the wire.
    /// </summary>
    /// <remarks>
    /// <b>This is a description of a frame, not a decision about one.</b> Nothing in the server
    /// calls it and nothing in the client calls it: the only callers are the differ's own suites,
    /// where it is the one expression of "this frame changed nothing observable" that the equality
    /// rules in <c>VizSnapshotDiffer.Equality</c> and the budget in
    /// <c>VizSnapshotDiffer.Budget</c> are tuned to make true at rest. A predicate that quietly
    /// reported every frame as changed would mean the elision channels were doing nothing, and
    /// this is how that is caught. Earlier revisions of this comment described it instead as the
    /// broadcaster's droppability predicate; that mechanism never existed.
    /// <para>
    /// <b>The mechanism that does exist ignores frame contents entirely.</b> A room holds one
    /// broadcast slot per stream family — one for v1, one for the v2 snapshot and delta streams —
    /// claimed at the top of a broadcast tick and released by the send that holds it. A tick that
    /// cannot claim a family's slot, because that family's previous send is still in flight,
    /// publishes nothing on it and counts a drop on
    /// <c>VizTelemetry.FramesDroppedBackpressure</c> under that stream's tag. That is the whole of
    /// backpressure, and it is decided before <c>SimulationRoom.PublishDeltaFrame</c> encodes
    /// anything, on no information about the frame at all.
    /// </para>
    /// <para>
    /// Which is also why a content-based drop could not be added here later. By the time a delta
    /// exists the room's baseline and stream sequence have already advanced to the frame it
    /// encodes, so withholding it would leave every subscriber's next delta naming a base nobody
    /// holds — the drop would land after the commit rather than before it, which is exactly what
    /// makes skipping a whole tick safe and skipping an encoded frame not.
    /// </para>
    /// <para>
    /// It ignores <see cref="Carried"/>, because re-stamping an unchanged asset is not an
    /// observable change; and it ignores <see cref="CommandResults"/> and the event counters,
    /// because those are lossless traffic rather than visual change and folding them in would
    /// blur what the predicate names. <see cref="JsonIgnoreAttribute"/> because it is derived: a
    /// client recomputes it from the frame it already holds, if it ever wants it, and a client
    /// must never use it to skip a frame it received.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public bool HasStateChanges =>
        Assets.Count > 0
        || RemovedAssetIds.Count > 0
        || Descriptors.Count > 0
        || RemovedDescriptorIds.Count > 0
        || Tracks.Count > 0
        || RemovedTrackIds.Count > 0
        || DetectionsChanged
        || Hazards.Count > 0
        || RemovedHazardIds.Count > 0
        || Transport is not null
        || Network is not null
        || NetworkCleared
        || EnvironmentRevision is not null
        || Scenario is not null
        || ScenarioCleared;
}
