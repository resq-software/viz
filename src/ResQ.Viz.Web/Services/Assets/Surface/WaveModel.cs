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

namespace ResQ.Viz.Web.Services.Assets.Surface;

/// <summary>Wave-driven heave, roll and pitch at one vessel, at one instant.</summary>
/// <remarks>
/// <b>Visual only.</b> Every field here is presentation. Nothing in this record reaches
/// <see cref="SurfaceDynamics"/>, and nothing derived from it may: the navigation solution is
/// computed as though the water surface were flat, and these three numbers are added on top so
/// the rendered hull is not sitting on a mirror. They are published on
/// <see cref="Models.SurfaceDomainState"/> for the same reason, and the field documentation
/// there says so too.
/// </remarks>
/// <param name="HeaveM">Vertical displacement about the mean water surface, in metres. Positive is up.</param>
/// <param name="RollRad">Roll about the longitudinal axis, in radians. Positive puts the starboard rail down.</param>
/// <param name="PitchRad">Pitch about the lateral axis, in radians. Positive is bow-up.</param>
/// <param name="SignificantHeightM">Significant wave height of the sea state this was sampled from, in metres.</param>
public readonly record struct WaveMotion(
    double HeaveM,
    double RollRad,
    double PitchRad,
    double SignificantHeightM)
{
    /// <summary>A flat, motionless surface.</summary>
    public static WaveMotion Calm => default;
}

/// <summary>A deterministic sea surface, sampled for hull motion.</summary>
/// <remarks>
/// <b>This is a visual model and nothing else.</b> It is <em>not</em> a seakeeping model: there
/// is no added mass, no radiation or diffraction, no natural roll period, no roll damping, no
/// resonance and no wave-induced load on the hull. A vessel is not slowed by a head sea here,
/// does not broach in a following one, and its motion is a function of the surface it sits on
/// rather than of its own dynamics. Anything that claims to plan against sea state needs a
/// seakeeping model first; treating these outputs as one would be reading far more into them
/// than they contain.
/// <para>
/// What it <em>is</em>: a fixed set of long-crested sinusoidal components at deep-water
/// frequencies, summed as a function of position and simulation time. The surface elevation
/// gives heave; its gradient, resolved onto the hull's own axes, gives roll and pitch. Two
/// vessels close together therefore ride the same wave in the same phase, and a vessel steaming
/// across the swell rolls periodically, both of which are the point.
/// </para>
/// <para>
/// <b>The wave field's geometry is a property of the sea, not of the wind at one hull.</b> Every
/// component's wavelength, bearing and frequency is fixed when the model is built, so its phase
/// — <c>k*x - w*t</c>, measured over the whole scene — depends on position and simulation time
/// and on nothing else. That is load-bearing. An earlier revision took the wavelength and the
/// bearing from the wind sampled <em>at the vessel</em>, which is a turbulent field quantised to
/// one-second buckets: a hull a kilometre from the origin has a phase of that distance over a
/// wavelength of tens of metres, so every gust swung it by many multiples of <c>2*pi</c> and its
/// attitude stepped about once a second. Fixed geometry leaves the phase nothing a gust can
/// move, and no distance from the origin can amplify what is not there.
/// </para>
/// <para>
/// The wind still sets the sea state — a fresh breeze raises a bigger sea than a calm — but only
/// through <see cref="SignificantHeightFor"/>, which enters as one smooth scalar multiplying the
/// whole field. Heave is <c>Hs</c> times a fixed function of position and time, and the tangents
/// of roll and pitch likewise, so however abruptly the sampled wind moves the rendered motion
/// can only change in proportion to the sea state: it cannot jump between crest and trough. The
/// swell bearing is withheld from the wind for the same reason and is a constructor parameter,
/// so turning the sea with a weather change costs one discontinuity at an operator action rather
/// than one per step per hull.
/// </para>
/// <para>
/// Pure and deterministic: the sample is a function of position, time, heading, wind and
/// profile alone. No wall clock and no random source — a recorded run replays, and the client
/// could evaluate the same function to draw a matching surface without any state being shipped.
/// Stateless and immutable, so one instance serves every vessel in the world.
/// </para>
/// </remarks>
public sealed class WaveModel
{
    /// <summary>Standard gravity, in metres per second squared.</summary>
    /// <remarks>Sets the deep-water dispersion relation, and therefore how fast the sea moves.</remarks>
    private const double GravityMps2 = 9.80665;

