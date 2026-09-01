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

using System.Collections.ObjectModel;
using System.Numerics;
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Entities;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>
/// The simulated population of one session: drones the SDK world owns, plus the ground and
/// surface assets we own, advanced by one deterministic step.
/// </summary>
/// <remarks>
/// <b>Threading.</b> This type performs no synchronisation. Every member is called under the
/// owning room's single lock, which is why there is no lock, no concurrent collection and no
/// <c>Interlocked</c> here. That guarantee is upheld by never handing out a live collection:
/// <see cref="Assets"/>, <see cref="Descriptors"/>, <see cref="States"/> and
/// <see cref="DrainEvents"/> each return a materialised copy built inside the call, so a caller
/// cannot enumerate world state after releasing the lock. Returning a lazy query from any of
/// them would quietly reintroduce that race with no compile error.
/// <para>
/// <b>Composition, not replacement.</b> The SDK's <see cref="SimulationWorld"/> is held
/// privately and remains the sole authority on air physics. Nothing here forks it, reimplements
/// it, or reaches into its random stream, so drone trajectories stay bit-for-bit what they were
/// before ground and surface assets existed.
/// </para>
/// <para>
/// <b>Reset.</b> There is no reset method. A room resets by constructing a fresh world, which
/// drops the registry, the counters and the SDK world together and so cannot leave a stale
/// asset behind.
/// </para>
/// </remarks>
public sealed partial class AssetWorld
{
    /// <summary>Divisor turning the world step count into simulation seconds.</summary>
    /// <remarks>
    /// Simulation time is derived from an integer step count rather than accumulated a timestep
    /// at a time, because repeated floating-point addition drifts over hours of running while an
    /// integer-counted division does not. Deliberately a literal rather than
    /// <c>1 / Clock.DeltaTime</c>: the two agree today, and if they ever stopped agreeing the
    /// difference would surface as waypoint timeouts firing a fraction early.
    /// </remarks>
    private const double SimulationTicksPerSecond = 60.0;

    /// <summary>Salt mixed into the configured seed to give assets their own random stream.</summary>
    /// <remarks>
    /// Isolation, not secrecy. If ground and surface assets drew from the SDK world's generator,
    /// spawning a rover would shift every subsequent draw and therefore every drone's gust — a
    /// change with no visible cause. Any fixed non-zero constant works; this one is arbitrary.
    /// </remarks>
    private const int AssetSeedSalt = 0x5EED_A55E;

    private static readonly AssetEvent[] NoEvents = [];

    private readonly AssetWorldOptions _options;
    private readonly TimeProvider _time;
    private readonly EnvironmentSampler _environment;

    // Registry. _ordered is spawn order across every domain and is what publishing walks.
    // _byId is for lookup only and is never enumerated: dictionary iteration order is not a
    // contract, and a step whose result depended on it would not be reproducible.
    private readonly List<ISimulatedAsset> _ordered = [];
    private readonly Dictionary<string, ISimulatedAsset> _byId = new(StringComparer.Ordinal);

    // Pre-partitioned by domain rather than filtered out of _ordered on every step. Same
    // resulting order, no per-element branch, and it makes step order provably a function of
    // (domain, spawn index) rather than of how spawns happened to be interleaved.
    private readonly List<AirAsset> _air = [];
    private readonly List<IStepDrivenAsset> _ground = [];
    private readonly List<IStepDrivenAsset> _surface = [];

    private readonly List<PeerPose> _peerPoses = [];
    private readonly ReadOnlyCollection<PeerPose> _peerPoseView;
    private readonly Random _random;
    private readonly SimulationWorld _flight;

