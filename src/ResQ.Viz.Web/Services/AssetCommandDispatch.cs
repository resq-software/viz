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

using System.Diagnostics.CodeAnalysis;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

/// <summary>Turns a validated <see cref="CommandIntent"/> into the command an asset executes.</summary>
/// <remarks>
/// The last step of the pipeline, and deliberately the only one that knows both vocabularies.
/// Everything upstream speaks the wire's string kinds; everything downstream speaks
/// <see cref="AssetCommandKind"/>. Keeping the mapping in one pure static function means a kind
/// that has no simulation counterpart yet — a route to follow, a dock to resolve — is refused
/// here with a stable code, rather than reaching an asset that silently ignores it.
/// <para>
/// Pure: no clock, no logging, no world access, no mutation. Translation failure is not an
/// exception because "we cannot execute this yet" is an ordinary, expected answer that has to
/// travel back to the issuer as a reason code.
/// </para>
/// </remarks>
public static class AssetCommandTranslator
{
    /// <summary>Translates a validated intent, or explains why it cannot be executed.</summary>
    /// <remarks>
    /// A geodetic, asset-referenced or route-referenced target is refused rather than
    /// approximated. Resolving one needs something this layer does not have — a
    /// <see cref="LocalOrigin"/> for the geodetic case, a live registry lookup for the asset
    /// case, a route store for the third — and guessing a position from a target we cannot
    /// resolve is how a vehicle ends up driving somewhere nobody asked for.
    /// <para>
    /// <b>None of the three is reachable from a conforming client any more.</b> A geodetic target
    /// is re-expressed as a point at the REST boundary, where the origin is known;
    /// <see cref="CommandTargetKinds.Asset"/> is advertised by no catalog row since <c>dock</c>
    /// withdrew it; and <see cref="CommandTargetKinds.Route"/> is advertised by none since
    /// <c>followRoute</c> was withdrawn whole, for exactly the refusal below. Each was a command
    /// the capability report offered and this function then refused, which is a promise that
    /// cannot be kept rather than a momentary "not now". The arms stay as backstops — this
    /// function is callable without passing either gate, and a refusal here is cheaper than a
    /// vehicle aimed at an approximation — but nothing should now reach them.
    /// </para>
    /// </remarks>
    /// <param name="intent">Intent produced by <see cref="CommandCatalog.Validate"/>.</param>
    /// <param name="command">The translated command on success.</param>
    /// <param name="reasonCode">Stable code from <see cref="CommandRejectionReasons"/> on failure.</param>
    /// <param name="message">Operator-facing explanation on failure.</param>
    /// <returns><see langword="true"/> when the intent could be translated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="intent"/> is null.</exception>
    public static bool TryTranslate(
        CommandIntent intent,
        out SimulatedAssetCommand command,
        [NotNullWhen(false)] out string? reasonCode,
        [NotNullWhen(false)] out string? message)
    {
        ArgumentNullException.ThrowIfNull(intent);

        command = default;

        var kind = ToAssetCommandKind(intent.Kind);
        if (kind == AssetCommandKind.Unspecified)
        {
            // A server-side gap, not a malformed request: the kind passed the catalog, so the
            // caller sent something this build registered and then could not execute. A
            // payload-class code here would report the server's own gap as the caller's typo.
            reasonCode = CommandContractReasons.KindNotExecutable;
            message = $"Command kind '{intent.Kind}' has no simulation counterpart.";
            return false;
        }

        // Captured into a local so the null-state of the union is tracked across the switch;
        // a property re-read in the default arm would not be.
        var requested = intent.Target;
        FramedPose? target = null;

        switch (requested)
        {
            case null:
                break;

            case PointCommandTarget point:
                target = point.Point;
                break;

            default:
                // Not a payload complaint: the shape is well formed, and this build simply owns
                // no registry to resolve it against: no asset registry for an asset-referenced
                // berth, no route store for a route identifier. The HTTP layer keys the status
                // off the code's prefix, so borrowing a payload code here would answer a server
                // limitation with "your request is malformed". A shape that reaches this arm
                // while a catalog row still advertises it is the advertised-is-not-accepted bug
                // that withdrew Asset from the dock row and then withdrew followRoute entirely;
                // CrossDomainInvariantTests pins that, with no quarantine list to hide in.
                reasonCode = CommandContractReasons.TargetNotResolvable;
                message =
                    $"Target shape '{requested.Kind}' cannot be resolved by this simulation; " +
                    "supply a framed point target.";
                return false;
        }

        command = new SimulatedAssetCommand(
            Kind: kind,
            AssetId: intent.AssetId,
            Target: target,
            SpeedMps: intent.SpeedMps,
            HeadingRad: intent.CourseRad,
            AltitudeM: intent.AltitudeM,
            CommandId: intent.CommandId,

            // An intent's altitude is already on the scene's vertical axis: the API boundary
            // converts a reference-qualified one there, where the terrain under the asset is
            // known, and refuses one that named no datum at all. Stamping the datum rather than
            // leaving it Unknown is what lets the executor refuse an altitude that reached it
            // without passing that boundary.
            AltitudeReference: intent.AltitudeM is null
                ? VerticalReference.Unknown
                : VerticalReference.MeanSeaLevel);

        reasonCode = null;
        message = null;
        return true;
    }

