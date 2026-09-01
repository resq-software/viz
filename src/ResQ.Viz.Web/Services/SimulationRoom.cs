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
using Microsoft.Extensions.Logging;
using ResQ.Simulation.Engine.Environment;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Snapshot of a single drone's state at one point in simulation time.
/// </summary>
public record DroneSnapshot(
    string Id,
    float[] Position,
    float[] Rotation,
    float[] Velocity,
    double Battery,
    string Status,
    bool Armed,
    string? Vendor = null);

/// <summary>
/// Per-room simulation state. One instance per active session — owns its own
/// <see cref="AssetWorld"/>, terrain, weather, and swarm controller. The
/// 60 Hz tick loop and SignalR broadcast live in <see cref="SimulationManager"/>;
/// this type is only state and a single-step API.
/// </summary>
/// <remarks>
/// The room is the session host; <see cref="AssetWorld"/> is the domain core. Everything about
/// <em>who is connected</em> and <em>how fast the loop runs</em> lives here, and everything
/// about <em>what exists in the world</em> lives there. The world performs no synchronisation
/// of its own, so every touch of it happens inside this type's single <c>_lock</c> — see
/// <c>SimulationRoom.Assets.cs</c>, which holds the v2 asset surface and the rules that keep
/// that guarantee true. <c>SimulationRoom.Environment.cs</c> holds the other partial half:
/// weather, terrain and the world factory, split out so this file stays about the session.
/// </remarks>
public sealed partial class SimulationRoom
{
    /// <summary>Broadcast a viz frame every N real ticks (60 Hz / 6 = 10 Hz).</summary>
    private const int BroadcastEveryNTicks = 6;

    /// <summary>Lowest and highest run-speed multipliers (world steps per real tick).</summary>
    private const int MinSpeed = 1;
    private const int MaxSpeed = 8;

    /// <summary>Upper bound on queued single-steps, so a runaway caller can't stall the loop.</summary>
    private const int MaxQueuedSteps = 600;

    /// <summary>Terrain preset a fresh room starts on; must match <see cref="TerrainNoiseService"/>'s own default.</summary>
    private const string DefaultTerrainPreset = "alpine";

    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly UpdatableWeatherSystem _weather;
    private readonly TerrainNoiseService _terrain;
    private readonly SwarmCoordinator _swarm;
    private readonly AssetCommandLog _commands = new();

    // The world owns the tick count and simulation time (both long/derived, so neither drifts
    // nor overflows at 8x speed), and the drone vendor tags that used to live in a side
    // dictionary here now travel on each air asset's descriptor — one population, one source.
    private AssetWorld _assets;
    private int _swarmTick;
    // Terrain preset currently installed, remembered so a reset can restore the matching sea
    // level. Without it a reset silently reverts the water surface to the default while the
    // terrain keeps its preset, and a vessel ends up floating over dry land.
    private string _terrainPreset = DefaultTerrainPreset;
    private long _environmentRevision;
    private volatile bool _backhaulKilled;
    private long _lastActivityTicks;
    private int _connectionCount;

    // ── Transport state (guarded by _lock) ──────────────────────────────────
    private bool _paused;
    private int _speed = 1;
    private int _pendingSteps;
    // Drives broadcast cadence independently of sim steps, so frames keep
    // flowing at 10 Hz while paused (steps == 0) or sped up (steps > 1).
    private long _broadcastTick;

    /// <summary>Opaque, server-issued room id (256-bit hex).</summary>
    public string Id { get; }

    /// <summary>The IP-prefix bucket of the creator. Cookies are bound to this bucket.</summary>
    public string IpBucket { get; }

    /// <summary>UTC creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Last activity (controller hit, tick broadcast, hub event). Used by the reaper.</summary>
    public DateTimeOffset LastActivityUtc =>
        new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>Live SignalR connections in this room. Reaper only drops rooms with 0 connections.</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>
    /// Current simulated backhaul-link state. When <c>true</c>, the swarm is
    /// running mesh-only; the next viz frame will report
    /// <see cref="ResQ.Viz.Web.Models.MeshVizState.Partitioned"/> as <c>true</c>.
    /// </summary>
    public bool IsBackhaulKilled => _backhaulKilled;

