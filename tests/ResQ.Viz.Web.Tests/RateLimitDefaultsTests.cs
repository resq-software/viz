// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 ResQ Systems, Inc.

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ResQ.Viz.Web;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The shipped rate-limit budgets are what a deployment gets when it says nothing.
/// </summary>
/// <remarks>
/// These became configuration so the browser suite could raise them for itself: it drives three
/// consoles against one server process inside a minute, and booting a console spends destructive
/// permits on the scenario start and the terrain fetch. Measured, the first two specs left exactly
/// one permit, so the third console's scenario start returned 429 and it rendered an empty room
/// while the connection stayed up — which reads as a console bug and is not one.
/// <para>
/// A configuration knob added to make a test pass is one step from a production budget quietly
/// raised to make a test pass. That is what this guards. The defaults live in <c>Program.cs</c>
/// beside the reasoning for them, and no <c>appsettings</c> layer may set the keys — so relaxing
/// production takes a deliberate edit that fails this test, rather than a line nobody reads in a
/// JSON file.
/// </para>
/// </remarks>
public sealed class RateLimitDefaultsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RateLimitDefaultsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData("RateLimits:DestructivePermitsPerMinute", 10)]
    [InlineData("RateLimits:GeneralPermitsPerMinute", 60)]
    public void ShippedBudgets_AreUnset_SoTheCodeDefaultApplies(string key, int shipped)
    {
        using var scope = _factory.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Probed with a sentinel no real budget equals, so an ABSENT key is distinguishable from
        // one configured to the same number. Asserting the effective value alone would pass just
        // as happily against an appsettings entry that had pinned it.
        const int sentinel = -1;
        configuration.GetValue(key, sentinel).Should().Be(sentinel,
            $"{key} must not be set by any appsettings layer — the default belongs in Program.cs "
            + "where the reasoning for it lives, and a JSON override is precisely how a production "
            + "budget gets raised to make a test pass");

        // The shipped number itself is asserted by driving the limiter, not by reading it back
        // out of configuration — see DestructiveBudget_Is_Ten_Calls_Per_Minute below. Line 48
        // has already proved the key is absent, and GetValue returns its default verbatim for an
        // absent key, so `GetValue(key, shipped).Should().Be(shipped)` reduced to
        // `shipped == shipped`. It read as coverage of the Program.cs fallback and was a
        // comparison of the theory row against itself: raising the real default from 10 to 10000
        // left it green.
        _ = shipped;
    }

    /// <summary>
    /// Drives the destructive limiter until it rejects, pinning the shipped budget of ten.
    /// </summary>
    /// <remarks>
    /// Behavioural because there is no honest way to read the effective limit back: the value
    /// lives in a delegate inside <c>RateLimiterOptions</c>, and every attempt to assert it from
    /// configuration ends up comparing a literal to itself.
    /// <para>
    /// A private factory rather than the class fixture. Program.cs documents these windows as
    /// GLOBAL rather than per-caller, so permits consumed by any other test sharing a server
    /// would be carried into this one and the counts would depend on test order.
    /// </para>
    /// <para>
    /// Only 429-or-not is asserted. The limiter rejects before the action runs, so whether the
    /// endpoint itself would answer 200 or 503 in a test host is beside the point and asserting
    /// it would make this brittle for no gain.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DestructiveBudget_Is_Ten_Calls_Per_Minute()
    {
        const int shippedBudget = 10;

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var codes = new List<int>();
        for (int i = 0; i <= shippedBudget; i++)
        {
            using var response = await client.PostAsync(
                "/api/sim/session", new StringContent(string.Empty));
            codes.Add((int)response.StatusCode);
        }

        codes.Take(shippedBudget).Should().NotContain(StatusCodes.Status429TooManyRequests,
            $"the first {shippedBudget} destructive calls are within the shipped budget");
        codes[shippedBudget].Should().Be(StatusCodes.Status429TooManyRequests,
            $"call {shippedBudget + 1} exceeds a {shippedBudget}-per-minute window, and this is "
            + "the only thing in the suite that pins that number to the one in Program.cs");
    }
}
