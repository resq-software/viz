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

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>A ground asset: a rover this world owns, integrates and settles onto the terrain.</summary>
/// <remarks>
/// The ground-domain sibling of <see cref="AirAsset"/>, and its mirror image in one structural
/// respect. An air asset implements <see cref="ISimulatedAsset"/> only, because the SDK's world
/// integrates drones and would in any case skip anything reporting a landed flag — permanently
/// true for a rover sitting on the terrain. A ground asset therefore implements
/// <see cref="IStepDrivenAsset"/> and carries its own integration.
/// <para>
/// <b>What one step does, in order.</b> Re-baseline onto the terrain if the height field has been
/// replaced since the last step, read the setpoint from <see cref="GroundNavigator"/>, integrate it
/// through the profile's <see cref="IGroundDynamics"/>, settle the resulting planar pose onto the
/// terrain with <see cref="TerrainContact"/>, then apply the constraint outcomes: an unmountable
/// step reverts the translation and latches guidance into a block, and ground that will not carry
/// the vehicle drives its speed ceiling — and therefore its commanded speed — to zero. Events are
/// raised from <see cref="Step"/>, never from <see cref="Capture"/>.
/// </para>
/// <para>
/// <b>Immobilisation stops autonomy, not the operator.</b> A rover on ground that will not carry
/// it is stopped from driving itself further into that ground, and says so once on the transition.
/// It still accepts a reverse or a manual input, at a crawl and with forward inhibited, because
/// backing out the way it came is how a stuck vehicle is recovered — and a vehicle no command can
/// move is not in a safe state, it is a dead asset. The two halves of that gate,
/// <see cref="GroundNavigator.Sample"/>'s setpoint and this type's speed ceiling, read one figure,
/// <see cref="GroundNavigator.RecoveryCeilingMps"/>, so they cannot disagree.
/// </para>
/// <para>
/// <b>Why events live in Step.</b> <see cref="AirAsset"/> has no step of its own, so it must
/// observe transitions during capture and guard them on the tick to stay idempotent. A step-driven
/// asset needs no such guard: <see cref="Step"/> runs exactly once per world tick, so a transition
/// observed there is observed once by construction, and <see cref="Capture"/> is left as a pure
/// projection that may run any number of times per tick — a broadcast frame and a REST read on the
/// same tick do exactly that — without emitting anything. Every event here is an <em>edge</em>. A
/// level-triggered condition such as sitting on a steep cross-slope would otherwise emit sixty
/// alerts a second and bury everything else in the log.
/// </para>
/// <para>
/// <b>Why the asset holds an environment sampler.</b> The world samples the environment at each
/// asset's <em>pre-step</em> position and passes the value on the step context, which is what keeps
/// the arithmetic testable with literals. A ground vehicle has to settle onto the ground it moved
/// <em>onto</em>, so one further sample at the post-step position is unavoidable, and the same
/// sampler answers the look-ahead probe and vets a drive target before a command is accepted. That
/// is not a determinism break: the sampler is a pure function of position given the world's terrain
/// and the current weather state, and the weather is advanced exactly once per world step by the
/// SDK, before the asset pass runs.
/// </para>
/// <para>
/// Advisory throughout. Mobility, rollover proximity and traversability here are quasi-static
/// estimates from a rigid-body approximation over a procedural height field. They exist to tell an
/// operator where to look; none of them is a safety guarantee.
/// </para>
/// </remarks>
public sealed partial class GroundAsset : IStepDrivenAsset
{
    /// <summary>Standard gravity, in metres per second squared.</summary>
    private const double StandardGravityMps2 = 9.80665;

    /// <summary>Usable pack energy per kilogram of vehicle mass, in watt-hours.</summary>
    /// <remarks>
    /// Sized from mass because the profile declares no pack capacity, and mass is what a battery is
    /// actually specified against on a real platform. Advisory: it produces endurances of the right
    /// order for these envelopes, and nothing should plan against it as though it were a datasheet.
    /// </remarks>
    private const double PackEnergyWhPerKg = 15.0;

    /// <summary>Power drawn with the drivetrain stopped, in watts: avionics, radio and sensors.</summary>
    private const double IdlePowerW = 45.0;

