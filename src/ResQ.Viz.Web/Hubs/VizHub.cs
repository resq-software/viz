// Copyright 2024 ResQ Technologies Ltd.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

using Microsoft.AspNetCore.SignalR;

namespace ResQ.Viz.Web.Hubs;

/// <summary>
/// SignalR hub for streaming VizFrames to connected browser clients.
/// </summary>
public sealed class VizHub : Hub { }
