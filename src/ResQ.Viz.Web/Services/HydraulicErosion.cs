// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 ResQ Systems, Inc.

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Deterministic droplet (hydraulic) erosion over a heightmap grid, after
/// Beyer (2015) / Sebastian Lague. Carves drainage valleys, ridgelines, and
/// talus that pure fractal noise lacks, making procedural terrain read as
/// "real". Runs once server-side; the eroded grid is then installed as the
/// authoritative DEM so the rendered mesh, drone collision, and brick-map
/// sensors all share it.
/// </summary>
/// <remarks>
/// The published tuning constants assume a heightmap normalised to [0, 1], so
/// the grid is min/max-normalised before erosion and restored to metres after.
/// </remarks>
public static class HydraulicErosion
{
    private const double Inertia = 0.05;            // 0 = water ignores momentum, 1 = ignores slope
    private const double SedimentCapacityFactor = 4.0;
    private const double MinSedimentCapacity = 0.01; // stops capacity → 0 on flat ground
    private const double ErodeSpeed = 0.3;
    private const double DepositSpeed = 0.3;
    private const double EvaporateSpeed = 0.01;
    private const double Gravity = 4.0;

    /// <summary>
    /// Erodes <paramref name="heights"/> in place (row-major <c>[rows, cols]</c>, metres).
    /// </summary>
    /// <param name="heights">Elevation grid; modified in place.</param>
    /// <param name="seed">Droplet RNG seed — identical seed ⇒ identical result.</param>
    /// <param name="numDroplets">Number of water droplets to simulate.</param>
    /// <param name="erosionRadius">Brush radius (cells) over which a droplet erodes.</param>
    /// <param name="maxLifetime">Maximum steps a droplet takes before it dies.</param>
    public static void Erode(
        float[,] heights,
        int seed,
        int numDroplets,
        int erosionRadius = 3,
        int maxLifetime = 30)
    {
        // Validate parameters that would otherwise corrupt the bake:
        //   erosionRadius is the brush divisor → 0 produces NaN weights;
        //   maxLifetime ≤ 0 skips erosion silently;
        //   null heights would NPE inside GetLength.
        ArgumentNullException.ThrowIfNull(heights);
        if (erosionRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(erosionRadius), "erosionRadius must be > 0.");
        if (maxLifetime <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLifetime), "maxLifetime must be > 0.");

        int mapH = heights.GetLength(0);   // rows  (world Z)
        int mapW = heights.GetLength(1);   // cols  (world X)
        if (mapW < 3 || mapH < 3 || numDroplets <= 0) return;

        // ── Normalise to [0, 1] so the tuned constants behave; remember the
        //    range to restore metres afterwards. ──────────────────────────────
        var map = new double[mapW * mapH];
        double min = double.MaxValue, max = double.MinValue;
        for (int y = 0; y < mapH; y++)
        {
            for (int x = 0; x < mapW; x++)
            {
                double h = heights[y, x];
                map[y * mapW + x] = h;
                if (h < min) min = h;
                if (h > max) max = h;
            }
        }
        double range = max - min;
        if (range < 1e-6) return;   // flat terrain — nothing to erode
        for (int i = 0; i < map.Length; i++) map[i] = (map[i] - min) / range;

        // ── Precompute the erosion brush: offsets + normalised weights. ──────
        var offX = new List<int>();
        var offY = new List<int>();
        var weights = new List<double>();
        double weightSum = 0;
        for (int dy = -erosionRadius; dy <= erosionRadius; dy++)
        {
            for (int dx = -erosionRadius; dx <= erosionRadius; dx++)
            {
                double sq = (double)dx * dx + (double)dy * dy;
                if (sq > (double)erosionRadius * erosionRadius) continue;
                double w = 1.0 - Math.Sqrt(sq) / erosionRadius;
                offX.Add(dx);
                offY.Add(dy);
                weights.Add(w);
                weightSum += w;
            }
        }
        for (int i = 0; i < weights.Count; i++) weights[i] /= weightSum;

        var rng = new Random(seed);

