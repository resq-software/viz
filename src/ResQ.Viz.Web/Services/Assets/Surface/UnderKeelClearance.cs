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

/// <summary>The water-relevant envelope of one hull, and nothing else about it.</summary>
/// <remarks>
/// Deliberately narrow. Under-keel clearance needs a draft, a length to space route samples by
/// and a beam to reason about how much of the bed the hull actually covers; it needs nothing
/// about thrust, actuator time constants or turning circle. Keeping the water functions on this
/// projection is what lets them be tested with four literals and no world at all.
/// <para>
/// This is a <em>projection</em> of <see cref="SurfaceProfile"/>, not a second copy of it.
/// <see cref="SurfaceProfile"/> owns length, beam and draft; <see cref="From"/> is the one place
/// they are read across, and the safe margin is the only figure this type adds. Constructing one
/// from loose numbers rather than from a profile is inventing a draft, which is exactly the
/// drift <see cref="UnderKeelClearance"/> exists to make impossible.
/// </para>
/// </remarks>
/// <param name="DraftM">Static draft — how far the hull sits below the water surface, in metres.</param>
/// <param name="LengthOverallM">Overall hull length in metres. Route sampling spacing is derived from it.</param>
/// <param name="BeamM">Overall hull width in metres.</param>
/// <param name="SafeUnderKeelClearanceM">Clearance below which the vessel is advised off, in metres. See <see cref="UnderKeelClearance.SafeMarginForDraft"/>.</param>
public sealed record VesselWaterProfile(
    double DraftM,
    double LengthOverallM,
    double BeamM,
    double SafeUnderKeelClearanceM)
{
    /// <summary>Builds an envelope whose safety margin comes from the documented basis.</summary>
    /// <remarks>
    /// The preferred constructor. Writing the margin out by hand is permitted — a scenario may
    /// want a deliberately tighter or looser one — but doing so silently is how the enforced
    /// margin and the documented one part company.
    /// </remarks>
    /// <param name="draftM">Static draft in metres.</param>
    /// <param name="lengthOverallM">Overall hull length in metres.</param>
    /// <param name="beamM">Overall hull width in metres.</param>
    /// <returns>An envelope carrying <see cref="UnderKeelClearance.SafeMarginForDraft"/>.</returns>
    public static VesselWaterProfile ForHull(double draftM, double lengthOverallM, double beamM) =>
        new(draftM, lengthOverallM, beamM, UnderKeelClearance.SafeMarginForDraft(draftM));

    /// <summary>Projects a vessel profile onto the figures the water functions need.</summary>
    /// <remarks>
    /// The only crossing between the two types. Everything downstream of here takes the
    /// projection, so there is exactly one line in the codebase that decides which hull
    /// dimensions under-keel clearance is measured against.
    /// </remarks>
    /// <param name="profile">Authoritative vessel profile.</param>
    /// <returns>Its water-relevant envelope, carrying the documented safe margin.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static VesselWaterProfile From(SurfaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ForHull(profile.DraftM, profile.LengthM, profile.BeamM);
    }
}

/// <summary>How much water a vessel has under it, as a band rather than a bit.</summary>
/// <remarks>
/// Five values because a single unsafe/safe flag cannot tell a vessel that should slow down
/// from one that is already sitting on the bed, and cannot tell either from a point where the
/// bathymetry simply is not known. Those three deserve different words in front of an operator
/// and different behaviour from the integrator.
/// </remarks>
public enum UnderKeelClearanceClass
{
    /// <summary>No water data for this point, so no clearance can be stated. Neither a refusal nor an invitation.</summary>
    Unknown,

    /// <summary>Clearance is at or below zero: the hull is on the bed.</summary>
    /// <remarks>
    /// A <b>recoverable</b> state. It derates the speed ceiling hard but never to zero — see
    /// <see cref="UnderKeelClearance.AgroundSpeedFactor"/> — because a vessel that could not
    /// move once aground could never work itself off.
    /// </remarks>
    Aground,

    /// <summary>Afloat, but with less clearance than the profile's safe margin. Unsafe.</summary>
    Critical,

    /// <summary>Clear of the safe margin, but within the band worth warning an operator about.</summary>
    /// <remarks>
    /// Cautionary and nothing more: it carries no derate and blocks nothing. An advisory that
    /// quietly became a refusal is the defect this wording is guarding against.
    /// </remarks>
    Marginal,

