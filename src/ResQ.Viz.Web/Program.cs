// Copyright 2024 ResQ Technologies Ltd.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

using Vite.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.SimulationService>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.VizFrameBuilder>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.ScenarioService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResQ.Viz.Web.Services.SimulationService>());
builder.Services.AddViteServices();

var app = builder.Build();

// In development, UseViteDevMiddleware MUST come before UseStaticFiles.
// It starts the Vite child process and proxies frontend requests to it, so
// a previously built wwwroot/index.html cannot shadow the live dev server.
if (app.Environment.IsDevelopment())
    app.UseViteDevelopmentServer();

app.UseStaticFiles();
app.MapControllers();
app.MapHub<ResQ.Viz.Web.Hubs.VizHub>("/viz");

if (!app.Environment.IsDevelopment())
    app.MapFallbackToFile("index.html");  // serves Vite-built wwwroot/index.html in production

app.Run();
