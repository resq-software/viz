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

using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Tracks;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Contacts the session observes but does not control, and the advisory geometry read off them.
/// </summary>
/// <remarks>
/// Four properties, failing in four different ways.
/// <list type="number">
///   <item><description>
///     <b>A track is not an asset.</b> It declares no <see cref="AssetCapability"/>, every
///     command gate keys on capability, and no route resolves the track identifier space — so the
///     absence of control authority is structural rather than a rule somebody has to remember.
///     That is asserted against the routes the application actually publishes, not merely against
///     the model: a model with no capability field is still commandable if a route reaches it.
///   </description></item>
///   <item><description>
///     <b>Fusion and ageing are functions of simulated time.</b> Not one assertion here reads a
///     wall clock, sleeps, or lets a value depend on how fast the machine ran. Ages are supplied
///     as literals, so the same reports produce the same picture on every run and in every replay
///     — which is the whole reason ageing is measured in simulated seconds.
///   </description></item>
///   <item><description>
///     <b>The store is bounded.</b> A source spraying identifiers cannot grow it, a source minting
///     a new sensor id per plot cannot grow one track's source list, and every drop is counted
///     where an operator can see it. Freshness notices fire on a band change and not per sweep, so
///     a contact that sits stale for a minute produces one notice rather than sixty a second.
///   </description></item>
///   <item><description>
///     <b>The geometry is closed-form and advisory.</b> Every case has an answer that exists
///     independently of the code — a time and a separation obtained by hand from
///     <c>t* = -(r.v)/(v.v)</c> — so a regression that changes the geometry consistently still
///     fails. The wording case is the other half: an advisory that stops describing itself as one
///     is a safety defect even when every number in it is right.
///   </description></item>
/// </list>
/// <para>
/// Fixtures live in <c>ExternalTrackTests.Fixtures.cs</c> so this file reads as a list of
/// contracts, following the split the ground suites use.
/// </para>
/// </remarks>
public sealed partial class ExternalTrackTests
{
    // ─── A contact is observed, never commanded ──────────────────────────────

