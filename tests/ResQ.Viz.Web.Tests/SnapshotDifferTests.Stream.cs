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

using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Tests;

// The frame generator behind the round-trip and purity cases. The type's summary lives on the
// primary declaration in SnapshotDifferTests.cs.
public sealed partial class SnapshotDifferTests
{
    /// <summary>
    /// A reproducible stream of v2 frames for a mixed air/ground/surface room with external
    /// tracks, detections, hazards and a comms mesh.
    /// </summary>
    /// <remarks>
    /// <b>Why generated and not hand-built.</b> A hand-built pair proves the differ handles the
    /// transition its author thought of. The defects that matter here are the ones nobody thinks
    /// of: an asset removed in the same frame another arrives, a descriptor bumped while its asset
    /// is elided, comms disappearing entirely, a transport tick that disagrees with the frame's
    /// own. Every one of those produces a well-formed, plausible, silently wrong scene rather than
    /// an exception, so the only economical way to find them is to generate transitions in bulk
    /// and assert the round-trip property over all of them.
    /// <para>
    /// <b>Everything derives from one seeded <see cref="Random"/>, frame identifiers included.</b>
    /// Two streams built with the same seed are frame-for-frame identical, which is what lets the
    /// purity case diff two equal-but-distinct rooms and compare the results — a check that would
    /// be meaningless against a generator reaching for <see cref="Guid.NewGuid"/> or the clock. A
    /// failure is likewise replayable from the seed alone.
    /// </para>
    /// <para>
    /// <b>Collections are only ever mutated in place, removed from, or appended to.</b> The
    /// differ's merge reconstructs order as "base-frame order, minus removals, with new entries
    /// appended", which reproduces any producer that emits entities in a stable order — the order
    /// <c>SimulationRoom</c> publishes. A generator that reshuffled an unchanged collection
    /// would be modelling a producer this format does not claim to support, and would fail the
    /// round trip for a reason that says nothing about the differ.
    /// </para>
    /// </remarks>
    private sealed class SnapshotStream
    {
        private const int AssetsPerDomain = 2;
        private const long TicksPerFrame = 6;

        /// <summary>Tick interval between deliberately quiet frames.</summary>
        /// <remarks>
        /// A frame on which nothing but the capture stamps moves — an idle or paused room — is the
        /// transition the format is built for, and it is the one a purely probabilistic generator
        /// stops producing as soon as the room grows: with a dozen assets each moving
        /// independently, "none of them moved" becomes rare enough that whether the run covers it
        /// turns on the seed. Scheduling it makes the coverage assertion mean something.
        /// </remarks>
        private const long QuietFrameInterval = TicksPerFrame * 11;

        private static readonly AssetDomain[] Domains =
            [AssetDomain.Air, AssetDomain.Ground, AssetDomain.Surface];

        private readonly Random _random;
        private readonly List<AssetSeed> _assets = [];
        private readonly List<TrackSeed> _tracks = [];
        private readonly List<HazardSeed> _hazards = [];
        private readonly List<DetectionSeed> _detections = [];
        private readonly List<LinkSeed> _links = [];

        private bool _isPartitioned;
        private bool _isQuietFrame;
        private bool _reportsNetwork = true;
        private string _environment = "env-0";
        private bool _paused;
        private int _speed = 1;
        private long _tick;
        private long _transportTick;
        private int _spawned;

        /// <summary>Builds the opening frame of a stream.</summary>
        /// <param name="seed">Seed for the stream's only source of randomness.</param>
        public SnapshotStream(int seed)
        {
            _random = new Random(seed);

            foreach (var domain in Domains)
            {
                for (var i = 0; i < AssetsPerDomain; i++)
                {
                    var height = domain == AssetDomain.Air ? 40f : 0f;
                    _assets.Add(Seeded(
                        $"{PrefixOf(domain)}-{i + 1}", domain, new Vector3(i * 12f, height, i * -8f)));
                }
            }

            _tracks.Add(new TrackSeed(
                "trk-vessel", new Vector3(120f, 0f, -60f), TrackClassification.Vessel, 3, Epoch));
            _tracks.Add(new TrackSeed(
                "trk-aircraft", new Vector3(-80f, 300f, 40f), TrackClassification.Aircraft, 7, Epoch));
            _hazards.Add(new HazardSeed(
                "haz-fire", new Vector3(30f, 0f, 30f), 25.0, HazardSeverity.High, Epoch));
            _hazards.Add(new HazardSeed(
                "haz-shoal", new Vector3(-40f, 0f, 90f), 60.0, HazardSeverity.Medium, Epoch));
            _detections.Add(new DetectionSeed(
                "det-1", new Vector3(5f, 0f, 5f), 0.72, "uav-1", Epoch));
            _links.Add(new LinkSeed("uav-1", "ugv-1", 0.82, 120.0));
            _links.Add(new LinkSeed("ugv-1", "usv-1", 0.61, 240.0));

            Current = Build();
        }