    /// <summary>Current simulation time in seconds.</summary>
    public double SimTime { get { lock (_lock) return _assets.SimulationTimeSeconds; } }

    /// <summary>Whether world advancement is paused. Frames still broadcast while paused.</summary>
    public bool IsPaused { get { lock (_lock) return _paused; } }

    /// <summary>Current run-speed multiplier (world steps per real tick).</summary>
    public int Speed { get { lock (_lock) return _speed; } }

    /// <summary>Total world steps advanced since the last reset.</summary>
    public long TickCount { get { lock (_lock) return _assets.TickCount; } }

    /// <summary>
    /// Atomic read of the transport triple (paused / speed / tick) under a single
    /// lock, so a broadcast frame can't report a mix of pre- and post-mutation
    /// values from three separate locked getters.
    /// </summary>
    public (bool Paused, int Speed, long Tick) TransportSnapshot()
    {
        lock (_lock) return (_paused, _speed, _assets.TickCount);
    }

    /// <summary>Initialises the room with a flat terrain and calm weather using default settings.</summary>
    public SimulationRoom(string id, string ipBucket, ILogger logger)
    {
        Id = id;
        IpBucket = ipBucket;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        _lastActivityTicks = CreatedAtUtc.UtcTicks;
        _logger = logger;
        _terrain = new TerrainNoiseService();
        _weather = new UpdatableWeatherSystem(new WeatherConfig());
        _assets = CreateWorld();
        _swarm = new SwarmCoordinator(_terrain);
    }

    /// <summary>Updates the activity timestamp so the reaper doesn't drop an actively-used room.</summary>
    public void Touch() =>
        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>Increments the live-connection counter when a hub client joins this room's group.</summary>
    public int IncrementConnections()
    {
        Touch();
        return Interlocked.Increment(ref _connectionCount);
    }

    /// <summary>Decrements the live-connection counter when a hub client disconnects.</summary>
    public int DecrementConnections()
    {
        Touch();
        var v = Interlocked.Decrement(ref _connectionCount);
        return v < 0 ? Interlocked.Exchange(ref _connectionCount, 0) : v;
    }

    /// <summary>Pauses world advancement. Frames keep broadcasting so the client reflects the paused state.</summary>
    public void Pause()
    {
        lock (_lock) { _paused = true; }
        Touch();
        _logger.LogInformation("[room {RoomId}] Simulation paused.", Id);
    }

    /// <summary>Resumes world advancement at the current speed.</summary>
    public void Resume()
    {
        lock (_lock) { _paused = false; }
        Touch();
        _logger.LogInformation("[room {RoomId}] Simulation resumed.", Id);
    }

    /// <summary>Sets the run-speed multiplier (world steps per real tick), clamped to [<see cref="MinSpeed"/>, <see cref="MaxSpeed"/>].</summary>
    public void SetSpeed(int multiplier)
    {
        var clamped = Math.Clamp(multiplier, MinSpeed, MaxSpeed);
        lock (_lock) { _speed = clamped; }
        Touch();
        _logger.LogInformation("[room {RoomId}] Speed set to {Speed}x.", Id, clamped);
    }

    /// <summary>
    /// Queues <paramref name="frames"/> single steps that advance even while paused
    /// (clamped to [1, <see cref="MaxQueuedSteps"/>]). Each consumes one real tick.
    /// </summary>
    public void StepFrames(int frames)
    {
        var n = Math.Clamp(frames, 1, MaxQueuedSteps);
        // Cap the TOTAL queue, not just the per-call count, so repeated calls
        // can't bank thousands of steps that keep advancing while the bar reads
        // paused.
        lock (_lock) { _pendingSteps = Math.Min(_pendingSteps + n, MaxQueuedSteps); }
        Touch();
    }

    /// <summary>Adds a drone to the simulation world at the specified start position.</summary>
    public void AddDrone(string id, Vector3 position) => AddDrone(id, position, vendor: null);

