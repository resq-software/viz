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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

// The runner and the digest: how a package is re-run in a fresh world, what is recorded from it,
// and exactly which fields are excluded before hashing. The suite's summary lives on the primary
// declaration in ReplayDeterminismTests.cs.
public sealed partial class ReplayDeterminismTests
{
    /// <summary>Frozen wall clock every run uses unless a case deliberately varies it.</summary>
    private static readonly DateTimeOffset WallClockUtc =
        new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    /// <summary>A second frozen wall clock, hours away from the first.</summary>
    /// <remarks>
    /// Used to prove the exclusion list is exactly right rather than merely convenient: a run
    /// under this clock must produce the same canonical digest as one under
    /// <see cref="WallClockUtc"/>, and a visibly different one when nothing is excluded.
    /// </remarks>
    private static readonly DateTimeOffset LateWallClockUtc =
        new(2026, 1, 1, 3, 47, 11, TimeSpan.Zero);

    /// <summary>Instant every excluded field is rewritten to before a canonical digest is taken.</summary>
    /// <remarks>
    /// <b>The exclusion list in full — two fields, one source, and nothing else.</b>
    /// <list type="bullet">
    /// <item>
    /// <see cref="AssetState.ReceiveTime"/> — when the server took delivery of the report.
    /// </item>
    /// <item>
    /// <see cref="LinkState.LastHeardAt"/> on <see cref="AssetState.Link"/> — the same instant,
    /// republished as link liveness.
    /// </item>
    /// </list>
    /// Both are written from the capture context's receive time, which <see cref="AssetWorld"/>
    /// fills from the injected <see cref="TimeProvider"/> at its single <c>GetUtcNow</c> call
    /// site — the only wall clock the simulation reaches — and every domain's capture copies that
    /// one value into both fields verbatim. <see cref="LinkState.LastHeardAt"/> in particular is
    /// republished unconditionally on every capture, whether or not the asset's command link is
    /// being held down, so it carries no link information of its own; that is what makes
    /// excluding it lossless rather than merely convenient. They are excluded because a real
    /// deployment stamps them from a clock that is genuinely different on a second run, and for
    /// no other reason.
    /// <para>
    /// <b>Nothing else is excluded, and specifically not the other timestamps.</b>
    /// <see cref="AssetState.SourceTime"/> and <see cref="FaultCode.RaisedAt"/> are the world
    /// epoch plus simulation time, and an <see cref="AssetEvent"/> carries no wall-clock field at
    /// all — only the simulation time and the tick it was raised at. Every one of them is
    /// reproducible and every one of them is hashed. Excluding one would hide exactly the class
    /// of defect this gate exists to catch.
    /// </para>
    /// <para>
    /// <b>What stops this list growing.</b> Two cases, and they pull in opposite directions.
    /// <see cref="Only_The_Wall_Clock_Reaches_The_Excluded_Fields"/> runs the same package under
    /// two different clocks and requires the canonical digests to match — so an exclusion cannot
    /// be added to paper over a divergence a clock did not cause, because that divergence would
    /// still be there under a single clock.
    /// <see cref="Every_Excluded_Field_Carries_Exactly_The_Wall_Clock_Instant"/> requires each
    /// excluded field to equal the frozen clock exactly — so a field may only be excluded if it
    /// carries the clock and nothing else. A field carrying anything of its own fails that check
    /// the moment someone adds it here.
    /// </para>
    /// </remarks>
    private static readonly DateTimeOffset ExcludedInstant = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Serializer options mirroring the wire path, so the digest is taken over what a client
    /// would actually be sent.
    /// </summary>
    /// <remarks>
    /// Serialising rather than listing fields by hand is the point: a field added to
    /// <see cref="AssetState"/>, to a domain extension or to <see cref="AssetEvent"/> tomorrow is
    /// hashed from the moment it exists, with nobody having to remember to add it here. A
    /// hand-written renderer silently stops covering whatever it was not updated for, which is
    /// the same failure mode as a quietly growing exclusion list wearing a different hat.
    /// <para>
    /// Numbers round-trip: <see cref="System.Text.Json"/> writes the shortest representation that
    /// reads back to the same bits, so the digest is over the exact values the simulation
    /// produced and not a rounded rendering of them.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Runs a package in a fresh world and records everything it published.</summary>
    /// <remarks>
    /// Every input is either taken from the package or pinned here: the seed, the timestep, the
    /// epoch, the terrain preset, the water surface and the only wall clock in the pipeline.
    /// Terrain and weather are the real implementations, because a determinism claim made over a
    /// stubbed environment would prove nothing about the one that ships.
    /// <para>
    /// Order within this runner is: apply the actions logged against the step, advance the world,
    /// drain all events then queued, and finally capture state on capture ticks. A world step can
    /// itself end with supervision captures, so events those captures raise are included in that
    /// step's drain. The runner's later state capture can also raise an air transition event; the
    /// drain has already happened, so an event first raised by that capture on step <c>N</c> stays
    /// queued until the drain after step <c>N + 1</c>. Draining on every step rather than only on
    /// capture ticks still matters — a drain is destructive, and otherwise events raised between
    /// captures would be assigned to whichever later tick happened to drain them.
    /// </para>
    /// <para>
    /// <b>What a step number means, exactly.</b> This loop counter is one-based and the world's
    /// own tick counter starts at zero, so loop step <c>N</c> is the step that leaves
    /// <c>AssetWorld.TickCount</c> at <c>N</c>. The SDK advances its clock and integrates air first.
    /// The asset world then increments its counter and derives simulation time from it before the
    /// ground and surface passes, the ending supervision sweep and the post-step capture. Those
    /// phases therefore use <c>N</c> divided by <see cref="SimulationTicksPerSecond"/> — tick 60
    /// is simulation time 1.0 s, not 0.0. Any claim about when a logged action takes effect has to
    /// combine the full order: the action lands ahead of the step it names, while a fallback from
    /// the ending sweep becomes a setpoint only after that step's domain integrations are complete.
    /// </para>
    /// </remarks>
    /// <param name="package">Recorded inputs to replay.</param>
    /// <param name="wallClock">Instant the frozen clock reports for every receive-time stamp.</param>
    /// <returns>Captured states and drained events, in the order they were produced.</returns>
    private static ReplayRun Run(ReplayPackage package, DateTimeOffset wallClock)
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset(package.TerrainPreset);

