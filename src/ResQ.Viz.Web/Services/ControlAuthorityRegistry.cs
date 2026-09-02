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

using System.Globalization;
using System.Runtime.CompilerServices;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

/// <summary>The control authority belonging to each session, and the control mode they all run in.</summary>
/// <remarks>
/// A <see cref="ControlAuthority"/> has to outlive a request — a lease taken by one request is
/// what a later request is measured against — and it has to be scoped to a session, because two
/// rooms hold two different populations and an asset id means different things in each. Nothing
/// in <see cref="SimulationRoom"/> owns one, so this registry supplies it.
/// <para>
/// <b>Keyed by the room object, in a <see cref="ConditionalWeakTable{TKey,TValue}"/>.</b> That is
/// what makes "an authority never outlives its room" structural rather than a cleanup step
/// somebody has to remember: rooms are reaped by dropping them, and an entry keyed on a
/// collectable room goes with it. Keying on the room <em>id</em> would have needed the reaper to
/// call back here, and a missed call would leak one entry per session for the life of the
/// process.
/// </para>
/// <para>
/// <b>Lock order is registry, then authority, then room.</b> The presence probe below takes the
/// room's simulation lock while the authority holds its own, so an authority operation can block
/// behind a tick. Nothing anywhere takes the authority lock while holding the room lock, so the
/// order cannot invert. The room's lifecycle notifications do reach the authority — from a
/// removal and from the tick loop — but every one of them is raised with the room's lock already
/// released (see <see cref="IRoomLifecycleObserver"/>), so they enter at the top of that same
/// order rather than against it.
/// </para>
/// <para>
/// <b>The mode is fixed at startup, deliberately.</b> Resolving it once, where the process can
/// still refuse to start, is the whole value of the guard — a setting re-read per request could
/// change what a running console is attached to between two clicks.
/// </para>
/// </remarks>
public sealed class ControlAuthorityRegistry
{
    /// <summary>Configuration section the control mode and lease limits are read from.</summary>
    public const string ConfigurationSection = "ControlAuthority";

    /// <summary>Default cap on a single lease, in seconds, when configuration names none.</summary>
    private const int DefaultMaxLeaseSeconds = 120;

    /// <summary>Default number of lease records one session retains.</summary>
    private const int DefaultAuditCapacity = 256;

    /// <summary>Widest lease cap an operator may configure, in seconds.</summary>
    /// <remarks>
    /// An hour. The cap exists so an asset cannot be parked out of everyone else's reach, and a
    /// configured cap of a day would defeat it while looking like a setting rather than a defect.
    /// </remarks>
    private const int MaxConfigurableLeaseSeconds = 3600;

    private static readonly Lazy<ControlAuthorityRegistry> SharedInstance =
        new(() => new ControlAuthorityRegistry(TimeProvider.System, new ControlAuthorityOptions()));

    private readonly object _gate = new();
    private readonly ConditionalWeakTable<SimulationRoom, ControlAuthority> _byRoom = new();
    private readonly TimeProvider _clock;
    private readonly ControlAuthorityOptions _options;