    /// <summary>Creates a world over a terrain and a weather system.</summary>
    /// <param name="terrain">Terrain shared with the room and the SDK world.</param>
    /// <param name="weather">Weather system shared with the SDK world. Only the SDK ever steps it.</param>
    /// <param name="options">Optional settings; the defaults are deterministic.</param>
    /// <exception cref="ArgumentNullException"><paramref name="terrain"/> or <paramref name="weather"/> is null.</exception>
    public AssetWorld(ITerrain terrain, IWeatherSystem weather, AssetWorldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(weather);

        _options = options ?? new AssetWorldOptions();
        var config = _options.Simulation ?? new SimulationConfig();
        _time = _options.WallClock ?? TimeProvider.System;
        _flight = new SimulationWorld(config, terrain, weather);
        _environment = new EnvironmentSampler(
            terrain, new WeatherWindField(weather), _options.SeaLevelM, _options.Zones);
        _random = new Random(config.Seed ^ AssetSeedSalt);
        _peerPoseView = new ReadOnlyCollection<PeerPose>(_peerPoses);
        WorldEpochUtc = _options.WorldEpochUtc ?? DateTimeOffset.UnixEpoch;
    }

    /// <summary>Wall-clock instant that simulation time zero corresponds to.</summary>
    public DateTimeOffset WorldEpochUtc { get; }

    /// <summary>Total world steps advanced since this world was constructed.</summary>
    /// <remarks>
    /// <c>long</c> rather than <c>int</c>: at eight times speed this advances 480 a second and
    /// would overflow an <c>int</c> in about fifty days, turning simulation time negative.
    /// </remarks>
    public long TickCount { get; private set; }

    /// <summary>Current simulation time in seconds.</summary>
    public double SimulationTimeSeconds { get; private set; }

    /// <summary>The SDK clock, so a caller can read the effective timestep it will be stepped with.</summary>
    public SimulationClock Clock => _flight.Clock;

    /// <summary>The SDK's ordered drone list.</summary>
    /// <remarks>
    /// The same reference the swarm coordinator and the v1 snapshot projection have always been
    /// handed. The air population lives here and only here — mirroring it on our side is
    /// precisely how two populations would drift apart.
    /// </remarks>
    public IReadOnlyList<SimulatedDrone> Drones => _flight.Drones;

    /// <summary>Environment sampler shared by every asset in this world.</summary>
    public IEnvironmentSampler Environment => _environment;

    /// <summary>Number of assets across every domain.</summary>
    public int AssetCount => _ordered.Count;

    /// <summary>Number of air assets. Always matches <see cref="Drones"/>.</summary>
    public int DroneCount => _air.Count;

    /// <summary>Every asset, in spawn order.</summary>
    /// <remarks>
    /// A materialised copy: the list is safe to hold, but the assets in it are live and must
    /// only be touched under the room lock.
    /// </remarks>
    public IReadOnlyList<ISimulatedAsset> Assets => _ordered.ToArray();

    /// <summary>Every asset's descriptor, in spawn order.</summary>
    public IReadOnlyList<AssetDescriptor> Descriptors
    {
        get
        {
            var result = new AssetDescriptor[_ordered.Count];
            for (var i = 0; i < _ordered.Count; i++)
            {
                result[i] = _ordered[i].Descriptor;
            }

            return result;
        }
    }

    /// <summary>Captures every asset's current state, in spawn order.</summary>
    /// <remarks>
    /// The only place a wall clock is read. Capturing advances nothing, so reading this twice
    /// within one tick yields the same states and raises no duplicate events.
    /// </remarks>
    public IReadOnlyList<AssetState> States
    {
        get
        {
            var context = CreateCaptureContext();
            var result = new AssetState[_ordered.Count];
            for (var i = 0; i < _ordered.Count; i++)
            {
                result[i] = _ordered[i].Capture(in context);
            }

            return result;
        }
    }

    /// <summary>Moves the water surface to match a terrain preset.</summary>
    /// <remarks>
    /// Call this from the same place, and under the same lock, as the terrain preset switch.
    /// The server's water mask and the client's water plane must agree, or a vessel floats where
    /// the client draws grass.
    /// </remarks>
    /// <param name="presetKey">Terrain preset key. Case-insensitive; an unknown key falls back to the default.</param>
    public void SetSeaLevelForPreset(string? presetKey) =>
        _environment.SetSeaLevel(SeaLevel.ForPreset(presetKey));

