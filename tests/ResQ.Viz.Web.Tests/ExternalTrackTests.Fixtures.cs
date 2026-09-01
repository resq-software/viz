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

using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Tracks;

namespace ResQ.Viz.Web.Tests;

/// <summary>Fixtures, probes and the wording scanner for <see cref="ExternalTrackTests"/>.</summary>
/// <remarks>
/// Split from the assertions so that file reads as a list of contracts, following the same
/// convention the ground suites use.
/// <para>
/// Everything here is deterministic on purpose. The epoch is a literal so published instants
/// reproduce, command identifiers are derived from a fixed seed rather than minted, and every
/// simulated time a case cares about is passed in rather than read. Nothing below reads a wall
/// clock, sleeps, or allocates randomness.
/// </para>
/// </remarks>
public sealed partial class ExternalTrackTests
{
    /// <summary>Identifier every single-contact case reports under.</summary>
    private const string ContactId = "contact-1";

    /// <summary>Rounding budget for a value the arithmetic lands on exactly.</summary>
    private const double Tolerance = 1e-9;

    /// <summary>Instant simulated time zero maps to.</summary>
    /// <remarks>
    /// A literal rather than "now", so a store constructed twice from the same inputs publishes
    /// the same instants and an assertion on <see cref="ExternalTrackState.LastUpdateTime"/> means
    /// what it says.
    /// </remarks>
    private static readonly DateTimeOffset Epoch = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Seed the fixture's command identifiers are derived from.</summary>
    private const int DeterministicSeed = 0x20240517;

    // ─── Store and report fixtures ───────────────────────────────────────────

    /// <summary>A store with a fixed epoch, so published instants are reproducible.</summary>
    /// <param name="options">Ageing windows and bounds, or null for the defaults.</param>
    /// <returns>An empty store.</returns>
    private static ExternalTrackStore Store(ExternalTrackStoreOptions? options = null) =>
        new(options, Epoch);

    /// <summary>A scene-frame pose with no declared attitude, which is normal for a contact.</summary>
    /// <param name="eastM">Scene <c>X</c>, metres east.</param>
    /// <param name="upM">Scene <c>Y</c>, metres up.</param>
    /// <param name="southM">Scene <c>Z</c>, metres south.</param>
    /// <returns>The framed pose.</returns>
    private static FramedPose ScenePose(double eastM, double upM, double southM) =>
        new(CoordinateFrame.LocalEus,
            OriginId: null,
            Position: new Vector3((float)eastM, (float)upM, (float)southM),
            Orientation: default);

    /// <summary>A scene-frame velocity with no angular part.</summary>
    /// <param name="eastMps">Scene <c>X</c> rate, metres per second.</param>
    /// <param name="upMps">Scene <c>Y</c> rate, metres per second.</param>
    /// <param name="southMps">Scene <c>Z</c> rate, metres per second.</param>
    /// <returns>The framed twist.</returns>
    private static FramedTwist SceneTwist(double eastMps, double upMps, double southMps) =>
        new(CoordinateFrame.LocalEus,
            new Vector3((float)eastMps, (float)upMps, (float)southMps),
            Vector3.Zero);

    /// <summary>One validated observation, with everything a case does not care about defaulted.</summary>
    /// <remarks>
    /// Built directly rather than through <see cref="TrackReport.TryCreate"/>: these cases are
    /// about what the store does with a well-formed report, and routing every one through the
    /// boundary would make the fixture depend on the boundary's own rules.
    /// </remarks>
    /// <param name="observedAtSimulationTimeSeconds">Simulation time the observation was made at.</param>
    /// <param name="trackId">Contact the report names.</param>
    /// <param name="eastM">Reported scene <c>X</c>, metres east.</param>
    /// <param name="southM">Reported scene <c>Z</c>, metres south.</param>
    /// <param name="sourceId">Reporting sensor or feed.</param>
    /// <param name="sourceKind">How that source observes.</param>
    /// <param name="confidence">Confidence the contact is real, before ageing discounts it.</param>
    /// <param name="classification">What the source believes the contact is.</param>
    /// <param name="label">Operator-facing label, or null.</param>
    /// <param name="transponder">Cooperative identity, or null for a non-cooperative contact.</param>
    /// <param name="positionAccuracyM">One-sigma horizontal accuracy, or null when unreported.</param>
    /// <returns>The report.</returns>
    private static TrackReport Report(
        double observedAtSimulationTimeSeconds,
        string trackId = ContactId,
        double eastM = 100.0,
        double southM = 0.0,
        string sourceId = "radar-1",
        TrackSourceKind sourceKind = TrackSourceKind.Radar,
        double confidence = 1.0,
        TrackClassification classification = TrackClassification.Vessel,
        string? label = null,
        TransponderIdentity? transponder = null,
        double? positionAccuracyM = null) =>
        new(
            TrackId: trackId,
            Classification: classification,
            Pose: ScenePose(eastM, 0.0, southM),
            Twist: SceneTwist(0.0, 0.0, 0.0),
            SourceId: sourceId,
            SourceKind: sourceKind,
            SourceQuality: null,
            Confidence: confidence,
            ObservedAtSimulationTimeSeconds: observedAtSimulationTimeSeconds,
            PositionAccuracyM: positionAccuracyM,
            Label: label,
            Transponder: transponder);

