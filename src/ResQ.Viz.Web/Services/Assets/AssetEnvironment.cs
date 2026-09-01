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
using ResQ.Simulation.Engine.Environment;

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>Read-only view of the atmosphere, with no way to advance it.</summary>
/// <remarks>
/// This exists solely to put <see cref="IWeatherSystem.Step"/> out of an asset's reach. The
/// SDK's world steps the weather once per world step, inside its own <c>Step()</c>; a second
/// call from an asset would halve the effective turbulence correlation time and perturb every
/// drone trajectory. Placement alone would prevent that, but placement is a convention and a
/// missing method is a compiler error, so the guarantee is made structural.
/// </remarks>
public interface IWindField
{
    /// <summary>Atmospheric visibility as a normalised scalar in [0, 1].</summary>
    double Visibility { get; }

    /// <summary>Precipitation intensity as a normalised scalar in [0, 1].</summary>
    double Precipitation { get; }

    /// <summary>Wind velocity at a scene-frame position, in metres per second.</summary>
    /// <param name="x">East coordinate in metres.</param>
    /// <param name="y">Up coordinate in metres.</param>
    /// <param name="z">South coordinate in metres.</param>
    /// <returns>Wind velocity in <see cref="Models.CoordinateFrame.LocalEus"/>.</returns>
    Vector3 GetWind(double x, double y, double z);
}

/// <summary>Narrows an <see cref="IWeatherSystem"/> down to its read-only surface.</summary>
/// <remarks>
/// Holds the weather system by reference and forwards three members. It deliberately does not
/// forward <see cref="IWeatherSystem.Step"/> — see <see cref="IWindField"/> for why.
/// </remarks>
public sealed class WeatherWindField : IWindField
{
    private readonly IWeatherSystem _weather;

    /// <summary>Wraps <paramref name="weather"/> in a step-less view.</summary>
    /// <param name="weather">Weather system owned by the room; stepped only by the SDK world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="weather"/> is null.</exception>
    public WeatherWindField(IWeatherSystem weather) =>
        _weather = weather ?? throw new ArgumentNullException(nameof(weather));

    /// <inheritdoc />
    public double Visibility => _weather.Visibility;

    /// <inheritdoc />
    public double Precipitation => _weather.Precipitation;

    /// <inheritdoc />
    public Vector3 GetWind(double x, double y, double z) => _weather.GetWind(x, y, z);
}

/// <summary>An operating restriction that applies at a point in the world.</summary>
/// <remarks>
/// Advisory decision support. A zone raises a warning and derates a speed ceiling; it does not
/// assert compliance with any navigation regulation and must never be presented as doing so.
/// </remarks>
/// <param name="ZoneId">Stable identifier of the zone.</param>
/// <param name="Kind">Zone classification for display and filtering (e.g. "restricted", "shallow", "no-wake").</param>
/// <param name="IsEntryProhibited">True when an asset should not enter the zone at all.</param>
/// <param name="SpeedLimitMps">Speed ceiling inside the zone, in metres per second, or null when unrestricted.</param>
/// <param name="Advisory">Operator-facing note. Render it; never branch on it.</param>
public sealed record EnvironmentZone(
    string ZoneId,
    string Kind,
    bool IsEntryProhibited,
    double? SpeedLimitMps = null,
    string? Advisory = null);

/// <summary>Supplies the zones applying at a horizontal position.</summary>
public interface IZoneSource
{
    /// <summary>Zones covering a scene-frame position.</summary>
    /// <param name="x">East coordinate in metres.</param>
    /// <param name="z">South coordinate in metres.</param>
    /// <returns>Applicable zones, empty when none apply. Never null.</returns>
    IReadOnlyList<EnvironmentZone> GetZones(double x, double z);
}

/// <summary>A zone source that declares no zones anywhere.</summary>
/// <remarks>
/// The default. Scenario-defined zones are not modelled this pass, and returning a shared
/// empty array keeps the sampler allocation-free on the hot path until they are.
/// </remarks>
public sealed class EmptyZoneSource : IZoneSource
{
    private static readonly EnvironmentZone[] None = [];

    private EmptyZoneSource()
    {
    }

    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static EmptyZoneSource Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<EnvironmentZone> GetZones(double x, double z) => None;
}