        /// <summary>The most recently produced frame.</summary>
        public VizSnapshotV2 Current { get; private set; }

        /// <summary>Produces the next frame in the stream.</summary>
        /// <returns>The new <see cref="Current"/>.</returns>
        public VizSnapshotV2 Advance()
        {
            _tick += TicksPerFrame;
            _transportTick = _tick;
            _isQuietFrame = _tick % QuietFrameInterval == 0;

            AdvanceAssets();

            if (!_isQuietFrame)
            {
                MaybeRemoveAsset();
                MaybeSpawnAsset();
                MaybeBumpDescriptor();
                MaybeChangeTracks();
                MaybeChangeDetections();
                MaybeChangeHazards();
                MaybeChangeNetwork();
                MaybeChangeEnvironment();
                MaybeChangeTransport();
            }

            Current = Build();
            return Current;
        }

        private void AdvanceAssets()
        {
            for (var i = 0; i < _assets.Count; i++)
            {
                // The volatile core advances for every asset on every frame, moved or not, exactly
                // as a real capture stamps it. An asset that is not moved below therefore differs
                // from its predecessor in nothing but that core, which is the whole condition the
                // carried channel exists to encode.
                var seed = _assets[i] with { Sequence = _assets[i].Sequence + 1 };

                if (!_isQuietFrame && Chance(0.55))
                {
                    seed = seed with
                    {
                        Position = seed.Position + new Vector3(Jitter(), 0f, Jitter()),
                        Heading = seed.Heading + (Jitter() * 0.05f),
                        Battery = Math.Round(seed.Battery - 0.05, 6),
                    };
                }

                if (!_isQuietFrame && Chance(0.04))
                {
                    seed = seed with
                    {
                        Operational = seed.Operational == OperationalState.Active
                            ? OperationalState.Holding
                            : OperationalState.Active,
                    };
                }

                _assets[i] = seed;
            }
        }

        private void MaybeRemoveAsset()
        {
            if (_assets.Count > 3 && Chance(0.07))
            {
                _assets.RemoveAt(_random.Next(_assets.Count));
            }
        }

        private void MaybeSpawnAsset()
        {
            if (!Chance(0.07))
            {
                return;
            }

            var domain = Domains[_random.Next(Domains.Length)];
            _spawned++;

            _assets.Add(Seeded(
                $"{PrefixOf(domain)}-new-{_spawned}",
                domain,
                new Vector3(_random.Next(-200, 200), domain == AssetDomain.Air ? 60f : 0f, _random.Next(-200, 200))));
        }

        private void MaybeBumpDescriptor()
        {
            if (_assets.Count == 0 || !Chance(0.09))
            {
                return;
            }

            var index = _random.Next(_assets.Count);
            _assets[index] = _assets[index] with { Revision = _assets[index].Revision + 1 };
        }

        private void MaybeChangeTracks()
        {
            if (!Chance(0.16))
            {
                return;
            }

            if (_tracks.Count > 1 && Chance(0.15))
            {
                _tracks.RemoveAt(_random.Next(_tracks.Count));
                return;
            }

            if (Chance(0.15))
            {
                _tracks.Add(new TrackSeed(
                    $"trk-{_tick}",
                    new Vector3(Jitter() * 30f, 0f, Jitter() * 30f),
                    TrackClassification.SmallUnmannedAircraft,
                    1,
                    TimeOf(_tick)));
                return;
            }

            var index = _random.Next(_tracks.Count);
            var track = _tracks[index];
            _tracks[index] = track with
            {
                Position = track.Position + new Vector3(Jitter() * 2f, 0f, Jitter() * 2f),
                UpdateCount = track.UpdateCount + 1,
                ObservedAt = TimeOf(_tick),
            };
        }

