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


namespace ResQ.Viz.Web.Services.Assets;

/// <summary>Which way a latched level crossed, or that it did not.</summary>
internal enum LatchEdge
{
    /// <summary>The level observed is the level already held; nothing transitioned.</summary>
    Unchanged = 0,

    /// <summary>The level went from false to true on this observation.</summary>
    Rose = 1,

    /// <summary>The level went from true to false on this observation.</summary>
    Fell = 2,
}

/// <summary>Remembers one boolean level, so a transition can be told from a repetition.</summary>
/// <remarks>
/// Every asset raises events on edges rather than levels — "one event per transition, not one
/// per tick" — and every asset implemented that by hand with a <c>_was*</c> field, an
/// <c>if (now != was)</c> and an assignment. Thirteen of them, and the invariant they encode was
/// stated only in prose. This is that invariant with a name.
/// <para>
/// A value type, and returned rather than mutated, so a caller cannot observe an edge and forget
/// to carry the level forward — the failure that produced a stale hold on this codebase's own
/// peer-separation work. It runs at 60 Hz across up to 150 assets and allocates nothing: a
/// two-byte struct assigned back over a field.
/// </para>
/// <para>
/// Deliberately narrow. It models ONE boolean with ONE threshold. It is not for the drift
/// detector, which has two thresholds and a genuine hysteresis band; not for the hull-contact
/// pair, which is one three-state detector wearing two bools; and not for the station-keep and
/// docking phase trackers, which are multi-state enum transitions. Forcing any of those through
/// this type would flatten a distinction the code makes on purpose.
/// </para>
/// </remarks>
/// <param name="IsHigh">The level carried forward from the previous observation.</param>
internal readonly record struct Latch(bool IsHigh)
{
    /// <summary>A latch holding false: the first true level it observes is a rising edge.</summary>
    /// <remarks>
    /// The spelling of "seed low on purpose". Some levels must announce themselves on the first
    /// step even when they were already true at spawn — a vehicle that autonomy cannot move, or a
    /// hull already on a beach, would otherwise sit in the asset list looking healthy.
    /// </remarks>
    public static Latch Low => default;

    /// <summary>A latch seeded from a level already in force, so that level is not a transition.</summary>
    /// <remarks>
    /// The other deliberate policy, for levels where the spawn condition is ordinary rather than
    /// an anomaly: a drone starting on the pad has not just landed, and a vehicle spawned on a
    /// slope has not just started leaning.
    /// </remarks>
    /// <param name="level">The level in force at construction.</param>
    /// <returns>A latch already holding <paramref name="level"/>.</returns>
    public static Latch SeededAt(bool level) => new(level);

    /// <summary>Returns the latch carrying <paramref name="level"/>, reporting the edge crossed.</summary>
    /// <param name="level">The level observed now.</param>
    /// <param name="edge">Set to the transition this observation made, or <see cref="LatchEdge.Unchanged"/>.</param>
    /// <returns>A latch holding <paramref name="level"/>, to be assigned back over the caller's own.</returns>
    public Latch Observe(bool level, out LatchEdge edge)
    {
        edge = level == IsHigh
            ? LatchEdge.Unchanged
            : level ? LatchEdge.Rose : LatchEdge.Fell;

        return new Latch(level);
    }
}
