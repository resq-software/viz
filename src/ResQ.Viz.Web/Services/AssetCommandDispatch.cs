/**
 * Copyright 2024 ResQ Technologies Ltd.
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
                // Not a payload complaint: the shape is advertised and well formed, and this
                // build simply owns no registry to resolve it against. The HTTP layer keys the
                // status off the code's prefix, so borrowing a payload code here would answer a
                // server limitation with "your request is malformed".
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
/// The commands one session has issued: their idempotency keys, and their latest results.
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

    /// <summary>Ledger deciding whether an incoming command is new, a retry or a key conflict.</summary>
    /// <remarks>
    /// Exposed rather than wrapped: the ledger is already internally synchronised, and its
    /// classify/claim/update split is the contract callers need. Wrapping it here would only
    /// duplicate three methods and hide which of them mutates.
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

    /// <summary>Forgets every command and every claimed idempotency key.</summary>
    /// <remarks>
    /// Called when the room resets. The ledger is replaced rather than pruned so a key claimed
    /// against the discarded world cannot suppress the same command against the new one — after
    /// a reset the assets it referred to no longer exist, and reporting the old result would be
    /// a lie about a vehicle that is gone.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _results.Clear();
            _insertionOrder.Clear();
            _ledger = new CommandIdempotencyLedger(IdempotencyRetention);
        }
    }
}
