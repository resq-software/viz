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
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The pack every asset carries: what it accounts for, and what it deliberately does not.
/// </summary>
/// <remarks>
/// It accounts; it does not model. The draw is the domain's physics — a rover's is a tractive
/// load, a vessel's a cube law about rated propulsion at speed through water — and folding
/// either in here is how a vessel ends up billed for speed over ground. These cases pin the
/// accounting and the projection, and nothing about how the draw was arrived at.
/// </remarks>
public sealed class AssetBatteryTests
{
    private const double Hour = 3600.0;

    /// <summary>A pack starts full.</summary>
    [Fact]
    public void A_New_Pack_Is_Full()
    {
        var battery = new AssetBattery(capacityWh: 100.0, idleDrawWatts: 45.0);

        battery.PercentRemaining.Should().Be(100.0);
        battery.StoredWh.Should().Be(100.0);
    }

    /// <summary>It publishes an idle draw before it has ever been stepped.</summary>
    /// <remarks>
    /// Supplied rather than defaulted, and the two domains disagree about it on purpose — a
    /// rover idles at 45 W and a vessel's hotel load is 60. Defaulting it to zero would be
    /// silent: the determinism hashes append only the remaining percentage, not the draw or the
    /// endurance, so a first-frame change passes every existing test.
    /// </remarks>
    [Fact]
    public void A_Pack_Draws_Its_Idle_Load_Before_The_First_Step()
    {
        new AssetBattery(100.0, idleDrawWatts: 45.0).DrawWatts.Should().Be(45.0);
        new AssetBattery(100.0, idleDrawWatts: 60.0).DrawWatts.Should().Be(60.0);
    }

    /// <summary>Draining subtracts watt-hours, not watts.</summary>
    /// <remarks>An hour at 10 W is 10 Wh; the same draw for a second is 10/3600 of one.</remarks>
    [Fact]
    public void Draining_Accounts_In_Watt_Hours()
    {
        var battery = new AssetBattery(100.0, 0.0);

        battery.Drain(drawWatts: 10.0, deltaSeconds: Hour);

        battery.StoredWh.Should().BeApproximately(90.0, 1e-9);
        battery.PercentRemaining.Should().BeApproximately(90.0, 1e-9);
    }

    /// <summary>A flat pack stops at empty rather than going negative.</summary>
    /// <remarks>
    /// Flat is a state; a negative charge is an arithmetic artefact that would then be published
    /// as a negative percentage and rendered as one.
    /// </remarks>
    [Fact]
    public void A_Flat_Pack_Does_Not_Go_Negative()
    {
        var battery = new AssetBattery(10.0, 0.0);

        battery.Drain(drawWatts: 1000.0, deltaSeconds: Hour);

        battery.StoredWh.Should().Be(0.0);
        battery.PercentRemaining.Should().Be(0.0);
    }

    /// <summary>The last draw is what gets published, not an average.</summary>
    [Fact]
    public void The_Published_Draw_Is_The_Most_Recent_One()
    {
        var battery = new AssetBattery(1000.0, 45.0);

        battery.Drain(500.0, 1.0);
        battery.DrawWatts.Should().Be(500.0);

        battery.Drain(20.0, 1.0);
        battery.DrawWatts.Should().Be(20.0);
    }

    /// <summary>Endurance is the charge over the draw.</summary>
    [Fact]
    public void Endurance_Is_Charge_Over_Draw()
    {
        var battery = new AssetBattery(100.0, 0.0);
        battery.Drain(drawWatts: 50.0, deltaSeconds: 0.0);

        battery.ToPowerState().RemainingTime
            .Should().Be(TimeSpan.FromHours(2.0));
    }

    /// <summary>With nothing being drawn, endurance is unknown rather than enormous.</summary>
    /// <remarks>
    /// An asset that is not consuming has no meaningful time to empty. Publishing a very large
    /// number invites a client to render it as one.
    /// </remarks>
    [Fact]
    public void Nothing_Drawn_Means_No_Endurance_Rather_Than_A_Huge_One()
    {
        var battery = new AssetBattery(100.0, 0.0);
        battery.Drain(drawWatts: 0.0, deltaSeconds: 1.0);

        battery.ToPowerState().RemainingTime.Should().BeNull();
    }

    /// <summary>The projection agrees with itself across the envelope and the source.</summary>
    /// <remarks>
    /// The wire carries the same three figures twice — once on the pack and once on its one
    /// source — and a client may read either. They have to be the same numbers.
    /// </remarks>
    [Fact]
    public void The_Projection_Agrees_With_Its_Own_Source()
    {
        var battery = new AssetBattery(200.0, 0.0);
        battery.Drain(40.0, Hour);

        var power = battery.ToPowerState();
        var source = power.Sources.Should().ContainSingle().Subject;

        source.PercentRemaining.Should().Be(power.PercentRemaining);
        source.RemainingEnergyWh.Should().Be(power.RemainingEnergyWh);
        source.RemainingTime.Should().Be(power.RemainingTime);
        source.DrawWatts.Should().Be(battery.DrawWatts);
    }

    /// <summary>A pack with no capacity reports nothing rather than dividing by it.</summary>
    [Fact]
    public void A_Pack_With_No_Capacity_Reports_Zero()
    {
        new AssetBattery(0.0, 0.0).PercentRemaining.Should().Be(0.0);
    }

    /// <summary>Nonsense construction is refused.</summary>
    [Fact]
    public void A_Pack_Refuses_Negative_Or_Non_Finite_Construction()
    {
        var negative = () => new AssetBattery(-1.0, 0.0);
        var infinite = () => new AssetBattery(double.PositiveInfinity, 0.0);
        var nanDraw = () => new AssetBattery(100.0, double.NaN);

        negative.Should().Throw<ArgumentOutOfRangeException>();
        infinite.Should().Throw<ArgumentOutOfRangeException>();
        nanDraw.Should().Throw<ArgumentOutOfRangeException>();
    }
}
