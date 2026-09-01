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
/// The determinism gate: a recorded replay package, re-run in two independent worlds, must
/// produce byte-identical states and events.
/// </summary>
/// <remarks>
/// Every domain's step is documented as a pure function of its context, the world owns seeded
/// generators, and every published timestamp but one derives from a simulation clock. Those are
/// claims, and each is load-bearing for something an operator relies on: replaying a recorded
/// incident, bisecting a regression to the step that introduced it, and comparing two runs that
/// differ by one deliberate change. This suite is what turns the claims into a gate.
/// <para>
/// <b>Why a digest and not a field comparison.</b> A digest fails on <em>any</em> divergence — a
/// hundredth step, one field of one domain extension, a sign — rather than only on the fields
/// somebody remembered to list. The rendering is a JSON serialisation of the shipping records, so
/// a field added tomorrow is covered from the moment it exists.
/// </para>
/// <para>
/// <b>Why events as well as states.</b> A state stream says where things ended up; the event
/// stream says what the world decided along the way. A run that reached the same positions by a
/// different route — raising a grounding twice, or raising it a step late — is not a replay, and
/// nothing in a state-only digest would notice.
/// </para>
/// <para>
/// <b>What is excluded, and what keeps that list honest.</b> Exactly two fields, both stamped
/// from the injected wall clock, both named and justified where the exclusion is applied. Two of
/// the cases below exist solely to keep the list from growing: one proves the excluded fields are
/// the only ones a clock reaches, the other proves each excluded field carries the clock and
/// nothing else.
/// </para>
/// <para>
/// <b>Boundary.</b> The gate covers the simulation: <see cref="AssetWorld"/>'s published states
/// and events. It deliberately does not cover the broadcast envelope around them —
/// <see cref="VizSnapshotV2.FrameId"/> is a fresh <see cref="Guid"/> per frame and
/// <see cref="VizSnapshotV2.ServerTime"/> is a wall-clock stamp, so neither is replayable and
/// neither is simulation output. Their reproducibility is not a property anything should rely on.
/// </para>
/// </remarks>
public sealed partial class ReplayDeterminismTests
{
    // ─── The core claim ─────────────────────────────────────────────────────

    /// <summary>
    /// A mixed air, ground and surface package replays to an identical digest in a second world.
    /// </summary>
    /// <remarks>
    /// The three domains together rather than one at a time, because the interesting failures are
    /// the shared ones: one generator serving both stepped domains, one frozen peer buffer, one
    /// environment sampler, one supervision sweep walking every asset. A per-domain replay check
    /// passes happily while any of those quietly makes a run depend on what else is in the world.
    /// </remarks>
    [Fact]
    public void A_Mixed_Three_Domain_Package_Replays_To_An_Identical_Digest()
    {
        var first = Run(MixedThreeDomain, WallClockUtc);
        var second = Run(MixedThreeDomain, WallClockUtc);

        Digest(second).Should().Be(Digest(first));

        // Guards against a digest taken over a world that did nothing. All three domains have to
        // be present, and the log has to have moved each of them.
        IReadOnlyList<AssetState> flown = StatesOf(first, AirId);
        flown.Should().NotBeEmpty();
        StatesOf(first, GroundId).Should().NotBeEmpty();
        StatesOf(first, SurfaceId).Should().NotBeEmpty();

        flown[^1].Pose.Position.Should().NotBe(
            flown[0].Pose.Position, "the drone must actually have flown");
        ModesOf(first, GroundId).Should().HaveCountGreaterThan(
            1, "the command log must actually change what the rover is doing");
        ModesOf(first, SurfaceId).Should().HaveCountGreaterThan(
            1, "the command log must actually change what the vessel is doing");
    }

    /// <summary>A run whose terrain preset changes part-way through still replays identically.</summary>
    /// <remarks>
    /// A preset switch is the most invasive thing an operator can do to a running world: it
    /// changes the ground every rover is standing on and the water surface every hull is floating
    /// on, in one step, with assets already under way. Both domains re-baseline against the new
    /// environment, and re-baselining is exactly the kind of work that reaches for a stored
    /// initial condition, a cached sample, or a settling loop that runs until it converges — none
    /// of which survives being replayed.
    /// <para>
    /// The second assertion is what stops this passing vacuously: the switch must actually change
    /// the run. It is compared against the same package without the switch, so the case cannot be
    /// satisfied by a preset that happened to change nothing under either asset.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Run_Including_A_Terrain_Preset_Switch_Replays_Identically()
    {
        var first = Run(WithTerrainPresetSwitch, WallClockUtc);
        var second = Run(WithTerrainPresetSwitch, WallClockUtc);

        Digest(second).Should().Be(Digest(first));

        var unswitched = Run(MixedThreeDomain, WallClockUtc);
        Digest(first).Should().NotBe(
            Digest(unswitched), "the preset switch must actually re-baseline something");
    }