    /// <summary>Significant height per squared wind speed, in seconds squared per metre.</summary>
    /// <remarks>
    /// The fully-developed relation <c>Hs ~ 0.021 * U^2</c>, which this model follows while the
    /// sea is small and then bends away from as it approaches its ceiling. Fetch, duration and
    /// swell from elsewhere are not modelled, so this reads as an upper bound on a local wind sea.
    /// </remarks>
    private const double HeightPerWindSpeedSquared = 0.021;

    /// <summary>Residual swell present even in a flat calm, as a significant height in metres.</summary>
    /// <remarks>
    /// Deliberate, and deliberately small. A perfectly still surface reads as a rendering fault
    /// rather than as calm weather; a hand's breadth of swell reads as water. It is the one
    /// figure here chosen for how it looks rather than for what it models.
    /// </remarks>
    private const double ResidualSwellHeightM = 0.12;

    /// <summary>Largest roll or pitch the model will report, in radians.</summary>
    /// <remarks>
    /// About twenty degrees. A cap rather than a physical limit: with a high enough ceiling the
    /// gradient of a steep short component can exceed anything a real hull would follow, and a
    /// vessel drawn on its beam ends looks like a bug rather than like weather. At the default
    /// ceiling the component slopes cannot sum to it.
    /// </remarks>
    private const double MaxAttitudeRad = 0.35;

    /// <summary>Bearing the swell runs toward when the caller does not choose one, in radians.</summary>
    /// <remarks><c>5*pi/4</c>: a swell setting toward the south-west, chosen only so it is not axis-aligned.</remarks>
    private const double DefaultSwellBearingRad = 3.9269908169872414;

    /// <summary>The components summed into the surface.</summary>
    /// <remarks>
    /// A fixed table, iterated a fixed number of times, so the cost of a sample never depends on
    /// the state being sampled. The weights sum to one so the significant height means what it
    /// says, and they fall with wavelength because a real sea carries most of its energy in its
    /// longest components. The bearing offsets fan the shorter components wider either side of
    /// the swell, which is what gives a short-crested look without a directional spectrum, and
    /// the phases are arbitrary constants chosen only so the components do not all crest
    /// together at the origin at time zero. The wavelengths are absolute rather than fractions of
    /// a wind-derived peak, which is the whole point of the table: geometry a gust can move is
    /// geometry that moves a distant hull's phase by whole cycles.
    /// </remarks>
    private static readonly WaveComponent[] Components =
    [
        new(AmplitudeWeight: 0.34, WavelengthM: 62.0, BearingOffsetRad: 0.05, PhaseRad: 0.00),
        new(AmplitudeWeight: 0.24, WavelengthM: 41.0, BearingOffsetRad: -0.31, PhaseRad: 1.97),
        new(AmplitudeWeight: 0.17, WavelengthM: 27.0, BearingOffsetRad: 0.44, PhaseRad: 4.11),
        new(AmplitudeWeight: 0.11, WavelengthM: 18.0, BearingOffsetRad: -0.62, PhaseRad: 2.55),
        new(AmplitudeWeight: 0.08, WavelengthM: 12.0, BearingOffsetRad: 0.78, PhaseRad: 5.63),
        new(AmplitudeWeight: 0.06, WavelengthM: 8.5, BearingOffsetRad: -0.95, PhaseRad: 3.21),
    ];

    /// <summary>The table resolved against this model's swell bearing, built once.</summary>
    private readonly ResolvedComponent[] _resolved;

