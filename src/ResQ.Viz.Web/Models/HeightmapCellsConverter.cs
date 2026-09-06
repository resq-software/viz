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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResQ.Viz.Web.Models;

/// <summary>
/// Reads a heightmap's <c>cells</c> array, refusing more than <see cref="MaxCells"/> of them
/// while still deserialising.
/// </summary>
/// <remarks>
/// A body-size limit cannot bound this allocation, because bytes on the wire and cells in memory
/// are not proportional: a 4096-square grid of zeros is about two bytes per cell as JSON, so it
/// arrives well under a 48 MiB cap and then binds to a 64 MiB <c>float[]</c>, which the endpoint
/// copies into a 64 MiB <c>float[,]</c> — 128 MiB of managed heap from 32 MiB of text.
/// <para>
/// The dimension check on the endpoint cannot bound it either, and this is the part worth being
/// precise about: <c>[FromBody]</c> means model binding has already run by the time the handler
/// body executes, so a guard written as the first statement of the action still runs
/// <i>after</i> the array exists. The limit has to be applied by whatever reads the tokens,
/// which is this.
/// </para>
/// <para>
/// The cap is the resolution the project already documents as its useful ceiling — the client's
/// heightmap guide puts the sweet spot at 512² to 2048² and notes that larger images waste
/// memory without adding detail past the terrain mesh's segment count. Refusing above that
/// rejects nothing anyone should be sending, and holds peak allocation to 16 MiB bound plus
/// 16 MiB copied.
/// </para>
/// </remarks>
public sealed class HeightmapCellsConverter : JsonConverter<float[]>
{
    /// <summary>Largest accepted cell count: a 2048-square grid.</summary>
    public const int MaxCells = 2048 * 2048;

    /// <inheritdoc />
    /// <exception cref="JsonException">
    /// The value is not an array, contains a non-numeric element, or exceeds
    /// <see cref="MaxCells"/>. The count is enforced as the array is read, so an oversized body
    /// is abandoned partway rather than materialised and then measured.
    /// </exception>
    public override float[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("cells must be an array of numbers");
        }

        // Grown rather than pre-sized: the reader cannot say how many elements are coming, and
        // sizing from a caller-supplied rows*cols would trust the very number this exists to bound.
        var cells = new List<float>(capacity: 1024);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return cells.ToArray();
            }

            if (reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException("cells must contain only numbers");
            }

            if (cells.Count >= MaxCells)
            {
                throw new JsonException(
                    $"cells exceeds the maximum of {MaxCells} ({(int)Math.Sqrt(MaxCells)} squared)");
            }

            cells.Add(reader.GetSingle());
        }

        throw new JsonException("cells array was not terminated");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writing is unbounded on purpose. The cap exists to stop an untrusted body allocating the
    /// server's heap; a grid this process already holds has been through that check once, and
    /// refusing to serialise it would only make a legitimate round-trip fail.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, float[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (float cell in value)
        {
            writer.WriteNumberValue(cell);
        }

        writer.WriteEndArray();
    }
}
