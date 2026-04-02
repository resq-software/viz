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

using System.Numerics;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="SimulationService"/>.</summary>
public class SimulationServiceTests
{
    private static SimulationService CreateService()
    {
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockClientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockHubContext = new Mock<IHubContext<VizHub>>();
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        return new SimulationService(mockHubContext.Object, new VizFrameBuilder());
    }

    [Fact]
    public void SimulationService_Creates_SimulationWorld()
    {
        // Simply constructing should not throw.
        var act = () => CreateService();
        act.Should().NotThrow();
    }

    [Fact]
    public void GetSnapshot_Returns_Empty_When_No_Drones()
    {
        var svc = CreateService();
        var snapshot = svc.GetSnapshot();
        snapshot.Should().BeEmpty();
    }

    [Fact]
    public void AddDrone_Adds_Drone_To_World()
    {
        var svc = CreateService();
        svc.AddDrone("drone-1", new Vector3(10f, 0f, 20f));

        var snapshot = svc.GetSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Id.Should().Be("drone-1");
        snapshot[0].Position.Should().HaveCount(3);
        snapshot[0].Position[0].Should().BeApproximately(10f, 0.001f);
        snapshot[0].Position[2].Should().BeApproximately(20f, 0.001f);
    }

    [Fact]
    public void Step_Advances_Simulation()
    {
        var svc = CreateService();
        svc.AddDrone("drone-2", new Vector3(0f, 100f, 0f));

        // Should complete without throwing.
        var act = () => svc.StepOnce();
        act.Should().NotThrow();

        // Snapshot still contains the drone after stepping.
        var snapshot = svc.GetSnapshot();
        snapshot.Should().HaveCount(1);
    }
}
