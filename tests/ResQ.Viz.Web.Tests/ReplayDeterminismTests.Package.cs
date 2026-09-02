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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Tests;

// The replay package: the recorded inputs a run is a function of, and the three packages the
// gate replays. Split from the runner so "what was recorded" reads separately from "how it is
// re-run"; the suite's summary lives on the primary declaration in ReplayDeterminismTests.cs.
public sealed partial class ReplayDeterminismTests
{
    /// <summary>Fixed integration timestep, in seconds. Both halves of a step use this one value.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Divisor the world turns its step count into simulation seconds with.</summary>
    /// <remarks>
    /// A separate constant from <see cref="Dt"/> on purpose, mirroring the world's own split.
    /// Simulation time is derived from an integer step count rather than accumulated a timestep
    /// at a time, and reconstructing an expected source time as "steps times timestep" instead of
    /// "steps over rate" agrees to well under a millisecond but not in the last bits.
    /// </remarks>
    private const double SimulationTicksPerSecond = 60.0;

    /// <summary>Seed every replayed world is built with, so a failure reproduces exactly.</summary>
    /// <remarks>
    /// Recorded in the package because a replay that did not pin it would not be a replay. It is
    /// deliberately <em>not</em> used as the canary for whether the digest notices anything:
    /// nothing in the current step path draws from either generator — no asset reads
    /// <see cref="AssetStepContext.Random"/>, and the SDK's world exposes its generator without
    /// drawing from it — so changing the seed today changes nothing, and a test that asserted
    /// otherwise would be pinning a coincidence. The command log is the canary instead.
    /// </remarks>
    private const int FixedSeed = 20260831;

    /// <summary>Terrain preset the packages start on; the only preset with water above the datum.</summary>
    private const string CoastalPreset = "coastal";

    /// <summary>Terrain preset the switch package moves to mid-run.</summary>
    /// <remarks>
    /// Chosen because it moves both surfaces at once and by a lot: the ground under the rover
    /// changes shape, and the water surface drops from <see cref="SeaLevel.CoastalM"/> to
    /// <see cref="SeaLevel.CanyonM"/>, which leaves the vessel on what is now dry land. Being
    /// aground is a modelled state with its own published fields and its own events, so the
    /// switch exercises the re-baseline in both domains rather than nudging one of them.
    /// </remarks>
    private const string CanyonPreset = "canyon";

    /// <summary>Steps the mixed and preset packages advance.</summary>
    private const int MixedSteps = 900;

    /// <summary>
    /// Steps the fault package advances: long enough for the immediate link-loss fallbacks to be
    /// integrated, followed by later logged commands, link restoration and post-restoration state.
    /// </summary>
    /// <remarks>
    /// Worked through step by step, because this is exactly the sequence a reader loses if they
    /// assume a logged action lands after the step it names. It lands <em>before</em> it, so
    /// <c>LinkDown(60, ...)</c> is already in force when step 60 begins. The world first advances
    /// the SDK clock, weather and air physics; it then increments its own tick counter, derives
    /// simulation time 1.0 s for tick 60, freezes peers, and integrates ground then surface. The
    /// sixtieth-step supervision sweep runs last. Its capture sees each cut link as disconnected,
    /// which demands the fallback immediately even though the just-created contact-ledger entry
    /// reports zero elapsed silence. The sweep issues air <c>returnToBase</c> and ground
    /// <c>stop</c> at the end of step 60, after those domains have integrated, so the commands first
    /// affect physics on step 61. The links are restored before step 720; its ending sweep sees
    /// them connected and re-arms supervision without issuing another command. Nine hundred steps
    /// retain a further 180 post-restoration steps in the replay.
    /// </remarks>
    private const int FaultSteps = 900;

    /// <summary>Steps between state captures. A divisor of the one-hertz supervision sweep.</summary>
    private const int CaptureEveryTicks = 30;

    /// <summary>Step the terrain preset switch happens on.</summary>
    /// <remarks>
    /// After the last command in the log — which is applied before step 540 — so no command's
    /// acceptance can depend on the terrain under it. A logged action precedes the step it names,
    /// so step 600 is the first step integrated against the new preset and its own capture is the
    /// first post-switch one, not the capture after it. With <see cref="CaptureEveryTicks"/> at
    /// thirty and <see cref="MixedSteps"/> at nine hundred, that is eleven capture ticks — 600
    /// through 900 — on the switched environment.
    /// </remarks>
    private const int PresetSwitchStep = 600;

