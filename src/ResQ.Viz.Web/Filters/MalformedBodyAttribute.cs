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

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Filters;

/// <summary>
/// Turns a request body the model binder could not deserialise into the surface's own rejection
/// shape instead of letting it escape as an unhandled exception.
/// </summary>
/// <remarks>
/// The v2 surface promises that every unacceptable request comes back with a status and a stable
/// code, and that a refusal leaves the simulation untouched. A body that fails to bind never
/// reaches the validator that keeps that promise: the exception is raised inside model binding,
/// before the action runs, so the caller gets a bodyless 500 and cannot tell "you sent me
/// something I could not read" from "the server fell over". Both are the caller's problem to act
/// on and they call for opposite responses — fix the request, or retry — so collapsing them is a
/// real loss of information, not a cosmetic one.
/// <para>
/// <b>Why two exception types.</b> <see cref="JsonException"/> is the obvious one and on its own
/// it is not enough. A polymorphic member whose type discriminator is missing or unknown —
/// <see cref="CommandTarget"/> being the case that matters here, since every targeted command
/// carries one — is reported by <c>System.Text.Json</c> as a
/// <see cref="NotSupportedException"/>, which is the single most likely malformed body this API
/// will ever see and the one a naive <c>catch (JsonException)</c> misses.
/// </para>
/// <para>
/// <b>Why the source check.</b> <see cref="NotSupportedException"/> is a general-purpose type and
/// swallowing every one of them would convert genuine server faults — an unimplemented path
/// somewhere inside a command — into a 400 blaming the caller for a request that was perfectly
/// well-formed. Narrowing to exceptions originating in the serialiser's own assembly keeps this
/// filter to the one thing it is for. Anything else is left to propagate untouched.
/// </para>
/// <para>
/// This runs as an exception filter rather than middleware because the surface's problem shape is
/// a controller concern, and because scoping it to the annotated controller keeps the v1 routes —
/// which have no polymorphic payloads and their own error contract — behaving exactly as before.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class MalformedBodyAttribute : Attribute, IExceptionFilter
{
    /// <summary>Assembly whose deserialisation failures are attributable to the request body.</summary>
    private const string SerializerSource = "System.Text.Json";

    /// <summary>Converts a body-deserialisation failure into a 400 carrying the surface's problem shape.</summary>
    /// <param name="context">The failing action's context.</param>
    public void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsBodyDeserialisationFailure(context.Exception))
        {
            return;
        }

        var problem = new CommandProblemDetails(
            Code: CommandRejectionReasons.PayloadMalformed,
            Title: "Invalid request",
            Detail: "The request body could not be read as JSON of the expected shape. "
                + "A polymorphic member such as a command target must carry its 'type' discriminator.",
            TraceId: context.HttpContext.TraceIdentifier,
            Errors: [new CommandFieldError("body", CommandRejectionReasons.PayloadMalformed,
                context.Exception.Message)]);

        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
        context.ExceptionHandled = true;
    }

    /// <summary>True when the exception is the serialiser rejecting the caller's body.</summary>
    /// <param name="exception">Exception that ended the request.</param>
    /// <returns><see langword="true"/> if the body, not the server, is at fault.</returns>
    private static bool IsBodyDeserialisationFailure(Exception exception) =>
        exception is JsonException
        || (exception is NotSupportedException
            && string.Equals(exception.Source, SerializerSource, StringComparison.Ordinal));
}