    /// <summary>A run carrying commands and an injected link-loss fault replays identically.</summary>
    /// <remarks>
    /// A fault is where determinism is most often lost: supervision cadence and its contact ledger
    /// are both tempting places to reach for a wall clock. Both use simulation steps and simulation
    /// time here, while an explicit disconnected-link flag is enough to demand an immediate action.
    /// The package takes two links down and puts them back, so the recovery is replayed alongside
    /// the failure rather than only the failure.
    /// <para>
    /// The sequence, since the digest is only as legible as the run behind it: both links are
    /// taken down ahead of step 60. The SDK air pass runs first; the asset-world counter then
    /// advances to 60 and its simulation time to 1.0 s; ground and surface integrate; and the
    /// sixtieth-step sweep runs last. Its captures report the links disconnected, so the explicit
    /// link flag demands both fallbacks immediately even though their new ledger entries measure
    /// zero elapsed silence. The commands are issued at the end of step 60 and affect physics from
    /// step 61; both links are restored ahead of step 720. See <c>FaultSteps</c> for the full
    /// sequence.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Run_Including_Commands_And_An_Injected_Fault_Replays_Identically()
    {
        var first = Run(WithInjectedFaults, WallClockUtc);
        var second = Run(WithInjectedFaults, WallClockUtc);

        Digest(second).Should().Be(Digest(first));

        // And the fault has to have done something, or this is a digest over an untroubled world.
        var unfaulted = Run(
            WithInjectedFaults
                .WithoutActions(ReplayActionKind.LinkDown)
                .WithoutActions(ReplayActionKind.LinkUp),
            WallClockUtc);

        Digest(first).Should().NotBe(
            Digest(unfaulted), "the injected link loss must actually change the run");

        var firstSweepFallback = first.SafeActions.Single(captured =>
            captured.Tick == 60
            && string.Equals(
                captured.Record.Assessment.AssetId, AirId, StringComparison.Ordinal));

        firstSweepFallback.Record.ObservedAtSeconds.Should().Be(
            1.0, "the sixtieth step is supervised at one simulated second");
        firstSweepFallback.Record.Assessment.ElapsedSinceContactSeconds.Should().Be(
            0.0, "the first sweep creates the contact-ledger entry at that same instant");
        firstSweepFallback.Record.Assessment.Trigger.Should().Be(
            SafeActionTrigger.LinkLoss,
            "an explicitly disconnected link must trigger without waiting five seconds");
        firstSweepFallback.Record.AppliedCommand.Should().Be(
            AssetCommandKind.ReturnToBase,
            "the air fallback must be applied on the first sweep at the end of step 60");
        firstSweepFallback.Record.AppliedResult.Should().Be(SafeActionReasons.Nominal);
    }

    // ─── Cross-domain isolation ─────────────────────────────────────────────

    /// <summary>Adding an asset of one domain leaves every other domain bit-for-bit unchanged.</summary>
    /// <remarks>
    /// Tested in both directions on purpose, because the two are not symmetric. A world steps
    /// ground before surface and hands both passes the same generator, so a ground asset that
    /// ever drew from it would shift every subsequent surface draw — a perturbation a check
    /// running only in the other direction cannot see. Nothing draws from it today; this case is
    /// what keeps that true, or fails the moment it stops being.
    /// <para>
    /// Compared exactly rather than approximately: a perturbation of this kind appears in the
    /// last bits long before it appears anywhere a tolerance would notice.
    /// </para>
    /// </remarks>
    [Fact]
    public void Adding_An_Asset_Of_One_Domain_Perturbs_No_Other_Domain()
    {
        var threeDomain = Run(MixedThreeDomain, WallClockUtc);
        var withoutVessel = Run(MixedThreeDomain.WithoutAsset(SurfaceId), WallClockUtc);
        var withoutRover = Run(MixedThreeDomain.WithoutAsset(GroundId), WallClockUtc);

        Digest(threeDomain, AirId).Should().Be(
            Digest(withoutVessel, AirId), "a vessel must not move a drone at all");
        Digest(threeDomain, GroundId).Should().Be(
            Digest(withoutVessel, GroundId), "nor may it move a rover at all");

        Digest(threeDomain, AirId).Should().Be(
            Digest(withoutRover, AirId), "a rover must not move a drone at all");
        Digest(threeDomain, SurfaceId).Should().Be(
            Digest(withoutRover, SurfaceId), "nor may it move a vessel at all");

        // The removed assets really were absent, and the retained ones really were stepped, so
        // none of the four comparisons above passes by both sides being empty.
        StatesOf(withoutVessel, SurfaceId).Should().BeEmpty();
        StatesOf(withoutRover, GroundId).Should().BeEmpty();
        StatesOf(threeDomain, SurfaceId).Should().NotBeEmpty();
        StatesOf(threeDomain, GroundId).Should().NotBeEmpty();
    }