    /// <summary>Heading due north, in radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Heading due east, in radians clockwise from true north.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>Heading due west, in radians clockwise from true north.</summary>
    private const double West = 3.0 * Math.PI / 2.0;

    /// <summary>Identifier of the air asset every package spawns.</summary>
    private const string AirId = "uav-1";

    /// <summary>Identifier of the ground asset every package spawns.</summary>
    private const string GroundId = "ugv-1";

    /// <summary>Identifier of the surface asset every package spawns.</summary>
    private const string SurfaceId = "usv-1";

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Scene-frame launch point for the drone, from the shipped coastal search preset.</summary>
    private static readonly Vector3 DroneSpawn = new(-700f, 140f, -180f);

    /// <summary>Waypoint the drone is sent to, far enough never to be reached inside a run.</summary>
    private static readonly Vector3 DroneTarget = new(-560f, 130f, 60f);

    /// <summary>Scene-frame spawn point for the rover: the coastal preset's dry, gentle shore.</summary>
    private static readonly Vector3 RoverSpawn = new(-525f, 0f, 0f);

    /// <summary>Scene-frame spawn point for the vessel: the coastal preset's deep water.</summary>
    private static readonly Vector3 VesselSpawn = new(-1000f, 0f, -250f);

    /// <summary>A three-domain package with a command log and no faults or environment changes.</summary>
    /// <remarks>
    /// The baseline every other package is derived from, so a difference between two packages is
    /// only ever the thing the derivation added. The log moves each asset through several
    /// guidance modes — a rover that only ever drove forwards would make a digest agree for
    /// uninteresting reasons — and every command in it is satisfiable by the capabilities of the
    /// asset it is addressed to, which the runner asserts rather than assumes.
    /// </remarks>
    private static ReplayPackage MixedThreeDomain => new(
        ScenarioName: "coastal-mixed",
        Seed: FixedSeed,
        TimestepSeconds: Dt,
        TerrainPreset: CoastalPreset,
        Spawns:
        [
            new ReplaySpawn(AirId, VehicleClass.Multirotor, DroneSpawn, North),
            new ReplaySpawn(GroundId, VehicleClass.AckermannRover, RoverSpawn, West),
            new ReplaySpawn(SurfaceId, VehicleClass.SurfaceVessel, VesselSpawn, North),
        ],
        Actions:
        [
            Cmd(1, Target(AirId, AssetCommandKind.GoTo, DroneTarget)),
            Cmd(1, Command(GroundId, AssetCommandKind.SetSpeed, speedMps: 2.0)),
            Cmd(1, Command(SurfaceId, AssetCommandKind.SetCourse, 4.0, North)),
            Cmd(120, Command(GroundId, AssetCommandKind.Reverse, speedMps: 1.5)),
            Cmd(180, Command(AirId, AssetCommandKind.SetSpeed, speedMps: 12.0)),
            Cmd(240, Command(SurfaceId, AssetCommandKind.SetCourse, 3.0, East)),
            Cmd(300, Command(GroundId, AssetCommandKind.Hold)),
            Cmd(360, Command(GroundId, AssetCommandKind.ResumeAutonomy)),
            Cmd(420, Command(AirId, AssetCommandKind.Loiter)),
            Cmd(480, Command(SurfaceId, AssetCommandKind.Stop)),
            Cmd(540, Command(GroundId, AssetCommandKind.Park)),
        ],
        Steps: MixedSteps,
        CaptureEveryTicks: CaptureEveryTicks);

    /// <summary>The mixed package with a terrain preset switch part-way through.</summary>
    /// <remarks>
    /// Reproduces what a room does when an operator changes the preset, for the parts the world
    /// owns: the terrain source is switched and the water surface is moved to match, in that
    /// order and between two steps. A room additionally re-routes drones through the swarm
    /// coordinator, which belongs to the room rather than the world and is not exercised here.
    /// </remarks>
    private static ReplayPackage WithTerrainPresetSwitch => MixedThreeDomain with
    {
        ScenarioName = "coastal-mixed+preset-switch",
        Actions = [.. MixedThreeDomain.Actions, PresetSwitch(PresetSwitchStep, CanyonPreset)],
    };