    /// <summary>Comfortably clear of the bed.</summary>
    Safe,
}

/// <summary>Whether a hull is floating, floating tight, or sitting on the bed.</summary>
/// <remarks>
/// Three operator-facing situations, and the reason this exists as its own vocabulary is that
/// the two <em>bad</em> ones are routinely collapsed into one. Being on the bed is a casualty:
/// the vessel is held by the ground, and the response is to work it off. Being afloat inside the
/// advisory margin is a warning: the vessel is under way, answering, and merely closer to the bed
/// than its own margin wants — the response is to slow down and stand off. Reporting the second
/// as the first tells an operator a grounding has happened when nothing has touched anything,
/// and the two get acted on differently.
/// <para>
/// Deliberately <b>not</b> derived from the navigable-water mask. That mask refuses points which
/// are merely inside the margin, and points under a prohibited zone, at neither of which is the
/// hull touching anything. It answers "may a hull plan to be here"; this answers "what is this
/// hull doing about the bed". Reading one off the other is the confusion this type ends.
/// </para>
/// </remarks>
public enum HullContactState
{
    /// <summary>No depth could be established, so nothing may be claimed either way.</summary>
    Unknown,

    /// <summary>Afloat with the advisory margin intact.</summary>
    /// <remarks>
    /// Covers <see cref="UnderKeelClearanceClass.Marginal"/> as well as
    /// <see cref="UnderKeelClearanceClass.Safe"/>: the cautionary band is advice about water that
    /// is getting tight, not a statement that the margin has been given up.
    /// </remarks>
    Afloat,

    /// <summary>Afloat, but with less than the advisory margin under the keel.</summary>
    /// <remarks>
    /// A warning that derates the speed ceiling — see <see cref="UnderKeelClearance.SpeedFactorFor"/>
    /// — and nothing more. The hull is still floating and still steering.
    /// </remarks>
    InsideSafetyMargin,

    /// <summary>The hull is resting on the bed.</summary>
    /// <remarks>
    /// Recoverable, and never a fault: see <see cref="UnderKeelClearance.AgroundSpeedFactor"/> for
    /// why the speed ceiling keeps a floor here rather than falling to zero.
    /// </remarks>
    OnTheBed,
}

/// <summary>Water depth, vessel draft and the clearance between them, kept apart.</summary>
/// <remarks>
/// All three are carried explicitly. They are routinely confused — a depth sounder reads depth,
/// a chart states draft, and only their difference tells a vessel whether it may proceed — and
/// a consumer handed one of them cannot recover the others. Publishing the subtraction as well
/// as its operands means no client has to redo it and get the sign wrong.
/// <para>
/// Advisory decision support throughout. The bed comes from a procedural height field, not a
/// survey, and none of these figures asserts that any particular passage is safe.
/// </para>
/// </remarks>
/// <param name="HasWaterData">False only when no depth could be established. Dry land reports a known depth of zero.</param>
/// <param name="WaterDepthM">Water column from surface to bed, in metres. Zero when there is no water data.</param>
/// <param name="DraftM">Static draft of the hull, in metres.</param>
/// <param name="ClearanceM">Depth less draft, in metres. Negative when the hull is into the bed.</param>
/// <param name="SafeMarginM">Clearance the profile wants kept, in metres.</param>
/// <param name="Class">Which band the clearance falls in.</param>
/// <param name="SpeedFactor">Multiplier the speed ceiling is derated by. See <see cref="UnderKeelClearance.SpeedFactorFor"/>.</param>
public sealed record UnderKeelClearanceState(
    bool HasWaterData,
    double WaterDepthM,
    double DraftM,
    double ClearanceM,
    double SafeMarginM,
    UnderKeelClearanceClass Class,
    double SpeedFactor)
{
    /// <summary>True when clearance has fallen below the profile's safe margin.</summary>
    /// <remarks>
    /// The distinct flag the wire model's <c>HasUnsafeUnderKeelClearance</c> is filled from.
    /// Derived from <see cref="Class"/> rather than stored, so the flag and the band cannot
    /// disagree. <see cref="UnderKeelClearanceClass.Marginal"/> is deliberately <em>not</em>
    /// unsafe: it is the band that says "watch this", and treating it as unsafe would derate a
    /// vessel that has margin in hand.
    /// <para>
    /// It spans two <em>different</em> situations — afloat inside the margin, and on the bed —
    /// and is therefore the wrong thing to word a report from. <see cref="Contact"/> is the one
    /// that separates them; this flag only says the margin has been given up.
    /// </para>
    /// </remarks>
    public bool IsUnsafe => Class is UnderKeelClearanceClass.Critical or UnderKeelClearanceClass.Aground;

    /// <summary>True when the hull is on the bed.</summary>
    public bool IsAground => Class == UnderKeelClearanceClass.Aground;

    /// <summary>What the hull is doing about the bed: floating, floating tight, or aground.</summary>
    /// <remarks>
    /// The clearance band restated in the terms a report is written in, so a summary, an event
    /// and a health entry describing the same hull cannot each invent their own threshold for
    /// "aground". Derived rather than stored, so it can never drift from <see cref="Class"/>.
    /// </remarks>
    public HullContactState Contact => UnderKeelClearance.ContactFor(Class);

    /// <summary>Shallowest water this hull may float in with its margin intact, in metres.</summary>
    /// <remarks>
    /// Draft plus margin, and the single threshold the navigable-water mask is cut at. Exposed
    /// so a route preview and the integrator ask the same question of the same number.
    /// </remarks>
    public double MinimumNavigableDepthM => DraftM + SafeMarginM;

    /// <summary>Stable machine-readable token for <see cref="Class"/>, or null when clearance is ample.</summary>
    public string? ReasonCode => UnderKeelClearance.ReasonCode(Class);
}

