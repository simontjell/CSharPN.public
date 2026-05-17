using CSharPN.Visualizer.Components;
using CSharPN.Visualizer.Services;
using CSharPN.Visualizer.Server.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Scoped: each browser tab gets its own simulation session.
builder.Services.AddScoped<SimulationService>();
builder.Services.AddScoped<ModelCatalog>();

// Singleton: Roslyn metadata references are expensive to build; share across tabs.
builder.Services.AddSingleton<ICpnCompiler, CpnRoslynCompiler>();

// Singleton: file watcher for hot-reload of model .cs files from the hot/ directory.
builder.Services.AddSingleton<ModelFileWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelFileWatcher>());

// Circuit handler: registers / unregisters each tab's SimulationService with the
// ModelFileWatcher so file changes are broadcast to all active sessions.
builder.Services.AddScoped<CircuitHandler, ModelWatcherCircuitHandler>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<CSharPN.Visualizer.Server.App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(CSharPN.Visualizer.Components.Routes).Assembly);

// ── Source-navigation endpoint (polled by VS Code extension) ─────────────────
string? _pendingNavigate = null;

app.MapPost("/api/navigate", (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    _pendingNavigate = reader.ReadToEndAsync().Result;
    return Results.Ok();
});

app.MapGet("/api/navigate", () =>
{
    var val = _pendingNavigate;
    _pendingNavigate = null;
    return val != null ? Results.Text(val) : Results.NoContent();
});

app.Run();