    /// <summary>Fraction of electrical power that reaches the ground as tractive effort.</summary>
    private const double DrivetrainEfficiency = 0.80;

    /// <summary>Charge below which health is reported as degraded, as a percentage.</summary>
    private const double LowEnergyPercent = 20.0;

    /// <summary>Seconds in an hour, for turning a power draw into a state-of-charge change.</summary>
    private const double SecondsPerHour = 3600.0;

    /// <summary>Fraction of distance travelled that accumulates as odometry error on ideal ground.</summary>
    /// <remarks>
    /// Divided by the available traction, so a wheel spinning on a wet slope drifts faster than one
    /// gripping dry pavement — the physical mechanism, wheel slip, rather than a tuned constant.
    /// See <see cref="PositionUncertaintyGrowthMps"/> for why the whole quantity vanishes at a
    /// standstill.
    /// </remarks>
    private const double OdometryDriftFraction = 0.02;

    /// <summary>Furthest ahead the traversability probe ever looks, in metres.</summary>
    /// <remarks>
    /// A cap on a distance that otherwise grows with the square of speed. Probing half a kilometre
    /// ahead answers a question about ground the vehicle may never reach, and would refuse routes a
    /// turn two seconds from now makes irrelevant.
    /// </remarks>
    private const double MaxLookaheadM = 40.0;

    /// <summary>World steps the look-ahead allows for before commanded braking takes effect.</summary>
    /// <remarks>
    /// One step for the probe taken now to reach the navigator's next setpoint, and one more for
    /// the drivetrain to begin chasing it. Both are real latencies of this pipeline rather than
    /// padding: the setpoint produced from a probe is integrated on the following step, and the
    /// drivetrain then moves towards it at a rate limit rather than instantly.
    /// </remarks>
    private const double LookaheadReactionSteps = 2.0;

    /// <summary>Horizontal distance below which a step is not attributed to travel, in metres.</summary>
    /// <remarks>
    /// A collision requires the vehicle to have gone somewhere. Positions are single-precision
    /// and reach hundreds of metres in this scene, where one unit in the last place is already
    /// about 6e-5 m, so a displacement under a tenth of a millimetre is not a movement the pose
    /// can even represent — and a rise measured across it is a change in the ground, never a
    /// vehicle driving into something.
    /// </remarks>
    private const double MinCollisionTravelM = 1e-4;

    /// <summary>Elevation change under a stationary vehicle that counts as new terrain, in metres.</summary>
    /// <remarks>
    /// Tiny on purpose. The comparison is between two samples of the same height field at the
    /// same coordinates, which agree bit for bit while the field is unchanged, so anything above
    /// numerical noise is a genuinely different terrain rather than a different position.
    /// </remarks>
    private const double TerrainChangeEpsilonM = 1e-6;

    /// <summary>Horizontal distance within which a supplied sample counts as taken here, in metres.</summary>
    /// <remarks>
    /// The step contract says the context's environment sample is taken at this asset's pre-step
    /// position, and the re-baseline test only means anything if that holds — a sample from
    /// somewhere else differs in elevation because it is elsewhere, not because the ground moved.
    /// A centimetre is far below anything a step can translate the vehicle by and far above the
    /// single-precision noise in the comparison.
    /// </remarks>
    private const double SameStationToleranceM = 0.01;

    private static readonly FaultCode[] NoFaults = [];
    private static readonly ComponentHealth[] NoComponents = [];
    private static readonly AssetEvent[] NoEvents = [];

    private readonly IGroundDynamics _dynamics;
    private readonly GroundProfile _profile;
    private readonly GroundNavigator _navigator;
    private readonly IEnvironmentSampler _environment;
    private readonly List<AssetEvent> _events = [];
    private readonly Vector3 _basePositionEus;
    private readonly double _capacityWh;

    /// <summary>Onset memory so a standing fault reports when it started, not when it was seen.</summary>
    private readonly FaultOnsetLedger _faultOnsets = new();

    private GroundMotionState _motion;
    private TerrainNormalFilter _filter = TerrainNormalFilter.Uninitialised;
    private TerrainContactState _contact;
    private EnvironmentSample _sample;
    private Vector3 _positionEus;
    private Vector3 _groundVelocityEus;
    private double _energyWh;
    private double _drawWatts = IdlePowerW;
    private ulong _sequence;

