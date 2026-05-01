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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Filters;

/// <summary>
/// Action filter that enforces a valid <see cref="RoomSession"/> cookie on every
/// hit, looks up the live <see cref="SimulationRoom"/>, and stashes it on
/// <see cref="HttpContext.Items"/> for the action method to retrieve. Missing
/// or invalid cookies produce <c>401 { redirect: "/" }</c> and clear the cookie
/// so the client requests a fresh session before retrying.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireRoomAttribute : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>Key under which the resolved <see cref="SimulationRoom"/> is stored in <see cref="HttpContext.Items"/>.</summary>
    public const string RoomItemKey = "sim.room";

    /// <inheritdoc/>
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var sessions = context.HttpContext.RequestServices.GetRequiredService<RoomSessionService>();
        var ip = context.HttpContext.Connection.RemoteIpAddress;
        var cookie = context.HttpContext.Request.Cookies[RoomSessionService.CookieName];

        if (sessions.TryValidate(cookie, ip, out _, out var room) && room is not null)
        {
            context.HttpContext.Items[RoomItemKey] = room;
            return Task.CompletedTask;
        }

        // Invalidate the stale/forged cookie so the next request boots a fresh
        // session rather than re-presenting the same garbage.
        context.HttpContext.Response.Cookies.Delete(RoomSessionService.CookieName);
        context.Result = new ObjectResult(new { error = "unauthorized", redirect = "/" }) { StatusCode = 401 };
        return Task.CompletedTask;
    }
}

/// <summary>Convenience accessors for <see cref="ControllerBase"/> implementations.</summary>
public static class RoomItemAccessor
{
    /// <summary>Pulls the resolved room out of <see cref="HttpContext.Items"/>. Throws if the filter didn't run.</summary>
    public static SimulationRoom Room(this HttpContext context) =>
        (SimulationRoom)(context.Items[RequireRoomAttribute.RoomItemKey]
            ?? throw new InvalidOperationException(
                $"No room on HttpContext.Items[{RequireRoomAttribute.RoomItemKey}]. Did you forget [RequireRoom]?"));
}
