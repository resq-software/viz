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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

// Spawning a non-air asset: building it and registering it inside one acquisition of the room's
// lock, and publishing the environment sampler that the build reads.
//
// Split from SimulationRoom.Assets.cs, which owns everything else the v2 surface needs from a
// room, because this is a different concern with a different hazard. The rest of that file hands
// values out of the lock; this hands a callback INTO it, and the reason is that constructing a
// ground or surface asset is not bookkeeping — it samples terrain, the terrain normal and the
// water surface while it settles the vehicle. Those samples have to happen where every other
// world read happens, which means the factory runs with the lock held rather than before it.
public sealed partial class SimulationRoom
{
    /// <summary>The sampler of the room whose spawn is running on the calling thread.</summary>
    /// <remarks>
    /// The bridge for <see cref="IAssetFactory"/>, which resolves its own
    /// <see cref="IEnvironmentSampler"/> and is registered in the composition root long before
    /// any room exists. A factory built for the container reads this instead of reaching back
    /// through a request for a room and asking it for its sampler — which returned a live view
    /// out of <see cref="UseAssets{T}"/> and then sampled terrain outside the lock, exactly what
    /// that method's contract forbids.
    /// <para>
    /// Thread-static rather than async-local on purpose: it is set and cleared inside a single
    /// <c>lock</c> block in <see cref="TrySpawnAsset"/>, with no await in between, so its whole
    /// lifetime is one thread holding one lock. Null everywhere else, so a factory invoked
    /// outside a spawn fails loudly instead of settling a vehicle against nothing.
    /// </para>
    /// </remarks>
    public static IEnvironmentSampler? SpawningEnvironment => _spawningEnvironment;

    [ThreadStatic]
    private static IEnvironmentSampler? _spawningEnvironment;

    /// <summary>Builds a ground or surface asset and registers it, both under the room lock.</summary>
    /// <remarks>
    /// The only correct way to spawn a non-air asset. Construction is not a bookkeeping step: a
    /// rover settles onto the terrain in its own constructor, so building one reads the height
    /// field, the terrain normal and the water surface. Doing that outside the lock races the
    /// 60 Hz tick loop and, worse, races a heightmap upload — <see cref="SetHeightmap"/> replaces
    /// the terrain the sampler reads — so a rover could settle against a terrain that no longer
    /// exists by the time it is registered.
    /// <para>
    /// Registration is inside the same acquisition as the build, which is what makes the
    /// identifier check meaningful: a concurrent spawn cannot slip between "is this id free?" and
    /// "take it".
    /// </para>
    /// <para>
    /// <paramref name="build"/> runs with the tick loop's lock held on the caller's thread. It
    /// must not call back into this room, block, or advance the world. It is handed the sampler
    /// directly, and the same sampler is published on <see cref="SpawningEnvironment"/> for a
    /// factory that resolves its own.
    /// </para>
    /// </remarks>
    /// <param name="assetId">Identifier the asset must be built with.</param>
    /// <param name="build">Builds the asset from the room's environment sampler.</param>
    /// <param name="reasonCode">Stable code from <see cref="AssetProblems"/> when the spawn was refused.</param>
    /// <returns><see langword="true"/> when the asset was built and registered.</returns>
    /// <exception cref="ArgumentException"><paramref name="assetId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="build"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="build"/> produced nothing, or produced an asset under a different
    /// identifier. Either is a programming error in the factory rather than a caller-fixable
    /// rejection: the id was already reserved against the registry under this lock, so an asset
    /// carrying a different one would be registered under a name nobody checked.
    /// </exception>
    public bool TrySpawnAsset(
        string assetId,
        Func<IEnvironmentSampler, ISimulatedAsset> build,
        [NotNullWhen(false)] out string? reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentNullException.ThrowIfNull(build);

        ISimulatedAsset asset;

        lock (_lock)
        {
            if (_assets.TryGet(assetId, out _))
            {
                reasonCode = AssetProblems.AssetIdTaken;
                return false;
            }

            // Restored rather than nulled, so a factory that itself spawns — a launcher placing
            // its payload, one day — cannot blank the outer spawn's sampler on the way out.
            var previous = _spawningEnvironment;
            _spawningEnvironment = _assets.Environment;
            try
            {
                asset = build(_assets.Environment);
            }
            finally
            {
                _spawningEnvironment = previous;
            }

            if (asset is null)
            {
                throw new InvalidOperationException(
                    $"The factory for '{assetId}' returned no asset.");
            }

            if (!string.Equals(asset.AssetId, assetId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The factory was asked for '{assetId}' but built '{asset.AssetId}'.");
            }

            _assets.AddAsset(asset);
        }

        Touch();
        _logger.LogInformation(
            "[room {RoomId}] Asset {AssetId} added: domain={Domain}, class={VehicleClass}.",
            Id, LogSafe(asset.AssetId), asset.Domain, asset.Descriptor.VehicleClass);
        reasonCode = null;
        return true;
    }
}