    /// <summary>Creates a registry issuing authorities against one clock and one set of limits.</summary>
    /// <param name="clock">Source of every instant the authorities stamp or compare.</param>
    /// <param name="options">Lease cap and audit window applied to every session.</param>
    /// <param name="mode">Control mode to publish, or null for the simulation-only default.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ControlAuthorityRegistry(
        TimeProvider clock, ControlAuthorityOptions options, ControlModeStatus? mode = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _clock = clock;
        _options = options;
        Mode = mode ?? SimulationOnly;
    }

    /// <summary>
    /// The registry used when a <c>SimV2Controller</c> is built without the composition
    /// root, as its unit tests do.
    /// </summary>
    /// <remarks>
    /// Not a back door around dependency injection: <c>Program.cs</c> registers a configured
    /// instance and that is what a running server injects. This one exists so constructing the
    /// controller directly does not silently produce an authority that forgets every lease
    /// between two requests — which would make a lease gate untestable by exactly the tests most
    /// likely to be written for it. It is keyed by room object like any other, so a lease taken
    /// through it still belongs to one session and dies with it.
    /// </remarks>
    public static ControlAuthorityRegistry Shared => SharedInstance.Value;

    /// <summary>The mode every session in this process runs in.</summary>
    public ControlModeStatus Mode { get; }

    /// <summary>Longest a single lease may run before it has to be renewed.</summary>
    public TimeSpan MaxLeaseDuration => _options.MaxLeaseDuration ?? TimeSpan.FromMinutes(2);

    /// <summary>Builds the registry described by configuration, refusing a configuration this build cannot honour.</summary>
    /// <remarks>
    /// <b><c>AllowLiveControl</c> is a guard, not a feature toggle.</b> There is no hardware
    /// bearer anywhere in this build — no serial link, no radio, no vehicle-side endpoint — so
    /// setting it cannot enable anything. It is refused at startup rather than logged and
    /// ignored, because the failure being designed against is an operator who set it, saw the
    /// server come up, and concluded the console in front of them was attached to a vehicle. The
    /// flag exists now so that the day a live path is added it arrives behind a gate that already
    /// defaults closed, instead of behind one written in the same change as the path itself.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>A registry configured from <see cref="ConfigurationSection"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The configuration asks for something this build has no path for.</exception>
    public static ControlAuthorityRegistry FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        if (section.GetValue<bool>("AllowLiveControl", defaultValue: false))
        {
            throw new InvalidOperationException(
                $"{ConfigurationSection}:AllowLiveControl is set, but this build contains no live "
                + "control path: every command it accepts is executed by the in-process "
                + "simulation and reaches no hardware. Remove the setting rather than run a "
                + "server whose reported mode would not describe what it is attached to.");
        }

        var leaseSeconds = Math.Clamp(
            section.GetValue<int>("MaxLeaseSeconds", DefaultMaxLeaseSeconds), 1, MaxConfigurableLeaseSeconds);
        var auditCapacity = Math.Max(1, section.GetValue<int>("AuditCapacity", DefaultAuditCapacity));

        return new ControlAuthorityRegistry(
            TimeProvider.System,
            new ControlAuthorityOptions(TimeSpan.FromSeconds(leaseSeconds), auditCapacity));
    }

    /// <summary>The authority governing one session, creating it on first use.</summary>
    /// <remarks>
    /// The probe handed to a new authority reads the room's own registry under the room's lock,
    /// so a lease can never be issued for an asset that is not there, and every standing lease
    /// over a removed asset is swept on the next operation.
    /// <para>
    /// <b>It reports an instance, not a yes-or-no.</b> Ids are recyclable — remove a rover and
    /// spawn another under the same name and the id is back — so an existence check would let a
    /// standing lease follow the id onto a vehicle its holder never asked for. The ledger below
    /// gives each registered asset object a token of its own, which is what makes the
    /// replacement a different asset as far as a lease is concerned.
    /// </para>
    /// <para>
    /// The authority is also subscribed to the room's lifecycle here, which is what turns
    /// "a lapsed or orphaned lease would be swept whenever somebody next asks" into "it is
    /// swept when the asset goes, when the room resets, and once a second regardless".
    /// </para>
    /// </remarks>
    /// <param name="room">Session to get the authority for.</param>
    /// <returns>That session's authority. The same instance for the life of the room.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="room"/> is null.</exception>
    public ControlAuthority For(SimulationRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);

        if (_byRoom.TryGetValue(room, out var existing))
        {
            return existing;
        }

        // ConditionalWeakTable.GetValue may run its factory more than once under contention and
        // keep only one result. Creating a second authority is harmless in itself, but the loser
        // would be handed to whichever caller created it, and a lease taken through it would be
        // invisible to every later request. The lock is what makes one instance per room a fact.
        lock (_gate)
        {
            return _byRoom.GetValue(room, Create);
        }
    }

    /// <summary>Builds one room's authority and subscribes it to that room's lifecycle.</summary>
    /// <remarks>
    /// Runs exactly once per room: <see cref="For"/> holds <c>_gate</c> across
    /// <see cref="ConditionalWeakTable{TKey,TValue}.GetValue"/>, so the factory cannot race with
    /// itself. That is what makes subscribing from in here safe — a second run would attach a
    /// second observer and revoke every removed asset's lease twice, writing two records for one
    /// removal.
    /// <para>
    /// The ledger is captured by the probe and so lives exactly as long as the authority. Only
    /// the token leaves the room's lock: it is resolved inside
    /// <see cref="SimulationRoom.UseAssets{T}"/>, because handing the asset itself back out would
    /// be handing out a live view of world state.
    /// </para>
    /// </remarks>
    /// <param name="room">Room the authority governs.</param>
    /// <returns>The new authority, already listening to the room.</returns>
    private ControlAuthority Create(SimulationRoom room)
    {
        var ledger = new AssetInstanceLedger();

        var authority = new ControlAuthority(
            _clock,
            assetId => room.UseAssets(
                world => world.TryGet(assetId, out var asset) && asset is not null
                    ? ledger.TokenFor(asset)
                    : null),
            _options);

        room.AddLifecycleObserver(new AuthorityLifecycle(authority));
        return authority;
    }

    /// <summary>The mode this build always runs in.</summary>
    private static ControlModeStatus SimulationOnly { get; } = new(
        Mode: "simulationOnly",
        LiveControlAvailable: false,
        Detail: "Commands are executed by the in-process simulation. This build has no hardware "
            + "bearer, so nothing it accepts can move a physical vehicle.");

    /// <summary>Gives every asset object a token that no later asset can be issued.</summary>
    /// <remarks>
    /// The identity a lease is bound to. Asset <i>ids</i> are chosen by whoever spawns the thing
    /// and are freely recyclable; asset <i>objects</i> are not, and a removed asset's object is
    /// never the one a re-spawn produces. Keying on the object therefore answers "is this still
    /// the vehicle the lease was taken over" in the one way an id cannot.
    /// <para>
    /// A <see cref="ConditionalWeakTable{TKey,TValue}"/> so the ledger cannot become the reason a
    /// removed asset stays in memory: an entry is collected with the asset it describes. The
    /// counter only ever goes up, so a token retired with its asset is never handed out again —
    /// which is the property the whole scheme rests on.
    /// </para>
    /// </remarks>
    private sealed class AssetInstanceLedger
    {
        private readonly ConditionalWeakTable<ISimulatedAsset, string> _tokens = new();
        private long _sequence;

        /// <summary>The token for one asset object, minting it on first sight.</summary>
        /// <param name="asset">Asset to identify.</param>
        /// <returns>The same token for the life of that object.</returns>
        public string TokenFor(ISimulatedAsset asset) => _tokens.GetValue(asset, _ => Mint());

        /// <summary>Issues the next never-before-used token.</summary>
        /// <remarks>
        /// <see cref="ConditionalWeakTable{TKey,TValue}.GetValue"/> may run its factory more than
        /// once under contention and keep one result, so this may burn a number. That is
        /// harmless: what matters is that no number is ever reused, not that none is skipped.
        /// </remarks>
        private string Mint() => string.Create(
            CultureInfo.InvariantCulture, $"instance-{Interlocked.Increment(ref _sequence)}");
    }

    /// <summary>Points a room's lifecycle at the authority governing it.</summary>
    /// <remarks>
    /// A separate adapter rather than making <see cref="ControlAuthority"/> itself an observer:
    /// the authority is about who may command what and knows nothing of rooms, and the three
    /// methods here are only a naming of which of its operations each event means. It satisfies
    /// the observer contract: none of the three throws, and each is called with the room's lock
    /// released, so the probe underneath is free to take that lock itself.
    /// </remarks>
    /// <param name="authority">Authority to drive.</param>
    private sealed class AuthorityLifecycle(ControlAuthority authority) : IRoomLifecycleObserver
    {
        /// <inheritdoc/>
        public void OnAssetRemoved(string assetId) => authority.RevokeForAsset(assetId);

        /// <inheritdoc/>
        public void OnWorldReset() => authority.Reset();

        /// <inheritdoc/>
        public void OnUpkeep() => authority.Sweep();
    }
}