    /// <summary>The mixed package with two command links taken down and later restored.</summary>
    /// <remarks>
    /// The air and ground links, because their declared link-loss behaviours differ — an air
    /// asset returns, a ground asset stops and stays put — so one injected fault exercises two
    /// fallbacks. Both links are restored before the run ends: a package that only ever broke
    /// things would replay a world no operator could get back, and the recovery has to come out
    /// the same twice as well.
    /// </remarks>
    private static ReplayPackage WithInjectedFaults => MixedThreeDomain with
    {
        ScenarioName = "coastal-mixed+link-loss",
        Steps = FaultSteps,
        Actions =
        [
            .. MixedThreeDomain.Actions,
            LinkDown(60, AirId),
            LinkDown(60, GroundId),
            LinkUp(720, AirId),
            LinkUp(720, GroundId),
        ],
    };

    /// <summary>Everything a replayed run is a function of.</summary>
    /// <remarks>
    /// A package is data, not code: two runs of the same package must agree, and a run of a
    /// package derived from another by one edit must differ from it in exactly what that edit
    /// implies. Holding the inputs in one record is what makes those derivations —
    /// <see cref="WithoutAsset"/>, <see cref="WithoutActions"/> — one-liners rather than
    /// hand-copied logs that could drift apart.
    /// </remarks>
    /// <param name="ScenarioName">Operator-facing name of the recorded scenario.</param>
    /// <param name="Seed">Seed the world's generators are built from.</param>
    /// <param name="TimestepSeconds">Fixed timestep the SDK clock and the asset pass both use.</param>
    /// <param name="TerrainPreset">Terrain preset the run starts on; also fixes the initial water surface.</param>
    /// <param name="Spawns">Assets to create, in the order they are created.</param>
    /// <param name="Actions">Commands, injected faults and environment changes, each pinned to a step.</param>
    /// <param name="Steps">Steps to advance.</param>
    /// <param name="CaptureEveryTicks">Steps between state captures.</param>
    private sealed record ReplayPackage(
        string ScenarioName,
        int Seed,
        double TimestepSeconds,
        string TerrainPreset,
        IReadOnlyList<ReplaySpawn> Spawns,
        IReadOnlyList<ReplayAction> Actions,
        int Steps,
        int CaptureEveryTicks)
    {
        /// <summary>The same package with one asset, and everything addressed to it, removed.</summary>
        /// <remarks>
        /// Both halves matter. Dropping the spawn alone would leave commands addressed to an
        /// asset that no longer exists, which the runner refuses; dropping the actions alone
        /// would leave the asset in the world doing nothing, which is not the same experiment.
        /// An action carrying no asset — a terrain preset switch — is kept.
        /// </remarks>
        /// <param name="assetId">Asset to remove.</param>
        /// <returns>A package with that asset absent from spawns and log alike.</returns>
        public ReplayPackage WithoutAsset(string assetId) => this with
        {
            Spawns = Spawns
                .Where(spawn => !string.Equals(spawn.AssetId, assetId, StringComparison.Ordinal))
                .ToArray(),
            Actions = Actions
                .Where(action => !string.Equals(action.AssetId, assetId, StringComparison.Ordinal))
                .ToArray(),
        };

        /// <summary>The same package with every action of one kind removed.</summary>
        /// <param name="kind">Kind of action to drop.</param>
        /// <returns>A package whose log no longer contains that kind.</returns>
        public ReplayPackage WithoutActions(ReplayActionKind kind) => this with
        {
            Actions = Actions.Where(action => action.Kind != kind).ToArray(),
        };
    }

    /// <summary>One asset to create before a run starts.</summary>
    /// <param name="AssetId">Identifier the asset is registered under.</param>
    /// <param name="VehicleClass">Class to build; also decides which factory builds it.</param>
    /// <param name="PositionEus">Spawn position in the scene frame, in metres.</param>
    /// <param name="HeadingRad">Initial heading in radians clockwise from true north.</param>
    private sealed record ReplaySpawn(
        string AssetId, VehicleClass VehicleClass, Vector3 PositionEus, double HeadingRad);

