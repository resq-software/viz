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

using ResQ.Simulation.Engine.Environment;

namespace ResQ.Viz.Web.Services.Assets;

// The per-step half of AssetWorld: ordering the SDK's air step against the ground and surface
// steps we own, and the frozen peer snapshot they all read. Split from the registry half so the
// file answering "what is in this world" stays separate from the one answering "how one tick
// runs"; the type's summary lives on the primary declaration in AssetWorld.cs.
public sealed partial class AssetWorld
{
    /// <summary>Advances the world by one step at the SDK clock's effective timestep.</summary>
    public void Step() => Step(_flight.Clock.EffectiveDeltaTime);

    /// <summary>Advances the world by exactly one step.</summary>
    /// <remarks>
    /// <b>How air assets avoid being stepped twice.</b> Air physics happens in exactly one
    /// place: the call to the SDK world's own <c>Step</c> on the first line, which advances the
    /// clock, steps the weather once, and integrates every non-landed drone with the wind
    /// sampled at its position. After that, this method walks <see cref="IStepDrivenAsset"/>
    /// lists only — ground, then surface. The air list is never iterated here at all, and it
    /// could not usefully be: <see cref="AirAsset"/> does not implement
    /// <see cref="IStepDrivenAsset"/>, so it cannot be placed in a step list and has no
    /// <c>Step</c> to call. The guarantee is a compile error, not a convention.
    /// <para>
    /// The weather is stepped once for the same reason. Assets receive an
    /// <see cref="IWindField"/>, which has no <c>Step</c> member, so
    /// <see cref="IWeatherSystem.Step"/> keeps exactly one call site inside the SDK. A second
    /// call would halve the effective turbulence correlation time and move every drone.
    /// </para>
    /// <para>
    /// <b>Order.</b> SDK step, then the counters, then the frozen peer buffer, then ground in
    /// spawn order, then surface in spawn order. Freezing before any integration is what stops
    /// asset <c>N</c> observing asset <c>N-1</c>'s post-step position; without it, a future
    /// interaction would make the result depend on registry order.
    /// </para>
    /// <para>
    /// <b>Zero timestep.</b> The asset pass is skipped when the timestep is not positive,
    /// mirroring the SDK's own early return while its clock is paused. The room does not pause
    /// that clock today, and a stepped clock reports a full timestep even while paused — so
    /// without this guard the two pause mechanisms would silently disagree the first time anyone
    /// touched the clock. The tick counter still advances, because an attempted step has always
    /// been counted.
    /// </para>
    /// <para>
    /// <b>Behaviour deliberately pinned, not fixed.</b> Two pre-existing quirks are load-bearing
    /// for any recorded baseline and are left exactly as they are. First, the swarm
    /// coordinator's separation force sums <c>float</c> offsets while enumerating a
    /// <c>Dictionary&lt;string, Vector3&gt;</c>: floating-point addition is not associative, so
    /// enumeration order changes the result in the last bits. Second,
    /// <c>UpdatableWeatherSystem.Update</c> builds a fresh weather system, which rewinds
    /// turbulence phase to zero on every weather change. Correcting either is a visible
    /// behaviour change rather than a cleanup, and belongs in a deliberate, separate diff.
    /// </para>
    /// </remarks>
    /// <param name="deltaSeconds">Timestep for the asset pass, in seconds. Non-positive skips it.</param>
    public void Step(double deltaSeconds)
    {
        _flight.Step();

        TickCount++;
        SimulationTimeSeconds = TickCount / SimulationTicksPerSecond;

        if (!(deltaSeconds > 0.0) || !double.IsFinite(deltaSeconds))
        {
            return;
        }

        FreezePeerPoses();
        StepDomain(_ground, deltaSeconds);
        StepDomain(_surface, deltaSeconds);
    }

    /// <summary>Refills the frozen peer-pose buffer from the registry.</summary>
    /// <remarks>
    /// Reuses one buffer rather than allocating per step, and hands assets a read-only view of
    /// it so a callee cannot cast it back to a list and mutate the world's own state.
    /// </remarks>
    private void FreezePeerPoses()
    {
        _peerPoses.Clear();

        foreach (var asset in _ordered)
        {
            _peerPoses.Add(new PeerPose(
                asset.AssetId,
                asset.Domain,
                asset.PositionEus,
                asset.Descriptor.Dimensions.FootprintRadiusM));
        }
    }

    /// <summary>Steps one pre-partitioned domain list in spawn order.</summary>
    /// <param name="assets">Assets of a single domain, in spawn order.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    private void StepDomain(List<IStepDrivenAsset> assets, double deltaSeconds)
    {
        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];

            // Sampling per asset, at its pre-step position, is the impure half of the step. The
            // asset receives a value and integrates from it, which is what lets its arithmetic
            // be exercised with literals and no world at all.
            var sample = _environment.Sample(
                asset.PositionEus, asset.Descriptor.Dimensions.FootprintRadiusM);

            var context = new AssetStepContext(
                DeltaSeconds: deltaSeconds,
                SimulationTimeSeconds: SimulationTimeSeconds,
                Tick: TickCount,
                Environment: sample,
                Peers: _peerPoseView,
                Random: _random);

            asset.Step(in context);
        }
    }
}
