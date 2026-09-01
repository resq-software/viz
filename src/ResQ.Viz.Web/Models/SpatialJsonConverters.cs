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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResQ.Viz.Web.Models;

/// <summary>
/// Writes a <see cref="Vector3"/> as <c>{ "x": .., "y": .., "z": .. }</c> and reads it back.
/// </summary>
/// <remarks>
/// <see cref="System.Text.Json"/> has no built-in handling for <see cref="Vector3"/>: its
/// components are public <i>fields</i>, and field serialisation is off by default, so an
/// unconverted vector goes over the wire as the empty object <c>{}</c> and every position,
/// velocity and rate silently arrives as the origin. Because that failure is invisible — the
/// payload is still valid JSON and still deserialises, just to zero — the converter is attached
/// by attribute on each property rather than registered on one options instance, so it applies
/// to MVC, to the SignalR hub protocol and to any ad-hoc <see cref="JsonSerializer"/> call
/// alike.
/// <para>
/// Named components rather than a three-element array, because an array is exactly the
/// frame-less <c>[x, y, z]</c> the v2 contract exists to eliminate: a reader that guesses at
/// index 1 cannot be told apart from one that guesses right.
/// </para>
/// <para>
/// A converter only ever runs for a property that is <b>present</b>, so this one cannot see an
/// omitted coordinate: <c>{ "frame": 2 }</c> alone would bind the scene origin without the
/// converter being asked anything. Presence is therefore enforced at the declaring member —
/// <see cref="JsonRequiredAttribute"/> on <see cref="FramedPose.Position"/>,
/// <see cref="FramedTwist.Linear"/> and <see cref="FramedTwist.Angular"/> — and the two guards
/// together are what make "a partial coordinate is rejected rather than defaulted" true of a
/// wholly absent coordinate as well as a half-written one.
/// </para>
/// </remarks>
public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    /// <inheritdoc />
    /// <exception cref="JsonException">
    /// The value is not an object, or any of <c>x</c>, <c>y</c> or <c>z</c> is missing. A
    /// partial coordinate is rejected rather than defaulted, because a defaulted component is
    /// indistinguishable from a real zero.
    /// </exception>
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"Expected an object with x, y and z for {nameof(Vector3)}, found {reader.TokenType}.");
        }

        float? x = null;
        float? y = null;
        float? z = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();

            switch (name.ToLowerInvariant())
            {
                case "x": x = reader.GetSingle(); break;
                case "y": y = reader.GetSingle(); break;
                case "z": z = reader.GetSingle(); break;
                default: reader.Skip(); break;
            }
        }

        if (x is null || y is null || z is null)
        {
            throw new JsonException($"A {nameof(Vector3)} needs all of x, y and z.");
        }

        return new Vector3(x.Value, y.Value, z.Value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("z", value.Z);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Writes a <see cref="Quaternion"/> as <c>{ "x": .., "y": .., "z": .., "w": .. }</c> and reads
/// it back.
/// </summary>
/// <remarks>
/// Exists for the same reason as <see cref="Vector3JsonConverter"/>, but the unconverted
/// failure is worse: <see cref="Quaternion"/> exposes a public <c>IsIdentity</c> property, so
/// an attitude serialises as <c>{"isIdentity":false}</c> — a payload that looks deliberate
/// while carrying no rotation at all.
/// <para>
/// The sign of the components is preserved rather than canonicalised, because <c>q</c> and
/// <c>-q</c> are the same rotation and a consumer must compare rotations by the basis vectors
/// they produce, never component-wise.
/// </para>
/// <para>
/// As with <see cref="Vector3JsonConverter"/>, this converter never runs for an <i>absent</i>
/// property, and unlike a position an absent orientation is not marked required — see
/// <see cref="FramedPose.Orientation"/> for why. An omitted attitude binds the all-zero
/// quaternion, which is not a rotation, so it is refused wherever a rotation is needed and read
/// as "undeclared" where a heading is merely optional. What this converter rules out is the
/// worse case: a rotation that was written down and is quietly wrong.
/// </para>
/// </remarks>
public sealed class QuaternionJsonConverter : JsonConverter<Quaternion>
{
    /// <inheritdoc />
    /// <exception cref="JsonException">
    /// The value is not an object, or any of <c>x</c>, <c>y</c>, <c>z</c> or <c>w</c> is
    /// missing. In particular a missing <c>w</c> is not defaulted to 1: a silently-identity
    /// attitude is the bug this converter exists to prevent.
    /// </exception>
    public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"Expected an object with x, y, z and w for {nameof(Quaternion)}, found {reader.TokenType}.");
        }

        float? x = null;
        float? y = null;
        float? z = null;
        float? w = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();

            switch (name.ToLowerInvariant())
            {
                case "x": x = reader.GetSingle(); break;
                case "y": y = reader.GetSingle(); break;
                case "z": z = reader.GetSingle(); break;
                case "w": w = reader.GetSingle(); break;
                default: reader.Skip(); break;
            }
        }

        if (x is null || y is null || z is null || w is null)
        {
            throw new JsonException($"A {nameof(Quaternion)} needs all of x, y, z and w.");
        }

        return new Quaternion(x.Value, y.Value, z.Value, w.Value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("z", value.Z);
        writer.WriteNumber("w", value.W);
        writer.WriteEndObject();
    }
}
