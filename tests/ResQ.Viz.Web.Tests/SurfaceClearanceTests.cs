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

using System.Numerics;
using FluentAssertions;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The line between a hull sitting on the bed and a hull floating inside its advisory margin,
/// and the insistence that a report about one is never a report about the other.
/// </summary>
/// <remarks>
/// This suite exists because the two were once the same bit. Health was worded from the
/// navigable-water mask — <c>aground = !IsNavigable</c> — and that mask is cut at draft
/// <em>plus</em> the advisory margin, so a vessel afloat, under way and merely wanting a little
/// more water under it was published as <c>HULL_AGROUND</c>; the shallow-water branch beside it
/// could not be reached at all. The two call for different responses — work the hull off the
/// ground, versus slow down and stand off into deeper water — so publishing the graver one for
/// the lesser overclaims in exactly the direction an operator acts on.
/// <para>
/// Every case runs over a level analytic bed at a stated depth, against the shipped workboat's
/// draft, so each band is arithmetic rather than observation. Nothing here reads a clock, sleeps,
/// or samples a procedural height field, and the pure-function cases feed clearances in directly
/// so both thresholds can be straddled exactly rather than nearly.
/// </para>
/// <para>
/// Advisory throughout, as everything about this bed is. Nothing here asserts that a passage is
/// safe, and nothing claims conformance with any navigation regulation.
/// </para>
/// </remarks>
public sealed class SurfaceClearanceTests
{
    /// <summary>Fixed integration timestep in seconds. Matches the world's 60 Hz asset pass.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed the rig's generator draws from, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Water-surface elevation of every basin here, in metres.</summary>
    /// <remarks>
    /// Zero, so a bed elevation is a depth negated and every depth this suite names round-trips
    /// through the sampler bit for bit. At any other level the two boundary cases would be
    /// decided by a rounding error rather than by the side of the threshold they were written for.
    /// </remarks>
    private const double SeaLevelM = 0.0;

    /// <summary>Water column, in metres, that leaves the hull afloat but well inside its margin.</summary>
    /// <remarks>
    /// 0.70 m against a 0.55 m draft: 0.15 m under the keel — positive, so the hull is floating —
    /// and comfortably inside the 0.305 m the profile wants kept. The band this suite is about.
    /// </remarks>
    private const double InsideMarginDepthM = 0.70;

    /// <summary>Water column, in metres, shallower than the hull's draft. The hull is on the bed.</summary>
    private const double OnTheBedDepthM = 0.40;

    /// <summary>How far either side of a threshold the boundary cases are staged, in metres.</summary>
    /// <remarks>
    /// A centimetre: large enough that no case is decided by a rounding error in the sampler,
    /// small enough that both cases are unmistakably about the same threshold.
    /// </remarks>
    private const double BoundaryOffsetM = 0.01;

    /// <summary>Steps a vessel is driven for before its speed is read.</summary>
    /// <remarks>
    /// Fifty seconds against a surge time constant of a few seconds, so the hull has settled onto
    /// whatever ceiling the water permits. A speed read mid-acceleration would pass against a
    /// vessel that was merely still on its way to full speed and never derated at all.
    /// </remarks>
    private const int SettlingSteps = 3000;

    /// <summary>Tolerance in metres per second on a speed both sides derive from the same constants.</summary>
    private const double SpeedToleranceMps = 0.02;

    /// <summary>Heading due west, in radians clockwise from true north.</summary>
    private const double West = 3.0 * Math.PI / 2.0;

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Frozen receive-time stamp, so a capture is a function of its inputs alone.</summary>
    private static readonly DateTimeOffset WallClockUtc = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    /// <summary>The hull every case is measured against.</summary>
    private static SurfaceProfile Profile => SurfaceProfile.SurfaceVessel;

    /// <summary>The water-relevant projection of <see cref="Profile"/>.</summary>
    private static VesselWaterProfile WaterProfile => VesselWaterProfile.From(Profile);

    /// <summary>The advisory safe under-keel margin <see cref="Profile"/> keeps, in metres.</summary>
    private static double SafeMarginM => UnderKeelClearance.SafeMarginForDraft(Profile.DraftM);

    // ─── The bands themselves ───────────────────────────────────────────────