    // ─── Keeping the exclusion list honest ──────────────────────────────────

    /// <summary>The wall clock reaches the two excluded fields and nothing else.</summary>
    /// <remarks>
    /// The same package under two clocks hours apart. The canonical digests must agree, which
    /// says no other published field — and no event — carries a wall-clock value; the unexcluded
    /// digests must differ, which says the exclusion is load-bearing rather than decorative.
    /// <para>
    /// Together these are what an exclusion list cannot be grown past. Adding a third field to
    /// silence a failure would not silence it: the failing divergence would still be present
    /// under a single clock, where this case already requires the two runs to agree.
    /// </para>
    /// </remarks>
    [Fact]
    public void Only_The_Wall_Clock_Reaches_The_Excluded_Fields()
    {
        var early = Run(MixedThreeDomain, WallClockUtc);
        var late = Run(MixedThreeDomain, LateWallClockUtc);

        Digest(late).Should().Be(
            Digest(early), "nothing but the excluded fields may depend on the wall clock");
        RawDigest(late).Should().NotBe(
            RawDigest(early), "the excluded fields must be the ones that actually differ");
    }

    /// <summary>Each excluded field carries the wall-clock instant and no information of its own.</summary>
    /// <remarks>
    /// The other half of the guard. A field may be excluded only if excluding it discards
    /// nothing: both of these are the frozen clock exactly, on every capture of every asset in
    /// every domain. The faulted package is included so <see cref="LinkState.LastHeardAt"/> is
    /// pinned while links are actually held down, not only while they are connected. A field that
    /// carried a value of its own — a receive time offset by a modelled transport delay, say —
    /// fails here rather than being quietly dropped from the digest, which is the point. The
    /// unexcluded timestamps are checked in the same place, so the line between the two sets is
    /// stated once and asserted rather than described.
    /// </remarks>
    [Fact]
    public void Every_Excluded_Field_Carries_Exactly_The_Wall_Clock_Instant()
    {
        ReplayRun[] runs =
        [
            Run(MixedThreeDomain, LateWallClockUtc),
            Run(WithInjectedFaults, LateWallClockUtc),
        ];

        runs.SelectMany(run => run.States).Should().NotBeEmpty();
        runs[1].States.Should().Contain(
            captured => !captured.State.Link.IsConnected,
            "the faulted package must capture states while a link is held down");

        foreach (var captured in runs.SelectMany(run => run.States))
        {
            captured.State.ReceiveTime.Should().Be(
                LateWallClockUtc, "a receive time is the wall clock and nothing else");
            captured.State.Link.LastHeardAt.Should().Be(
                LateWallClockUtc, "and link liveness republishes that same instant");

            // The timestamp that is NOT excluded tracks simulation time instead, which is what
            // makes it safe to hash — and what would break first if a capture started sampling a
            // clock for it. The recorded tick is the world's own counter after the step, and the
            // world increments that counter before deriving simulation time from it, so a capture
            // on step N carries simulation time N / 60 exactly — step 60 is 1.0 s, not 0.0.
            // Written that way, as the integer step count over the 60 Hz tick rate, rather than
            // as the step count times the package's timestep: the two agree to well under a
            // millisecond but not in the last bits, and pinning a timestamp to an expression that
            // merely ought to match is how a gate starts flaking.
            captured.State.SourceTime.Should().Be(
                WorldEpochUtc + TimeSpan.FromSeconds(captured.Tick / SimulationTicksPerSecond),
                "a source time is the epoch plus simulation time, never a clock");
        }
    }

    // ─── The digest itself ──────────────────────────────────────────────────

    /// <summary>The digest notices a change to the recorded inputs.</summary>
    /// <remarks>
    /// A determinism gate whose digest ignored the run would pass forever. The command log is the
    /// canary rather than the seed: nothing in the current step path draws from either generator,
    /// so a seed change is inert today and asserting otherwise would pin a coincidence. Dropping
    /// every command is not inert — three assets left to their own devices are a different run,
    /// and the digest has to say so.
    /// </remarks>
    [Fact]
    public void The_Digest_Notices_A_Changed_Command_Log()
    {
        var commanded = Run(MixedThreeDomain, WallClockUtc);
        var uncommanded = Run(
            MixedThreeDomain.WithoutActions(ReplayActionKind.Command), WallClockUtc);

        Digest(uncommanded).Should().NotBe(Digest(commanded));

        // And the uncommanded run is itself a replay, so the canary is a controlled comparison
        // rather than two runs that merely happened to differ.
        Digest(Run(MixedThreeDomain.WithoutActions(ReplayActionKind.Command), WallClockUtc))
            .Should().Be(Digest(uncommanded));
    }
}
