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

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Tests for <see cref="BrowserVerificationMode"/>, the policy deciding whether this process
/// refuses v2 subscriptions so a browser test can exercise the legacy path.
///
/// The cases that matter are the negative ones. A seam that forces every client onto the legacy
/// transport is a denial of the whole v2 stream, so what has to be established is not that it can
/// be switched on but that nothing a deployment does by accident switches it on: not the
/// environment alone, not the setting alone, and not any environment other than the dedicated
/// one.
/// </summary>
public class BrowserVerificationModeTests
{
    [Theory]
    [InlineData("Production", true, false)]
    [InlineData("Development", true, false)]
    [InlineData("BrowserVerification", false, false)]
    [InlineData("BrowserVerification", true, true)]
    public void RejectV2_Requires_Both_Environment_And_Flag(
        string environment, bool configured, bool expected)
    {
        BrowserVerificationMode.Resolve(environment, configured).RejectV2Subscriptions
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("browserverification-staging")]
    [InlineData("")]
    [InlineData(null)]
    public void No_Environment_But_The_Dedicated_One_Can_Enable_The_Seam(string? environment)
    {
        BrowserVerificationMode.Resolve(environment, configuredRejectV2: true).RejectV2Subscriptions
            .Should().BeFalse();
    }

    [Fact]
    public void Disabled_Is_The_Default_Instance()
    {
        BrowserVerificationMode.Disabled.RejectV2Subscriptions.Should().BeFalse();
        BrowserVerificationMode.Disabled.SuspendSceneRendering.Should().BeFalse();
    }

    [Theory]
    [InlineData("Production", true, false)]
    [InlineData("Development", true, false)]
    [InlineData("Staging", true, false)]
    [InlineData("browserverification-staging", true, false)]
    [InlineData("BrowserVerification", false, false)]
    [InlineData("BrowserVerification", true, true)]
    public void SuspendSceneRendering_Requires_Both_Environment_And_Flag(
        string environment, bool configured, bool expected)
    {
        // A server with this on serves a page that does not draw. That is a real reduction in what
        // a browser suite covers, so it is gated exactly as the v2 refusal is: the dedicated
        // environment AND its own setting, with neither sufficient alone.
        BrowserVerificationMode
            .Resolve(environment, configuredRejectV2: false, configuredSuspendSceneRendering: configured)
            .SuspendSceneRendering.Should().Be(expected);
    }

    [Fact]
    public void The_Two_Affordances_Are_Independent()
    {
        // The suite runs two servers that disagree about v2 and agree about rendering, so neither
        // setting may imply the other. A single combined flag would have made the forced-legacy
        // server the only one that stopped drawing — one slow spec among three fast ones, and the
        // hardest kind of difference to notice.
        var refusalOnly = BrowserVerificationMode.Resolve(
            BrowserVerificationMode.EnvironmentName,
            configuredRejectV2: true,
            configuredSuspendSceneRendering: false);

        refusalOnly.RejectV2Subscriptions.Should().BeTrue();
        refusalOnly.SuspendSceneRendering.Should().BeFalse();

        var suspensionOnly = BrowserVerificationMode.Resolve(
            BrowserVerificationMode.EnvironmentName,
            configuredRejectV2: false,
            configuredSuspendSceneRendering: true);

        suspensionOnly.RejectV2Subscriptions.Should().BeFalse();
        suspensionOnly.SuspendSceneRendering.Should().BeTrue();
    }

    [Fact]
    public void Resolve_Defaults_Suspension_Off_For_Callers_That_Do_Not_Mention_It()
    {
        // Every pre-existing caller passes two arguments. None of them may acquire a page that
        // stops drawing by having a parameter appear behind them.
        BrowserVerificationMode
            .Resolve(BrowserVerificationMode.EnvironmentName, configuredRejectV2: true)
            .SuspendSceneRendering.Should().BeFalse();
    }

    [Fact]
    public void Environment_Name_Matching_Is_Case_Insensitive()
    {
        // IHostEnvironment.IsEnvironment compares ordinal-ignore-case; the pure policy has to
        // agree with the host helper or the two entry points would disagree about one process.
        BrowserVerificationMode.Resolve("browserverification", configuredRejectV2: true)
            .RejectV2Subscriptions.Should().BeTrue();
    }

    [Fact]
    public void FromHost_Is_Disabled_When_Nothing_Is_Configured()
    {
        var mode = BrowserVerificationMode.FromHost(
            HostEnvironment(BrowserVerificationMode.EnvironmentName),
            new ConfigurationBuilder().Build());

        mode.RejectV2Subscriptions.Should().BeFalse();
        mode.SuspendSceneRendering.Should().BeFalse();
    }

    [Fact]
    public void FromHost_Reads_The_Suspension_Key_And_Only_In_The_Verification_Environment()
    {
        BrowserVerificationMode.FromHost(
                HostEnvironment(Environments.Production),
                Configuration((BrowserVerificationMode.SuspendSceneRenderingConfigurationKey, "true")))
            .SuspendSceneRendering.Should().BeFalse();

        BrowserVerificationMode.FromHost(
                HostEnvironment(BrowserVerificationMode.EnvironmentName),
                Configuration((BrowserVerificationMode.SuspendSceneRenderingConfigurationKey, "true")))
            .SuspendSceneRendering.Should().BeTrue();
    }

    [Fact]
    public void FromHost_Ignores_The_Setting_Outside_The_Verification_Environment()
    {
        var mode = BrowserVerificationMode.FromHost(
            HostEnvironment(Environments.Production),
            Configuration((BrowserVerificationMode.RejectV2ConfigurationKey, "true")));

        mode.RejectV2Subscriptions.Should().BeFalse();
    }

    [Fact]
    public void FromHost_Enables_Only_With_Both_The_Environment_And_The_Setting()
    {
        var mode = BrowserVerificationMode.FromHost(
            HostEnvironment(BrowserVerificationMode.EnvironmentName),
            Configuration((BrowserVerificationMode.RejectV2ConfigurationKey, "true")));

        mode.RejectV2Subscriptions.Should().BeTrue();
    }

    private static IHostEnvironment HostEnvironment(string environmentName) =>
        new HostingEnvironment { EnvironmentName = environmentName };

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    /// <summary>The smallest <see cref="IHostEnvironment"/> that answers an environment name.</summary>
    private sealed class HostingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "ResQ.Viz.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
