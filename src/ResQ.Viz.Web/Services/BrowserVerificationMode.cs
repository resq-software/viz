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

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Whether this process refuses v2 snapshot and delta subscriptions so a browser test can watch
/// the client fall back to the legacy v1 frame stream.
/// </summary>
/// <remarks>
/// The client's legacy fallback only runs when the server declines v2, and a fallback nothing
/// exercises is a fallback nobody knows is broken. Rather than delete the v2 path from a test
/// build — which would verify a binary no deployment ever runs — this seam makes one running
/// server refuse the v2 opt-in, so the browser test drives the same code production ships.
/// <para>
/// <b>What turns it on.</b> Both of these, together, and nothing else:
/// </para>
/// <list type="number">
///   <item><description>
///     the host environment is exactly <see cref="EnvironmentName"/> — that is
///     <c>ASPNETCORE_ENVIRONMENT=BrowserVerification</c>, an environment name this repository uses
///     for nothing else and that no deployment profile sets; and
///   </description></item>
///   <item><description>
///     configuration key <see cref="RejectV2ConfigurationKey"/> is true.
///   </description></item>
/// </list>
/// <para>
/// <b>What does NOT turn it on.</b> Neither condition alone. No request-scoped input of any kind:
/// there is no query string, header, cookie, or hub argument that reaches this decision, because
/// the mode is resolved once at startup from the host environment and handed to the hub as a
/// singleton. Running under <c>Production</c> or <c>Development</c> with the setting present
/// leaves it off — the setting is read but its answer is discarded, which is the case
/// <c>BrowserVerificationModeTests</c> pins. And a near-miss environment name does not count:
/// matching is ordinal-ignore-case equality, not a prefix or substring test, so
/// <c>BrowserVerification-Staging</c> is a different environment.
/// </para>
/// <para>
/// <b>What it does when off.</b> Nothing whatsoever. <see cref="Disabled"/> is what
/// <see cref="Hubs.VizHub"/> holds unless a mode is passed, the hub consults
/// <see cref="RejectV2Subscriptions"/> and takes no other action, and every subscription path
/// therefore runs the code it ran before this type existed.
/// </para>
/// <para>
/// <b>What it does when on.</b> One thing: a positive <c>SubscribeSnapshots(true)</c> or
/// <c>SubscribeDeltas(true)</c> is refused with a <c>HubException</c> before any group or
/// subscriber-count change. It does not stop the server building v2 snapshots, does not touch the
/// v1 frame stream, does not affect the REST API, and does not refuse the negative
/// <c>(false)</c> calls — a client that had subscribed before the seam was switched on can still
/// opt back out.
/// </para>
/// </remarks>
public sealed class BrowserVerificationMode
{
    /// <summary>The one host environment in which the seam can be enabled.</summary>
    public const string EnvironmentName = "BrowserVerification";

    /// <summary>Configuration key that, in that environment alone, enables the refusal.</summary>
    public const string RejectV2ConfigurationKey = "BrowserVerification:RejectV2Subscriptions";

    private BrowserVerificationMode(bool rejectV2Subscriptions) =>
        RejectV2Subscriptions = rejectV2Subscriptions;

    /// <summary>The off position: the mode every ordinary build and every test construction gets.</summary>
    /// <remarks>
    /// A singleton rather than a factory so "is this the default?" is reference equality at a
    /// debugger, and so the disabled instance cannot accumulate per-call state.
    /// </remarks>
    public static BrowserVerificationMode Disabled { get; } = new(rejectV2Subscriptions: false);

    /// <summary>
    /// Whether <c>SubscribeSnapshots(true)</c> and <c>SubscribeDeltas(true)</c> are refused.
    /// </summary>
    public bool RejectV2Subscriptions { get; }

    /// <summary>Applies the policy to an environment name and the resolved setting.</summary>
    /// <remarks>
    /// Split out from <see cref="FromHost"/> and kept free of <see cref="IConfiguration"/> so the
    /// decision itself can be tested as a truth table. The comparison is ordinal-ignore-case to
    /// match <see cref="HostEnvironmentEnvExtensions.IsEnvironment"/>, which is what
    /// <see cref="FromHost"/> would otherwise use: the two entry points must not be able to
    /// disagree about one process.
    /// </remarks>
    /// <param name="environmentName">Host environment name, as <c>ASPNETCORE_ENVIRONMENT</c> set it.</param>
    /// <param name="configuredRejectV2">Value of <see cref="RejectV2ConfigurationKey"/>.</param>
    /// <returns><see cref="Disabled"/> unless both conditions hold.</returns>
    public static BrowserVerificationMode Resolve(string? environmentName, bool configuredRejectV2)
    {
        var enabled = configuredRejectV2
            && string.Equals(environmentName, EnvironmentName, StringComparison.OrdinalIgnoreCase);

        return enabled ? new BrowserVerificationMode(rejectV2Subscriptions: true) : Disabled;
    }

    /// <summary>Resolves the mode for a running host.</summary>
    /// <remarks>
    /// Called once, at registration time in <c>Program.cs</c>. Reading the setting outside the
    /// verification environment is deliberate and harmless: <see cref="Resolve"/> discards it, and
    /// reading it unconditionally keeps the "environment gates the setting" rule in one place
    /// rather than duplicated as an early return here.
    /// </remarks>
    /// <param name="environment">The host environment.</param>
    /// <param name="configuration">Configuration to read <see cref="RejectV2ConfigurationKey"/> from.</param>
    /// <returns>The mode this process runs under.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static BrowserVerificationMode FromHost(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        return Resolve(
            environment.EnvironmentName,
            configuration.GetValue<bool>(RejectV2ConfigurationKey, defaultValue: false));
    }
}
