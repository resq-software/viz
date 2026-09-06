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
/// The one-tag vocabulary by which a <see cref="BrowserVerificationMode"/> server tells the page
/// to stop drawing its 3D scene, and the injection that puts that tag in the served HTML.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The browser suite's four specs are about the operator console's DOM:
/// the roster, the context panel, the rail's stacking order and hit targets, focus containment,
/// the legacy branch, and the DVR's retained heap. Not one of them asserts on a rendered pixel.
/// But <c>client/scene.ts</c> draws the whole terrain-and-water scene through a post-processing
/// chain on every animation frame regardless, and on a runner with no GPU that draw is software
/// rasterised. It then costs enough that it stops being the application's problem and becomes the
/// harness's: Playwright's own DOM snapshotter runs inside the page, so it queues behind the draw.
/// </para>
/// <para>
/// Measured in the traces of CI run 33862263781, at 1440x900: every <c>before@</c>/<c>after@</c>
/// snapshot cost 11.2–12.2 s, and the cost was flat against the work — a 407-byte snapshot took
/// 12.18 s and an 82,689-byte one took 12.10 s — while tracking viewport pixels almost exactly
/// (3.6 s at 390x844, a quarter of the pixels). That is contention, not DOM traversal. It put
/// 34–51% of every spec's wall clock inside instrumentation, in windows with no page operation in
/// flight, and it scaled with the number of Playwright calls a test reached — so each budget
/// increase bought more calls, each costing another ~23 s, and three rounds of raising timeouts
/// produced three failures at three different, later assertions. None of those assertions ever
/// returned an error; all four specs died of the clock.
/// </para>
/// <para>
/// <b>What suspension does.</b> Everything the console does still happens. The scene, the
/// renderer, the canvas, the terrain and every asset are still constructed; the animation frame
/// loop still runs and still drives every tick callback, the camera and the shadow frustum; layout
/// is untouched, so <c>#scene-container</c> keeps its real box and stacking order and
/// <c>elementFromPoint</c> answers exactly as it did. Precisely one thing is skipped: the draw at
/// the end of the frame — <c>DeferredPostFx.render</c> and the post-render callbacks that paint
/// the onboard picture-in-picture through the same renderer.
/// </para>
/// <para>
/// <b>What is therefore NOT covered while this is on — read this before trusting a green run.</b>
/// A server with this set verifies nothing at all about rendering. Shader compilation, draw-call
/// correctness, the post-processing chain, the picture-in-picture scissor view, WebGL context
/// loss, GPU-side memory, and anything whose only symptom is a wrong or missing pixel are all
/// invisible to a suite running against it. This is a DOM and transport suite in that mode, and
/// the honest name for the coverage it gives is "the console's markup, layout, focus, streaming
/// and retention", not "the console works". A rendering regression must be caught somewhere else.
/// </para>
/// <para>
/// <b>Why it is delivered as a meta tag.</b> The client needs the answer synchronously, before it
/// builds the scene, and the answer must come from the environment rather than from anything a
/// visitor controls. A meta tag in the served document is the only channel that is both: the
/// content security policy in <c>Program.cs</c> allows no inline script and no nonce, an extra
/// fetch would not have resolved before <c>new Scene(...)</c>, and a query string, cookie or
/// <c>localStorage</c> key would have been a switch a production page could be talked into. The
/// tag is added by the SPA fallback of a server already running under
/// <see cref="BrowserVerificationMode.EnvironmentName"/> with
/// <see cref="BrowserVerificationMode.SuspendSceneRenderingConfigurationKey"/> set, and by nothing
/// else — no deployment serves it, so no deployed page can read it.
/// </para>
/// </remarks>
public static class SceneRenderingSuspension
{
    /// <summary>Name of the marker meta tag. Must match <c>client/sceneRendering.ts</c>.</summary>
    public const string MetaName = "resq-scene-rendering";

    /// <summary>The one content value the client acts on. Must match <c>client/sceneRendering.ts</c>.</summary>
    public const string SuspendedValue = "suspended";

    /// <summary>The exact markup injected into the served document's head.</summary>
    public const string MetaTag = $"""<meta name="{MetaName}" content="{SuspendedValue}">""";

    /// <summary>Where the tag is inserted.</summary>
    private const string HeadClose = "</head>";

    /// <summary>Returns <paramref name="html"/> with <see cref="MetaTag"/> added to its head.</summary>
    /// <remarks>
    /// Idempotent: HTML that already carries the tag is returned unchanged, so re-serving a cached
    /// document cannot accumulate copies.
    /// <para>
    /// A document with no <c>&lt;/head&gt;</c> throws rather than being served as-is. Serving it
    /// unmarked would hand the suite a page that renders at full cost and fails four minutes later
    /// at an unrelated assertion, which is the exact failure mode this whole change exists to
    /// remove; a 500 on the very first navigation says what went wrong, once.
    /// </para>
    /// </remarks>
    /// <param name="html">The built <c>index.html</c>.</param>
    /// <returns>The marked document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document has no closing head tag.</exception>
    public static string Mark(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        if (html.Contains(MetaTag, StringComparison.Ordinal)) return html;

        var headClose = html.IndexOf(HeadClose, StringComparison.OrdinalIgnoreCase);
        if (headClose < 0)
        {
            throw new InvalidOperationException(
                $"Cannot suspend scene rendering: the served document has no '{HeadClose}' to "
                + $"add '{MetaTag}' to. The client reads that tag before it builds the scene, so "
                + "without it the browser suite would run against a fully rendering console and "
                + "fail on the clock instead of on a claim.");
        }

        return string.Concat(html.AsSpan(0, headClose), MetaTag, html.AsSpan(headClose));
    }
}
