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

using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Guards the delta stream as a <em>transport</em>: that a client is never asked to merge a
/// change onto a picture it does not hold, that the chain is checkable rather than merely
/// plausible, that a resync is bounded on the server no matter how a client behaves, and that
/// neither of the two older streams notices any of this exists.
/// </summary>
/// <remarks>
/// The failures this file exists for are silent ones. A delta applied to the wrong base still
/// deserialises and still renders — it renders a fleet that is subtly and permanently wrong, with
/// no error anywhere. A chain that advanced across a frame nobody received loses an asset that
/// spawned and despawned inside the gap, and the symptom is a vehicle that was never drawn rather
/// than an exception. So the assertions below are almost never "a message arrived": they compare
/// a delta's declared base against the frame that actually preceded it on the wire, and where the
/// property is reconstruction they run the frames back through
/// <see cref="VizSnapshotDiffer.Apply"/>, which refuses a mismatch instead of merging it.
/// <para>
/// Nothing here runs the 60 Hz loop. <see cref="SimulationManager.BroadcastRoomAsync"/> is driven
/// one tick at a time, because racing a background service to observe a broadcast makes a test
/// that fails on a busy machine and proves nothing on a quiet one. The world is deliberately left
/// unstepped in most cases too: what is under test is which shape of picture reaches the wire and
/// what it names as its base, and a moving fleet only adds noise to that.
/// </para>
/// </remarks>
public sealed partial class DeltaStreamTests
{
    // ─── A client never merges onto a picture it does not hold ──────────────

    /// <summary>The first thing a new delta subscriber receives is a complete frame.</summary>
    /// <remarks>
    /// There is nothing for a first delta to apply to, so a stream that opened with one would be
    /// asking a client to reconstruct a fleet from a description of how it changed. The room
    /// holds no baseline until a frame is published, which makes this structural rather than a
    /// convention the broadcaster has to remember.
    /// </remarks>
    [Fact]
    public async Task First_Frame_For_A_New_Subscriber_Is_A_Full_Snapshot()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();

