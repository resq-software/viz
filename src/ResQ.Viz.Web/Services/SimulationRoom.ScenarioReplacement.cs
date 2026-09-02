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

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

/// <summary>A candidate scenario population that is not visible until its room commits it.</summary>
/// <remarks>
/// Internal so no caller can retain the candidate or its world. The scenario service receives it
/// only inside <see cref="SimulationRoom.TryReplaceScenario"/>, while the room lock is held.
/// </remarks>
internal sealed class ScenarioPopulationBuilder(AssetWorld world)
{
    private readonly AssetWorld _world = world;

    /// <summary>Adds one air asset to the candidate world.</summary>
    internal void AddDrone(string id, Vector3 position, string? vendor) =>
        _world.AddDrone(id, position, vendor);

    /// <summary>Builds and adds one non-air asset against the candidate environment.</summary>
    internal void AddAsset(string assetId, Func<IEnvironmentSampler, ISimulatedAsset> build)
    {
        var asset = build(_world.Environment)
            ?? throw new InvalidOperationException($"The factory for '{assetId}' returned no asset.");
        if (!string.Equals(asset.AssetId, assetId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The factory was asked for '{assetId}' but built '{asset.AssetId}'.");
        }

        _world.AddAsset(asset);
    }
}

// Scenario replacement belongs to the room because the room owns both the lock and every piece of
// state reset changes. A controller/service sequence cannot make the same promise across calls.
public sealed partial class SimulationRoom
{
    /// <summary>Stages and atomically commits a complete scenario population.</summary>
    /// <remarks>
    /// The candidate world shares this room's current terrain and weather, preserving manual
    /// environment changes, but remains unreachable from every public room read until staging has
    /// succeeded. Factory construction runs under the room lock against the candidate sampler, so
    /// it cannot race a terrain or heightmap change. A staging exception discards only the
    /// candidate; the published world, scenario, transport, contacts and command log remain intact.
    /// </remarks>
    /// <param name="name">Canonical configured scenario name.</param>
    /// <param name="stage">Populates the candidate world. It must not call back into this room.</param>
    /// <param name="committed">Exact scenario state committed with the candidate population.</param>
    /// <returns><see langword="true"/> when the candidate was committed.</returns>
    internal bool TryReplaceScenario(
        string name,
        Action<ScenarioPopulationBuilder> stage,
        [NotNullWhen(true)] out ScenarioSessionState? committed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stage);

        ScenarioSessionState? next;
        long worldRevision;
        lock (_lock)
        {
            AssetWorld candidate;
            SwarmCoordinator candidateSwarm;
            try
            {
                candidate = CreateWorld();
                var previousEnvironment = _spawningEnvironment;
                _spawningEnvironment = candidate.Environment;
                try
                {
                    stage(new ScenarioPopulationBuilder(candidate));
                }
                finally
                {
                    _spawningEnvironment = previousEnvironment;
                }

                // Scenario policy is staged too. Mutating the live coordinator before the world
                // swap would make a failed replacement change how the old fleet is flown.
                candidateSwarm = new SwarmCoordinator(_terrain);
                candidateSwarm.SetScenario(name, candidate.Drones);
                candidateSwarm.SetTerrainPreset(_terrainPreset, _terrain, candidate.Drones);
            }
            catch (Exception)
            {
                committed = null;
                return false;
            }

            // Nothing below can invoke scenario data or a factory. The fallible work is complete,
            // so the first write to public room state is the candidate-world swap itself.
            _assets = candidate;
            _swarm = candidateSwarm;
            _swarmTick = 0;

            // A replacement is the same two authoritative transitions Reset + NotifyScenario
            // published before this method existed: clear, then activate. Neither intermediate
            // state is externally visible, but preserving both revision steps keeps reconnect
            // and delta consumers monotonic across old and new servers.
            _scenarioRevision += 2;
            next = new ScenarioSessionState(name, candidate.SimulationTimeSeconds, _scenarioRevision);
            _scenario = next;

            ClearAssetEventBuffer();
            ClearTracks();
            _commands.Clear();
            _environmentRevision++;
            _backhaulKilled = false;
            _paused = false;
            _speed = 1;
            _pendingSteps = 0;
            _broadcastTick = 0;
            worldRevision = ++_worldRevision;
        }

        // The population and scenario are already committed. Notifications stay outside the room
        // lock because authority observers call back into the room; the state returned below is the
        // local committed value, not a second reading that another replacement can overtake.
        NotifyWorldReset(worldRevision);
        Touch();
        committed = next;
        return true;
    }
}
