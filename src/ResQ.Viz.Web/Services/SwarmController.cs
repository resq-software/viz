// Copyright 2024 ResQ Technologies Ltd.
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using ResQ.Simulation.Engine.Entities;
using ResQ.Simulation.Engine.Physics;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Terrain-aware swarm flight controller.  Runs at 2 Hz (every 30 simulation ticks at 60 Hz)
/// and issues <see cref="FlightCommand.GoTo"/> waypoints to each drone so they fly distinct,
/// meaningful patterns rather than clustering at a fixed point.
/// </summary>
/// <remarks>
/// Each drone is assigned a <see cref="DroneRole"/> that holds a cyclic waypoint route.
/// The controller advances each drone along its route, applies peer-separation forces, and
/// triggers RTL when battery falls below <see cref="LowBatteryThreshold"/>.
///
/// Terrain awareness: all waypoints have their Y coordinate clamped to
/// <c>TerrainHeight(x,z) + minAgl</c>, where <c>minAgl</c> depends on the active preset.
/// </remarks>
public sealed class SwarmController
{
    // ── Tuning constants ─────────────────────────────────────────────────────

    /// <summary>Battery percentage at which a drone breaks formation and returns to launch.</summary>
    public const float LowBatteryThreshold = 0.25f;

    /// <summary>Distance in metres within which a drone is considered to have reached its waypoint.</summary>
    public const float WaypointArrivalRadius = 18f;

    /// <summary>Seconds before the controller forcibly advances to the next waypoint (timeout).</summary>
    public const float WaypointTimeout = 35f;

    /// <summary>Horizontal radius within which two drones trigger a separation push.</summary>
    public const float SeparationRadius = 65f;

    /// <summary>Maximum horizontal offset applied by the separation force, in metres.</summary>
    public const float SeparationMaxOffset = 40f;

    // ── State ─────────────────────────────────────────────────────────────────

    private TerrainNoiseService _terrain;
    private float _minAgl = 25f;
    private string _preset = "alpine";
    private string _scenario = "";

    /// <summary>Per-drone route state, keyed by drone ID.</summary>
    private readonly Dictionary<string, DroneRole> _roles = new();

    private sealed class DroneRole
    {
        public Vector3[] Route { get; }
        public int RouteIndex { get; set; }
        public double AssignedAt { get; set; }
        public bool Retiring { get; set; }

        public DroneRole(Vector3[] route, int routeIndex, double assignedAt, bool retiring)
        {
            Route = route;
            RouteIndex = routeIndex;
            AssignedAt = assignedAt;
            Retiring = retiring;
        }
    }

    /// <summary>Initialises the controller with an initial terrain service (alpine preset).</summary>
    public SwarmController(TerrainNoiseService terrain) => _terrain = terrain;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Switches to a new terrain preset, updating the minimum AGL and regenerating all routes
    /// so drones immediately adapt to the new landscape.
    /// </summary>
    public void SetTerrainPreset(string preset, TerrainNoiseService terrain, IReadOnlyList<SimulatedDrone> drones)
    {
        _terrain = terrain;
        _preset = preset.ToLowerInvariant();
        _minAgl = _preset switch
        {
            "ridgeline" => 20f,
            "coastal" => 15f,
            "canyon" => 12f,
            "dunes" => 8f,
            _ => 25f,  // alpine default
        };

        // Rebuild routes for all current drones under the new terrain
        RegenerateAllRoutes(drones);
    }

    /// <summary>
    /// Assigns scenario-specific flight patterns to the given drone list.
    /// Call this after spawning drones for a new scenario.
    /// </summary>
    public void SetScenario(string scenarioName, IReadOnlyList<SimulatedDrone> drones)
    {
        _scenario = scenarioName.ToLowerInvariant();
        _roles.Clear();
        RegenerateAllRoutes(drones);
    }

