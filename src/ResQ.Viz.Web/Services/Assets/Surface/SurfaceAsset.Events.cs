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

namespace ResQ.Viz.Web.Services.Assets.Surface;

// The event half of SurfaceAsset: observing state transitions once per step and queueing one
// event per edge. Split from the projection half because the two have opposite disciplines — a
// capture must be repeatable within a tick and raise nothing, an event pass must run exactly once
// and raise something — and keeping them apart is what stops one acquiring the other's habits.
// The type's summary lives on the primary declaration in SurfaceAsset.cs.
public sealed partial class SurfaceAsset
{
    /// <summary>Event code raised on the transition into a change of water level or bed.</summary>
    /// <remarks>
    /// Not a vessel event at all, strictly: it says the world under the vessel was replaced. It
    /// is raised because the state changes it causes are otherwise indistinguishable from ones
    /// the vessel caused — a boat that is suddenly aground because the tide went out looks exactly
    /// like one that drove onto a bank — and the two call for entirely different responses.
    /// </remarks>
    public const string EnvironmentChangedCode = "surface.environment.changed";

    /// <summary>Event code raised when a passage is refused because the water ahead is not navigable.</summary>
    public const string BlockedCode = "surface.blocked";

    /// <summary>Event code raised when a vessel that was held against the edge of navigable water gets free.</summary>
    /// <remarks>
    /// One clearing code for both kinds of contact — <see cref="ShorelineContact.ShorelineCode"/>
    /// and <see cref="ShorelineContact.ShoalCode"/> — because what an operator needs to hear is
    /// that the vessel is no longer pinned, and which edge it was pinned against was said when it
    /// met one. A matched pair per edge kind would also make the pairing depend on the bed under a
    /// stationary hull not changing, which is precisely what does change here.
    /// </remarks>
    public const string ContactClearedCode = "surface.collision.cleared";

    /// <summary>Event code raised when a commanded position is reached.</summary>
    public const string TargetReachedCode = "surface.targetReached";

    /// <summary>Event code raised when an unpowered vessel starts making way over the ground.</summary>
    public const string DriftingCode = "surface.drifting";

    /// <summary>Event code raised when an unpowered vessel stops making way over the ground.</summary>
    public const string DriftingClearedCode = "surface.drifting.cleared";

    /// <summary>Event code raised when the pack falls below the return reserve.</summary>
    public const string EnergyLowCode = "surface.energyLow";

    /// <summary>Event code raised once when the queue has had to drop events.</summary>
    public const string EventsDroppedCode = "surface.events.dropped";

    // Trailing-edge state for the shoreline contact. Held here rather than beside the other
    // edge-detection fields because it is read and written by nothing but the event pass, and a
    // latch that only one pass touches is one nothing else can quietly start depending on.
    private bool _shorelineContactLatched;

    /// <summary>Whether the vessel is currently being held against the edge of navigable water.</summary>
    /// <remarks>
    /// <b>The level behind <see cref="ShorelineContact.ShorelineCode"/>.</b> Meeting an edge is an
    /// event and is raised once; <em>remaining</em> against it is a condition, and a condition has
    /// to be readable at any moment rather than re-announced sixty times a second. Anything asking
    /// whether a hull is still pinned — an operator display, a task allocator deciding whether it
    /// is assignable — reads this and never counts events. True from the step the vessel is
    /// stopped by an edge until the first step a move of its own is allowed through again, which is
    /// also the step <see cref="ContactClearedCode"/> is raised on.
    /// </remarks>
    public bool IsInShorelineContact => _shorelineContactLatched;

    /// <inheritdoc />
    /// <remarks>
    /// Destructive, as the contract requires: an event delivered twice would be counted twice.
    /// When the bounded queue has had to drop anything since the last drain, one extra event says
    /// so and the counter resets — so a stalled consumer learns that it missed something rather
    /// than silently receiving a partial history.
    /// </remarks>
    public IReadOnlyList<AssetEvent> DrainEvents()
    {
        if (_events.Count == 0 && _droppedEvents == 0)
        {
            return NoEvents;
        }

        if (_droppedEvents > 0)
        {
            int dropped = _droppedEvents;
            _droppedEvents = 0;
            _events.Add(new AssetEvent(
                AssetId,
                EventsDroppedCode,
                AssetEventSeverity.Warning,
                $"{dropped} event(s) were dropped because nothing drained this asset's queue.",
                _simulationTimeSeconds,
                _tick));
        }

        var drained = _events.ToArray();
        _events.Clear();
        return drained;
    }

