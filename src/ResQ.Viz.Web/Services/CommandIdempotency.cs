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

using System.Globalization;
using System.Numerics;
using System.Text;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>What an incoming (idempotency key, payload hash) pair turned out to be.</summary>
public enum CommandIdempotencyOutcome
{
    /// <summary>The key has not been seen. Execute the command.</summary>
    New,

    /// <summary>Same key, same payload, and the original has not reached a terminal state. Do not re-execute; report the original's progress.</summary>
    DuplicateInFlight,

    /// <summary>Same key, same payload, and the original already finished. Replay its result.</summary>
    DuplicateCompleted,

    /// <summary>Same key, materially different payload. Reject: the issuer has reused a key by mistake.</summary>
    KeyReuseConflict,
}

/// <summary>What the ledger knows about one idempotency key.</summary>
/// <param name="IdempotencyKey">Key the issuer chose.</param>
/// <param name="PayloadHash">Canonical hash of the request that first claimed the key.</param>
/// <param name="CommandId">Command minted for that first request.</param>
/// <param name="State">Latest known state of that command.</param>
/// <param name="UpdatedAt">When the entry was created or last updated; drives retention.</param>
public sealed record CommandIdempotencyRecord(
    string IdempotencyKey,
    string PayloadHash,
    Guid CommandId,
    CommandState State,
    DateTimeOffset UpdatedAt);

/// <summary>Outcome of classifying one incoming command against the ledger.</summary>
/// <param name="Outcome">What the key turned out to be.</param>
/// <param name="PayloadHash">Canonical hash computed for the incoming command.</param>
/// <param name="Existing">
/// The record already holding the key, for every outcome but <see cref="CommandIdempotencyOutcome.New"/>.
/// Carries the original command id, so a duplicate can be answered with the original's result
/// instead of a second execution.
/// </param>
public readonly record struct CommandIdempotencyDecision(
    CommandIdempotencyOutcome Outcome,
    string PayloadHash,
    CommandIdempotencyRecord? Existing);

/// <summary>Canonical payload hashing and the pure duplicate decision.</summary>
/// <remarks>
/// Split from the ledger so the interesting parts are testable with literals: the hash is a
/// total function of an envelope, and the decision is a total function of (existing record,
/// incoming hash, clock reading, retention).
/// </remarks>
public static class CommandIdempotency
{
    private const ulong Fnv1aOffsetBasis = 14695981039346656037;
    private const ulong Fnv1aPrime = 1099511628211;

    /// <summary>Bit pattern every NaN collapses to, so two NaNs never hash differently.</summary>
    private const long CanonicalNaNBits = unchecked((long)0xFFF8000000000000UL);

    /// <summary>Hashes everything about a command that makes it a <i>different request</i>.</summary>
    /// <remarks>
    /// Deliberately excludes <see cref="AssetCommandEnvelope.CommandId"/>,
    /// <see cref="AssetCommandEnvelope.IssuedAt"/>, <see cref="AssetCommandEnvelope.Deadline"/>
    /// and <see cref="AssetCommandEnvelope.ControlLeaseId"/>: a retry after a timeout is the
    /// same logical request with a new attempt id, a later timestamp and often a pushed-out
    /// deadline, and hashing any of those would classify every retry as a fresh command —
    /// exactly the double-execution idempotency exists to prevent.
    /// <para>
    /// Includes <see cref="AssetCommandEnvelope.IssuerId"/>, so two operators who happen to
    /// choose the same key collide loudly rather than silently sharing a command.
    /// </para>
    /// <para>
    /// Doubles and floats are hashed as raw bit patterns with NaN and negative zero
    /// canonicalised, and a quaternion is negated when its scalar part is negative so that
    /// <c>q</c> and <c>-q</c> — the same rotation — produce the same hash. Parameters are
    /// ordered by ordinal key, because dictionary enumeration order is not a contract.
    /// </para>
    /// </remarks>
    /// <param name="envelope">Command to hash.</param>
    /// <returns>A 16-character lowercase hexadecimal FNV-1a 64 digest.</returns>
    public static string ComputePayloadHash(AssetCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var canonical = new StringBuilder();
        canonical.Append(envelope.Kind).Append('|')
            .Append(envelope.AssetId).Append('|')
            .Append(envelope.IssuerId).Append('|')
            .Append(((int?)envelope.Frame)?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|');

        AppendTarget(canonical, envelope.Target);
        AppendConstraints(canonical, envelope.Constraints);

        if (envelope.Parameters is { Count: > 0 } parameters)
        {
            foreach (var key in parameters.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                canonical.Append(key).Append('=').Append(parameters[key]).Append('|');
            }
        }

        return Fnv1a(canonical.ToString());
    }

    /// <summary>Classifies an incoming request against whatever already holds its key.</summary>
    /// <remarks>
    /// Pure: no clock of its own, no store, no mutation. An entry older than
    /// <paramref name="retention"/> is treated as never seen, which is what stops a key from
    /// being un-reusable forever — the window only has to outlive a client's retry budget.
    /// </remarks>
    /// <param name="existing">Record currently holding the key, or null if none.</param>
    /// <param name="payloadHash">Hash of the incoming command from <see cref="ComputePayloadHash"/>.</param>
    /// <param name="nowUtc">Current instant.</param>
    /// <param name="retention">How long a completed command stays replayable.</param>
    /// <returns>The outcome for this request.</returns>
    public static CommandIdempotencyOutcome Decide(
        CommandIdempotencyRecord? existing,
        string payloadHash,
        DateTimeOffset nowUtc,
        TimeSpan retention)
    {
        if (existing is null || nowUtc - existing.UpdatedAt > retention)
        {
            return CommandIdempotencyOutcome.New;
        }

        if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return CommandIdempotencyOutcome.KeyReuseConflict;
        }

        return IsTerminal(existing.State)
            ? CommandIdempotencyOutcome.DuplicateCompleted
            : CommandIdempotencyOutcome.DuplicateInFlight;
    }