        var world = new AssetWorld(
            terrain,
            new UpdatableWeatherSystem(new WeatherConfig()),
            new AssetWorldOptions(
                Simulation: new SimulationConfig
                {
                    Seed = package.Seed,
                    DeltaTime = package.TimestepSeconds,
                },
                WorldEpochUtc: WorldEpochUtc,
                WallClock: new FixedClock(wallClock),
                SeaLevelM: SeaLevel.ForPreset(package.TerrainPreset)));

        // The package names one timestep and both halves of a step have to be integrating at it:
        // the asset pass takes it as an argument, the SDK's flight step takes it from its own
        // clock. A package whose timestep did not reach the clock would replay two worlds at once.
        world.Clock.EffectiveDeltaTime.Should().Be(
            package.TimestepSeconds, "the package's timestep must be the one the SDK clock uses");

        foreach (var spawn in package.Spawns)
        {
            Spawn(world, spawn);
        }

        world.AssetCount.Should().Be(package.Spawns.Count);

        var states = new List<CapturedState>();
        var events = new List<AssetEvent>();
        var safeActions = new List<CapturedSafeAction>();

        for (var tick = 1; tick <= package.Steps; tick++)
        {
            foreach (var action in package.Actions.Where(entry => entry.Tick == tick))
            {
                Apply(world, terrain, action);
            }

            world.Step(package.TimestepSeconds);
            events.AddRange(world.DrainEvents());

            if (tick % package.CaptureEveryTicks != 0)
            {
                continue;
            }

            foreach (var state in world.States)
            {
                states.Add(new CapturedState(tick, state));
            }

            foreach (var spawn in package.Spawns)
            {
                if (world.SafeActionFor(spawn.AssetId) is { } record)
                {
                    safeActions.Add(new CapturedSafeAction(tick, record));
                }
            }
        }