    /// <summary>Raises an event for every transition this step observed, and for nothing else.</summary>
    /// <remarks>
    /// Called once per step, from <see cref="Step"/>, after the hull has been floated. Every
    /// branch here is an <b>edge</b>: the guidance flags are true on exactly the call that made
    /// the transition, the water and phase conditions are compared against the values carried
    /// from the previous step, and the drift and low-energy advisories are latched with
    /// hysteresis rather than level-triggered. A vessel sitting on a shoal would otherwise emit
    /// sixty alerts a second and bury everything else in the log — which is precisely the defect
    /// this discipline exists to prevent.
    /// <para>
    /// A shoreline contact was once the exception, raised whenever the water mask refused a move,
    /// on the reasoning that meeting the same beach twice really is two contacts. That reasoning
    /// is sound and it was applied to the wrong quantity. A refusal is not a meeting: it is a
    /// <em>level</em>, true for every step a current keeps pressing a hull against an edge and
    /// true forever for a vessel on dry land, so the exception produced an alert per tick for
    /// exactly the vessels most in need of a readable log. The two are separated here — the
    /// leading edge is the contact and is raised once, remaining pinned is the condition
    /// <see cref="IsInShorelineContact"/> publishes, and getting free is the trailing edge and
    /// raises <see cref="ContactClearedCode"/>. A vessel that gets clear and meets the beach again
    /// has genuinely contacted twice, and raises twice. The block a contact triggers is an edge in
    /// its own right, so a fresh contact still produces one contact and one refusal.
    /// </para>
    /// <para>
    /// <b>The general rule, which the air and ground domains want too.</b> Anything read from a
    /// predicate a persisting cause keeps true is a level, however event-like the moment it first
    /// becomes true feels: raise on the leading edge, publish the level as readable state, raise a
    /// clearing event on the trailing edge, and never let a condition's duration decide how many
    /// entries reach the log.
    /// </para>
    /// <para>
    /// The intermediate docking stages are deliberately not events. They are edges and would be
    /// well behaved, but an approach that ran to plan would then produce four entries saying
    /// nothing went wrong; the stage is published continuously on the mission state instead, and
    /// only the three outcomes that need an operator are raised.
    /// </para>
    /// </remarks>
    /// <param name="guidance">Outcome the navigator returned this step, carrying its edge flags.</param>
    /// <param name="contact">Edge of navigable water met while moving, or <see cref="ShorelineContact.None"/>.</param>
    /// <param name="blockedByContact">True when that contact latched the navigator into a block.</param>
    /// <param name="worldChanged">True when the water level or the bed under the vessel was replaced this step.</param>
    private void RaiseStepEvents(
        in SurfaceGuidanceOutcome guidance,
        in ShorelineContact contact,
        bool blockedByContact,
        bool worldChanged)
    {
        if (worldChanged)
        {
            Raise(
                EnvironmentChangedCode,
                AssetEventSeverity.Info,
                "The water level or the bed under the vessel changed; depth and clearance were "
                + "re-read against the new environment.");
        }

        if (guidance.HasReachedTarget)
        {
            Raise(TargetReachedCode, AssetEventSeverity.Info, "Reached the commanded position.");
        }

        if (guidance.HasBecomeBlocked)
        {
            RaiseBlocked(guidance.BlockingReason);
        }

        RaiseShorelineContactEvents(in contact);

        if (blockedByContact)
        {
            RaiseBlocked(contact.Reason);
        }

        RaiseWaterConditionEvents(worldChanged);
        RaiseStationKeepEvents();
        RaiseDockingEvents();
        RaiseDriftEvents();
        RaiseEnergyEvents();
    }