    private static bool IsTerminal(CommandState state) =>
        state is CommandState.Rejected or CommandState.Succeeded or CommandState.Failed
            or CommandState.Cancelled or CommandState.TimedOut;

    private static void AppendTarget(StringBuilder builder, CommandTarget? target)
    {
        switch (target)
        {
            case null:
                builder.Append("t-|");
                break;
            case PointCommandTarget p:
                builder.Append("t-point|").Append((int)p.Point.Frame).Append('|')
                    .Append(p.Point.OriginId ?? "-").Append('|');
                AppendVector(builder, p.Point.Position);
                AppendQuaternion(builder, p.Point.Orientation);
                AppendDouble(builder, p.AcceptanceRadiusM);
                break;
            case GeoCommandTarget g:
                builder.Append("t-geo|").Append((int)g.Position.VerticalReference).Append('|');
                AppendDouble(builder, g.Position.LatitudeDeg);
                AppendDouble(builder, g.Position.LongitudeDeg);
                AppendDouble(builder, g.Position.VerticalMeters);
                AppendDouble(builder, g.AcceptanceRadiusM);
                break;
            case AssetCommandTarget a:
                builder.Append("t-asset|").Append(a.AssetId).Append('|');
                AppendDouble(builder, a.StandoffM);
                break;
            case RouteCommandTarget r:
                builder.Append("t-route|").Append(r.RouteId).Append('|')
                    .Append(r.StartWaypointIndex?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|');
                break;
            default:
                builder.Append("t-?|").Append(target.Kind).Append('|');
                break;
        }
    }

    private static void AppendConstraints(StringBuilder builder, MotionConstraints? constraints)
    {
        if (constraints is null)
        {
            builder.Append("c-|");
            return;
        }

        builder.Append("c|").Append(constraints.CanStationKeep ? '1' : '0').Append('|');
        AppendDouble(builder, constraints.MinSpeedMps);
        AppendDouble(builder, constraints.MaxSpeedMps);
        AppendDouble(builder, constraints.MinTurnRadiusM);
        AppendDouble(builder, constraints.PassiveDriftMps);
        AppendDouble(builder, constraints.StationKeepCostW);
    }

    private static void AppendVector(StringBuilder builder, Vector3 v)
    {
        AppendDouble(builder, v.X);
        AppendDouble(builder, v.Y);
        AppendDouble(builder, v.Z);
    }

    // q and -q are the same rotation, so the sign is normalised before hashing. Comparing
    // rotations component-wise without this is the classic way to get two identical commands
    // that hash differently.
    private static void AppendQuaternion(StringBuilder builder, Quaternion q)
    {
        var canonical = q.W < 0 ? new Quaternion(-q.X, -q.Y, -q.Z, -q.W) : q;
        AppendDouble(builder, canonical.X);
        AppendDouble(builder, canonical.Y);
        AppendDouble(builder, canonical.Z);
        AppendDouble(builder, canonical.W);
    }

    private static void AppendDouble(StringBuilder builder, double? value)
    {
        if (value is not { } v)
        {
            builder.Append("-|");
            return;
        }

        // Zero is written as +0 so that -0.0 and 0.0 — equal numbers — hash the same.
        var normalised = v == 0 ? 0d : v;
        var bits = double.IsNaN(normalised) ? CanonicalNaNBits : BitConverter.DoubleToInt64Bits(normalised);
        builder.Append(bits.ToString("x16", CultureInfo.InvariantCulture)).Append('|');
    }

    private static string Fnv1a(string canonical)
    {
        var hash = Fnv1aOffsetBasis;
        unchecked
        {
            foreach (var ch in canonical)
            {
                hash = (hash ^ (byte)(ch & 0xFF)) * Fnv1aPrime;
                hash = (hash ^ (byte)(ch >> 8)) * Fnv1aPrime;
            }
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Remembers which idempotency keys have been claimed recently, so a retried command is
/// answered rather than executed twice.
/// </summary>
/// <remarks>
/// One instance per session: keys are issuer-chosen and only unique within the scope that
/// hands them out, so a process-wide ledger would let one room's key collide with another's.
/// <para>
/// Deliberately bounded in both time and size. <see cref="Retention"/> only has to outlive a
/// client's retry budget, and <see cref="Capacity"/> caps memory when a client generates keys
/// faster than they expire; the oldest entries are dropped first, which at worst turns a very
/// old duplicate back into a fresh command.
/// </para>
/// <para>
/// Instance methods take an internal lock, so the ledger is safe to share between a controller
/// and a hub. It is intentionally the only synchronised type in this layer — validation itself
/// is pure and needs none.
/// </para>
/// </remarks>
public sealed class CommandIdempotencyLedger
{
    private readonly Dictionary<string, CommandIdempotencyRecord> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates a ledger.</summary>
    /// <param name="retention">How long a key stays claimed after its last update.</param>
    /// <param name="capacity">Maximum entries retained before the oldest are evicted.</param>
    /// <exception cref="ArgumentOutOfRangeException">Retention is not positive, or capacity is below one.</exception>
    public CommandIdempotencyLedger(TimeSpan retention, int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Retention = retention;
        Capacity = capacity;
    }

    /// <summary>How long a key stays claimed after its last update.</summary>
    public TimeSpan Retention { get; }

    /// <summary>Maximum entries retained before the oldest are evicted.</summary>
    public int Capacity { get; }

    /// <summary>Classifies a command without claiming anything. Read-only.</summary>
    /// <param name="envelope">Incoming command.</param>
    /// <param name="nowUtc">Current instant.</param>
    /// <returns>The outcome, plus the record already holding the key when there is one.</returns>
    public CommandIdempotencyDecision Classify(AssetCommandEnvelope envelope, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var hash = CommandIdempotency.ComputePayloadHash(envelope);
        lock (_gate)
        {
            return Decide(envelope.IdempotencyKey, hash, nowUtc);
        }
    }

    /// <summary>
    /// Classifies a command and, when it is new, claims its key so a concurrent retry is seen
    /// as a duplicate.
    /// </summary>
    /// <remarks>
    /// Claiming is the one deliberate side effect in this layer, and it happens only on
    /// <see cref="CommandIdempotencyOutcome.New"/>: a conflicting or duplicate request leaves
    /// the ledger exactly as it found it, so rejecting one still changes nothing.
    /// </remarks>
    /// <param name="envelope">Incoming command; its key must already have passed validation.</param>
    /// <param name="nowUtc">Current instant.</param>
    /// <returns>The outcome, plus the record already holding the key when there is one.</returns>
    /// <exception cref="ArgumentException">The envelope carries no idempotency key.</exception>
    public CommandIdempotencyDecision Claim(AssetCommandEnvelope envelope, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(envelope.IdempotencyKey))
        {
            throw new ArgumentException("Command has no idempotency key.", nameof(envelope));
        }

        var hash = CommandIdempotency.ComputePayloadHash(envelope);
        lock (_gate)
        {
            var decision = Decide(envelope.IdempotencyKey, hash, nowUtc);
            if (decision.Outcome != CommandIdempotencyOutcome.New)
            {
                return decision;
            }

            Prune(nowUtc);
            _entries[envelope.IdempotencyKey] = new CommandIdempotencyRecord(
                envelope.IdempotencyKey, hash, envelope.CommandId, CommandState.Requested, nowUtc);
            return decision;
        }
    }

    /// <summary>Records how a claimed command turned out, so duplicates can replay it.</summary>
    /// <remarks>No-op for an unknown or already-evicted key: a late update must not resurrect a claim.</remarks>
    /// <param name="idempotencyKey">Key the command claimed.</param>
    /// <param name="state">Latest state of that command.</param>
    /// <param name="nowUtc">Current instant; restarts the retention window.</param>
    public void Update(string idempotencyKey, CommandState state, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(idempotencyKey, out var existing))
            {
                _entries[idempotencyKey] = existing with { State = state, UpdatedAt = nowUtc };
            }
        }
    }

    private CommandIdempotencyDecision Decide(string key, string hash, DateTimeOffset nowUtc)
    {
        _entries.TryGetValue(key, out var existing);
        var outcome = CommandIdempotency.Decide(existing, hash, nowUtc, Retention);
        return new CommandIdempotencyDecision(
            outcome, hash, outcome == CommandIdempotencyOutcome.New ? null : existing);
    }

    private void Prune(DateTimeOffset nowUtc)
    {
        foreach (var key in _entries.Where(e => nowUtc - e.Value.UpdatedAt > Retention).Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }

        while (_entries.Count >= Capacity)
        {
            var oldest = _entries.MinBy(e => e.Value.UpdatedAt).Key;
            _entries.Remove(oldest);
        }
    }
}
