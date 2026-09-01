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
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>What a surface asset is allowed to say, and how often it is allowed to say it.</summary>
/// <remarks>
/// One suite for the event discipline alone, because the defects it exists to pin are not
/// arithmetic and would never fail a physics case. They share a shape: a condition that persists
/// gets reported as though it had just happened, or a transition gets reported as the transition
/// next to it. Both produce a log that is plausible on every line and useless in aggregate, which
/// is the failure mode nobody notices until an operator needs it.
/// <list type="number">
///   <item><b>Levels raised as events.</b> A hull the set holds against a shoal, or one sitting on
///   dry land, meets the same refusal on every world step. Raising the contact on that refusal put
///   an alert in the log sixty times a second for exactly the vessels most in need of a readable
///   one — and, because the per-asset queue is bounded, threw away the earlier transitions that
///   explained how the vessel got there. The leading edge is the event, the pin is a state, and
///   getting free is an event of its own.</item>
///   <item><b>Transitions reported as their neighbours.</b> A station keep leaving its tolerance
///   radius announced that the hold was "nominal again" — the opposite of what had happened, to an
///   operator who is being told while they can still act on it.</item>
///   <item><b>Two thresholds for one question.</b> A heading policy that gated on "faster than
///   zero" while the bearing it then called had its own dead band turned a disturbance of a
///   micrometre per second into a permanent hundred-and-eighty-degree turn.</item>
/// </list>
/// <para>
/// Everything here is a literal or a closed-form expression: a fixed timestep, a seeded generator,
/// and an analytic basin whose slope, current and wind are constants. Nothing reads a clock or
/// sleeps, and every loop is bounded by a step budget, so a case that never reaches its event
/// fails on a stated expectation instead of hanging.
/// </para>
/// </remarks>
public sealed class SurfaceEventTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed for every generator in this suite, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Identifier every vessel in this suite is spawned with.</summary>
    private const string RigId = "usv-events-1";

    /// <summary>Heading due north, in radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Heading due west, in radians clockwise from true north.</summary>
    private const double West = 3.0 * Math.PI / 2.0;

    /// <summary>Heading the reciprocal cases fall back to, in radians clockwise from true north.</summary>
    /// <remarks>
    /// Deliberately not a cardinal point and not the answer to any policy under test, so a policy
    /// that silently kept the heading it already had cannot be mistaken for one that worked — and,
    /// here, so the broken answer (this value plus <c>pi</c>) is nowhere near the right one.
    /// </remarks>
    private const double FallbackHeadingRad = 0.75;

    /// <summary>Gradient of the analytic beach: one metre of rise in ten of easting.</summary>
    /// <remarks>
    /// A real gradient rather than a cliff, because the bed normal is what
    /// <see cref="WaterConstraints.DeflectAlongEdge"/> deflects a refused move along, and a
    /// vertical normal has no upslope direction at all. A hull pinned against a step in a flat bed
    /// could never work itself off, which is a fixture that cannot express the case rather than a
    /// vessel that cannot recover.
    /// </remarks>
    private const double BeachGradient = 0.1;

    /// <summary>Scene easting the pinning cases spawn at, in metres.</summary>
    /// <remarks>
    /// Fourteen metres offshore is 1.4 m of water: comfortably more than the hull's draft plus its
    /// advisory margin, so the vessel starts in water it is entitled to be in and drifts into
    /// water it is not, rather than beginning the case already in contact.
    /// </remarks>
    private const double PinningSpawnEastM = -14.0;

    /// <summary>Scene easting the deep-water cases sit at, in metres.</summary>
    /// <remarks>Thirty metres of water, so no clearance derate touches anything under test.</remarks>
    private const double DeepWaterEastM = -300.0;

    /// <summary>East-setting current the pinning cases use, in metres per second.</summary>
    /// <remarks>
    /// The amplitude the shipped surface-current field actually produces, not a storm: whatever a
    /// vessel meets on an ordinary day it has to be able to leave again.
    /// </remarks>
    private const double PinningCurrentMps = 0.35;

    /// <summary>An east-setting current past anything this hull can hold against, in metres per second.</summary>
    private const double OverwhelmingCurrentMps = 6.0;

    /// <summary>Ticks a pinned vessel is held for before its event log is counted.</summary>
    /// <remarks>
    /// Ten seconds at 60 Hz. The defect this suite exists for raised one alert per tick, so the
    /// difference between the right answer and the broken one here is one event against six
    /// hundred — and the broken run also overruns the bounded queue and starts losing history,
    /// which is asserted separately below.
    /// </remarks>
    private const int PinnedTicks = 600;

    /// <summary>Ticks a recovering vessel runs under command before it is stopped again.</summary>
    /// <remarks>
    /// Thirty seconds, and the first ten of them buy no offing at all: the hull is lying head to
    /// the set when the course order lands, so it spends them swinging ninety degrees and
    /// gathering way against a ceiling the clearance derate still has hold of. Only once it is
    /// round and in water that lifts the derate does it make its two and a half metres a second,
    /// which is why a budget measured as "distance equals speed times time" came out short by
    /// about a hull's length. Long enough that the vessel is unambiguously off the shoal rather
    /// than a step or two clear of it, and short enough that the drift back onto the shoal
    /// afterwards still fits inside <see cref="MaxDriftBackSteps"/>.
    /// </remarks>
    private const int RecoveryTicks = 1_800;

    /// <summary>Ceiling on the steps a case runs while waiting for an event.</summary>
    private const int MaxSteps = 6000;

    /// <summary>Ceiling on the steps the re-contact case runs; drifting back onto a beach is slow.</summary>
    private const int MaxDriftBackSteps = 20000;

    /// <summary>Speed the recovery course is commanded at, in metres per second.</summary>
    /// <remarks>
    /// A third of the hull's envelope. A course is held rather than a position run to, so the
    /// vessel keeps driving offshore for as long as the case asks it to instead of arriving
    /// somewhere and quietly beginning to drift back — which would leave "is it clear yet" a
    /// function of how long the transit happened to take.
    /// </remarks>
    private const double RecoverySpeedMps = 2.0;

    /// <summary>Range north of the vessel the station-keeping cases move the station to, in metres.</summary>
    /// <remarks>
    /// Nine tolerance radii, so the hold is unambiguously outside the radius the instant the
    /// station moves, and close enough to settle inside one bounded run. The closure law is
    /// proportional with the hull's own lag behind it, which damps at about 0.7 — an overshoot of a
    /// few per cent of the run, well inside the radius, so the hold does not chatter back out and
    /// the transition sequence stays a sequence.
    /// </remarks>
    private const double StationRunM = 60.0;

    /// <summary>Most events one drain may return once the bounded queue has had to drop some.</summary>
    /// <remarks>
    /// The queue holds sixty-four and <see cref="SurfaceAsset.DrainEvents"/> appends one notice
    /// saying how many were lost, so a drain from a saturated queue returns sixty-five.
    /// </remarks>
    private const int MaxDrainedEvents = 65;

    /// <summary>Emergency-stop and release cycles the queue-bound case issues between two steps.</summary>
    /// <remarks>
    /// Each cycle raises exactly two events — the stop engaging and the stop releasing, both
    /// genuine edges — so forty cycles offer eighty events to a queue that holds sixty-four.
    /// Driven by commands rather than by a pinned hull because a pinned hull now correctly raises
    /// almost nothing, which is the whole point of this file.
    /// </remarks>
    private const int StopCycles = 40;

    /// <summary>Timeline marker for a contact with the edge of navigable water.</summary>
    private const string Contacted = "contact";

    /// <summary>Timeline marker for getting free of one.</summary>
    private const string Cleared = "cleared";

    /// <summary>The shipped workboat: a displacement hull that cannot hold a station.</summary>
    private static readonly SurfaceProfile DisplacementHull = SurfaceProfile.SurfaceVessel;

    /// <summary>The same hull with the propulsion to hold a station, and the power draw to match.</summary>
    /// <remarks>
    /// Both shipped profiles refuse a station keep, so a hull that can hold one has to be built
    /// here. Only the two figures that must move together are changed, so it still passes
    /// <see cref="SurfaceProfile.Validated"/>.
    /// </remarks>
    private static readonly SurfaceProfile HoldingHull =
        SurfaceProfile.SurfaceVessel with { CanStationKeep = true, StationKeepPowerW = 900.0 };

    /// <summary>A zone that denies a position fix to anything inside it.</summary>
    private static readonly EnvironmentZone[] PositionDeniedZone =
    [
        new EnvironmentZone(
            ZoneId: "gnss-shadow",
            Kind: SurfaceAsset.PositionDeniedZoneKind,
            IsEntryProhibited: false,
            SpeedLimitMps: null,
            Advisory: "Advisory: no position fix inside this zone."),
    ];

    private static readonly EnvironmentZone[] NoZones = [];

    /// <summary>Every event code a station keep can raise, so a filter cannot miss one.</summary>
    private static readonly string[] StationKeepCodes =
    [
        StationKeeping.EngagedCode,
        StationKeeping.CorrectingCode,
        StationKeeping.RestoredCode,
        StationKeeping.SaturatedCode,
        StationKeeping.DegradedCode,
        StationKeeping.ReleasedCode,
    ];

    // ─── A contact is an edge; a pin is a state ─────────────────────────────

    /// <summary>
    /// A vessel the set holds against a shoal for hundreds of ticks reports meeting it once, says
    /// it is still there for as long as it is, and reports getting free once.
    /// </summary>
    /// <remarks>
    /// <b>The case this file exists for.</b> The refusal the water mask issues is a level: true on
    /// every step something keeps pressing the hull at the edge, and true forever for a hull on
    /// dry land that cannot move at all. Raising the contact on that level did three things, each
    /// worse than the last — it buried every other event, it overran the bounded per-asset queue
    /// so real history was dropped, and it taught an operator to ignore the one code that means a
    /// vessel has stopped somewhere it did not intend to.
    /// <para>
    /// The condition itself is not suppressed. It moves to
    /// <see cref="SurfaceAsset.IsInShorelineContact"/>, where a display or an allocator reads it at
    /// any instant without counting anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Vessel_Pinned_Against_A_Shoal_Reports_Meeting_It_Once_And_Getting_Free_Once()
    {
        var rig = DriftingOntoTheShoal();

        rig.RunUntilContacts(1, MaxSteps);
        rig.Asset.IsInShorelineContact.Should().BeTrue();

        rig.Run(PinnedTicks);

        rig.Timeline().Should().Equal(
            new[] { Contacted },
            "the vessel has been held against the same shoal for ten seconds; it did not meet it "
            + "six hundred more times, and it never got free");

        rig.Asset.IsInShorelineContact.Should().BeTrue(
            "remaining pinned is a condition, and a condition has to be readable rather than "
            + "counted out of a stream of alerts");

        rig.Log.Should().NotContain(
            e => e.Code == SurfaceAsset.EventsDroppedCode,
            "an edge-triggered log cannot overrun a sixty-four event queue in six hundred ticks; "
            + "a level-triggered one loses history on every pin");

        rig.Apply(SetCourse(West, RecoverySpeedMps)).IsAccepted.Should().BeTrue();

        rig.RunUntilTimeline(2, MaxSteps);

        rig.Timeline().Should().Equal(
            new[] { Contacted, Cleared }, "getting free is one event, on the step it happened");
        rig.Asset.IsInShorelineContact.Should().BeFalse();
    }

    /// <summary>A vessel that gets clear and drifts back onto the same shoal reports a second contact.</summary>
    /// <remarks>
    /// The other half of the contract, and the reason the fix is a latch rather than a
    /// suppression. Meeting the same shoal twice really is two contacts, and an operator watching
    /// a hull work itself off and then set back down onto it has to be told the second time.
    /// Nothing here is a duplicate-suppression window or a rate limit: the vessel was genuinely
    /// free in between, and <see cref="SurfaceAsset.IsInShorelineContact"/> said so.
    /// <para>
    /// The timeline is asserted as strictly alternating rather than as an exact list. That is the
    /// real invariant — every contact is followed by its clearance before another contact can be
    /// raised — and it is the one the broken code violated by hundreds of consecutive entries.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Vessel_That_Gets_Clear_And_Meets_The_Shoal_Again_Reports_A_Second_Contact()
    {
        var rig = DriftingOntoTheShoal();

        rig.RunUntilContacts(1, MaxSteps);
        float pinnedEastM = rig.Asset.PositionEus.X;

        rig.Apply(SetCourse(West, RecoverySpeedMps)).IsAccepted.Should().BeTrue();
        rig.Run(RecoveryTicks);

        rig.Asset.IsInShorelineContact.Should().BeFalse("the vessel has driven itself off");
        rig.Asset.PositionEus.X.Should().BeLessThan(
            pinnedEastM - (float)DisplacementHull.LengthM,
            "this case is about a genuine re-contact, so the vessel has to be genuinely clear "
            + "first — further off than its own length");

        // The propeller stops and the set does the rest, which is exactly how the vessel came to
        // be on the shoal in the first place.
        rig.Apply(Command(AssetCommandKind.Stop)).IsAccepted.Should().BeTrue();
        rig.RunUntilContacts(2, MaxDriftBackSteps);

        rig.Contacts().Count.Should().BeGreaterThanOrEqualTo(
            2,
            "the vessel was free in between, so this is a second contact and not the first one "
            + "repeating");

        // The count alone would be satisfied by a log that raised nothing but contacts, which is
        // exactly the defect. This is the assertion that will not be.
        AssertStrictlyAlternating(rig.Timeline());
        rig.Timeline().Last().Should().Be(Contacted, "it is pinned again as the case ends");
        rig.Asset.IsInShorelineContact.Should().BeTrue();
    }

    /// <summary>The bounded event queue still drops rather than growing, and still says that it did.</summary>
    /// <remarks>
    /// Nothing may accumulate in a per-asset collection without a drain or a bounded drop policy,
    /// and that contract is unchanged by the contact becoming an edge — it is simply no longer
    /// reachable by pinning a hull, which is the improvement. It is reached here by issuing
    /// genuine edges faster than anything drains them: forty emergency stops and forty releases,
    /// eighty real transitions, against a queue of sixty-four. The oldest are kept because they
    /// are the ones that explain how the vessel got into the state it is in, and the loss is
    /// reported rather than silent.
    /// </remarks>
    [Fact]
    public void The_Event_Queue_Stays_Bounded_And_Reports_What_It_Dropped()
    {
        var rig = DeepWater();
        rig.Run(1);
        rig.Drain();

        for (int i = 0; i < StopCycles; i++)
        {
            rig.Apply(Command(AssetCommandKind.EmergencyStop)).IsAccepted.Should().BeTrue();
            rig.Apply(Command(AssetCommandKind.Stop)).IsAccepted.Should().BeTrue();
        }

        var drained = rig.Drain();

        drained.Count.Should().BeLessThanOrEqualTo(
            MaxDrainedEvents,
            "a stalled consumer cannot make one vessel hold an event for every transition it was "
            + "not drained on");

        drained.Should().Contain(
            e => e.Code == SurfaceAsset.EventsDroppedCode,
            "a bounded queue that dropped events must say so, rather than hand back a partial "
            + "history that reads like a complete one");
    }

    // ─── Station-keeping phases, each reported as itself ────────────────────

    /// <summary>Every station-keep phase transition raises the code that matches it.</summary>
    /// <remarks>
    /// One run through the whole state machine, asserted as an ordered sequence, because the
    /// defect was not a missing event but a <em>wrong</em> one: leaving the tolerance radius fell
    /// into the same arm as returning to it and announced that the hold was "nominal again" at the
    /// moment it began losing ground. A case that only checked something was raised would pass
    /// against that.
    /// <para>
    /// The station is moved rather than the weather, so the excursion is exact and instantaneous
    /// and the phase it produces cannot be confused with a saturation: in still water the hold has
    /// its whole allowance in hand throughout.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_Station_Keep_Transition_Raises_The_Code_That_Matches_It()
    {
        var basin = new Basin();
        var rig = new Rig(basin, HoldingHull, declareStationKeep: true);
        rig.Run(1);

        rig.Apply(Command(AssetCommandKind.StationKeep)).IsAccepted.Should().BeTrue();
        rig.NextStationKeepCode(MaxSteps).Should().Be(
            StationKeeping.EngagedCode, "a hold begins by being engaged");

        rig.Apply(StationKeepAt(NorthOfTheVessel())).IsAccepted.Should().BeTrue();
        rig.NextStationKeepCode(MaxSteps).Should().Be(
            StationKeeping.CorrectingCode,
            "the vessel is suddenly sixty metres off its station: it is correcting, and saying "
            + "'the hold is nominal again' here is the single most misleading thing this domain "
            + "was capable of");

        rig.NextStationKeepCode(MaxSteps).Should().Be(
            StationKeeping.RestoredCode, "closing on the station and reaching it is the restoration");

        basin.IsPositionDenied = true;
        rig.NextStationKeepCode(MaxSteps).Should().Be(
            StationKeeping.DegradedCode, "a hold that has lost its fix is degraded, not released");

        basin.IsPositionDenied = false;
        rig.NextStationKeepCode(MaxSteps).Should().Be(
            StationKeeping.RestoredCode, "getting the fix back restores the hold it never gave up");

        rig.Apply(Command(AssetCommandKind.Stop)).IsAccepted.Should().BeTrue();
        rig.NextStationKeepCode(MaxSteps).Should().Be(
            StationKeeping.ReleasedCode, "giving the station up is a release and nothing else");
    }

    /// <summary>A hold commanded onto a station the vessel is not at engages, rather than reporting a return.</summary>
    /// <remarks>
    /// The transition the old default arm got backwards in the other direction: engaging straight
    /// into <see cref="StationKeepPhase.Correcting"/> is still an engagement, because there was no
    /// hold before it. Reporting it as a restoration claims a hold recovered that had never begun.
    /// </remarks>
    [Fact]
    public void A_Hold_Commanded_Onto_A_Distant_Station_Engages_Rather_Than_Restoring()
    {
        var rig = new Rig(new Basin(), HoldingHull, declareStationKeep: true);
        rig.Run(1);

        rig.Apply(StationKeepAt(NorthOfTheVessel())).IsAccepted.Should().BeTrue();

        rig.NextStationKeepCode(MaxSteps).Should().Be(StationKeeping.EngagedCode);
    }

    /// <summary>A hold engaged into a set it cannot stem reports the saturation, not an engagement.</summary>
    /// <remarks>
    /// Saturation outranks the position error by design — the vessel is still exactly on station
    /// when it fires — so the first thing an operator hears about this hold is that it has no
    /// effort left, which is the thing they can still act on.
    /// </remarks>
    [Fact]
    public void A_Hold_Engaged_Into_An_Overwhelming_Set_Reports_The_Saturation()
    {
        var rig = new Rig(
            new Basin(currentEastMps: OverwhelmingCurrentMps),
            HoldingHull,
            declareStationKeep: true);
        rig.Run(1);

        rig.Apply(Command(AssetCommandKind.StationKeep)).IsAccepted.Should().BeTrue();

        rig.NextStationKeepCode(MaxSteps).Should().Be(StationKeeping.SaturatedCode);
    }

    // ─── One dead band, asked for once ──────────────────────────────────────

    /// <summary>A disturbance too small to have a direction leaves the bow where it is.</summary>
    /// <remarks>
    /// <b>The gap between two thresholds, made unreachable.</b> The reciprocal policies used to ask
    /// "is this faster than zero", while the bearing they then called refused anything below
    /// <see cref="CoordinateFrames.MinHorizontalMagnitude"/> and handed back the fallback heading.
    /// A disturbance in between — real to the gate, degenerate to the bearing — therefore produced
    /// the fallback heading with <c>pi</c> added to it, and a vessel in flat calm was commanded to
    /// turn through a hundred and eighty degrees and hold there for as long as the hold lasted.
    /// <para>
    /// Driven at exactly half the shared dead band, read from the constant rather than from a
    /// copied literal, so the case follows the threshold if the threshold ever moves.
    /// </para>
    /// </remarks>
    /// <param name="policy">Reciprocal heading policy under test.</param>
    [Theory]
    [InlineData(StationKeepHeadingPolicy.IntoCurrent)]
    [InlineData(StationKeepHeadingPolicy.IntoWind)]
    [InlineData(StationKeepHeadingPolicy.MinimumPower)]
    public void A_Sub_Threshold_Disturbance_Leaves_A_Reciprocal_Hold_On_Its_Heading(
        StationKeepHeadingPolicy policy)
    {
        float negligible = (float)(CoordinateFrames.MinHorizontalMagnitude / 2.0);
        var disturbance = new Vector3(negligible, 0f, 0f);

        CoordinateFrames.SpeedOverGround(disturbance).Should().BeGreaterThan(
            0.0,
            "the vector is not identically zero — that is precisely what made the gap between the "
            + "gate and the dead band reachable");

        var outcome = EvaluateHold(policy, disturbance, disturbance);

        outcome.HeadingSetpointRad.Should().BeApproximately(
            FallbackHeadingRad, 1e-9,
            "a disturbance with no direction is nothing to bow into, so the hold keeps the heading "
            + "it has; adding pi to a fallback commands a permanent about-turn in flat calm");
    }

    /// <summary>A disturbance large enough to have a direction is still bowed into.</summary>
    /// <remarks>
    /// The other side of the same threshold, because a fix that made the reciprocal unreachable
    /// would satisfy the case above and break the policy entirely.
    /// </remarks>
    /// <param name="policy">Reciprocal heading policy under test.</param>
    [Theory]
    [InlineData(StationKeepHeadingPolicy.IntoCurrent)]
    [InlineData(StationKeepHeadingPolicy.IntoWind)]
    [InlineData(StationKeepHeadingPolicy.MinimumPower)]
    public void A_Real_Disturbance_Still_Turns_A_Reciprocal_Hold_Into_It(
        StationKeepHeadingPolicy policy)
    {
        var setting = new Vector3(1.0f, 0f, 0f);

        var outcome = EvaluateHold(policy, setting, setting);

        outcome.HeadingSetpointRad.Should().BeApproximately(
            West, 1e-6, "an east-setting disturbance is bowed into by heading west");
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────

    /// <summary>A vessel drifting onto the analytic basin's shoal under the shipped set.</summary>
    /// <returns>A rig whose vessel is in 1.4 m of water and closing on the beach.</returns>
    private static Rig DriftingOntoTheShoal() => new(
        new Basin(currentEastMps: PinningCurrentMps),
        DisplacementHull,
        spawnEus: new Vector3((float)PinningSpawnEastM, 0f, 0f),
        headingRad: North);

    /// <summary>A vessel in thirty metres of still water, far from anything.</summary>
    /// <returns>A rig nothing in the environment acts on.</returns>
    private static Rig DeepWater() => new(new Basin(), DisplacementHull);

    /// <summary>The station the station-keeping cases move a hold to.</summary>
    /// <remarks>
    /// Due north of the deep-water spawn, so the easting — and therefore the depth, the clearance
    /// and the speed ceiling — is the same at the station as at the vessel. A station that changed
    /// the depth would put a derate in the middle of a case about phases.
    /// </remarks>
    /// <returns>A scene-frame point <see cref="StationRunM"/> north of the spawn.</returns>
    private static Vector3 NorthOfTheVessel() =>
        new((float)DeepWaterEastM, 0f, -(float)StationRunM);

    /// <summary>A validated command carrying nothing but its kind.</summary>
    /// <param name="kind">Command kind to issue.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(AssetCommandKind kind) =>
        new(Kind: kind, AssetId: RigId);

    /// <summary>A validated course command.</summary>
    /// <param name="courseRad">Course to steer, radians clockwise from true north.</param>
    /// <param name="speedMps">Speed to make, in metres per second.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand SetCourse(double courseRad, double speedMps) =>
        new(
            Kind: AssetCommandKind.SetCourse,
            AssetId: RigId,
            HeadingRad: courseRad,
            SpeedMps: speedMps);

    /// <summary>A validated station-keep command on an explicit station.</summary>
    /// <param name="targetEus">Station to hold, in the scene frame.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand StationKeepAt(Vector3 targetEus) =>
        new(
            Kind: AssetCommandKind.StationKeep,
            AssetId: RigId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity));

    /// <summary>Asserts a contact timeline alternates, beginning with a contact.</summary>
    /// <remarks>
    /// The invariant the level-versus-edge fix establishes, stated once. A log that raises a
    /// contact on a persisting refusal violates it on its second entry; one that never clears
    /// violates it on the entry after the vessel gets free.
    /// </remarks>
    /// <param name="timeline">Contact and clearance markers, in the order they were raised.</param>
    private static void AssertStrictlyAlternating(IReadOnlyList<string> timeline)
    {
        timeline.Should().NotBeEmpty("this assertion is about a timeline that has entries");

        for (int i = 0; i < timeline.Count; i++)
        {
            timeline[i].Should().Be(
                i % 2 == 0 ? Contacted : Cleared,
                "a contact and its clearance alternate: entry {0} of {1} says otherwise, which "
                + "means either a level was raised as an event or a pin was never cleared",
                i,
                timeline.Count);
        }
    }

    /// <summary>Runs one evaluation of the station-keeping law over a literal disturbance.</summary>
    /// <remarks>
    /// The law is pure arithmetic over a goal, a state and a disturbance, so the reciprocal cases
    /// need no world at all: the drift is stated rather than integrated, which is the only way to
    /// place one exactly inside a dead band.
    /// </remarks>
    /// <param name="policy">Heading policy the goal is held on.</param>
    /// <param name="currentEus">Ambient drift of the water column, in the scene frame.</param>
    /// <param name="windEus">Wind velocity, in the scene frame.</param>
    /// <returns>The outcome the law produced.</returns>
    private static StationKeepOutcome EvaluateHold(
        StationKeepHeadingPolicy policy, Vector3 currentEus, Vector3 windEus)
    {
        var state = SurfaceMotionState.DeadInTheWater(0.0, 0.0, FallbackHeadingRad);

        var velocities = new SurfaceVelocities(
            GroundVelocityEus: currentEus,
            WaterRelativeVelocityEus: Vector3.Zero,
            DriftVelocityEus: currentEus,
            HeadingRad: FallbackHeadingRad,
            CourseOverGroundRad: FallbackHeadingRad,
            SpeedOverGroundMps: CoordinateFrames.SpeedOverGround(currentEus),
            SpeedThroughWaterMps: 0.0);

        return StationKeeping.Evaluate(
            HoldingHull,
            StationKeepGoal.For(HoldingHull, Vector3.Zero, headingPolicy: policy),
            new StationKeepInput(
                State: state,
                Velocities: velocities,
                PassiveDriftEus: currentEus,
                WindEus: windEus,
                SpeedCeilingMps: HoldingHull.MaxSpeedMps,
                HasPositionFix: true));
    }

    /// <summary>One vessel on an analytic basin, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what the world does per step — sample the environment at the asset's pre-step
    /// position, build a context, step the asset — without a world, so a case can be stated in
    /// literals. Every step drains into <see cref="Log"/>, because the asset's queue is
    /// deliberately bounded and a long run would otherwise start dropping the very transitions
    /// these cases count.
    /// </remarks>
    private sealed class Rig
    {
        private readonly Random _random = new(FixedSeed);
        private readonly Basin _basin;
        private readonly SurfaceProfile _profile;

        /// <summary>Floats a vessel and prepares it to be stepped.</summary>
        /// <param name="basin">Water to float on.</param>
        /// <param name="profile">Hull envelope to integrate within.</param>
        /// <param name="spawnEus">Scene-frame spawn point, or null for deep water.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        /// <param name="declareStationKeep">True to declare a station-keeping capability.</param>
        public Rig(
            Basin basin,
            SurfaceProfile profile,
            Vector3? spawnEus = null,
            double headingRad = North,
            bool declareStationKeep = false)
        {
            _basin = basin;
            _profile = profile;

            var shipped = AssetProfiles.Create(RigId, VehicleClass.SurfaceVessel);
            var descriptor = declareStationKeep
                ? shipped with { Capabilities = shipped.Capabilities | AssetCapability.StationKeep }
                : shipped;

            Asset = new SurfaceAsset(
                descriptor,
                SurfaceDynamics.For(profile),
                basin,
                spawnEus ?? new Vector3((float)DeepWaterEastM, 0f, 0f),
                headingRad);
        }

        /// <summary>The vessel under test.</summary>
        public SurfaceAsset Asset { get; }

        /// <summary>World steps taken so far.</summary>
        public long Tick { get; private set; }

        /// <summary>Every event raised since the rig was built, in the order they were raised.</summary>
        public List<AssetEvent> Log { get; } = [];

        /// <summary>Advances the vessel by exactly one step and drains what it raised.</summary>
        public void Step()
        {
            var before = Asset.PositionEus;
            Tick++;

            Asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: Tick * Dt,
                Tick: Tick,
                Environment: _basin.Sample(before, _profile.FootprintRadiusM),
                Peers: [],
                Random: _random));

            Log.AddRange(Asset.DrainEvents());
        }

        /// <summary>Advances the vessel by a fixed number of steps.</summary>
        /// <param name="steps">Number of steps.</param>
        public void Run(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Step();
            }
        }

        /// <summary>Advances until a stated number of contacts has been raised.</summary>
        /// <remarks>
        /// A bounded loop over a literal budget, never a wait: the step count is the only clock in
        /// this suite, so a run that never reaches its event fails on a stated expectation rather
        /// than hanging.
        /// </remarks>
        /// <param name="count">Contacts to wait for.</param>
        /// <param name="maxSteps">Most steps to take before failing.</param>
        public void RunUntilContacts(int count, int maxSteps)
        {
            for (int i = 0; i < maxSteps && Contacts().Count < count; i++)
            {
                Step();
            }

            Contacts().Count.Should().BeGreaterThanOrEqualTo(
                count, $"{count} contact(s) must be raised within {maxSteps} steps");
        }

        /// <summary>Advances until the contact timeline has a stated number of entries.</summary>
        /// <param name="entries">Timeline entries to wait for.</param>
        /// <param name="maxSteps">Most steps to take before failing.</param>
        public void RunUntilTimeline(int entries, int maxSteps)
        {
            for (int i = 0; i < maxSteps && Timeline().Count < entries; i++)
            {
                Step();
            }

            Timeline().Count.Should().BeGreaterThanOrEqualTo(
                entries, $"{entries} timeline entries must be raised within {maxSteps} steps");
        }

        /// <summary>Advances until the next station-keep event, and returns its code.</summary>
        /// <param name="maxSteps">Most steps to take before failing.</param>
        /// <returns>The code of the first station-keep event raised after this call.</returns>
        public string NextStationKeepCode(int maxSteps)
        {
            int from = Log.Count;

            for (int i = 0; i < maxSteps; i++)
            {
                Step();

                foreach (var raised in Log.Skip(from))
                {
                    if (StationKeepCodes.Contains(raised.Code))
                    {
                        return raised.Code;
                    }
                }
            }

            Log.Skip(from).Should().Contain(
                e => StationKeepCodes.Contains(e.Code),
                $"a station-keep event must be raised within {maxSteps} steps");

            return string.Empty;
        }

        /// <summary>Applies a validated command to the vessel.</summary>
        /// <param name="command">Command to apply.</param>
        /// <returns>Acceptance, or a rejection carrying a machine-readable reason.</returns>
        public AssetCommandResult Apply(SimulatedAssetCommand command) => Asset.Apply(command);

        /// <summary>Drains the vessel's queue directly, bypassing <see cref="Log"/>.</summary>
        /// <returns>What the queue held.</returns>
        public IReadOnlyList<AssetEvent> Drain() => Asset.DrainEvents();

        /// <summary>Every shoreline or shoal contact raised so far.</summary>
        /// <returns>The contacts, in the order they were raised.</returns>
        public IReadOnlyList<AssetEvent> Contacts() => Log
            .Where(e => e.Code == ShorelineContact.ShorelineCode
                || e.Code == ShorelineContact.ShoalCode)
            .ToList();

        /// <summary>Contacts and clearances, in the order they were raised.</summary>
        /// <remarks>
        /// The two codes interleaved and reduced to markers, because what these cases are really
        /// about is the <em>shape</em> of the sequence rather than the content of any one entry.
        /// </remarks>
        /// <returns>One marker per contact or clearance.</returns>
        public IReadOnlyList<string> Timeline() => Log
            .Where(e => e.Code == ShorelineContact.ShorelineCode
                || e.Code == ShorelineContact.ShoalCode
                || e.Code == SurfaceAsset.ContactClearedCode)
            .Select(e => e.Code == SurfaceAsset.ContactClearedCode ? Cleared : Contacted)
            .ToList();
    }

    /// <summary>A uniformly shelving beach under still or setting water.</summary>
    /// <remarks>
    /// Deliberately not the procedural terrain. A bed whose depth varies in two directions puts an
    /// under-keel derate into the middle of every run, and a set whose direction varies makes the
    /// disturbance a function of where the vessel drifted to — either of which turns a closed-form
    /// expectation into an approximation of one. Here the bed rises at a constant
    /// <see cref="BeachGradient"/> towards the east and is level north to south, so depth is a
    /// function of easting alone and the contour a pinned hull can work along runs due north.
    /// <para>
    /// <see cref="IsPositionDenied"/> is the only thing that moves. It is this simulation's one
    /// mechanism for taking a vessel's position quality away, and toggling it changes neither the
    /// water surface nor the bed, so it can never be mistaken for the environment being replaced
    /// under the hull.
    /// </para>
    /// </remarks>
    private sealed class Basin : IEnvironmentSampler
    {
        private readonly Vector3 _current;
        private readonly Vector3 _wind;
        private readonly Vector3 _normal;

        /// <summary>Builds a basin.</summary>
        /// <param name="currentEastMps">East-setting surface current in metres per second.</param>
        /// <param name="windEastMps">East-blowing wind in metres per second.</param>
        public Basin(double currentEastMps = 0.0, double windEastMps = 0.0)
        {
            _current = new Vector3((float)currentEastMps, 0f, 0f);
            _wind = new Vector3((float)windEastMps, 0f, 0f);
            _normal = Vector3.Normalize(new Vector3((float)-BeachGradient, 1f, 0f));
            Wind = new UniformWind(_wind);
        }

        /// <summary>True while every point of this basin denies a position fix.</summary>
        public bool IsPositionDenied { get; set; }

        /// <inheritdoc />
        public double SeaLevelM => 0.0;

        /// <inheritdoc />
        public IWindField Wind { get; }

        /// <inheritdoc />
        public double GetElevation(double x, double z) => BeachGradient * x;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => _normal;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM)
        {
            double elevation = GetElevation(positionEus.X, positionEus.Z);
            bool ashore = elevation >= SeaLevelM;

            return new EnvironmentSample(
                PositionEus: positionEus,
                WindEus: _wind,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: _current,
                TerrainElevationM: elevation,
                TerrainNormalEus: _normal,
                SurfaceMaterial: ashore ? SurfaceType.BareGround : SurfaceType.Water,
                WaterSurfaceElevationM: ashore ? null : (double?)SeaLevelM,
                BathymetricElevationM: ashore ? null : (double?)elevation,
                Zones: IsPositionDenied ? PositionDeniedZone : NoZones);
        }
    }

    /// <summary>A wind field that blows the same way everywhere.</summary>
    private sealed class UniformWind : IWindField
    {
        private readonly Vector3 _wind;

        /// <summary>Builds a uniform wind field.</summary>
        /// <param name="wind">Wind velocity in the scene frame, in metres per second.</param>
        public UniformWind(Vector3 wind) => _wind = wind;

        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => _wind;
    }
}