    /// <summary>Maps a catalog kind token to the enum the simulation executes.</summary>
    /// <remarks>
    /// Ordinal and exhaustive over <see cref="CommandKinds"/>. A token with no member here
    /// returns <see cref="AssetCommandKind.Unspecified"/>, which the caller reports as an
    /// unknown kind — the same answer the catalog gives for a token it has never heard of, so a
    /// half-registered command cannot look like a working one.
    /// </remarks>
    /// <param name="kind">Wire token from <see cref="CommandKinds"/>.</param>
    /// <returns>The matching kind, or <see cref="AssetCommandKind.Unspecified"/>.</returns>
    public static AssetCommandKind ToAssetCommandKind(string? kind) => kind switch
    {
        CommandKinds.Stop => AssetCommandKind.Stop,
        CommandKinds.EmergencyStop => AssetCommandKind.EmergencyStop,
        CommandKinds.Hold => AssetCommandKind.Hold,
        CommandKinds.ResumeAutonomy => AssetCommandKind.ResumeAutonomy,
        CommandKinds.GoTo => AssetCommandKind.GoTo,
        CommandKinds.FollowRoute => AssetCommandKind.FollowRoute,
        CommandKinds.ReturnToBase => AssetCommandKind.ReturnToBase,
        CommandKinds.SetSpeed => AssetCommandKind.SetSpeed,
        CommandKinds.Takeoff => AssetCommandKind.Takeoff,
        CommandKinds.Land => AssetCommandKind.Land,
        CommandKinds.SetAltitude => AssetCommandKind.SetAltitude,
        CommandKinds.Loiter => AssetCommandKind.Loiter,
        CommandKinds.DriveTo => AssetCommandKind.DriveTo,
        CommandKinds.SetSteering => AssetCommandKind.SetSteering,
        CommandKinds.Reverse => AssetCommandKind.Reverse,
        CommandKinds.Park => AssetCommandKind.Park,
        CommandKinds.TransitTo => AssetCommandKind.TransitTo,
        CommandKinds.SetCourse => AssetCommandKind.SetCourse,
        CommandKinds.StationKeep => AssetCommandKind.StationKeep,
        CommandKinds.Dock => AssetCommandKind.Dock,
        CommandKinds.Undock => AssetCommandKind.Undock,
        _ => AssetCommandKind.Unspecified,
    };

