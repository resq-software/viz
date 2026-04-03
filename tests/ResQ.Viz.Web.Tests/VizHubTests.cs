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
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using ResQ.Viz.Web.Hubs;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="VizHub"/> connection lifecycle.</summary>
public class VizHubTests
{
    private static (VizHub hub, Mock<ILogger<VizHub>> logger) CreateHub(string connectionId = "conn-test")
    {
        var mockLogger = new Mock<ILogger<VizHub>>();
        var hub = new VizHub(mockLogger.Object);

        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockCtx.Object;

        return (hub, mockLogger);
    }

    [Fact]
    public async Task OnConnectedAsync_Completes_Without_Exception()
    {
        var (hub, _) = CreateHub();
        var act = async () => await hub.OnConnectedAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnConnectedAsync_Logs_Information()
    {
        var (hub, mockLogger) = CreateHub("my-conn");
        await hub.OnConnectedAsync();

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("my-conn")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_Without_Exception_Completes()
    {
        var (hub, _) = CreateHub();
        var act = async () => await hub.OnDisconnectedAsync(null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnDisconnectedAsync_Without_Exception_Logs_Information()
    {
        var (hub, mockLogger) = CreateHub("disc-conn");
        await hub.OnDisconnectedAsync(null);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("disc-conn")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_With_Exception_Completes()
    {
        var (hub, _) = CreateHub();
        var act = async () => await hub.OnDisconnectedAsync(new InvalidOperationException("transport closed"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnDisconnectedAsync_With_Exception_Logs_Warning()
    {
        var (hub, mockLogger) = CreateHub("err-conn");
        await hub.OnDisconnectedAsync(new InvalidOperationException("transport closed"));

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("err-conn")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
