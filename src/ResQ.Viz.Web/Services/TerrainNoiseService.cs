// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 ResQ Systems, Inc.

using ResQ.Simulation.Engine.Environment;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Procedural terrain elevation service — ports the five TypeScript terrain preset
/// height functions verbatim so the backend simulation matches the Three.js frontend.
/// </summary>
/// <remarks>
/// All height functions work in centred world-space coordinates: X and Z each span
/// −2000 to +2000 metres.  <see cref="GetElevation"/> takes these centred coords
/// directly, matching the drone position space.
/// </remarks>
public sealed class TerrainNoiseService : ITerrain
{
    /// <summary>The terrain in force: a DEM override if one is installed, else a preset.</summary>
    /// <param name="Dem">Client-uploaded heightmap, or null to use <paramref name="Preset"/>.</param>
    /// <param name="Preset">Procedural preset key, used only when <paramref name="Dem"/> is null.</param>
    private sealed record TerrainState(HeightmapTerrain? Dem, string Preset);

    // ONE FIELD, DELIBERATELY — and this is the second time that argument has had to be made
    // here, one level further out than the first.
    //
    // The footprint a DEM covers is carried by the DEM itself (HeightmapTerrain.Width and
    // .Depth), never beside it. Holding width and depth in their own fields published the grid
    // before its dimensions: a reader landing between those stores got the new DEM addressed with
    // the previous upload's footprint — or, on a first upload, with zero — and sampled it
    // somewhere else entirely.
    //
    // The preset stood in exactly the same relationship to the DEM and was still in its own
    // field, so the pair could tear. GetSurfaceType takes FOUR elevation probes to estimate a
    // slope; with two fields, an upload landing mid-estimate let those probes span two different
    // worlds and yield a gradient — and so a surface type — describing neither. Collapsing both
    // into one immutable record makes installing terrain a single reference store again, so a
    // reader sees the whole world or none of it.
    //
    // A concurrent SetPreset and SetHeightmap can still lose one update, because the writers
    // read-modify-write. That is a different and acceptable hazard: a lost update leaves a
    // coherent world and these are operator control-plane calls. A torn read does not.
    private volatile TerrainState _state = new(null, "alpine");

    /// <inheritdoc/>
    public double Width => 4000;

    /// <inheritdoc/>
    public double Depth => 4000;

    /// <summary>
    /// Switches the active terrain preset.  Valid keys: alpine, ridgeline, coastal, canyon, dunes.
    /// </summary>
    public void SetPreset(string key) =>
        _state = _state with { Preset = key.ToLowerInvariant() };

    /// <summary>
    /// Installs a heightmap override.  Subsequent <see cref="GetElevation"/>
    /// queries sample the uploaded DEM instead of the procedural preset, so
    /// drone physics clamp to the same terrain the viz renders.
    /// </summary>
    /// <param name="heights">Row-major elevation grid in metres.</param>
    /// <param name="width">World width the grid covers, in metres.</param>
    /// <param name="depth">World depth the grid covers, in metres.</param>
    public void SetHeightmap(float[,] heights, double width, double depth) =>
        _state = _state with { Dem = new HeightmapTerrain(heights, width, depth) };

    /// <summary>
    /// Clears the heightmap override.  <see cref="GetElevation"/> resumes
    /// sampling the procedural preset.
    /// </summary>
    public void ClearHeightmap() =>
        _state = _state with { Dem = null };

    /// <inheritdoc/>
    public double GetElevation(double x, double z) => Sample(_state, x, z);

    /// <summary>Elevation from one already-captured terrain state.</summary>
    /// <remarks>
    /// Takes the state as an argument rather than reading the field, so a caller needing several
    /// samples of the SAME world — <see cref="GetSurfaceType"/> needs four — can capture once and
    /// pass it in. Reading the field per sample is what let a slope estimate straddle an upload.
    /// </remarks>
    private static double Sample(TerrainState state, double x, double z)
    {
        if (state.Dem is { } dem)
        {
            // Client world-space is centred on origin; HeightmapTerrain expects
            // origin-bottom-left indexing.  Shift by half-width/depth — read off the DEM, so the
            // footprint can never be the previous upload's.
            return dem.GetElevation(x + dem.Width * 0.5, z + dem.Depth * 0.5);
        }

        return state.Preset switch
        {
            "ridgeline" => RidgelineHeight(x, z),
            "coastal" => CoastalHeight(x, z),
            "canyon" => CanyonHeight(x, z),
            "dunes" => DuneHeight(x, z),
            _ => AlpineHeight(x, z),
        };
    }

