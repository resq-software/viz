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

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Configuration;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

// Reading one configuration row into an Entry, and refusing every row that cannot be placed.
//
// Split from the executing half so the parser reads as a list of rules. Everything here is pure
// and total: it returns false with a reason rather than throwing, because the caller's contract
// is that a bad row is skipped and logged, and an exception raised from a parser would defeat
// that from the inside. That includes the numbers themselves — they are parsed by hand rather
// than through IConfiguration's binder, which throws on a value it cannot convert and would turn
// one mistyped coordinate into a host that will not start.
public sealed partial class ScenarioService
{
    /// <summary>Furthest from the scene origin an entry may place an asset, in metres.</summary>
    /// <remarks>
    /// The same envelope the v2 spawn endpoint enforces. A preset and an API call place assets in
    /// the same world, so accepting a coordinate through one that the other refuses would produce
    /// a vehicle no operator could have created and no client is laid out to draw.
    /// </remarks>
    private const double MaxSceneCoordinateM = 20_000.0;

    /// <summary>Longest an identifier may be, in characters.</summary>
    /// <remarks>Matches the v2 spawn endpoint's limit, for the same reason as the coordinate envelope.</remarks>
    private const int MaxIdentifierLength = 64;

    /// <summary>Longest a value may be when it reaches a log line, in characters.</summary>
    private const int MaxLoggedLength = 120;

    private const string PositionKey = "pos";
    private const string VehicleClassKey = "class";
    private const string DomainKey = "domain";
    private const string HeadingKey = "headingDeg";

    /// <summary>Punctuation an identifier may carry beyond ASCII letters and digits.</summary>
    /// <remarks>
    /// Deliberately narrow, and the same set the v2 endpoint allows. Identifiers appear in URLs,
    /// in log lines and as mesh endpoints, and one carrying a slash or a newline is a problem in
    /// all three places at once.
    /// </remarks>
    private static readonly char[] IdentifierExtraChars = ['-', '_', '.'];

    /// <summary>Reads one configuration entry, rejecting anything it cannot place safely.</summary>
    /// <remarks>
    /// Order matters only for which problem is reported first, not for whether a row is accepted:
    /// the rules below are independent, and a row has to satisfy all of them.
    /// </remarks>
    /// <param name="section">Configuration section for a single entry.</param>
    /// <param name="entry">The parsed entry on success.</param>
    /// <param name="problem">Operator-facing description of the first rule the row broke.</param>
    /// <returns><see langword="true"/> when the entry is usable.</returns>
    private static bool TryReadEntry(
        IConfigurationSection section,
        out Entry entry,
        [NotNullWhen(false)] out string? problem)
    {
        entry = default;

        if (!TryReadIdentifier(section, out var id, out problem)
            || !TryReadPosition(section, out var position, out problem)
            || !TryReadVehicleClass(section, out var vehicleClass, out problem))
        {
            return false;
        }

        var domain = AssetProfiles.DomainFor(vehicleClass);

        if (!DeclaredDomainAgrees(section, domain, out problem)
            || !TryReadHeadingRad(section, out var headingRad, out problem))
        {
            return false;
        }

        var vendor = section["vendor"];

        entry = new Entry(
            Id: id,
            Pos: position,
            Vendor: string.IsNullOrWhiteSpace(vendor) ? null : vendor,
            Domain: domain,
            VehicleClass: vehicleClass,
            HeadingRad: headingRad);

        problem = null;
        return true;
    }