    /// <summary>Toggles the simulated backhaul link.</summary>
    public void SetBackhaulKilled(bool killed)
    {
        _backhaulKilled = killed;
        Touch();
        _logger.LogInformation("[room {RoomId}] Backhaul link {State}.", Id, killed ? "KILLED (mesh-only)" : "RESTORED");
    }

    /// <summary>Adds a drone with an optional vendor tag.</summary>
    public void AddDrone(string id, Vector3 position, string? vendor)
    {
        lock (_lock)
        {
            _assets.AddDrone(id, position, vendor);
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Drone {DroneId} added at ({X}, {Y}, {Z}) vendor={Vendor}.",
            Id, LogSafe(id), position.X, position.Y, position.Z, LogSafe(vendor) ?? "none");
    }

    /// <summary>Sends a <see cref="FlightCommand"/> to the named drone.</summary>
    public void SendCommand(string droneId, FlightCommand command)
    {
        lock (_lock)
        {
            var drone = _assets.Drones.FirstOrDefault(d => d.Id == droneId);
            if (drone is null)
            {
                _logger.LogWarning("[room {RoomId}] SendCommand: drone {DroneId} not found.", Id, LogSafe(droneId));
                return;
            }
            // Manual control wins: detach the drone from the swarm coordinator so
            // its 2 Hz pass stops overwriting this command on the next tick.
            _swarm.DetachManual(droneId);
            drone.SendCommand(command);
        }
        Touch();
    }

    /// <summary>
    /// Returns a manually-controlled drone to autonomous swarm flight. The
    /// coordinator re-assigns it a patrol route on its next tick. No-op if the
    /// drone is unknown or was never taken over.
    /// </summary>
    public void ResumeAuto(string droneId)
    {
        lock (_lock)
        {
            if (_assets.Drones.All(d => d.Id != droneId))
            {
                _logger.LogWarning("[room {RoomId}] ResumeAuto: drone {DroneId} not found.", Id, LogSafe(droneId));
                return;
            }
            _swarm.AttachAuto(droneId);
        }
        Touch();
    }

