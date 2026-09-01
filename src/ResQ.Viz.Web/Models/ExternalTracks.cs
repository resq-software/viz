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

namespace ResQ.Viz.Web.Models;

/// <summary>How a contributing observation of an external track was obtained.</summary>
/// <remarks>
/// Carried per source rather than once per track because a fused track routinely mixes a
/// cooperative report with a non-cooperative one, and the two decay differently: a
/// transponder report is identity-bearing and sparse, a radar plot is anonymous and dense.
/// </remarks>
public enum TrackSourceKind
{
    /// <summary>Source not reported.</summary>
    Unknown,

    /// <summary>Cooperative broadcast identity (ADS-B, AIS, Remote ID and similar).</summary>
    Transponder,

    /// <summary>Non-cooperative radar return.</summary>
    Radar,

    /// <summary>Electro-optical or infrared detection.</summary>
    Optical,

    /// <summary>Acoustic detection.</summary>
    Acoustic,

    /// <summary>Track supplied wholesale by a third-party feed or partner system.</summary>
    ExternalFeed,

    /// <summary>Entered by an operator from a voice report or visual sighting.</summary>
    OperatorEntered,
}

/// <summary>What an external track is believed to be.</summary>
/// <remarks>
/// Deliberately coarser than <see cref="VehicleClass"/>: a track is something observed, not
/// something modelled, and claiming to know a contact's exact airframe from a radar return
/// would be a fiction the rest of the system would then plan against.
/// </remarks>
public enum TrackClassification
{
    /// <summary>Nothing is known about what the contact is.</summary>
    Unknown = 0,

    /// <summary>Observed but deliberately not yet assigned a class.</summary>
    Unclassified = 1,

    /// <summary>Fixed-wing or unspecified aircraft.</summary>
    Aircraft = 2,

    /// <summary>Rotorcraft.</summary>
    Rotorcraft = 3,

    /// <summary>Small uncrewed aircraft.</summary>
    SmallUnmannedAircraft = 4,

    /// <summary>Vessel on the water surface.</summary>
    Vessel = 5,

    /// <summary>Road or off-road ground vehicle.</summary>
    GroundVehicle = 6,

    /// <summary>Person on foot.</summary>
    Person = 7,

    /// <summary>Static obstacle: a mast, crane, wire or structure.</summary>
    Obstacle = 8,

    /// <summary>Classified, but as none of the above.</summary>
    Other = 9,
}

/// <summary>Family of cooperative broadcast an external track's identity came from.</summary>
public enum TransponderKind
{
    /// <summary>Not a cooperative report, or the family is unknown.</summary>
    Unknown,

    /// <summary>Automatic Dependent Surveillance-Broadcast.</summary>
    AdsB,

    /// <summary>Universal Access Transceiver.</summary>
    Uat,

    /// <summary>Automatic Identification System (maritime).</summary>
    Ais,

    /// <summary>Broadcast uncrewed-aircraft Remote ID.</summary>
    RemoteId,

    /// <summary>Some other cooperative identity broadcast.</summary>
    Other,
}

/// <summary>One sensor or feed contributing observations to an external track.</summary>
/// <param name="SourceId">Stable identifier of the contributing sensor or feed.</param>
/// <param name="Kind">How this source observes.</param>
/// <param name="ObservedAt">When this source last contributed an observation to the track.</param>
/// <param name="Quality">
/// Normalised confidence this source has in its own contribution, in 0–1. Null when the
/// source reports none; absent must stay distinguishable from zero.
/// </param>
public sealed record TrackSource(
    string SourceId,
    TrackSourceKind Kind,
    DateTimeOffset ObservedAt,
    double? Quality = null);

/// <summary>How well an external track is resolved.</summary>
/// <remarks>
/// Accuracy fields are nullable rather than defaulted because a fabricated accuracy is worse
/// than none: a consumer that sees 0 m will draw a point where it should draw a circle.
/// </remarks>
/// <param name="Confidence">Overall confidence the track corresponds to a real object, in 0–1.</param>
/// <param name="PositionAccuracyM">One-sigma horizontal position accuracy in metres, if reported.</param>
/// <param name="VelocityAccuracyMps">One-sigma velocity accuracy in metres per second, if reported.</param>
/// <param name="UpdateCount">Number of observations fused into this track so far.</param>
/// <param name="IsFused">True when more than one source contributed; a fused track can disagree with any single source.</param>
public sealed record TrackQuality(
    double Confidence,
    double? PositionAccuracyM = null,
    double? VelocityAccuracyMps = null,
    int UpdateCount = 0,
    bool IsFused = false);

