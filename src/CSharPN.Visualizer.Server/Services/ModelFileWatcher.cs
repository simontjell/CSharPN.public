using System.Text.RegularExpressions;
using CSharPN.Core;
using CSharPN.Visualizer.Services;

namespace CSharPN.Visualizer.Server.Services;

/// <summary>
/// Singleton background service that watches a .cs model file for changes
/// (specified via the CSHARPN_MODEL_FILE env var / serve.sh &lt;model.cs&gt;, with the
/// editor's cursor line in CSHARPN_MODEL_LINE).
///
/// When the file belongs to a project (a .csproj above it), the project is built with
/// <c>dotnet build</c> and the model class under the cursor is loaded from the output
/// (<see cref="ProjectModelLoader"/>); every .cs file of the project is watched. A loose
/// file without a project is compiled with Roslyn together with its sibling files.
///
/// On startup the model is loaded immediately and pushed to all sessions. On every
/// subsequent save it is hot-reloaded with marking migration. When no file is specified
/// the service is a no-op (normal catalog mode).
/// </summary>
public sealed class ModelFileWatcher : BackgroundService
{
    private readonly ICpnCompiler              _compiler;
    private readonly ProjectModelLoader        _projectLoader = new();
    private readonly ILogger<ModelFileWatcher> _log;
    private readonly string? _modelFile;   // null → catalog mode (no-op)
    private readonly int?    _modelLine;   // editor cursor line (1-based) when launched from the extension
    private readonly string? _project;     // nearest .csproj, null → loose-file Roslyn mode
    private string? _layoutPath;

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
        _modelLine = int.TryParse(Environment.GetEnvironmentVariable("CSHARPN_MODEL_LINE"), out var l) && l > 0 ? l : null;
        _project   = _modelFile != null && File.Exists(_modelFile) ? ProjectModelLoader.FindProject(_modelFile) : null;
        _layoutPath = _modelFile != null ? _modelFile + ".layout" : null;
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
                sim.LayoutFilePath = _layoutPath;
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

        _log.LogInformation("ModelFileWatcher: watching {File}{Project}", _modelFile,
            _project != null ? $" (project {Path.GetFileName(_project)})" : " (no project: loose-file mode)");

        var pending = System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });

        void Enqueue() => pending.Writer.TryWrite(_modelFile);

        // Watch every .cs file of the project and of the projects it references (or,
        // without a project, of the folder): the model may be split across files or live
        // partly in a referenced project, and editing any of them should trigger a reload.
        var dirs = _project != null
            ? ProjectModelLoader.SourceDirectories(_project)
            : [Path.GetDirectoryName(_modelFile)!];
        _log.LogInformation("ModelFileWatcher: watching for changes in {Dirs}", string.Join(", ", dirs));

        static bool IsBuildOutput(string path) =>
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj");

        var watchers = new List<FileSystemWatcher>();
        foreach (var dir in dirs)
        {
            var watcher = new FileSystemWatcher(dir, "*.cs")
            {
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = _project != null,
            };
            // Editors save in different ways (in-place write, or write-to-temp + rename), so
            // every kind of event counts.
            void OnEvent(object _, FileSystemEventArgs e)
            {
                if (IsBuildOutput(e.FullPath)) return;
                _log.LogInformation("ModelFileWatcher: {Change} {File} → reloading", e.ChangeType, e.FullPath);
                Enqueue();
            }
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Renamed += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Error   += (_, e) => _log.LogError("ModelFileWatcher: file watcher error: {Msg}", e.GetException().Message);
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
        using var disposeWatchers = new WatcherSet(watchers);

        // Compile immediately on startup.
        Enqueue();

        await foreach (var _ in pending.Reader.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(300, stoppingToken);
            try
            {
                await CompileAndBroadcast(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // The watcher must survive any failure; the next save gets another chance.
                _log.LogError(ex, "ModelFileWatcher: reload failed");
                ReportError($"Reload failed: {ex.Message}");
            }
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

        CpnModel? model;
        string    name;
        IReadOnlyList<CompileDiagnostic> diagnostics;

        if (_project != null)
        {
            SetStatus($"Building {Path.GetFileName(_project)} …");
            var started = System.Diagnostics.Stopwatch.StartNew();
            ProjectModelLoader.LoadResult result;
            try
            {
                result = await _projectLoader.LoadAsync(_modelFile!, _modelLine, ct);
                _log.LogInformation("ModelFileWatcher: built {Project} in {Ms} ms", Path.GetFileName(_project), started.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                ReportError($"{Path.GetFileName(_modelFile!)}:\n{ex.Message}");
                return;
            }
            model       = result.Model;
            name        = result.ModelName ?? Path.GetFileNameWithoutExtension(_modelFile!);
            diagnostics = result.Diagnostics;

            // A file with several models gets one layout file per model.
            _layoutPath = result.ModelsInFile.Count > 1 && result.ModelName != null
                ? $"{_modelFile}.{result.ModelName}.layout"
                : _modelFile + ".layout";
        }
        else
        {
            // Loose file: compile it together with its sibling .cs files so a model split
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

            var result  = _compiler.Compile(sources);
            model       = result.Model;
            name        = Path.GetFileNameWithoutExtension(_modelFile!);
            diagnostics = result.Diagnostics;
        }

        if (model == null)
        {
            var errors = string.Join("\n", diagnostics
                .Where(d => d.Severity == "Error")
                .Select(d => d.Line > 0 ? $"({d.Line},{d.Column}) {d.Message}" : d.Message));
            _log.LogError("ModelFileWatcher: load failed:\n{Errors}", errors);
            ReportError($"{Path.GetFileName(_modelFile!)}:\n{errors}");
            return;
        }

        foreach (var w in diagnostics.Where(d => d.Severity == "Warning" && d.Code.StartsWith("CPN")))
            _log.LogWarning("ModelFileWatcher: {Message}", w.Message);
        _log.LogInformation("ModelFileWatcher: loaded {Name}", name);

        lock (_sessLock)
        {
            _lastModel     = model;
            _lastModelName = name;
        }

        SimulationService[] sessions;
        lock (_sessLock) sessions = [.. _sessions];

        var sourceMap = BuildSourceMap(source);
        _log.LogInformation("ModelFileWatcher: pushing {Name} to {Count} session(s)", name, sessions.Length);
        foreach (var sim in sessions)
        {
            sim.LayoutFilePath = _layoutPath;
            sim.ModelFilePath = _modelFile;
            sim.SourceMap = sourceMap;
            try
            {
                await sim.LoadModelAsync(model, name, preserveMarking: true);
            }
            catch (Exception ex)
            {
                // One broken session must not stop the watcher for the others.
                _log.LogError(ex, "ModelFileWatcher: a session failed to load {Name}", name);
                sim.SetHotError($"Reload failed: {ex.Message}");
            }
        }
    }

    private sealed class WatcherSet(List<FileSystemWatcher> watchers) : IDisposable
    {
        public void Dispose() { foreach (var w in watchers) w.Dispose(); }
    }

    private void ReportError(string text)
    {
        SimulationService[] sessions;
        lock (_sessLock) sessions = [.. _sessions];
        foreach (var sim in sessions) sim.SetHotError(text);
    }

    /// <summary>Shown while a (slow) project build runs, so a stale error does not linger.</summary>
    private void SetStatus(string text)
    {
        SimulationService[] sessions;
        lock (_sessLock) sessions = [.. _sessions];
        foreach (var sim in sessions)
            if (sim.Model == null) sim.SetHotError(text);
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
