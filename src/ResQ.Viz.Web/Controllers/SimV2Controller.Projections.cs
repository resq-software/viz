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

namespace ResQ.Viz.Web.Controllers;

// The wire projections of the v2 surface: expanding capability and target-kind masks into stable
// names, and naming the optional data an asset's latest report actually carries. Pure functions
// with no room, no request and no logging, kept apart from the validation gates so a change to
// how something is displayed cannot reach what is accepted.
//
// Lifting v1 detections and hazards into frame-qualified v2 shapes used to live here too. It
// moved to VizSnapshotV2Builder when the broadcast loop became a second publisher of v2 frames:
// a projection two surfaces depend on cannot sit inside one of them.
public sealed partial class SimV2Controller
{
    /// <summary>Expands a capability mask into stable names, for display and for logs.</summary>
    private static IReadOnlyList<string> CapabilityNames(AssetCapability capabilities)
    {
        var names = new List<string>();
        foreach (var value in Enum.GetValues<AssetCapability>())
        {
            if (value != AssetCapability.None && (capabilities & value) == value)
            {
                names.Add(value.ToString());
            }
        }

        return names;
    }

    /// <summary>Expands a target-shape mask into stable names.</summary>
    private static IReadOnlyList<string> TargetKindNames(CommandTargetKinds kinds)
    {
        var names = new List<string>();
        foreach (var value in Enum.GetValues<CommandTargetKinds>())
        {
            if (value != CommandTargetKinds.None && (kinds & value) == value)
            {
                names.Add(value.ToString());
            }
        }

        return names;
    }

    /// <summary>Names the optional data this asset's latest report actually carries.</summary>
    /// <remarks>
    /// Derived from the state rather than declared on the descriptor, because absent data is
    /// normal and changes over a session: a link drops its mesh path, a mission ends. A client
    /// that renders a panel for a field the asset never reports shows an empty box forever.
    /// </remarks>
    private static IReadOnlyList<string> DataFeatures(AssetState state)
    {
        var features = new List<string> { "pose", "twist", "power", "health", "link" };

        if (state.Pose.Geo is not null)
        {
            features.Add("pose.geo");
        }

        if (state.Pose.Covariance is not null)
        {
            features.Add("pose.covariance");
        }

        if (state.Twist.Covariance is not null)
        {
            features.Add("twist.covariance");
        }

        if (state.Power.Sources.Count > 0)
        {
            features.Add("power.sources");
        }

        if (state.Health.Components.Count > 0)
        {
            features.Add("health.components");
        }

        if (state.Health.Faults.Count > 0)
        {
            features.Add("health.faults");
        }

        if (state.Link.MeshPath is not null)
        {
            features.Add("link.meshPath");
        }

        if (state.Mission is not null)
        {
            features.Add("mission");
        }

        if (state.DomainState is { } domainState)
        {
            features.Add($"domain.{domainState.Type}");
        }

        return features;
    }
}