/// <summary>Cooperative broadcast identity attached to an external track.</summary>
/// <remarks>
/// Optional on the track, because non-cooperative contacts are the interesting ones and a
/// model that assumes identity is present would have to invent it. Fields are named
/// neutrally so an aviation identity and a maritime one share the record instead of forcing
/// two near-identical shapes onto the wire.
/// </remarks>
/// <param name="Kind">Which cooperative broadcast family this identity came from.</param>
/// <param name="Identifier">Primary broadcast identifier (ICAO 24-bit address, MMSI, Remote ID serial).</param>
/// <param name="CallSign">Broadcast call sign or vessel name, for display.</param>
/// <param name="Code">Secondary code where the family has one, e.g. a squawk.</param>
/// <param name="Registration">Registration or hull marking, when broadcast.</param>
/// <param name="NavigationStatus">Broadcast status string (e.g. "under-way", "at-anchor"). Render it; do not branch on it.</param>
/// <param name="Operator">Operating organisation, when broadcast.</param>
public sealed record TransponderIdentity(
    TransponderKind Kind,
    string Identifier,
    string? CallSign = null,
    string? Code = null,
    string? Registration = null,
    string? NavigationStatus = null,
    string? Operator = null);

/// <summary>A contact we observe but do not control.</summary>
/// <remarks>
/// An external track is deliberately <b>not</b> an <see cref="AssetState"/>. It has a pose and
/// a classification, and that is where the resemblance stops.
/// <para>
/// There is no <c>Capabilities</c> field and no command endpoint accepts a track id. That
/// absence is the safety property, not an omission to be filled in later: capability is what
/// every command gate keys on (see <see cref="AssetCapability"/>), so a type that has none can
/// never pass validation, and a UI that binds command affordances to declared capabilities has
/// nothing to bind to. Giving a track capabilities — even an empty set — would turn "we cannot
/// command this" into "we happen not to be commanding this today".
/// </para>
/// <para>
/// Tracks are therefore carried in their own list on <see cref="VizSnapshotV2"/> rather than
/// mixed into <see cref="VizSnapshotV2.Assets"/> with a flag, because a flag is something a
/// caller can forget to check.
/// </para>
/// </remarks>
/// <param name="TrackId">
/// Stable identifier for this track within the session. Distinct from any
/// <see cref="AssetDescriptor.AssetId"/>; the two id spaces must never be joined.
/// </param>
/// <param name="Classification">What the contact is believed to be.</param>
/// <param name="Pose">Frame-qualified position and orientation of the contact.</param>
/// <param name="Twist">
/// Frame-qualified velocity. Present even for a stationary contact so consumers do not have
/// to distinguish "not moving" from "motion not reported" — that distinction lives in
/// <paramref name="Quality"/> and <paramref name="Freshness"/>.
/// </param>
/// <param name="Sources">Sources contributing to the track, most recently updated first. Never empty.</param>
/// <param name="Quality">How well the track is resolved.</param>
/// <param name="LastUpdateTime">When the track was last updated from any source.</param>
/// <param name="Freshness">How far the track can still be trusted, on the same scale assets use.</param>
/// <param name="Label">Operator-facing label. Display only; never parsed.</param>
/// <param name="Transponder">Cooperative identity, or null for a non-cooperative contact.</param>
public sealed record ExternalTrackState(
    string TrackId,
    TrackClassification Classification,
    FramedPose Pose,
    FramedTwist Twist,
    IReadOnlyList<TrackSource> Sources,
    TrackQuality Quality,
    DateTimeOffset LastUpdateTime,
    DataFreshness Freshness = DataFreshness.Unknown,
    string? Label = null,
    TransponderIdentity? Transponder = null);
