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

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>The command link's state, asset by asset, as the server knows it.</summary>
/// <remarks>
/// <b>Why an asset cannot answer this itself.</b> Connectivity is a property of the bearer
/// between the server and the vehicle, and only one end of it is inside the simulation. An asset
/// asked whether it is connected would always say yes: it is a method call away, it is producing
/// telemetry, and it has no way to know the far end has stopped listening. So every capture
/// stamped <c>IsConnected: true</c> even while an operator was holding that asset's link down,
/// and <see cref="SafeActionPolicy.Evaluate"/>'s test on the flag could never fire.
/// <para>
/// This interface is the one channel by which the fact travels the other way: the world hands a
/// view of the link ledger to <see cref="AssetCaptureContext"/>, and an asset stamps what the
/// server knows rather than what it wishes were true. It is deliberately a read-only lookup —
/// nothing an asset can reach may take its own link down or bring it back up.
/// </para>
/// </remarks>
public interface IAssetLinkView
{
    /// <summary>Whether the server is currently hearing an asset.</summary>
    /// <param name="assetId">Asset to ask about.</param>
    /// <returns><see langword="true"/> unless the link is being held down.</returns>
    bool IsLinkConnected(string assetId);
}
