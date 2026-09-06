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

namespace ResQ.Viz.Web.Models;

/// <summary>One vehicle profile this deployment can spawn through the v2 asset endpoint.</summary>
/// <param name="VehicleClass">Mobility archetype accepted by the spawn endpoint.</param>
/// <param name="Domain">Medium derived from the authoritative asset profile.</param>
/// <param name="DisplayName">Stable operator-facing label for the vehicle class.</param>
/// <param name="HeadingApplies">Whether the spawn form should collect an initial heading.</param>
public sealed record AssetSpawnProfile(
    VehicleClass VehicleClass,
    AssetDomain Domain,
    string DisplayName,
    bool HeadingApplies);

/// <summary>The spawn profiles available in this deployment, in stable numeric class order.</summary>
/// <param name="Profiles">Profiles accepted by <c>POST /api/v2/sim/assets</c>.</param>
public sealed record AssetProfileCatalogResponse(IReadOnlyList<AssetSpawnProfile> Profiles);
