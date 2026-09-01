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
using FluentAssertions;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Tests;

// Where the fleet goes. Split from MixedFleetLoadTests.Fixtures.cs, which decides what is placed,
// because choosing a site is the half that talks to the terrain and it is the half that breaks
// when the terrain is retuned. The suite's summary lives on the primary declaration in
// MixedFleetLoadTests.cs.
//
// NOTHING HERE IS A COORDINATE SOMEBODY MEASURED ONCE. Every site is read back out of the room's
// own sampler — dry ground gentle enough that no rover can be refused it, water deep enough that
// no hull starts with a clearance warning — so retuning the coastal preset moves the fleet with
// it instead of quietly leaving half of it aground. What is fixed is the scan: same box, same
// pitch, same order, same stride, so two runs stage the identical fleet.
public sealed partial class MixedFleetLoadTests
{
    /// <summary>Site survey grid pitch, in metres. Also the closest two spawned assets can be.</summary>
    private const double SurveySpacingM = 60.0;

    /// <summary>Survey cells per axis; with the pitch above this sweeps a 3.5 km box.</summary>
    private const int SurveyCells = 60;

    /// <summary>South-west corner of the survey, in metres, keeping the sweep inside the terrain.</summary>
    private const double SurveyOriginM = -1770.0;

    /// <summary>Central-difference half-spacing used while surveying, in metres.</summary>
    /// <remarks>
    /// Finer than the footprint radius of every shipped ground profile, which is the spacing each
    /// samples its own terrain normal at. The survey therefore sees a slope at least as steep as
    /// the vehicle will, and its verdict is the conservative one; sampling coarser would smooth a
    /// site flat here and leave the rover to discover the grade for itself.
    /// </remarks>
    private const double SurveyProbeSpacingM = 0.5;

    /// <summary>Shallowest water a vessel is staged in, in metres.</summary>
    /// <remarks>Comfortably past every shipped hull's draft, so no vessel starts with a clearance warning.</remarks>
    private const double MinSiteDepthM = 4.0;

    /// <summary>Shortest route a rover is given, in metres, so no rover is sent where it stands.</summary>
    private const double MinRouteLengthM = 100.0;

    /// <summary>Steepest ground a rover is staged on or sent to, in radians.</summary>
    /// <remarks>
    /// About six degrees. The shallowest climbable grade any shipped ground profile declares is
    /// twenty-five, so a site inside this band is several times clear of a refusal even when the
    /// contact solver re-samples it at its own, finer spacing — which is what keeps a staging
    /// step from failing for a reason that has nothing to do with load.
    /// </remarks>
    private const double MaxSiteSlopeRad = 0.10;

    /// <summary>Places the survey found for one room: dry sites and navigable water.</summary>
    /// <param name="Land">Dry, gently-sloped sites, in scan order.</param>
    /// <param name="Water">Navigable sites deeper than <see cref="MinSiteDepthM"/>, in scan order.</param>
    private readonly record struct SiteSurvey(IReadOnlyList<Vector3> Land, IReadOnlyList<Vector3> Water);

    /// <summary>Scans the room's own environment for sites of each kind, in a fixed order.</summary>
    /// <remarks>
    /// Read through <see cref="SimulationRoom.UseAssets{T}"/>, and the whole scan happens inside
    /// that one acquisition: the lists it returns are materialised values, which is what that
    /// method's contract requires. Candidates are spread by stride rather than taken from the
    /// head of the scan, so a fleet occupies the whole coastline instead of the first two columns
    /// of it — which matters because a fleet packed into one corner exercises neither the
    /// separation pass nor the range-dependent detection pass the way a dispersed one does.
    /// </remarks>
    /// <param name="room">Room whose terrain and water to survey.</param>
    /// <param name="landWanted">Dry sites required.</param>
    /// <param name="waterWanted">Navigable sites required.</param>
    /// <returns>The selected sites, in scan order.</returns>
    private static SiteSurvey SurveySites(SimulationRoom room, int landWanted, int waterWanted) =>
        room.UseAssets(world =>
        {
            var land = new List<Vector3>();
            var water = new List<Vector3>();

            for (var ix = 0; ix < SurveyCells; ix++)
            {
                for (var iz = 0; iz < SurveyCells; iz++)
                {
                    double x = SurveyOriginM + (ix * SurveySpacingM);
                    double z = SurveyOriginM + (iz * SurveySpacingM);
                    var probe = new Vector3(
                        (float)x, (float)world.Environment.GetElevation(x, z), (float)z);
                    var sample = world.Environment.Sample(probe, SurveyProbeSpacingM);

                    if (sample.IsWater)
                    {
                        if (sample.WaterDepthM is { } depth && depth > MinSiteDepthM)
                        {
                            water.Add(probe);
                        }
                    }
                    else if (sample.SlopeRad < MaxSiteSlopeRad)
                    {
                        land.Add(probe);
                    }
                }
            }

            return new SiteSurvey(Spread(land, landWanted), Spread(water, waterWanted));
        });

    /// <summary>Takes <paramref name="wanted"/> entries spread evenly across <paramref name="found"/>.</summary>
    /// <param name="found">Every candidate, in scan order.</param>
    /// <param name="wanted">How many to keep.</param>
    /// <returns>The kept candidates, in scan order; shorter than asked for when too few were found.</returns>
    private static IReadOnlyList<Vector3> Spread(IReadOnlyList<Vector3> found, int wanted)
    {
        if (found.Count <= wanted)
        {
            return found;
        }

        var stride = found.Count / wanted;
        var kept = new List<Vector3>(wanted);
        for (var i = 0; i < wanted; i++)
        {
            kept.Add(found[i * stride]);
        }

        return kept;
    }

    /// <summary>The closest surveyed site to <paramref name="from"/> that is worth driving to.</summary>
    /// <remarks>
    /// Nearest rather than arbitrary, because the surveyed sites are the interiors of five
    /// separate islands and a destination picked at random is usually across water. The route is
    /// never swept for a crossing — that is a planner's job this simulation does not do, and the
    /// rover's own look-ahead stops it at a shoreline — but a rover halted on the beach two
    /// seconds in is a rover this gate has stopped measuring, so the staging picks routes that
    /// keep the fleet under way rather than routes that prove a point about blocking.
    /// <para>
    /// Deterministic to the last tie: the scan order decides, because the comparison keeps the
    /// first site at a given distance rather than the last.
    /// </para>
    /// </remarks>
    /// <param name="sites">Every surveyed site, in scan order.</param>
    /// <param name="from">Site the rover is standing on.</param>
    /// <returns>The destination to drive to.</returns>
    private static Vector3 NearestSite(IReadOnlyList<Vector3> sites, Vector3 from)
    {
        var best = from;
        var bestDistance = double.PositiveInfinity;

        foreach (var site in sites)
        {
            // Horizontal separation only: the sites carry their own terrain elevation, and a
            // hillside site would otherwise read as further away than a flat one beside it.
            double separation = Vector3.Distance(
                new Vector3(from.X, 0f, from.Z), new Vector3(site.X, 0f, site.Z));

            if (separation < MinRouteLengthM || separation >= bestDistance)
            {
                continue;
            }

            bestDistance = separation;
            best = site;
        }

        double.IsFinite(bestDistance).Should().BeTrue(
            "the survey must offer a destination at least {0} m from every staged rover",
            MinRouteLengthM);

        return best;
    }
}
