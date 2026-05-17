using CSharPN.Visualizer.Services;
using Microsoft.Extensions.Logging;

namespace UserApi;

/// <summary>
/// Singleton bridge between the global <see cref="CpnApiHost.ModelChanged"/> event
/// and the scoped <see cref="SimulationService"/> instances (one per Blazor circuit).
/// Each circuit registers/unregisters itself; the API host calls <see cref="NotifyAll"/>
/// after every transition firing.
/// </summary>
public sealed class SimSessionBridge(ILogger<SimSessionBridge> logger)
{
    private readonly List<SimulationService> _sessions = [];
    private readonly object _lock = new();

    public void Register(SimulationService sim)
    {
        lock (_lock) _sessions.Add(sim);
        logger.LogInformation("SimSessionBridge: registered session (total: {Count})", _sessions.Count);
    }

    public void Unregister(SimulationService sim)
    {
        lock (_lock) _sessions.Remove(sim);
        logger.LogInformation("SimSessionBridge: unregistered session (total: {Count})", _sessions.Count);
    }

    public void NotifyAll()
    {
        SimulationService[] snapshot;
        lock (_lock) snapshot = [.. _sessions];
        logger.LogInformation("SimSessionBridge: notifying {Count} sessions", snapshot.Length);
        foreach (var sim in snapshot)
            _ = sim.NotifyExternalChangeAsync();
    }
}
