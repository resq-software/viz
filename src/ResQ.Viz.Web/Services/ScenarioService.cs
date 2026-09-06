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

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Loads and executes named scenario presets from application configuration.
/// </summary>
/// <remarks>
/// A preset is a flat list of entries, each placing one asset. An entry that names no vehicle
/// class is an air multirotor, which is what every preset written before the ground domain
/// existed relies on: those presets parse to exactly the entries they always did and take
/// exactly the spawn path they always took.
/// <para>
/// <b>A malformed entry is skipped, never thrown.</b> That is the behaviour presets have always
/// had, and it is deliberate: a preset is data, it is read at startup, and one bad row must not
/// stop the host serving the presets around it — nor, once a run is under way, abort it
/// half-applied. A partially-spawned world is worse than a missing vehicle, because nothing
/// about it says which half is missing. Every skip is logged with the preset, the row and what
/// was wrong with it, so a typo reads as a typo rather than as a vehicle that mysteriously
/// never appears.
/// </para>
/// <para>
/// Validation is therefore complete rather than cursory: a blank, malformed or repeated
/// identifier, a coordinate that is unparseable, non-finite or outside the scene, a vehicle
/// class this build cannot simulate, and a declared domain that contradicts its own class are
/// all caught at load, while the row can still be named.
/// </para>
/// </remarks>
public sealed partial class ScenarioService
{
    /// <summary>Per-asset scenario entry: what to place, where, and which way round.</summary>
    /// <remarks>
    /// <paramref name="Domain"/> is derived from <paramref name="VehicleClass"/> at load time
    /// rather than trusted from configuration, for the same reason the v2 spawn endpoint derives
    /// it: a preset able to declare a domain contradicting its class would produce an asset that
    /// is filtered as one kind of thing and simulated as another.
    /// </remarks>
    /// <param name="Id">Asset identifier, unique within the preset.</param>
    /// <param name="Pos">Spawn position in the scene frame, in metres. A ground asset's height is read off the terrain, so its <c>Y</c> is ignored.</param>
    /// <param name="Vendor">Optional vendor tag; null when unattributed.</param>
    /// <param name="Domain">Medium the asset operates in. Defaults to <see cref="AssetDomain.Air"/>.</param>
    /// <param name="VehicleClass">Mobility archetype. Defaults to <see cref="VehicleClass.Multirotor"/>.</param>
    /// <param name="HeadingRad">Initial heading in radians clockwise from true north. Ignored by an air spawn, which takes no heading.</param>
    public readonly record struct Entry(
        string Id,
        Vector3 Pos,
        string? Vendor,
        AssetDomain Domain = AssetDomain.Air,
        VehicleClass VehicleClass = VehicleClass.Multirotor,
        double HeadingRad = 0.0);