/// <summary>Turns a water column and a hull into a clearance, a band and a speed derate.</summary>
/// <remarks>
/// Pure arithmetic over three numbers, with no notion of a vessel's position, heading or
/// history, so every band and every point on the derating curve can be driven from literals.
/// <para>
/// <b>This is the one place the derate is defined.</b>
/// <see cref="SpeedFactorFor"/> is the curve, <see cref="DerateSpeedMps"/> applies it, and no
/// caller should re-derive either: a second copy of a derating rule is how a documented curve
/// and an enforced curve come to disagree.
/// </para>
/// <para>
/// Advisory decision support. Nothing here claims conformance with any navigation regulation
/// or certifies autonomous operation; it is a simulated hull over a procedural bed.
/// </para>
/// </remarks>
public static class UnderKeelClearance
{
    /// <summary>Speed multiplier retained once the hull is on the bed.</summary>
    /// <remarks>
    /// Non-zero on purpose. A zero ceiling would make grounding permanent — the vessel could no
    /// longer execute the very commands that back it into deeper water — and grounding here is
    /// a recoverable state, not a terminal one. A crawl is enough to work off a bank and slow
    /// enough that nothing about it looks like normal transit.
    /// </remarks>
    public const double AgroundSpeedFactor = 0.15;

    /// <summary>Multiple of the safe margin below which clearance is worth mentioning.</summary>
    /// <remarks>
    /// Twice the margin. The margin itself is the floor an operator is asked to keep, so the
    /// point at which they should be told is necessarily above it — telling them on arrival at
    /// the floor gives them nothing to act with. This band carries no derate.
    /// </remarks>
    public const double CautionaryMarginMultiple = 2.0;

    /// <summary>Event code raised on the transition into <see cref="HullContactState.OnTheBed"/>.</summary>
    /// <remarks>
    /// The codes here exist so the asset that owns the event queue raises one vocabulary rather
    /// than inventing its own. They are for <b>transitions</b>: raising any of them on every
    /// step, level-triggered, floods the queue at the world's tick rate.
    /// <para>
    /// <b>The transition is the one <see cref="ContactFor"/> reports, and never a refusal by the
    /// navigable-water mask.</b> The mask is cut at draft plus the advisory margin and refuses a
    /// prohibited zone outright, so raising this off it announces a grounding for a hull afloat
    /// inside its own margin, and for one merely turned back at the edge of a no-go area in any
    /// depth of water at all. Losing the margin has its own transition —
    /// <see cref="UnsafeClearanceCode"/> — at its own severity, because it calls for a different
    /// response.
    /// </para>
    /// <para>
    /// The constant does double duty as the token <see cref="ReasonCode"/> returns for
    /// <see cref="UnderKeelClearanceClass.Aground"/>, so an event and the band a published state
    /// carries spell the same situation the same way.
    /// </para>
    /// </remarks>
    public const string AgroundCode = "surface.aground";