        private void MaybeChangeHazards()
        {
            if (!Chance(0.12))
            {
                return;
            }

            if (_hazards.Count > 1 && Chance(0.2))
            {
                _hazards.RemoveAt(_random.Next(_hazards.Count));
                return;
            }

            if (Chance(0.2))
            {
                _hazards.Add(new HazardSeed(
                    $"haz-{_tick}",
                    new Vector3(Jitter() * 40f, 0f, Jitter() * 40f),
                    10.0 + (_random.NextDouble() * 40.0),
                    HazardSeverity.Low,
                    TimeOf(_tick)));
                return;
            }

            var index = _random.Next(_hazards.Count);
            var hazard = _hazards[index];
            _hazards[index] = hazard with
            {
                RadiusM = Math.Round(hazard.RadiusM + Jitter(), 4),
                ObservedAt = TimeOf(_tick),
            };
        }

        private void MaybeChangeDetections()
        {
            if (!Chance(0.2))
            {
                return;
            }

            // Replaced whole, never reconciled, because that is what the delta does with them.
            // Zero is one of the outcomes: a frame that stops reporting detections is the
            // transition a differ that treated an empty list as "unchanged" would lose.
            _detections.Clear();
            var count = _random.Next(0, 3);

            for (var i = 0; i < count; i++)
            {
                _detections.Add(new DetectionSeed(
                    $"det-{i + 1}",
                    new Vector3(Jitter() * 10f, 0f, Jitter() * 10f),
                    Math.Round(_random.NextDouble(), 4),
                    _assets.Count == 0 ? "uav-1" : _assets[_random.Next(_assets.Count)].Id,
                    TimeOf(_tick)));
            }
        }

        private void MaybeChangeNetwork()
        {
            if (!Chance(0.12))
            {
                return;
            }

            // A room that stops reporting comms altogether is a real transition — a server that
            // models no propagation says nothing rather than saying "healthy" — and it is the only
            // thing that reaches the delta's cleared flag.
            if (Chance(0.25))
            {
                _reportsNetwork = !_reportsNetwork;
                return;
            }

            var index = _random.Next(_links.Count);
            var link = _links[index];
            _links[index] = link with
            {
                Quality = Math.Round(_random.NextDouble(), 4),
                RangeM = Math.Round(link.RangeM + Jitter(), 4),
            };

            _isPartitioned = Chance(0.3);
        }

        private void MaybeChangeEnvironment()
        {
            if (Chance(0.05))
            {
                _environment = $"env-{_tick}";
            }
        }

        private void MaybeChangeTransport()
        {
            if (Chance(0.07))
            {
                _paused = !_paused;
            }

            if (Chance(0.05))
            {
                _speed = 1 + _random.Next(4);
            }

            // A frame whose transport tick disagrees with its own tick cannot be elided. The real
            // producer keeps the two equal, so nothing else would reach that branch of the encoder.
            if (Chance(0.04))
            {
                _transportTick = _tick - TicksPerFrame;
            }
        }

        private VizSnapshotV2 Build() => Room(
            NextGuid(),
            _tick,
            _assets,
            _tracks,
            _detections,
            _hazards,
            _reportsNetwork ? Network(_links, _isPartitioned) : null,
            _environment,
            new TransportState(_paused, _speed, _transportTick));

        private bool Chance(double probability) => _random.NextDouble() < probability;

        private float Jitter() => (float)((_random.NextDouble() - 0.5) * 4.0);

        /// <summary>Draws a frame identifier from the stream's own randomness.</summary>
        /// <remarks>
        /// <see cref="Guid.NewGuid"/> would make two same-seeded streams differ in the one field
        /// the delta chain is keyed on, and the purity case compares whole encoded deltas.
        /// </remarks>
        private Guid NextGuid()
        {
            Span<byte> bytes = stackalloc byte[16];
            _random.NextBytes(bytes);
            return new Guid(bytes);
        }
    }
}