    /// <summary>
    /// Main tick — call every 30 simulation ticks (≈2 Hz).
    /// Advances each drone toward its next waypoint, applies separation, and handles battery RTL.
    /// </summary>
    public void Tick(double simTime, IReadOnlyList<SimulatedDrone> drones)
    {
        if (drones.Count == 0) return;

        // Ensure every drone has a role (handles late-spawned drones)
        for (int i = 0; i < drones.Count; i++)
        {
            var drone = drones[i];
            if (!_roles.ContainsKey(drone.Id) && !drone.FlightModel.HasLanded)
                AssignRole(drone, drones, i, simTime);
        }

        // Compute peer positions for separation
        var positions = new Dictionary<string, Vector3>();
        foreach (var d in drones)
        {
            if (!d.FlightModel.HasLanded)
                positions[d.Id] = d.FlightModel.State.Position;
        }

        foreach (var drone in drones)
        {
            if (drone.FlightModel.HasLanded) continue;
            if (!_roles.TryGetValue(drone.Id, out var role)) continue;
            if (role.Retiring) continue;

            var state = drone.FlightModel.State;

            // Battery failsafe
            if (state.BatteryPercent < LowBatteryThreshold)
            {
                drone.FlightModel.ApplyCommand(FlightCommand.RTL());
                role.Retiring = true;
                continue;
            }

            var target = role.Route[role.RouteIndex];

            // Check arrival or timeout → advance waypoint
            var distXZ = new Vector2(state.Position.X - target.X, state.Position.Z - target.Z).Length();
            var timedOut = simTime - role.AssignedAt > WaypointTimeout;

            if (distXZ < WaypointArrivalRadius || timedOut)
            {
                role.RouteIndex = (role.RouteIndex + 1) % role.Route.Length;
                role.AssignedAt = simTime;
                target = role.Route[role.RouteIndex];
            }

            // Apply separation: offset target away from nearby peers
            var offset = Vector3.Zero;
            foreach (var (peerId, peerPos) in positions)
            {
                if (peerId == drone.Id) continue;
                var diff = new Vector2(state.Position.X - peerPos.X, state.Position.Z - peerPos.Z);
                var dist = diff.Length();
                if (dist < SeparationRadius && dist > 0.1f)
                {
                    var push = diff / dist * (SeparationRadius - dist);
                    offset += new Vector3(push.X, 0f, push.Y);
                }
            }

            if (offset.Length() > SeparationMaxOffset)
                offset = Vector3.Normalize(offset) * SeparationMaxOffset;

            var adjustedTarget = target + offset;
            // Re-clamp Y to terrain after offset shifts XZ
            adjustedTarget = adjustedTarget with
            {
                Y = Math.Max(adjustedTarget.Y, (float)_terrain.GetElevation(adjustedTarget.X, adjustedTarget.Z) + _minAgl),
            };

            drone.FlightModel.ApplyCommand(FlightCommand.GoTo(adjustedTarget));
        }
    }

    // ── Route generation ──────────────────────────────────────────────────────

    private void RegenerateAllRoutes(IReadOnlyList<SimulatedDrone> drones)
    {
        _roles.Clear();
        for (int i = 0; i < drones.Count; i++)
        {
            var drone = drones[i];
            if (!drone.FlightModel.HasLanded)
            {
                var route = BuildRoute(i, drones.Count, drone.FlightModel.State.Position);
                _roles[drone.Id] = new DroneRole(route, 0, 0, false);
            }
        }
    }

    private void AssignRole(SimulatedDrone drone, IReadOnlyList<SimulatedDrone> all, int idx, double simTime)
    {
        if (idx < 0) idx = _roles.Count;
        var route = BuildRoute(idx, Math.Max(all.Count, 1), drone.FlightModel.State.Position);
        _roles[drone.Id] = new DroneRole(route, 0, simTime, false);
    }

    /// <summary>
    /// Builds a cyclic waypoint route for drone at index <paramref name="droneIndex"/> out of
    /// <paramref name="totalDrones"/> total drones, adapted to the current scenario and terrain preset.
    /// </summary>
    private Vector3[] BuildRoute(int droneIndex, int totalDrones, Vector3 spawnPos)
    {
        return _scenario switch
        {
            "single" => BuildLawnmowerRoute(0, 0, 500f, 200f),
            "sar" => BuildSarSectorRoute(droneIndex, totalDrones),
            _ => BuildSectorPatrolRoute(droneIndex, totalDrones),
        };
    }

    /// <summary>Lawnmower grid search over a square of half-size <paramref name="halfExtent"/>.</summary>
    private Vector3[] BuildLawnmowerRoute(float cx, float cz, float halfExtent, float step)
    {
        var pts = new List<Vector3>();
        bool ltr = true;
        for (float z = cz - halfExtent; z <= cz + halfExtent + 0.1f; z += step)
        {
            var xs = ltr
                ? Enumerable.Range(0, (int)((halfExtent * 2) / step) + 1).Select(i => cx - halfExtent + i * step)
                : Enumerable.Range(0, (int)((halfExtent * 2) / step) + 1).Select(i => cx + halfExtent - i * step);
            foreach (var x in xs)
                pts.Add(TerrainPoint(x, z));
            ltr = !ltr;
        }
        return pts.ToArray();
    }

    /// <summary>
    /// Sector patrol for swarm scenarios: divides the map into N sectors on a grid and
    /// assigns drone <paramref name="idx"/> an octagon route around its sector center.
    /// </summary>
    private Vector3[] BuildSectorPatrolRoute(int idx, int total)
    {
        // Grid layout
        int cols = (int)Math.Ceiling(Math.Sqrt(total));
        int rows = (int)Math.Ceiling((double)total / cols);
        float cellW = 1800f / cols;
        float cellH = 1800f / rows;
        int col = idx % cols;
        int row = idx / cols;
        float cx = -900f + (col + 0.5f) * cellW;
        float cz = -900f + (row + 0.5f) * cellH;

        // Terrain-specific route shape
        return _preset switch
        {
            "coastal" => BuildIslandPatrolRoute(cx, cz, Math.Min(cellW, cellH) * 0.38f),
            "canyon" => BuildCanyonCorridor(idx, total),
            "dunes" => BuildDuneSweep(cx, cz, cellW * 0.4f, cellH * 0.4f),
            "ridgeline" => BuildRidgelineRoute(cx, cz, cellW * 0.45f),
            _ => BuildOctagonRoute(cx, cz, Math.Min(cellW, cellH) * 0.40f),
        };
    }

