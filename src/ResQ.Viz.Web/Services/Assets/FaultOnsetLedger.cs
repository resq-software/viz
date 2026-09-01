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

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>
/// Remembers when each of an asset's faults first went up, so a standing fault reports the
/// instant it started rather than the instant it was last looked at.
/// </summary>
/// <remarks>
/// A health builder evaluates conditions afresh on every capture and has no memory, so the only
/// instant available to it is the capture's own source time. Stamping that straight into
/// <see cref="FaultCode.RaisedAt"/> makes every fault permanently zero seconds old: an operator
/// cannot tell an advisory that just appeared from one that has been up since the sortie began,
/// which is the difference between "watch it" and "act now". This ledger sits between the
/// builder and the published state and restores the real onset.
/// <para>
/// <b>Clearing is what makes an occurrence.</b> A code absent from one capture is forgotten, so
/// the same condition returning later is a genuinely new occurrence with a new instant, rather
/// than being back-dated to the first time it was ever seen. Two separate groundings are two
/// events, not one long one.
/// </para>
/// <para>
/// There is a secondary benefit and it is worth naming because it is easy to mistake for the
/// purpose: a fault whose timestamp stops moving lets the delta stream recognise an otherwise
/// unchanged asset as unchanged. That is a consequence of reporting the truth, not the reason
/// for it — the ledger would be right even if nothing downstream diffed anything.
/// </para>
/// <para>
/// One instance belongs to one asset, and it is touched only from that asset's capture path, so
/// it carries no synchronisation of its own.
/// </para>
/// </remarks>
public sealed class FaultOnsetLedger
{
    /// <summary>Onset instant per fault code currently standing.</summary>
    private readonly Dictionary<string, DateTimeOffset> _onsets = new(StringComparer.Ordinal);

    /// <summary>Rewrites a freshly built health state so each fault carries its true onset.</summary>
    /// <remarks>
    /// Returns <paramref name="health"/> itself when there is nothing to rewrite — the common
    /// case by far, since most assets are nominal most of the time — which also keeps successive
    /// nominal captures reference-equal.
    /// </remarks>
    /// <param name="health">Health state as the domain's builder produced it.</param>
    /// <param name="observedAt">Instant of this capture, used for faults appearing now.</param>
    /// <returns>The health state with onset-corrected fault instants.</returns>
    public HealthState Stamp(HealthState health, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(health);

        if (health.Faults.Count == 0)
        {
            _onsets.Clear();
            return health;
        }

        var corrected = new FaultCode[health.Faults.Count];
        var stillUp = new HashSet<string>(health.Faults.Count, StringComparer.Ordinal);
        var rewritten = false;

        for (var i = 0; i < health.Faults.Count; i++)
        {
            var fault = health.Faults[i];
            stillUp.Add(fault.Code);

            if (_onsets.TryGetValue(fault.Code, out var onset))
            {
                corrected[i] = onset == fault.RaisedAt ? fault : fault with { RaisedAt = onset };
                rewritten |= onset != fault.RaisedAt;
            }
            else
            {
                // First sighting: the builder's instant is the onset, and becomes the one every
                // later capture of the same standing condition will report.
                _onsets[fault.Code] = fault.RaisedAt;
                corrected[i] = fault;
            }
        }

        Forget(stillUp);

        return rewritten ? health with { Faults = corrected } : health;
    }

    /// <summary>Drops remembered onsets for conditions that are no longer raised.</summary>
    /// <param name="stillUp">Codes present in the capture just processed.</param>
    private void Forget(HashSet<string> stillUp)
    {
        if (_onsets.Count == stillUp.Count)
        {
            return;
        }

        foreach (var code in _onsets.Keys.Where(c => !stillUp.Contains(c)).ToArray())
        {
            _onsets.Remove(code);
        }
    }
}