/// <summary>Canonical water-surface elevation per terrain preset, in metres.</summary>
/// <remarks>
/// One server-side source of truth, and the value the environment payload publishes to the
/// client. It matters because the water mask and the rendered water plane are decided in two
/// different languages: the server decides whether a vessel is afloat, the client draws the
/// surface it appears to float on. Nothing at compile time stops those two constants drifting
/// apart, and when they do a vessel visibly sails on grass — so the client must read this value
/// from the payload rather than keep its own copy.
/// <para>
/// These figures mirror the per-preset water levels the client's terrain presets were authored
/// with. A scenario may override the level for storm surge or drawdown; the override travels on
/// the same payload.
/// </para>
/// <para>
/// The water mask is derived from elevation rather than from <c>ITerrain.GetSurfaceType</c>,
/// because the terrain service reports vegetation unconditionally and therefore cannot
/// distinguish water at all. That leaves two notions of "is this water" in the codebase; this
/// one is authoritative for buoyancy and navigability.
/// </para>
/// </remarks>
public static class SeaLevel
{
    /// <summary>Water level for the alpine preset, in metres.</summary>
    public const double AlpineM = -3.0;

    /// <summary>Water level for the ridgeline preset, in metres.</summary>
    public const double RidgelineM = -15.0;

    /// <summary>Water level for the coastal preset, in metres. The only preset with water above the datum.</summary>
    public const double CoastalM = 3.0;

    /// <summary>Water level for the canyon preset, in metres.</summary>
    public const double CanyonM = -60.0;

    /// <summary>Water level for the dunes preset, in metres.</summary>
    public const double DunesM = -25.0;

    /// <summary>Level used when no preset has been selected.</summary>
    public const double DefaultM = AlpineM;

    /// <summary>Water-surface elevation for a terrain preset key.</summary>
    /// <param name="presetKey">Preset key, matching the terrain service's keys. Case-insensitive.</param>
    /// <returns>Water-surface elevation in metres, or <see cref="DefaultM"/> for an unknown key.</returns>
    public static double ForPreset(string? presetKey) => presetKey?.ToLowerInvariant() switch
    {
        "ridgeline" => RidgelineM,
        "coastal" => CoastalM,
        "canyon" => CanyonM,
        "dunes" => DunesM,
        _ => AlpineM,
    };
}

/// <summary>The environment as one asset sees it at one point, for one step.</summary>
/// <remarks>
/// A value handed to an asset so its step can be a pure function: the asset samples nothing
/// itself, which keeps the impure part — querying terrain and weather — separate from the
/// arithmetic, and lets that arithmetic be tested with literals and no world at all.
/// <para>
/// Water fields are nullable rather than sentinel-valued because "this point is dry land" and
/// "the water surface here is at zero metres" are different facts, and the coastal preset puts
/// the surface at a positive elevation where a zero sentinel would be indistinguishable.
/// </para>
/// </remarks>
/// <param name="PositionEus">Point this sample was taken at, in the scene frame.</param>
/// <param name="WindEus">Wind velocity at the point, in metres per second.</param>
/// <param name="Visibility">Atmospheric visibility as a normalised scalar in [0, 1].</param>
/// <param name="Precipitation">Precipitation intensity as a normalised scalar in [0, 1].</param>
/// <param name="SurfaceCurrentEus">Surface current at the point, in metres per second. Zero on dry land.</param>
/// <param name="TerrainElevationM">Ground or bed elevation under the point, in metres.</param>
/// <param name="TerrainNormalEus">Unit up-normal of the terrain surface, from central differences on the elevation field.</param>
/// <param name="SurfaceMaterial">Surface classification at the point.</param>
/// <param name="WaterSurfaceElevationM">Water-surface elevation, or null when the point is dry land.</param>
/// <param name="BathymetricElevationM">Bed elevation below the water surface, or null when the point is dry land.</param>
/// <param name="Zones">Zones applying at the point. Empty when none apply.</param>
public sealed record EnvironmentSample(
    Vector3 PositionEus,
    Vector3 WindEus,
    double Visibility,
    double Precipitation,
    Vector3 SurfaceCurrentEus,
    double TerrainElevationM,
    Vector3 TerrainNormalEus,
    SurfaceType SurfaceMaterial,
    double? WaterSurfaceElevationM,
    double? BathymetricElevationM,
    IReadOnlyList<EnvironmentZone> Zones)
{
    /// <summary>True when the point is navigable water.</summary>
    public bool IsWater => WaterSurfaceElevationM is not null;

    /// <summary>Angle between the terrain normal and vertical, in radians.</summary>
    public double SlopeRad => Math.Acos(Math.Clamp(TerrainNormalEus.Y, -1.0, 1.0));

    /// <summary>Water depth from surface to bed in metres, or null on dry land.</summary>
    public double? WaterDepthM =>
        WaterSurfaceElevationM is { } surface && BathymetricElevationM is { } bed
            ? Math.Max(0.0, surface - bed)
            : null;

    /// <summary>Surface material as the lower-case token the wire model carries.</summary>
    /// <remarks>
    /// The wire uses a string rather than the SDK enum so a new material can be added without a
    /// breaking numeric renumber of a contract the client already persists.
    /// </remarks>
    public string SurfaceMaterialName => SurfaceMaterial switch
    {
        SurfaceType.Water => "water",
        SurfaceType.Urban => "urban",
        SurfaceType.BareGround => "bare-ground",
        _ => "vegetation",
    };
}