    /// <summary>Spacing, in metres, of the probes the slope estimate is taken across.</summary>
    /// <remarks>
    /// Wide enough to read the landform rather than a single noise octave, narrow enough that a
    /// rover-sized footprint is not averaged away.
    /// </remarks>
    private const double SurfaceSlopeProbeM = 6.0;

    /// <summary>Gradient above which ground is treated as exposed rather than vegetated.</summary>
    /// <remarks>
    /// About 30 degrees. Soil and plant cover do not hold on a slope much steeper than this, so
    /// the ground a vehicle meets there is rock and scree.
    /// </remarks>
    private const double BareGroundSlopeGradient = 0.58;

    /// <inheritdoc/>
    /// <remarks>
    /// This returned <see cref="SurfaceType.Vegetation"/> unconditionally, so every rover in every
    /// preset drove on one surface everywhere. That was not merely coarse: the whole traction
    /// model downstream became unreachable. <c>GroundSurfaces.For</c> carries distinct traction
    /// and rolling-resistance rows per material, and <c>Traversability</c> can emit
    /// <c>traversability.blocked.traction</c> and <c>traversability.costly.surface</c> — none of
    /// which could ever fire, because the one material returned here sits mid-table and trips
    /// neither. The consequences were wired; only the input was missing.
    /// <para>
    /// The classification is DERIVED from the height field this service already generates, so it
    /// cannot disagree with the terrain a vehicle is actually driving on:
    /// </para>
    /// <list type="bullet">
    /// <item>Dunes are sand end to end, which is bare ground by definition.</item>
    /// <item>Anything steeper than <see cref="BareGroundSlopeGradient"/> is rock and scree.</item>
    /// <item>Everything else is vegetated, the conservative default.</item>
    /// </list>
    /// <para>
    /// Two classifications are deliberately NOT produced here.
    /// <see cref="SurfaceType.Water"/> is decided upstream by the environment sampler from
    /// elevation against sea level, and returning it here as well would be a second water model
    /// free to disagree with the first. <see cref="SurfaceType.Urban"/> is not produced at all:
    /// this service has no building mask, and inventing one would put a pavement traction bonus
    /// on ground with nothing standing on it — a number that looks surveyed and is not.
    /// </para>
    /// </remarks>
    public SurfaceType GetSurfaceType(double x, double z)
    {
        // ONE capture, then four probes from it. Re-reading the field per probe let an upload
        // land mid-estimate, so dx and dz could come from different worlds and the gradient
        // describe neither.
        TerrainState state = _state;

        // The dune shortcut is a fact about the PROCEDURAL preset, so it may only be consulted
        // when the preset is what is in force. Testing it before the DEM inverted the precedence
        // Sample() applies: a heightmap uploaded while "dunes" was selected drove elevation from
        // the DEM while this returned bare ground everywhere, whatever the imported terrain
        // actually looked like.
        if (state.Dem is null && string.Equals(state.Preset, "dunes", StringComparison.Ordinal))
        {
            return SurfaceType.BareGround;
        }

        // Central differences over the same height field the contact solver samples, so the
        // classification and the grade a vehicle is assessed on come from one source.
        double dx = (Sample(state, x + SurfaceSlopeProbeM, z) - Sample(state, x - SurfaceSlopeProbeM, z))
            / (2 * SurfaceSlopeProbeM);
        double dz = (Sample(state, x, z + SurfaceSlopeProbeM) - Sample(state, x, z - SurfaceSlopeProbeM))
            / (2 * SurfaceSlopeProbeM);
        double gradient = Math.Sqrt((dx * dx) + (dz * dz));

        return gradient > BareGroundSlopeGradient ? SurfaceType.BareGround : SurfaceType.Vegetation;
    }

    // ── Shared noise primitives ──────────────────────────────────────────────

    private static double H(int ix, int iz)
    {
        unchecked
        {
            int n = (ix * 374761393) ^ (iz * 668265263);
            n = (n ^ (int)((uint)n >> 13)) * 1274126177;
            return (uint)(n ^ (int)((uint)n >> 16)) / 4_294_967_295.0;
        }
    }

