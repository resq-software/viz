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

using System.Numerics;
using FluentAssertions;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Which of the two bad water conditions a vessel announces, and which it does not.</summary>
/// <remarks>
/// One suite for a single distinction, because it was lost twice and in two different files, and
/// because losing it produces a log every line of which is plausible. Being on the bed and being
/// afloat inside the advisory margin are different situations with different responses — work the
/// hull off, against slow down and stand off — and only <see cref="WaterConstraints.ContactAt"/>
/// is entitled to tell them apart.
/// <para>
/// The tempting shortcut is <see cref="WaterSample.IsNavigable"/>, because a vessel that is
/// aground is certainly not in navigable water. The converse is what fails. The mask is a
/// <em>planning</em> verdict cut at draft plus the advisory margin, and it refuses a prohibited
/// zone outright, so <c>!IsNavigable</c> is true for a hull afloat with water under its keel and
/// for one merely turned back at the edge of a no-go area in any depth at all. Deriving the
/// grounding from it overclaims in the direction that matters, and — because the mask already
/// refuses every clearance the unsafe band covers — it also makes the unsafe-clearance arm
/// unreachable, so the one level with time left in it is never announced.
/// </para>
/// <para>
/// Everything here is a literal. A level basin of settable depth, no current, no wind and no
/// command, so the only thing that ever changes is the water column and every event raised is
/// attributable to it. Nothing reads a clock or sleeps, and every run is a fixed step count.
/// </para>
/// </remarks>
public sealed class SurfaceClearanceEventTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed for every generator in this suite, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Identifier every vessel in this suite is spawned with.</summary>
    private const string RigId = "usv-ukc-1";

    /// <summary>Steps a settled condition is held for before its event log is counted.</summary>
    /// <remarks>
    /// Five seconds at 60 Hz. The events under test are edges, so the difference between the
    /// right answer and a level-triggered one here is one event against three hundred.
    /// </remarks>
    private const int SettleSteps = 300;

    /// <summary>Steps taken after moving the bed, to observe one crossing and no repeat of it.</summary>
    /// <remarks>
    /// Two: one for the transition and one to prove it was a transition. The bed is level, the
    /// water is still and nothing is commanded, so a crossing that has not happened by the second
    /// step is not going to.
    /// </remarks>
    private const int BandSteps = 2;

    /// <summary>Water column, in metres, that leaves the hull afloat and inside its margin.</summary>
    /// <remarks>
    /// The shipped workboat draws 0.55 m and wants 0.305 m under it, so a 0.70 m column leaves
    /// 0.15 m of water under the keel: genuinely afloat, genuinely short of the margin. This is
    /// the case the conflation got loudest about — it announced a grounding, at alert severity,
    /// for a vessel under way and answering the helm.
    /// </remarks>
    private const double InsideMarginDepthM = 0.70;

    /// <summary>Water column, in metres, shallower than the hull's draft.</summary>
    private const double OnTheBedDepthM = 0.40;

    /// <summary>Water column, in metres, deep enough that no clearance band is in play.</summary>
    /// <remarks>Eight metres under a half-metre draft: nothing here is about the bed.</remarks>
    private const double DeepDepthM = 8.0;

    /// <summary>Offset either side of a band edge, in metres.</summary>
    /// <remarks>
    /// A millimetre: far enough above double-precision error at these magnitudes to be an
    /// unambiguous side of the boundary, and small enough that a classifier which had drifted
    /// even slightly would land on the wrong side of it. The exact-equality behaviour of the
    /// bands themselves belongs to the pure functions and is pinned in <c>SurfaceClearanceTests</c>;
    /// what this suite pins is which event each side raises.
    /// </remarks>
    private const double BoundaryOffsetM = 0.001;

    /// <summary>Heading due north, in radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Tolerance in metres for a clearance recomputed from the same two literals.</summary>
    private const double ClearanceToleranceM = 1e-9;

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Frozen receive-time stamp, so a capture is a function of its inputs alone.</summary>
    private static readonly DateTimeOffset WallClockUtc = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    /// <summary>Hull every case in this suite floats: the shipped workboat.</summary>
    private static readonly SurfaceProfile Hull = SurfaceProfile.SurfaceVessel;

    /// <summary>Advisory margin the shipped workboat's draft implies, in metres.</summary>
    private static readonly double SafeMarginM = UnderKeelClearance.SafeMarginForDraft(Hull.DraftM);

    /// <summary>Every code the clearance conditions can raise, so a filter cannot miss one.</summary>
    private static readonly string[] ClearanceCodes =
    [
        UnderKeelClearance.AgroundCode,
        UnderKeelClearance.UnsafeClearanceCode,
        UnderKeelClearance.ClearanceRestoredCode,
    ];

    /// <summary>A no-go area covering the whole basin, prohibiting entry at any depth.</summary>
    private static readonly EnvironmentZone[] ProhibitedZone =
        [new EnvironmentZone("nogo-1", "restricted", IsEntryProhibited: true)];

    // ─── Afloat inside the margin is a warning, not a grounding ─────────────

    /// <summary>
    /// A vessel afloat with less water than its margin announces the unsafe clearance, once, and
    /// never announces a grounding.
    /// </summary>
    /// <remarks>
    /// The case the whole suite exists for, and the one there was no coverage of at all: with the
    /// grounding derived from the navigable-water mask this vessel published
    /// <see cref="UnderKeelClearance.AgroundCode"/> at <see cref="AssetEventSeverity.Alert"/>
    /// while floating with 0.15 m under its keel, and
    /// <see cref="UnderKeelClearance.UnsafeClearanceCode"/> was unreachable dead code. The
    /// severity is asserted as well as the code, because the severity is what an operator triages
    /// on before they have read a word of the message.
    /// </remarks>
    [Fact]
    public void A_Vessel_Afloat_Inside_Its_Margin_Announces_The_Unsafe_Clearance_And_No_Grounding()
    {
        var rig = new Rig(new LevelBasin(InsideMarginDepthM));

        rig.Run(SettleSteps);

        var surface = rig.SurfaceState();
        surface.UnderKeelClearanceM.Should().BeApproximately(
            InsideMarginDepthM - Hull.DraftM,
            ClearanceToleranceM,
            "this case only means anything if the hull really is floating");
        surface.UnderKeelClearanceM.Should().BeLessThan(
            SafeMarginM, "and really is short of the margin it wants");

        var raised = rig.ClearanceLog();

        raised.Should().ContainSingle(
            $"{SettleSteps} ticks inside the margin is one transition, not {SettleSteps} of them")
            .Which.Code.Should().Be(UnderKeelClearance.UnsafeClearanceCode);

        raised[0].Severity.Should().Be(
            AssetEventSeverity.Warning,
            "a vessel that is under way and answering has not had a casualty");

        raised[0].Message.Should().NotContain(
            "bed", "nothing has touched the bottom, and the prose must not say it has");

        rig.Log.Should().NotContain(
            e => e.Code == UnderKeelClearance.AgroundCode,
            "the hull is afloat; a grounding announced here is an overclaim an operator acts on");
    }

    // ─── On the bed is a grounding, and still only one ──────────────────────

    /// <summary>A vessel in water shallower than its draft announces the grounding, once.</summary>
    /// <remarks>
    /// The other side of the same distinction. Separating the two conditions must not cost the
    /// grounding: a hull carrying its weight on the bottom is exactly what
    /// <see cref="UnderKeelClearance.AgroundCode"/> is for, and it is still an alert.
    /// </remarks>
    [Fact]
    public void A_Vessel_On_The_Bed_Announces_The_Grounding_And_No_Unsafe_Clearance()
    {
        var rig = new Rig(new LevelBasin(OnTheBedDepthM));

        rig.Run(SettleSteps);

        rig.SurfaceState().UnderKeelClearanceM.Should().BeNegative("the hull is into the bed");

        var raised = rig.ClearanceLog();

        raised.Should().ContainSingle(
            $"{SettleSteps} ticks aground is one transition, not {SettleSteps} of them")
            .Which.Code.Should().Be(UnderKeelClearance.AgroundCode);

        raised[0].Severity.Should().Be(AssetEventSeverity.Alert);

        rig.Log.Should().NotContain(
            e => e.Code == UnderKeelClearance.UnsafeClearanceCode,
            "a hull on the bottom has passed the margin warning, not stopped at it");
    }

    // ─── A refusal that is not about the bed announces neither ──────────────

    /// <summary>
    /// A vessel refused by a prohibited zone in deep water announces neither condition, however
    /// firmly the water mask refuses it.
    /// </summary>
    /// <remarks>
    /// The refusal here has nothing to do with the bed: there are eight metres of water under a
    /// half-metre draft, and the vessel is turned back because an operator declared the area
    /// off-limits. Deriving grounding from the mask put
    /// <see cref="UnderKeelClearance.AgroundCode"/> in the log for exactly this vessel, which is
    /// how a no-go area came to read as a casualty. The zone is published on
    /// <see cref="SurfaceDomainState.IsInsideWaterMask"/>, where a refusal belongs.
    /// </remarks>
    [Fact]
    public void A_Vessel_Refused_By_A_Prohibited_Zone_In_Deep_Water_Announces_Neither()
    {
        var rig = new Rig(new LevelBasin(DeepDepthM, ProhibitedZone));

        rig.Run(SettleSteps);

        var surface = rig.SurfaceState();

        surface.IsInsideWaterMask.Should().BeFalse(
            "the zone really does refuse this point, or the case proves nothing");
        surface.UnderKeelClearanceM.Should().BeGreaterThan(
            SafeMarginM, "and it is refused with the whole advisory margin in hand");
        surface.HasUnsafeUnderKeelClearance.Should().BeFalse();

        rig.ClearanceLog().Should().BeEmpty(
            "a decision about where a vessel may go is not a claim about what its hull is doing");
    }

    // ─── Both boundaries, crossed in both directions ────────────────────────

    /// <summary>
    /// Walking the water column across both band edges and back announces each transition once,
    /// in the right order, and never announces a restoration for a hull still short of its margin.
    /// </summary>
    /// <remarks>
    /// One run rather than four, because the ordering is the contract: coming off the bed into
    /// water that is still inside the margin is <em>not</em> a restoration, and a vessel told
    /// otherwise reads it as permission to carry on. Each leg moves the bed by a millimetre or
    /// by a band, so every crossing is unambiguous and every event in the log is attributable to
    /// the leg that caused it.
    /// <para>
    /// The environment-changed advisory is filtered out here on purpose: it is a different
    /// contract, pinned elsewhere, and this case is about which clearance transition each side of
    /// each edge produces.
    /// </para>
    /// </remarks>
    [Fact]
    public void Both_Clearance_Boundaries_Are_Announced_From_Both_Sides()
    {
        double afloatOfTheBed = Hull.DraftM + BoundaryOffsetM;
        double insideTheMargin = Hull.DraftM + SafeMarginM - BoundaryOffsetM;
        double clearOfTheMargin = Hull.DraftM + SafeMarginM + BoundaryOffsetM;

        const string Aground = UnderKeelClearance.AgroundCode;
        const string Unsafe = UnderKeelClearance.UnsafeClearanceCode;
        const string Restored = UnderKeelClearance.ClearanceRestoredCode;

        var basin = new LevelBasin(Hull.DraftM - BoundaryOffsetM);
        var rig = new Rig(basin);

        rig.Run(BandSteps);
        rig.ClearanceCodesRaised().Should().Equal(
            new[] { Aground },
            "a millimetre short of floating is on the bed");

        basin.DepthM = afloatOfTheBed;
        rig.Run(BandSteps);
        rig.ClearanceCodesRaised().Should().Equal(
            new[] { Aground, Unsafe },
            "a millimetre of water under the keel floats the hull and leaves it inside its "
            + "margin, which is the warning rather than a restoration");

        basin.DepthM = clearOfTheMargin;
        rig.Run(BandSteps);
        rig.ClearanceCodesRaised().Should().Equal(
            new[] { Aground, Unsafe, Restored },
            "a millimetre past the margin is the first point at which anything is restored");

        basin.DepthM = insideTheMargin;
        rig.Run(BandSteps);
        rig.ClearanceCodesRaised().Should().Equal(
            new[] { Aground, Unsafe, Restored, Unsafe },
            "a millimetre back inside the margin is the warning again, coming the other way");

        basin.DepthM = OnTheBedDepthM;
        rig.Run(BandSteps);
        rig.ClearanceCodesRaised().Should().Equal(
            new[] { Aground, Unsafe, Restored, Unsafe, Aground },
            "settling onto the bed is the grounding again, and five crossings are five events");
    }

    /// <summary>One vessel on a level basin, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what the world does per step — sample the environment at the asset's pre-step
    /// position, build a context, step the asset — without a world, so a case can be stated in
    /// literals. Every step drains into <see cref="Log"/>, because the asset's queue is
    /// deliberately bounded and a long run would otherwise start dropping the transitions these
    /// cases count.
    /// </remarks>
    private sealed class Rig
    {
        private readonly Random _random = new(FixedSeed);
        private readonly LevelBasin _basin;

        /// <summary>Floats a vessel and prepares it to be stepped.</summary>
        /// <param name="basin">Water to float on.</param>
        public Rig(LevelBasin basin)
        {
            _basin = basin;
            Asset = new SurfaceAsset(
                AssetProfiles.Create(RigId, VehicleClass.SurfaceVessel),
                SurfaceDynamics.For(Hull),
                basin,
                Vector3.Zero,
                North);
        }

        /// <summary>The vessel under test.</summary>
        public SurfaceAsset Asset { get; }

        /// <summary>Every event raised since the rig was built, in the order they were raised.</summary>
        public List<AssetEvent> Log { get; } = [];

        /// <summary>World steps taken so far.</summary>
        public long Tick { get; private set; }

        /// <summary>Advances the vessel by a fixed number of steps, draining as it goes.</summary>
        /// <param name="steps">Number of steps.</param>
        public void Run(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                var before = Asset.PositionEus;
                Tick++;

                Asset.Step(new AssetStepContext(
                    DeltaSeconds: Dt,
                    SimulationTimeSeconds: Tick * Dt,
                    Tick: Tick,
                    Environment: _basin.Sample(before, Hull.FootprintRadiusM),
                    Peers: [],
                    Random: _random));

                Log.AddRange(Asset.DrainEvents());
            }
        }

        /// <summary>Every grounding, unsafe-clearance or restoration raised so far.</summary>
        /// <returns>The clearance events, in the order they were raised.</returns>
        public IReadOnlyList<AssetEvent> ClearanceLog() =>
            Log.Where(e => ClearanceCodes.Contains(e.Code)).ToList();

        /// <summary>The clearance events reduced to their codes, for asserting on a sequence.</summary>
        /// <returns>One code per clearance event, in order.</returns>
        public IReadOnlyList<string> ClearanceCodesRaised() =>
            ClearanceLog().Select(e => e.Code).ToList();

        /// <summary>Projects the vessel onto the wire and narrows to its surface extension.</summary>
        /// <returns>The published surface-domain state.</returns>
        public SurfaceDomainState SurfaceState() => Asset
            .Capture(new AssetCaptureContext(
                Environment: _basin,
                SimulationTimeSeconds: Tick * Dt,
                Tick: Tick,
                SourceTime: WorldEpochUtc.AddSeconds(Tick * Dt),
                ReceiveTime: WallClockUtc,
                Origin: null))
            .DomainState.Should().BeOfType<SurfaceDomainState>().Subject;
    }

    /// <summary>A flat-bedded basin of settable depth, in still water and still air.</summary>
    /// <remarks>
    /// The bed is level everywhere, so depth is a constant of the basin rather than a function of
    /// position and a drifting hull cannot change the condition under test. Nothing sets and
    /// nothing blows, so the vessel does not move at all unless commanded — which means every
    /// transition in these cases is caused by <see cref="DepthM"/> and by nothing else.
    /// <para>
    /// The water surface sits on the datum so that the column really is the literal each case
    /// states: depth is the surface less the bed, and a bed at <c>-depth</c> under a surface at
    /// zero differences exactly. A basin whose datum was elsewhere would put a rounding error
    /// either side of the band edges these cases deliberately sit a millimetre from.
    /// </para>
    /// </remarks>
    private sealed class LevelBasin : IEnvironmentSampler
    {
        private readonly IReadOnlyList<EnvironmentZone> _zones;

        /// <summary>Builds a basin.</summary>
        /// <param name="depthM">Initial water column everywhere, in metres.</param>
        /// <param name="zones">Zones applying everywhere, or null for none.</param>
        public LevelBasin(double depthM, IReadOnlyList<EnvironmentZone>? zones = null)
        {
            DepthM = depthM;
            _zones = zones ?? [];
            Wind = new StillAir();
        }

        /// <summary>Water column everywhere in this basin, in metres. Settable, to move the bed.</summary>
        /// <remarks>
        /// The only thing in this suite that changes. Moving it lowers or raises the bed under a
        /// stationary hull, which is how a case crosses a clearance band without the vessel
        /// having to be driven anywhere.
        /// </remarks>
        public double DepthM { get; set; }

        /// <inheritdoc />
        public double SeaLevelM => 0.0;

        /// <inheritdoc />
        public IWindField Wind { get; }

        /// <inheritdoc />
        public double GetElevation(double x, double z) => SeaLevelM - DepthM;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) =>
            new(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: Vector3.Zero,
                TerrainElevationM: GetElevation(positionEus.X, positionEus.Z),
                TerrainNormalEus: Vector3.UnitY,
                SurfaceMaterial: SurfaceType.Water,
                WaterSurfaceElevationM: SeaLevelM,
                BathymetricElevationM: GetElevation(positionEus.X, positionEus.Z),
                Zones: _zones);
    }

    /// <summary>Still, clear air, so nothing blows a hull off the condition under test.</summary>
    private sealed class StillAir : IWindField
    {
        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }
}
