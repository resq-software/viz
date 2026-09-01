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

namespace ResQ.Viz.Web.Services.Assets.Surface;

/// <summary>Builds the surface vessels this deployment can simulate, from a validated spawn plan.</summary>
/// <remarks>
/// The whole of the surface domain's wiring, and the exact counterpart of
/// <see cref="Ground.GroundAssetFactory"/>. Registering an instance makes
/// <c>POST /api/v2/sim/assets</c> accept every class <see cref="CanCreate"/> answers for and
/// makes a maritime preset spawn rather than skip; leaving it unregistered makes those classes
/// refuse with <see cref="AssetProblems.MobilityModelUnavailable"/>. Nothing else in the system
/// switches on vehicle class to reach a surface model.
/// <para>
/// <b>Why the environment arrives as a delegate.</b> A vessel is floated onto the water surface
/// by its own constructor — it discards the requested height, reads the water level in force and
/// probes the bed for its under-keel clearance — so it needs an <see cref="IEnvironmentSampler"/>
/// before it exists. The only correct sampler is the one belonging to the room being spawned
/// into, which <see cref="AssetSpawnPlan"/> deliberately does not carry, and the water level is
/// per-room because it travels with that room's terrain preset. Resolving the sampler per call
/// rather than per registration is what lets one registered factory serve every room in the
/// process while still binding each vessel to its own room's bathymetry, weather and sea level.
/// A factory that captured a single sampler at construction would float every session's vessels
/// on the first session's water.
/// </para>
/// <para>
/// The classes here are exactly the ones <see cref="SurfaceProfile.ForVehicleClass"/> describes,
/// which includes <see cref="VehicleClass.Sailboat"/>. That class has no
/// <see cref="AssetProfiles"/> row, so the API refuses it earlier with
/// <see cref="AssetProblems.VehicleClassUnsupported"/> and it is reachable only by a caller that
/// builds its own descriptor — the same arrangement <see cref="VehicleClass.LeggedRover"/> is in
/// on the ground side. Answering for it anyway keeps one question — "is this a surface vessel?" —
/// with one answer, rather than two tables that can quietly disagree. It is also honest about
/// what it builds: <see cref="SurfaceProfile.Sailboat"/> is a displacement hull wearing a sailing
/// hull's envelope and is not a sail model, which its own documentation states at length.
/// </para>
/// </remarks>
public sealed class SurfaceAssetFactory : IAssetFactory
{
    private readonly Func<IEnvironmentSampler> _environment;

    /// <summary>Builds vessels against one already-resolved sampler.</summary>
    /// <remarks>
    /// For a caller that already holds the room's world — a scenario runner, a test — and would
    /// otherwise be writing a closure that returns a constant.
    /// </remarks>
    /// <param name="environment">Sampler every vessel from this factory floats against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public SurfaceAssetFactory(IEnvironmentSampler environment)
        : this(Fixed(environment))
    {
    }

    /// <summary>Builds vessels against the sampler resolved at the moment of each spawn.</summary>
    /// <remarks>
    /// The registration form. The delegate is invoked once per <see cref="Create"/> and its
    /// result is never stored, so a process-lifetime factory holds no reference to any room.
    /// </remarks>
    /// <param name="environment">Resolves the sampler for the room being spawned into. Must not return null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public SurfaceAssetFactory(Func<IEnvironmentSampler> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <inheritdoc />
    public bool CanCreate(VehicleClass vehicleClass) =>
        SurfaceProfile.ForVehicleClass(vehicleClass) is not null;

    /// <inheritdoc />
    /// <remarks>
    /// A vessel whose spawn point is dry land, or water too shallow for its draft, is still
    /// built: it is placed there, reports itself aground, and goes on accepting every command
    /// that would work it off. Throwing instead would abort a scenario run part way through
    /// building a world, and a partially-spawned world is worse than one bad vehicle because
    /// nothing about it says which half is missing. A preset that stages a boat on a hillside is
    /// a bad preset, not a reason for the host to fail.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The plan's descriptor describes a different vehicle class from the one it asks for. The
    /// dynamics come from the class and the capability mask comes from the descriptor, so a
    /// disagreement between them builds a vessel that handles like one hull and is commanded as
    /// another — silently, which is why this throws rather than picking a side.
    /// </exception>
    /// <exception cref="InvalidOperationException">The environment resolver returned no sampler.</exception>
    public ISimulatedAsset Create(in AssetSpawnPlan plan)
    {
        var profile = SurfaceProfile.ForVehicleClass(plan.VehicleClass)
            ?? throw new ArgumentOutOfRangeException(
                nameof(plan),
                plan.VehicleClass,
                $"'{plan.VehicleClass}' has no surface motion model; ask CanCreate first.");

        if (plan.Descriptor.VehicleClass != plan.VehicleClass)
        {
            throw new ArgumentException(
                $"The plan asks for '{plan.VehicleClass}' but carries a descriptor for "
                + $"'{plan.Descriptor.VehicleClass}'.",
                nameof(plan));
        }

        var environment = _environment()
            ?? throw new InvalidOperationException(
                "A surface asset needs an environment sampler; the resolver returned none.");

        return new SurfaceAsset(
            plan.Descriptor,
            SurfaceDynamics.For(profile),
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
