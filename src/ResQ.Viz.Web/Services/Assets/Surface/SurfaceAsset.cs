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

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets.Surface;

/// <summary>A surface asset: a vessel this world owns, integrates and floats on its water.</summary>
/// <remarks>
/// The surface-domain sibling of <see cref="Ground.GroundAsset"/>, and structurally its twin: the
/// SDK's world integrates drones only, so a vessel carries its own integration and implements
/// <see cref="IStepDrivenAsset"/>.
/// <para>
/// <b>The one thing that makes this domain different from the other two.</b> A vessel has no
/// stop. An air asset that loses its link comes down and stays down; a rover stops and stays put
/// indefinitely, for free; a hull stops its propeller and then keeps moving, at the vector sum of
/// the current and the wind, for as long as nobody intervenes. Every design decision below falls
/// out of that: why <c>stop</c> and <c>emergencyStop</c> are documented as not stopping the
/// vessel, why <see cref="SurfaceDomainState.PositionUncertaintyGrowthMps"/> never reaches zero,
/// why running aground is not a fault, and why no advisory here is ever allowed to take the speed
/// authority to zero.
/// </para>
/// <para>
/// <b>What one step does, in order.</b> Re-baseline if the water or the bed under the hull has
/// been replaced since the last step; adopt the environment sample the world took at the
/// pre-step position and resolve the speed ceiling from it; read the setpoint from
/// <see cref="SurfaceNavigator"/>; integrate it through <see cref="ISurfaceDynamics"/>; put the
/// proposed position through <see cref="WaterConstraints.ResolveMotion"/>, which holds the hull
/// at the edge of navigable water and reports what it met; adopt whichever position survived;
/// resample the wave surface for the renderer; draw the step's energy; and raise one event per
/// transition. Events are raised from <see cref="Step"/>, never from <see cref="Capture"/>.
/// </para>
/// <para>
/// <b>Running aground stops a vessel; it never disables one.</b> The clearance derate keeps a
/// floor of <see cref="UnderKeelClearance.AgroundSpeedFactor"/> of the hull's top speed, the
/// combined ceiling is floored at <see cref="RecoveryCeilingMps"/> whatever a zone asks for, and
/// <see cref="WaterConstraints.ResolveMotion"/> permits an aground hull to move towards deeper
/// water. The published <see cref="OperationalState"/> is never
/// <see cref="OperationalState.Faulted"/> for a grounding, because the command catalog's
/// <c>Operable</c> policy excludes that state and it would refuse exactly the commands that get
/// the vessel off. A vessel no command can move is not in a safe state; it is a dead asset
/// drifting downwind.
/// </para>
/// <para>
/// <b>Why events live in Step.</b> <see cref="Step"/> runs exactly once per world tick, so a
/// transition observed there is observed once by construction, and <see cref="Capture"/> is left
/// as a pure projection that may run any number of times per tick — a broadcast frame and a REST
/// read on the same tick do exactly that — without emitting anything. Every event here is an
/// <em>edge</em>; a level-triggered condition such as sitting on a shoal would otherwise emit
/// sixty alerts a second and bury everything else in the log.
/// </para>
/// <para>
/// <b>Why the asset holds an environment sampler.</b> The world samples the environment at the
/// pre-step position, which keeps the arithmetic testable with literals, but a vessel has to be
/// floated on the water it moved <em>onto</em>, so one further sample at the post-step position
/// is unavoidable — and the same sampler answers the look-ahead probe and sweeps a route before
/// a transit is accepted. That is not a determinism break: the sampler is a pure function of
/// position given the room's terrain, water level and current weather, and the weather is
/// advanced exactly once per world step, before the asset pass runs. Every call happens inside
/// <see cref="Step"/> or <see cref="Apply"/>, both of which the room invokes under its own lock,
/// and every one returns a value rather than a view onto anything the room may replace.
/// </para>
/// <para>
/// Advisory throughout. The bed is a procedural height field rather than a survey, the current is
/// a smooth synthetic field rather than a tidal stream atlas, and the hull is a 3-DOF
/// approximation. Nothing here is a navigation guarantee and nothing asserts compliance with any
/// navigation regulation.
/// </para>
/// </remarks>
public sealed partial class SurfaceAsset : IStepDrivenAsset
{
    /// <summary>Zone kind that denies a position fix to any vessel inside it.</summary>
    /// <remarks>
    /// The one mechanism by which a vessel in this simulation loses position quality, and
    /// therefore the only route to <see cref="StationKeepPhase.Degraded"/> and to a
    /// <see cref="DockingAbortReason.PositionLost"/> abort. It is a zone rather than a receiver
    /// model because a zone is data a scenario can already declare, and because denial is a
    /// property of a place far more often than of a hull.
    /// <para>
    /// No zone source ships one today, so the degraded paths are reachable only from a scenario
    /// or a test that declares one. Saying so is better than leaving a state machine with an arm
    /// nobody can explain how to enter.
    /// </para>
    /// </remarks>
    public const string PositionDeniedZoneKind = "gnss-denied";

    /// <summary>Usable pack energy per kilogram of displacement, in watt-hours.</summary>
    /// <remarks>
    /// The same figure the ground domain sizes a rover's pack with, applied to the hull's loaded
    /// displacement. Advisory: it produces endurances of the right order for these envelopes and
    /// nothing should plan against it as though it were a builder's data sheet.
    /// </remarks>
    private const double PackEnergyWhPerKg = 15.0;

    /// <summary>Power drawn with the propeller stopped, in watts: avionics, radio and sensors.</summary>
    private const double HotelPowerW = 60.0;