    /// <summary>Maps an executable kind back to its catalog token.</summary>
    /// <remarks>
    /// The inverse of <see cref="ToAssetCommandKind"/>, and the reason an asset can re-check a
    /// command's capability requirement against the same table the validator gated it with
    /// instead of a second, drifting copy. Before this existed the two disagreed: the catalog
    /// advertised <c>hold</c> to every mobile asset while the executor demanded
    /// <see cref="AssetCapability.StationKeep"/>, so a vessel was offered a command it would
    /// then refuse.
    /// </remarks>
    /// <param name="kind">Executable kind.</param>
    /// <returns>The wire token, or <see langword="null"/> for <see cref="AssetCommandKind.Unspecified"/>.</returns>
    public static string? ToCatalogKind(AssetCommandKind kind) => kind switch
    {
        AssetCommandKind.Stop => CommandKinds.Stop,
        AssetCommandKind.EmergencyStop => CommandKinds.EmergencyStop,
        AssetCommandKind.Hold => CommandKinds.Hold,
        AssetCommandKind.ResumeAutonomy => CommandKinds.ResumeAutonomy,
        AssetCommandKind.GoTo => CommandKinds.GoTo,
        AssetCommandKind.FollowRoute => CommandKinds.FollowRoute,
        AssetCommandKind.ReturnToBase => CommandKinds.ReturnToBase,
        AssetCommandKind.SetSpeed => CommandKinds.SetSpeed,
        AssetCommandKind.Takeoff => CommandKinds.Takeoff,
        AssetCommandKind.Land => CommandKinds.Land,
        AssetCommandKind.SetAltitude => CommandKinds.SetAltitude,
        AssetCommandKind.Loiter => CommandKinds.Loiter,
        AssetCommandKind.DriveTo => CommandKinds.DriveTo,
        AssetCommandKind.SetSteering => CommandKinds.SetSteering,
        AssetCommandKind.Reverse => CommandKinds.Reverse,
        AssetCommandKind.Park => CommandKinds.Park,
        AssetCommandKind.TransitTo => CommandKinds.TransitTo,
        AssetCommandKind.SetCourse => CommandKinds.SetCourse,
        AssetCommandKind.StationKeep => CommandKinds.StationKeep,
        AssetCommandKind.Dock => CommandKinds.Dock,
        AssetCommandKind.Undock => CommandKinds.Undock,
        _ => null,
    };
}

/// <summary>
/// The commands one session has issued: their idempotency keys, their latest results, and the
/// trail of every decision taken about who may command what — refusals, lease grants, and the
/// link changes that decide whether an asset can be reached at all.
/// </summary>
/// <remarks>
/// Owned by a <see cref="SimulationRoom"/> so its lifetime is exactly the session's. Keys are
/// issuer-chosen and only unique within the scope that hands them out, so a process-wide store
/// would let one room's key collide with another's, and a room reaped by the session reaper
/// would leak its results forever.
/// <para>
/// Synchronised internally with its own gate, deliberately <b>not</b> the room's simulation
/// lock. Recording a result must never contend with the 60 Hz tick loop, and nothing here
/// touches world state, so widening the simulation lock to cover it would buy nothing and cost
/// tick latency.
/// </para>
/// <para>
/// Bounded in size as well as time. A client that issues commands faster than they expire would
/// otherwise grow this without limit; the oldest results are evicted first, which at worst turns
/// a very old poll into a 404 rather than an unbounded dictionary.
/// </para>
/// </remarks>
public sealed class AssetCommandLog
{
    /// <summary>How long an idempotency key stays claimed after its last update.</summary>
    /// <remarks>Only has to outlive a client's retry budget; ten minutes is comfortably past any sane one.</remarks>
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromMinutes(10);

    /// <summary>Maximum command results retained before the oldest are evicted.</summary>
    private const int MaxTrackedResults = 512;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, CommandResult> _results = new();
    private readonly Queue<Guid> _insertionOrder = new();

    private CommandIdempotencyLedger _ledger = new(IdempotencyRetention);
    private long _generation;

    /// <summary>Opens a generation-bound view for one command request.</summary>
    internal AssetCommandLogSession OpenSession()
    {
        lock (_gate)
        {
            return new AssetCommandLogSession(this, _generation);
        }
    }

    internal bool IsCurrent(long generation)
    {
        lock (_gate)
        {
            return generation == _generation;
        }
    }

    internal AssetCommandReplayResolution ResolveReplay(
        long generation,
        CommandIdempotencyDecision decision,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return new AssetCommandReplayResolution(false, null);
            }