    /// <summary>What a logged action does when its step comes round.</summary>
    private enum ReplayActionKind
    {
        /// <summary>Route a validated command to an asset.</summary>
        Command,

        /// <summary>Take an asset's command link down, injecting a link-loss fault.</summary>
        LinkDown,

        /// <summary>Bring an asset's command link back up.</summary>
        LinkUp,

        /// <summary>Switch the terrain preset and move the water surface to match.</summary>
        TerrainPreset,
    }

    /// <summary>One entry in the ordered log, pinned to the step it is applied before.</summary>
    /// <remarks>
    /// Applied before the step it names, because the world executes a command immediately rather
    /// than queueing it: recording "after step N" would replay a step later than it was recorded.
    /// <para>
    /// The rule is the same for every kind, which is the half that is easy to lose when reading a
    /// log entry back. A <see cref="ReplayActionKind.LinkDown"/> at step <c>N</c> is already down
    /// when step <c>N</c> is supervised, and a <see cref="ReplayActionKind.TerrainPreset"/> at
    /// step <c>N</c> is the terrain step <c>N</c> integrates against — not the one after it.
    /// </para>
    /// </remarks>
    /// <param name="Tick">One-based step this action precedes.</param>
    /// <param name="Kind">What the action does.</param>
    /// <param name="AssetId">Asset the action addresses, or empty for an environment change.</param>
    /// <param name="Command">The command to route, for <see cref="ReplayActionKind.Command"/>.</param>
    /// <param name="TerrainPreset">Preset key, for <see cref="ReplayActionKind.TerrainPreset"/>.</param>
    private sealed record ReplayAction(
        int Tick,
        ReplayActionKind Kind,
        string AssetId,
        SimulatedAssetCommand? Command = null,
        string? TerrainPreset = null);

    /// <summary>Logs a command against the step it is issued before.</summary>
    /// <param name="tick">One-based step the command precedes.</param>
    /// <param name="command">Validated, translated command to route.</param>
    /// <returns>The log entry.</returns>
    private static ReplayAction Cmd(int tick, SimulatedAssetCommand command) =>
        new(tick, ReplayActionKind.Command, command.AssetId, Command: command);

    /// <summary>Logs a link-loss fault injection.</summary>
    /// <param name="tick">One-based step the link goes down before.</param>
    /// <param name="assetId">Asset losing its command link.</param>
    /// <returns>The log entry.</returns>
    private static ReplayAction LinkDown(int tick, string assetId) =>
        new(tick, ReplayActionKind.LinkDown, assetId);

    /// <summary>Logs a link restoration.</summary>
    /// <param name="tick">One-based step the link comes back before.</param>
    /// <param name="assetId">Asset regaining its command link.</param>
    /// <returns>The log entry.</returns>
    private static ReplayAction LinkUp(int tick, string assetId) =>
        new(tick, ReplayActionKind.LinkUp, assetId);

    /// <summary>Logs a terrain preset switch.</summary>
    /// <param name="tick">One-based step the switch precedes.</param>
    /// <param name="presetKey">Preset to switch to.</param>
    /// <returns>The log entry.</returns>
    private static ReplayAction PresetSwitch(int tick, string presetKey) =>
        new(tick, ReplayActionKind.TerrainPreset, string.Empty, TerrainPreset: presetKey);

    /// <summary>A command carrying no destination.</summary>
    /// <param name="assetId">Asset the command is addressed to.</param>
    /// <param name="kind">Command kind to issue.</param>
    /// <param name="speedMps">Speed the kind may carry, or null.</param>
    /// <param name="headingRad">Heading or course the kind may carry, or null.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(
        string assetId,
        AssetCommandKind kind,
        double? speedMps = null,
        double? headingRad = null) =>
        new(Kind: kind, AssetId: assetId, SpeedMps: speedMps, HeadingRad: headingRad);

    /// <summary>A command carrying a scene-frame destination.</summary>
    /// <param name="assetId">Asset the command is addressed to.</param>
    /// <param name="kind">Command kind to issue.</param>
    /// <param name="targetEus">Destination in the scene frame, in metres.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Target(
        string assetId, AssetCommandKind kind, Vector3 targetEus) =>
        new(
            Kind: kind,
            AssetId: assetId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity));
}