    /// <summary>Rated propulsion power at the hull's top speed, per kilogram of displacement, in watts.</summary>
    /// <remarks>
    /// Combined with the cube law below it gives a full-speed draw of about eleven kilowatts for
    /// the shipped workboat and an endurance near two hours, which is the right order for a small
    /// electric hull. Advisory; nothing here is a propulsion model.
    /// </remarks>
    private const double RatedPropulsionWPerKg = 8.0;

    /// <summary>Charge below which health is reported as degraded, as a percentage.</summary>
    private const double LowEnergyPercent = 20.0;

    /// <summary>Seconds in an hour, for turning a power draw into a state-of-charge change.</summary>
    private const double SecondsPerHour = 3600.0;

    /// <summary>Furthest ahead the navigability probe ever looks, in metres.</summary>
    /// <remarks>
    /// A cap on a distance that otherwise grows with the hull's coast distance. Probing two
    /// hundred metres ahead answers a question about water the vessel may never reach, and would
    /// refuse passages a turn thirty seconds from now makes irrelevant.
    /// </remarks>
    private const double MaxLookaheadM = 120.0;

    /// <summary>World steps the look-ahead allows for before a commanded change of speed takes effect.</summary>
    /// <remarks>
    /// One step for the probe taken now to reach the navigator's next setpoint, and one more for
    /// the hull to begin answering it. Both are real latencies of this pipeline rather than
    /// padding.
    /// </remarks>
    private const double LookaheadReactionSteps = 2.0;

    /// <summary>Shortest travel a deflected move must still make to count as a move, in metres.</summary>
    /// <remarks>
    /// A micrometre. It is not a physical threshold and is not tuned: it separates a move that
    /// survived the bed contour with something left of it from one the contour consumed entirely,
    /// which is a hull the edge really has stopped and which must be reported as a contact rather
    /// than as an imperceptible slide. Below this the deflection is abandoned and the ordinary
    /// refusal runs, so the contact and the blocked mode are reached by exactly the path they
    /// were before deflection existed.
    /// </remarks>
    private const double MinDeflectedTravelM = 1e-6;

    /// <summary>Ground speed above which an unpowered vessel is reported as drifting, in metres per second.</summary>
    /// <remarks>
    /// A tenth of a metre per second is three hundred and sixty metres in an hour: the point at
    /// which an unpowered vessel's position stops being where the operator left it. The clear
    /// threshold below is deliberately lower, so a hull hovering on the boundary does not raise
    /// and clear the same advisory every second.
    /// </remarks>
    private const double DriftAlertSpeedMps = 0.10;

    /// <summary>Ground speed below which the drift advisory is cleared, in metres per second.</summary>
    private const double DriftClearSpeedMps = 0.05;

    /// <summary>Horizontal distance within which a supplied sample counts as taken here, in metres.</summary>
    /// <remarks>
    /// The step contract says the context's environment sample is taken at this asset's pre-step
    /// position, and the re-baseline test only means anything if that holds — a sample from
    /// somewhere else differs because it is elsewhere, not because the water moved.
    /// </remarks>
    private const double SameStationToleranceM = 0.01;

    /// <summary>Change in the water surface or the bed under a vessel that counts as new environment, in metres.</summary>
    /// <remarks>
    /// Tiny on purpose. The comparison is between two samples of the same fields at the same
    /// coordinates, which agree bit for bit while those fields are unchanged, so anything above
    /// numerical noise is a genuinely different world rather than a different position.
    /// </remarks>
    private const double EnvironmentChangeEpsilonM = 1e-6;

    /// <summary>Most events one vessel may hold between drains.</summary>
    /// <remarks>
    /// A bounded drop policy, not a resize. Every event raised here is an edge, so the queue
    /// cannot grow without something genuinely happening — but a room that stops assembling
    /// frames stops draining, and an unbounded per-asset list behind a stalled consumer is a
    /// leak whatever the intended raise rate. The <em>oldest</em> events are kept and later ones
    /// dropped, because the first transitions are the ones that explain how the vessel got into
    /// the state it is in; the count of what was dropped travels on the next drain.
    /// </remarks>
    private const int MaxQueuedEvents = 64;

    private static readonly FaultCode[] NoFaults = [];
    private static readonly ComponentHealth[] NoComponents = [];
    private static readonly AssetEvent[] NoEvents = [];

    private readonly ISurfaceDynamics _dynamics;
    private readonly SurfaceProfile _profile;
    private readonly VesselWaterProfile _waterProfile;
    private readonly SurfaceNavigator _navigator;
    private readonly IEnvironmentSampler _environment;
    private readonly WaveModel _waves;
    private readonly List<AssetEvent> _events = [];
    private readonly Vector3 _basePositionEus;
    private readonly double _capacityWh;

    /// <summary>Onset memory so a standing fault reports when it started, not when it was seen.</summary>
    private readonly FaultOnsetLedger _faultOnsets = new();

    private SurfaceMotionState _motion;
    private EnvironmentSample _sample;
    private WaterSample _water;
    private SurfaceVelocities _velocities;
    private WaveMotion _wave = WaveMotion.Calm;
    private Vector3 _positionEus;
    private Vector3 _groundVelocityEus;
    private Vector3 _passiveDriftEus;
    private double _waterSurfaceElevationM;
    private double _speedCeilingMps;
    private double _energyWh;
    private double _drawWatts = HotelPowerW;
    private ulong _sequence;
    private int _droppedEvents;

    // The most recent step's clock, so a command that arrives between steps can stamp the event
    // it raises. Nothing has been integrated since, so that is the honest instant to attribute
    // it to — and an asset has no clock of its own to reach for instead.
    private double _simulationTimeSeconds;
    private long _tick = -1;

