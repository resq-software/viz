// Copyright 2024 ResQ Technologies Ltd.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Vite.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddControllers();

// Per-room simulation: SimulationManager owns the dictionary of rooms and the
// 60 Hz tick loop; RoomSessionService issues / validates the encrypted
// HttpOnly cookie (viz_session) that binds a request to its room. The cookie
// payload is signed+encrypted with IDataProtector (AES-256-CBC + HMAC-SHA256,
// automatic key rotation) and bound to a /24 (IPv4) or /64 (IPv6) IP-prefix
// bucket so a leaked cookie replayed from a different network is rejected.
//
// Each SimulationRoom owns its own TerrainNoiseService, UpdatableWeatherSystem,
// and SwarmCoordinator — they are no longer process-wide singletons because
// rooms must not share state. The IFrameBroadcaster abstraction introduced
// for the legacy single-sim path is unused here: SimulationManager broadcasts
// directly to per-room SignalR groups so it can fan out one frame per room
// without an extra hop.
builder.Services.AddDataProtection();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.VizFrameBuilder>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.ScenarioService>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.SimulationManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResQ.Viz.Web.Services.SimulationManager>());
builder.Services.AddSingleton<ResQ.Viz.Web.Services.RoomSessionService>();

// Forwarded headers: when deployed behind a reverse proxy (nginx, Cloudflare,
// load balancer), the proxy MUST be added to KnownProxies/KnownNetworks via
// configuration — otherwise an attacker can spoof X-Forwarded-For and bypass
// IP-bucket binding. The default (loopback only) is safe for direct exposure
// and for proxies on the same host.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddViteServices();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddFixedWindowLimiter("destructive", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("general", opt =>
    {
        opt.PermitLimit = 60;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
});

var app = builder.Build();

// Forwarded headers must run before any middleware that inspects the client
// IP — otherwise IP-bucket session binding sees the proxy's address rather
// than the real client's. Default trust list is loopback only; production
// behind a non-local proxy must add KnownProxies/KnownNetworks via config.
app.UseForwardedHeaders();

// Security headers must run BEFORE UseStaticFiles (which short-circuits for
// physical assets) and UseRateLimiter (which can emit a 429 and skip the rest
// of the pipeline). Using OnStarting rather than pre-await writes ensures the
// headers land even on early-terminated responses.
//
// Baseline follows the OWASP Secure Headers Project recommendations:
//   X-Content-Type-Options           — prevents MIME sniffing
//   X-Frame-Options: DENY            — prevents clickjacking (legacy; also covered by CSP frame-ancestors)
//   Content-Security-Policy          — tight script / style / connect / img allow-lists
//     + frame-ancestors 'none'       — modern clickjacking block
//     + base-uri 'self'              — prevent <base> injection redirecting relative URLs
//     + form-action 'self'           — prevent form submission to external origins
//     + object-src 'none'            — block legacy plugins (Flash, Java applets)
//   Referrer-Policy                  — strip cross-origin referrer query strings
//   Permissions-Policy               — deny every powerful feature the viz doesn't use
//   Cross-Origin-Opener-Policy       — isolate from window.opener (Spectre mitigation)
//   Cross-Origin-Resource-Policy     — prevent cross-site resource pulls
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self' ws: wss:; " +
            "img-src 'self' data:; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'; " +
            "object-src 'none';";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        // Deny every Permissions-Policy feature the viz doesn't need. If a
        // future feature needs one (e.g. camera for AR overlay), relax the
        // specific entry here rather than dropping the header.
        headers["Permissions-Policy"] =
            "accelerometer=(), ambient-light-sensor=(), autoplay=(), battery=(), " +
            "camera=(), display-capture=(), document-domain=(), encrypted-media=(), " +
            "fullscreen=(self), geolocation=(), gyroscope=(), magnetometer=(), " +
            "microphone=(), midi=(), payment=(), picture-in-picture=(), " +
            "publickey-credentials-get=(), screen-wake-lock=(), sync-xhr=(), " +
            "usb=(), web-share=(), xr-spatial-tracking=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-site";

        if (context.Request.Path.StartsWithSegments("/api"))
            headers["Cache-Control"] = "no-store";

        return Task.CompletedTask;
    });

    await next();
});

// In development, UseViteDevMiddleware MUST come before UseStaticFiles.
// It starts the Vite child process and proxies frontend requests to it, so
// a previously built wwwroot/index.html cannot shadow the live dev server.
if (app.Environment.IsDevelopment())
    app.UseViteDevelopmentServer();

app.UseStaticFiles();
app.UseRateLimiter();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();

app.MapControllers();
app.MapHub<ResQ.Viz.Web.Hubs.VizHub>("/viz");

if (!app.Environment.IsDevelopment())
    app.MapFallbackToFile("index.html");  // serves Vite-built wwwroot/index.html in production

app.Run();

// Exposes the top-level-statement `Program` class to tests so they can
// bootstrap the real pipeline via `WebApplicationFactory<Program>`. No
// members needed — the partial declaration just flips the implicit
// access modifier from internal to public.
public partial class Program;