    /// <summary>Resets the simulation by discarding all drones and restarting the world clock.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            // A fresh world rather than a cleared one: it drops the registry, the counters and
            // the SDK world together, so a reset cannot leave a stale asset of any domain behind.
            _assets = CreateWorld();
            _swarmTick = 0;
            _swarm.ResetState();
            ClearScenario();
            ClearAssetEventBuffer();
            // Simulated time restarts with the world, so the observed contacts have to go with
            // it: a store that survived would measure every later report against a high-water
            // mark from the previous run and refuse the lot as arriving out of order.
            ClearTracks();
            _commands.Clear();
            _environmentRevision++;
            _backhaulKilled = false;
            _paused = false;
            _speed = 1;
            _pendingSteps = 0;
            _broadcastTick = 0;
        }

        // Outside the lock, and after the swap: every asset the old world held is gone, so
        // anything holding authority over one has to hear about it now rather than at whatever
        // request next happens to look. See IRoomLifecycleObserver.
        NotifyWorldReset();
        Touch();
        _logger.LogInformation("[room {RoomId}] Simulation reset.", Id);
    }

    /// <summary>Returns a snapshot of all drones' current state.</summary>
    /// <remarks>
    /// The v1 broadcast path's reading. A v2 frame must not pair this with a separate
    /// <see cref="CaptureAssetFrame"/>: two locked reads are two frames, and
    /// <see cref="RoomAssetFrame.Drones"/> exists so one frame stays one reading.
    /// </remarks>
    public IReadOnlyList<DroneSnapshot> GetSnapshot()
    {
        lock (_lock)
        {
            return CaptureDroneSnapshots();
        }
    }

    /// <summary>Projects every air asset into the v1 drone shape.</summary>
    /// <remarks>Must be called with <c>_lock</c> held; the returned list is fully materialised.</remarks>
    /// <returns>One snapshot per drone, in the flight world's order.</returns>
    private IReadOnlyList<DroneSnapshot> CaptureDroneSnapshots() =>
        _assets.Drones.Select(d =>
        {
            var state = d.FlightModel.State;
            var q = state.Orientation;
            return new DroneSnapshot(
                Id: d.Id,
                Position: [state.Position.X, state.Position.Y, state.Position.Z],
                Rotation: [q.X, q.Y, q.Z, q.W],
                Velocity: [state.Velocity.X, state.Velocity.Y, state.Velocity.Z],
                Battery: state.BatteryPercent,
                Status: d.FlightModel.HasLanded ? "landed" : "flying",
                Armed: !d.FlightModel.HasLanded,
                Vendor: _assets.TryGet(d.Id, out var asset) && asset is not null
                    ? asset.Descriptor.Vendor
                    : null);
        }).ToList();

    /// <summary>
    /// Advances the simulation by exactly one tick. Returns whether this tick
    /// is a broadcast tick (every 6th = 10 Hz) and the current sim time.
    /// </summary>
    public (bool ShouldBroadcast, double SimTime) Tick()
    {
        (bool ShouldBroadcast, double SimTime) tick;

        lock (_lock)
        {
            // World steps to advance this real (60 Hz) tick: a queued single-step
            // always advances exactly one (even while paused); otherwise paused
            // means zero, and running means the speed multiplier.
            int steps;
            if (_pendingSteps > 0) { steps = 1; _pendingSteps--; }
            else if (_paused) { steps = 0; }
            else { steps = _speed; }

            for (var i = 0; i < steps; i++)
            {
                // The world advances its own tick count and derives sim time from it (an
                // integer-counted division, which doesn't drift over hours the way accumulating
                // 1/60 per tick does), then steps ground and surface assets. The coordinator
                // pass runs after that, at the same 2 Hz phase it always has.
                _assets.Step();

                // A safe action that fired inside that step has taken its asset off autonomous
                // control, and the coordinator has to hear about it before its next pass — it
                // drives the same drones on a 2 Hz cycle and would otherwise retask a vehicle
                // the failsafe just sent home, inside half a simulated second and with nothing
                // recording that it did. Exactly the rule an operator command already follows,
                // through exactly the same call.
                var detached = _assets.DrainAutonomyDetachments();
                for (var d = 0; d < detached.Count; d++)
                {
                    _swarm.DetachManual(detached[d]);
                }

                _swarmTick++;
                if (_swarmTick % 30 == 0)
                    _swarm.Tick(_assets.SimulationTimeSeconds, _assets.Drones);
            }

            // Sweep whatever the assets raised into this room's bounded buffer, every tick and
            // unconditionally. Assets raise events during capture, so without a call site on the
            // loop itself the per-asset lists only shrink when a v2 consumer happens to drain
            // them — and a session nobody is draining is exactly the one that runs for hours.
            BufferAssetEvents();

            // Age the observed contacts against the same clock, on the same loop and for the
            // same reason: a session nobody is reading is the one that runs for hours, and a
            // contact that only expired when somebody asked would hold capacity a live one then
            // could not have. A function of simulated time only, so a paused session ages
            // nothing and a replay ages identically.
            AdvanceTracks();

            // Broadcast cadence is driven by REAL ticks, not sim steps, so the
            // client keeps receiving 10 Hz frames while paused (to reflect the
            // paused state) or sped up (without multiplying network traffic).
            _broadcastTick++;
            tick = (_broadcastTick % BroadcastEveryNTicks == 0, _assets.SimulationTimeSeconds);
        }

        // Upkeep for state that lives outside this room and lapses on its own — a control lease
        // in a session nobody is watching, above all. Outside the lock because an observer calls
        // back in, and throttled inside NotifyUpkeep so this stays a once-a-second pass rather
        // than a 60 Hz one. See IRoomLifecycleObserver.
        NotifyUpkeep();
        return tick;
    }

    /// <summary>Single-step helper for tests; ignores the broadcast flag.</summary>
    public void StepOnce() => Tick();

    private static string? LogSafe(string? value) =>
        value?.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