    // Edge-detection state for the transition events raised from Step. Never read by Capture.
    private bool _wasAground;
    private bool _wasUnsafeClearance;
    private bool _lowEnergyLatched;
    private bool _driftLatched;
    private StationKeepPhase _wasStationKeepPhase = StationKeepPhase.Disengaged;
    private DockingPhase _wasDockingPhase = DockingPhase.Inactive;

    /// <summary>Floats a vessel on the water and prepares it to be stepped.</summary>
    /// <remarks>
    /// The spawn position's vertical component is discarded. A hull's height is the water-surface
    /// elevation in force where it floats, never a commanded value, so honouring a requested
    /// <c>Y</c> would let a caller submerge a vessel or hang one above the sea.
    /// <para>
    /// A vessel spawned on dry land is placed there and reports itself aground rather than
    /// throwing. A scenario row that puts a boat on a hillside is a bad row, and the loader's
    /// contract is to skip bad rows — but a spawn that has already been accepted must produce a
    /// working, commandable asset, not an exception from the middle of building a world.
    /// </para>
    /// </remarks>
    /// <param name="descriptor">Descriptor for this asset; its domain must be <see cref="AssetDomain.Surface"/>.</param>
    /// <param name="dynamics">Motion model to integrate with; its profile becomes this vessel's envelope.</param>
    /// <param name="environment">Sampler used to float the hull, probe ahead, and sweep a route before a transit.</param>
    /// <param name="spawnPositionEus">Spawn position in the scene frame; only the horizontal components are used.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <param name="safety">Emergency-stop and link-loss policy, or null to derive it from the profile.</param>
    /// <param name="waves">Sea-surface model for the visual-only hull motion, or null for the shared default.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The descriptor is not a surface descriptor, or a spawn value is not finite.</exception>
    public SurfaceAsset(
        AssetDescriptor descriptor,
        ISurfaceDynamics dynamics,
        IEnvironmentSampler environment,
        Vector3 spawnPositionEus,
        double headingRad = 0.0,
        SurfaceSafetyPolicy? safety = null,
        WaveModel? waves = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(dynamics);
        ArgumentNullException.ThrowIfNull(environment);

        if (descriptor.Domain != AssetDomain.Surface)
        {
            throw new ArgumentException(
                $"A surface asset needs a surface descriptor; got '{descriptor.Domain}'.",
                nameof(descriptor));
        }

        if (!float.IsFinite(spawnPositionEus.X) || !float.IsFinite(spawnPositionEus.Z))
        {
            throw new ArgumentException("A spawn position must be finite.", nameof(spawnPositionEus));
        }

        if (!double.IsFinite(headingRad))
        {
            throw new ArgumentException("A spawn heading must be finite.", nameof(headingRad));
        }

        Descriptor = descriptor;
        _dynamics = dynamics;
        _profile = dynamics.Profile;
        _environment = environment;
        _waves = waves ?? WaveModel.Default;
        _waterProfile = VesselWaterProfile.From(_profile);
        Safety = safety ?? SurfaceSafetyPolicy.For(_profile);
        _navigator = new SurfaceNavigator(_profile, Safety);

        _motion = SurfaceMotionState
            .DeadInTheWater(spawnPositionEus.X, spawnPositionEus.Z, headingRad)
            .Validated(nameof(headingRad));

        _capacityWh = PackEnergyWhPerKg * _profile.DisplacementKg;
        _energyWh = _capacityWh;

        Adopt(SampleHere());
        _basePositionEus = _positionEus;
        _velocities = _dynamics.Resolve(in _motion, SurfaceConditions.From(_sample, _speedCeilingMps));
        _passiveDriftEus = PassiveDrift(in _velocities, _sample.WindEus);

        // Deliberately left false even when the spawn position is already aground, so the first
        // step announces it. A vessel that arrives on a shoal — placed on dry land, or floated
        // under a preset whose water level does not cover its draft — is the one case where
        // seeding from the sample suppresses the alert an operator most needs: nothing later
        // transitions, so nothing is ever raised, and a boat sitting on a beach appears in the
        // asset list looking healthy. Being aground at tick zero is still entering that state,
        // and it is still an edge, so it is still raised exactly once.
        _wasAground = false;
        _wasUnsafeClearance = false;
    }

    /// <inheritdoc />
    public string AssetId => Descriptor.AssetId;

    /// <inheritdoc />
    public AssetDomain Domain => AssetDomain.Surface;

    /// <inheritdoc />
    public Vector3 PositionEus => _positionEus;

    /// <inheritdoc />
    public AssetDescriptor Descriptor { get; }

    /// <summary>Physical envelope this vessel is integrated within.</summary>
    public SurfaceProfile Profile => _profile;

    /// <summary>Emergency-stop and link-loss policy in force for this vessel.</summary>
    public SurfaceSafetyPolicy Safety { get; }

    /// <summary>Guidance mode as a stable lower-case token, as published in the wire's mode field.</summary>
    /// <remarks>
    /// Exposed so the compatibility projection and the v2 mode string cannot drift apart, the
    /// same reason <see cref="AirAsset.StatusV1"/> exists.
    /// </remarks>
    public string ModeToken => IsEmergencyStopped ? "emergency-stop" : _navigator.ModeToken;

    /// <summary>Whether an emergency stop is latched.</summary>
    /// <remarks>
    /// Tracked separately from the navigator's mode because it also gates command acceptance.
    /// See <see cref="SurfaceSafetyPolicy"/> for what an emergency stop does, and
    /// <see cref="Apply"/> for the release that is always reachable — a drifting vessel that no
    /// command could move would be the worst outcome this whole domain has to avoid.
    /// </remarks>
    public bool IsEmergencyStopped { get; private set; }