    /// <summary>The single contact a store holds, aged to a simulation time.</summary>
    /// <param name="store">Store to read.</param>
    /// <param name="nowSimulationTimeSeconds">Simulation time to compute the age at.</param>
    /// <returns>The one held contact, with its age.</returns>
    private static AgedExternalTrack OnlyTrack(
        ExternalTrackStore store, double nowSimulationTimeSeconds) =>
        store.Snapshot(nowSimulationTimeSeconds).Should().ContainSingle().Which;

    // ─── Geometry fixtures ───────────────────────────────────────────────────

    /// <summary>A scene-frame vector, named by axis so a case reads as geography.</summary>
    /// <param name="eastM">Scene <c>X</c>.</param>
    /// <param name="upM">Scene <c>Y</c>.</param>
    /// <param name="southM">Scene <c>Z</c>.</param>
    /// <returns>The vector.</returns>
    private static Vector3 Eus(double eastM, double upM, double southM) =>
        new((float)eastM, (float)upM, (float)southM);

    /// <summary>One platform's motion at one instant, as the geometry consumes it.</summary>
    /// <param name="id">Platform or contact identifier.</param>
    /// <param name="positionEus">Position in the scene frame.</param>
    /// <param name="velocityEus">Velocity in the scene frame.</param>
    /// <param name="headingRad">Declared heading, or null when no attitude was reported.</param>
    /// <param name="age">Simulated seconds since the observation behind the sample.</param>
    /// <param name="confidence">Confidence in the observation, in 0-1.</param>
    /// <param name="freshness">Freshness band the observation falls in.</param>
    /// <returns>The sample.</returns>
    private static TrackMotionSample Sample(
        string id,
        Vector3 positionEus,
        Vector3 velocityEus,
        double? headingRad = null,
        double age = 0.0,
        double confidence = 1.0,
        DataFreshness freshness = DataFreshness.Fresh) =>
        new(id, positionEus, velocityEus, headingRad, age, confidence, freshness);

    /// <summary>Every scalar an advisory publishes, for a blanket finiteness check.</summary>
    /// <remarks>
    /// Nullable fields collapse to zero here on purpose: their absence is asserted separately, and
    /// what this collection is for is catching a <c>NaN</c> that would otherwise travel into a
    /// frame and take every other contact in it with it.
    /// </remarks>
    /// <param name="advisory">Advisory to flatten.</param>
    /// <returns>Its numeric fields.</returns>
    private static IReadOnlyList<double> EveryPublishedNumber(ApproachAdvisory advisory) =>
    [
        advisory.RangeM,
        advisory.HorizontalRangeM,
        advisory.RelativeSpeedMps,
        advisory.ClosestApproachDistanceM,
        advisory.ClosestApproachHorizontalDistanceM,
        advisory.ClosestApproachVerticalSeparationM,
        advisory.TimeToClosestApproachSeconds ?? 0.0,
        advisory.TrueBearingRad ?? 0.0,
        advisory.RelativeBearingRad ?? 0.0,
        advisory.SubjectAgeSeconds,
        advisory.ContactAgeSeconds,
        advisory.DataAgeSeconds,
        advisory.Confidence,
    ];

    // ─── Wire-surface fixtures ───────────────────────────────────────────────