    /// <summary>Moves the water surface to an explicit elevation, for a scenario override.</summary>
    /// <param name="seaLevelM">Water-surface elevation in metres.</param>
    /// <exception cref="ArgumentException"><paramref name="seaLevelM"/> is not finite.</exception>
    public void SetSeaLevel(double seaLevelM) => _environment.SetSeaLevel(seaLevelM);

    /// <summary>Adds a multirotor at a start position and registers it as an air asset.</summary>
    /// <remarks>
    /// The SDK world is asked first, so a duplicate drone id still throws the SDK's own
    /// exception with its own message and parameter name, exactly as before. The only check that
    /// runs ahead of it is for a collision with a <em>non-air</em> asset — a case that cannot
    /// reach the SDK at all, and catching it first avoids adding a drone we would then have to
    /// roll back, which the SDK offers no way to do.
    /// </remarks>
    /// <param name="id">Identifier, unique across every domain.</param>
    /// <param name="position">Scene-frame launch position, in metres.</param>
    /// <param name="vendor">Optional vendor tag. Empty is normalised to no vendor, as in v1.</param>
    /// <returns>The registered air asset.</returns>
    /// <exception cref="ArgumentException">The id is null, whitespace, or already taken.</exception>
    public AirAsset AddDrone(string id, Vector3 position, string? vendor = null)
    {
        if (_byId.TryGetValue(id, out var existing) && existing.Domain != AssetDomain.Air)
        {
            throw new ArgumentException($"An asset with id '{id}' already exists.", nameof(id));
        }

        var drone = _flight.AddDrone(id, position);
        var asset = new AirAsset(
            drone, AssetProfiles.Create(id, VehicleClass.Multirotor, vendor: vendor));

        Register(asset);
        return asset;
    }

    /// <summary>Registers a ground, surface or fixed asset built elsewhere.</summary>
    /// <remarks>
    /// Air assets are refused: their lifetime belongs to the SDK world, and registering one here
    /// without also adding it there would leave an asset that publishes state but is never
    /// integrated. Use <see cref="AddDrone"/>.
    /// </remarks>
    /// <param name="asset">Asset to register. Ground and surface assets should implement <see cref="IStepDrivenAsset"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> is null.</exception>
    /// <exception cref="ArgumentException">The asset is an air asset, or its id is already taken.</exception>
    public void AddAsset(ISimulatedAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Domain == AssetDomain.Air)
        {
            throw new ArgumentException(
                "Air assets are owned by the SDK world; add them with AddDrone.", nameof(asset));
        }

        if (_byId.ContainsKey(asset.AssetId))
        {
            throw new ArgumentException(
                $"An asset with id '{asset.AssetId}' already exists.", nameof(asset));
        }

