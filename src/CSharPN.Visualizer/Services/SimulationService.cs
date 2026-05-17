using System.Text.Json;
using CSharPN.Core;
using CSharPN.Visualizer.Layout;

namespace CSharPN.Visualizer.Services;

/// <summary>
/// Scoped service that owns one CPN simulation session per browser tab.
/// Supports step, run (async loop) and reset.  Thread-safe via a semaphore.
/// </summary>
public sealed class SimulationService : IAsyncDisposable
{
    private CpnModel?            _model;
    private CpnSimulator?        _sim;
    private TimedCpnModel?       _timedModel;
    private TimedCpnSimulator?   _timedSim;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Public state ──────────────────────────────────────────────────────────

    public string    ModelName  { get; private set; } = "";
    public int       StepCount  { get; private set; }
    public bool      IsRunning  { get; private set; }
    public bool      IsDeadlock { get; private set; }
    public int       SpeedMs    { get; set; } = 300;

    public CpnModel? Model => _model;

    public CpnTime? GlobalClock => _timedSim?.GlobalClock;

    public IReadOnlyList<PageGroup>? PageGroups { get; private set; }

    /// <summary>Page names for hierarchical navigation. Null for flat models.</summary>
    public IReadOnlyList<string>? PageNames { get; private set; }

    /// <summary>Current page being viewed. Null = top page (or flat model).</summary>
    public string? CurrentPage { get; private set; }

    /// <summary>Path to the model source file (for source navigation). Null for catalog models.</summary>
    public string? ModelFilePath { get; set; }

    /// <summary>Element name → line number in the source file.</summary>
    public Dictionary<string, int> SourceMap { get; set; } = [];

    public IReadOnlyList<(Transition Transition, BindingSnapshot Binding)> EnabledBindings
        { get; private set; } = [];

    public LayoutResult Layout { get; private set; } = new([], [], 900, 500);

    /// <summary>
    /// Result of the last marking migration; null when no migration was attempted
    /// (e.g. first load or preserveMarking was false).
    /// </summary>
    public MarkingMigrationResult? LastMigration { get; private set; }

    /// <summary>
    /// Last hot-reload compile error, or null when the last reload succeeded.
    /// Set by <c>ModelFileWatcher</c> when compilation fails.
    /// </summary>
    public string? LastHotError { get; private set; }

