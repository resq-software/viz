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

using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Controllers;

// The wire projections of the v2 surface: expanding capability and target-kind masks into stable
// names, and lifting v1 detections and hazards into frame-qualified v2 shapes. Pure functions
// with no room, no request and no logging, kept apart from the validation gates so a change to
// how something is displayed cannot reach what is accepted.
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

    /// <summary>Lifts a v1 detection into the frame-qualified v2 shape.</summary>
    /// <remarks>
    /// The reporting field becomes <see cref="DetectionV2State.SourceAssetId"/>: the v1 producer
    /// only ever attributes to a drone, but the field name is no longer an assumption baked into
    /// the contract, so a rover or a vessel reporting one needs no wire change.
    /// </remarks>
    private static DetectionV2State ToDetectionV2(DetectionVizState detection, DateTimeOffset detectedAt) =>
        new(
            DetectionId: detection.Id,
            Type: detection.Type,
            Pose: SceneFramePose(detection.Pos),
            SourceAssetId: detection.DroneId,
            Confidence: Math.Clamp(detection.Confidence, 0.0, 1.0),
            DetectedAt: detectedAt);

    /// <summary>Lifts a v1 hazard zone into the frame-qualified v2 shape.</summary>
    /// <remarks>
    /// The v1 severity is a free string, so it is parsed rather than cast, and an unrecognised
    /// value becomes <see cref="HazardSeverity.Unknown"/> instead of a silently wrong level.
    /// <paramref name="hazard"/> declares no affected domains, and null means "assume it affects
    /// everything" — the safe reading when the source does not say.
    /// </remarks>
    private static HazardV2State ToHazardV2(HazardVizState hazard) =>
        new(
            HazardId: hazard.Id,
            Type: hazard.Type,
            Centre: SceneFramePose(hazard.Center),
            RadiusM: hazard.Radius,
            Severity: Enum.TryParse<HazardSeverity>(hazard.Severity, ignoreCase: true, out var severity)
                ? severity
                : HazardSeverity.Unknown,
            AffectedDomains: null);

    /// <summary>Wraps a v1 position array as a scene-frame pose with no rotation.</summary>
    /// <remarks>
    /// The scene frame is the frame v1 always meant and never said; stamping it here is the
    /// whole of the v1-to-v2 lift for a point. A malformed array becomes the origin rather than
    /// throwing, matching how the v1 hazard builder already handles one.
    /// </remarks>
    private static FramedPose SceneFramePose(float[]? components) =>
        new(
            Frame: CoordinateFrame.LocalEus,
            OriginId: null,
            Position: components is { Length: 3 }
                ? new Vector3(components[0], components[1], components[2])
                : Vector3.Zero,
            Orientation: Quaternion.Identity);
}