    /// <summary>
    /// Zero clearance is the bed and anything above it is afloat; the advisory margin is a
    /// second, separate threshold higher up.
    /// </summary>
    /// <remarks>
    /// Both thresholds straddled exactly, which is only possible because
    /// <see cref="UnderKeelClearance.Classify"/> takes a clearance rather than a position: the
    /// comparison is against the literals below with no sampler standing in between. A single
    /// "unsafe" bit spanning both bands is what let a report about the upper one be printed as a
    /// report about the lower.
    /// </remarks>
    [Fact]
    public void The_Two_Thresholds_Are_Straddled_Exactly_And_Mean_Different_Things()
    {
        double margin = SafeMarginM;
        margin.Should().BeGreaterThan(0.0, "a zero margin would collapse the two into one");

        Contact(0.0, margin).Should().Be(
            HullContactState.OnTheBed, "no clearance at all is the hull resting on the bed");
        Contact(-0.5, margin).Should().Be(HullContactState.OnTheBed, "half a metre into the bed");

        // BitIncrement and BitDecrement rather than a hand-picked epsilon: they name the adjacent
        // representable value, so these two cases sit against the threshold with nothing between
        // them and it. Subtracting a literal epsilon from the margin would round back onto it.
        Contact(Math.BitIncrement(0.0), margin).Should().Be(
            HullContactState.InsideSafetyMargin,
            "the very first sliver of water under the keel is a hull that is floating");

        Contact(Math.BitDecrement(margin), margin).Should().Be(
            HullContactState.InsideSafetyMargin, "just short of the margin is still short of it");

        Contact(margin, margin).Should().Be(
            HullContactState.Afloat,
            "the margin is the figure the hull wants kept, and keeping it exactly is keeping it");

        Contact(margin * UnderKeelClearance.CautionaryMarginMultiple, margin).Should().Be(
            HullContactState.Afloat, "the cautionary band advises; it does not give up the margin");

        UnderKeelClearance.ContactFor(UnderKeelClearanceClass.Unknown).Should().Be(
            HullContactState.Unknown, "an unanswerable depth claims nothing in either direction");
    }

    /// <summary>
    /// The navigable-water mask refusing a point is a routing verdict, not a claim that the hull
    /// has touched anything.
    /// </summary>
    /// <remarks>
    /// The three notions that were once one bit, pinned apart on a single sample: the mask says
    /// "do not plan to be here", the clearance band says "you are floating with less than you
    /// want", and only the second may say anything about the bed. Water inside the margin is
    /// refused precisely because the mask is cut at draft plus margin — that conservatism is
    /// deliberate, and it is exactly why it cannot be read back as a grounding.
    /// </remarks>
    [Fact]
    public void A_Refused_Point_Is_Not_A_Claim_That_The_Hull_Is_On_The_Bed()
    {
        var sample = WaterConstraints.Evaluate(WaterProfile, SampleAt(InsideMarginDepthM));

        sample.IsNavigable.Should().BeFalse("the mask is cut at draft plus the advisory margin");
        sample.Class.Should().Be(WaterNavigability.Blocked);
        sample.Reason.Should().Be(
            WaterBlockReason.InsufficientDepth, "shallower than draft plus margin, not than draft");

        sample.Clearance.ClearanceM.Should().BePositive("the hull is floating, not sitting down");
        sample.Clearance.Class.Should().Be(UnderKeelClearanceClass.Critical);
        sample.Clearance.IsAground.Should().BeFalse();

        WaterConstraints.ContactAt(sample).Should().Be(
            HullContactState.InsideSafetyMargin,
            "the physical question — what is this hull doing about the bed — has a different "
            + "answer from the planning question, and reading one off the other is the defect");

        // The one flag that legitimately spans both bands, and the reason it is the wrong thing
        // to word a report from: on its own it cannot tell these two situations apart.
        sample.Clearance.IsUnsafe.Should().BeTrue("the margin has been given up either way");
    }

    // ─── What the vessel reports ────────────────────────────────────────────