    /// <summary>Nothing on the track surface carries, or could carry, control authority.</summary>
    /// <remarks>
    /// The model half of the property. A track has a pose and a classification and that is where
    /// the resemblance to an asset stops: no capability mask for a gate to test, and no member
    /// whose name even suggests one — on the published state, on the ingest request, on the store
    /// that holds them, or on the room that owns the store.
    /// </remarks>
    [Fact]
    public void No_Track_Type_Exposes_A_Capability_Command_Or_Control_Surface()
    {
        Type[] trackTypes =
        [
            typeof(ExternalTrackState), typeof(AgedExternalTrack), typeof(TrackReportRequest),
            typeof(TrackReportResponse), typeof(TrackInventoryResponse), typeof(TrackSource),
            typeof(TrackQuality), typeof(TransponderIdentity), typeof(TrackReport),
            typeof(ExternalTrackStore), typeof(RoomTrackFrame),
        ];

        foreach (var type in trackTypes)
        {
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(AssetCapability))
                .Should().BeEmpty(
                    "{0} must declare no capability: capability is what every command gate keys "
                    + "on, so a type carrying none can never pass validation", type.Name);

            type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.Name)
                .Where(SuggestsControl)
                .Should().BeEmpty("nothing on {0} may read as a way to drive a contact", type.Name);
        }

        // The room is the other place the two identifier spaces could quietly merge. It ingests,
        // captures and looks up contacts; there is deliberately no member that sends one anything.
        typeof(SimulationRoom)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(name => name.Contains("Track", StringComparison.Ordinal) && SuggestsControl(name))
            .Should().BeEmpty("a contact is an observation, so the room offers no way to drive one");
    }

    /// <summary>The wire surface offers three verbs on a contact, and none of them is a command.</summary>
    /// <remarks>
    /// Asserted against the routes the application publishes rather than against the file that
    /// happens to hold them, because the hazard is a route added anywhere: a
    /// <c>tracks/{id}/commands</c> beside the asset one would be invisible to any test that only
    /// read the track model. Pinned by equality, so a fourth track route has to be justified here.
    /// </remarks>
    [Fact]
    public void The_Only_Routes_That_Name_A_Track_Are_List_Fetch_And_Report()
    {
        var trackRoutes = Routes()
            .Where(route => route.Contains("track", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string[] published =
        [
            "GET api/v2/sim/tracks",
            "GET api/v2/sim/tracks/{trackId}",
            "POST api/v2/sim/tracks",
        ];

        trackRoutes.Should().BeEquivalentTo(
            published,
            "a contact may be listed, fetched and reported; there is no fourth verb, and the "
            + "absence of one is the safety property rather than an omission");

        trackRoutes
            .Where(route => route.Contains("command", StringComparison.OrdinalIgnoreCase)
                || route.Contains("cmd", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("no route may join the track identifier space to the command one");
    }

    /// <summary>Every registered command kind, addressed to a held contact, resolves no asset.</summary>
    /// <remarks>
    /// The behavioural half, and deliberately the whole catalog rather than a sample: a gate that
    /// happens to refuse <c>stop</c> says nothing about <c>dock</c>. Each probe carries the most
    /// permissive well-formed payload its definition allows — a target in whichever shape it
    /// accepts, every required parameter present and parseable — so the refusal is about the
    /// identifier space and not about a payload that would have been refused for an asset too.
    /// <para>
    /// The refusal must also leave nothing behind: no command result is tracked afterwards, and
    /// the contact is byte-for-byte the one that was reported.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_Command_Kind_Addressed_To_A_Track_Resolves_No_Asset_And_Changes_Nothing()
    {
        var (controller, room) = Api();
        var reported = ReportContact(controller, ContactId);

        int ordinal = 0;
        foreach (var definition in CommandCatalog.All)
        {
            var commandId = CommandId(++ordinal);
            var result = controller.SendCommand(ContactId, ProbeFor(definition, commandId, ordinal));

            result.Should().NotBeOfType<AcceptedResult>(
                "'{0}' must never be accepted for a contact", definition.Kind);

            var problem = result.Should().BeOfType<ObjectResult>().Which;
            problem.StatusCode.Should().Be(
                StatusCodes.Status404NotFound,
                "a track id is simply not in the asset space, so '{0}' finds nothing to command",
                definition.Kind);
            problem.Value.Should().BeOfType<CommandProblemDetails>()
                .Which.Code.Should().Be(CommandRejectionReasons.AssetNotFound);

            room.Commands.TryGet(commandId, out _).Should().BeFalse(
                "a refused command leaves the ledger exactly as it found it");
        }

        // Capabilities are the other affordance a client would render a control from.
        controller.GetAssetCapabilities(ContactId).Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        Body<TrackInventoryResponse>(controller.GetTracks()).Tracks
            .Should().ContainSingle().Which
            .Should().BeEquivalentTo(reported, "none of that may have touched the contact");
    }

    // ─── Fusion: repeated reports of one identifier ──────────────────────────

    /// <summary>Repeated reports of one identifier fuse into one contact rather than piling up.</summary>
    /// <remarks>
    /// Last writer wins for everything the newer observation measured, and the sources it was
    /// heard from accumulate — bounded, most recent first — so a contact seen by a radar and a
    /// transponder is one contact that knows it is fused, not two contacts that disagree.
    /// </remarks>
    [Fact]
    public void Repeated_Reports_Of_One_Identifier_Fuse_Into_A_Single_Contact()
    {
        var store = Store();

        store.Ingest(Report(0.0, eastM: 100.0, sourceId: "radar-1"))
            .Outcome.Should().Be(TrackIngestOutcome.Created);

        var updated = store.Ingest(Report(
            2.0, eastM: 140.0, sourceId: "ais-1", sourceKind: TrackSourceKind.Transponder));

        updated.Outcome.Should().Be(TrackIngestOutcome.Updated);
        updated.IsAccepted.Should().BeTrue();
        store.Count.Should().Be(1, "one identifier is one contact, however many sources report it");

        var held = OnlyTrack(store, 2.0);
        held.Track.Pose.Position.X.Should().BeApproximately(140f, 1e-6f, "the newer plot wins");
        held.ObservedAtSimulationTimeSeconds.Should().Be(2.0);
        held.Track.Quality.UpdateCount.Should().Be(2);
        held.Track.Quality.IsFused.Should().BeTrue("two sources contributed");
        held.Track.Sources.Select(s => s.SourceId).Should().Equal("ais-1", "radar-1");
        held.Track.Sources[0].ObservedAt.Should().Be(
            Epoch.AddSeconds(2.0), "a source's instant is the epoch plus simulated time");
    }

    /// <summary>Observation time breaks the tie, and a late-arriving old plot is discarded.</summary>
    /// <remarks>
    /// The asymmetry is the point. A report observed <em>before</em> the one already held would
    /// drag the contact backwards, which is worse than no update at all, so it is refused and
    /// counted. A report observed at exactly the same time is accepted, because a source stamping
    /// at its own resolution must still be able to correct itself.
    /// </remarks>
    [Fact]
    public void Observation_Time_Breaks_The_Tie_Between_Two_Reports_Of_One_Contact()
    {
        var store = Store();
        store.Ingest(Report(10.0, eastM: 100.0));

        var stale = store.Ingest(Report(9.5, eastM: 999.0));

        stale.Outcome.Should().Be(TrackIngestOutcome.RejectedOutOfOrder);
        stale.Track.Should().BeNull("a caller must not be able to publish a track the store refused");
        stale.IsAccepted.Should().BeFalse();
        stale.ReasonCode.Should().Be(TrackProblems.ReportOutOfOrder);
        store.RejectedReportCount.Should().Be(1, "back-pressure has to be visible");

        OnlyTrack(store, 10.0).Track.Pose.Position.X.Should().BeApproximately(
            100f, 1e-6f, "the older plot must not move the contact");

        // Equal times accept: a source that corrects itself inside its own tick still lands.
        store.Ingest(Report(10.0, eastM: 120.0)).Outcome.Should().Be(TrackIngestOutcome.Updated);
        OnlyTrack(store, 10.0).Track.Pose.Position.X.Should().BeApproximately(120f, 1e-6f);
    }

    /// <summary>An absent claim never erases a claim an earlier source made.</summary>
    /// <remarks>
    /// The documented exception to last-writer-wins, and the reason it exists: a dense anonymous
    /// radar plot arriving after a sparse transponder report must not blank the identity the
    /// transponder gave. Accuracies are deliberately <em>not</em> an exception — they describe the
    /// plot they arrived with — so the same report leaves the contact with none rather than
    /// inheriting a precision nobody claimed for it.
    /// </remarks>
    [Fact]
    public void An_Unknown_Classification_Or_A_Missing_Label_Overwrites_Nothing()
    {
        var store = Store();
        var identity = new TransponderIdentity(TransponderKind.Ais, "232003821", CallSign: "RESQ-1");

        store.Ingest(Report(
            0.0,
            classification: TrackClassification.Vessel,
            sourceId: "ais-1",
            sourceKind: TrackSourceKind.Transponder,
            label: "Harbour tug",
            transponder: identity,
            positionAccuracyM: 12.0));

        store.Ingest(Report(1.0, classification: TrackClassification.Unknown, sourceId: "radar-1"));

        var fused = OnlyTrack(store, 1.0).Track;
        fused.Classification.Should().Be(
            TrackClassification.Vessel, "Unknown is the absence of a claim, not a claim of ignorance");
        fused.Label.Should().Be("Harbour tug");
        fused.Transponder.Should().Be(identity);
        fused.Quality.PositionAccuracyM.Should().BeNull(
            "an accuracy describes the observation it arrived with, so it is not inherited");
    }

    // ─── Ageing: driven by simulated time, visible on the wire ───────────────

    /// <summary>Confidence decays along the published curve, and the age travels with it.</summary>
    /// <remarks>
    /// Every figure here is read off the documented curve rather than off a run: full confidence
    /// to five seconds, then a straight line down to the floor at twenty, then held at the floor.
    /// The reported confidence travels beside the discounted one, because a consumer asking how
    /// good the source claims to be and one asking how much to trust this picture are asking
    /// different questions, and collapsing them either overstates a stale contact or permanently
    /// understates a source that reports well.
    /// <para>
    /// No wall clock moves during this test. The contact goes stale purely because the caller
    /// asked what it looks like at a later <em>simulated</em> second, which is exactly the
    /// property that makes a replay age identically to the run it replays.
    /// </para>
    /// </remarks>
    /// <param name="atSimulationTimeSeconds">Simulation time to read the contact at.</param>
    /// <param name="expectedConfidence">Confidence the ageing curve must publish at that age.</param>
    /// <param name="expectedFreshness">Freshness band that age falls in.</param>
    [Theory]
    [InlineData(0.0, 1.0, DataFreshness.Fresh)]
    [InlineData(5.0, 1.0, DataFreshness.Fresh)]
    [InlineData(12.5, 0.6, DataFreshness.Stale)]
    [InlineData(20.0, 0.2, DataFreshness.Stale)]
    [InlineData(45.0, 0.2, DataFreshness.Lost)]
    public void Ageing_Degrades_Confidence_Along_The_Published_Curve(
        double atSimulationTimeSeconds, double expectedConfidence, DataFreshness expectedFreshness)
    {
        var store = Store();
        store.Ingest(Report(0.0, confidence: 1.0));

        var view = OnlyTrack(store, atSimulationTimeSeconds);

        view.AgeSeconds.Should().BeApproximately(
            atSimulationTimeSeconds, Tolerance, "the age is published, not left to be derived");
        view.Track.Freshness.Should().Be(expectedFreshness);
        view.Track.Quality.Confidence.Should().BeApproximately(expectedConfidence, 1e-12);
        view.ReportedConfidence.Should().Be(
            1.0, "what the source claimed is preserved beside the discount");
        view.IsDegraded.Should().Be(expectedFreshness != DataFreshness.Fresh);
        view.Track.LastUpdateTime.Should().Be(
            Epoch, "the instant on the wire is the epoch plus simulated time, never a read clock");
    }

    /// <summary>A contact past the retention window stops being published, then is retired.</summary>
    /// <remarks>
    /// The two halves are separate on purpose. A consumer must see the same population whether or
    /// not the tick loop has swept since a contact expired, so the read omits it immediately; the
    /// sweep is then what actually frees the capacity a live contact would need.
    /// </remarks>
    [Fact]
    public void An_Expired_Contact_Is_Omitted_Before_It_Is_Swept_And_Retired_By_The_Sweep()
    {
        var store = Store();
        store.Ingest(Report(0.0));

        store.Snapshot(60.0).Should().ContainSingle("the drop window is inclusive at its edge");
        store.Snapshot(60.5).Should().BeEmpty("past the window a contact is not published at all");
        store.TryGet(ContactId, 60.5, out _).Should().BeFalse();
        store.Count.Should().Be(1, "omitting it from a read is not the same as retiring it");

        var sweep = store.Advance(60.5);

        sweep.DroppedTrackIds.Should().Equal(ContactId);
        sweep.IsUnchanged.Should().BeFalse();
        store.Count.Should().Be(0);
        store.DroppedTrackCount.Should().Be(1);
    }

    /// <summary>A freshness notice fires when the band changes, and never while it merely persists.</summary>
    /// <remarks>
    /// The trap this pins is a standing condition reported as an event: a sweep raising "still
    /// stale" every tick would put sixty messages a second per contact into a queue somebody has
    /// to drain. The sweep also hands its findings back to the caller rather than queuing them
    /// internally, so there is nothing inside the store that accumulates waiting for a reader.
    /// </remarks>
    [Fact]
    public void A_Freshness_Notice_Is_Raised_On_The_Transition_And_Not_On_Every_Sweep()
    {
        var store = Store();
        store.Ingest(Report(0.0));

        store.Advance(1.0).IsUnchanged.Should().BeTrue("still fresh is not news");

        var crossing = store.Advance(6.0);
        var transition = crossing.Transitions.Should().ContainSingle().Which;
        transition.TrackId.Should().Be(ContactId);
        transition.Previous.Should().Be(DataFreshness.Fresh);
        transition.Current.Should().Be(DataFreshness.Stale);
        transition.AgeSeconds.Should().BeApproximately(6.0, Tolerance);
        transition.SimulationTimeSeconds.Should().Be(6.0);
        crossing.DroppedTrackIds.Should().BeEmpty();

        store.Advance(7.0).IsUnchanged.Should().BeTrue("a contact that stays stale says nothing more");
        store.Advance(12.0).IsUnchanged.Should().BeTrue();

        store.Advance(21.0).Transitions.Should().ContainSingle()
            .Which.Current.Should().Be(DataFreshness.Lost);
        store.Advance(22.0).IsUnchanged.Should().BeTrue();
    }

    /// <summary>The same reports produce the same picture, because nothing here reads a clock.</summary>
    /// <remarks>
    /// Two stores built from identical inputs, fed identical reports, read at an identical
    /// simulated second. Anything consulting a wall clock — an instant stamped from "now", an age
    /// measured against real elapsed time — would make these two disagree, because real time
    /// passes between the two runs and simulated time does not.
    /// </remarks>
    [Fact]
    public void Two_Stores_Fed_The_Same_Reports_Publish_The_Same_Picture()
    {
        TrackReport[] reports =
        [
            Report(0.0, trackId: "alpha", eastM: 10.0),
            Report(1.0, trackId: "bravo", eastM: 20.0, sourceId: "ais-1"),
            Report(2.0, trackId: "alpha", eastM: 30.0, sourceId: "optical-1"),
        ];

        var left = Store();
        var right = Store();
        foreach (var report in reports)
        {
            left.Ingest(report);
            right.Ingest(report);
        }

        left.Snapshot(9.0).Should().BeEquivalentTo(
            right.Snapshot(9.0), options => options.WithStrictOrdering());

        left.Snapshot(9.0).Select(view => view.Track.TrackId).Should().Equal(
            ["alpha", "bravo"], "freshest observation first, ties broken by identifier");
    }

    // ─── Bounds: a flood cannot grow the session ─────────────────────────────

    /// <summary>A flood of identifiers is capped, and the stalest contact is the one that goes.</summary>
    /// <remarks>
    /// A hundred reports into a four-contact session leave four contacts and ninety-six
    /// retirements — visible in the counter rather than silent. The survivors are the four most
    /// recently observed, which is the documented stalest-first policy: dropping the newest
    /// instead would let a chatty source freeze the picture.
    /// </remarks>
    [Fact]
    public void A_Flood_Of_Distinct_Contacts_Cannot_Grow_The_Store_Past_Its_Cap()
    {
        var store = Store(new ExternalTrackStoreOptions(MaxTracks: 4));

        for (int i = 0; i < 100; i++)
        {
            store.Ingest(Report(i * 0.1, trackId: $"flood-{i:D3}", eastM: i));
        }

        store.Count.Should().Be(4);
        store.DroppedTrackCount.Should().Be(96, "every retirement is counted where it can be seen");
        store.Snapshot(9.9).Select(view => view.Track.TrackId).Should().Equal(
            ["flood-099", "flood-098", "flood-097", "flood-096"]);
    }

    /// <summary>A full session refuses a report older than everything it already holds.</summary>
    /// <remarks>
    /// The guard on the eviction policy. Without it a source spraying unique identifiers with old
    /// timestamps could evict a well-observed contact to make room for its own noise; with it the
    /// report is refused, the refusal is counted, and nothing is evicted for a report that was not
    /// kept.
    /// </remarks>
    [Fact]
    public void A_Full_Session_Refuses_A_Report_Staler_Than_Everything_It_Holds()
    {
        var store = Store(new ExternalTrackStoreOptions(MaxTracks: 2));
        store.Ingest(Report(5.0, trackId: "held-a"));
        store.Ingest(Report(6.0, trackId: "held-b"));

        var refused = store.Ingest(Report(4.0, trackId: "latecomer"));

        refused.Outcome.Should().Be(TrackIngestOutcome.RejectedCapacity);
        refused.Track.Should().BeNull();
        refused.ReasonCode.Should().Be(TrackProblems.CapacityReached);
        refused.EvictedTrackId.Should().BeNull("nothing may be evicted for a report that was refused");
        store.Count.Should().Be(2);
        store.RejectedReportCount.Should().Be(1);
        store.Snapshot(6.0).Select(view => view.Track.TrackId).Should().Equal(["held-b", "held-a"]);
    }

    /// <summary>One contact's source list is bounded too, dropping the least recently heard from.</summary>
    /// <remarks>
    /// The bound that is easy to miss: a feed minting a new sensor identifier per plot grows a
    /// single contact's source list forever while looking, from outside, like one well-observed
    /// track.
    /// </remarks>
    [Fact]
    public void One_Contacts_Source_List_Is_Bounded_And_Ordered_Most_Recent_First()
    {
        var store = Store(new ExternalTrackStoreOptions(MaxSourcesPerTrack: 3));

        for (int i = 0; i < 10; i++)
        {
            store.Ingest(Report(i, sourceId: $"sensor-{i}"));
        }

        OnlyTrack(store, 9.0).Track.Sources.Select(s => s.SourceId).Should().Equal(
            ["sensor-9", "sensor-8", "sensor-7"]);
    }

    // ─── Closest point of approach, against closed-form cases ────────────────

    /// <summary>Head-on: the time and the separation match the closed form, in three quantities.</summary>
    /// <remarks>
    /// Two platforms closing head-on 1000 m apart at 10 m/s each, 120 m apart vertically. The
    /// minimum of <c>|r + v t|</c> is at <c>t* = -(r.v)/(v.v) = 20000/400 = 50 s</c>, and there the
    /// horizontal separation is zero while the slant separation is the whole 120 m of height. A
    /// single "range" field would be read as whichever one the reader had in mind, which is why
    /// all three are published — and why all three are asserted.
    /// </remarks>
    [Fact]
    public void Head_On_Closing_Reports_The_Closed_Form_Time_And_All_Three_Separations()
    {
        var subject = Sample("own", Eus(0.0, 0.0, 0.0), Eus(0.0, 0.0, -10.0));
        var contact = Sample(ContactId, Eus(0.0, 120.0, -1000.0), Eus(0.0, 0.0, 10.0));

        var advisory = ClosestPointOfApproach.Compute(subject, contact);

        advisory.IsClosing.Should().BeTrue();
        advisory.RelativeSpeedMps.Should().BeApproximately(20.0, Tolerance);
        advisory.HasClosestApproach.Should().BeTrue();
        advisory.TimeToClosestApproachSeconds.Should().BeApproximately(50.0, Tolerance);

        advisory.RangeM.Should().BeApproximately(Math.Sqrt(1_014_400.0), 1e-6);
        advisory.HorizontalRangeM.Should().BeApproximately(1000.0, 1e-6);
        advisory.ClosestApproachDistanceM.Should().BeApproximately(120.0, 1e-6);
        advisory.ClosestApproachHorizontalDistanceM.Should().BeApproximately(0.0, 1e-6);
        advisory.ClosestApproachVerticalSeparationM.Should().BeApproximately(120.0, 1e-6);

        advisory.TrueBearingRad.Should().BeApproximately(0.0, Tolerance, "the contact bears due north");
        advisory.RelativeBearingRad.Should().BeApproximately(0.0, Tolerance);
        advisory.BearingReference.Should().Be(
            BearingReferenceKind.CourseOverGround,
            "no attitude was reported, and the substitution is recorded rather than hidden");
        advisory.Geometry.Should().Be(EncounterGeometry.ApproachingFromAhead);
    }

    /// <summary>Parallel at the same speed: no approach, and no division by a vanishing speed.</summary>
    /// <remarks>
    /// The case the closed form has to be guarded against. With a relative velocity of zero,
    /// <c>t* = -(r.v)/(v.v)</c> is nought over nought; the separation is not changing, so there is
    /// no time at which it is least and none is reported. Every published number stays finite,
    /// which is the assertion that fails on a <c>NaN</c> leaking into a frame.
    /// </remarks>
    [Fact]
    public void Parallel_At_The_Same_Speed_Reports_No_Approach_And_No_Division_By_Zero()
    {
        var subject = Sample("own", Eus(0.0, 0.0, 0.0), Eus(0.0, 0.0, -10.0));
        var contact = Sample(ContactId, Eus(300.0, 0.0, 0.0), Eus(0.0, 0.0, -10.0));

        var advisory = ClosestPointOfApproach.Compute(subject, contact);

        advisory.RelativeSpeedMps.Should().Be(0.0);
        advisory.IsClosing.Should().BeFalse();
        advisory.TimeToClosestApproachSeconds.Should().BeNull();
        advisory.HasClosestApproach.Should().BeFalse();
        advisory.Geometry.Should().Be(EncounterGeometry.NoRelativeMotion);

        advisory.RangeM.Should().BeApproximately(300.0, 1e-6);
        advisory.ClosestApproachDistanceM.Should().Be(
            advisory.RangeM, "with no approach ahead, the closest point is the present one");

        EveryPublishedNumber(advisory).Should().OnlyContain(
            value => double.IsFinite(value), "a vanishing relative speed must not produce a NaN");
    }

    /// <summary>Diverging: reported as no approach, and never as a negative time.</summary>
    /// <remarks>
    /// The minimum lies behind them. A negative time reads on a display as an approach that has
    /// not happened yet, so none is published at all and the picture is labelled for what it is.
    /// </remarks>
    [Fact]
    public void Diverging_Reports_No_Approach_Rather_Than_A_Time_In_The_Past()
    {
        var subject = Sample("own", Eus(0.0, 0.0, 0.0), Eus(0.0, 0.0, 0.0));
        var contact = Sample(ContactId, Eus(100.0, 0.0, 0.0), Eus(10.0, 0.0, 0.0));

        var advisory = ClosestPointOfApproach.Compute(subject, contact);

        advisory.IsClosing.Should().BeFalse();
        advisory.TimeToClosestApproachSeconds.Should().BeNull("a time in the past is not an approach");
        advisory.HasClosestApproach.Should().BeFalse();
        advisory.Geometry.Should().Be(EncounterGeometry.Diverging);
        advisory.ClosestApproachDistanceM.Should().BeApproximately(100.0, 1e-6);
        advisory.RelativeSpeedMps.Should().BeApproximately(10.0, Tolerance);

        // A stationary subject has no course to measure a bearing from, and none is invented.
        advisory.RelativeBearingRad.Should().BeNull();
        advisory.BearingReference.Should().Be(BearingReferenceKind.None);
    }

    /// <summary>A crossing geometry whose analytic closest point is known exactly.</summary>
    /// <remarks>
    /// Subject running east at 10 m/s from the origin; contact 600 m to the south running north at
    /// 10 m/s. Then <c>r = (0, 0, 600)</c>, <c>v = (-10, 0, -10)</c>, <c>r.v = -6000</c> and
    /// <c>v.v = 200</c>, so <c>t* = 30 s</c> and the separation there is
    /// <c>|(-300, 0, 300)| = 300*sqrt(2)</c>. The contact bears due south while the subject makes
    /// good due east, which puts it on the beam — outside both quadrantal sectors, and so
    /// described as crossing.
    /// </remarks>
    [Fact]
    public void A_Crossing_Geometry_Matches_Its_Analytic_Closest_Point_And_Time()
    {
        var subject = Sample("own", Eus(0.0, 0.0, 0.0), Eus(10.0, 0.0, 0.0));
        var contact = Sample(ContactId, Eus(0.0, 0.0, 600.0), Eus(0.0, 0.0, -10.0));

        var advisory = ClosestPointOfApproach.Compute(subject, contact);

        advisory.IsClosing.Should().BeTrue();
        advisory.RelativeSpeedMps.Should().BeApproximately(Math.Sqrt(200.0), Tolerance);
        advisory.TimeToClosestApproachSeconds.Should().BeApproximately(30.0, Tolerance);
        advisory.ClosestApproachDistanceM.Should().BeApproximately(300.0 * Math.Sqrt(2.0), 1e-6);
        advisory.ClosestApproachHorizontalDistanceM.Should().BeApproximately(
            300.0 * Math.Sqrt(2.0), 1e-6);
        advisory.ClosestApproachVerticalSeparationM.Should().BeApproximately(0.0, 1e-6);

        advisory.RangeM.Should().BeApproximately(600.0, 1e-6);
        advisory.HorizontalRangeM.Should().BeApproximately(600.0, 1e-6);
        advisory.TrueBearingRad.Should().BeApproximately(Math.PI, Tolerance);
        advisory.RelativeBearingRad.Should().BeApproximately(Math.PI / 2.0, Tolerance);
        advisory.Geometry.Should().Be(EncounterGeometry.Crossing);
    }

    /// <summary>A declared heading, not the course made good, is what a bearing is measured from.</summary>
    /// <remarks>
    /// The same encounter twice, differing only in whether the subject declared an attitude. A
    /// vessel crabbing under a beam current points east while making good north, so the same
    /// contact bears dead ahead of its course and on the beam of its bow. Both answers are right;
    /// which one was measured travels with the number, because a relative bearing quietly taken
    /// from a course when the reader assumed a heading is wrong by exactly the drift angle and
    /// nothing in the number says so.
    /// <para>
    /// Every scalar in the geometry is identical across the pair: the reference direction changes
    /// the description, never the closing arithmetic.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Bearing_Reference_Says_Whether_A_Heading_Or_A_Course_Was_Used()
    {
        var contact = Sample(ContactId, Eus(0.0, 120.0, -1000.0), Eus(0.0, 0.0, 10.0));
        var drifting = Sample("own", Eus(0.0, 0.0, 0.0), Eus(0.0, 0.0, -10.0));
        var pointing = drifting with { HeadingRad = Math.PI / 2.0 };

        var fromCourse = ClosestPointOfApproach.Compute(drifting, contact);
        var fromHeading = ClosestPointOfApproach.Compute(pointing, contact);

        fromCourse.BearingReference.Should().Be(BearingReferenceKind.CourseOverGround);
        fromCourse.RelativeBearingRad.Should().BeApproximately(0.0, Tolerance);
        fromCourse.Geometry.Should().Be(EncounterGeometry.ApproachingFromAhead);

        fromHeading.BearingReference.Should().Be(BearingReferenceKind.Heading);
        fromHeading.RelativeBearingRad.Should().BeApproximately(3.0 * Math.PI / 2.0, Tolerance);
        fromHeading.Geometry.Should().Be(EncounterGeometry.Crossing);

        fromHeading.TrueBearingRad.Should().Be(fromCourse.TrueBearingRad);
        fromHeading.TimeToClosestApproachSeconds.Should().Be(fromCourse.TimeToClosestApproachSeconds);
        fromHeading.ClosestApproachDistanceM.Should().Be(fromCourse.ClosestApproachDistanceM);
        fromHeading.RelativeSpeedMps.Should().Be(fromCourse.RelativeSpeedMps);
    }

    /// <summary>An advisory carries the age and confidence of the worse of its two inputs.</summary>
    /// <remarks>
    /// An advisory is exactly as current as its least current input, so the older age and the
    /// lower confidence are the ones to put in front of an operator — and both halves travel too,
    /// so nobody has to go and look them up. A held contact carries its own age straight through
    /// <see cref="ClosestPointOfApproach.TryFromTrack"/>, which is the wiring that makes an
    /// advisory built on a twelve-second-old plot say so.
    /// </remarks>
    [Fact]
    public void An_Advisory_Carries_The_Staleness_Of_Its_Least_Current_Input()
    {
        var store = Store();
        store.Ingest(Report(0.0, confidence: 1.0));

        ClosestPointOfApproach.TryFromTrack(OnlyTrack(store, 12.5), out var contact)
            .Should().BeTrue();
        contact.AgeSeconds.Should().BeApproximately(12.5, Tolerance);
        contact.Freshness.Should().Be(DataFreshness.Stale);
        contact.Confidence.Should().BeApproximately(0.6, 1e-12);

        var subject = Sample(
            "own", Eus(0.0, 0.0, 0.0), Eus(0.0, 0.0, -10.0), age: 0.2, confidence: 0.95);
        var advisory = ClosestPointOfApproach.Compute(subject, contact);

        advisory.SubjectAgeSeconds.Should().BeApproximately(0.2, Tolerance);
        advisory.ContactAgeSeconds.Should().BeApproximately(12.5, Tolerance);
        advisory.DataAgeSeconds.Should().BeApproximately(12.5, Tolerance, "the older of the two");
        advisory.Confidence.Should().BeApproximately(0.6, 1e-12, "the lower of the two");
        advisory.Freshness.Should().Be(DataFreshness.Stale, "the worse of the two bands");
        advisory.IsBuiltOnDegradedData.Should().BeTrue();
    }

    // ─── The classification is a description, not a decision ─────────────────

    /// <summary>Encounter geometry names describe a picture and nothing else.</summary>
    /// <remarks>
    /// Two ways this could stop being true, and both are checked. The names could start carrying
    /// advice — anything about avoiding, giving way, alarming or acting — and the record could
    /// grow a member that reads as a decision. What survives is a label for where a contact bears
    /// and whether the separation is shrinking, which a person then reads.
    /// </remarks>
    [Fact]
    public void The_Encounter_Vocabulary_Describes_The_Picture_And_Advises_Nothing()
    {
        var vocabulary = Enum.GetNames<EncounterGeometry>()
            .Concat(typeof(ApproachAdvisory)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name))
            .ToList();

        vocabulary.Where(IsDirective).Should().BeEmpty(
            "the geometry describes where a contact bears and whether the separation is "
            + "shrinking; deciding what to do about it is a person's job");

        typeof(ApproachAdvisory)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && !IsCompilerSuppliedRecordMember(m.Name))
            .Should().BeEmpty("an advisory is a description, so it has nothing to do");
    }

    /// <summary>Neither platform is privileged: swapping the two changes only the viewpoint.</summary>
    /// <remarks>
    /// The label is measured from whichever platform is named subject, so it may legitimately
    /// differ between the two orderings. What must not differ is the geometry itself — the same
    /// two platforms pass at the same distance at the same moment whichever of them is asking —
    /// because a scalar that changed with the ordering would be a precedence claim hiding inside a
    /// number.
    /// </remarks>
    [Fact]
    public void Swapping_Subject_And_Contact_Changes_The_Viewpoint_Not_The_Geometry()
    {
        var alpha = Sample("alpha", Eus(0.0, 0.0, 0.0), Eus(10.0, 0.0, 0.0));
        var bravo = Sample("bravo", Eus(0.0, 0.0, 600.0), Eus(0.0, 0.0, -10.0));

        var forward = ClosestPointOfApproach.Compute(alpha, bravo);
        var reverse = ClosestPointOfApproach.Compute(bravo, alpha);

        reverse.RangeM.Should().Be(forward.RangeM);
        reverse.HorizontalRangeM.Should().Be(forward.HorizontalRangeM);
        reverse.RelativeSpeedMps.Should().Be(forward.RelativeSpeedMps);
        reverse.IsClosing.Should().Be(forward.IsClosing);
        reverse.TimeToClosestApproachSeconds.Should().Be(forward.TimeToClosestApproachSeconds);
        reverse.ClosestApproachDistanceM.Should().Be(forward.ClosestApproachDistanceM);

        reverse.SubjectId.Should().Be("bravo");
        reverse.ContactId.Should().Be("alpha");
    }

    // ─── Wording: an advisory must keep saying so ────────────────────────────

    /// <summary>Nothing in the track or geometry code claims more than advisory status.</summary>
    /// <remarks>
    /// The one property here with no runtime consequence and the worst failure mode. Every number
    /// this code produces can be right while a doc comment or an operator-facing string quietly
    /// promises regulatory compliance, certified collision avoidance or authority to navigate —
    /// and a reader who believes the sentence will not go on to check the arithmetic.
    /// <para>
    /// A claim word is permitted only inside an explicitly advisory framing: the same sentence has
    /// to negate or qualify it. That is deliberately a low bar, so the scanner is exercised
    /// against a fabricated claim first — a check that can only ever pass proves nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_Symbol_Comment_Or_String_Claims_Compliance_Certification_Or_Authority()
    {
        ImpliesAnUnqualifiedClaim("Provides certified collision avoidance for autonomous navigation.")
            .Should().BeTrue("the scanner has to be able to fail");
        ImpliesAnUnqualifiedClaim("It is not collision avoidance and claims no compliance.")
            .Should().BeFalse("an explicitly negated claim is the framing this code is required to use");

        var offenders = new List<string>();

        foreach (var path in TrackSourcePaths())
        {
            offenders.AddRange(Statements(path)
                .Where(ImpliesAnUnqualifiedClaim)
                .Select(statement => $"{Path.GetFileName(path)}: {statement}"));
        }

        foreach (var type in TrackSurfaceTypes())
        {
            offenders.AddRange(PublicSymbolText(type)
                .Where(ImpliesAnUnqualifiedClaim)
                .Select(text => $"{type.Name}: {text}"));
        }

        offenders.Should().BeEmpty(
            "closest-point-of-approach features here are decision support: nothing may read as "
            + "compliance with a navigation rule set, as certified avoidance, or as authority to "
            + "navigate");

        ClosestPointOfApproach.AdvisoryNotice.Should().Contain(
            "Advisory only", "the qualification travels with the numbers it qualifies");
    }
}
