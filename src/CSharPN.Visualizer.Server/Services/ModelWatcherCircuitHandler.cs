using CSharPN.Visualizer.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CSharPN.Visualizer.Server.Services;

/// <summary>
/// Scoped Blazor circuit handler that registers the tab's <see cref="SimulationService"/>
/// with the singleton <see cref="ModelFileWatcher"/> when the circuit opens and
/// unregisters it when the circuit closes (tab closed / disconnected).
/// </summary>
public sealed class ModelWatcherCircuitHandler(
    SimulationService sim,
    ModelFileWatcher  watcher) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        sim.HideModelSelector = watcher.HasInitialModel;
        watcher.Register(sim);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        watcher.Unregister(sim);
        return Task.CompletedTask;
    }
}