        for (int iter = 0; iter < numDroplets; iter++)
        {
            // NextDouble() ∈ [0,1) ⇒ pos ∈ [0, map-1) ⇒ node ≤ map-2, so the
            // bilinear (+1 / +mapW) accesses below never read out of bounds.
            double posX = rng.NextDouble() * (mapW - 1);
            double posY = rng.NextDouble() * (mapH - 1);
            double dirX = 0, dirY = 0;
            double speed = 1, water = 1, sediment = 0;

            for (int life = 0; life < maxLifetime; life++)
            {
                int nodeX = (int)posX;
                int nodeY = (int)posY;
                int dropletIndex = nodeY * mapW + nodeX;
                double cellOffX = posX - nodeX;
                double cellOffY = posY - nodeY;

                (double height, double gradX, double gradY) = HeightAndGradient(map, mapW, posX, posY);

                // Steer with momentum (inertia) blended against the gradient.
                dirX = dirX * Inertia - gradX * (1 - Inertia);
                dirY = dirY * Inertia - gradY * (1 - Inertia);
                double len = Math.Sqrt(dirX * dirX + dirY * dirY);
                if (len != 0) { dirX /= len; dirY /= len; }
                posX += dirX;
                posY += dirY;

                // Stop if the droplet stalled or ran off the (inner) grid.
                if ((dirX == 0 && dirY == 0) ||
                    posX < 0 || posX >= mapW - 1 || posY < 0 || posY >= mapH - 1)
                {
                    break;
                }

                double newHeight = HeightAndGradient(map, mapW, posX, posY).Height;
                double deltaHeight = newHeight - height;

                double capacity =
                    Math.Max(-deltaHeight, MinSedimentCapacity) * speed * water * SedimentCapacityFactor;

                if (sediment > capacity || deltaHeight > 0)
                {
                    // Over capacity (or flowing uphill into a pit) ⇒ deposit.
                    double deposit = deltaHeight > 0
                        ? Math.Min(deltaHeight, sediment)
                        : (sediment - capacity) * DepositSpeed;
                    sediment -= deposit;
                    map[dropletIndex] += deposit * (1 - cellOffX) * (1 - cellOffY);
                    map[dropletIndex + 1] += deposit * cellOffX * (1 - cellOffY);
                    map[dropletIndex + mapW] += deposit * (1 - cellOffX) * cellOffY;
                    map[dropletIndex + mapW + 1] += deposit * cellOffX * cellOffY;
                }
                else
                {
                    // Under capacity ⇒ erode, spread over the brush so single
                    // cells don't spike into pits.
                    double erode = Math.Min((capacity - sediment) * ErodeSpeed, -deltaHeight);
                    for (int b = 0; b < weights.Count; b++)
                    {
                        int bx = nodeX + offX[b];
                        int by = nodeY + offY[b];
                        if (bx < 0 || bx >= mapW || by < 0 || by >= mapH) continue;
                        int bi = by * mapW + bx;
                        double delta = Math.Min(map[bi], erode * weights[b]);
                        map[bi] -= delta;
                        sediment += delta;
                    }
                }

                // Downhill (deltaHeight < 0) slows the canonical update; water
                // evaporates each step until the droplet dies. Matches Lague.
                speed = Math.Sqrt(Math.Max(0, speed * speed + deltaHeight * Gravity));
                water *= 1 - EvaporateSpeed;
            }
        }

        // ── Restore metres and write back into the caller's grid. ────────────
        for (int y = 0; y < mapH; y++)
        {
            for (int x = 0; x < mapW; x++)
            {
                heights[y, x] = (float)(min + map[y * mapW + x] * range);
            }
        }
    }

    /// <summary>Bilinear height plus its X/Z gradient at a continuous grid position.</summary>
    private static (double Height, double GradX, double GradY) HeightAndGradient(
        double[] map, int mapW, double posX, double posY)
    {
        int coordX = (int)posX;
        int coordY = (int)posY;
        double x = posX - coordX;
        double y = posY - coordY;
        int idx = coordY * mapW + coordX;

        double hNW = map[idx];
        double hNE = map[idx + 1];
        double hSW = map[idx + mapW];
        double hSE = map[idx + mapW + 1];

        double gradX = (hNE - hNW) * (1 - y) + (hSE - hSW) * y;
        double gradY = (hSW - hNW) * (1 - x) + (hSE - hNE) * x;
        double height = hNW * (1 - x) * (1 - y) + hNE * x * (1 - y)
                      + hSW * (1 - x) * y + hSE * x * y;
        return (height, gradX, gradY);
    }
}
