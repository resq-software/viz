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


using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>
/// One asset's pack: how much it holds, how much is left, what it is drawing, and the wire
/// projection of all three.
/// </summary>
/// <remarks>
/// The accounting only. The <em>draw</em> is the domain's physics and stays there: a rover's is
/// a tractive load — rolling resistance plus the grade component of its weight, over drivetrain
/// efficiency — and a vessel's is a cube law about its rated propulsion power at speed through
/// water. Those are not the same model and must not be made one. Folding either into this type
/// is how a vessel ends up billed for speed over ground instead of speed through water, which
/// its own remarks call out as the thing that must not happen.
/// <para>
/// So the cut is at the single line the two domains genuinely shared: subtract what was drawn
/// over the step, and never below zero. The domain computes, the pack accounts, and the invariant
/// that charge is bounded by capacity lives with the thing that has a capacity.
/// </para>
/// </remarks>
public sealed class AssetBattery
{
    private readonly double _capacityWh;
    private double _storedWh;

    /// <summary>Creates a full pack.</summary>
    /// <param name="capacityWh">Usable capacity in watt-hours.</param>
    /// <param name="idleDrawWatts">
    /// What the asset draws before it has been stepped. Not zero, and supplied rather than
    /// defaulted: it is what a capture taken before the first step publishes for draw and
    /// endurance, and the two domains disagree about it on purpose — a rover idles at less than
    /// a vessel's hotel load. Defaulting it would be silent, because the determinism hashes
    /// append only the remaining percentage.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Either argument is negative or not finite.</exception>
    public AssetBattery(double capacityWh, double idleDrawWatts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityWh);
        ArgumentOutOfRangeException.ThrowIfNegative(idleDrawWatts);

        if (!double.IsFinite(capacityWh) || !double.IsFinite(idleDrawWatts))
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacityWh), "A pack's capacity and idle draw must be finite.");
        }

        _capacityWh = capacityWh;
        _storedWh = capacityWh;
        DrawWatts = idleDrawWatts;
    }

    /// <summary>What the asset drew over the most recent step, in watts.</summary>
    public double DrawWatts { get; private set; }

    /// <summary>Charge remaining, in watt-hours.</summary>
    public double StoredWh => _storedWh;

    /// <summary>Charge remaining as a percentage of capacity, clamped to 0-100.</summary>
    /// <remarks>Zero for a pack with no capacity, which is the honest answer rather than a divide.</remarks>
    public double PercentRemaining =>
        _capacityWh > 0.0 ? Math.Clamp(100.0 * _storedWh / _capacityWh, 0.0, 100.0) : 0.0;

    /// <summary>Accounts for one step at a draw the domain computed.</summary>
    /// <remarks>
    /// Floors at empty rather than going negative: a flat pack is a state, and a negative one is
    /// an arithmetic artefact that would then be published as a negative percentage.
    /// </remarks>
    /// <param name="drawWatts">What the asset is drawing over this step, in watts.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    public void Drain(double drawWatts, double deltaSeconds)
    {
        DrawWatts = drawWatts;
        _storedWh = Math.Max(0.0, _storedWh - (drawWatts * deltaSeconds / SecondsPerHour));
    }

    /// <summary>Projects the pack onto the wire model.</summary>
    /// <remarks>
    /// Endurance is null rather than infinite when nothing is being drawn. An asset that is not
    /// consuming has no meaningful time to empty, and publishing a very large number invites a
    /// client to render it as one.
    /// </remarks>
    /// <returns>The published power state.</returns>
    public PowerState ToPowerState()
    {
        TimeSpan? endurance = DrawWatts > 0.0
            ? TimeSpan.FromHours(_storedWh / DrawWatts)
            : null;

        double percent = PercentRemaining;

        return new PowerState(
            Sources:
            [
                new PowerSource(
                    SourceId: "pack-a",
                    Kind: PowerSourceKind.Battery,
                    PercentRemaining: percent,
                    RemainingEnergyWh: _storedWh,
                    RemainingTime: endurance,
                    DrawWatts: DrawWatts),
            ],
            PercentRemaining: percent,
            RemainingEnergyWh: _storedWh,
            RemainingTime: endurance);
    }

    /// <summary>Seconds in an hour, for the watt-hour accounting.</summary>
    private const double SecondsPerHour = 3600.0;
}