    /// <summary>Builds a sea surface with a ceiling on how rough it may become.</summary>
    /// <param name="maxSignificantHeightM">
    /// Upper bound on significant wave height, in metres. Caps the sea a strong wind can raise,
    /// which matters because the wind field is a procedural stand-in and a gust in it should not
    /// put a workboat under a breaking crest. Approached smoothly rather than clipped at, so the
    /// sea state has no kink at any wind speed.
    /// </param>
    /// <param name="swellBearingRad">
    /// Bearing the swell runs toward, in radians clockwise from true north. Fixed for the life of
    /// the model on purpose: it appears in every component's phase, so anything that changed it
    /// per step would move a distant hull through whole wave cycles at once.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxSignificantHeightM"/> is not finite or is negative, or
    /// <paramref name="swellBearingRad"/> is not finite.
    /// </exception>
    public WaveModel(
        double maxSignificantHeightM = 2.5,
        double swellBearingRad = DefaultSwellBearingRad)
    {
        if (!double.IsFinite(maxSignificantHeightM) || maxSignificantHeightM < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSignificantHeightM),
                maxSignificantHeightM,
                "The significant wave height ceiling must be finite and not negative.");
        }

        if (!double.IsFinite(swellBearingRad))
        {
            throw new ArgumentOutOfRangeException(
                nameof(swellBearingRad),
                swellBearingRad,
                "The swell bearing must be finite.");
        }

        MaxSignificantHeightM = maxSignificantHeightM;
        SwellBearingRad = CoordinateFrames.NormalizeAngle(swellBearingRad);
        _resolved = Resolve(SwellBearingRad);
    }

    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static WaveModel Default { get; } = new();

    /// <summary>Ceiling on significant wave height, in metres.</summary>
    public double MaxSignificantHeightM { get; }

    /// <summary>Bearing the swell runs toward, in <c>[0, 2*pi)</c> clockwise from true north.</summary>
    public double SwellBearingRad { get; }

    /// <summary>Significant wave height the wind at a point would raise, in metres.</summary>
    /// <remarks>
    /// Published separately from <see cref="Sample"/> so a caller that only wants to describe the
    /// sea state — a weather panel, a mission advisory — need not invent a vessel to ask about
    /// it. The fully-developed <c>0.021 * U^2</c> relation, bent smoothly onto
    /// <see cref="MaxSignificantHeightM"/> rather than clamped to it: this is the one channel by
    /// which the wind reaches the rendered motion, and a clamp would put a corner in that channel
    /// exactly where a gusting wind spends most of its time.
    /// </remarks>
    /// <param name="windEus">Wind velocity in the scene frame, in metres per second.</param>
    /// <returns>Significant height in metres, never below the residual swell and never above <see cref="MaxSignificantHeightM"/>.</returns>
    public double SignificantHeightFor(Vector3 windEus)
    {
        double span = MaxSignificantHeightM - ResidualSwellHeightM;
        if (span <= 0.0)
        {
            // A ceiling at or below the residual swell: the ceiling wins, and a zero ceiling is
            // the caller asking for a mirror.
            return MaxSignificantHeightM;
        }

        double windSpeed = CoordinateFrames.SpeedOverGround(windEus);
        if (!double.IsFinite(windSpeed))
        {
            return ResidualSwellHeightM;
        }

        double raised = HeightPerWindSpeedSquared * windSpeed * windSpeed;

        // double.ExpM1 is e^x - 1 evaluated without the cancellation that expression suffers for small
        // x, which is the whole low-wind end of this curve.
        return ResidualSwellHeightM - (span * double.ExpM1(-raised / span));
    }

    /// <summary>Samples the wave-driven motion of one hull.</summary>
    /// <remarks>
    /// <b>Visual only — see the type's own remarks.</b> The result must not be fed back into
    /// <see cref="SurfaceDynamics"/>, added to a navigation altitude, or used to derive an
    /// under-keel clearance: clearance is measured against the mean water surface, and a hull
    /// that grounded on a wave trough here would be grounding on a decoration.
    /// <para>
    /// Roll and pitch come from the surface gradient resolved onto the hull's own axes, scaled
    /// per component by a crude contouring factor: a hull long relative to a component bridges it
    /// and barely responds, a hull short relative to it follows the surface. That factor is the
    /// only thing standing in for a response amplitude operator, and it is a stand-in rather than
    /// an approximation of one. <paramref name="windEus"/> sets the sea state and nothing else —
    /// it does not reach any component's phase, which is why an instantaneous turbulent sample
    /// cannot make the attitude jump.
    /// </para>
    /// </remarks>
    /// <param name="positionEus">Vessel position in the scene frame. Only the horizontal components are read.</param>
    /// <param name="simulationTimeSeconds">Simulation time in seconds. Never a wall clock.</param>
    /// <param name="headingRad">Heading in radians clockwise from true north, used to resolve the gradient onto the hull.</param>
    /// <param name="windEus">Wind velocity at the vessel in the scene frame, which sets the sea state.</param>
    /// <param name="profile">Hull whose length and beam decide how much of the surface it follows.</param>
    /// <returns>Heave, roll and pitch. <see cref="WaveMotion.Calm"/> when any input is non-finite.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public WaveMotion Sample(
        Vector3 positionEus,
        double simulationTimeSeconds,
        double headingRad,
        Vector3 windEus,
        SurfaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // A becalmed, level hull is the right answer for a bad input. This is decoration, and
        // faulting the whole asset pass over a decoration would be the wrong trade.
        if (!double.IsFinite(simulationTimeSeconds) || !double.IsFinite(headingRad)
            || !float.IsFinite(positionEus.X) || !float.IsFinite(positionEus.Z)
            || !float.IsFinite(windEus.X) || !float.IsFinite(windEus.Z))
        {
            return WaveMotion.Calm;
        }

        double significantHeight = SignificantHeightFor(windEus);
        if (significantHeight <= 0.0)
        {
            return WaveMotion.Calm;
        }

        double amplitude = 0.5 * significantHeight;

        double elevation = 0.0;
        double rollGradientEast = 0.0;
        double rollGradientSouth = 0.0;
        double pitchGradientEast = 0.0;
        double pitchGradientSouth = 0.0;

        for (int i = 0; i < _resolved.Length; i++)
        {
            var component = _resolved[i];

            double distance = (positionEus.X * component.DirectionEast)
                + (positionEus.Z * component.DirectionSouth);
            double phase = (component.WaveNumber * distance)
                - (component.AngularFrequency * simulationTimeSeconds)
                + component.PhaseRad;

            double componentAmplitude = amplitude * component.AmplitudeWeight;

            // Beam decides how much of a component's slope becomes roll, length how much becomes
            // pitch, and the two are accumulated separately because a hull can bridge a component
            // fore-and-aft while still rolling to it.
            double alongResponse = component.ContouringFactor(profile.LengthM);
            double acrossResponse = component.ContouringFactor(profile.BeamM);

            elevation += componentAmplitude * alongResponse * Math.Sin(phase);

            // Analytic gradient of the same expression, so the slope a hull is tilted by is the
            // slope of the surface it is drawn on rather than a second, separately shaped
            // function that merely resembles it.
            double slope = componentAmplitude * component.WaveNumber * Math.Cos(phase);
            rollGradientEast += slope * component.DirectionEast * acrossResponse;
            rollGradientSouth += slope * component.DirectionSouth * acrossResponse;
            pitchGradientEast += slope * component.DirectionEast * alongResponse;
            pitchGradientSouth += slope * component.DirectionSouth * alongResponse;
        }

        double sin = Math.Sin(headingRad);
        double cos = Math.Cos(headingRad);

        // Project the gradient onto the unit bow vector (sin h, -cos h) and the unit starboard
        // vector (cos h, sin h) — the same body axes SurfaceDynamics rotates velocities into.
        double slopeAhead = (pitchGradientEast * sin) - (pitchGradientSouth * cos);
        double slopeStarboard = (rollGradientEast * cos) + (rollGradientSouth * sin);

        return new WaveMotion(
            HeaveM: elevation,

            // The rail goes down on the side the water falls away from, so roll takes the
            // opposite sign to the starboard slope. Pitch takes the same sign as the slope
            // ahead: water rising in front of the bow lifts it.
            RollRad: Clamp(-Math.Atan(slopeStarboard)),
            PitchRad: Clamp(Math.Atan(slopeAhead)),
            SignificantHeightM: significantHeight);
    }

    /// <summary>Resolves the fixed table against a swell bearing, once per model.</summary>
    /// <param name="swellBearingRad">Bearing the swell runs toward, in radians.</param>
    /// <returns>The components with their directions, wave numbers and frequencies precomputed.</returns>
    private static ResolvedComponent[] Resolve(double swellBearingRad)
    {
        var resolved = new ResolvedComponent[Components.Length];

        for (int i = 0; i < Components.Length; i++)
        {
            var component = Components[i];
            double bearing = swellBearingRad + component.BearingOffsetRad;
            double waveNumber = Math.Tau / component.WavelengthM;

            resolved[i] = new ResolvedComponent(
                AmplitudeWeight: component.AmplitudeWeight,
                InverseWavelengthPerM: 1.0 / component.WavelengthM,
                WaveNumber: waveNumber,

                // Deep-water dispersion: omega = sqrt(g*k). Taking the frequency from the
                // wavelength rather than quoting both is what keeps short components visibly
                // quicker than long ones instead of the whole surface pulsing in unison.
                AngularFrequency: Math.Sqrt(GravityMps2 * waveNumber),
                DirectionEast: Math.Sin(bearing),
                DirectionSouth: -Math.Cos(bearing),
                PhaseRad: component.PhaseRad);
        }

        return resolved;
    }

    /// <summary>Holds an attitude inside <see cref="MaxAttitudeRad"/>.</summary>
    private static double Clamp(double angleRad) =>
        Math.Clamp(angleRad, -MaxAttitudeRad, MaxAttitudeRad);

    /// <summary>One sinusoidal component of the surface, as authored.</summary>
    /// <param name="AmplitudeWeight">Share of the sea state's amplitude this component carries.</param>
    /// <param name="WavelengthM">Wavelength in metres. Fixed: it must not depend on the sea state.</param>
    /// <param name="BearingOffsetRad">Bearing offset from the swell, in radians.</param>
    /// <param name="PhaseRad">Fixed phase offset, in radians.</param>
    private readonly record struct WaveComponent(
        double AmplitudeWeight,
        double WavelengthM,
        double BearingOffsetRad,
        double PhaseRad);

    /// <summary>A component with everything that does not depend on the sample precomputed.</summary>
    /// <param name="AmplitudeWeight">Share of the sea state's amplitude this component carries.</param>
    /// <param name="InverseWavelengthPerM">Reciprocal wavelength, for the contouring factor.</param>
    /// <param name="WaveNumber">Spatial angular frequency <c>2*pi/lambda</c>, in radians per metre.</param>
    /// <param name="AngularFrequency">Temporal angular frequency <c>sqrt(g*k)</c>, in radians per second.</param>
    /// <param name="DirectionEast">East component of the unit travel direction.</param>
    /// <param name="DirectionSouth">South component of the unit travel direction.</param>
    /// <param name="PhaseRad">Fixed phase offset, in radians.</param>
    private readonly record struct ResolvedComponent(
        double AmplitudeWeight,
        double InverseWavelengthPerM,
        double WaveNumber,
        double AngularFrequency,
        double DirectionEast,
        double DirectionSouth,
        double PhaseRad)
    {
        /// <summary>How much of this component a hull of a given dimension follows, in <c>(0, 1]</c>.</summary>
        /// <remarks>
        /// <c>1 / (1 + (dimension / wavelength)^2)</c>. A hull far shorter than the wave contours
        /// it almost exactly; a hull comparable to it bridges between crests and responds far
        /// less. A shape chosen to behave sensibly at both extremes, not a derived transfer
        /// function, and deliberately the only frequency-dependence in the model. Applied per
        /// component rather than to one peak wavelength, so a hull rides the swell while
        /// following the chop riding on it.
        /// </remarks>
        /// <param name="dimensionM">Hull dimension across the motion — length for heave and pitch, beam for roll.</param>
        /// <returns>A factor in <c>(0, 1]</c>.</returns>
        public double ContouringFactor(double dimensionM)
        {
            double ratio = dimensionM * InverseWavelengthPerM;
            return 1.0 / (1.0 + (ratio * ratio));
        }
    }
}