    /// <summary>A v2 controller bound to a fresh room whose simulation clock stands at zero.</summary>
    /// <remarks>
    /// The same shortcut the other v2 suites use: the room is stashed where
    /// <see cref="RequireRoomAttribute"/> would have put it, so this stays a unit test. No tick
    /// loop is attached, so simulated time does not move underneath an assertion.
    /// </remarks>
    /// <returns>The controller and the room it operates on.</returns>
    private static (SimV2Controller Controller, SimulationRoom Room) Api()
    {
        var room = new SimulationRoom(
            id: "track-test-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        IAssetFactory[] factories = [];
        var controller = new SimV2Controller(
            new VizFrameBuilder(), factories, NullLogger<SimV2Controller>.Instance);

        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, room);
    }

    /// <summary>Reports one contact through the wire surface and returns it as published.</summary>
    /// <param name="controller">Controller to report through.</param>
    /// <param name="trackId">Contact identifier.</param>
    /// <returns>The contact as the ingest endpoint published it.</returns>
    private static AgedExternalTrack ReportContact(SimV2Controller controller, string trackId)
    {
        var created = controller.ReportTrack(new TrackReportRequest(
            TrackId: trackId,
            Pose: ScenePose(200.0, 0.0, -150.0),
            Twist: SceneTwist(0.0, 0.0, -4.0),
            Classification: TrackClassification.Vessel,
            SourceId: "ais-1",
            SourceKind: TrackSourceKind.Transponder,
            Confidence: 0.9))
            .Should().BeOfType<CreatedResult>().Which;

        created.StatusCode.Should().Be(StatusCodes.Status201Created);

        var body = created.Value.Should().BeOfType<TrackReportResponse>().Which;
        body.Created.Should().BeTrue();
        return body.Track;
    }

    /// <summary>The most permissive well-formed command a definition allows, aimed at a contact.</summary>
    /// <remarks>
    /// Generous on purpose, because the question is whether a contact can <em>ever</em> be
    /// commanded rather than whether one particular payload clears every gate. A target is
    /// supplied in whichever shape the definition accepts, every required parameter is present and
    /// numeric, and an altitude is always accompanied by the datum the boundary requires — leaving
    /// the identifier space itself as the only thing left that can refuse the probe.
    /// </remarks>
    /// <param name="definition">Catalog row being probed.</param>
    /// <param name="commandId">Identifier so the attempt can be polled for afterwards.</param>
    /// <param name="ordinal">Ordinal used to keep idempotency keys distinct.</param>
    /// <returns>The request.</returns>
    private static AssetCommandRequest ProbeFor(
        CommandDefinition definition, Guid commandId, int ordinal)
    {
        CommandTarget? target = null;
        if (definition.AllowedTargets.HasFlag(CommandTargetKinds.Point))
        {
            target = new PointCommandTarget(ScenePose(10.0, 0.0, 10.0));
        }
        else if (definition.AllowedTargets.HasFlag(CommandTargetKinds.Route))
        {
            target = new RouteCommandTarget("route-1", 0);
        }

        Dictionary<string, string>? parameters = null;
        if (definition.RequiredParameters.Count > 0)
        {
            parameters = definition.RequiredParameters.ToDictionary(
                key => key, _ => "1", StringComparer.Ordinal);

            // An altitude must name its datum or the boundary refuses it before the asset is ever
            // resolved, which would answer the wrong question.
            if (parameters.ContainsKey(CommandParameters.Altitude))
            {
                parameters[CommandParameters.VerticalReference] =
                    nameof(VerticalReference.MeanSeaLevel);
            }
        }

        return new AssetCommandRequest(
            definition.Kind,
            $"key-{ordinal}",
            CommandId: commandId,
            Target: target,
            Parameters: parameters);
    }

    /// <summary>Command identifiers derived from a fixed seed rather than minted.</summary>
    /// <param name="ordinal">Attempt number.</param>
    /// <returns>A stable identifier.</returns>
    private static Guid CommandId(int ordinal) =>
        new(DeterministicSeed, (short)ordinal, 0x4B54, 0x9F, 0x0D, 0xE5, 0x1D, 0x00, 0x00, 0x00, 0x02);

