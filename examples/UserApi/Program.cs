using CSharPN.Visualizer.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;
using UserApi;


// ── Build ─────────────────────────────────────────────────────────────────────

// The guard rule is enforced when each transition is built. This adds the runtime
// backstop for the one case that check cannot see: a marking reached through a
// method call rather than named in the guard expression itself.
CSharPN.Core.GuardScope.Strict = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The model IS the application state — one singleton shared across all requests.
var model = new UserManagementModel();
builder.Services.AddSingleton(model);
builder.Services.AddSingleton(sp =>
    new CpnApiHost(model, sp.GetRequiredService<ILogger<CpnApiHost>>()));

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

// Users and ResetTokens hold one collection token each, so they are flattened to rows.
host.MapGet(app, "/api/users",        model.Users,       db => db.All);
host.MapGet(app, "/api/reset-tokens", model.ResetTokens, db => db.All);
host.MapGet(app, "/api/sessions",     model.Sessions);

// ── Hook: when the engine fires transitions, notify all visualizer sessions ──

var bridge = app.Services.GetRequiredService<SimSessionBridge>();
host.ModelChanged += _ => bridge.NotifyAll();

// All endpoints are mapped — the engine may now start firing.
host.Start();
app.Lifetime.ApplicationStopping.Register(() => host.DisposeAsync().AsTask().GetAwaiter().GetResult());

// ── Blazor dashboard + visualizer ─────────────────────────────────────────────

app.MapRazorComponents<UserApi.Components.App>()
   .AddInteractiveServerRenderMode()
   .AddAdditionalAssemblies(typeof(CSharPN.Visualizer.Components.Routes).Assembly);

var port = Environment.GetEnvironmentVariable("PORT") ?? "5001";
app.Run($"http://0.0.0.0:{port}");