    /// <summary>Raises the shoreline contact and its clearance, one apiece per pin.</summary>
    /// <remarks>
    /// The level-versus-edge separation described on <see cref="RaiseStepEvents"/>, in one place.
    /// <paramref name="contact"/> carries the level: the water mask sets it on every step it
    /// refuses a move, so it stays set for as long as whatever presses the hull at the edge keeps
    /// pressing, and indefinitely for a hull on dry land that cannot move at all. Only the two
    /// transitions of that level are worth an entry in the log.
    /// <para>
    /// A move the bed deflected rather than refused arrives as <see cref="ShorelineContact.None"/>
    /// and so clears the latch, which is right: a hull that slid along the contour with travel
    /// left is under way, not held. Stopped squarely a step later, it has contacted twice, and it
    /// really has. A pure function of this step's resolution and the latch carried from the last,
    /// so two replays raise the same events on the same ticks.
    /// </para>
    /// </remarks>
    /// <param name="contact">Edge met while moving this step, or <see cref="ShorelineContact.None"/>.</param>
    private void RaiseShorelineContactEvents(in ShorelineContact contact)
    {
        if (contact.HasContacted)
        {
            if (_shorelineContactLatched)
            {
                // Still pinned. The condition is published on IsInShorelineContact and the
                // clearance band raises its own transitions; nothing here has changed.
                return;
            }

            _shorelineContactLatched = true;

            Raise(
                contact.Code ?? ShorelineContact.ShorelineCode,
                contact.Severity,
                $"Met the edge of navigable water at {contact.ImpactSpeedMps:0.0} m/s "
                + $"({WaterConstraints.ReasonCode(contact.Reason)}). The vessel is stopped, still "
                + "commandable, and may be driven towards deeper water.");
            return;
        }

        if (!_shorelineContactLatched)
        {
            return;
        }

        _shorelineContactLatched = false;

        Raise(
            ContactClearedCode,
            AssetEventSeverity.Info,
            "Advisory: no longer held against the edge of navigable water; the vessel's moves are "
            + "being allowed through again.");
    }

    /// <summary>Raises the grounding and under-keel transitions, at most one per step.</summary>
    /// <remarks>
    /// Three levels — on the bed, afloat inside the advisory margin, and clear of it — collapsed
    /// into one edge test, so a vessel moving from a shoal onto a bank raises the grounding once
    /// rather than a restoration followed by a fresh warning. The restored event fires only when
    /// the vessel was impaired and is not any more, which is what makes it meaningful rather than
    /// routine.
    /// <para>
    /// <b>All three levels are read off <see cref="WaterConstraints.ContactAt"/>, never off
    /// <see cref="WaterSample.IsNavigable"/>.</b> The mask is a planning verdict cut at draft plus
    /// the advisory margin, so it refuses water a hull floats in quite happily, and it refuses a
    /// prohibited zone in any depth at all. Deriving grounding from it publishes
    /// <see cref="UnderKeelClearance.AgroundCode"/> — an alert — for a vessel under way with water
    /// under its keel, and for one merely turned back at the edge of a no-go area. Worse, because
    /// every refusal the clearance is responsible for already implies
    /// <see cref="UnderKeelClearanceState.IsUnsafe"/>, an unsafe-clearance arm guarded on
    /// <c>!aground</c> can never be entered, so the one level that still has time left in it is
    /// never announced at all. An operator responds differently to "you are on the bottom" and to
    /// "you have less water under you than your margin", which is why they carry different codes
    /// and different severities here.
    /// </para>
    /// <para>
    /// A refusal that is not about the bed — a prohibited zone, or a shoreline met in deep water —
    /// deliberately raises nothing here. It is a decision about where the vessel may go rather
    /// than a change in what the hull is doing about the bed; it is already raised as
    /// <see cref="BlockedCode"/> and published continuously as the surface state's
    /// <c>IsInsideWaterMask</c> flag, which is <see cref="WaterSample.IsNavigable"/> itself.
    /// </para>
    /// </remarks>
    /// <param name="worldChanged">True when the environment rather than the vessel is what moved.</param>
    private void RaiseWaterConditionEvents(bool worldChanged)
    {
        var contact = WaterConstraints.ContactAt(_water);
        bool aground = contact == HullContactState.OnTheBed;
        bool unsafeClearance = contact == HullContactState.InsideSafetyMargin;

        if (aground == _wasAground && unsafeClearance == _wasUnsafeClearance)
        {
            return;
        }

        bool wasImpaired = _wasAground || _wasUnsafeClearance;
        _wasAground = aground;
        _wasUnsafeClearance = unsafeClearance;

        string cause = worldChanged
            ? "the water level or the bed changed under the vessel"
            : "the vessel moved into it";

        if (aground)
        {
            Raise(
                UnderKeelClearance.AgroundCode,
                UnderKeelClearance.SeverityOf(UnderKeelClearanceClass.Aground),
                // Worded off the clearance rather than off the mask's refusal code, so this
                // sentence is only ever printed about a hull that is genuinely on the ground.
                $"Advisory: the hull is resting on the bed "
                + $"({_water.Clearance.ClearanceM:0.00} m under the keel); {cause}. The vessel "
                + "keeps a derated speed ceiling and accepts every command that recovers it.");
            return;
        }

        if (unsafeClearance)
        {
            Raise(
                UnderKeelClearance.UnsafeClearanceCode,
                UnderKeelClearance.SeverityOf(_water.Clearance.Class),
                $"Advisory: {_water.Clearance.ClearanceM:0.00} m under the keel, inside the "
                + $"hull's {_water.Clearance.SafeMarginM:0.00} m safe margin; {cause}.");
            return;
        }

        if (wasImpaired)
        {
            Raise(
                UnderKeelClearance.ClearanceRestoredCode,
                AssetEventSeverity.Info,
                $"Advisory: no longer on the bed and no longer inside the hull's "
                + $"{_water.Clearance.SafeMarginM:0.00} m safe margin; "
                + $"{_water.Clearance.ClearanceM:0.00} m under the keel.");
        }
    }

