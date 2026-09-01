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

namespace ResQ.Viz.Web.Services;

/// <summary>Everything a factory needs to build one asset, with every value already validated.</summary>
/// <remarks>
/// The API boundary does the parts that need a request: it resolves the coordinate frame,
/// range-checks the coordinates, mints or vets the identifier and builds the descriptor from
/// <see cref="AssetProfiles"/>. A factory therefore receives a plan it can trust and only has
/// to construct the motion model, which is the part it alone knows about.
/// <para>
/// Passing the descriptor in rather than letting each factory build its own is what keeps
/// <see cref="AssetProfiles"/> the single place capabilities and motion limits are decided.
/// A factory that minted its own capability mask could hand a rover
/// <see cref="AssetCapability.Takeoff"/> without anything else in the system noticing.
/// </para>
/// </remarks>
/// <param name="AssetId">Identifier the asset will be registered under; already checked for uniqueness.</param>
/// <param name="VehicleClass">Mobility archetype to build.</param>
/// <param name="Descriptor">Descriptor built from the class profile plus the caller's metadata.</param>
/// <param name="PositionEus">Spawn position in the scene frame (<see cref="CoordinateFrame.LocalEus"/>), in metres.</param>
/// <param name="HeadingRad">
/// Initial heading in radians clockwise from true north, derived from the request's orientation.
/// Zero when the request declared no meaningful orientation, which is a request an asset with no
/// heading authority may ignore.
/// </param>
public readonly record struct AssetSpawnPlan(
    string AssetId,
    VehicleClass VehicleClass,
    AssetDescriptor Descriptor,
    Vector3 PositionEus,
    double HeadingRad);

/// <summary>Builds the simulated assets the flight world does not own.</summary>
/// <remarks>
/// Air assets never come through here: their lifetime belongs to the SDK's own world, which
/// <see cref="AssetWorld.AddDrone"/> is the only correct way into. This seam exists for ground
/// and surface assets, whose motion models live on our side of the submodule boundary.
/// <para>
/// Resolved from dependency injection as <c>IEnumerable&lt;IAssetFactory&gt;</c>, so a
/// deployment with no ground or surface models registered simply refuses those classes with
/// <see cref="AssetProblems.MobilityModelUnavailable"/> instead of failing to start. Registering
/// an implementation is the whole of the wiring: nothing switches on vehicle class outside the
/// factory's own <see cref="CanCreate"/>.
/// </para>
/// </remarks>
public interface IAssetFactory
{
    /// <summary>Whether this factory can build <paramref name="vehicleClass"/>.</summary>
    /// <remarks>
    /// Must be a pure function of its argument: the API boundary calls it to pick a factory
    /// before it has committed to anything, and a probe with a side effect would leave state
    /// behind for a class it then refused.
    /// </remarks>
    /// <param name="vehicleClass">Class the caller wants to spawn.</param>
    /// <returns><see langword="true"/> when <see cref="Create"/> will succeed for that class.</returns>
    bool CanCreate(VehicleClass vehicleClass);

    /// <summary>Builds one asset from a validated plan.</summary>
    /// <remarks>
    /// The returned asset is not yet registered and has not been stepped. Ground and surface
    /// assets should implement <see cref="IStepDrivenAsset"/>; one that does not will be
    /// published but never integrated, which looks like a frozen vehicle rather than an error.
    /// </remarks>
    /// <param name="plan">Identifier, descriptor, spawn pose and heading, all already validated.</param>
    /// <returns>The constructed asset, ready to register with <see cref="AssetWorld.AddAsset"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AssetSpawnPlan.VehicleClass"/> is one this factory does not build; callers are
    /// expected to have asked <see cref="CanCreate"/> first.
    /// </exception>
    ISimulatedAsset Create(in AssetSpawnPlan plan);
}
