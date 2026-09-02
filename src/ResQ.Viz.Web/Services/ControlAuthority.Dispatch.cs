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

namespace ResQ.Viz.Web.Services;

/// <summary>Final authority decision paired with the room dispatch outcome.</summary>
internal readonly record struct AuthorizedCommandDispatch(
    string? ReasonCode,
    RoomCommandDispatchResult RoomResult)
{
    internal bool IsAuthorized => ReasonCode is null;
}

public sealed partial class ControlAuthority
{
    /// <summary>Revalidates authority and invokes final room dispatch under authority-to-room locks.</summary>
    internal AuthorizedCommandDispatch DispatchCommand(
        string assetId,
        string issuerId,
        string? leaseId,
        Func<RoomCommandDispatchResult> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Maintain(now);
            var currentInstance = ResolveInstance(assetId);

            if (_live.TryGetValue(assetId, out var lease))
            {
                if (!lease.IsHeldBy(issuerId, now)
                    || (leaseId is not null
                        && !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)))
                {
                    return new AuthorizedCommandDispatch(
                        WasPreemptedCore(assetId, issuerId, leaseId, currentInstance)
                            ? CommandAuthorityReasons.LeasePreempted
                            : string.Equals(lease.HolderId, issuerId, StringComparison.Ordinal)
                                ? CommandAuthorityReasons.LeaseNotLive
                                : CommandAuthorityReasons.NotHolder,
                        default);
                }
            }
            else if (leaseId is not null)
            {
                return new AuthorizedCommandDispatch(
                    WasPreemptedCore(assetId, issuerId, leaseId, currentInstance)
                        ? CommandAuthorityReasons.LeasePreempted
                        : CommandAuthorityReasons.LeaseNotLive,
                    default);
            }

            var roomResult = dispatch();
            return roomResult.IsCurrent
                ? new AuthorizedCommandDispatch(null, roomResult)
                : new AuthorizedCommandDispatch(CommandAuthorityReasons.AssetInstanceChanged, roomResult);
        }
    }

    /// <summary>Whether the caller's most recent matching lease transition was a preemption.</summary>
    internal bool WasPreempted(string assetId, string issuerId, string? leaseId)
    {
        lock (_gate)
        {
            return WasPreemptedCore(assetId, issuerId, leaseId, ResolveInstance(assetId));
        }
    }

    /// <summary>Lock-held implementation shared by command validation and final dispatch.</summary>
    private bool WasPreemptedCore(
        string assetId, string issuerId, string? leaseId, string? currentInstance)
    {
        if (currentInstance is null)
        {
            return false;
        }

        ControlAuditRecord? last = null;
        foreach (var record in _audit)
        {
            if (string.Equals(record.AssetId, assetId, StringComparison.Ordinal)
                && string.Equals(record.AssetInstanceId, currentInstance, StringComparison.Ordinal)
                && (string.Equals(record.HolderId, issuerId, StringComparison.Ordinal)
                    || (leaseId is not null
                        && string.Equals(record.LeaseId, leaseId, StringComparison.Ordinal))))
            {
                last = record;
            }
        }

        return last?.Kind == ControlAuditKind.Preempted;
    }
}