    /// <summary>The motion models this build ships, bound to no room at all.</summary>
    /// <remarks>
    /// Ground and surface, which is exactly what the composition root registers. This list is
    /// only ever reached by a caller that supplied none — the unit tests, which is why it exists
    /// — but it must still match, because a fallback that lagged the registration would make a
    /// preset spawn one population under the host and a different one under test, and the test
    /// would be the one that passed. An entry naming a class nothing here builds is skipped and
    /// logged, never thrown.
    /// <para>
    /// Room-independent, and it has to be: the sampler a rover settles against, or a vessel
    /// floats on, is resolved from <see cref="SimulationRoom.SpawningEnvironment"/> at the moment
    /// of the build, inside the room's own lock. Capturing a sampler here instead would mean
    /// reading it out of a room before the lock was taken and sampling terrain after it was
    /// released — the race <see cref="SimulationRoom.UseAssets{T}"/> documents and forbids.
    /// </para>
    /// </remarks>
    private static readonly IAssetFactory[] ShippedAssetFactories =
    [
        new GroundAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A ground asset may only be built from inside SimulationRoom.TrySpawnAsset, "
                + "which is what keeps its terrain sampling under the room's lock.")),

        new SurfaceAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A surface asset may only be built from inside SimulationRoom.TrySpawnAsset, "
                + "which is what keeps its bathymetry sampling under the room's lock.")),
    ];

    private readonly IReadOnlyDictionary<string, IReadOnlyList<Entry>> _scenarios;
    private readonly IReadOnlyList<ScenarioSummary> _scenarioSummaries;
    private readonly IReadOnlyList<IAssetFactory> _assetFactories;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises the service and loads scenario presets from <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">Application configuration containing the <c>Scenarios</c> section.</param>
    /// <param name="assetFactories">
    /// Motion models a preset may spawn, or null for the ones this build ships. Injected as a
    /// list rather than resolved per room because a factory holds no room: the environment it
    /// settles an asset against is supplied by the room during the build itself.
    /// </param>
    /// <param name="logger">Where skipped rows are reported, or null to discard them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    public ScenarioService(
        IConfiguration configuration,
        IReadOnlyList<IAssetFactory>? assetFactories = null,
        ILogger<ScenarioService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _assetFactories = assetFactories ?? ShippedAssetFactories;
        _logger = logger ?? NullLogger<ScenarioService>.Instance;

        var dict = new Dictionary<string, IReadOnlyList<Entry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in configuration.GetSection("Scenarios").GetChildren())
        {
            var entries = new List<Entry>();

            // Ordinal, because that is how the asset registry compares identifiers: two ids
            // differing only in case are two assets there, and pretending otherwise here would
            // skip a row the world would have accepted.
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in preset.GetChildren())
            {
                if (!TryReadEntry(row, out var parsed, out var problem))
                {
                    _logger.LogWarning(
                        "Scenario '{Scenario}' entry [{Row}] skipped: {Problem}.",
                        LogSafe(preset.Key), LogSafe(row.Key), problem);
                    continue;
                }

                if (!claimed.Add(parsed.Id))
                {
                    _logger.LogWarning(
                        "Scenario '{Scenario}' entry [{Row}] skipped: id '{AssetId}' is already "
                        + "used earlier in the same preset.",
                        LogSafe(preset.Key), LogSafe(row.Key), LogSafe(parsed.Id));
                    continue;
                }

                entries.Add(parsed);
            }

            if (entries.Count > 0)
            {
                dict[preset.Key] = entries;
            }
        }

        _scenarios = dict;
        _scenarioSummaries = dict
            .Select(pair => BuildSummary(pair.Key, pair.Value))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Names of all available scenario presets.</summary>
    public IEnumerable<string> ScenarioNames => _scenarios.Keys;

    /// <summary>Immutable discovery summaries derived from the validated scenario entries.</summary>
    public IReadOnlyList<ScenarioSummary> ScenarioSummaries => _scenarioSummaries;

    /// <summary>Motion models this loader may spawn a non-air entry through.</summary>
    /// <remarks>
    /// Exposed so the composition root's choice is inspectable rather than inferred. The
    /// constructor accepts a null list and falls back to the models this build ships, which is
    /// what keeps the unit tests independent of a container — but a host taking that fallback
    /// while registering its own factories would leave a preset and the v2 spawn endpoint
    /// disagreeing about which classes exist, and nothing but a skipped-row log line would say so.
    /// </remarks>
    public IReadOnlyList<IAssetFactory> AssetFactories => _assetFactories;

    /// <summary>Returns true if the named scenario exists.</summary>
    public bool HasScenario(string name) => _scenarios.ContainsKey(name);

    /// <summary>Resolves a case-insensitive request to the configured scenario key.</summary>
    /// <param name="name">Requested scenario name.</param>
    /// <param name="canonicalName">
    /// Configured key when found; otherwise an empty string. Callers publish this value so every
    /// client sees one stable name regardless of route casing.
    /// </param>
    /// <returns><see langword="true"/> when the requested scenario exists.</returns>
    public bool TryResolveScenarioName(string name, out string canonicalName)
    {
        canonicalName = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var configuredName in _scenarios.Keys)
        {
            if (string.Equals(configuredName, name, StringComparison.OrdinalIgnoreCase))
            {
                canonicalName = configuredName;
                return true;
            }
        }

        return false;
    }

    /// <summary>Stages and atomically replaces a room with one validated scenario.</summary>
    /// <param name="name">Canonical configured scenario name.</param>
    /// <param name="room">Room whose population and scenario state are replaced.</param>
    /// <param name="committed">Exact scenario state committed with the new population.</param>
    /// <returns>
    /// <see langword="true"/> when the scenario existed and its complete population was committed;
    /// otherwise <see langword="false"/> with the previous room left unchanged.
    /// </returns>
    public bool TryReplace(
        string name,
        SimulationRoom room,
        [NotNullWhen(true)] out ScenarioSessionState? committed) =>
        TryReplace(name, room, out committed, out _);

    /// <summary>Atomically replaces a room population and reports a bounded failure category.</summary>
    /// <param name="name">Canonical configured scenario name.</param>
    /// <param name="room">Room whose population and scenario state are replaced.</param>
    /// <param name="committed">Exact scenario state committed with the new population.</param>
    /// <param name="failure">Stable failure category plus the internal exception, on failure.</param>
    /// <returns>
    /// <see langword="true"/> when the scenario existed and its complete population was committed;
    /// otherwise <see langword="false"/> with the previous room left unchanged.
    /// </returns>
    internal bool TryReplace(
        string name,
        SimulationRoom room,
        [NotNullWhen(true)] out ScenarioSessionState? committed,
        [NotNullWhen(false)] out ScenarioReplacementFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (!_scenarios.TryGetValue(name, out var entries))
        {
            committed = null;
            failure = new ScenarioReplacementFailure(
                "catalog.resolve", new InvalidOperationException("Scenario was not present in the catalog."));
            return false;
        }

        return room.TryReplaceScenario(
            name,
            candidate =>
            {
                foreach (var entry in entries)
                {
                    if (entry.Domain == AssetDomain.Air)
                    {
                        candidate.AddDrone(entry.Id, entry.Pos, entry.Vendor);
                    }
                    else
                    {
                        StageNonAir(candidate, entry);
                    }
                }
            },
            out committed,
            out failure);
    }

    /// <summary>Builds one immutable discovery summary from validated entries.</summary>
    /// <param name="name">Canonical configured scenario name.</param>
    /// <param name="entries">Validated entries in configured order.</param>
    /// <returns>The scenario summary.</returns>
    private static ScenarioSummary BuildSummary(string name, IReadOnlyList<Entry> entries)
    {
        var vehicleClassCounts = new ReadOnlyDictionary<string, int>(
            entries
                .GroupBy(entry => entry.VehicleClass)
                .ToDictionary(group => group.Key.ToString(), group => group.Count(), StringComparer.Ordinal));

        return new ScenarioSummary(
            Name: name,
            AssetCount: entries.Count,
            DomainCounts: new ScenarioDomainCounts(
                Air: entries.Count(entry => entry.Domain == AssetDomain.Air),
                Ground: entries.Count(entry => entry.Domain == AssetDomain.Ground),
                Surface: entries.Count(entry => entry.Domain == AssetDomain.Surface)),
            VehicleClassCounts: vehicleClassCounts);
    }

    /// <summary>
    /// Runs a named scenario by spawning its assets into the simulation room.
    /// Returns <see langword="false"/> if the scenario name is not found.
    /// </summary>
    /// <remarks>
    /// Air entries go through <see cref="SimulationRoom.AddDrone(string, Vector3, string)"/>, the
    /// same call this method has always made, so an all-air preset produces the world it always
    /// did. Everything else is built by a registered motion model, under the room's lock, and
    /// registered as an asset; a class this build ships no model for is skipped, which leaves the
    /// rest of the preset intact rather than failing a whole run over one vehicle nobody can
    /// simulate yet.
    /// <para>
    /// The per-entry guard is a backstop behind the load-time validation, not a substitute for
    /// it. A preset run into a room that already holds one of its identifiers — two presets
    /// applied in sequence — is refused by the world with an exception this method's caller
    /// cannot act on, and swallowing exactly that so the remaining entries still spawn is the
    /// difference between a scenario missing one vehicle and an endpoint returning 500 over a
    /// world it has already half-built.
    /// </para>
    /// </remarks>
    /// <param name="name">Scenario name.</param>
    /// <param name="room">The simulation room to spawn into.</param>
    /// <returns><see langword="true"/> if the scenario was found and started; <see langword="false"/> otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="room"/> is null.</exception>
    public bool TryRun(string name, SimulationRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);

        if (!_scenarios.TryGetValue(name, out var entries))
        {
            return false;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (entry.Domain == AssetDomain.Air)
                {
                    room.AddDrone(entry.Id, entry.Pos, entry.Vendor);
                }
                else
                {
                    SpawnNonAir(room, entry);
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Scenario '{Scenario}': asset '{AssetId}' was refused by the world and "
                    + "skipped; the rest of the preset still ran.",
                    LogSafe(name), LogSafe(entry.Id));
            }
        }

        return true;
    }

    /// <summary>Builds and registers one ground or surface asset, or logs why it did not.</summary>
    /// <remarks>
    /// The build runs inside <see cref="SimulationRoom.TrySpawnAsset"/> so that the terrain
    /// samples a rover takes while settling happen under the room's lock — the same reason the v2
    /// spawn endpoint routes through it. A duplicate identifier comes back as a reason code
    /// rather than an exception, because a preset that repeats an id should produce one asset and
    /// a visibly short scenario, not a 500 from whichever endpoint ran it.
    /// </remarks>
    /// <param name="room">Room to build and register the asset in.</param>
    /// <param name="entry">Parsed scenario entry.</param>
    private void SpawnNonAir(SimulationRoom room, in Entry entry)
    {
        // Copied out of the `in` parameter first: a by-reference parameter cannot be captured by
        // a closure, and both the predicate and the build delegate below need these.
        var vehicleClass = entry.VehicleClass;
        var assetId = entry.Id;

        var factory = _assetFactories.FirstOrDefault(f => f.CanCreate(vehicleClass));
        if (factory is null)
        {
            _logger.LogWarning(
                "Asset '{AssetId}' skipped: this build registers no motion model for vehicle "
                + "class '{VehicleClass}'.",
                LogSafe(assetId), vehicleClass);
            return;
        }

        var plan = new AssetSpawnPlan(
            assetId,
            vehicleClass,
            AssetProfiles.Create(assetId, vehicleClass, vendor: entry.Vendor),
            entry.Pos,
            entry.HeadingRad);

        if (!room.TrySpawnAsset(assetId, _ => factory.Create(plan), out var reasonCode))
        {
            _logger.LogWarning(
                "Asset '{AssetId}' skipped: the room refused it ({ReasonCode}).",
                LogSafe(assetId), reasonCode);
        }
    }

    /// <summary>Builds one non-air entry into an unpublished candidate world.</summary>
    private void StageNonAir(ScenarioPopulationBuilder candidate, in Entry entry)
    {
        var vehicleClass = entry.VehicleClass;
        var assetId = entry.Id;
        var factory = _assetFactories.FirstOrDefault(f => f.CanCreate(vehicleClass))
            ?? throw new InvalidOperationException(
                $"No motion model is registered for vehicle class '{vehicleClass}'.");
        var plan = new AssetSpawnPlan(
            assetId,
            vehicleClass,
            AssetProfiles.Create(assetId, vehicleClass, vendor: entry.Vendor),
            entry.Pos,
            entry.HeadingRad);

        candidate.AddAsset(assetId, _ => factory.Create(plan));
    }
}
