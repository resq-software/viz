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
/// The two things this process may do differently so a browser test can observe something a DOM
/// emulator cannot: refuse v2 subscriptions (forcing the client's legacy branch), and tell the
/// page to suspend its 3D render loop.
/// </summary>
/// <remarks>
/// The client's legacy fallback only runs when the server declines v2, and a fallback nothing
/// exercises is a fallback nobody knows is broken. Rather than delete the v2 path from a test
/// build — which would verify a binary no deployment ever runs — this seam makes one running
/// server refuse the v2 opt-in, so the browser test drives the same code production ships.
/// <para>
/// <b>What turns either of them on.</b> Both of these, together, and nothing else:
/// </para>
/// <list type="number">
///   <item><description>
///     the host environment is exactly <see cref="EnvironmentName"/> — that is
///     <c>ASPNETCORE_ENVIRONMENT=BrowserVerification</c>, an environment name this repository uses
///     for nothing else and that no deployment profile sets; and
///   </description></item>
///   <item><description>
///     that affordance's own configuration key — <see cref="RejectV2ConfigurationKey"/> or
///     <see cref="SuspendSceneRenderingConfigurationKey"/> — is true.
///   </description></item>
/// </list>
/// <para>
/// The two keys are read independently and neither implies the other, so the suite can run one
/// server that refuses v2 and one that does not while both suspend rendering.
/// </para>
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
/// <see cref="RejectV2Subscriptions"/> and takes no other action, the SPA fallback serves the
/// built <c>index.html</c> byte for byte, and every path therefore runs the code it ran before
/// this type existed.
/// </para>
/// <para>
/// <b>What <see cref="RejectV2Subscriptions"/> does when on.</b> One thing: a positive
/// <c>SubscribeSnapshots(true)</c> or <c>SubscribeDeltas(true)</c> is refused with a
/// <c>HubException</c> before any group or subscriber-count change. It does not stop the server
/// building v2 snapshots, does not touch the v1 frame stream, does not affect the REST API, and
/// does not refuse the negative <c>(false)</c> calls — a client that had subscribed before the
/// seam was switched on can still opt back out.
/// </para>
/// <para>
/// <b>What <see cref="SuspendSceneRendering"/> does when on.</b> One thing: the SPA fallback
/// serves <c>index.html</c> with <see cref="SceneRenderingSuspension.MetaTag"/> added to its
/// head, which the client reads once at startup and answers by skipping the WebGL draw at the
/// end of each animation frame. See <see cref="SceneRenderingSuspension"/> for why that exists
/// and, more importantly, for what it stops covering.
/// </para>
/// </remarks>
public sealed class BrowserVerificationMode
{
    /// <summary>The one host environment in which the seam can be enabled.</summary>
    public const string EnvironmentName = "BrowserVerification";

    /// <summary>Configuration key that, in that environment alone, enables the refusal.</summary>
    public const string RejectV2ConfigurationKey = "BrowserVerification:RejectV2Subscriptions";

    /// <summary>
    /// Configuration key that, in that environment alone, tells the page to stop drawing.
    /// </summary>
    public const string SuspendSceneRenderingConfigurationKey =
        "BrowserVerification:SuspendSceneRendering";

    private BrowserVerificationMode(bool rejectV2Subscriptions, bool suspendSceneRendering)
    {
        RejectV2Subscriptions = rejectV2Subscriptions;
        SuspendSceneRendering = suspendSceneRendering;
    }

    /// <summary>The off position: the mode every ordinary build and every test construction gets.</summary>
    /// <remarks>
    /// A singleton rather than a factory so "is this the default?" is reference equality at a
    /// debugger, and so the disabled instance cannot accumulate per-call state.
    /// </remarks>
    public static BrowserVerificationMode Disabled { get; } =
        new(rejectV2Subscriptions: false, suspendSceneRendering: false);

    /// <summary>
    /// Whether <c>SubscribeSnapshots(true)</c> and <c>SubscribeDeltas(true)</c> are refused.
    /// </summary>
    public bool RejectV2Subscriptions { get; }

    /// <summary>
    /// Whether the SPA fallback marks its HTML so the client suspends the scene's WebGL draw.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="RejectV2Subscriptions"/>: the browser suite runs two servers that
    /// disagree about v2 and agree about this.
    /// </remarks>
    public bool SuspendSceneRendering { get; }

    /// <summary>Applies the policy to an environment name and the resolved settings.</summary>
    /// <remarks>
    /// Split out from <see cref="FromHost"/> and kept free of <see cref="IConfiguration"/> so the
    /// decision itself can be tested as a truth table. The comparison is ordinal-ignore-case to
    /// match <see cref="HostEnvironmentEnvExtensions.IsEnvironment"/>, which is what
    /// <see cref="FromHost"/> would otherwise use: the two entry points must not be able to
    /// disagree about one process.
    /// <para>
    /// The environment test is applied once, to both settings, rather than once per setting. That
    /// is the property worth keeping as affordances are added here: there is exactly one place a
    /// future affordance can be made reachable outside <see cref="EnvironmentName"/>, and it is
    /// this line.
    /// </para>
    /// </remarks>
    /// <param name="environmentName">Host environment name, as <c>ASPNETCORE_ENVIRONMENT</c> set it.</param>
    /// <param name="configuredRejectV2">Value of <see cref="RejectV2ConfigurationKey"/>.</param>
    /// <param name="configuredSuspendSceneRendering">
    /// Value of <see cref="SuspendSceneRenderingConfigurationKey"/>. Defaults to false so a caller
    /// that only cares about the v2 refusal — every existing one — reads unchanged.
    /// </param>
    /// <returns><see cref="Disabled"/> unless the environment matches and a setting is set.</returns>
    public static BrowserVerificationMode Resolve(
        string? environmentName,
        bool configuredRejectV2,
        bool configuredSuspendSceneRendering = false)
    {
        var inVerificationEnvironment =
            string.Equals(environmentName, EnvironmentName, StringComparison.OrdinalIgnoreCase);

        var rejectV2 = inVerificationEnvironment && configuredRejectV2;
        var suspendRendering = inVerificationEnvironment && configuredSuspendSceneRendering;

        return rejectV2 || suspendRendering
            ? new BrowserVerificationMode(rejectV2, suspendRendering)
            : Disabled;
    }

    /// <summary>Resolves the mode for a running host.</summary>
    /// <remarks>
    /// Called once, at registration time in <c>Program.cs</c>. Reading the setting outside the
    /// verification environment is deliberate and harmless: <see cref="Resolve"/> discards it, and
    /// reading it unconditionally keeps the "environment gates the setting" rule in one place
    /// rather than duplicated as an early return here.
    /// </remarks>
    /// <param name="environment">The host environment.</param>
    /// <param name="configuration">Configuration to read this type's keys from.</param>
    /// <returns>The mode this process runs under.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static BrowserVerificationMode FromHost(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        return Resolve(
            environment.EnvironmentName,
            configuration.GetValue<bool>(RejectV2ConfigurationKey, defaultValue: false),
            configuration.GetValue<bool>(SuspendSceneRenderingConfigurationKey, defaultValue: false));
    }
}