    /// <summary>Raises the station-keeping phase transitions.</summary>
    /// <remarks>
    /// Every phase is named, and the message comes from the transition actually taken rather than
    /// from the destination phase alone. Leaving the tolerance radius and returning to it are
    /// opposite transitions, and while <see cref="StationKeepPhase.InsideRadius"/> and
    /// <see cref="StationKeepPhase.Correcting"/> shared one arm the vessel announced that the hold
    /// was <em>nominal again</em> at the moment it began losing ground. A wrong statement in an
    /// operator's log is worse than no statement, because it is acted on.
    /// <para>
    /// Saturation is the one worth reading twice. It fires while the vessel is still <em>on</em>
    /// station, and says the disturbance has reached the effort the hold is permitted to spend —
    /// so an operator who acts on it retasks a vessel that is still where they left it, rather
    /// than one already going downwind.
    /// </para>
    /// <para>
    /// The arms are exhaustive over <see cref="StationKeepPhase"/>; the discard exists only
    /// because C# requires a value for a phase that does not exist yet, and it raises nothing. A
    /// phase added later gets a message written for it instead of silently inheriting one written
    /// for something else — which is exactly how the defect above got in.
    /// </para>
    /// </remarks>
    private void RaiseStationKeepEvents()
    {
        var phase = _navigator.StationKeepOutcome.Phase;

        if (phase == _wasStationKeepPhase)
        {
            return;
        }

        var previous = _wasStationKeepPhase;
        _wasStationKeepPhase = phase;

        var outcome = _navigator.StationKeepOutcome;

        switch (phase)
        {
            case StationKeepPhase.Disengaged:
                Raise(
                    StationKeeping.ReleasedCode,
                    AssetEventSeverity.Info,
                    "Station keeping released; the vessel is no longer holding a position.");
                return;

            case StationKeepPhase.Saturated:
                Raise(
                    StationKeeping.SaturatedCode,
                    AssetEventSeverity.Warning,
                    $"Advisory: the hold has no effort left — {outcome.DriftSpeedMps:0.00} m/s of "
                    + $"drift against a {outcome.MaxEffortMps:0.00} m/s allowance. The vessel is "
                    + "still on best effort and will begin losing station.");
                return;

            case StationKeepPhase.Degraded:
                Raise(
                    StationKeeping.DegradedCode,
                    AssetEventSeverity.Warning,
                    $"Advisory: the hold has lost position quality ({outcome.DegradedReason}).");
                return;

            case StationKeepPhase.InsideRadius:
                if (previous == StationKeepPhase.Disengaged)
                {
                    RaiseStationKeepEngaged(in outcome);
                    return;
                }

                Raise(
                    StationKeeping.RestoredCode,
                    AssetEventSeverity.Info,
                    "Advisory: the hold is nominal again, "
                    + $"{outcome.PositionErrorM:0.0} m from station.");
                return;

            case StationKeepPhase.Correcting:
                if (previous == StationKeepPhase.Disengaged)
                {
                    RaiseStationKeepEngaged(in outcome);
                    return;
                }

                Raise(
                    StationKeeping.CorrectingCode,
                    AssetEventSeverity.Info,
                    $"Advisory: {outcome.PositionErrorM:0.0} m from station, outside the "
                    + $"{ToleranceRadiusM:0.0} m tolerance radius, and closing on it under "
                    + "control.");
                return;

            default:
                return;
        }
    }