    /// <summary>Sets (or clears) the hot-reload error and notifies subscribers.</summary>
    public void SetHotError(string? error)
    {
        LastHotError = error;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// When true the UI hides the model-selector dropdown and shows only the
    /// model loaded via hot-reload (single-model mode via serve.sh &lt;model.cs&gt;).
    /// Set by the circuit handler based on <c>ModelFileWatcher.HasInitialModel</c>.
    /// </summary>
    public bool HideModelSelector { get; set; }

    // ── Notification ──────────────────────────────────────────────────────────

    /// <summary>
    /// Raised on every state change.  Subscribers must marshal to the UI thread
    /// via <c>InvokeAsync(StateHasChanged)</c>.
    /// </summary>
    public event Action? StateChanged;

    // ── Model loading ─────────────────────────────────────────────────────────

    /// <param name="preserveMarking">
    /// When true the current marking is snapshot before the swap and restored
    /// into the new model for every place whose name and token type are unchanged.
    /// </param>
    public async Task LoadModelAsync(CpnModel model, string name,
                                     bool preserveMarking = false)
    {
        await _lock.WaitAsync();
        try
        {
            var oldModel = preserveMarking ? _model : null;

            StopRunLocked();
            _model      = model;
            ModelName   = name;
            _sim        = new CpnSimulator(model);
            _timedModel = model as TimedCpnModel;
            _timedSim   = _timedModel != null ? new TimedCpnSimulator(_timedModel) : null;
            var hier = model as HierarchicalCpnModel;
            PageGroups  = hier?.GetPageGroups();
            PageNames   = hier?.GetPageNames();
            CurrentPage = null;
            StepCount   = 0;
            IsDeadlock  = false;
            LoadLayout();
            Layout      = ComputeCurrentLayout();

            LastHotError = null;

            if (oldModel != null)
            {
                var (r, s) = model.MigrateMarkingFrom(oldModel);
                LastMigration = new MarkingMigrationResult(r, s);
            }
            else LastMigration = null;

            RefreshEnabled();
        }
        finally { _lock.Release(); }

        StateChanged?.Invoke();
    }

    // ── Stepping ──────────────────────────────────────────────────────────────

    public async Task<bool> StepAsync()
    {
        bool ok;
        await _lock.WaitAsync();
        try   { ok = StepLocked(); }
        finally { _lock.Release(); }
        StateChanged?.Invoke();
        return ok;
    }

    private bool StepLocked()
    {
        if (_sim == null || IsDeadlock) return false;
        var ok = _timedSim != null ? _timedSim.Step() : _sim.Step();
        StepCount++;
        if (!ok) IsDeadlock = true;
        RefreshEnabled();
        return ok;
    }

    // ── Running ───────────────────────────────────────────────────────────────

    public async Task StartRunAsync()
    {
        if (IsRunning || _sim == null) return;
        _cts      = new CancellationTokenSource();
        IsRunning = true;
        StateChanged?.Invoke();

        try
        {
            while (!_cts.Token.IsCancellationRequested && !IsDeadlock)
            {
                await _lock.WaitAsync(_cts.Token);
                try   { if (!StepLocked()) break; }
                finally { _lock.Release(); }
                StateChanged?.Invoke();
                await Task.Delay(SpeedMs, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }

        IsRunning = false;
        StateChanged?.Invoke();
    }

    public void PauseRun() => _cts?.Cancel();

    // ── Reset ─────────────────────────────────────────────────────────────────

    public async Task ResetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            StopRunLocked();
            _model?.Reset();
            _timedSim?.ResetClock();
            _sim      = _model != null ? new CpnSimulator(_model) : null;
            _timedSim = _timedModel != null ? new TimedCpnSimulator(_timedModel) : null;
            StepCount  = 0;
            IsDeadlock = false;
            LastMigration = null;
            RefreshEnabled();
        }
        finally { _lock.Release(); }
        StateChanged?.Invoke();
    }

    // ── Fire specific binding ─────────────────────────────────────────────────

    public async Task FireAsync(Transition t, BindingSnapshot b)
    {
        await _lock.WaitAsync();
        try
        {
            if (_sim == null || IsDeadlock) return;
            if (_timedSim != null) _timedSim.Step(t, b);
            else _sim.Step(t, b);
            StepCount++;
            RefreshEnabled();
            if (EnabledBindings.Count == 0)
                IsDeadlock = _timedModel == null
                    || _timedModel.GetNextReadyTime(_timedModel.GlobalClock) == null;
        }
        finally { _lock.Release(); }
        StateChanged?.Invoke();
    }

    // ── Page navigation ──────────────────────────────────────────────────────

    /// <summary>Switch the current page view. Null = top page.</summary>
    public void SwitchPage(string? pageName)
    {
        CurrentPage = pageName;
        Layout = ComputeCurrentLayout();
        StateChanged?.Invoke();
    }

    private LayoutResult ComputeCurrentLayout()
    {
        if (_model is not HierarchicalCpnModel hier)
            return LayoutEngine.Compute(_model ?? new EmptyModel());

        if (CurrentPage == null)
        {
            // Top page view: shared places + substitution transitions
            var (nodes, edges) = hier.GetTopPageView();
            return LayoutEngine.Compute(nodes, edges);
        }
        else
        {
            // Sub-page view: port places + local places + transitions
            var (nodes, edges) = hier.GetSubPageView(CurrentPage);
            return LayoutEngine.Compute(nodes, edges);
        }
    }

    private sealed class EmptyModel : CpnModel { }

    // ── Layout persistence ────────────────────────────────────────────────────

    /// <summary>
    /// Path to the .layout file (e.g. model2.cs.layout). Set by ModelFileWatcher.
    /// Null for catalog models (no persistence).
    /// </summary>
    public string? LayoutFilePath { get; set; }

    // page key ("" = top) → nodeId → node data
    private Dictionary<string, Dictionary<string, NodeLayout>> _layoutData = [];

    public sealed class NodeLayout
    {
        public double? X { get; set; }
        public double? Y { get; set; }
        public bool? ShowMarking { get; set; }
    }

    private NodeLayout EnsureNode(string nodeId)
    {
        var pageKey = CurrentPage ?? "";
        if (!_layoutData.TryGetValue(pageKey, out var page))
            _layoutData[pageKey] = page = [];
        if (!page.TryGetValue(nodeId, out var node))
            page[nodeId] = node = new();
        return node;
    }

    /// <summary>Record a node position and persist to file.</summary>
    public void SaveNodePosition(string nodeId, double x, double y)
    {
        var node = EnsureNode(nodeId);
        node.X = Math.Round(x, 1);
        node.Y = Math.Round(y, 1);
        PersistLayout();
    }

    /// <summary>Returns saved position overrides for the current page.</summary>
    public Dictionary<string, (double X, double Y)> GetCurrentPageOverrides()
    {
        var pageKey = CurrentPage ?? "";
        if (!_layoutData.TryGetValue(pageKey, out var page)) return [];
        var result = new Dictionary<string, (double, double)>();
        foreach (var (id, n) in page)
            if (n.X.HasValue && n.Y.HasValue)
                result[id] = (n.X.Value, n.Y.Value);
        return result;
    }

    /// <summary>
    /// Clears all saved node positions for the current page (autolayout reset).
    /// ShowMarking flags are preserved.
    /// </summary>
    public void ResetNodePositions()
    {
        var pageKey = CurrentPage ?? "";
        if (_layoutData.TryGetValue(pageKey, out var page))
            foreach (var node in page.Values) { node.X = null; node.Y = null; }
        PersistLayout();
    }

    /// <summary>Toggle expanded marking for a place and persist.</summary>
    public bool ToggleExpandedMarking(string placeId)
    {
        var node = EnsureNode(placeId);
        var show = !(node.ShowMarking ?? false);
        node.ShowMarking = show ? true : null; // omit false from JSON
        PersistLayout();
        return show;
    }

    /// <summary>Returns expanded marking place ids for the current page.</summary>
    public HashSet<string> GetCurrentExpandedMarkings()
    {
        var pageKey = CurrentPage ?? "";
        if (!_layoutData.TryGetValue(pageKey, out var page)) return [];
        return page.Where(kv => kv.Value.ShowMarking == true)
                   .Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>Load layout data from file (called on model load).</summary>
    internal void LoadLayout()
    {
        _layoutData = [];
        if (LayoutFilePath == null || !File.Exists(LayoutFilePath)) return;
        try
        {
            var json = File.ReadAllText(LayoutFilePath);
            _layoutData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, NodeLayout>>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch { _layoutData = []; }
    }

    private static readonly JsonSerializerOptions _layoutJsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private void PersistLayout()
    {
        if (LayoutFilePath == null) return;
        try { File.WriteAllText(LayoutFilePath, JsonSerializer.Serialize(_layoutData, _layoutJsonOpts)); }
        catch { /* best effort */ }
    }

    // ── External mutation notification ─────────────────────────────────────────

    /// <summary>
    /// Call when the model has been mutated externally (e.g. by an API host
    /// firing transitions outside the simulator).  Refreshes enabled bindings,
    /// increments the step count and notifies the UI.
    /// </summary>
    public async Task NotifyExternalChangeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            StepCount++;
            RefreshEnabled();
        }
        finally { _lock.Release(); }
        StateChanged?.Invoke();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void RefreshEnabled()
    {
        if (_sim == null) { EnabledBindings = []; return; }
        // For timed models: advance the clock when nothing is currently enabled
        // so the UI shows what is actually fireable on the next step.
        _timedSim?.AdvanceClock();
        EnabledBindings = _sim.GetEnabled();
    }

    private void StopRunLocked()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try   { StopRunLocked(); }
        finally { _lock.Release(); }
        _lock.Dispose();
    }
}

/// <summary>Result of migrating a marking across a model hot-reload.</summary>
public sealed record MarkingMigrationResult(int Restored, int Skipped);
