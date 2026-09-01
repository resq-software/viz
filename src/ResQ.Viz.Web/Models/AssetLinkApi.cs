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

namespace ResQ.Viz.Web.Models;

/// <summary>Asks for one asset's command link to be held down, or brought back up.</summary>
/// <remarks>
/// <paramref name="Available"/> is nullable on purpose. A missing field must not bind to
/// <see langword="false"/> and quietly cut a link nobody asked to cut, so an absent value is
/// refused as a malformed request rather than taken as the more destructive of the two.
/// <para>
/// <b>There is deliberately no role and no lease id here.</b> The route is not lease-gated — see
/// <c>SimV2Controller.Link.cs</c> for why taking a link away cannot be gated behind the very
/// authority it interrupts — and a field the server accepts but never checks is a claim it cannot
/// stand behind. The lease actually in force is read off the session's control authority when the
/// change is recorded, so the record names the holder whose asset went quiet whether or not the
/// caller mentioned one.
/// </para>
/// </remarks>
/// <param name="Available">False to hold the link down, true to restore it. Required.</param>
/// <param name="IssuerId">
/// Operator, station or service asking for the change, recorded as the actor on the decision
/// trail. Optional on the wire for the same reason it is optional on a command: this deployment
/// has no identity provider, so an absent value falls back to the session's own identity rather
/// than to an invented user name.
/// </param>
/// <param name="Reason">
/// Free-text justification, retained on the decision trail beside the machine-readable code.
/// Prose for a human reading the trail afterwards; never parsed.
/// </param>
public sealed record AssetLinkRequest(bool? Available, string? IssuerId = null, string? Reason = null);

/// <summary>The state of one asset's command link after a change, or as it stands.</summary>
/// <remarks>
/// <paramref name="Changed"/> lets a caller tell "I cut it" from "it was already cut" without a
/// second read, which is what makes a retry after a lost response harmless.
/// <para>
/// Nothing about the consequence is reported here, deliberately. What the asset does about the
/// silence is published in its own state on the frame stream — the operational state it moves to,
/// the speed it holds and the uncertainty growth it declares — and that is the record an operator
/// should be reading, not an acknowledgement from the endpoint that took the link away.
/// </para>
/// </remarks>
/// <param name="AssetId">Asset the link belongs to.</param>
/// <param name="IsAvailable">True when the link is up.</param>
/// <param name="Changed">True when the call that produced this response changed the state.</param>
public sealed record AssetLinkResponse(string AssetId, bool IsAvailable, bool Changed);