    /// <summary>Raises the engagement event, which either working phase can be entered through.</summary>
    /// <remarks>
    /// A hold commanded from outside its tolerance radius engages into
    /// <see cref="StationKeepPhase.Correcting"/>; one commanded on top of its station engages into
    /// <see cref="StationKeepPhase.InsideRadius"/>. Both are the same transition — nothing
    /// holding, then something holding — so both carry the same code, and neither is reported as a
    /// return to a hold that had never begun.
    /// </remarks>
    /// <param name="outcome">Outcome the law produced on the step the hold engaged.</param>
    private void RaiseStationKeepEngaged(in StationKeepOutcome outcome) => Raise(
        StationKeeping.EngagedCode,
        AssetEventSeverity.Info,
        $"Station keeping engaged with {outcome.RemainingAuthorityFraction:0.00} of its effort "
        + "in hand.");

    /// <summary>Tolerance radius the engaged hold is being judged against, in metres.</summary>
    /// <remarks>
    /// Read from the goal the navigator is actually holding rather than from the profile, so the
    /// number in the message is the one the phase was decided by. Zero when no hold is engaged,
    /// which no message that reads it is reachable in.
    /// </remarks>
    private double ToleranceRadiusM => _navigator.StationKeep?.ToleranceRadiusM ?? 0.0;

    /// <summary>Raises the three berthing outcomes that need an operator.</summary>
    /// <remarks>
    /// An abort names its reason in <see cref="Docking.ReasonCode"/>'s vocabulary and says
    /// plainly that the vessel is still commandable, because an operator reading "docking
    /// aborted" beside a hull a few metres off a pontoon needs to know at once whether they still
    /// have the controls. They always do.
    /// </remarks>
    private void RaiseDockingEvents()
    {
        var phase = _navigator.DockingProgress.Phase;

        if (phase == _wasDockingPhase)
        {
            return;
        }

        var previous = _wasDockingPhase;
        _wasDockingPhase = phase;

        switch (phase)
        {
            case DockingPhase.Approach when previous is DockingPhase.Inactive or DockingPhase.Moored
                or DockingPhase.Aborted:
                Raise(
                    Docking.StartedCode,
                    AssetEventSeverity.Info,
                    "Berthing approach begun; staged speed limits and the approach corridor are "
                    + "in force.");
                return;

            case DockingPhase.Moored:
                Raise(
                    Docking.MooredCode,
                    AssetEventSeverity.Info,
                    "Secured at the berth: on the terminal pose, inside tolerance and with no way "
                    + "on.");
                return;

            case DockingPhase.Aborted:
                Raise(
                    Docking.AbortedCode,
                    AssetEventSeverity.Warning,
                    "Berthing approach abandoned "
                    + $"({Docking.ReasonCode(_navigator.DockingProgress.AbortReason)}). The "
                    + "propeller is stopped and the vessel accepts every command.");
                return;

            default:
                // Approach to corridor to final: real transitions, deliberately not raised. See
                // the remarks on RaiseStepEvents.
                return;
        }
    }