    /// <summary>Event code raised on the transition into <see cref="HullContactState.InsideSafetyMargin"/>.</summary>
    /// <remarks>
    /// Afloat, under way and answering, with less than the advisory margin under the keel: a
    /// warning that derates the speed ceiling, not a casualty. It is a genuinely separate
    /// transition from <see cref="AgroundCode"/>, and it is only <em>reachable</em> because both
    /// are read off <see cref="ContactFor"/>. A raiser that derived grounding from the
    /// navigable-water mask instead leaves this arm dead code — the mask refuses every clearance
    /// this band covers, so "aground" is already true wherever it would have fired — and the one
    /// level an operator still has time to act on is never announced at all.
    /// </remarks>
    public const string UnsafeClearanceCode = "surface.ukc.unsafe";

    /// <summary>Event code raised on the transition back out of an unsafe or aground state.</summary>
    /// <remarks>
    /// One code for both, because what it reports is that the hull is neither on the bed nor
    /// inside its margin any more. Moving between those two states is not a restoration and does
    /// not raise this: it raises whichever of the other two codes describes the state now in
    /// force.
    /// </remarks>
    public const string ClearanceRestoredCode = "surface.ukc.restored";

    /// <summary>Fraction of static draft allowed for dynamic squat and wave-driven heave.</summary>
    /// <remarks>
    /// Scales with draft because both effects do: a deeper hull squats further at a given speed
    /// and heaves further in a given sea. A tenth is the order this simulation's speeds and its
    /// modest wave field produce; it is a modelling figure, not a measured one.
    /// </remarks>
    private const double SquatAndHeaveFractionOfDraft = 0.10;

    /// <summary>Allowance for bathymetry sampling error, in metres.</summary>
    /// <remarks>
    /// The bed is a procedural height field read at a single point, while a real hull spans its
    /// own beam and the true minimum under it sits below that point sample. This covers the
    /// field's short-wavelength content across the beams modelled here. It does not scale with
    /// draft, because it is a property of the terrain rather than of the vessel.
    /// </remarks>
    private const double BathymetrySamplingAllowanceM = 0.25;

    /// <summary>Floor on the safe margin, in metres.</summary>
    /// <remarks>
    /// Without it a shallow-draft craft would compute an almost-zero margin and be declared
    /// safe skimming the bed, which is the opposite of what a margin is for.
    /// </remarks>
    private const double MinimumSafeMarginM = 0.30;

    /// <summary>The safe under-keel margin a hull of a given draft should keep, in metres.</summary>
    /// <remarks>
    /// The sum of a squat-and-heave allowance proportional to draft and a fixed bathymetry
    /// sampling allowance, floored so a shallow hull still keeps something. Each term is named
    /// and documented above so the number can be argued with rather than merely obeyed — an
    /// unexplained constant is one nobody can safely change.
    /// </remarks>
    /// <param name="draftM">Static draft in metres. Non-finite or negative values are treated as zero.</param>
    /// <returns>The advisory safe clearance in metres.</returns>
    public static double SafeMarginForDraft(double draftM)
    {
        double draft = double.IsFinite(draftM) ? Math.Max(0.0, draftM) : 0.0;
        return Math.Max(
            MinimumSafeMarginM, (SquatAndHeaveFractionOfDraft * draft) + BathymetrySamplingAllowanceM);
    }

