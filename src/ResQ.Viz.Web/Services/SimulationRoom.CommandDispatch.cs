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

using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

/// <summary>Opaque identity plus state captured for one command-validation pass.</summary>
internal sealed record CommandDispatchCandidate(
    string AssetId,
    AssetDescriptor Descriptor,
    AssetState State,
    AssetWorld World,
    ISimulatedAsset Asset);

/// <summary>Result of the final room-locked identity check and dispatch.</summary>
internal readonly record struct RoomCommandDispatchResult(
    bool IsCurrent,
    AssetCommandResult Outcome,
    CommandIdempotencyDecision? ClaimDecision);

public sealed partial class SimulationRoom
{
    /// <summary>Captures state and opaque identity from one room-locked asset resolution.</summary>
    internal CommandDispatchCandidate? CaptureCommandCandidate(string assetId)
    {
        lock (_lock)
        {
            if (!_assets.TryGet(assetId, out var asset) || asset is null)
            {
                return null;
            }

            var state = _assets.States.First(current => current.AssetId == assetId);
            return new CommandDispatchCandidate(
                assetId, asset.Descriptor, state, _assets, asset);
        }
    }

    /// <summary>Dispatches only if the candidate still names this room's current asset instance.</summary>
    internal RoomCommandDispatchResult DispatchCommand(
        CommandDispatchCandidate candidate,
        AssetCommandLogSession logSession,
        AssetCommandEnvelope envelope,
        DateTimeOffset now,
        in SimulatedAssetCommand command)
    {
        AssetCommandResult outcome;
        CommandIdempotencyDecision claim;
        lock (_lock)
        {
            if (!ReferenceEquals(_assets, candidate.World)
                || !_assets.TryGet(candidate.AssetId, out var current)
                || !ReferenceEquals(current, candidate.Asset)
                || !logSession.IsCurrent)
            {
                return new RoomCommandDispatchResult(false, default, null);
            }

            claim = logSession.Claim(envelope, now);
            if (claim.Outcome != CommandIdempotencyOutcome.New)
            {
                return new RoomCommandDispatchResult(true, default, claim);
            }

            outcome = SendAssetCommandCore(in command);
        }

        Touch();
        return new RoomCommandDispatchResult(true, outcome, claim);
    }
}
