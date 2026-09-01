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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>Builds the rovers this deployment can simulate, from a validated spawn plan.</summary>
/// <remarks>
/// The whole of the ground domain's wiring. Registering an instance makes
/// <c>POST /api/v2/sim/assets</c> accept every class <see cref="CanCreate"/> answers for;
/// leaving it unregistered makes those classes refuse with
/// <see cref="AssetProblems.MobilityModelUnavailable"/>. Nothing else in the system switches on
/// vehicle class to reach a ground model.
/// <para>
/// <b>Why the environment arrives as a delegate.</b> A rover is settled onto the terrain by its
/// own constructor, so it needs an <see cref="IEnvironmentSampler"/> before it exists — and the
/// only correct sampler is the one belonging to the room being spawned into, which
/// <see cref="AssetSpawnPlan"/> deliberately does not carry. Resolving it per call rather than
/// per registration is what lets one registered factory serve every room in the process while
/// still binding each rover to its own room's terrain, weather and water level. A factory that
/// captured a single sampler at construction would settle every session's rovers onto the first
/// session's hillside.
/// </para>
/// <para>
/// The classes here are exactly the ones <see cref="GroundProfile.ForVehicleClass"/> describes,
/// which includes <see cref="VehicleClass.LeggedRover"/>. That class has no
/// <see cref="AssetProfiles"/> row, so the API refuses it earlier with
/// <see cref="AssetProblems.VehicleClassUnsupported"/> and it is reachable only by a caller that
/// builds its own descriptor. Answering for it anyway keeps one question — "is this a ground
/// vehicle?" — with one answer, rather than two tables that can quietly disagree.
/// </para>
/// </remarks>
public sealed class GroundAssetFactory : IAssetFactory
{
    private readonly Func<IEnvironmentSampler> _environment;

    /// <summary>Builds rovers against one already-resolved sampler.</summary>
    /// <remarks>
    /// For a caller that already holds the room's world — a scenario runner, a test — and would
    /// otherwise be writing a closure that returns a constant.
    /// </remarks>
    /// <param name="environment">Sampler every rover from this factory settles against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public GroundAssetFactory(IEnvironmentSampler environment)
        : this(Fixed(environment))
    {
    }

    /// <summary>Builds rovers against the sampler resolved at the moment of each spawn.</summary>
    /// <remarks>
    /// The registration form. The delegate is invoked once per <see cref="Create"/> and its
    /// result is never stored, so a process-lifetime factory holds no reference to any room.
    /// </remarks>
    /// <param name="environment">Resolves the sampler for the room being spawned into. Must not return null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public GroundAssetFactory(Func<IEnvironmentSampler> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <inheritdoc />
    public bool CanCreate(VehicleClass vehicleClass) =>
        GroundProfile.ForVehicleClass(vehicleClass) is not null;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// The plan's descriptor describes a different vehicle class from the one it asks for. The
    /// dynamics come from the class and the capability mask comes from the descriptor, so a
    /// disagreement between them builds a vehicle that drives like one platform and is commanded
    /// as another — silently, which is why this throws rather than picking a side.
    /// </exception>
    /// <exception cref="InvalidOperationException">The environment resolver returned no sampler.</exception>
    public ISimulatedAsset Create(in AssetSpawnPlan plan)
    {
        var profile = GroundProfile.ForVehicleClass(plan.VehicleClass)
            ?? throw new ArgumentOutOfRangeException(
                nameof(plan),
                plan.VehicleClass,
                $"'{plan.VehicleClass}' has no ground motion model; ask CanCreate first.");

        if (plan.Descriptor.VehicleClass != plan.VehicleClass)
        {
            throw new ArgumentException(
                $"The plan asks for '{plan.VehicleClass}' but carries a descriptor for "
                + $"'{plan.Descriptor.VehicleClass}'.",
                nameof(plan));
        }

        var environment = _environment()
            ?? throw new InvalidOperationException(
                "A ground asset needs an environment sampler; the resolver returned none.");

        return new GroundAsset(
            plan.Descriptor,
            GroundDynamics.For(profile),
            environment,
            plan.PositionEus,
            plan.HeadingRad);
    }

    /// <summary>Wraps one sampler as a resolver, validating it eagerly.</summary>
    /// <remarks>
    /// Eagerly, so passing null fails at construction naming the parameter, rather than at the
    /// first spawn as a resolver that mysteriously produced nothing.
    /// </remarks>
    /// <param name="environment">Sampler to wrap.</param>
    /// <returns>A resolver that always yields <paramref name="environment"/>.</returns>
    private static Func<IEnvironmentSampler> Fixed(IEnvironmentSampler environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return () => environment;
    }
}