    /// <summary>SAR: lawnmower strips divided equally among drones.</summary>
    private Vector3[] BuildSarSectorRoute(int idx, int total)
    {
        // Divide the 2000m x 2000m area into N vertical strips
        total = Math.Max(total, 1);
        float stripW = 2000f / total;
        float stripCx = -1000f + (idx + 0.5f) * stripW;
        float halfW = stripW * 0.45f;
        return BuildLawnmowerRoute(stripCx, 0f, halfW, Math.Max(halfW * 0.4f, 60f));
    }

    /// <summary>Octagon patrol of given radius around (cx, cz).</summary>
    private Vector3[] BuildOctagonRoute(float cx, float cz, float radius)
    {
        var pts = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float x = cx + radius * MathF.Cos(angle);
            float z = cz + radius * MathF.Sin(angle);
            pts[i] = TerrainPoint(x, z);
        }
        return pts;
    }

    /// <summary>Coastal: circular patrol staying over land (terrain above water level).</summary>
    private Vector3[] BuildIslandPatrolRoute(float cx, float cz, float radius)
    {
        var pts = new List<Vector3>();
        for (int i = 0; i < 12; i++)
        {
            float angle = i * MathF.PI / 6f;
            // Try radius, fall back to closer if over ocean (terrain < -1m)
            for (float r = radius; r >= 40f; r -= 20f)
            {
                float x = cx + r * MathF.Cos(angle);
                float z = cz + r * MathF.Sin(angle);
                if (_terrain.GetElevation(x, z) > -1.0)
                {
                    pts.Add(TerrainPoint(x, z));
                    break;
                }
            }
        }
        return pts.Count >= 3 ? pts.ToArray() : BuildOctagonRoute(cx, cz, radius);
    }

    /// <summary>Canyon: fly along a north-south corridor at canyon altitude, then a cross-corridor.</summary>
    private Vector3[] BuildCanyonCorridor(int idx, int total)
    {
        int cols = Math.Max(total, 1);
        float spacing = 1800f / cols;
        float cx = -900f + (idx + 0.5f) * spacing;

        // North-south sweep within the corridor, then an E-W cross at midpoint
        var pts = new List<Vector3>();
        float[] zs = { -800f, -400f, 0f, 400f, 800f };
        for (int i = 0; i < zs.Length; i++)
        {
            float x = i % 2 == 0 ? cx - spacing * 0.2f : cx + spacing * 0.2f;
            pts.Add(TerrainPoint(x, zs[i]));
        }
        return pts.ToArray();
    }

    /// <summary>Dunes: east-west sweeping lanes (perpendicular to N-S dune ridges).</summary>
    private Vector3[] BuildDuneSweep(float cx, float cz, float halfW, float halfH)
    {
        var pts = new List<Vector3>();
        float[] zOffsets = { -halfH, -halfH * 0.5f, 0f, halfH * 0.5f, halfH };
        bool ltr = true;
        foreach (var dz in zOffsets)
        {
            pts.Add(TerrainPoint(ltr ? cx - halfW : cx + halfW, cz + dz));
            pts.Add(TerrainPoint(ltr ? cx + halfW : cx - halfW, cz + dz));
            ltr = !ltr;
        }
        return pts.ToArray();
    }

    /// <summary>Ridgeline: N-S traverse across the ridgeline region at high AGL to stay above knife edges.</summary>
    private Vector3[] BuildRidgelineRoute(float cx, float cz, float halfR)
    {
        // Sample path across the ridgeline, using max terrain + extra clearance
        var pts = new List<Vector3>();
        float[] offsets = { -halfR, -halfR * 0.5f, 0f, halfR * 0.5f, halfR };
        for (int i = 0; i < offsets.Length; i++)
        {
            float x = cx + offsets[i];
            float z = cz + offsets[(offsets.Length - 1 - i)];
            // Extra 10m clearance on ridgelines — terrain is very spiky
            float y = (float)_terrain.GetElevation(x, z) + _minAgl + 10f;
            pts.Add(new Vector3(x, y, z));
        }
        return pts.ToArray();
    }

    /// <summary>Returns a terrain-clamped waypoint at (x, z) with active minAgl clearance.</summary>
    private Vector3 TerrainPoint(float x, float z)
    {
        float y = (float)_terrain.GetElevation(x, z) + _minAgl;
        return new Vector3(x, Math.Max(y, _minAgl), z);
    }
}
