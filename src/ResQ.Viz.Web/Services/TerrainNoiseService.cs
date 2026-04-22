// SPDX-License-Identifier: Apache-2.0
// Copyright 2024 ResQ Technologies Ltd.

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
    private string _preset = "alpine";

    // Heightmap override — when installed (via SetHeightmap), replaces the
    // procedural preset with a client-uploaded DEM. Drone altitude clamping
    // then tracks the imported terrain, matching what the viz renders.
    private HeightmapTerrain? _heightmap;
    // Dimensions of the uploaded DEM, stored so `GetElevation` can shift
    // client-centred world coords onto the SDK's corner-origin grid without
    // assuming the DEM covers the service's default 4000×4000 m footprint.
    private double _hmWidth;
    private double _hmDepth;

    /// <inheritdoc/>
    public double Width => 4000;

    /// <inheritdoc/>
    public double Depth => 4000;

    /// <summary>
    /// Switches the active terrain preset.  Valid keys: alpine, ridgeline, coastal, canyon, dunes.
    /// </summary>
    public void SetPreset(string key) =>
        _preset = key.ToLowerInvariant();

    /// <summary>
    /// Installs a heightmap override.  Subsequent <see cref="GetElevation"/>
    /// queries sample the uploaded DEM instead of the procedural preset, so
    /// drone physics clamp to the same terrain the viz renders.
    /// </summary>
    /// <param name="heights">Row-major elevation grid in metres.</param>
    /// <param name="width">World width the grid covers, in metres.</param>
    /// <param name="depth">World depth the grid covers, in metres.</param>
    public void SetHeightmap(float[,] heights, double width, double depth)
    {
        _heightmap = new HeightmapTerrain(heights, width, depth);
        _hmWidth = width;
        _hmDepth = depth;
    }

    /// <summary>
    /// Clears the heightmap override.  <see cref="GetElevation"/> resumes
    /// sampling the procedural preset.
    /// </summary>
    public void ClearHeightmap() =>
        _heightmap = null;

    /// <inheritdoc/>
    public double GetElevation(double x, double z)
    {
        if (_heightmap is not null)
        {
            // Client world-space is centred on origin; HeightmapTerrain expects
            // origin-bottom-left indexing.  Shift by half-width/depth.
            return _heightmap.GetElevation(x + _hmWidth * 0.5, z + _hmDepth * 0.5);
        }

        return _preset switch
        {
            "ridgeline" => RidgelineHeight(x, z),
            "coastal" => CoastalHeight(x, z),
            "canyon" => CanyonHeight(x, z),
            "dunes" => DuneHeight(x, z),
            _ => AlpineHeight(x, z),
        };
    }

    /// <inheritdoc/>
    public SurfaceType GetSurfaceType(double x, double z) => SurfaceType.Vegetation;

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
        double lacunarity = 2.17, double gain = 1.8)
    {
        double value = 0, weight = 1;
        for (int i = 0; i < octaves; i++)
        {
            double freq = Math.Pow(lacunarity, i);
            double n = Noise(x * freq, z * freq);
            double signal = 1 - Math.Abs(n * 2 - 1);
            double s2 = signal * signal * weight;
            value += s2;
            weight = Math.Min(signal * gain, 1.0);
        }
        return value / octaves;
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
            double t = 1 - ((x - px) * (x - px) + (z - pz) * (z - pz)) / (pr * pr);
            if (t > 0) peaks += ph * t * t;
        }
        return 22 + large + medium + fine + peaks;
    }

    // ── Ridgeline — ridged multifractal ──────────────────────────────────────

    private static double RidgelineHeight(double x, double z)
    {
        double ridge = Ridged(x * 0.00075 + 1.1, z * 0.00075 + 0.8, 8) * 195;
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
            double t = 1 - ((x - ix) * (x - ix) + (z - iz) * (z - iz)) / (ir * ir);
            if (t > 0) mask = Math.Max(mask, t);
        }
        double perturbN = (Fbm(x * 0.005 + 2.1, z * 0.005 + 0.7, 4) * 2 - 1) * 0.28;
        double m = Math.Max(0, mask + perturbN);
        double topo = (Fbm(x * 0.0040 + 1.3, z * 0.0040 + 5.2, 5) * 2 - 1) * 62;
        return topo * Math.Pow(m, 1.3) - 4;
    }

    // ── Canyon — terrace + threshold canyon cuts ──────────────────────────────

    private static double CanyonHeight(double x, double z)
    {
        double baseH = (Fbm(x * 0.00095 + 1.3, z * 0.00095 + 2.7, 5) * 2 - 1) * 28 + 55;
        const double T = 20;
        double frac = (((baseH % T) + T) % T) / T;
        double step = Math.Min(frac / 0.18, 1.0);
        double sf = step * step * (3 - 2 * step);
        double terraced = baseH - frac * T + sf * T;
        double canyonN = Fbm(x * 0.0048 + 7.1, z * 0.0038 + 3.4, 4);
        double depth = canyonN < 0.32 ? Math.Pow(1 - canyonN / 0.32, 2) * 80 : 0;
        return terraced - depth;
    }

    // ── Dunes — anisotropic ridge noise ──────────────────────────────────────

    private static double DuneHeight(double x, double z)
    {
        double d1n = Noise(x * 0.0028 + 0.0, z * 0.0145 + 0.0);
        double d1 = Math.Pow(1 - Math.Abs(d1n * 2 - 1), 2.8) * 28;
        double ang = Math.PI * 0.15;
        double cx = x * Math.Cos(ang) + z * Math.Sin(ang);
        double cz = -x * Math.Sin(ang) + z * Math.Cos(ang);
        double d2n = Noise(cx * 0.0038 + 5.2, cz * 0.018 + 2.1);
        double d2 = Math.Pow(1 - Math.Abs(d2n * 2 - 1), 2.2) * 14;
        double baseH = (Fbm(x * 0.0010, z * 0.0010, 4) * 2 - 1) * 14;
        double field = Noise(x * 0.0018 + 1.7, z * 0.0018 + 3.3);
        return 4 + baseH + d1 * (0.5 + field * 0.5) + d2;
    }
}