    /// <summary>A vessel afloat inside its margin is a clearance warning, never a grounding.</summary>
    /// <remarks>
    /// The regression this suite was written for. Before the fix the branch for this situation
    /// was unreachable — <c>aground</c> was <c>!IsNavigable</c> and the shallow-water branch was
    /// gated on <c>!aground</c> — so this vessel, floating with 0.15 m under the keel, was
    /// published as <c>HULL_AGROUND</c> at critical severity and summarised as "Aground.".
    /// </remarks>
    [Fact]
    public void A_Vessel_Afloat_Inside_Its_Margin_Is_Warned_About_And_Never_Called_Aground()
    {
        var state = Rig(InsideMarginDepthM).Capture();
        var surface = Surface(state);

        surface.UnderKeelClearanceM.Should().BePositive(
            "0.70 m of water under a 0.55 m draft is a hull that is afloat");

        Codes(state).Should().Contain("UNDER_KEEL_CLEARANCE_LOW")
            .And.NotContain(
                "HULL_AGROUND",
                "the hull is floating; announcing a grounding sends an operator to recover a "
                + "vessel that needs only to stand off into deeper water");

        state.Health.Overall.Should().Be(
            ComponentHealthStatus.Warning, "a warning, not the critical status grounding carries");
        state.Health.Summary.Should().Be("Shallow water.");

        state.Health.Components.Select(component => component.Component).Should()
            .Contain("mobility.underKeel").And.NotContain("mobility.hull");

        // The three views stay coherent rather than identical: the mask still refuses this water,
        // the wire flag still says the margin is gone, and neither of those is a grounding.
        surface.IsInsideWaterMask.Should().BeFalse("the mask is cut at draft plus margin");
        surface.HasUnsafeUnderKeelClearance.Should().BeTrue();
        state.OperationalState.Should().NotBe(OperationalState.Faulted, "the water is the problem");
    }

    /// <summary>A vessel actually on the bed is reported aground, and not merely as shallow water.</summary>
    /// <remarks>
    /// The other half of the separation, and why the fix cannot simply be to stop raising the
    /// grounding: a hull with its keel in the ground has to be reported as one.
    /// </remarks>
    [Fact]
    public void A_Vessel_On_The_Bed_Is_Reported_Aground()
    {
        var state = Rig(OnTheBedDepthM).Capture();

        Surface(state).UnderKeelClearanceM.Should().BeNegative(
            "0.40 m of water under a 0.55 m draft");

        Codes(state).Should().Contain("HULL_AGROUND")
            .And.NotContain(
                "UNDER_KEEL_CLEARANCE_LOW",
                "a hull on the ground is not merely short of its margin, and raising both would "
                + "leave a display to guess which of them to show");

        state.Health.Overall.Should().Be(ComponentHealthStatus.Critical);
        state.Health.Summary.Should().Be("Aground.");

        state.OperationalState.Should().NotBe(
            OperationalState.Faulted,
            "grounding is recoverable, and a faulted state would refuse the commands that "
            + "recover it — the vessel is in perfect health and the water is the problem");
    }

    /// <summary>The threshold between the two is reported differently from each side of it.</summary>
    /// <remarks>
    /// A centimetre either side of the hull's draft, driven through the whole vessel rather than
    /// through the classifier, so the separation is shown where it is consumed. Two hulls a
    /// fingerbreadth apart in depth get the two different reports, which is the entire point.
    /// </remarks>
    /// <param name="offsetM">Signed offset from the draft, in metres.</param>
    /// <param name="expected">Fault code the vessel must publish.</param>
    /// <param name="forbidden">Fault code it must not publish.</param>
    /// <param name="summary">Summary line an operator sees.</param>
    [Theory]
    [InlineData(-BoundaryOffsetM, "HULL_AGROUND", "UNDER_KEEL_CLEARANCE_LOW", "Aground.")]
    [InlineData(BoundaryOffsetM, "UNDER_KEEL_CLEARANCE_LOW", "HULL_AGROUND", "Shallow water.")]
    public void The_Bottom_Boundary_Is_Reported_Differently_From_Each_Side(
        double offsetM, string expected, string forbidden, string summary)
    {
        var state = Rig(Profile.DraftM + offsetM).Capture();

        Codes(state).Should().Contain(expected).And.NotContain(forbidden);
        state.Health.Summary.Should().Be(summary);
    }

    /// <summary>Above the advisory margin nothing is reported and nothing is derated.</summary>
    /// <remarks>
    /// The upper threshold from both sides. A margin that produced a fault or a derate once
    /// crossed would not be an advisory at all, and the cautionary band above it exists to give
    /// early notice rather than to take anything away.
    /// </remarks>
    [Fact]
    public void Just_Clear_Of_The_Margin_Nothing_Is_Reported_And_Nothing_Is_Derated()
    {
        double keelToMargin = Profile.DraftM + SafeMarginM;

        Codes(Rig(keelToMargin - BoundaryOffsetM).Capture()).Should()
            .Contain("UNDER_KEEL_CLEARANCE_LOW", "a centimetre inside the margin is inside it");

        var outside = Rig(keelToMargin + BoundaryOffsetM).Capture();

        outside.Health.Faults.Should().BeEmpty("the margin is intact");
        outside.Health.Overall.Should().Be(ComponentHealthStatus.Nominal);
        outside.Health.Summary.Should().Be("Nominal.");
        Surface(outside).HasUnsafeUnderKeelClearance.Should().BeFalse();

        UnderKeelClearance
            .Evaluate(WaterProfile, keelToMargin + BoundaryOffsetM).SpeedFactor
            .Should().Be(1.0, "an advisory that quietly halved a speed ceiling is not an advisory");
    }