    // The most recent step's clock, so a command that arrives between steps can stamp the event
    // it raises. Nothing has been integrated since, so that is the honest instant to attribute
    // it to — and an asset has no clock of its own to reach for instead.
    private double _simulationTimeSeconds;
    private long _tick = -1;

    // Edge-detection state for the transition events raised from Step. Never read by Capture.
    private bool _wasImmobilised;
    private bool _wasRolloverRisk;
    private bool _lowEnergyLatched;

    /// <summary>Places a rover on the terrain and prepares it to be stepped.</summary>
    /// <remarks>
    /// The spawn position's vertical component is discarded. A ground vehicle's height is read off
    /// the terrain under its footprint and never commanded, so honouring a requested <c>Y</c> would
    /// let a caller bury a rover in a hillside or hang one in the air.
    /// </remarks>
    /// <param name="descriptor">Descriptor for this asset; its domain must be <see cref="AssetDomain.Ground"/>.</param>
    /// <param name="dynamics">Motion model to integrate with; its profile becomes this asset's envelope.</param>
    /// <param name="environment">Sampler used to settle onto the terrain, probe ahead, and vet drive targets.</param>
    /// <param name="spawnPositionEus">Spawn position in the scene frame; only the horizontal components are used.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <param name="safety">Emergency-stop policy, or null to derive it from the profile.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The descriptor is not a ground descriptor, or a spawn value is not finite.</exception>
    public GroundAsset(
        AssetDescriptor descriptor,
        IGroundDynamics dynamics,
        IEnvironmentSampler environment,
        Vector3 spawnPositionEus,
        double headingRad = 0.0,
        GroundSafetyPolicy? safety = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(dynamics);
        ArgumentNullException.ThrowIfNull(environment);

        if (descriptor.Domain != AssetDomain.Ground)
        {
            throw new ArgumentException(
                $"A ground asset needs a ground descriptor; got '{descriptor.Domain}'.",
                nameof(descriptor));
        }

        if (!float.IsFinite(spawnPositionEus.X) || !float.IsFinite(spawnPositionEus.Z))
        {
            throw new ArgumentException("A spawn position must be finite.", nameof(spawnPositionEus));
        }

        Descriptor = descriptor;
        _dynamics = dynamics;
        _profile = dynamics.Profile;
        _environment = environment;
        _navigator = new GroundNavigator(_profile);
        Safety = safety ?? GroundSafetyPolicy.For(_profile);

        _motion = GroundMotionState
            .AtRest(spawnPositionEus.X, spawnPositionEus.Z, headingRad)
            .Validated(nameof(headingRad));

        _capacityWh = PackEnergyWhPerKg * _profile.MassKg;
        _energyWh = _capacityWh;

        // A provisional height, so the first environment sample has a wind-sampling altitude.
        // Settle immediately replaces it with the contact solver's answer.
        _positionEus = new Vector3(
            spawnPositionEus.X,
            (float)(environment.GetElevation(spawnPositionEus.X, spawnPositionEus.Z)
                + GroundContactGeometry.RideHeightM(_profile)),
            spawnPositionEus.Z);

        Settle(deltaSeconds: 0.0);

        _basePositionEus = _positionEus;

        // Deliberately left false even when the spawn contact is already immobilised, so the first
        // step announces it. A rover that arrives stuck — placed over water, or on a grade past
        // what it climbs — is the one case where seeding from the contact suppresses the alert an
        // operator most needs: nothing later transitions, so nothing is ever raised, and a vehicle
        // that autonomy cannot move sits silent in the asset list looking healthy. Being stuck at
        // tick zero is still entering the immobilised state, and it is still an edge, so it is
        // still raised exactly once.
        _wasImmobilised = false;

        // The lean is seeded from the contact, because it is not the same finding. A rover spawned
        // on a bank is mobile, keeps every heading it had, and publishes its rollover fraction and
        // its ROLLOVER_RISK fault continuously — so the standing advisory reaches an operator
        // without an event claiming a transition that never happened.
        _wasRolloverRisk = _contact.HasRolloverRisk;
    }

    /// <inheritdoc />
    public string AssetId => Descriptor.AssetId;

    /// <inheritdoc />
    public AssetDomain Domain => AssetDomain.Ground;

