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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ResQ.Viz.Web.Models;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// A body the v2 surface cannot deserialise must be refused, not thrown.
/// </summary>
/// <remarks>
/// The v2 command contract promises that an unacceptable request is answered with a status and a
/// machine-readable code and leaves the simulation untouched. A body that fails to bind never
/// reaches the validator that makes that promise, so without an explicit translation it escapes
/// as an unhandled exception and the caller gets a bodyless 500 — indistinguishable, from the
/// client's side, from the server having fallen over mid-command. Two shapes reach the model
/// binder by different routes and only one of them was ever handled. Syntactically broken JSON
/// raises <see cref="System.Text.Json.JsonException"/>, which the input formatter already
/// converts into a model-state error and thus a framework 400. A polymorphic member missing its
/// type discriminator raises <see cref="NotSupportedException"/> instead, which the formatter
/// does not convert — so it escaped as a 500. Both routes are asserted here, because the
/// invariant worth pinning is the one that spans them: a body the caller got wrong is never
/// answered with a 5xx. Only the second route is answered by this repository's own filter, and
/// only that one asserts the specific code.
/// </remarks>
public sealed class MalformedPayloadTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    /// <summary>Binds the shared in-memory host.</summary>
    /// <param name="factory">Host factory supplied by xUnit.</param>
    public MalformedPayloadTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>A command target without its <c>type</c> discriminator is a 400, not a 500.</summary>
    [Fact]
    public async Task CommandTargetWithoutDiscriminator_IsRejectedAsBadRequest()
    {
        using var client = await SessionClientAsync();

        var response = await PostRawAsync(
            client,
            "/api/v2/sim/assets/uav-1/commands",
            """
            {"kind":"goTo","idempotencyKey":"k1","issuerId":"test","frame":2,
             "target":{"point":{"frame":2,"position":{"x":1,"y":2,"z":3}}}}
            """);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a target the binder cannot resolve is the caller's mistake, not the server's");

        var problem = await response.Content.ReadFromJsonAsync<CommandProblemDetails>();
        problem!.Code.Should().Be(CommandRejectionReasons.PayloadMalformed);
        problem.Detail.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Syntactically broken JSON is a 400 too — the formatter's own path, pinned here.</summary>
    [Fact]
    public async Task UnparseableBody_IsRejectedAsBadRequest()
    {
        using var client = await SessionClientAsync();

        var response = await PostRawAsync(
            client, "/api/v2/sim/assets/uav-1/commands", "{\"kind\":\"goTo\",");

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest, "unreadable JSON is the caller's mistake");
    }

    /// <summary>A malformed spawn body is refused rather than throwing.</summary>
    [Fact]
    public async Task MalformedSpawnBody_IsRejectedAsBadRequest()
    {
        using var client = await SessionClientAsync();

        var response = await PostRawAsync(
            client, "/api/v2/sim/assets", "{\"vehicleClass\":1,\"pose\":{\"position\":\"nope\"}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>No malformed body on this surface may ever be answered with a server error.</summary>
    /// <remarks>
    /// The regression this locks down: before the filter existed, the discriminator case below
    /// returned a bodyless 500, which tells a client to retry a request that will never succeed.
    /// </remarks>
    [Theory]
    [InlineData("{\"kind\":\"goTo\",")]
    [InlineData("{\"kind\":\"goTo\",\"target\":{\"point\":{\"frame\":2}}}")]
    [InlineData("{\"kind\":\"goTo\",\"target\":{\"type\":\"nonesuch\"}}")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    public async Task NoMalformedBody_ProducesAServerError(string body)
    {
        using var client = await SessionClientAsync();

        var response = await PostRawAsync(client, "/api/v2/sim/assets/uav-1/commands", body);

        ((int)response.StatusCode).Should().BeLessThan(
            500, "a body the caller got wrong is never the server's fault");
    }

    /// <summary>Refusing a malformed body must not disturb the session's asset inventory.</summary>
    [Fact]
    public async Task MalformedBody_LeavesTheSessionUntouched()
    {
        using var client = await SessionClientAsync();

        var before = await client.GetStringAsync("/api/v2/sim/assets");
        await PostRawAsync(
            client,
            "/api/v2/sim/assets/uav-1/commands",
            """{"kind":"goTo","idempotencyKey":"k2","issuerId":"t","frame":2,"target":{"point":{}}}""");
        var after = await client.GetStringAsync("/api/v2/sim/assets");

        // The tick counter advances on its own; the asset roster is what must not move.
        Descriptors(before).Should().Be(Descriptors(after));
    }

    private static string Descriptors(string inventoryJson)
    {
        const string key = "\"descriptors\":";
        var start = inventoryJson.IndexOf(key, StringComparison.Ordinal);
        var end = inventoryJson.IndexOf("\"assets\":", StringComparison.Ordinal);
        return start < 0 || end < 0 ? inventoryJson : inventoryJson[start..end];
    }

    private static Task<HttpResponseMessage> PostRawAsync(
        HttpClient client, string route, string body)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return client.PostAsync(route, content);
    }

    private async Task<HttpClient> SessionClientAsync()
    {
        // The session cookie is issued `secure`, so the handler will only replay it over an
        // https base address. The in-memory server does no real TLS; the scheme is what matters.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });

        var session = await client.PostAsync("/api/sim/session", content: null);
        session.EnsureSuccessStatusCode();
        return client;
    }
}