    // ─── What the vessel does about it ──────────────────────────────────────

    /// <summary>
    /// Inside the margin the vessel is slowed down — not stopped, and not moved anywhere it did
    /// not steam to.
    /// </summary>
    /// <remarks>
    /// The behavioural half of "a warning that derates speed". A hull floating inside its margin
    /// answers the helm and makes way at a derated ceiling; every step it takes is one it drove.
    /// A vessel that had instead been stopped fails on the distance made good, and one snapped
    /// back to a mask boundary fails on the largest single step — which an end-to-end distance
    /// check would average away entirely.
    /// </remarks>
    [Fact]
    public void Inside_The_Margin_The_Vessel_Is_Derated_And_Still_Makes_Way()
    {
        var rig = Rig(InsideMarginDepthM, headingRad: West);
        double ceiling = UnderKeelClearance.DerateSpeedMps(
            UnderKeelClearance.Evaluate(WaterProfile, InsideMarginDepthM), Profile.MaxSpeedMps);

        ceiling.Should().BeGreaterThan(
            rig.Asset.RecoveryCeilingMps,
            "the derate ramps through this band; the aground crawl is its floor, not its value");
        ceiling.Should().BeLessThan(Profile.MaxSpeedMps * 0.9, "and it is a real reduction");

        var helm = rig.Apply(new SimulatedAssetCommand(
            Kind: AssetCommandKind.SetCourse,
            AssetId: rig.Asset.AssetId,
            HeadingRad: West,
            SpeedMps: Profile.MaxSpeedMps));

        helm.IsAccepted.Should().BeTrue(
            "a helm order is never refused by the water the vessel is already floating in");

        var start = rig.Asset.PositionEus;
        double longestStepM = rig.RunTrackingLongestStep(SettlingSteps);

        ((double)Vector3.Distance(start, rig.Asset.PositionEus)).Should().BeGreaterThan(
            0.5 * ceiling * SettlingSteps * Dt,
            "a vessel merely warned about its clearance is under way, not stopped");

        longestStepM.Should().BeLessThan(
            (ceiling * Dt) + 1e-3,
            "every position was steamed to at the derated ceiling; a jump larger than one step's "
            + "worth of it would be the hull being placed somewhere rather than sailing there");

        var settled = Surface(rig.Capture());

        settled.SpeedOverGroundMps.Should().BeApproximately(
            ceiling, SpeedToleranceMps,
            "settled speed is the ceiling the clearance derate allows — neither the hull's full "
            + "speed, which would ignore the warning, nor zero, which would be a refusal");

        settled.UnderKeelClearanceM.Should().BePositive(
            "and it is still afloat the whole way, over a bed that never changes depth");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>The contact state a clearance and a margin imply.</summary>
    /// <param name="clearanceM">Depth less draft, in metres. May be negative.</param>
    /// <param name="marginM">Advisory margin, in metres.</param>
    /// <returns>What the hull is doing about the bed.</returns>
    private static HullContactState Contact(double clearanceM, double marginM) =>
        UnderKeelClearance.ContactFor(UnderKeelClearance.Classify(clearanceM, marginM));

    /// <summary>Samples a level basin of a given depth, at the scene origin.</summary>
    /// <param name="depthM">Water column, in metres.</param>
    /// <returns>A fully populated sample.</returns>
    private static EnvironmentSample SampleAt(double depthM) =>
        new Shelf(depthM).Sample(Vector3.Zero, Profile.FootprintRadiusM);

    /// <summary>Floats a vessel on a level basin of a given depth.</summary>
    /// <param name="depthM">Water column everywhere, in metres.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <returns>A rig holding the vessel and its tick counter.</returns>
    private static VesselRig Rig(double depthM, double headingRad = 0.0) =>
        new(new Shelf(depthM), headingRad);

    /// <summary>Narrows a captured state's domain extension to its surface form.</summary>
    /// <param name="state">State captured from a surface asset.</param>
    /// <returns>The surface-domain state.</returns>
    private static SurfaceDomainState Surface(AssetState state) =>
        state.DomainState.Should().BeOfType<SurfaceDomainState>().Subject;

    /// <summary>The fault codes a captured state publishes.</summary>
    /// <param name="state">State to read.</param>
    /// <returns>Every fault code, in the order they were raised.</returns>
    private static IEnumerable<string> Codes(AssetState state) =>
        state.Health.Faults.Select(fault => fault.Code);

    /// <summary>A basin with a level bed, so a case states its depth and nothing else varies.</summary>
    /// <remarks>
    /// Level on purpose. A shelving bed would let a vessel change bands while a case was reading
    /// one, and would put <see cref="WaterConstraints.ResolveMotion"/>'s recovery rule — which
    /// permits movement towards deeper water — between an assertion and the behaviour it is
    /// about. Here the column under the hull is the same number at every point and every step.
    /// </remarks>
    /// <param name="depthM">Water column everywhere, in metres.</param>
    private sealed class Shelf(double depthM) : IEnvironmentSampler
    {
        private readonly double _bedElevationM = SurfaceClearanceTests.SeaLevelM - depthM;

        /// <inheritdoc />
        public double SeaLevelM => SurfaceClearanceTests.SeaLevelM;

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <inheritdoc />
        public double GetElevation(double x, double z) => _bedElevationM;

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
                TerrainElevationM: _bedElevationM,
                TerrainNormalEus: Vector3.UnitY,
                SurfaceMaterial: SurfaceType.Water,
                WaterSurfaceElevationM: SeaLevelM,
                BathymetricElevationM: _bedElevationM,
                Zones: []);
    }