    /// <summary>Raises the drifting advisory, latched with hysteresis.</summary>
    /// <remarks>
    /// <b>The event this domain exists to raise.</b> An operator who reads "stopped" and then
    /// watches a vessel move two hundred metres downstream has been lied to, and no amount of
    /// correct speed telemetry fixes that on its own — the number is right there and still gets
    /// read as noise. This says it in words, once, on the transition: the propeller is stopped
    /// and the vessel is going somewhere anyway.
    /// <para>
    /// Latched between two thresholds rather than level-triggered on one, so a hull hovering on
    /// the boundary in slack water does not raise and clear the same advisory every second.
    /// </para>
    /// </remarks>
    private void RaiseDriftEvents()
    {
        if (_navigator.IsUnderPower)
        {
            // Under way under command. Any ground speed is the passage, not a drift, so the
            // advisory is cleared silently rather than announced as an improvement.
            _driftLatched = false;
            return;
        }

        double speed = CoordinateFrames.SpeedOverGround(_groundVelocityEus);

        if (!_driftLatched && speed >= DriftAlertSpeedMps)
        {
            _driftLatched = true;
            Raise(
                DriftingCode,
                AssetEventSeverity.Warning,
                $"Advisory: making {speed:0.00} m/s over the ground with the propeller stopped, "
                + $"towards {CoordinateFrames.BearingFromEusVector(_groundVelocityEus, _motion.HeadingRad) * 180.0 / Math.PI:000} degrees. "
                + "The vessel is not holding position and cannot without being commanded to.");
            return;
        }

        if (_driftLatched && speed <= DriftClearSpeedMps)
        {
            _driftLatched = false;
            Raise(
                DriftingClearedCode,
                AssetEventSeverity.Info,
                "Advisory: no longer making way over the ground.");
        }
    }

    /// <summary>Raises the low-energy warning, latched with hysteresis.</summary>
    private void RaiseEnergyEvents()
    {
        double percent = EnergyPercent;

        if (percent < LowEnergyPercent && !_lowEnergyLatched)
        {
            _lowEnergyLatched = true;
            Raise(
                EnergyLowCode,
                AssetEventSeverity.Warning,
                "Battery below the return-to-base reserve.");
        }
        else if (percent >= LowEnergyPercent)
        {
            _lowEnergyLatched = false;
        }
    }

    /// <summary>Raises the passage-refused event, naming the reason in the water mask's vocabulary.</summary>
    /// <param name="reason">Why the water was refused.</param>
    private void RaiseBlocked(WaterBlockReason reason) => Raise(
        BlockedCode,
        AssetEventSeverity.Warning,
        $"Advisory: passage refused ({WaterConstraints.ReasonCode(reason)}). The propeller is "
        + "stopped, so the vessel will now drift until it is retasked.");

    /// <summary>Queues one event stamped with the most recent step's clock.</summary>
    /// <remarks>
    /// Stamped from the last step rather than from a clock of its own, so an event raised by a
    /// command arriving between steps is attributed to the last instant that was actually
    /// simulated. Nothing has been integrated since, so no later instant would be truthful — and
    /// an asset has no wall clock to reach for in any case.
    /// <para>
    /// Beyond <see cref="MaxQueuedEvents"/> the event is counted and dropped rather than queued.
    /// The oldest are kept because they are the transitions that explain how the vessel reached
    /// the state it is in, and <see cref="DrainEvents"/> reports the count so the loss is never
    /// silent.
    /// </para>
    /// </remarks>
    /// <param name="code">Stable machine-readable code; the contract alerting and tests key on.</param>
    /// <param name="severity">How much operator attention the occurrence deserves.</param>
    /// <param name="message">Operator-facing description. Free to be rewritten at any time.</param>
    private void Raise(string code, AssetEventSeverity severity, string message)
    {
        if (_events.Count >= MaxQueuedEvents)
        {
            _droppedEvents++;
            return;
        }

        _events.Add(new AssetEvent(
            AssetId, code, severity, message, _simulationTimeSeconds, _tick));
    }
}