    /// <summary>Floor under the speed ceiling, in metres per second, whatever the water advises.</summary>
    /// <remarks>
    /// The surface counterpart of <see cref="Ground.GroundNavigator.RecoveryCeilingMps"/>, and it
    /// exists for the same reason: <b>no advisory may take a vessel's speed authority to zero.</b>
    /// A grounded hull with no way of driving itself off the bank is unrecoverable, and unlike a
    /// bogged rover it does not even stay where it is — it lifts on the tide and goes somewhere
    /// else. Derived from <see cref="UnderKeelClearance.AgroundSpeedFactor"/> rather than picked,
    /// so the floor and the derating curve that approaches it are the same number, and read from
    /// this one property by every site that needs it.
    /// </remarks>
    public double RecoveryCeilingMps => _profile.MaxSpeedMps * UnderKeelClearance.AgroundSpeedFactor;

    /// <summary>Whether the vessel currently has a usable position fix.</summary>
    /// <remarks>
    /// False only inside a zone declaring <see cref="PositionDeniedZoneKind"/>. Reported rather
    /// than acted on directly: it feeds <see cref="StationKeeping"/> and <see cref="Docking"/>,
    /// each of which applies its own documented policy, and it never refuses a command. A vessel
    /// that has stopped knowing where it is needs an operator more than it needs a gate.
    /// </remarks>
    public bool HasPositionFix
    {
        get
        {
            for (int i = 0; i < _sample.Zones.Count; i++)
            {
                if (string.Equals(
                    _sample.Zones[i].Kind, PositionDeniedZoneKind, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A pure function of the context and this asset's own state: no clock, no adaptive
    /// substepping, no convergence-based early exit, and no iteration count that varies with
    /// state. The work per call is fixed by the guidance mode alone, never by the water: one
    /// environment sample to float the hull after the integration, plus one for the look-ahead
    /// probe while the vessel is under power and one for the berth while a docking approach is
    /// running. One further sample is taken on a step where the water level or the bed has been
    /// replaced, to re-baseline — see <see cref="HasEnvironmentChanged"/> — which is
    /// still fixed work rather than a search, and a function of an <em>input</em> rather than of
    /// the vessel's history.
    /// </remarks>
    public void Step(in AssetStepContext context)
    {
        double delta = context.DeltaSeconds;

        // The world already skips the asset pass on a non-positive timestep. Repeated here
        // because an asset stepped directly by a test must behave identically, and because
        // dividing the position delta by zero below would publish an infinite velocity.
        if (!double.IsFinite(delta) || delta <= 0.0)
        {
            return;
        }

        // Tested before the fresh sample is adopted, because afterwards there is nothing left to
        // compare against. See HasEnvironmentChanged for what it is for.
        bool worldChanged = HasEnvironmentChanged(context.Environment);

        // The context's sample is annotated non-nullable, but AssetStepContext is a struct and a
        // default-constructed one carries no sample at all — which is exactly what a test driving
        // this asset directly is likely to hand it. Sampling here is the same value the world
        // would have supplied, so the fallback changes nothing except whether it throws.
        var origin = context.Environment ?? SampleHere();
        Adopt(origin);

        var conditions = SurfaceConditions.From(origin, _speedCeilingMps);
        _velocities = _dynamics.Resolve(in _motion, in conditions);
        _passiveDriftEus = PassiveDrift(in _velocities, conditions.WindEus);

        var guidance = _navigator.Sample(in _motion, BuildGuidanceInput(delta, in conditions));

        var previousMotion = _motion;
        var previousPosition = _positionEus;

        _motion = _dynamics.Step(in previousMotion, guidance.Setpoint, delta, in conditions);

        // The water mask has the last word on where the hull ends up. Both samples are taken
        // within this step against the terrain and water level now in force — the origin one by
        // the world, this one here — so the two positions being differenced always describe the
        // same world, and a preset switch structurally cannot read as a grounding the vessel
        // caused. Nothing stored from a previous step takes part in the comparison.
        var destination = SampleHere();
        var resolution = WaterConstraints.ResolveMotion(
            _waterProfile, origin, destination, _velocities.SpeedOverGroundMps);

        var contact = ShorelineContact.None;
        bool blockedByContact = false;

        // Read before the mask has its say, because it is the question "was this vessel already
        // ashore when the step began?" — which is what decides whether a refusal is an edge the
        // hull met or the ordinary condition of a hull working itself off one.
        bool hereWasNavigable = WaterConstraints.IsNavigable(_waterProfile, origin);

        if (!resolution.IsBlocked)
        {
            Adopt(destination);
        }
        else if (TryDeflect(in previousMotion, origin, out var deflected))
        {
            // The move ran into shoaling water but not squarely into it, so the part of it that
            // followed the contour survives and the vessel is still under way. Nothing is
            // reported: the hull met no edge it was stopped by, and the clearance band it is in
            // is already published and already raises its own transitions.
            _motion = _motion with { EastM = deflected.PositionEus.X, SouthM = deflected.PositionEus.Z };
            Adopt(deflected);
        }
        else
        {
            contact = resolution.Contact;

            // A hull cannot be inside what it met, so the translation is undone in either
            // case. The heading is kept: the vessel really did swing, and reverting that would
            // make the published bow direction disagree with the track it made.
            _motion = _motion with
            {
                EastM = previousMotion.EastM,
                SouthM = previousMotion.SouthM,
            };

            if (hereWasNavigable)
            {
                // A hull that was afloat when the step began has struck an edge, and a strike
                // takes the way off: the velocities go with the translation, and guidance is
                // told, or the vessel opens the throttle into the same beach next step and keeps
                // doing so. The look-ahead cannot catch this one — the edge was discovered only
                // by reaching it.
                _motion = _motion with { SurgeMps = 0.0, SwayMps = 0.0, YawRateRadPerSec = 0.0 };
                blockedByContact = _navigator.Block(contact.Reason);
            }

            // A hull that was already outside the mask keeps its way and its task, and this is
            // what makes running aground recoverable rather than terminal. It has struck
            // nothing — it has been sitting on the bed since before the step — so there is no
            // impact to take the way off, and the surge and rate of turn it has built are the
            // recovery itself: the turn ceiling is
            // <c>speed / <see cref="SurfaceProfile.MinTurnRadiusM"/></c>, so a hull whose surge
            // is reset every refused step cannot gather way, and a hull that cannot gather way
            // cannot swing its bow seaward, and every route off a beach begins by swinging the
            // bow seaward. Latching guidance here would be worse still: it clears the task, so
            // the recovery order would be accepted and then silently abandoned on its own first
            // step. The exemption grants no new freedom, because
            // <see cref="WaterConstraints.ResolveMotion"/> only ever admits a move from a
            // non-navigable position onto bed no higher than the bed already under the hull —
            // an exempt vessel can work itself seaward and can never drive further ashore.
            Adopt(origin);
        }

        // The realised ground velocity, from the position the vessel actually reached. It is
        // preferred to the analytic one out of ISurfaceDynamics.Resolve at every site that
        // publishes a track, because the water mask can hold a hull the analytic velocity still
        // says is making three knots — and a vessel pinned against a beach must not report that
        // it is under way. Horizontal by construction: the vertical component of a vessel's pose
        // is the water surface it floats on, not a velocity it has.
        _groundVelocityEus = new Vector3(
            (float)((_positionEus.X - previousPosition.X) / delta),
            0f,
            (float)((_positionEus.Z - previousPosition.Z) / delta));

        // The hold was evaluated against the pose at the top of the step; the frame about to be
        // published carries the pose at the bottom of it. Re-measuring here is what stops a
        // client recomputing the distance from the station and getting a different answer from
        // the one the vessel reported.
        _navigator.SettleStationKeep(_positionEus);

        var settled = SurfaceConditions.From(_sample, _speedCeilingMps);
        _velocities = _dynamics.Resolve(in _motion, in settled);
        _passiveDriftEus = PassiveDrift(in _velocities, settled.WindEus);

        // Visual only, and evaluated last so nothing above it can read it. Feeding wave motion
        // back into the navigation solution — into an under-keel clearance especially — would
        // ground a hull on a decoration.
        _wave = _waves.Sample(
            _positionEus, context.SimulationTimeSeconds, _motion.HeadingRad, _sample.WindEus, _profile);

        ConsumeEnergy(delta);

        _simulationTimeSeconds = context.SimulationTimeSeconds;
        _tick = context.Tick;

        RaiseStepEvents(in guidance, in contact, blockedByContact, worldChanged);

        _sequence++;
    }

    /// <summary>Whether the water or the bed under this vessel has been replaced since the last step.</summary>
    /// <remarks>
    /// The room's environment is mutable at runtime — a terrain preset switch, a heightmap
    /// upload, a scenario's sea-level override — and every terrain preset carries its own water
    /// level, so a switch moves the sea as well as the bed. An asset sees no revision counter and
    /// does not need one: the world hands it a sample taken at its own pre-step position against
    /// the <em>current</em> world, and it holds one taken at that same position at the end of the
    /// previous step. Nothing moves a vessel between two steps, so any disagreement between those
    /// two is the world having changed and can be nothing else.
    /// <para>
    /// <b>Nothing here has to be repaired, and that is deliberate.</b> The stored sample is
    /// replaced at the top of every step by the one the world just took, and
    /// <see cref="WaterConstraints.ResolveMotion"/> is only ever handed that fresh sample and a
    /// second one taken in the same step — so the two positions being differenced always describe
    /// the same world, and a preset switch structurally cannot read as the vessel having driven
    /// somewhere. The rover this pattern was written for differenced a <em>stored</em> elevation
    /// against a sampled one and reported a preset switch as a permanent collision, sixty alerts
    /// a second for as long as the room lived.
    /// </para>
    /// <para>
    /// What is left is worth reporting rather than suppressing. A vessel that was afloat and is
    /// aground because the sea dropped is in exactly the same state as one that ran onto a
    /// shoal, and the ordinary edge test raises the same event for both — so this predicate
    /// travels into <see cref="RaiseStepEvents"/> and lets that event say which of the two
    /// happened. "The water left" and "you drove onto a bank" call for different responses.
    /// </para>
    /// <para>
    /// Deterministic: a pure function of the vessel's own planar position and the world now in
    /// force, with no clock, no history and no iteration. Two replays that switch preset at the
    /// same tick re-baseline identically.
    /// </para>
    /// </remarks>
    /// <param name="preStep">Environment sampled by the world at this asset's pre-step position.</param>
    /// <returns><see langword="true"/> when the world under this vessel is not the one it was floated on.</returns>
    private bool HasEnvironmentChanged(EnvironmentSample? preStep) =>
        preStep is not null && IsSameStation(preStep) && HasWorldChanged(preStep);

    /// <summary>Whether a supplied sample was taken where this vessel is.</summary>
    /// <param name="sample">Sample to test.</param>
    /// <returns><see langword="true"/> when it is within <see cref="SameStationToleranceM"/>.</returns>
    private bool IsSameStation(EnvironmentSample sample)
    {
        double east = sample.PositionEus.X - _positionEus.X;
        double south = sample.PositionEus.Z - _positionEus.Z;
        return Math.Sqrt((east * east) + (south * south)) <= SameStationToleranceM;
    }

    /// <summary>Whether the water surface or the bed has changed since the stored sample.</summary>
    /// <remarks>
    /// Both are compared, and both matter. A heightmap upload moves the bed and leaves the sea
    /// where it was, changing the under-keel clearance; a preset switch usually moves both. The
    /// dry-land case is a change of kind rather than of value, so it is compared as one.
    /// </remarks>
    /// <param name="sample">Sample taken against the world now in force.</param>
    /// <returns><see langword="true"/> when the world under this vessel is not the one it was floated on.</returns>
    private bool HasWorldChanged(EnvironmentSample sample)
    {
        if (sample.IsWater != _sample.IsWater)
        {
            return true;
        }

        if (Math.Abs(sample.TerrainElevationM - _sample.TerrainElevationM) > EnvironmentChangeEpsilonM)
        {
            return true;
        }

        double surface = sample.WaterSurfaceElevationM ?? sample.TerrainElevationM;
        return Math.Abs(surface - _waterSurfaceElevationM) > EnvironmentChangeEpsilonM;
    }

    /// <summary>Takes an environment sample as this vessel's own, and re-derives everything from it.</summary>
    /// <remarks>
    /// The single place the stored sample, the water classification, the floating height, the
    /// scene position and the speed ceiling are set, so no two of them can ever describe
    /// different instants. The position is rebuilt from <see cref="SurfaceMotionState"/>, which
    /// is the authoritative planar pose; this method only decides what height it floats at.
    /// </remarks>
    /// <param name="sample">Environment sampled at this vessel's current planar position.</param>
    [MemberNotNull(nameof(_sample), nameof(_water))]
    private void Adopt(EnvironmentSample sample)
    {
        _sample = sample;
        _water = WaterConstraints.Evaluate(_waterProfile, sample);

        // The mean water surface, never the wave-displaced one. Under-keel clearance is measured
        // against the mean surface, and a hull that grounded in a wave trough here would be
        // grounding on a decoration. On dry land the hull sits on the ground it is stranded on.
        _waterSurfaceElevationM = sample.WaterSurfaceElevationM ?? sample.TerrainElevationM;
        _positionEus = _motion.ToPositionEus(_waterSurfaceElevationM);
        _speedCeilingMps = ResolveSpeedCeiling(_water);
    }

    /// <summary>Tries to slide a refused move along the bed contour instead of cancelling it.</summary>
    /// <remarks>
    /// The recovery path for a hull the set is holding against a bank. The mask's last permitted
    /// position is by construction the shallowest navigable one, so once a vessel is pinned there
    /// every move it makes carries some inshore component and cancelling whole moves leaves it
    /// unable to build way, unable to turn, and therefore unable to leave — accepting recovery
    /// orders it can never execute. <see cref="WaterConstraints.DeflectAlongEdge"/> drops only
    /// the inshore part; what is left runs along the contour or back down the slope, which is
    /// motion the shoal never objected to.
    /// <para>
    /// The deflected point is sampled and classified before it is accepted, so this can only ever
    /// place the hull somewhere the mask would have admitted it anyway. When the contour consumes
    /// the whole move, or the deflected point is refused in its own right, the caller falls back
    /// to the ordinary refusal and the vessel is stopped and reported exactly as before.
    /// </para>
    /// <para>
    /// Costs one extra terrain sample, and only on a step that was already refused. Deterministic:
    /// the branch is decided by the mask, which is itself a function of position and the world.
    /// </para>
    /// </remarks>
    /// <param name="previous">Motion state at the start of the step, holding the position to deflect from.</param>
    /// <param name="origin">Environment sampled at that position, read for the bed normal.</param>
    /// <param name="sample">The environment at the deflected position, when one was accepted.</param>
    /// <returns><see langword="true"/> when a deflected position was found and may be occupied.</returns>
    private bool TryDeflect(
        in SurfaceMotionState previous, EnvironmentSample origin, out EnvironmentSample sample)
    {
        sample = origin;

        var fromEus = new Vector3(
            (float)previous.EastM, (float)_environment.SeaLevelM, (float)previous.SouthM);
        var toEus = new Vector3(
            (float)_motion.EastM, (float)_environment.SeaLevelM, (float)_motion.SouthM);

        var slidEus = WaterConstraints.DeflectAlongEdge(fromEus, toEus, origin.TerrainNormalEus);

        // Nothing to deflect against, or nothing left after deflecting: the edge stopped the hull.
        if (slidEus == toEus || Vector3.Distance(slidEus, fromEus) < MinDeflectedTravelM)
        {
            return false;
        }

        var probe = _environment.Sample(slidEus, _profile.FootprintRadiusM);

        if (!WaterConstraints.IsNavigable(_waterProfile, probe))
        {
            return false;
        }

        sample = probe;
        return true;
    }

    /// <summary>Samples the environment at this vessel's current planar position.</summary>
    /// <remarks>
    /// Probed at the water level in force rather than at the hull's own floating height, so the
    /// wind read into the sample is the wind at the waterline — the same convention
    /// <see cref="WaterConstraints.CheckRoute"/> uses, so a route preview and the step it
    /// predicts read the same air. The normal half-spacing is the hull's footprint radius:
    /// sampling the bed far finer than the hull makes the bathymetry chatter on procedural noise,
    /// which shows up as an under-keel warning flickering while the vessel holds a steady line.
    /// </remarks>
    /// <returns>A fully populated sample.</returns>
    private EnvironmentSample SampleHere() => _environment.Sample(
        _motion.ToPositionEus(_environment.SeaLevelM), _profile.FootprintRadiusM);

    /// <summary>The one place a zone advisory and the clearance derate are combined.</summary>
    /// <remarks>
    /// <see cref="SurfaceConditions.SpeedCeilingMps"/> is documented as arriving already
    /// resolved, because the water layer is the only party that knows how a zone limit and a
    /// grounding derate combine; this method is that layer. The derate goes through
    /// <see cref="UnderKeelClearance.DerateSpeedMps"/> rather than multiplying by the state's
    /// speed factor, so the curve documented as canonical is demonstrably the curve the
    /// integrator obeys.
    /// <para>
    /// The result is floored at <see cref="RecoveryCeilingMps"/>. A zone may legitimately declare
    /// a speed limit of zero, and a hull handed a ceiling of zero cannot be driven off the shoal
    /// it is sitting on — so the floor is the difference between an advisory and a trap. The zone
    /// still slows the vessel to a crawl, which is what a no-entry-speed advisory is asking for;
    /// it simply cannot immobilise it.
    /// </para>
    /// </remarks>
    /// <param name="water">Water classification at the vessel, carrying its zones and clearance.</param>
    /// <returns>The ceiling in metres per second. Never zero.</returns>
    private double ResolveSpeedCeiling(WaterSample water)
    {
        double ceiling = _profile.MaxSpeedMps;

        if (water.AdvisorySpeedLimitMps is { } zone && double.IsFinite(zone))
        {
            ceiling = Math.Min(ceiling, Math.Max(0.0, zone));
        }

        return Math.Max(RecoveryCeilingMps, UnderKeelClearance.DerateSpeedMps(water.Clearance, ceiling));
    }

    /// <summary>Velocity an unpowered hull would make good here, in metres per second.</summary>
    /// <remarks>
    /// Current <em>and</em> wind, which is what makes it the honest disturbance rather than half
    /// of one. The current term is read off <see cref="SurfaceVelocities.DriftVelocityEus"/>, so
    /// the coupling factor is applied by the motion model and not by a second copy of it here;
    /// the wind term reads <see cref="SurfaceProfile.LeewayFraction"/>, which is documented as
    /// the single definition of how much wind a hull feels, rather than restating its algebra.
    /// <para>
    /// This is the vector two separate things need. It is the disturbance a station keep is
    /// fighting, so it decides the remaining control authority; and it is the rate an unpowered
    /// hull's position becomes uncertain at, so it is
    /// <see cref="SurfaceDomainState.PositionUncertaintyGrowthMps"/>. Computing it once means
    /// those two can never disagree about the same vessel in the same weather.
    /// </para>
    /// </remarks>
    /// <param name="velocities">Resolved velocities, read for the current-driven drift.</param>
    /// <param name="windEus">Wind velocity at the vessel, in metres per second, in the scene frame.</param>
    /// <returns>The horizontal drift velocity in the scene frame.</returns>
    private Vector3 PassiveDrift(in SurfaceVelocities velocities, Vector3 windEus)
    {
        float leeway = (float)_profile.LeewayFraction;

        return new Vector3(
            velocities.DriftVelocityEus.X + (windEus.X * leeway),
            0f,
            velocities.DriftVelocityEus.Z + (windEus.Z * leeway));
    }

    /// <summary>Builds the guidance input, probing the water ahead while the vessel is under power.</summary>
    /// <remarks>
    /// The probe sits one coast distance, one reaction allowance and one footprint radius along
    /// the <em>track</em>, so a vessel that must refuse the water ahead can still take its way
    /// off before reaching it.
    /// <para>
    /// <b>Course and speed made good, never heading and surge.</b> Those are different
    /// quantities and they diverge whenever there is set, wind or sideslip — which afloat is
    /// nearly always. A vessel crabbing across a beam current neither points where it is going
    /// nor covers ground at the speed its log reads, so a probe laid off along the bow at speed
    /// through the water inspects water the hull will never enter while ignoring the water it is
    /// about to be in: it clears a passage the tide is setting the vessel out of and refuses one
    /// the vessel is already set clear of. <see cref="SurfaceVelocities.CourseOverGroundRad"/>
    /// and <see cref="SurfaceVelocities.SpeedOverGroundMps"/> are published precisely because
    /// they are not the heading and the surge, and this geometry is the reason they exist. Going
    /// astern needs no special case either: a course made good already points the way the hull is
    /// actually moving.
    /// </para>
    /// <para>
    /// <b>The distance is the one the integrator delivers, in the quantity the direction is
    /// measured in.</b> A displacement hull has no brake: with the throttle cut the surge relaxes
    /// exponentially with <see cref="SurfaceProfile.SurgeTimeConstantSec"/>, so <c>tau_u</c> is
    /// the horizon the vessel is committed over, and the ground it makes good across that horizon
    /// is its speed over ground times it. Two ways to get this wrong, and the domains here have
    /// shipped one each: probing against a square-root braking profile understates the horizon
    /// badly — the ground domain looked ahead with dry-ground braking while the integrator braked
    /// with traction — and probing a ground distance at a water-relative speed mis-states the
    /// reach by the whole of the set. Direction and distance here are both ground quantities, so
    /// the probe and the track it is laid off along cannot come to disagree. The estimate errs
    /// long where the set runs with the vessel, which is the direction that keeps the probe ahead
    /// of the hull.
    /// </para>
    /// <para>
    /// <b>It stands down once the hull is inside its own clearance advisory.</b> A vessel already
    /// in the band <see cref="UnderKeelClearanceClass.Marginal"/> and below is within a reach of
    /// the boundary the mask drew, so <em>every</em> track it can make begins with an inshore
    /// component — including the ones that end in deep water — and a track-following probe would
    /// refuse the lot. That costs nothing in protection and everything in recoverability: the mask
    /// refuses any move into blocked water in the first place, so the probe is not what keeps a
    /// hull out of a shoal down there; it is only what would take the throttle away from the one
    /// manoeuvre that gets a pinned hull off, which is the immobilised-rover failure this domain
    /// is written against. In that band the authorities that work are the ones that only ever
    /// permit motion away from the shoal —
    /// <see cref="WaterConstraints.DeflectAlongEdge"/>, the clearance derate and the aground
    /// advisory — and this is the exemption the navigator already makes for a hull that is
    /// <em>aground</em>, extended to one the same water is about to strand.
    /// </para>
    /// <para>
    /// The distance is arithmetic on the current speed, not a search and not an iteration count,
    /// so the step stays a pure function of state; and the probe is skipped entirely when no
    /// control law is asking for thrust, where there is no commanded motion to refuse.
    /// </para>
    /// <para>
    /// <b>Advisory.</b> A margin over a quasi-static estimate on a procedural bed, never an
    /// assertion that what the probe permits is safe to navigate.
    /// </para>
    /// </remarks>
    /// <param name="deltaSeconds">Timestep in seconds, used for the reaction allowance and the docking clock.</param>
    /// <param name="conditions">Clamped conditions at the vessel, read for the wind.</param>
    /// <returns>Everything the navigator is allowed to see this step.</returns>
    private SurfaceGuidanceInput BuildGuidanceInput(double deltaSeconds, in SurfaceConditions conditions)
    {
        // Only a berthing approach has a destination worth re-checking every step, and it is the
        // one operation with an abort that depends on the answer. A transit's destination is
        // vetted once, when the command is accepted, and again by the look-ahead as the vessel
        // closes on it — re-sweeping it here would make the per-step sample count a function of
        // whether a target happened to be assigned.
        var berth = _navigator.Berth;

        var input = new SurfaceGuidanceInput(
            DeltaSeconds: deltaSeconds,
            SpeedCeilingMps: _speedCeilingMps,
            Velocities: _velocities,
            PassiveDriftEus: _passiveDriftEus,
            WindEus: conditions.WindEus,
            HasPositionFix: HasPositionFix,
            IsHereNavigable: _water.IsNavigable,
            IsTargetNavigable: berth is null || IsNavigableAt(berth.BerthEus));

        if (!_navigator.IsUnderPower
            || _water.Clearance.Class != UnderKeelClearanceClass.Safe)
        {
            return input;
        }

        // Ground quantities throughout: the distance the hull makes good over its coast horizon,
        // laid off along the track it is actually making. Neither term reads the surge or the
        // heading, because under a set neither of those describes where this vessel is going.
        double speed = _velocities.SpeedOverGroundMps;
        double coast = speed * _profile.SurgeTimeConstantSec;
        double reaction = speed * LookaheadReactionSteps * deltaSeconds;
        double reach = Math.Min(MaxLookaheadM, _profile.FootprintRadiusM + coast + reaction);

        var offset = CoordinateFrames.BearingToEusVector(_velocities.CourseOverGroundRad, reach);

        var ahead = _environment.Sample(
            new Vector3(
                _positionEus.X + offset.X, (float)_environment.SeaLevelM, _positionEus.Z + offset.Z),
            _profile.FootprintRadiusM);

        var verdict = WaterConstraints.Evaluate(_waterProfile, ahead);

        return input with { AheadClass = verdict.Class, AheadReason = verdict.Reason };
    }

    /// <summary>Classifies the water at a point for this hull, sampled now.</summary>
    /// <remarks>
    /// Read-only, and taken under the owning room's lock like every other sample here: it returns
    /// a value, never a view onto anything the room may replace beneath it. Probed at the water
    /// level in force, for the same reason <see cref="SampleHere"/> is.
    /// </remarks>
    /// <param name="positionEus">Point to evaluate, in the scene frame; the vertical component is ignored.</param>
    /// <returns>The classification and the quantities behind it.</returns>
    private WaterSample EvaluateAt(Vector3 positionEus) => WaterConstraints.Evaluate(
        _waterProfile,
        _environment.Sample(
            new Vector3(positionEus.X, (float)_environment.SeaLevelM, positionEus.Z),
            _profile.FootprintRadiusM));

    /// <summary>Whether a hull may occupy a point, sampled now.</summary>
    /// <param name="positionEus">Point to test, in the scene frame; the vertical component is ignored.</param>
    /// <returns><see langword="true"/> when the point is navigable or merely cautionary.</returns>
    private bool IsNavigableAt(Vector3 positionEus) => EvaluateAt(positionEus).IsNavigable;

    /// <summary>Draws one step's energy from the pack.</summary>
    /// <remarks>
    /// Propulsion power follows the cube of <b>speed through the water</b>, not speed over
    /// ground, and the distinction is the whole point of keeping the two apart: resistance is a
    /// water-relative phenomenon, so a vessel stemming a foul tide burns full power for no ground
    /// progress at all, and one running with it makes six knots on almost nothing. Publishing
    /// ground speed and then billing the propeller for it would invert exactly that.
    /// <para>
    /// The hotel load continues with the propeller stopped, which is why a drifting vessel still
    /// eventually reports a low pack. Advisory: a cube law about a rated power, not a propulsion
    /// model.
    /// </para>
    /// </remarks>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    private void ConsumeEnergy(double deltaSeconds)
    {
        double rated = RatedPropulsionWPerKg * _profile.DisplacementKg;
        double fraction = _profile.MaxSpeedMps > 0.0
            ? Math.Abs(_motion.SpeedThroughWaterMps) / _profile.MaxSpeedMps
            : 0.0;

        _drawWatts = HotelPowerW + (rated * fraction * fraction * fraction);
        _energyWh = Math.Max(0.0, _energyWh - (_drawWatts * deltaSeconds / SecondsPerHour));
    }

    /// <summary>Remaining pack charge as a percentage.</summary>
    private double EnergyPercent =>
        _capacityWh > 0.0 ? Math.Clamp(100.0 * _energyWh / _capacityWh, 0.0, 100.0) : 0.0;
}
