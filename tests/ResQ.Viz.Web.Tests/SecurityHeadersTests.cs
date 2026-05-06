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

using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Integration tests asserting every security header from PR #46 lands on
/// real responses through the full ASP.NET Core middleware pipeline.
/// Drift in header wiring fails the suite rather than silently regressing.
/// </summary>
public sealed class SecurityHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityHeadersTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task RootResponse_SetsAllSecurityHeaders()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        var headers = response.Headers;

        headers.Should().ContainKey("X-Content-Type-Options")
            .WhoseValue.Should().Contain("nosniff");
        headers.Should().ContainKey("X-Frame-Options")
            .WhoseValue.Should().Contain("DENY");
        headers.Should().ContainKey("Referrer-Policy")
            .WhoseValue.Should().Contain("strict-origin-when-cross-origin");
        headers.Should().ContainKey("Cross-Origin-Opener-Policy")
            .WhoseValue.Should().Contain("same-origin");
        headers.Should().ContainKey("Cross-Origin-Resource-Policy")
            .WhoseValue.Should().Contain("same-site");
        headers.Should().ContainKey("Permissions-Policy");
        headers.Should().ContainKey("Content-Security-Policy");
    }

    [Fact]
    public async Task CspIncludesAllTightenedDirectives()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        // Assert the header is present first so a missing header surfaces as
        // "expected ContainKey" rather than InvalidOperationException from
        // GetValues on an absent key.
        response.Headers.Should().ContainKey("Content-Security-Policy");
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        csp.Should().Contain("default-src 'self'");
        csp.Should().Contain("script-src 'self'");
        csp.Should().Contain("connect-src 'self' ws: wss:");
        csp.Should().Contain("img-src 'self' data:");
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("base-uri 'self'");
        csp.Should().Contain("form-action 'self'");
        csp.Should().Contain("object-src 'none'");
    }

    /// <summary>
    /// Regression guard for the analytics integration (PR #97). The CSP must
    /// allow the script + connect endpoints used by `@resq-sw/analytics`
    /// (PostHog, Google Analytics 4) and the Cloudflare Web Analytics beacon
    /// auto-injected by the edge proxy. Prior to this guard, the SPA crashed
    /// at runtime with `script-src 'self'` blocks for posthog-js, gtag, and
    /// the Cloudflare bootstrap inline script.
    /// </summary>
    [Fact]
    public async Task CspAllowsAnalyticsOrigins()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        response.Headers.Should().ContainKey("Content-Security-Policy");
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        // Cloudflare Web Analytics: script + bootstrap inline-script hashes +
        // beacon. Both the apex and the wildcard are required — CSP wildcards
        // don't match the base domain that beacon ingest posts to. Multiple
        // hashes are listed because Cloudflare rotates the bootstrap inline
        // script; each variant we've observed in production must remain
        // allow-listed until auto-inject is disabled in the CF dashboard.
        csp.Should().Contain("'sha256-ZlBaXTgBboiytLHGbGnTgT67kpRdxavJqMHVBSTxRaE='");
        csp.Should().Contain("'sha256-XtmvLUr10hivmkrNKCgQcNREHptkg6tWqm9iZo3mlAc='");
        csp.Should().Contain("https://static.cloudflareinsights.com");
        csp.Should().Contain("https://cloudflareinsights.com");
        csp.Should().Contain("https://*.cloudflareinsights.com");

        // PostHog (US cloud): config script + event ingest.
        csp.Should().Contain("https://us-assets.i.posthog.com");
        csp.Should().Contain("https://us.i.posthog.com");

        // Google Analytics 4: gtag.js + collect endpoints + tagging pixel.
        // GA4 uses regional subdomains (region1.google-analytics.com etc.),
        // so the connect/img sources must wildcard the analytics domain.
        csp.Should().Contain("https://www.googletagmanager.com");
        csp.Should().Contain("https://*.google-analytics.com");
        csp.Should().Contain("https://*.analytics.google.com");
    }

    [Fact]
    public async Task PermissionsPolicyDeniesSensitiveFeatures()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        response.Headers.Should().ContainKey("Permissions-Policy");
        var pp = string.Join(",", response.Headers.GetValues("Permissions-Policy"));

        // Features that should be locked out entirely.
        pp.Should().Contain("camera=()");
        pp.Should().Contain("microphone=()");
        pp.Should().Contain("geolocation=()");
        pp.Should().Contain("payment=()");
        pp.Should().Contain("usb=()");

        // Features the viz explicitly keeps available to itself.
        pp.Should().Contain("fullscreen=(self)");

        // Tokens removed because Chromium logs "Unrecognized feature" for
        // them — they pollute the console without adding any real protection.
        // Adding any of them back must be a deliberate decision, not an
        // accidental copy-paste from an outdated OWASP cheat sheet.
        pp.Should().NotContain("ambient-light-sensor");
        pp.Should().NotContain("battery=");
        pp.Should().NotContain("document-domain");
        pp.Should().NotContain("web-share");
    }

    [Fact]
    public async Task ApiResponsesSetCacheControlNoStore()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/sim/scenarios");

        response.Headers.Should().ContainKey("Cache-Control")
            .WhoseValue.Should().Contain("no-store");
    }

    /// <summary>
    /// Regression guard for the middleware-ordering bug caught in PR #46
    /// review: security headers must apply to responses that short-circuit
    /// the pipeline before the terminal handler runs (static files, 404s,
    /// rate-limit rejections). The previous placement — after UseStaticFiles
    /// + UseRateLimiter — silently skipped those responses. `OnStarting`
    /// registered at the top of the pipeline fixes both.
    /// </summary>
    [Fact]
    public async Task NonRootPathStillReceivesSecurityHeaders()
    {
        using var client = _factory.CreateClient();
        // A path that doesn't match any controller or static file — exercises
        // the terminal-404 path through the middleware chain.
        var response = await client.GetAsync("/nonexistent-asset-for-header-coverage.js");

        response.Headers.Should().ContainKey("X-Content-Type-Options")
            .WhoseValue.Should().Contain("nosniff");
        response.Headers.Should().ContainKey("Content-Security-Policy");
        response.Headers.Should().ContainKey("Cross-Origin-Resource-Policy");
    }
}
