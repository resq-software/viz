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

using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// A fault's raised instant must be when it started, not when it was last observed.
/// </summary>
/// <remarks>
/// Every domain's health builder stamps its faults with the capture's source time, because at the
/// moment it builds one it has no memory of whether the same condition was already up. Left
/// there, a fault standing for ten minutes reports an age of zero every time anybody looks, so
/// "how long has this been going on" — the first question an operator asks about an advisory —
/// is unanswerable from the telemetry. The secondary cost is that a re-stamped timestamp makes
/// an otherwise-unchanged asset look different on every frame, which is exactly what the delta
/// stream's unchanged-asset channel exists to avoid.
/// </remarks>
public sealed class FaultOnsetLedgerTests
{
    private static readonly DateTimeOffset T0 = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    private static HealthState Warning(DateTimeOffset at, params string[] codes) =>
        new(
            ComponentHealthStatus.Warning,
            [],
            [.. codes.Select(c => new FaultCode(c, FaultSeverity.Warning, "sub", "msg", at))],
            "Warning.");

    private static HealthState Nominal() =>
        new(ComponentHealthStatus.Nominal, [], [], "Nominal.");

    /// <summary>A fault that stays up keeps the instant it first appeared.</summary>
    [Fact]
    public void StandingFault_KeepsItsOriginalInstant()
    {
        var ledger = new FaultOnsetLedger();

        ledger.Stamp(Warning(T0, "BATTERY_LOW"), T0);
        var later = ledger.Stamp(
            Warning(T0.AddSeconds(30), "BATTERY_LOW"), T0.AddSeconds(30));

        later.Faults.Single().RaisedAt.Should().Be(
            T0, "the condition started at T0 and has not cleared since");
    }

    /// <summary>Successive captures of a standing fault are equal, so a differ can elide them.</summary>
    [Fact]
    public void StandingFault_ProducesEqualHealthStatesAcrossCaptures()
    {
        var ledger = new FaultOnsetLedger();

        var first = ledger.Stamp(Warning(T0, "HULL_AGROUND"), T0);
        var second = ledger.Stamp(Warning(T0.AddSeconds(1), "HULL_AGROUND"), T0.AddSeconds(1));

        second.Faults.Single().Should().Be(first.Faults.Single());
    }

    /// <summary>A fault that clears and returns is a new occurrence with a new instant.</summary>
    [Fact]
    public void ClearedThenReraisedFault_GetsAFreshInstant()
    {
        var ledger = new FaultOnsetLedger();

        ledger.Stamp(Warning(T0, "ROLLOVER_RISK"), T0);
        ledger.Stamp(Nominal(), T0.AddSeconds(10));
        var again = ledger.Stamp(
            Warning(T0.AddSeconds(20), "ROLLOVER_RISK"), T0.AddSeconds(20));

        again.Faults.Single().RaisedAt.Should().Be(
            T0.AddSeconds(20), "the condition cleared, so this is a separate occurrence");
    }

    /// <summary>Each code is tracked on its own, not as one block.</summary>
    [Fact]
    public void ConcurrentFaults_TrackTheirOwnOnsets()
    {
        var ledger = new FaultOnsetLedger();

        ledger.Stamp(Warning(T0, "BATTERY_LOW"), T0);
        var both = ledger.Stamp(
            Warning(T0.AddSeconds(5), "BATTERY_LOW", "UNDER_KEEL_CLEARANCE_LOW"),
            T0.AddSeconds(5));

        both.Faults.Single(f => f.Code == "BATTERY_LOW").RaisedAt.Should().Be(T0);
        both.Faults.Single(f => f.Code == "UNDER_KEEL_CLEARANCE_LOW").RaisedAt
            .Should().Be(T0.AddSeconds(5), "this one only just appeared");
    }

    /// <summary>A fault-free health state is passed straight through.</summary>
    [Fact]
    public void NominalHealth_IsReturnedUnchanged()
    {
        var ledger = new FaultOnsetLedger();
        var nominal = Nominal();

        ledger.Stamp(nominal, T0).Should().BeSameAs(
            nominal, "there is nothing to rewrite and nothing to allocate");
    }

    /// <summary>Everything but the instant survives the rewrite untouched.</summary>
    [Fact]
    public void Stamp_PreservesEveryOtherFaultField()
    {
        var ledger = new FaultOnsetLedger();
        var original = new FaultCode(
            "MOBILITY_IMMOBILISED", FaultSeverity.Error, "mobility.drivetrain",
            "Advisory: the ground will not carry the vehicle.", T0, IsLatched: true);

        ledger.Stamp(new HealthState(ComponentHealthStatus.Warning, [], [original], "s"), T0);
        var second = ledger.Stamp(
            new HealthState(
                ComponentHealthStatus.Warning, [], [original with { RaisedAt = T0.AddMinutes(1) }],
                "s"),
            T0.AddMinutes(1));

        second.Faults.Single().Should().Be(original);
    }
}
