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
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Entities;
using ResQ.Simulation.Engine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace ResQ.Viz.Web.Tests;

/// <summary>Scratch probe; deleted after the critique.</summary>
public sealed class ZzCriticProbe(ITestOutputHelper output)
{
    /// <summary>Probe.</summary>
    [Fact]
    public void Probe()
    {
        var drone = new SimulatedDrone("air-1", new Vector3(0f, 40f, 0f), FlightModelType.Kinematic);
        var asset = new AirAsset(drone, AssetProfiles.Create(drone.Id, VehicleClass.Multirotor));

        var sk = asset.Apply(new SimulatedAssetCommand(AssetCommandKind.StationKeep, drone.Id));
        output.WriteLine($"AIR StationKeep -> accepted={sk.IsAccepted} reason={sk.Reason ?? "<null>"}");

        foreach (var definition in CommandCatalog.All)
        {
            var kind = AssetCommandTranslator.ToAssetCommandKind(definition.Kind);
            var applies = definition.AppliesTo(AssetDomain.Air);
            var r = asset.Apply(new SimulatedAssetCommand(kind, drone.Id));
            output.WriteLine($"  {definition.Kind,-16} kind={kind,-16} appliesToAir={applies,-5} accepted={r.IsAccepted,-5} reason={r.Reason ?? "<null>"}");
        }

        output.WriteLine("=== footprint radii ===");
        foreach (var vc in Enum.GetValues<VehicleClass>())
        {
            if (!AssetProfiles.IsSupported(vc)) { continue; }
            var d = AssetProfiles.DimensionsFor(vc);
            var computed = 0.5 * Math.Sqrt((d.LengthM * d.LengthM) + (d.WidthM * d.WidthM));
            var g = GroundProfile.ForVehicleClass(vc);
            var s = SurfaceProfile.ForVehicleClass(vc);
            output.WriteLine($"  {vc,-20} declared={d.FootprintRadiusM,-8} computed={computed} ground={(g?.FootprintRadiusM.ToString() ?? "-")} surface={(s?.FootprintRadiusM.ToString() ?? "-")}");
            if (g is not null) { output.WriteLine($"      groundLW=({g.FootprintLengthM},{g.FootprintWidthM}) descLW=({d.LengthM},{d.WidthM})"); }
            if (s is not null) { output.WriteLine($"      surfLB=({s.LengthM},{s.BeamM}) descLW=({d.LengthM},{d.WidthM})"); }
        }

        output.WriteLine("=== modelled but unspawnable ===");
        foreach (var vc in Enum.GetValues<VehicleClass>())
        {
            var sup = AssetProfiles.IsSupported(vc);
            var g = GroundProfile.ForVehicleClass(vc) is not null;
            var s = SurfaceProfile.ForVehicleClass(vc) is not null;
            output.WriteLine($"  {vc,-20} ({(int)vc,2}) supported={sup,-5} ground={g,-5} surface={s}");
        }
    }
}
