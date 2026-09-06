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

using Microsoft.AspNetCore.Mvc;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Controllers;

// Deployment-derived spawn discovery. Kept separate from the live asset endpoints because this
// describes what the host can create without reading or mutating one room.
public sealed partial class SimV2Controller
{
    /// <summary>Lists the vehicle profiles this deployment's v2 spawn endpoint can create.</summary>
    /// <remarks>
    /// Multirotors belong to the flight world and bypass <c>IAssetFactory</c>. Every other class
    /// must have both an authoritative <see cref="AssetProfiles"/> entry and a registered factory.
    /// Discovery probes only <c>CanCreate</c>; it never constructs an asset.
    /// </remarks>
    /// <returns>Spawnable profiles in stable numeric <see cref="VehicleClass"/> order.</returns>
    [HttpGet("asset-profiles")]
    public IActionResult GetAssetProfiles()
    {
        var profiles = Enum.GetValues<VehicleClass>()
            .OrderBy(vehicleClass => (int)vehicleClass)
            .Where(IsSpawnable)
            .Select(vehicleClass =>
            {
                var domain = AssetProfiles.DomainFor(vehicleClass);
                return new AssetSpawnProfile(
                    vehicleClass,
                    domain,
                    DisplayNameFor(vehicleClass),
                    HeadingApplies: domain != AssetDomain.Air);
            })
            .ToList();

        return Ok(new AssetProfileCatalogResponse(profiles));
    }

    private bool IsSpawnable(VehicleClass vehicleClass) =>
        vehicleClass == VehicleClass.Multirotor
        || (vehicleClass != VehicleClass.Unspecified
            && AssetProfiles.IsSupported(vehicleClass)
            && _factories.Any(factory => factory.CanCreate(vehicleClass)));

    private static string DisplayNameFor(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.Multirotor => "Multirotor",
        VehicleClass.AckermannRover => "Ackermann rover",
        VehicleClass.DifferentialRover => "Differential rover",
        VehicleClass.TrackedRover => "Tracked rover",
        VehicleClass.SurfaceVessel => "Surface vessel",
        _ => vehicleClass.ToString(),
    };
}