        return new ReplayRun(package.ScenarioName, states, events, safeActions);
    }

    /// <summary>Creates one asset through the same factory the API boundary would use.</summary>
    /// <remarks>
    /// Air assets go through <see cref="AssetWorld.AddDrone"/> because their lifetime belongs to
    /// the SDK's world; everything else is built by a factory and registered. Going through the
    /// real factories rather than constructing assets directly is what makes this a replay of the
    /// shipping spawn path, including the settling a rover does on contact and the flotation a
    /// hull does on the water surface.
    /// </remarks>
    /// <param name="world">World to spawn into.</param>
    /// <param name="spawn">Recorded spawn.</param>
    private static void Spawn(AssetWorld world, ReplaySpawn spawn)
    {
        if (spawn.VehicleClass == VehicleClass.Multirotor)
        {
            world.AddDrone(spawn.AssetId, spawn.PositionEus);
            return;
        }

        world.AddAsset(FactoryFor(world, spawn.VehicleClass).Create(new AssetSpawnPlan(
            AssetId: spawn.AssetId,
            VehicleClass: spawn.VehicleClass,
            Descriptor: AssetProfiles.Create(spawn.AssetId, spawn.VehicleClass),
            PositionEus: spawn.PositionEus,
            HeadingRad: spawn.HeadingRad)));
    }

    /// <summary>The factory that builds a vehicle class.</summary>
    /// <param name="world">World whose environment sampler the asset binds to.</param>
    /// <param name="vehicleClass">Class to build.</param>
    /// <returns>A factory able to build that class.</returns>
    private static IAssetFactory FactoryFor(AssetWorld world, VehicleClass vehicleClass) =>
        vehicleClass == VehicleClass.SurfaceVessel
            ? new SurfaceAssetFactory(world.Environment)
            : new GroundAssetFactory(world.Environment);

    /// <summary>Applies one logged action to a running world.</summary>
    /// <remarks>
    /// Every outcome is asserted rather than assumed. A command the log expects to be accepted
    /// and that is refused, or a link that was already down, means the package no longer replays
    /// what it recorded — and a digest taken over a run that quietly skipped half its log would
    /// still agree with itself, which is exactly the vacuous pass this gate must not produce.
    /// </remarks>
    /// <param name="world">World to act on.</param>
    /// <param name="terrain">Terrain source, for a preset switch.</param>
    /// <param name="action">Action to apply.</param>
    private static void Apply(AssetWorld world, TerrainNoiseService terrain, ReplayAction action)
    {
        switch (action.Kind)
        {
            case ReplayActionKind.Command:
                var command = action.Command ?? throw new InvalidOperationException(
                    $"The command logged at step {action.Tick} carries no command.");

                world.SendCommand(command).IsAccepted.Should().BeTrue(
                    $"'{command.Kind}' on '{action.AssetId}' at step {action.Tick} must be "
                    + "accepted for the package to replay");
                break;

            case ReplayActionKind.LinkDown:
                world.SetLinkAvailable(action.AssetId, false).Should().BeTrue(
                    $"'{action.AssetId}' must still have had a link at step {action.Tick}");
                break;

            case ReplayActionKind.LinkUp:
                world.SetLinkAvailable(action.AssetId, true).Should().BeTrue(
                    $"'{action.AssetId}' must still have been offline at step {action.Tick}");
                break;

            default:
                var preset = action.TerrainPreset ?? throw new InvalidOperationException(
                    $"The preset switch logged at step {action.Tick} names no preset.");

                // The room's own order: switch the terrain source, then move the water surface to
                // match it. Doing only the first would leave a hull floating over new ground.
                terrain.SetPreset(preset);
                world.SetSeaLevelForPreset(preset);
                break;
        }
    }

    /// <summary>Everything one run recorded for its replay and timing assertions.</summary>
    /// <param name="ScenarioName">Name of the package that produced this run.</param>
    /// <param name="States">Captured states, in capture order.</param>
    /// <param name="Events">Drained events, in the order they were raised.</param>
    /// <param name="SafeActions">
    /// Safe-action records observed on capture ticks, outside the digest.
    /// </param>
    private sealed record ReplayRun(
        string ScenarioName,
        IReadOnlyList<CapturedState> States,
        IReadOnlyList<AssetEvent> Events,
        IReadOnlyList<CapturedSafeAction> SafeActions);

    /// <summary>One state, with the step it was captured on.</summary>
    /// <remarks>
    /// The tick is recorded beside the state rather than inferred from it: a state captured on
    /// the wrong step, or one whose own counters stopped advancing, has to be visible in the
    /// digest, and it would not be if the digest only ever saw the state's account of itself.
    /// </remarks>
    /// <param name="Tick">World step this state was captured on.</param>
    /// <param name="State">The captured state.</param>
    private sealed record CapturedState(long Tick, AssetState State);

    /// <summary>One safe-action record, with the post-step capture tick that observed it.</summary>
    /// <remarks>
    /// Kept outside the replay digest because this is test instrumentation rather than a shipping
    /// wire record. It pins when a fallback was actually applied, which a state-only comparison
    /// cannot do: a cut link changes <see cref="LinkState.IsConnected"/> even if the policy never
    /// acts on it.
    /// </remarks>
    /// <param name="Tick">World step whose post-step capture observed the record.</param>
    /// <param name="Record">The governor's most recent decision and applied command.</param>
    private sealed record CapturedSafeAction(long Tick, SafeActionRecord Record);

    /// <summary>Canonical digest of a run, with the wall-clock fields excluded.</summary>
    /// <param name="run">Run to hash.</param>
    /// <param name="assetId">Restrict to one asset, or null for the whole run.</param>
    /// <returns>An uppercase SHA-256 hex digest.</returns>
    private static string Digest(ReplayRun run, string? assetId = null) =>
        Sha256(Render(run, assetId, excludeWallClock: true));

    /// <summary>Digest of a run with nothing excluded at all.</summary>
    /// <remarks>
    /// Only used to show that the exclusion is load-bearing. If this agreed across two different
    /// wall clocks, the excluded fields would not need excluding.
    /// </remarks>
    /// <param name="run">Run to hash.</param>
    /// <returns>An uppercase SHA-256 hex digest.</returns>
    private static string RawDigest(ReplayRun run) =>
        Sha256(Render(run, assetId: null, excludeWallClock: false));

    /// <summary>Renders a run to the exact text a digest is taken over.</summary>
    /// <param name="run">Run to render.</param>
    /// <param name="assetId">Restrict to one asset, or null for the whole run.</param>
    /// <param name="excludeWallClock">Whether to blank the two wall-clock fields.</param>
    /// <returns>One line per state and per event, states first.</returns>
    private static string Render(ReplayRun run, string? assetId, bool excludeWallClock)
    {
        var text = new StringBuilder();

        foreach (var captured in run.States)
        {
            if (!Matches(captured.State.AssetId, assetId))
            {
                continue;
            }

            var state = excludeWallClock ? WithoutWallClock(captured.State) : captured.State;
            text.Append("S|").Append(captured.Tick).Append('|').Append(Json(state)).Append('\n');
        }

        foreach (var raised in run.Events)
        {
            if (Matches(raised.AssetId, assetId))
            {
                text.Append("E|").Append(Json(raised)).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Whether an asset's line belongs in a filtered rendering.</summary>
    /// <param name="candidate">Asset the line was produced by.</param>
    /// <param name="assetId">Asset being filtered to, or null for no filter.</param>
    /// <returns><see langword="true"/> when the line is included.</returns>
    private static bool Matches(string candidate, string? assetId) =>
        assetId is null || string.Equals(candidate, assetId, StringComparison.Ordinal);

    /// <summary>Rewrites the two excluded wall-clock fields to a fixed instant.</summary>
    /// <remarks>
    /// A rewrite rather than a removal, so the fields stay in the rendered shape: if one were
    /// dropped from the model entirely, or renamed, the digest changes and the cases that pin
    /// this list fail — the correct outcome for a change to what the wall clock touches. See
    /// <see cref="ExcludedInstant"/> for the list itself and why it is exactly these two.
    /// </remarks>
    /// <param name="state">State as captured.</param>
    /// <returns>The state with its wall-clock stamps normalised.</returns>
    private static AssetState WithoutWallClock(AssetState state) => state with
    {
        ReceiveTime = ExcludedInstant,
        Link = state.Link.LastHeardAt is null
            ? state.Link
            : state.Link with { LastHeardAt = ExcludedInstant },
    };

    /// <summary>Serialises one record the way the wire would.</summary>
    /// <typeparam name="T">Record type being rendered.</typeparam>
    /// <param name="value">Value to render.</param>
    /// <returns>Its canonical JSON rendering.</returns>
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, WireOptions);

    /// <summary>Hashes rendered text.</summary>
    /// <param name="text">Rendered run.</param>
    /// <returns>An uppercase SHA-256 hex digest.</returns>
    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The distinct modes one asset reported across a run.</summary>
    /// <param name="run">Run to read.</param>
    /// <param name="assetId">Asset to look at.</param>
    /// <returns>Every distinct mode token, in first-seen order.</returns>
    private static IReadOnlyList<string> ModesOf(ReplayRun run, string assetId) =>
    [
        .. StatesOf(run, assetId).Select(state => state.Mode).Distinct(StringComparer.Ordinal),
    ];

    /// <summary>Every state one asset published across a run, in capture order.</summary>
    /// <param name="run">Run to read.</param>
    /// <param name="assetId">Asset to look at.</param>
    /// <returns>That asset's captured states.</returns>
    private static IReadOnlyList<AssetState> StatesOf(ReplayRun run, string assetId) =>
    [
        .. run.States
            .Where(captured =>
                string.Equals(captured.State.AssetId, assetId, StringComparison.Ordinal))
            .Select(captured => captured.State),
    ];

    /// <summary>A clock frozen at one instant, so a capture is a function of its inputs alone.</summary>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        /// <summary>Freezes the clock at an instant.</summary>
        /// <param name="now">Instant every read returns.</param>
        public FixedClock(DateTimeOffset now) => _now = now;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