    /// <summary>Shallowest water this profile may float in with its margin intact, in metres.</summary>
    /// <param name="profile">Hull envelope to derive for.</param>
    /// <returns>Draft plus safe margin, in metres.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static double MinimumNavigableDepthM(VesselWaterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return SanitisedDraft(profile) + SanitisedMargin(profile);
    }

    /// <summary>Stable machine-readable token for a clearance band, or null when it is ample.</summary>
    /// <param name="value">Band to encode.</param>
    /// <returns>A dotted lower-case token, e.g. <c>surface.ukc.critical</c>.</returns>
    public static string? ReasonCode(UnderKeelClearanceClass value) => value switch
    {
        UnderKeelClearanceClass.Aground => AgroundCode,
        UnderKeelClearanceClass.Critical => "surface.ukc.critical",
        UnderKeelClearanceClass.Marginal => "surface.ukc.marginal",
        UnderKeelClearanceClass.Unknown => "surface.ukc.unknown",
        _ => null,
    };

    /// <summary>How much operator attention a clearance band deserves.</summary>
    /// <remarks>
    /// Unsafe clearance is a warning and grounding is an alert; the cautionary band is
    /// informational, because a band that exists to give early notice must not shout.
    /// </remarks>
    /// <param name="value">Band to rank.</param>
    /// <returns>The severity an event carrying this band should be raised at.</returns>
    public static AssetEventSeverity SeverityOf(UnderKeelClearanceClass value) => value switch
    {
        UnderKeelClearanceClass.Aground => AssetEventSeverity.Alert,
        UnderKeelClearanceClass.Critical => AssetEventSeverity.Warning,
        _ => AssetEventSeverity.Info,
    };

    /// <summary>Reduces a clearance band to what the hull is doing about the bed.</summary>
    /// <remarks>
    /// The single mapping from bands to the three situations a report distinguishes, and the only
    /// place the sentence "the vessel is aground" is allowed to originate. Anything that words a
    /// summary, an event or a health entry asks here rather than testing bands itself, because
    /// two spellings of that test are how a hull afloat inside its margin comes to be announced
    /// as having run aground.
    /// <para>
    /// <see cref="UnderKeelClearanceClass.Marginal"/> maps to <see cref="HullContactState.Afloat"/>
    /// on purpose: the cautionary band exists to give early notice, and an advisory that reported
    /// itself as a loss of margin would be a limit wearing an advisory's name.
    /// </para>
    /// </remarks>
    /// <param name="value">Band to reduce.</param>
    /// <returns>The situation the band describes.</returns>
    public static HullContactState ContactFor(UnderKeelClearanceClass value) => value switch
    {
        UnderKeelClearanceClass.Aground => HullContactState.OnTheBed,
        UnderKeelClearanceClass.Critical => HullContactState.InsideSafetyMargin,
        UnderKeelClearanceClass.Unknown => HullContactState.Unknown,
        _ => HullContactState.Afloat,
    };

    /// <summary>Places a clearance in its band.</summary>
    /// <param name="clearanceM">Depth less draft, in metres. May be negative.</param>
    /// <param name="safeMarginM">Margin the profile wants kept, in metres.</param>
    /// <returns>The band, or <see cref="UnderKeelClearanceClass.Unknown"/> for a non-finite input.</returns>
    public static UnderKeelClearanceClass Classify(double clearanceM, double safeMarginM)
    {
        if (!double.IsFinite(clearanceM) || !double.IsFinite(safeMarginM))
        {
            return UnderKeelClearanceClass.Unknown;
        }

        double margin = Math.Max(0.0, safeMarginM);

        return clearanceM switch
        {
            <= 0.0 => UnderKeelClearanceClass.Aground,
            _ when clearanceM < margin => UnderKeelClearanceClass.Critical,
            _ when clearanceM < margin * CautionaryMarginMultiple => UnderKeelClearanceClass.Marginal,
            _ => UnderKeelClearanceClass.Safe,
        };
    }

    /// <summary>The speed derating curve, and the only definition of it.</summary>
    /// <remarks>
    /// Full speed at or above the safe margin; a straight ramp from
    /// <see cref="AgroundSpeedFactor"/> at zero clearance up to one at the margin; and
    /// <see cref="AgroundSpeedFactor"/> at or below zero. Continuous and monotonic, so a vessel
    /// creeping into a shoal slows smoothly rather than stepping, and never reaches a standstill
    /// it cannot drive out of.
    /// <para>
    /// The cautionary band above the margin is not derated. It advises; advice that silently
    /// halved a speed ceiling would be a limit wearing an advisory's name.
    /// </para>
    /// </remarks>
    /// <param name="clearanceM">Depth less draft, in metres. May be negative.</param>
    /// <param name="safeMarginM">Margin the profile wants kept, in metres.</param>
    /// <returns>A multiplier in <c>[<see cref="AgroundSpeedFactor"/>, 1]</c>.</returns>
    public static double SpeedFactorFor(double clearanceM, double safeMarginM)
    {
        if (!double.IsFinite(clearanceM) || !double.IsFinite(safeMarginM))
        {
            return AgroundSpeedFactor;
        }

        if (clearanceM <= 0.0)
        {
            return AgroundSpeedFactor;
        }

        double margin = Math.Max(0.0, safeMarginM);

        if (margin <= 0.0 || clearanceM >= margin)
        {
            return 1.0;
        }

        return AgroundSpeedFactor + ((1.0 - AgroundSpeedFactor) * (clearanceM / margin));
    }

    /// <summary>Applies the clearance derate to a requested speed.</summary>
    /// <remarks>
    /// The single application site. Callers ask for a ceiling here rather than multiplying by
    /// <see cref="UnderKeelClearanceState.SpeedFactor"/> themselves, so the curve documented
    /// above is demonstrably the curve the integrator obeys.
    /// </remarks>
    /// <param name="state">Clearance evaluated at the vessel's position.</param>
    /// <param name="requestedSpeedMps">Speed the vessel would otherwise be allowed, in metres per second.</param>
    /// <returns>The derated speed, preserving the sign of <paramref name="requestedSpeedMps"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    public static double DerateSpeedMps(UnderKeelClearanceState state, double requestedSpeedMps)
    {
        ArgumentNullException.ThrowIfNull(state);
        return double.IsFinite(requestedSpeedMps) ? requestedSpeedMps * state.SpeedFactor : 0.0;
    }

    /// <summary>Evaluates clearance from an environment sample taken at the hull.</summary>
    /// <remarks>
    /// Reads <see cref="EnvironmentSample.WaterDepthM"/>, which is itself the water surface less
    /// the bathymetric bed. Going through it rather than differencing the two here keeps one
    /// definition of the water column in the codebase.
    /// <para>
    /// Dry land is <b>not</b> unknown water. The environment reports no column there because
    /// there is none, and a hull standing on the ground carries its whole draft on the bed —
    /// that is <see cref="UnderKeelClearanceClass.Aground"/>, with the unsafe flag raised, not
    /// <see cref="UnderKeelClearanceClass.Unknown"/> with it quietly clear. Only water whose bed
    /// the environment could not answer for is unknown.
    /// </para>
    /// </remarks>
    /// <param name="profile">Hull envelope.</param>
    /// <param name="sample">Environment sampled at the vessel's position.</param>
    /// <returns>Depth, draft, clearance, band and derate.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static UnderKeelClearanceState Evaluate(VesselWaterProfile profile, EnvironmentSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample.IsWater ? Evaluate(profile, sample.WaterDepthM) : Evaluate(profile, 0.0);
    }

    /// <summary>Evaluates clearance from a water column measured elsewhere.</summary>
    /// <param name="profile">Hull envelope.</param>
    /// <param name="waterDepthM">Water column in metres, or null when the point is dry land or unsurveyed.</param>
    /// <returns>Depth, draft, clearance, band and derate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static UnderKeelClearanceState Evaluate(VesselWaterProfile profile, double? waterDepthM)
    {
        ArgumentNullException.ThrowIfNull(profile);

        double draft = SanitisedDraft(profile);
        double margin = SanitisedMargin(profile);

        // An unanswerable depth is not a shallow one. A point the environment could not survey
        // is reported as unknown rather than as zero depth, so a consumer can tell "no reading"
        // from "a reading of nothing to spare" — and so a preset switch that moves the water
        // level is never mistaken for the hull having sunk.
        if (waterDepthM is not { } depth || !double.IsFinite(depth))
        {
            return new UnderKeelClearanceState(
                HasWaterData: false,
                WaterDepthM: 0.0,
                DraftM: draft,
                ClearanceM: 0.0,
                SafeMarginM: margin,
                Class: UnderKeelClearanceClass.Unknown,
                SpeedFactor: AgroundSpeedFactor);
        }

        double column = Math.Max(0.0, depth);
        double clearance = column - draft;

        return new UnderKeelClearanceState(
            HasWaterData: true,
            WaterDepthM: column,
            DraftM: draft,
            ClearanceM: clearance,
            SafeMarginM: margin,
            Class: Classify(clearance, margin),
            SpeedFactor: SpeedFactorFor(clearance, margin));
    }

    /// <summary>Draft as a usable non-negative number.</summary>
    private static double SanitisedDraft(VesselWaterProfile profile) =>
        double.IsFinite(profile.DraftM) ? Math.Max(0.0, profile.DraftM) : 0.0;

    /// <summary>Margin as a usable non-negative number, falling back to the documented basis.</summary>
    /// <remarks>
    /// A profile carrying a non-finite margin gets the margin its draft implies rather than an
    /// exception. Malformed configuration is skipped, not thrown: a bad row must not abandon a
    /// half-built world.
    /// </remarks>
    private static double SanitisedMargin(VesselWaterProfile profile) =>
        double.IsFinite(profile.SafeUnderKeelClearanceM) && profile.SafeUnderKeelClearanceM >= 0.0
            ? profile.SafeUnderKeelClearanceM
            : SafeMarginForDraft(profile.DraftM);
}
