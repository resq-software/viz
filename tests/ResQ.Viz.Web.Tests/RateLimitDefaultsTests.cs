// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 ResQ Systems, Inc.

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

        // ...and the default the code falls back to is still the shipped number.
        configuration.GetValue(key, shipped).Should().Be(shipped);
    }
}