    /// <inheritdoc />
    public Vector3 PositionEus => _positionEus;

    /// <inheritdoc />
    public AssetDescriptor Descriptor { get; }

    /// <summary>Physical envelope this rover is integrated within.</summary>
    public GroundProfile Profile => _profile;

    /// <summary>Emergency-stop policy in force for this rover.</summary>
    public GroundSafetyPolicy Safety { get; }

    /// <summary>Guidance mode as a stable lower-case token, as published in the wire's mode field.</summary>
    /// <remarks>
    /// Exposed so the compatibility projection and the v2 mode string cannot drift apart, which is
    /// the same reason <see cref="AirAsset.StatusV1"/> exists.
    /// </remarks>
    public string ModeToken => IsEmergencyStopped ? "emergency-stop" : _navigator.ModeToken;

    /// <summary>Whether an emergency stop is latched.</summary>
    /// <remarks>
    /// Tracked separately from the navigator's mode because it also gates command acceptance: while
    /// this is set, every command that would produce motion is refused. See
    /// <see cref="GroundSafetyPolicy"/> for what an emergency stop does and does not inhibit, and
    /// for how it is released.
    /// </remarks>
    public bool IsEmergencyStopped { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// A pure function of the context and this asset's own state: no clock, no adaptive
    /// substepping, no convergence-based early exit, and no iteration count that varies with state.
    /// The work per call is fixed by the guidance mode alone, never by the terrain: one environment
    /// sample and one contact resolution to settle onto the ground, plus one more of each for the
    /// look-ahead probe while the vehicle is under way.
    /// <para>
    /// One further pair is taken on a step where the height field itself has been replaced, to
    /// re-baseline onto the new ground — see <see cref="RebaselineIfEnvironmentChanged"/>. That is
    /// still fixed work rather than a search, and it is a function of an <em>input</em>, the
    /// environment, rather than of the vehicle's history: two replays that switch preset at the
    /// same tick do the same work in the same order and produce the same pose.
    /// </para>
    /// </remarks>
    public void Step(in AssetStepContext context)
    {
        double delta = context.DeltaSeconds;

        // The world already skips the asset pass on a non-positive timestep. Repeated here because
        // an asset stepped directly by a test must behave identically, and because dividing the
        // position delta by zero below would publish an infinite velocity.
        if (!double.IsFinite(delta) || delta <= 0.0)
        {
            return;
        }

        // Nothing has moved this vehicle since the last step, so the environment is the only thing
        // that can have changed under it — and it does change: a terrain preset switch or a
        // heightmap upload replaces the height field between two ticks. Re-baselining first is
        // what stops the rest of this method reading the resulting elevation jump as travel.
        RebaselineIfEnvironmentChanged(context.Environment);

        var guidance = _navigator.Sample(in _motion, BuildGuidanceInput(delta));

        // The emergency stop overrides guidance rather than being expressed through it, so a
        // latched stop survives anything that reached the navigator by another route.
        var setpoint = IsEmergencyStopped ? GroundSetpoint.Stop : guidance.Setpoint;

        var previousMotion = _motion;
        var previousContact = _contact;
        var previousSample = _sample;
        var previousFilter = _filter;
        var previousPosition = _positionEus;

        // The contact solver has already folded grade, cross-slope, surface and zone limits into
        // one ceiling, so the integrator is handed that rather than a second opinion derived from
        // the same sample. An immobilised vehicle's ceiling is zero, which brakes it to a halt —
        // except while an operator is recovering it, where the ceiling is the navigator's crawl.
        var conditions = new GroundConditions(
            SpeedCeilingFor(previousContact, guidance.Mode), previousContact.TractionCoefficient);

        _motion = _dynamics.Step(in previousMotion, in setpoint, delta, in conditions);
        Settle(delta);

        double travelled = HorizontalDistance(previousPosition, _positionEus);
        bool blockedByCollision = false;
        var collision = GroundStepCollision.None;

        // A collision requires the vehicle to have actually gone somewhere. Both elevations are
        // now read from the same height field — the re-baseline above guarantees it — so their
        // difference is a rise the vehicle drove onto and nothing else; the travel test is the
        // second half of that guarantee, and states it rather than leaving it to arithmetic.
        bool collided = travelled > MinCollisionTravelM
            && TerrainContact.TryDetectStepCollision(
                _profile,
                previousSample.TerrainElevationM,
                _sample.TerrainElevationM,
                travelled,
                previousMotion.ForwardSpeedMps,
                out collision);

        if (collided)
        {
            // A vehicle cannot be inside the obstruction it struck, so the whole translation is
            // undone and the drivetrain stopped. The steering angle is kept: the actuator did move,
            // and reverting it would make the published wheel angle disagree with the servo.
            _motion = previousMotion with
            {
                ForwardSpeedMps = 0.0,
                YawRateRadPerSec = 0.0,
                SteeringAngleRad = _motion.SteeringAngleRad,
            };

            _contact = previousContact;
            _sample = previousSample;
            _filter = previousFilter;
            _positionEus = previousPosition;

            // Guidance has to hear about it, or the vehicle re-accelerates into the same
            // obstruction next step and keeps doing so, raising a fresh impact each time. The
            // look-ahead cannot catch this case: the rise was discovered only by moving onto it.
            blockedByCollision = _navigator.Block(TraversabilityReason.StepHeightExceeded);
        }

        _groundVelocityEus = collided
            ? Vector3.Zero
            : (_positionEus - previousPosition) / (float)delta;

        ConsumeEnergy(delta);

        _simulationTimeSeconds = context.SimulationTimeSeconds;
        _tick = context.Tick;

        RaiseStepEvents(in guidance, in collision, blockedByCollision);

        _sequence++;
    }

    /// <summary>Speed ceiling to integrate at, in metres per second.</summary>
    /// <remarks>
    /// Normally the contact solver's own advisory ceiling, which has already folded grade,
    /// cross-slope, surface and zone limits into one number. The exception is the second half of
    /// the immobilisation trap: on ground that will not carry the vehicle that ceiling is exactly
    /// zero, so even a guidance law that permitted a recovery crawl would hand the integrator a
    /// budget of nothing and the rover would sit there being commanded to move and refusing to.
    /// While an operator is recovering it, the ceiling is <see cref="GroundNavigator.RecoveryCeilingMps"/>
    /// — the same figure the setpoint was clamped to, read from the navigator rather than
    /// recomputed, so the two cannot drift apart.
    /// <para>
    /// Forward travel is <em>not</em> what this opens up. The navigator has already clamped the
    /// commanded speed to zero or below in this case, so the budget released here can only be
    /// spent backing out.
    /// </para>
    /// </remarks>
    /// <param name="contact">Contact resolved under the vehicle at the start of this step.</param>
    /// <param name="mode">Guidance mode the setpoint was produced under.</param>
    /// <returns>The ceiling in metres per second.</returns>
    private double SpeedCeilingFor(TerrainContactState contact, GroundGuidanceMode mode) =>
        contact.IsImmobilised && GroundNavigator.IsOperatorRecovery(mode)
            ? _navigator.RecoveryCeilingMps
            : contact.SafeSpeedMps;

    /// <summary>Re-settles the vehicle when the ground under it has been replaced.</summary>
    /// <remarks>
    /// The room's environment is mutable at runtime — a terrain preset switch, a heightmap upload,
    /// a scenario's sea-level override — and each of those bumps the room's environment revision
    /// precisely because everything cached against the old height field is now wrong. An asset
    /// sees no revision counter, but it does not need one: the world hands it a sample taken at
    /// its own pre-step position against the <em>current</em> terrain, and the asset holds one
    /// taken at that same position at the end of the previous step. Nothing moves a rover between
    /// two steps, so any disagreement between those two elevations is the terrain having changed
    /// and can be nothing else. That makes this the asset-local form of the same invalidation.
    /// <para>
    /// Two things are invalidated. The stored sample, which is the baseline
    /// <see cref="TerrainContact.TryDetectStepCollision"/> differences against — left alone, a
    /// preset switch reads as a rise the vehicle drove onto, and since the vehicle then never
    /// moves the same phantom step is struck again every tick, sixty alerts a second for as long
    /// as the room lives. And the normal filter, whose memory is a low-pass over a surface that no
    /// longer exists; re-settling from <see cref="TerrainNormalFilter.Uninitialised"/> puts the
    /// attitude on the new ground at once rather than easing onto it over a time constant of
    /// terrain that has been deleted.
    /// </para>
    /// <para>
    /// Deterministic: a pure function of the vehicle's own planar position and the terrain now in
    /// force, with no clock, no history and no iteration. Two replays that switch preset at the
    /// same tick re-baseline identically, so a replay hash spanning the switch is unaffected.
    /// </para>
    /// </remarks>
    /// <param name="preStep">Environment sampled by the world at this asset's pre-step position.</param>
    private void RebaselineIfEnvironmentChanged(EnvironmentSample? preStep)
    {
        if (preStep is null
            || HorizontalDistance(preStep.PositionEus, _positionEus) > SameStationToleranceM
            || Math.Abs(preStep.TerrainElevationM - _sample.TerrainElevationM)
                <= TerrainChangeEpsilonM)
        {
            return;
        }

        _filter = TerrainNormalFilter.Uninitialised;
        Settle(deltaSeconds: 0.0);
    }

    /// <summary>Horizontal distance between two scene-frame points, in metres.</summary>
    /// <param name="from">First point; its vertical component is ignored.</param>
    /// <param name="to">Second point; its vertical component is ignored.</param>
    /// <returns>Distance in metres.</returns>
    private static double HorizontalDistance(Vector3 from, Vector3 to)
    {
        double east = to.X - from.X;
        double south = to.Z - from.Z;
        return Math.Sqrt((east * east) + (south * south));
    }

    /// <summary>Samples the ground under the vehicle and resolves its pose onto it.</summary>
    /// <remarks>
    /// The wind-sampling height is carried over from the previous position rather than derived from
    /// a second terrain query. It selects nothing but the altitude the wind field is read at, which
    /// at rover heights is indistinguishable between one step and the next, and avoiding the extra
    /// query keeps the step to exactly one environment sample.
    /// </remarks>
    /// <param name="deltaSeconds">Timestep in seconds; drives the terrain-normal filter's coefficient only.</param>
    [MemberNotNull(nameof(_sample), nameof(_contact))]
    private void Settle(double deltaSeconds)
    {
        var planar = new Vector3((float)_motion.EastM, _positionEus.Y, (float)_motion.SouthM);

        _sample = _environment.Sample(planar, GroundContactGeometry.NormalSpacingM(_profile));

        var resolved = TerrainContact.Resolve(
            planar, _motion.HeadingRad, _profile, _sample, deltaSeconds, _filter);

        _contact = resolved.Contact;
        _filter = resolved.Filter;
        _positionEus = _contact.PositionEus;
    }

    /// <summary>Builds the guidance input, probing the ground ahead while the vehicle is under way.</summary>
    /// <remarks>
    /// The probe sits one stopping distance, one reaction allowance and one footprint radius along
    /// the direction of travel, so a vehicle that must refuse the ground ahead can still stop short
    /// of it. That distance is arithmetic on the current speed — not a search and not an iteration
    /// count — so the step stays a pure function of state, and the probe is skipped entirely in the
    /// settled modes, where there is no motion to refuse.
    /// <para>
    /// <b>The stopping distance is computed at the braking rate the vehicle actually has.</b>
    /// <see cref="IGroundDynamics"/> decelerates at <c>MaxBrakingMps2 * traction</c>, not at
    /// <c>MaxBrakingMps2</c>, so probing against the dry-ground figure understates the real
    /// distance by the reciprocal of the traction: on wet vegetation — a table value of 0.75
    /// derated by <see cref="GroundSurfaces.PrecipitationTractionLoss"/> to about 0.56 — the
    /// vehicle needs nearly twice the room the probe allowed, and drives into the ground the probe
    /// was there to keep it out of. The traction is clamped exactly as
    /// <see cref="GroundConditions.Clamped"/> clamps it, so the rate used here is the rate the
    /// integrator will use and not a second estimate of it.
    /// </para>
    /// <para>
    /// <b>Margin, and where it comes from.</b> Two allowances, each a named mechanism rather than a
    /// tuned number. <see cref="LookaheadReactionSteps"/> steps of travel at the current speed
    /// cover this pipeline's real latency — the step before the setpoint this probe produces is
    /// integrated, and the further step the drivetrain takes to begin chasing it. Then
    /// <see cref="GroundSurfaces.PrecipitationTractionLoss"/> is taken off the measured grip before
    /// the braking rate is derived: grip is measured <em>under</em> the vehicle while the probe
    /// asks about ground it has not reached, which may be worse, and bounding that unknown by the
    /// fraction weather is already documented to cost a surface makes the probe brake as though the
    /// ground ahead were the ground underneath in the rain. It lengthens the stopping distance by a
    /// third, and reuses a documented constant of this model rather than introducing one.
    /// </para>
    /// <para>
    /// Both are <b>advisory</b>: margin against a quasi-static estimate over a procedural height
    /// field, never an assertion that what the probe permits is safe to drive.
    /// </para>
    /// </remarks>
    /// <param name="deltaSeconds">Timestep in seconds, used only for the reaction allowance.</param>
    /// <returns>The contact under the vehicle, plus the look-ahead verdict where one was taken.</returns>
    private GroundGuidanceInput BuildGuidanceInput(double deltaSeconds)
    {
        if (_navigator.Mode is not (GroundGuidanceMode.Driving or GroundGuidanceMode.Reversing
            or GroundGuidanceMode.Manual))
        {
            return new GroundGuidanceInput(_contact);
        }

        double speed = Math.Abs(_motion.ForwardSpeedMps);

        double traction = Math.Clamp(
            _contact.TractionCoefficient * (1.0 - GroundSurfaces.PrecipitationTractionLoss),
            GroundConditions.MinTractionCoefficient,
            1.0);

        double braking = _profile.MaxBrakingMps2 * traction;
        double stopping = braking > 0.0 ? (speed * speed) / (2.0 * braking) : MaxLookaheadM;
        double reaction = speed * LookaheadReactionSteps * Math.Max(0.0, deltaSeconds);

        double reach = Math.Min(
            MaxLookaheadM, _profile.FootprintRadiusM + reaction + stopping);

        // Direction of travel, which is not the heading while reversing. Falling back on the
        // guidance mode at a standstill is what lets a stopped vehicle refuse to reverse into water
        // before it has picked up any speed to infer a direction from.
        double sign = _motion.ForwardSpeedMps != 0.0
            ? (double)Math.Sign(_motion.ForwardSpeedMps)
            : _navigator.Mode == GroundGuidanceMode.Reversing ? -1.0 : 1.0;

        double travelHeading = sign >= 0.0
            ? _motion.HeadingRad
            : CoordinateFrames.NormalizeAngle(_motion.HeadingRad + Math.PI);

        var probe = new Vector3(
            (float)(_motion.EastM + (reach * Math.Sin(travelHeading))),
            _positionEus.Y,
            (float)(_motion.SouthM - (reach * Math.Cos(travelHeading))));

        var ahead = _environment.Sample(probe, GroundContactGeometry.NormalSpacingM(_profile));
        var verdict = Traversability.Evaluate(_profile, ahead, travelHeading);

        return new GroundGuidanceInput(_contact, verdict.Class, verdict.Reason);
    }

    /// <summary>Draws one step's energy from the pack.</summary>
    /// <remarks>
    /// Tractive effort is rolling resistance plus the grade component of weight, floored at zero
    /// because regeneration is not modelled: a rover coasting downhill draws its idle load and no
    /// more, rather than charging. The idle load continues while stopped, which is why a parked
    /// rover still eventually reports a low pack.
    /// </remarks>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    private void ConsumeEnergy(double deltaSeconds)
    {
        double speed = Math.Abs(_motion.ForwardSpeedMps);
        double weight = _profile.MassKg * StandardGravityMps2;
        double rolling = _contact.Surface.RollingResistanceCoefficient * weight;
        double grade = weight * Math.Sin(_contact.GradeRad);
        double tractive = Math.Max(0.0, rolling + grade);

        _drawWatts = IdlePowerW + (tractive * speed / DrivetrainEfficiency);
        _energyWh = Math.Max(0.0, _energyWh - (_drawWatts * deltaSeconds / SecondsPerHour));
    }

    /// <summary>Remaining pack charge as a percentage.</summary>
    private double EnergyPercent =>
        _capacityWh > 0.0 ? Math.Clamp(100.0 * _energyWh / _capacityWh, 0.0, 100.0) : 0.0;
}