        Register(asset);
    }

    /// <summary>Removes a ground, surface or fixed asset.</summary>
    /// <remarks>
    /// Air assets cannot be removed, and this returns <see langword="false"/> for them. The
    /// SDK's world exposes no removal, and dropping our view while the drone kept flying would
    /// leave an asset that is simulated but invisible. Reset the world instead.
    /// </remarks>
    /// <param name="assetId">Identifier of the asset to remove.</param>
    /// <returns><see langword="true"/> when an asset was removed.</returns>
    public bool RemoveAsset(string assetId)
    {
        if (!_byId.TryGetValue(assetId, out var asset) || asset.Domain == AssetDomain.Air)
        {
            return false;
        }

        _byId.Remove(assetId);
        _ordered.Remove(asset);

        if (asset is IStepDrivenAsset stepDriven)
        {
            _ground.Remove(stepDriven);
            _surface.Remove(stepDriven);
        }

        // The safe-action layer keeps per-asset state of its own — a held-down link, a contact
        // time, a latch saying the fallback has already been issued — and it is the one
        // collection removal used not to touch. Its sweep prunes against the registry, but only
        // once a second, so a removal and a respawn under the same id inside that second handed
        // the new asset the old one's outage.
        _safeActions.Forget(assetId);

        return true;
    }

    /// <summary>Looks up an asset by id.</summary>
    /// <param name="assetId">Identifier to resolve.</param>
    /// <param name="asset">The asset on success, otherwise null.</param>
    /// <returns><see langword="true"/> when the asset exists.</returns>
    public bool TryGet(string assetId, out ISimulatedAsset? asset) =>
        _byId.TryGetValue(assetId, out asset);

    /// <summary>Routes a validated command to its asset, once the safe-action layer allows it.</summary>
    /// <remarks>
    /// Applied immediately rather than queued. A queue would shift every command by one step and
    /// change the trajectory a replayed command log produces, for no benefit: the caller already
    /// holds the room lock, so there is no concurrency left to serialise.
    /// <para>
    /// <b>The v2 gate applied here.</b> A v2 command the catalog marks as needing a current
    /// position is refused while the position on file is stale or too uncertain to navigate from
    /// — see <see cref="AuthorizeCommand"/> for why only that half of the decision is enforced
    /// and the rest is left to the layers that already own it. Commands that need no position,
    /// including <c>stop</c>, remain reachable. The v1 drone endpoint does not enter this method:
    /// after validating its legacy payload it sends an SDK <c>FlightCommand</c> directly through
    /// <see cref="SimulationRoom.SendCommand"/> and therefore does not apply this gate.
    /// </para>
    /// </remarks>
    /// <param name="command">Validated, translated command.</param>
    /// <returns>Acceptance, or a rejection carrying a machine-readable reason.</returns>
    public AssetCommandResult SendCommand(in SimulatedAssetCommand command)
    {
        if (!_byId.TryGetValue(command.AssetId, out var asset))
        {
            return AssetCommandResult.Rejected("asset.notFound");
        }

        var decision = AuthorizeCommand(command.AssetId, command.Kind);

        return !decision.IsAllowed && SafeActionPolicy.IsPositionRefusal(decision.ReasonCode)
            ? AssetCommandResult.Rejected(decision.ReasonCode)
            : asset.Apply(in command);
    }

    /// <summary>Removes and returns every event raised by every asset since the last drain.</summary>
    /// <returns>Events grouped by asset in spawn order, empty when nothing happened.</returns>
    public IReadOnlyList<AssetEvent> DrainEvents()
    {
        List<AssetEvent>? drained = null;

        foreach (var asset in _ordered)
        {
            var events = asset.DrainEvents();
            if (events.Count == 0)
            {
                continue;
            }

            drained ??= [];
            drained.AddRange(events);
        }

        return drained is null ? NoEvents : drained;
    }
    private void Register(ISimulatedAsset asset)
    {
        _byId.Add(asset.AssetId, asset);
        _ordered.Add(asset);

        switch (asset)
        {
            case AirAsset air:
                _air.Add(air);
                break;

            case IStepDrivenAsset stepDriven when asset.Domain == AssetDomain.Ground:
                _ground.Add(stepDriven);
                break;

            case IStepDrivenAsset stepDriven when asset.Domain == AssetDomain.Surface:
                _surface.Add(stepDriven);
                break;

            default:
                // A fixed asset — a mast or a ground station — is registered and published but
                // never stepped, because it does not move.
                break;
        }
    }

    private AssetCaptureContext CreateCaptureContext() =>
        new(
            Environment: _environment,
            SimulationTimeSeconds: SimulationTimeSeconds,
            Tick: TickCount,
            SourceTime: WorldEpochUtc + TimeSpan.FromSeconds(SimulationTimeSeconds),
            ReceiveTime: _time.GetUtcNow(),
            Origin: _options.Origin,

            // Every capture stamps LinkState from the current link ledger. The safe-action
            // assessment is cached at the last sweep, so a restored link can be published as up
            // while that assessment still describes the preceding silence. V2 authorisation
            // intentionally uses the cached assessment until the next sweep makes them converge.
            Link: _safeActions);
}