            var priorId = decision.Existing?.CommandId ?? Guid.Empty;
            var replayed = _results.TryGetValue(priorId, out var stored)
                ? stored
                : new CommandResult(
                    priorId,
                    decision.Existing?.State ?? CommandState.Accepted,
                    now,
                    0,
                    "Duplicate of an earlier command with the same idempotency key.");
            return new AssetCommandReplayResolution(true, replayed);
        }
    }

    internal CommandIdempotencyDecision Classify(
        long generation,
        AssetCommandEnvelope envelope,
        DateTimeOffset now) =>
        WithGeneration(
            generation,
            ledger => ledger.Classify(envelope, now),
            new CommandIdempotencyDecision(CommandIdempotencyOutcome.New, string.Empty, null));

    internal CommandIdempotencyDecision Claim(
        long generation,
        AssetCommandEnvelope envelope,
        DateTimeOffset now) =>
        WithGeneration(
            generation,
            ledger => ledger.Claim(envelope, now),
            new CommandIdempotencyDecision(CommandIdempotencyOutcome.New, string.Empty, null));

    internal bool Update(
        long generation,
        string idempotencyKey,
        CommandState state,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            _ledger.Update(idempotencyKey, state, now);
            return true;
        }
    }

    internal bool Record(long generation, CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            RecordCore(result);
            return true;
        }
    }

    internal bool Complete(
        long generation,
        CommandResult result,
        string idempotencyKey,
        CommandState state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            RecordCore(result);
            _ledger.Update(idempotencyKey, state, now);
            return true;
        }
    }

    private T WithGeneration<T>(
        long generation,
        Func<CommandIdempotencyLedger, T> action,
        T stale)
    {
        lock (_gate)
        {
            return generation == _generation ? action(_ledger) : stale;
        }
    }

    /// <summary>Ledger deciding whether an incoming command is new, a retry or a key conflict.</summary>
    /// <remarks>
    /// Retained for direct lifecycle integrations. Request dispatch uses a generation-bound
    /// session so a retained reference to an earlier ledger cannot affect a replacement world.
    /// </remarks>
    public CommandIdempotencyLedger Idempotency
    {
        get { lock (_gate) { return _ledger; } }
    }

    /// <summary>Records the latest result for a command, replacing any earlier one.</summary>
    /// <param name="result">Result to store; its <see cref="CommandResult.CommandId"/> is the key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public void Record(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            RecordCore(result);
        }
    }

    private void RecordCore(CommandResult result)
    {
        if (!_results.ContainsKey(result.CommandId))
        {
            _insertionOrder.Enqueue(result.CommandId);
        }

        _results[result.CommandId] = result;

        while (_insertionOrder.Count > MaxTrackedResults)
        {
            _results.Remove(_insertionOrder.Dequeue());
        }
    }

    /// <summary>Looks up the latest result for a command.</summary>
    /// <param name="commandId">Command to poll.</param>
    /// <param name="result">The stored result on success, otherwise null.</param>
    /// <returns><see langword="true"/> when the command is still tracked.</returns>
    public bool TryGet(Guid commandId, [NotNullWhen(true)] out CommandResult? result)
    {
        lock (_gate)
        {
            return _results.TryGetValue(commandId, out result);
        }
    }

    /// <summary>Most authority decisions retained before the oldest are dropped.</summary>
    /// <remarks>
    /// Matches the lease trail's default window, so the two halves an operator reads side by side
    /// cover comparable ground rather than one silently reaching further back than the other.
    /// </remarks>
    private const int MaxDecisionRecords = 256;

    private readonly Queue<CommandAuditRecord> _decisions = new();
    private long _decisionSequence;
    private long _droppedDecisions;

    /// <summary>
    /// Decisions discarded to stay inside the window, so a reader never mistakes a truncated
    /// trail for a complete one.
    /// </summary>
    public long DroppedDecisionCount
    {
        get { lock (_gate) { return _droppedDecisions; } }
    }

    /// <summary>Appends one authority decision to this session's trail.</summary>
    /// <remarks>
    /// Called once per settled outcome and never on a check. A command that is accepted, refused
    /// by any gate, or refused because its issuer's control was taken produces exactly one record
    /// here; a replay of an already-decided command produces none, because the decision it
    /// replays is already in the trail and appending a copy per retry would let a client's retry
    /// budget push the records that explain an incident out of the window.
    /// <para>
    /// Lease operations and command-link changes land here too, and for the same reason: a
    /// reviewer asking why a vehicle stopped taking instructions has to find the answer in one
    /// place, whether it was refused, preempted, or simply could no longer be heard. A link
    /// change that changed nothing records nothing, on the same idempotency argument as a
    /// command replay.
    /// </para>
    /// <para>
    /// Oldest-first eviction, for the same reason the lease trail uses it: after an incident the
    /// records that explain it are the recent ones, and refusing new records instead would mean
    /// the trail stopped describing the present exactly when it mattered. Sequence numbers keep
    /// counting across a drop, so truncation is visible from the records alone.
    /// </para>
    /// </remarks>
    /// <param name="decision">What was decided.</param>
    /// <param name="at">Instant the decision was made.</param>
    /// <param name="correlationId">Trace identifier of the request that produced it.</param>
    /// <param name="assetId">Asset the decision concerns.</param>
    /// <param name="issuerId">Operator, station or service the request came from.</param>
    /// <param name="commandId">Command it concerns, or null on a lease decision.</param>
    /// <param name="kind">Command kind, or null on a lease decision.</param>
    /// <param name="leaseId">Lease it concerns, or null when none was named or produced.</param>
    /// <param name="reasonCode">Stable code for a refusal or a modification, or null on a plain acceptance.</param>
    /// <param name="detail">Operator-facing prose.</param>
    /// <returns>The record as stored, including the sequence number it was given.</returns>
    public CommandAuditRecord RecordDecision(
        CommandDecision decision,
        DateTimeOffset at,
        string correlationId,
        string assetId,
        string issuerId,
        Guid? commandId = null,
        string? kind = null,
        string? leaseId = null,
        string? reasonCode = null,
        string? detail = null)
    {
        lock (_gate)
        {
            var record = new CommandAuditRecord(
                ++_decisionSequence, decision, at, correlationId, assetId, commandId, kind,
                issuerId, leaseId, reasonCode, detail);

            _decisions.Enqueue(record);

            while (_decisions.Count > MaxDecisionRecords)
            {
                _decisions.Dequeue();
                _droppedDecisions++;
            }

            return record;
        }
    }

    /// <summary>The retained decision trail, oldest first.</summary>
    /// <returns>A materialised copy of at most <see cref="MaxDecisionRecords"/> records.</returns>
    public IReadOnlyList<CommandAuditRecord> ReadDecisions()
    {
        lock (_gate)
        {
            return [.. _decisions];
        }
    }

    /// <summary>Forgets every command and every claimed idempotency key.</summary>
    /// <remarks>
    /// Called when the room resets. The ledger is replaced rather than pruned so a key claimed
    /// against the discarded world cannot suppress the same command against the new one — after
    /// a reset the assets it referred to no longer exist, and reporting the old result would be
    /// a lie about a vehicle that is gone.
    /// <para>
    /// <b>The decision trail survives.</b> What a reset discards is the world and the commands
    /// still tracked against it, not the record of who was told no and why — the same stance
    /// <c>ControlAuthority.Reset</c> takes with its lease trail, and for the same reason: a reset
    /// is frequently the first thing that happens after the incident somebody will ask about. It
    /// stays bounded by its own window regardless of how often a session resets.
    /// </para>
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _results.Clear();
            _insertionOrder.Clear();
            _ledger = new CommandIdempotencyLedger(IdempotencyRetention);
            _generation++;
        }
    }
}