/// <summary>Produces an <see cref="EnvironmentSample"/> at a point.</summary>
public interface IEnvironmentSampler
{
    /// <summary>Water-surface elevation currently in force, in metres.</summary>
    double SeaLevelM { get; }

    /// <summary>Step-less view of the atmosphere.</summary>
    IWindField Wind { get; }

    /// <summary>Terrain elevation at a scene-frame position, in metres.</summary>
    /// <param name="x">East coordinate in metres.</param>
    /// <param name="z">South coordinate in metres.</param>
    /// <returns>Elevation in metres.</returns>
    double GetElevation(double x, double z);

    /// <summary>Unit terrain normal at a scene-frame position.</summary>
    /// <param name="x">East coordinate in metres.</param>
    /// <param name="z">South coordinate in metres.</param>
    /// <param name="spacingM">Central-difference half-spacing in metres; larger values filter more.</param>
    /// <returns>Unit normal in <see cref="Models.CoordinateFrame.LocalEus"/>, pointing away from the ground.</returns>
    Vector3 GetTerrainNormal(double x, double z, double spacingM);

    /// <summary>Samples everything an asset needs at <paramref name="positionEus"/>.</summary>
    /// <param name="positionEus">Point to sample, in the scene frame.</param>
    /// <param name="normalSpacingM">
    /// Central-difference half-spacing for the terrain normal, in metres. Pass the asset's
    /// footprint radius: sampling the normal far finer than the vehicle's contact patch makes
    /// it chatter on procedural noise, which shows up as a rover twitching in pitch and roll.
    /// </param>
    /// <returns>A fully populated sample.</returns>
    EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM);
}

/// <summary>Samples terrain, weather and water into one value per asset per step.</summary>
/// <remarks>
/// Performs no synchronisation. Every member is called under the owning room's single lock,
/// which is also what makes <see cref="SetSeaLevel"/> safe to call from a preset change.
/// </remarks>
public sealed class EnvironmentSampler : IEnvironmentSampler
{
    /// <summary>Smallest usable central-difference half-spacing, in metres.</summary>
    /// <remarks>
    /// Below roughly a quarter of a metre the height field's own high-frequency octaves
    /// dominate the difference, and the normal stops describing anything a vehicle rides on.
    /// </remarks>
    private const double MinNormalSpacingM = 0.25;

    /// <summary>Peak speed of the deterministic surface-current field, in metres per second.</summary>
    private const double CurrentAmplitudeMps = 0.35;

    /// <summary>Spatial frequency of the surface-current field, in radians per metre.</summary>
    private const double CurrentSpatialFrequency = 0.0004;

    /// <summary>Fraction of local wind speed that appears as wind-driven surface drift.</summary>
    private const double WindLeewayFraction = 0.02;

    private readonly ITerrain _terrain;
    private readonly IZoneSource _zones;

    /// <summary>Creates a sampler over a terrain and an atmosphere.</summary>
    /// <param name="terrain">Terrain the world is running on.</param>
    /// <param name="wind">Step-less atmosphere view.</param>
    /// <param name="seaLevelM">Initial water-surface elevation in metres; see <see cref="SeaLevel"/>.</param>
    /// <param name="zones">Zone source, or null for <see cref="EmptyZoneSource"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="terrain"/> or <paramref name="wind"/> is null.</exception>
    public EnvironmentSampler(
        ITerrain terrain,
        IWindField wind,
        double seaLevelM = SeaLevel.DefaultM,
        IZoneSource? zones = null)
    {
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        Wind = wind ?? throw new ArgumentNullException(nameof(wind));
        SeaLevelM = seaLevelM;
        _zones = zones ?? EmptyZoneSource.Instance;
    }

    /// <inheritdoc />
    public double SeaLevelM { get; private set; }

