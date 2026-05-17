using CSharPN.Visualizer.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace UserApi;

/// <summary>
/// Registers the circuit's <see cref="SimulationService"/> with the
/// <see cref="SimSessionBridge"/> when the circuit opens, and loads the
/// shared <see cref="UserManagementModel"/> into the visualizer.
/// </summary>
public sealed class SimBridgeCircuitHandler(
    SimulationService    sim,
    SimSessionBridge     bridge,
    UserManagementModel  model) : CircuitHandler
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        sim.HideModelSelector = true;
        bridge.Register(sim);
        await sim.LoadModelAsync(model, "User Management");
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        bridge.Unregister(sim);
        return Task.CompletedTask;
    }
}
