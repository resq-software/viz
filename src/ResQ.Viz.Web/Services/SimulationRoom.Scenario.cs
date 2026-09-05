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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

// The room-owned scenario publication. Scenario identity and the swarm policy it selects are
// changed under the same lock, so a frame cannot publish a new name beside the old policy.
public sealed partial class SimulationRoom
{
    private ScenarioSessionState? _scenario;
    private long _scenarioRevision;

    /// <summary>Publishes a successfully loaded scenario and updates the swarm policy atomically.</summary>
    /// <remarks>
    /// The key is retained as well as forwarded, because a frame has to be able to say which
    /// preset it belongs to. Hazards used to be one deployment-wide list republished on every
    /// frame of every scenario, so a flood ran with the same standing fire as a wildfire and its
    /// marker sat burning in open water. Carrying the key on the capture is what lets the frame
    /// builder pick the set that belongs to the preset actually running.
    /// </remarks>
    /// <param name="name">Configured scenario name.</param>
    public void NotifyScenario(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_lock)
        {
            _scenarioKey = name;
            _swarm.SetScenario(name, _assets.Drones.ToList());
            _scenario = new ScenarioSessionState(
                name,
                _assets.SimulationTimeSeconds,
                ++_scenarioRevision);
        }
        Touch();
    }

    /// <summary>Clears the active scenario while preserving the monotonic revision.</summary>
    /// <remarks>Call only while holding <c>_lock</c>.</remarks>
    private void ClearScenario()
    {
        _scenarioRevision++;
        _scenario = null;
    }
}
