using System.Text.RegularExpressions;
using CSharPN.Core;
using CSharPN.Visualizer.Services;

namespace CSharPN.Visualizer.Server.Services;

/// <summary>
/// Singleton background service that watches a single .cs model file for changes
/// (specified via the CSHARPN_MODEL_FILE env var / serve.sh &lt;model.cs&gt;).
///
/// On startup the file is compiled immediately and pushed to all sessions.
/// On every subsequent save the model is hot-reloaded with marking migration.
/// When no file is specified the service is a no-op (normal catalog mode).
/// </summary>
public sealed class ModelFileWatcher : BackgroundService
{
    private readonly ICpnCompiler              _compiler;
    private readonly ILogger<ModelFileWatcher> _log;
    private readonly string? _modelFile;   // null → catalog mode (no-op)

    // Last successfully compiled model — pushed to new sessions on Register.
    private CpnModel? _lastModel;
    private string?   _lastModelName;

    private readonly List<SimulationService> _sessions = [];
    private readonly object _sessLock = new();

    /// <summary>True when a specific model file was provided via CSHARPN_MODEL_FILE.</summary>
    public bool HasInitialModel => _modelFile != null;

    public ModelFileWatcher(ICpnCompiler compiler,
                            ILogger<ModelFileWatcher> log)
    {
        _compiler  = compiler;
        _log       = log;
        var raw    = Environment.GetEnvironmentVariable("CSHARPN_MODEL_FILE");
        _modelFile = string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    // ── Session registry ──────────────────────────────────────────────────────

    public void Register(SimulationService sim)
    {
        CpnModel? toLoad;
        string?   name;
        lock (_sessLock)
        {
            _sessions.Add(sim);
            toLoad = _lastModel;
            name   = _lastModelName;
        }
        if (toLoad != null)
        {
            if (_modelFile != null)
            {
                sim.LayoutFilePath = _modelFile + ".layout";
                sim.ModelFilePath = _modelFile;
                try { sim.SourceMap = BuildSourceMap(File.ReadAllText(_modelFile)); }
                catch { /* best effort */ }
            }
            _ = sim.LoadModelAsync(toLoad, name!, preserveMarking: false);
        }
    }

    public void Unregister(SimulationService sim)
    {
        lock (_sessLock) _sessions.Remove(sim);
    }

    // ── Background loop ───────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_modelFile == null)
            return;   // catalog mode — nothing to do

        if (!File.Exists(_modelFile))
        {
            _log.LogError("ModelFileWatcher: file not found: {File}", _modelFile);
            return;
        }

        _log.LogInformation("ModelFileWatcher: watching {File}", _modelFile);

        var pending = System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });

        void Enqueue() => pending.Writer.TryWrite(_modelFile);

        var dir  = Path.GetDirectoryName(_modelFile)!;
        var file = Path.GetFileName(_modelFile);

        // Watch every .cs file in the folder, not just the model file: the model may
        // be split across files (e.g. Domain.cs + Model.cs), and editing any of them
        // should trigger a recompile.
        _ = file;
        using var watcher = new FileSystemWatcher(dir, "*.cs")
        {
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents   = true,
        };
        watcher.Changed += (_, _) => Enqueue();
        watcher.Renamed += (_, _) => Enqueue();

        // Compile immediately on startup.
        Enqueue();

        await foreach (var _ in pending.Reader.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(300, stoppingToken);
            await CompileAndBroadcast(stoppingToken);
        }
    }

    private async Task CompileAndBroadcast(CancellationToken ct)
    {
        string source;
        try
        {
            await Task.Delay(100, ct);
            source = await File.ReadAllTextAsync(_modelFile!, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning("ModelFileWatcher: could not read file: {Msg}", ex.Message);
            return;
        }

        // Compile the model file together with its sibling .cs files so a model split
        // across files (Domain.cs + Model.cs …) resolves. Driver files with top-level
        // statements are ignored by the compiler.
        IReadOnlyList<string> sources;
        try
        {
            var dir = Path.GetDirectoryName(_modelFile)!;
            var list = new List<string>();
            foreach (var path in Directory.GetFiles(dir, "*.cs"))
                list.Add(await File.ReadAllTextAsync(path, ct));
            sources = list.Count > 0 ? list : [source];
        }
        catch { sources = [source]; }

        var result = _compiler.Compile(sources);

        if (result.Model == null)
        {
            var errors = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == "Error")
                .Select(d => $"({d.Line},{d.Column}) {d.Message}"));
            _log.LogError("ModelFileWatcher: compile failed:\n{Errors}", errors);

            SimulationService[] errSessions;
            lock (_sessLock) errSessions = [.. _sessions];
            foreach (var sim in errSessions)
                sim.SetHotError($"{Path.GetFileName(_modelFile!)}:\n{errors}");
            return;
        }

        var name = Path.GetFileNameWithoutExtension(_modelFile!);
        _log.LogInformation("ModelFileWatcher: loaded {Name}", name);

        lock (_sessLock)
        {
            _lastModel     = result.Model;
            _lastModelName = name;
        }

        SimulationService[] sessions;
        lock (_sessLock) sessions = [.. _sessions];

        var layoutPath = _modelFile + ".layout";
        var sourceMap = BuildSourceMap(source);
        foreach (var sim in sessions)
        {
            sim.LayoutFilePath = layoutPath;
            sim.ModelFilePath = _modelFile;
            sim.SourceMap = sourceMap;
            await sim.LoadModelAsync(result.Model, name, preserveMarking: true);
        }
    }

    /// <summary>
    /// Scans source text for AddPlace/AddTimedPlace/AddTransition calls
    /// and maps the string argument (element name) to its line number.
    /// </summary>
    private static Dictionary<string, int> BuildSourceMap(string source)
    {
        var map = new Dictionary<string, int>();
        var pattern = new Regex(
            @"(?:AddPlace|AddTimedPlace|AddTransition)\s*(?:<[^>]+>)?\s*\(\s*""([^""]+)""",
            RegexOptions.Compiled);
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in pattern.Matches(lines[i]))
                map.TryAdd(m.Groups[1].Value, i + 1); // 1-based line numbers
        }
        return map;
    }
}
