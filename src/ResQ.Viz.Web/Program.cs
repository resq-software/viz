// Copyright 2024 ResQ Technologies Ltd.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Vite.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.SimulationService>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.VizFrameBuilder>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.ScenarioService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResQ.Viz.Web.Services.SimulationService>());
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

// In development, UseViteDevMiddleware MUST come before UseStaticFiles.
// It starts the Vite child process and proxies frontend requests to it, so
// a previously built wwwroot/index.html cannot shadow the live dev server.
if (app.Environment.IsDevelopment())
    app.UseViteDevelopmentServer();

app.UseStaticFiles();
app.UseRateLimiter();

// Security headers + cache-control for API responses
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' ws: wss:; img-src 'self' data:;";

    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers["Cache-Control"] = "no-store";

    await next();
});

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
