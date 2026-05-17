using CSharPN.Visualizer.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;
using UserApi;


// ── Build ─────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The model IS the application state — one singleton shared across all requests.
var model = new UserManagementModel();
builder.Services.AddSingleton(model);
builder.Services.AddSingleton(new CpnApiHost(model));

// Visualizer services (scoped per Blazor circuit).
builder.Services.AddScoped<SimulationService>();
builder.Services.AddScoped<ModelCatalog>();

// Bridge: broadcasts CpnApiHost.ModelChanged to all active SimulationService sessions.
builder.Services.AddSingleton<SimSessionBridge>();
builder.Services.AddScoped<CircuitHandler, SimBridgeCircuitHandler>();

// ── App ───────────────────────────────────────────────────────────────────────

var app = builder.Build();
app.MapStaticAssets();
app.UseStaticFiles();
app.UseAntiforgery();

// ── REST API endpoints (driven by CPN transitions) ────────────────────────────

var host = app.Services.GetRequiredService<CpnApiHost>();

host.MapPost(app, "/api/register",         model.RegisterCh);
host.MapPost(app, "/api/login",            model.LoginCh);
host.MapPost(app, "/api/forgot-password",  model.ForgotCh);
host.MapPost(app, "/api/reset-password",   model.ResetCh);

host.MapGet(app, "/api/users",        model.Users);
host.MapGet(app, "/api/sessions",     model.Sessions);
host.MapGet(app, "/api/reset-tokens", model.ResetTokens);

// ── Hook: when API fires a transition, notify all visualizer sessions ─────────

var bridge = app.Services.GetRequiredService<SimSessionBridge>();
host.ModelChanged += () => bridge.NotifyAll();

// ── Blazor dashboard + visualizer ─────────────────────────────────────────────

app.MapRazorComponents<UserApi.Components.App>()
   .AddInteractiveServerRenderMode()
   .AddAdditionalAssemblies(typeof(CSharPN.Visualizer.Components.Routes).Assembly);

var port = Environment.GetEnvironmentVariable("PORT") ?? "5001";
app.Run($"http://0.0.0.0:{port}");