    /// <summary>Reads an entry's identifier and holds it to the same charset the API does.</summary>
    /// <remarks>
    /// Whitespace is not an identifier. A row whose id was <c>" "</c> used to clear the emptiness
    /// check, reach <see cref="AssetProfiles.Create"/>, and throw out of a scenario run that had
    /// already spawned half a world.
    /// </remarks>
    /// <param name="section">Configuration section for a single entry.</param>
    /// <param name="id">The identifier on success.</param>
    /// <param name="problem">Why the identifier was refused.</param>
    /// <returns><see langword="true"/> when the identifier is usable.</returns>
    private static bool TryReadIdentifier(
        IConfigurationSection section, out string id, [NotNullWhen(false)] out string? problem)
    {
        id = section["id"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(id))
        {
            problem = "'id' is missing or blank";
            return false;
        }

        if (id.Length > MaxIdentifierLength)
        {
            problem = $"'id' is longer than the {MaxIdentifierLength}-character limit";
            return false;
        }

        if (!id.All(c => char.IsAsciiLetterOrDigit(c) || IdentifierExtraChars.Contains(c)))
        {
            problem = $"'id' ('{LogSafe(id)}') must be letters, digits, '-', '_' or '.' only";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Reads the three scene-frame coordinates, refusing anything unplaceable.</summary>
    /// <remarks>
    /// Indexed explicitly rather than bound to an array, for two reasons. The binder throws on a
    /// value it cannot convert, which is precisely the failure this method exists to report
    /// rather than raise; and reading <c>pos:0</c>, <c>pos:1</c> and <c>pos:2</c> by name does not
    /// depend on the order a provider happens to enumerate an array's children in.
    /// <para>
    /// A non-finite coordinate is refused rather than clamped. <c>NaN</c> parses happily, and a
    /// rover settled at a <c>NaN</c> position poisons its own pose, every distance measured
    /// against it, and the frame that publishes it.
    /// </para>
    /// </remarks>
    /// <param name="section">Configuration section for a single entry.</param>
    /// <param name="position">Scene-frame position on success, in metres.</param>
    /// <param name="problem">Why the position was refused.</param>
    /// <returns><see langword="true"/> when the position is usable.</returns>
    private static bool TryReadPosition(
        IConfigurationSection section, out Vector3 position, [NotNullWhen(false)] out string? problem)
    {
        position = default;

        var pos = section.GetSection(PositionKey);
        int count = pos.GetChildren().Count();

        if (count != 3)
        {
            problem = $"'{PositionKey}' must be exactly three numbers, but has {count}";
            return false;
        }

        var components = new float[3];

        for (int i = 0; i < components.Length; i++)
        {
            string axis = $"{PositionKey}[{i}]";
            string? raw = pos[i.ToString(CultureInfo.InvariantCulture)];

            if (!double.TryParse(
                    raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value))
            {
                problem = $"'{axis}' ('{LogSafe(raw)}') is not a finite number";
                return false;
            }

            if (Math.Abs(value) > MaxSceneCoordinateM)
            {
                problem = string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{axis}' is {value:N0} m from the scene origin, beyond the {MaxSceneCoordinateM:N0} m limit");
                return false;
            }

            components[i] = (float)value;
        }

        position = new Vector3(components[0], components[1], components[2]);
        problem = null;
        return true;
    }

    /// <summary>Resolves an entry's vehicle class, defaulting to the pre-multi-domain one.</summary>
    /// <remarks>
    /// An unparseable or unsupported name is a rejection rather than a fallback to the default.
    /// Silently substituting a multirotor for a misspelled rover class would put an aircraft in a
    /// ground convoy, which reads as a terrain bug rather than as the typo it is.
    /// </remarks>
    /// <param name="section">Configuration section for a single entry.</param>
    /// <param name="vehicleClass">Resolved class on success.</param>
    /// <param name="problem">Why the class was refused.</param>
    /// <returns><see langword="true"/> when the class is absent or names a spawnable class.</returns>
    private static bool TryReadVehicleClass(
        IConfigurationSection section,
        out VehicleClass vehicleClass,
        [NotNullWhen(false)] out string? problem)
    {
        var declared = section[VehicleClassKey];

        if (string.IsNullOrWhiteSpace(declared))
        {
            vehicleClass = VehicleClass.Multirotor;
            problem = null;
            return true;
        }

        if (!Enum.TryParse(declared, ignoreCase: true, out vehicleClass)
            || !Enum.IsDefined(vehicleClass)
            || !AssetProfiles.IsSupported(vehicleClass))
        {
            vehicleClass = VehicleClass.Unspecified;
            problem =
                $"'{VehicleClassKey}' ('{LogSafe(declared)}') is not a vehicle class this build simulates";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Checks an optional declared domain against the one the class implies.</summary>
    /// <remarks>
    /// The declaration is documentation, not authority: it is allowed so a preset reads clearly,
    /// and it is checked so a preset that has drifted out of step with itself is caught at load
    /// rather than producing an asset in a domain nobody meant.
    /// </remarks>
    /// <param name="section">Configuration section for a single entry.</param>
    /// <param name="derived">Domain implied by the entry's vehicle class.</param>
    /// <param name="problem">Why the declared domain was refused.</param>
    /// <returns><see langword="true"/> when no domain was declared, or the declared one matches.</returns>
    private static bool DeclaredDomainAgrees(
        IConfigurationSection section, AssetDomain derived, [NotNullWhen(false)] out string? problem)
    {
        var declared = section[DomainKey];

        if (string.IsNullOrWhiteSpace(declared))
        {
            problem = null;
            return true;
        }

        if (!Enum.TryParse<AssetDomain>(declared, ignoreCase: true, out var parsed) || parsed != derived)
        {
            problem =
                $"'{DomainKey}' ('{LogSafe(declared)}') contradicts the '{derived}' domain its "
                + "vehicle class implies";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Reads an entry's initial heading, in radians clockwise from true north.</summary>
    /// <remarks>
    /// Written in degrees in configuration because that is the unit a scenario author thinks in,
    /// and converted here so the rest of the system only ever sees radians. An absent value is
    /// zero — due north — the same answer a spawn request that declared no meaningful orientation
    /// gets. A value that is present but unreadable is a rejection rather than a silent zero: a
    /// rover facing north when its author asked for something else is a bug nothing reports.
    /// </remarks>
    /// <param name="section">Configuration section for a single entry.</param>
    /// <param name="headingRad">Heading in radians on success.</param>
    /// <param name="problem">Why the heading was refused.</param>
    /// <returns><see langword="true"/> when the heading is absent or usable.</returns>
    private static bool TryReadHeadingRad(
        IConfigurationSection section, out double headingRad, [NotNullWhen(false)] out string? problem)
    {
        headingRad = 0.0;
        var declared = section[HeadingKey];

        if (string.IsNullOrWhiteSpace(declared))
        {
            problem = null;
            return true;
        }

        if (!double.TryParse(
                declared, NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees)
            || !double.IsFinite(degrees))
        {
            problem = $"'{HeadingKey}' ('{LogSafe(declared)}') is not a finite number of degrees";
            return false;
        }

        headingRad = double.DegreesToRadians(degrees);
        problem = null;
        return true;
    }

    /// <summary>Strips control characters and truncates, so configuration cannot forge a log line.</summary>
    /// <remarks>
    /// Presets are operator-authored files rather than request payloads, but they are still input
    /// that reaches a log, and a value carrying a newline can write a second, fabricated entry.
    /// </remarks>
    /// <param name="value">Value about to be logged.</param>
    /// <returns>A single-line, bounded rendering of <paramref name="value"/>.</returns>
    private static string LogSafe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var truncated = value.Length > MaxLoggedLength ? value[..MaxLoggedLength] : value;
        return truncated
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}