/// <summary>A command-log handle valid only for the world generation that opened it.</summary>
internal sealed class AssetCommandLogSession(AssetCommandLog owner, long generation)
{
    internal bool IsCurrent => owner.IsCurrent(generation);

    internal CommandIdempotencyDecision Classify(AssetCommandEnvelope envelope, DateTimeOffset now) =>
        owner.Classify(generation, envelope, now);

    internal CommandIdempotencyDecision Claim(AssetCommandEnvelope envelope, DateTimeOffset now) =>
        owner.Claim(generation, envelope, now);

    internal AssetCommandReplayResolution ResolveReplay(
        CommandIdempotencyDecision decision,
        DateTimeOffset now) =>
        owner.ResolveReplay(generation, decision, now);

    internal bool Update(string key, CommandState state, DateTimeOffset now) =>
        owner.Update(generation, key, state, now);

    internal bool Record(CommandResult result) => owner.Record(generation, result);

    internal bool Complete(
        CommandResult result,
        string key,
        CommandState state,
        DateTimeOffset now) =>
        owner.Complete(generation, result, key, state, now);
}

/// <summary>A replay lookup paired with its command-log generation validity.</summary>
internal readonly record struct AssetCommandReplayResolution(
    bool IsCurrent,
    CommandResult? Result);
