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
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Tests for <see cref="SceneRenderingSuspension"/>, the marker by which a browser-verification
/// server tells the page not to draw.
///
/// The interesting cases are the ones where the marker fails to land. The client reads the tag
/// once, before it builds the scene, and answers a missing tag by drawing — which is the right
/// default everywhere except here, where it silently restores the very cost the marker exists to
/// remove and turns a fast suite back into one that dies on the clock several minutes later at an
/// unrelated assertion. So an unmarkable document has to fail loudly and immediately instead.
/// </summary>
public class SceneRenderingSuspensionTests
{
    [Fact]
    public void Mark_Adds_The_Tag_Inside_The_Head()
    {
        var marked = SceneRenderingSuspension.Mark(
            "<!DOCTYPE html><html><head><title>t</title></head><body><div id=\"app\"></div></body></html>");

        marked.Should().Contain(SceneRenderingSuspension.MetaTag);
        marked.IndexOf(SceneRenderingSuspension.MetaTag, StringComparison.Ordinal)
            .Should().BeLessThan(marked.IndexOf("</head>", StringComparison.Ordinal));
    }

    [Fact]
    public void Mark_Preserves_Everything_Else()
    {
        const string html = "<!DOCTYPE html><html><head><title>t</title></head><body>body</body></html>";

        SceneRenderingSuspension.Mark(html).Replace(SceneRenderingSuspension.MetaTag, string.Empty)
            .Should().Be(html);
    }

    [Fact]
    public void Mark_Is_Idempotent()
    {
        // The handler caches the marked document, but a future caller that marks per response must
        // not be able to accumulate copies of the tag.
        var once = SceneRenderingSuspension.Mark("<html><head></head><body></body></html>");

        SceneRenderingSuspension.Mark(once).Should().Be(once);
    }

    [Theory]
    [InlineData("</HEAD>")]
    [InlineData("</Head>")]
    public void Mark_Finds_The_Head_Whatever_Its_Case(string headClose)
    {
        SceneRenderingSuspension.Mark($"<html><head>{headClose}<body></body></html>")
            .Should().Contain(SceneRenderingSuspension.MetaTag);
    }

    [Fact]
    public void Mark_Refuses_A_Document_It_Cannot_Mark()
    {
        // Serving this unmarked is the failure worth preventing: the suite would run against a
        // fully drawing console and report a timeout on whichever assertion happened to be in
        // flight, which is precisely the unreadable failure this whole seam removes.
        var act = () => SceneRenderingSuspension.Mark("<html><body>no head at all</body></html>");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*suspend scene rendering*");
    }

    [Fact]
    public void The_Tag_Is_The_Contract_The_Client_Reads()
    {
        // client/sceneRendering.ts declares these two strings independently and queries
        // `meta[name=...]` for that exact content. If either side is renamed without the other,
        // the page draws and nothing else fails — so the shape is pinned here.
        SceneRenderingSuspension.MetaName.Should().Be("resq-scene-rendering");
        SceneRenderingSuspension.SuspendedValue.Should().Be("suspended");
        SceneRenderingSuspension.MetaTag.Should()
            .Be("<meta name=\"resq-scene-rendering\" content=\"suspended\">");
    }
}
