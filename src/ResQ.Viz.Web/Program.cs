// Copyright 2024 ResQ Technologies Ltd.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.SimulationService>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.VizFrameBuilder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResQ.Viz.Web.Services.SimulationService>());

var app = builder.Build();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<ResQ.Viz.Web.Hubs.VizHub>("/viz");
app.MapFallbackToFile("index.html");
app.Run();