        await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);

        broadcaster.Deltas.Should().BeEmpty("there is no frame for a first delta to apply to");
        var opening = broadcaster.Keyframes.Should().ContainSingle().Which;
        opening.RoomId.Should().Be(RoomId);
        opening.Snapshot.DescriptorsComplete.Should().BeTrue(
            "a keyframe is a whole frame, and a client prunes its descriptor cache on this flag");
        opening.Snapshot.Assets.Select(a => a.AssetId).Should()
            .BeEquivalentTo(new[] { AirId, GroundId, SurfaceId });
        opening.Snapshot.SchemaVersion.Should().Be(VizSnapshotV2.CurrentSchemaVersion,
            "a keyframe and the deltas it interleaves with can never claim different schemas");
    }

    /// <summary>Subscribing through the hub is itself the resync, so the stream opens on a snapshot.</summary>
    /// <remarks>
    /// The path that matters operationally: a client calls <c>SubscribeDeltas</c> and the next
    /// broadcast is complete. Joining, reconnecting and recovering from a gap all end in the same
    /// message this way, which is why none of them needs a code path of its own on the client.
    /// </remarks>
    [Fact]
    public async Task Subscribing_Through_The_Hub_Opens_The_Stream_With_A_Snapshot()
    {
        var room = CreatePopulatedRoom();
        var (hub, _) = CreateBoundHub(room);
        var broadcaster = new RecordingBroadcaster();

        var version = await hub.SubscribeDeltas(true);
        await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);

        version.Should().Be(VizSnapshotV2.CurrentSchemaVersion);
        room.DeltaSubscriberCount.Should().Be(1);
        broadcaster.Published.Should().ContainSingle().Which.IsKeyframe.Should().BeTrue();
        broadcaster.Snapshots.Should().BeEmpty(
            "a delta subscriber leaves the full-snapshot group; receiving a whole snapshot and a "
            + "delta describing it every frame is worse than receiving either alone");
    }

    /// <summary>Every delta on a run names the frame that actually preceded it on the wire.</summary>
    /// <remarks>
    /// The base is checked as an identity — <see cref="VizDeltaV2.BaseFrameId"/> against the
    /// previous frame's id, and <see cref="VizDeltaV2.BaseSequence"/> against the position that
    /// frame was assigned. The frame id proves a mismatch; the sequence is the value a client
    /// actually tests, because a <see cref="Guid"/> has no order and cannot say how far apart two
    /// frames are.
    /// </remarks>
    [Fact]
    public async Task Deltas_Chain_To_The_Frame_That_Preceded_Them()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        for (var i = 0; i < MaxChainFrames; i++)
        {
            Step(room, 6);
            await manager.BroadcastRoomAsync(room, CancellationToken.None);
        }

        var published = broadcaster.Published;
        published.Should().HaveCount(MaxChainFrames);
        published.Skip(1).Should().OnlyContain(p => !p.IsKeyframe,
            "this room's periodic keyframe falls at stream sequence 42, well beyond this run");

        AssertChainIsSound(published);
    }

    /// <summary>A reconstruction from the chain agrees with the frames the server assembled.</summary>
    /// <remarks>
    /// A different claim from the case above: that the stream a client sees is <em>sufficient</em>,
    /// not merely well-labelled. An asset appears and is removed mid-run so the upsert and the
    /// removal channels both carry something — an all-static fleet would pass a merge that
    /// silently ignored both. <see cref="VizSnapshotDiffer.Apply"/> is strict on purpose: it
    /// refuses a base it does not hold and refuses an asset a delta leaves unaccounted for, so a
    /// chain that is self-consistently labelled and still does not reconstruct fails here.
    /// </remarks>
    [Fact]
    public async Task Applying_The_Chain_Reproduces_The_Frames_The_Server_Sent()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        Register(room, new StaticAsset(AssetProfiles.Create(LateId, VehicleClass.AckermannRover)));
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        room.TryRemoveAsset(LateId, out _).Should().BeTrue();
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var published = broadcaster.Published;
        published.Should().HaveCount(3);

        var appeared = DeltaOf(published[1]);
        appeared.Assets.Select(a => a.AssetId).Should().Contain(LateId,
            "an asset with no baseline record has nothing to diff against, so it ships whole");
        appeared.Descriptors.Select(d => d.AssetId).Should().Contain(LateId);

        DeltaOf(published[2]).RemovedAssetIds.Should().Contain(LateId,
            "omission already means unchanged, so a removal has to be stated explicitly");

        var reconstructed = KeyframeOf(published[0]);
        foreach (var frame in published.Skip(1))
        {
            reconstructed = frame.Keyframe ?? VizSnapshotDiffer.Apply(reconstructed, DeltaOf(frame));
        }

        reconstructed.Assets.Select(a => a.AssetId).Should()
            .BeEquivalentTo(new[] { AirId, GroundId, SurfaceId },
                "the late asset was added and removed inside the chain");
        reconstructed.DescriptorsComplete.Should().BeTrue(
            "a reconstruction is a complete frame, whatever the deltas that built it carried");
        reconstructed.Tick.Should().Be(room.TickCount);
    }

    // ─── Discontinuities publish a snapshot, not a delta ────────────────────

    /// <summary>A reset is a new world, so the stream restarts on a full snapshot.</summary>
    /// <remarks>
    /// A delta across a reset is not merely wasteful, it is wrong in a way nothing detects: the
    /// baseline's assets are gone rather than unchanged, so a client holding the old frame would
    /// keep every one of them and draw a fleet from a session that no longer exists. The reset
    /// bumps the environment revision, which is the trigger; the tick going backwards is the
    /// second, independent trigger behind it.
    /// </remarks>
    [Fact]
    public async Task Reset_Produces_A_Snapshot_Across_The_Discontinuity()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        Step(room, 30);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        var revisionBeforeReset = room.EnvironmentRevision;

        room.Reset();
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var published = broadcaster.Published;
        published.Should().HaveCount(3);
        published[1].IsKeyframe.Should().BeFalse("nothing discontinuous happened before the reset");
        published[2].IsKeyframe.Should().BeTrue(
            "the world was replaced under the chain, so there is nothing for a delta to apply to");

        var restart = KeyframeOf(published[2]);
        restart.EnvironmentRevision.Should().NotBe(revisionBeforeReset,
            "a client's separately-cached terrain and weather are stale after a reset");
        restart.Tick.Should().Be(0, "the reset restarted the world clock");
        restart.DescriptorsComplete.Should().BeTrue();

        // The chain still advances across the discontinuity: a keyframe is as much a position in
        // it as a delta is, and the next delta names the keyframe rather than the pre-reset frame.
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var resumed = DeltaOf(broadcaster.Published[3]);
        resumed.BaseFrameId.Should().Be(restart.FrameId);
        resumed.BaseSequence.Should().Be(3);
        resumed.StreamSequence.Should().Be(4,
            "the stream sequence is monotonic for the room's life and deliberately survives a reset");
    }

    // ─── Resync: answered, bounded, and never dropped ───────────────────────

    /// <summary>An accepted resync request makes the room's next broadcast a complete frame.</summary>
    /// <remarks>
    /// And makes only the <em>next</em> one complete. The request is a flag that is read and
    /// cleared once, so the frame after it is a delta again rather than the room having been
    /// quietly switched into snapshot mode by one client's recovery.
    /// </remarks>
    [Fact]
    public async Task Resync_Request_Makes_The_Next_Broadcast_A_Full_Snapshot()
    {
        var room = CreatePopulatedRoom();
        var (hub, _) = CreateBoundHub(room);
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await hub.SubscribeDeltas(true);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var accepted = await hub.RequestKeyframe();
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        accepted.Should().BeTrue();
        var published = broadcaster.Published;
        published.Should().HaveCount(4);
        published[1].IsKeyframe.Should().BeFalse();
        published[2].IsKeyframe.Should().BeTrue("the client asked for a picture it could rebuild from");
        KeyframeOf(published[2]).DescriptorsComplete.Should().BeTrue();

        published[3].IsKeyframe.Should().BeFalse("the request was spent, not made sticky");
        DeltaOf(published[3]).BaseFrameId.Should().Be(KeyframeOf(published[2]).FrameId,
            "the keyframe that answered the resync is the frame the next delta applies to");
    }

    /// <summary>A connection may spend only its budget of resync requests per window.</summary>
    /// <remarks>
    /// The budget is generous on purpose — a healthy client asks once per gap and gaps are rare —
    /// so exhausting it identifies a broken or hostile client rather than policing a normal one.
    /// A refused client is not stranded: the periodic keyframe re-establishes its picture on the
    /// same backstop a client that cannot ask at all relies on, which is why a rejection is
    /// reported rather than thrown.
    /// </remarks>
    [Fact]
    public async Task Resync_Is_Rate_Limited_Per_Connection()
    {
        var room = CreatePopulatedRoom();
        var (hub, _) = CreateBoundHub(room);
        await hub.SubscribeDeltas(true);

        var outcomes = new List<bool>();
        for (var i = 0; i < 8; i++)
        {
            outcomes.Add(await hub.RequestKeyframe());
        }

        outcomes.Take(5).Should().AllBeEquivalentTo(true, "the window's budget is five requests");
        outcomes.Skip(5).Should().AllBeEquivalentTo(false,
            "past the budget the hub does no work at all on a broken client's behalf");
    }

    /// <summary>One noisy connection cannot spend another connection's budget.</summary>
    /// <remarks>
    /// The budget lives in the caller's own connection items, so it dies with the connection and
    /// a reconnect starts fresh — which is correct, because a reconnect is itself a legitimate
    /// reason to want a keyframe. A budget shared per room would let one broken tab lock every
    /// other operator out of resynchronising.
    /// </remarks>
    [Fact]
    public async Task One_Connection_Cannot_Exhaust_Another_Connections_Budget()
    {
        var room = CreatePopulatedRoom();
        var (noisy, _) = CreateBoundHub(room, "conn-noisy");
        var (quiet, _) = CreateBoundHub(room, "conn-quiet");
        await noisy.SubscribeDeltas(true);
        await quiet.SubscribeDeltas(true);

        for (var i = 0; i < 8; i++)
        {
            await noisy.RequestKeyframe();
        }

        (await quiet.RequestKeyframe()).Should().BeTrue();
    }

    /// <summary>A full-snapshot connection cannot spend the room's resync budget.</summary>
    /// <remarks>
    /// It already receives a complete frame ten times a second and has nothing to resynchronise,
    /// so answering it would let any connection in the room force keyframes on behalf of the
    /// delta subscribers.
    /// </remarks>
    [Fact]
    public async Task Resync_From_A_Connection_Not_Receiving_Deltas_Is_Refused()
    {
        var room = CreatePopulatedRoom();
        var (hub, _) = CreateBoundHub(room);
        await hub.SubscribeSnapshots(true);

        (await hub.RequestKeyframe()).Should().BeFalse();
        room.DeltaSubscriberCount.Should().Be(0);
    }

    /// <summary>Spamming resync costs one keyframe per broadcast tick and never more.</summary>
    /// <remarks>
    /// <b>This is the bound that actually matters, and it is structural rather than policed.</b>
    /// The room holds a flag, not a queue, and reads it once per broadcast — so any number of
    /// requests arriving between two ticks collapses into a single keyframe, and the worst case a
    /// client can drive a room to is a keyframe on every tick. That worst case is exactly what a
    /// full-snapshot subscriber receives today: there is no input to the resync path that makes a
    /// room more expensive than not using deltas at all.
    /// <para>
    /// The requests are issued through the room rather than the hub deliberately. The hub's
    /// per-connection budget would cap the spam at five and the case would then be proving the
    /// rate limiter, rather than the bound underneath it that has to hold even if the limiter is
    /// misconfigured or removed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Spamming_Resync_Cannot_Force_More_Than_One_Snapshot_Per_Tick()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        for (var i = 0; i < 500; i++)
        {
            room.RequestKeyframe();
        }

        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var published = broadcaster.Published;
        published.Should().HaveCount(3, "a broadcast tick publishes exactly one frame, spam or not");
        published[1].IsKeyframe.Should().BeTrue("the pending request is answered once");
        published[2].IsKeyframe.Should().BeFalse(
            "five hundred requests collapsed into one; a queue would still be draining here");
        broadcaster.Keyframes.Should().HaveCount(2,
            "the opening snapshot and one answer — not five hundred snapshot rebuilds");
    }

    /// <summary>A pending resync survives a tick the room could not broadcast.</summary>
    /// <remarks>
    /// A resync answer is the one thing backpressure must never eat. A client that asked and was
    /// silently skipped would sit on a picture it cannot merge onto until the periodic keyframe
    /// came round seconds later — precisely the stall the request exists to avoid. The flag is
    /// read and cleared inside the broadcast slot, so a tick that never gets the slot cannot
    /// consume it.
    /// </remarks>
    [Fact]
    public async Task A_Resync_Answer_Is_Never_Dropped_By_Backpressure()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        room.RequestKeyframe();

        room.TryBeginBroadcast().Should().BeTrue("the slot is free between ticks");
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        room.EndBroadcast();

        broadcaster.Published.Should().ContainSingle("both ticks found the slot taken and were skipped");

        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        broadcaster.Published.Should().HaveCount(2);
        broadcaster.Published[1].IsKeyframe.Should().BeTrue(
            "the request outlived the skipped ticks rather than being spent by them");
    }

    // ─── Backpressure ───────────────────────────────────────────────────────

    /// <summary>Ticks skipped because the previous send was still in flight are counted, and cost no state.</summary>
    /// <remarks>
    /// A drop nobody counts is a room quietly streaming at a fraction of its cadence with no
    /// signal anywhere; the counter is what separates "clients are not keeping up with the wire"
    /// from "frames got bigger". The assertion is a lower bound because the instrument is
    /// process-wide and xUnit runs test classes in parallel — a concurrent suite can only inflate
    /// it, never hide a missing increment.
    /// <para>
    /// The stronger half is the second: the chain does not advance on a skip. The frame published
    /// after the gap names the last frame clients actually received, which is what keeps
    /// <see cref="VizDeltaV2.RemovedAssetIds"/> honest — an asset that appeared and vanished
    /// inside the gap would otherwise never be mentioned in any delta.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ticks_Dropped_Under_Backpressure_Are_Counted_And_Cost_No_State()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        var opening = KeyframeOf(broadcaster.Published.Single());

        using var drops = new CounterProbe(DropCounterName);

        room.TryBeginBroadcast().Should().BeTrue();
        for (var i = 0; i < 3; i++)
        {
            Step(room, 6);
            await manager.BroadcastRoomAsync(room, CancellationToken.None);
        }

        room.EndBroadcast();

        drops.Total.Should().BeGreaterThanOrEqualTo(3, "every skipped tick increments the counter");
        broadcaster.Published.Should().ContainSingle("a skipped tick publishes nothing at all");
        room.StreamSequence.Should().Be(1, "the chain does not advance across a frame nobody received");

        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var resumed = DeltaOf(broadcaster.Published[1]);
        resumed.BaseFrameId.Should().Be(opening.FrameId);
        resumed.BaseSequence.Should().Be(1);
        resumed.StreamSequence.Should().Be(2);
        resumed.Tick.Should().Be(room.TickCount,
            "the frame after the gap covers every tick the gap spanned");
    }

    // ─── The two older streams do not notice ────────────────────────────────

    /// <summary>The v1 frame is unaffected by the presence of delta subscribers.</summary>
    /// <remarks>
    /// The room is not stepped between the broadcasts, so the frames describe the same world and
    /// anything that differed would be the delta path leaking into a schema that has to keep
    /// working untouched for a full deprecation cycle. A v1 client never learns any of this
    /// exists: it is in the room group, it receives <c>ReceiveFrame</c>, and that is all.
    /// </remarks>
    [Fact]
    public async Task The_v1_Frame_Is_Unaffected_By_The_Delta_Stream()
    {
        var room = CreatePopulatedRoom();
        Step(room, 18);
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        room.IncrementDeltaSubscribers();
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        broadcaster.Frames.Should().HaveCount(3, "every tick broadcasts the v1 frame to the room group");
        broadcaster.Frames.Should().OnlyContain(f => f.RoomId == RoomId);
        broadcaster.Frames[1].Frame.Should().BeEquivalentTo(broadcaster.Frames[0].Frame);
        broadcaster.Frames[2].Frame.Should().BeEquivalentTo(broadcaster.Frames[0].Frame);
        broadcaster.Frames[0].Frame.Drones.Select(d => d.Id).Should().BeEquivalentTo(new[] { AirId },
            "the v1 schema is air-only and stays air-only");
        broadcaster.Published.Should().HaveCount(2, "only the ticks with a delta subscriber");
    }

    /// <summary>A full-snapshot client keeps receiving complete frames while deltas flow beside it.</summary>
    /// <remarks>
    /// The three tiers are layered, not alternatives, and this is the middle one: a client that
    /// migrated to v2 but not to deltas must see exactly what it saw before deltas existed. It
    /// receives a complete frame on every tick — no keyframe cadence, no gaps to detect, nothing
    /// to merge — and the deltas travelling to a different group never reach it.
    /// </remarks>
    [Fact]
    public async Task A_Full_Snapshot_Client_Still_Receives_Everything_It_Needs()
    {
        var room = CreatePopulatedRoom();
        room.IncrementSnapshotSubscribers();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        for (var i = 0; i < 4; i++)
        {
            Step(room, 6);
            await manager.BroadcastRoomAsync(room, CancellationToken.None);
        }

        broadcaster.Snapshots.Should().HaveCount(4, "a snapshot subscriber gets a whole frame per tick");
        broadcaster.Snapshots.Should().OnlyContain(s => s.Snapshot.DescriptorsComplete,
            "nothing on the snapshot stream is ever partial");
        broadcaster.Snapshots.Should().OnlyContain(
            s => s.Snapshot.Assets.Count == 3 && s.Snapshot.Descriptors.Count == 3,
            "every frame stands alone; a snapshot client holds no baseline to complete it from");

        broadcaster.Published.Should().HaveCount(4, "the delta group is served on the same ticks");
        broadcaster.Published[0].IsKeyframe.Should().BeTrue();
        broadcaster.Published.Skip(1).Should().OnlyContain(p => !p.IsKeyframe);
        // Both streams are projected from one capture, so they describe the same readings.
        broadcaster.Snapshots.Select(s => s.Snapshot.Tick).Should()
            .Equal(broadcaster.Published.Select(p => p.Tick));
    }

    // ─── The wire does not depend on the audience ───────────────────────────

    /// <summary>Two identically-driven rooms publish identical streams whatever their audience.</summary>
    /// <remarks>
    /// <b>The claim the whole design rests on: who is watching cannot change what is produced.</b>
    /// The per-room delta state lives outside the world and the tick loop never reads it, so
    /// subscriber counts, connection counts and client speed can only change which shape of
    /// picture is serialised. A regression here is a simulation whose output depends on its
    /// observers — reproducible on a developer's machine with one browser open and not
    /// reproducible in an incident review with nine.
    /// <para>
    /// Both rooms carry the same id, which is load-bearing rather than incidental: the periodic
    /// keyframe cadence is staggered by a hash of the id, so two rooms named differently would
    /// legitimately keyframe on different frames and the comparison would prove nothing.
    /// </para>
    /// <para>
    /// What is compared is the structure of the stream — shape, sequence, base, and which ids
    /// were upserted, carried and removed — rather than float poses, which would make this a
    /// determinism test of the physics instead of a test of the transport.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Published_Frames_Do_Not_Depend_On_The_Number_Of_Connected_Clients()
    {
        var lonely = new RecordingBroadcaster();
        var crowded = new RecordingBroadcaster();

        await DriveComparableRunAsync(lonely, deltaSubscribers: 1, snapshotSubscribers: 0, connections: 1);
        await DriveComparableRunAsync(crowded, deltaSubscribers: 9, snapshotSubscribers: 4, connections: 20);

        crowded.Published.Should().HaveCount(lonely.Published.Count);
        crowded.Frames.Should().HaveCount(lonely.Frames.Count,
            "the v1 frame is published once per tick regardless of how many clients read it");
        crowded.Snapshots.Should().NotBeEmpty("the crowded room also had full-snapshot subscribers");
        lonely.Snapshots.Should().BeEmpty("the lonely room had none, which must not change the chain");

        foreach (var (a, b) in lonely.Published.Zip(crowded.Published))
        {
            b.IsKeyframe.Should().Be(a.IsKeyframe,
                "the keyframe cadence is a property of the room, never of its audience");
            b.Tick.Should().Be(a.Tick);
            b.StreamSequence.Should().Be(a.StreamSequence);
            b.BaseSequence.Should().Be(a.BaseSequence);
            b.UpsertedAssetIds.Should().Equal(a.UpsertedAssetIds);
            b.UpsertedDescriptorIds.Should().Equal(a.UpsertedDescriptorIds);
            b.RemovedAssetIds.Should().Equal(a.RemovedAssetIds);
            b.CarriedAssetIds.Should().Equal(a.CarriedAssetIds);
        }

        AssertChainIsSound(lonely.Published);
        AssertChainIsSound(crowded.Published);
    }
}