    /// <summary>Unwraps a 200 response body.</summary>
    /// <typeparam name="T">Expected body type.</typeparam>
    /// <param name="result">Action result to unwrap.</param>
    /// <returns>The body.</returns>
    private static T Body<T>(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<T>().Which;

    /// <summary>Every routed action the application publishes, as "VERB template".</summary>
    /// <remarks>
    /// Flattened to strings deliberately: the assertion this feeds is about a set of published
    /// routes, and a string is what a reviewer can compare against the API by eye.
    /// </remarks>
    /// <returns>One entry per verb per action.</returns>
    private static IReadOnlyList<string> Routes()
    {
        var routes = new List<string>();

        foreach (var controller in typeof(SimV2Controller).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            string prefix = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(attribute => attribute.Template)
                .FirstOrDefault() ?? string.Empty;

            foreach (var action in controller.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var verb in action.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                {
                    string template = string.IsNullOrEmpty(verb.Template)
                        ? prefix
                        : $"{prefix}/{verb.Template}";

                    routes.AddRange(verb.HttpMethods.Select(http => $"{http} {template}"));
                }
            }
        }

        return routes;
    }

    // ─── Vocabulary predicates ───────────────────────────────────────────────

    /// <summary>Words that would turn a member name into a way to drive something.</summary>
    private static readonly string[] ControlWords =
        ["Capabilit", "Command", "Control", "Actuat", "Steer", "Throttle", "Waypoint"];

    /// <summary>Words that would turn a descriptive label into advice.</summary>
    private static readonly string[] DirectiveWords =
    [
        "avoid", "giveway", "standon", "manoeuvre", "maneuver", "action", "alarm", "alert",
        "danger", "warning", "unsafe", "violation", "must", "should", "recommend", "priority",
        "precedence", "risk",
    ];

    /// <summary>Members a record's compiler-generated surface always contributes.</summary>
    private static readonly string[] CompilerSuppliedRecordMembers =
        ["ToString", "Equals", "GetHashCode", "GetType", "Deconstruct", "PrintMembers", "<Clone>$"];

    /// <summary>Whether a member name reads as a way to drive something.</summary>
    /// <param name="name">Member name.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    private static bool SuggestsControl(string name) =>
        ControlWords.Any(word => name.Contains(word, StringComparison.Ordinal));

    /// <summary>Whether a label tells someone what to do rather than what is there.</summary>
    /// <param name="name">Enum member or property name.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    private static bool IsDirective(string name) =>
        DirectiveWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a method is one the record synthesis always adds.</summary>
    /// <param name="name">Method name.</param>
    /// <returns><see langword="true"/> when the compiler supplied it.</returns>
    private static bool IsCompilerSuppliedRecordMember(string name) =>
        CompilerSuppliedRecordMembers.Contains(name, StringComparer.Ordinal);

    // ─── Wording scanner ─────────────────────────────────────────────────────

    /// <summary>Phrases that assert more than advisory decision support.</summary>
    /// <remarks>
    /// Phrases rather than bare words, so "a contact colliding with an asset id" is not mistaken
    /// for a collision-avoidance claim. Each is permitted only inside a statement that also
    /// negates or qualifies it.
    /// </remarks>
    private static readonly string[] ClaimTerms =
    [
        "colreg", "solas", "collision avoidance", "collision-avoidance", "anti-collision",
        "collision warning", "autonomous navigation", "navigation authority", "control authority",
        "right of way", "right-of-way", "give-way", "stand-on", "certified", "certification",
        "type-approved", "airworthiness", "regulatory", "regulation", "compliant", "compliance",
        "complies", "guarantee",
    ];

    /// <summary>What has to appear beside a claim word for it to read as advisory.</summary>
    private static readonly Regex AdvisoryQualifier = new(
        @"\b(not|no|never|nothing|none|neither|nor|cannot|advisory|advisories)\b|decision support",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Breaks joined comment text into statements at sentence boundaries.</summary>
    private static readonly Regex SentenceBreak = new(@"(?<=\.)\s+", RegexOptions.CultureInvariant);

    /// <summary>Collapses runs of whitespace left by unwrapping a comment block.</summary>
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Whether one statement carries a claim word with nothing qualifying it.</summary>
    /// <param name="statement">Sentence, symbol name or literal to judge.</param>
    /// <returns><see langword="true"/> when the claim stands unqualified.</returns>
    private static bool ImpliesAnUnqualifiedClaim(string statement) =>
        ClaimTerms.Any(term => statement.Contains(term, StringComparison.OrdinalIgnoreCase))
        && !AdvisoryQualifier.IsMatch(statement);

    /// <summary>Source files that carry the track and geometry contracts.</summary>
    /// <remarks>
    /// Resolved from the repository root rather than embedded, and every path is asserted to
    /// exist: a scan that silently found no files would pass forever while the wording drifted.
    /// </remarks>
    /// <returns>Absolute paths, in a stable order.</returns>
    private static IReadOnlyList<string> TrackSourcePaths()
    {
        string web = Path.Combine(RepositoryRoot().FullName, "src", "ResQ.Viz.Web");

        string[] beside =
        [
            Path.Combine(web, "Models", "ExternalTracks.cs"),
            Path.Combine(web, "Models", "SimCommandTracks.cs"),
            Path.Combine(web, "Services", "SimulationRoom.Tracks.cs"),
            Path.Combine(web, "Controllers", "SimV2Controller.Tracks.cs"),
        ];

        var paths = Directory
            .GetFiles(Path.Combine(web, "Services", "Tracks"), "*.cs")
            .Concat(beside)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        paths.Should().HaveCountGreaterThan(8).And.OnlyContain(path => File.Exists(path));
        return paths;
    }

    /// <summary>The repository root, found by walking up from the test assembly.</summary>
    /// <returns>The directory holding the solution file.</returns>
    /// <exception cref="InvalidOperationException">No root was found above the test assembly.</exception>
    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "ResQ.Viz.sln")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            "The wording scan must read the sources it asserts about, and no repository root was "
            + "found above the test assembly.");
    }

    /// <summary>One source file as statements, with the licence header and comment markers gone.</summary>
    /// <remarks>
    /// Statements rather than lines, because the qualification and the claim word routinely sit on
    /// different lines of one wrapped doc comment: a line-wise scan would flag "nothing on this
    /// type can be mistaken for control authority" purely for where the wrap happened to fall.
    /// The licence header is skipped because it says "in compliance with the License", which is a
    /// statement about the licence and not about this code.
    /// </remarks>
    /// <param name="path">File to read.</param>
    /// <returns>The file's statements.</returns>
    private static IReadOnlyList<string> Statements(string path)
    {
        var lines = File.ReadAllLines(path);
        int headerEnd = Array.FindIndex(lines, line => line.Trim() == "*/");

        var body = lines
            .Skip(headerEnd + 1)
            .Select(line => line.Trim().TrimStart('/').TrimStart('*').Trim());

        return SentenceBreak.Split(WhitespaceRun.Replace(string.Join(" ", body), " "));
    }

    /// <summary>The public types the track and geometry surface is made of.</summary>
    /// <returns>Every public type in the tracks namespace plus the track models beside it.</returns>
    private static IReadOnlyList<Type> TrackSurfaceTypes() =>
        typeof(ExternalTrackStore).Assembly.GetTypes()
            .Where(type => type.IsPublic && IsTrackSurface(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Whether a type belongs to the track or geometry surface.</summary>
    /// <param name="type">Candidate type.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    private static bool IsTrackSurface(Type type)
    {
        if (type.Namespace == typeof(ExternalTrackStore).Namespace)
        {
            return true;
        }

        if (type.Namespace != typeof(ExternalTrackState).Namespace)
        {
            return false;
        }

        string[] prefixes = ["Track", "ExternalTrack", "Transponder", "Aged"];
        return prefixes.Any(prefix => type.Name.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Public names on a type, plus the text of any operator-facing constant it holds.</summary>
    /// <param name="type">Type to enumerate.</param>
    /// <returns>Symbol names and constant statements.</returns>
    private static IEnumerable<string> PublicSymbolText(Type type)
    {
        yield return type.Name;

        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            yield return member.Name;

            if (member is not FieldInfo { IsLiteral: true } literal
                || literal.FieldType != typeof(string)
                || literal.GetRawConstantValue() is not string text)
            {
                continue;
            }

            foreach (var statement in SentenceBreak.Split(WhitespaceRun.Replace(text, " ")))
            {
                yield return statement;
            }
        }
    }
}