    /// <inheritdoc />
    public IWindField Wind { get; }

    /// <summary>Moves the water surface, after a terrain-preset switch or a scenario override.</summary>
    /// <param name="seaLevelM">New water-surface elevation in metres.</param>
    /// <exception cref="ArgumentException"><paramref name="seaLevelM"/> is not finite.</exception>
    public void SetSeaLevel(double seaLevelM)
    {
        if (!double.IsFinite(seaLevelM))
        {
            throw new ArgumentException("Sea level must be finite.", nameof(seaLevelM));
        }

        SeaLevelM = seaLevelM;
    }

    /// <inheritdoc />
    public double GetElevation(double x, double z) => _terrain.GetElevation(x, z);

    /// <inheritdoc />
    /// <remarks>
    /// Central differences, because <c>ITerrain</c> exposes elevation but no normal and the
    /// height field is procedural rather than meshed — there is no vertex normal to read. The
    /// surface is <c>y = h(x, z)</c>, whose up-normal is <c>(-dh/dx, 1, -dh/dz)</c> normalised.
    /// Central rather than forward differences so the normal is not biased half a sample
    /// downhill, which would tilt a stationary vehicle that is standing on flat ground.
    /// </remarks>
    public Vector3 GetTerrainNormal(double x, double z, double spacingM)
    {
        double h = Math.Max(spacingM, MinNormalSpacingM);
        double dhdx = (_terrain.GetElevation(x + h, z) - _terrain.GetElevation(x - h, z)) / (2.0 * h);
        double dhdz = (_terrain.GetElevation(x, z + h) - _terrain.GetElevation(x, z - h)) / (2.0 * h);

        var normal = new Vector3((float)-dhdx, 1f, (float)-dhdz);
        float length = normal.Length();

        // A degenerate length can only come from a non-finite elevation. Report level ground
        // rather than propagating a NaN attitude into every downstream consumer.
        return float.IsFinite(length) && length > 1e-6f ? normal / length : Vector3.UnitY;
    }

    /// <inheritdoc />
    public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM)
    {
        double x = positionEus.X;
        double z = positionEus.Z;

        double elevation = _terrain.GetElevation(x, z);
        var normal = GetTerrainNormal(x, z, normalSpacingM);
        var wind = Wind.GetWind(x, positionEus.Y, z);

        bool isWater = elevation < SeaLevelM;
        var current = isWater ? SurfaceCurrentAt(x, z, wind) : Vector3.Zero;

        // The terrain reports vegetation everywhere today, so water has to be derived from
        // elevation. Its answer is kept for everything else: when it learns to distinguish
        // urban from bare ground, this picks that up for free.
        var material = isWater ? SurfaceType.Water : _terrain.GetSurfaceType(x, z);

        return new EnvironmentSample(
            PositionEus: positionEus,
            WindEus: wind,
            Visibility: Wind.Visibility,
            Precipitation: Wind.Precipitation,
            SurfaceCurrentEus: current,
            TerrainElevationM: elevation,
            TerrainNormalEus: normal,
            SurfaceMaterial: material,
            WaterSurfaceElevationM: isWater ? SeaLevelM : null,
            BathymetricElevationM: isWater ? elevation : null,
            Zones: _zones.GetZones(x, z));
    }

    /// <summary>Deterministic surface current at a point, in metres per second.</summary>
    /// <remarks>
    /// A smooth, time-invariant spatial field plus a fixed fraction of the local wind as
    /// leeway. Time-invariant on purpose: a current that varied with simulation time would make
    /// a vessel's drift depend on when it was spawned, and drift is exactly the quantity an
    /// advisory search radius is computed from. This is advisory-grade motion, not a tidal
    /// model, and nothing should plan against it as though it were surveyed.
    /// </remarks>
    /// <param name="x">East coordinate in metres.</param>
    /// <param name="z">South coordinate in metres.</param>
    /// <param name="wind">Wind at the point, in metres per second.</param>
    /// <returns>Horizontal current velocity in the scene frame.</returns>
    private static Vector3 SurfaceCurrentAt(double x, double z, Vector3 wind)
    {
        double cx = CurrentAmplitudeMps * Math.Sin((z * CurrentSpatialFrequency) + 1.7);
        double cz = CurrentAmplitudeMps * Math.Cos((x * CurrentSpatialFrequency) + 0.3);

        return new Vector3(
            (float)(cx + (wind.X * WindLeewayFraction)),
            0f,
            (float)(cz + (wind.Z * WindLeewayFraction)));
    }
}