    private static double Noise(double x, double z)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        double fx = x - ix, fz = z - iz;
        double ux = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
        double uz = fz * fz * fz * (fz * (fz * 6 - 15) + 10);
        return H(ix, iz) * (1 - ux) * (1 - uz)
             + H(ix + 1, iz) * ux * (1 - uz)
             + H(ix, iz + 1) * (1 - ux) * uz
             + H(ix + 1, iz + 1) * ux * uz;
    }

    private static double Fbm(double x, double z, int octaves)
    {
        double v = 0, a = 0.5, s = 1;
        for (int i = 0; i < octaves; i++)
        {
            v += a * Noise(x * s, z * s);
            s *= 2.09; a *= 0.47;
        }
        return v;
    }

    private static double Ridged(double x, double z, int octaves,
        double lacunarity = 2.0, double gain = 1.9)
    {
        // Spectral amplitude decay (amp halves per octave) so high-frequency
        // octaves only add fine detail — without it the octaves stacked into a
        // field of sharp spikes instead of coherent ridges. Mirrors `_ridged`
        // in terrainPresets.ts (TS↔C# parity for the eroded-DEM bake + sensors).
        double sum = 0, norm = 0, freq = 1, amp = 0.5, weight = 1;
        for (int i = 0; i < octaves; i++)
        {
            double n = Noise(x * freq, z * freq);
            double signal = 1 - Math.Abs(n * 2 - 1);
            signal *= signal;
            signal *= weight;
            weight = Math.Min(signal * gain, 1.0);
            sum += signal * amp;
            norm += amp;
            freq *= lacunarity;
            amp *= 0.5;
        }
        return sum / norm;
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }

    private static double AsymmetricDune(double u)
    {
        if (u < 0.75)
        {
            double t = u / 0.75;
            return t * t;
        }
        else
        {
            double t = (1.0 - u) / 0.25;
            return t * t;
        }
    }

    // ── Alpine — domain-warped FBM + 4 radial peaks ──────────────────────────

    private static readonly (double Px, double Pz, double Ph, double Pr)[] AlpinePeaks =
    [
        (-620, -820, 188, 560),
        ( 850,  280, 162, 510),
        (-180,  920, 138, 460),
        ( 420,-1080, 108, 420),
    ];

    private static double AlpineHeight(double x, double z)
    {
        const double freq = 0.00060;
        double wx = (Fbm(x * freq + 0.0, z * freq + 0.0, 3) * 2 - 1) * 260;
        double wz = (Fbm(x * freq + 5.2, z * freq + 1.3, 3) * 2 - 1) * 260;

        double large = (Fbm((x + wx) * 0.00055, (z + wz) * 0.00055, 6) * 2 - 1) * 46;
        double medium = (Fbm(x * 0.0028 + 4.1, z * 0.0028 + 8.6, 4) * 2 - 1) * 16;
        double fine = (Fbm(x * 0.013 + 2.2, z * 0.013 + 5.9, 3) * 2 - 1) * 3;

        double peaks = 0;
        foreach (var (px, pz, ph, pr) in AlpinePeaks)
        {
            double pWarpX = px + Fbm(x * 0.004, z * 0.004, 3) * 55;
            double pWarpZ = pz + Fbm(x * 0.004 + 12.0, z * 0.004 + 12.0, 3) * 55;
            double d = Math.Sqrt((x - pWarpX) * (x - pWarpX) + (z - pWarpZ) * (z - pWarpZ));
            double t = 1 - d / pr;
            if (t > 0)
            {
                double noiseFactor = 0.55 + 0.45 * Ridged(x * 0.006, z * 0.006, 4);
                peaks += ph * Math.Pow(t, 1.75) * noiseFactor;
            }
        }
        return 22 + large + medium + fine + peaks;
    }

    // ── Ridgeline — ridged multifractal ──────────────────────────────────────

    private static double RidgelineHeight(double x, double z)
    {
        double wx = (Fbm(x * 0.0008, z * 0.0008, 3) * 2 - 1) * 130;
        double wz = (Fbm(x * 0.0008 + 6.3, z * 0.0008 + 2.4, 3) * 2 - 1) * 130;
        // Lower base frequency → broader ranges; 5 octaves (higher ones decay
        // away now); gentler pow so crests read as long ridges, not needles.
        double rVal = Ridged((x + wx) * 0.00052 + 1.1, (z + wz) * 0.00052 + 0.8, 5);
        double ridge = Math.Pow(rVal, 1.15) * 235;
        double baseH = (Fbm(x * 0.0022 + 3.1, z * 0.0022 + 7.4, 4) * 2 - 1) * 22;
        double fine = (Fbm(x * 0.011 + 2.2, z * 0.011 + 5.9, 3) * 2 - 1) * 4;
        return 8 + ridge + baseH + fine;
    }

    // ── Coastal — island-mask × FBM ──────────────────────────────────────────

    private static readonly (double Ix, double Iz, double Ir)[] Islands =
    [
        (   0,    0, 900),
        ( 750, -650, 440),
        (-820,  290, 400),
        ( 190,  960, 370),
        (-460, -820, 320),
    ];

    private static double CoastalHeight(double x, double z)
    {
        double mask = 0;
        foreach (var (ix, iz, ir) in Islands)
        {
            double dx = x - ix;
            double dz = z - iz;
            double angle = Math.Atan2(dz, dx);
            double radWarp = Fbm(x * 0.006, z * 0.006, 3) * 0.22 +
                            Math.Sin(angle * 5) * 0.05 +
                            Math.Cos(angle * 9) * 0.02;
            double d = Math.Sqrt(dx * dx + dz * dz) * (1.0 + radWarp);
            double t = 1 - d / ir;
            if (t > 0) mask = Math.Max(mask, t);
        }
        double perturbN = (Fbm(x * 0.005 + 2.1, z * 0.005 + 0.7, 4) * 2 - 1) * 0.25;
        double m = Math.Max(0, mask + perturbN);
        double baseHeight = m * 38;
        double details = Fbm(x * 0.0035 + 1.3, z * 0.0035 + 5.2, 5) * 26 * m;
        return baseHeight + details - 2.5;
    }

    // ── Canyon — terrace + threshold canyon cuts ──────────────────────────────

    private static double CanyonHeight(double x, double z)
    {
        // Mirrors _canyonHeight in terrainPresets.ts (TS↔C# parity). Taller
        // plateau + subtle blended strata (12 m steps, 50 %-width sloped risers)
        // instead of the old 20 m sheer-riser "stacked plates", plus a deeper,
        // two-path branching gorge network.
        double baseH = (Fbm(x * 0.00085 + 1.3, z * 0.00085 + 2.7, 5) * 2 - 1) * 45 + 72;

        const double T = 12;
        double frac = (((baseH % T) + T) % T) / T;
        double step = Math.Min(frac / 0.5, 1.0);
        double sf = step * step * (3 - 2 * step);
        double terracedFull = baseH - frac * T + sf * T;
        double terraced = baseH * 0.45 + terracedFull * 0.55;

        double warpX = x + Fbm(x * 0.0018, z * 0.0018, 3) * 180;
        double warpZ = z + Fbm(x * 0.0018 + 8.0, z * 0.0018 + 8.0, 3) * 180;
        double c1 = Math.Abs(Noise(warpX * 0.0013, warpZ * 0.0013) - 0.5);
        double c2 = Math.Abs(Noise(warpX * 0.0026 + 5.1, warpZ * 0.0026 + 2.3) - 0.5);
        double carve = Math.Max(SmoothStep(0.10, 0.0, c1), SmoothStep(0.06, 0.0, c2) * 0.7);
        double depth = carve * carve * 110;

        return terraced - depth;
    }

    // ── Dunes — anisotropic ridge noise ──────────────────────────────────────

    private static double DuneHeight(double x, double z)
    {
        double d1Warp = Noise(x * 0.001, z * 0.001) * 40;
        double d1n = Noise((x + d1Warp) * 0.0028, z * 0.0145 + d1Warp * 0.1);
        double d1 = AsymmetricDune(d1n) * 28;
        double ang = Math.PI * 0.15;
        double cx = x * Math.Cos(ang) + z * Math.Sin(ang);
        double cz = -x * Math.Sin(ang) + z * Math.Cos(ang);
        double d2n = Noise(cx * 0.0038 + 5.2, cz * 0.018 + 2.1);
        double d2 = AsymmetricDune(d2n) * 12;
        double baseH = (Fbm(x * 0.0010, z * 0.0010, 4) * 2 - 1) * 14;
        double field = Noise(x * 0.0018 + 1.7, z * 0.0018 + 3.3);
        return 4 + baseH + d1 * (0.5 + field * 0.5) + d2;
    }
}