    /// <summary>Dead calm and perfectly clear, so nothing under test is derated by the weather.</summary>
    private sealed class StillAir : IWindField
    {
        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }

    /// <summary>One vessel on one basin, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what the asset world does per step — sample at the pre-step position with the
    /// descriptor's footprint radius, build a context, step — without a world, so a case can be
    /// stated as a depth. Both timestamps derive from a fixed epoch and the generator is seeded,
    /// so a capture is a function of its inputs alone.
    /// </remarks>
    private sealed class VesselRig
    {
        private readonly Random _random = new(FixedSeed);
        private readonly Shelf _water;

        /// <summary>Floats a vessel on a basin.</summary>
        /// <param name="water">Basin to float on and integrate over.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        public VesselRig(Shelf water, double headingRad)
        {
            _water = water;
            Descriptor = AssetProfiles.Create("usv-1", VehicleClass.SurfaceVessel);
            Asset = new SurfaceAsset(
                Descriptor, SurfaceDynamics.For(Profile), water, Vector3.Zero, headingRad);
        }

        /// <summary>The vessel under test.</summary>
        public SurfaceAsset Asset { get; }

        /// <summary>Descriptor the vessel publishes.</summary>
        public AssetDescriptor Descriptor { get; }

        /// <summary>World steps taken so far.</summary>
        public long Tick { get; private set; }

        /// <summary>Applies a validated command to the vessel.</summary>
        /// <param name="command">Command to apply.</param>
        /// <returns>Acceptance, or a rejection carrying a machine-readable reason.</returns>
        public AssetCommandResult Apply(SimulatedAssetCommand command) => Asset.Apply(command);

        /// <summary>Advances the vessel, returning the largest single-step displacement seen.</summary>
        /// <remarks>
        /// The largest step is the teleport detector. A hull that was placed rather than driven —
        /// snapped back to a mask boundary, or moved to a recomputed position — shows up as one
        /// displacement far larger than a step's worth of its speed ceiling, and a check on the
        /// distance between start and finish would average that away.
        /// </remarks>
        /// <param name="steps">Number of steps to take.</param>
        /// <returns>The largest displacement between consecutive steps, in metres.</returns>
        public double RunTrackingLongestStep(int steps)
        {
            double longest = 0.0;

            for (int i = 0; i < steps; i++)
            {
                var before = Asset.PositionEus;
                Tick++;

                Asset.Step(new AssetStepContext(
                    DeltaSeconds: Dt,
                    SimulationTimeSeconds: Tick * Dt,
                    Tick: Tick,
                    Environment: _water.Sample(before, Descriptor.Dimensions.FootprintRadiusM),
                    Peers: [],
                    Random: _random));

                longest = Math.Max(longest, Vector3.Distance(before, Asset.PositionEus));
            }

            return longest;
        }

        /// <summary>Projects the vessel onto the wire at the current tick.</summary>
        /// <returns>The captured state.</returns>
        public AssetState Capture() => Asset.Capture(new AssetCaptureContext(
            Environment: _water,
            SimulationTimeSeconds: Tick * Dt,
            Tick: Tick,
            SourceTime: WorldEpochUtc.AddSeconds(Tick * Dt),
            ReceiveTime: WallClockUtc,
            Origin: null));
    }
}
