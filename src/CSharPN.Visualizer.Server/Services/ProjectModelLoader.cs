using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharPN.Core;
using CSharPN.Visualizer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharPN.Visualizer.Server.Services;

/// <summary>
/// Loads the model under the cursor of a source file by building the file's
/// <b>project</b> with <c>dotnet build</c> and loading the output assembly, instead
/// of compiling loose files. NuGet packages, project references and
/// <c>InternalsVisibleTo</c> then all resolve exactly as in the IDE, and a file
/// (or a test file) may declare any number of models.
/// </summary>
/// <remarks>
/// The output assembly is loaded into a collectible <see cref="AssemblyLoadContext"/>
/// that shares every assembly the host already has loaded — in particular
/// <c>CSharPN.Core</c>, so that the loaded class <em>is</em> a <see cref="CpnModel"/>
/// of this process — and resolves everything else from the project's build output.
/// </remarks>
public sealed class ProjectModelLoader
{
    public sealed record LoadResult(
        CpnModel? Model,
        string? ModelName,
        IReadOnlyList<string> ModelsInFile,
        IReadOnlyList<CompileDiagnostic> Diagnostics,
        string ProjectPath);

    private ModelLoadContext? _previous;

    /// <summary>The nearest <c>.csproj</c> above <paramref name="file"/>, or null.</summary>
    public static string? FindProject(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (dir != null)
        {
            var projects = Directory.GetFiles(dir, "*.csproj");
            if (projects.Length > 0) return projects.OrderBy(p => p, StringComparer.Ordinal).First();
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// The directories of <paramref name="project"/> and of every project it references,
    /// directly or transitively: the set of source folders whose changes affect the build.
    /// </summary>
    public static IReadOnlyList<string> SourceDirectories(string project)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string csproj)
        {
            csproj = Path.GetFullPath(csproj);
            if (!seen.Add(csproj) || !File.Exists(csproj)) return;
            result.Add(Path.GetDirectoryName(csproj)!);
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(csproj);
                foreach (var include in doc.Descendants("ProjectReference").Select(e => e.Attribute("Include")?.Value))
                    if (!string.IsNullOrEmpty(include))
                        Visit(Path.Combine(Path.GetDirectoryName(csproj)!, include.Replace('\\', Path.DirectorySeparatorChar)));
            }
            catch { /* an unreadable project is simply not watched */ }
        }
        Visit(project);
        return result;
    }

    /// <summary>
    /// Builds the project of <paramref name="modelFile"/> and instantiates the model whose
    /// class declaration contains <paramref name="cursorLine"/> (1-based). When the cursor
    /// is not inside a model class, the first model declared in the file is used.
    /// </summary>
    public async Task<LoadResult> LoadAsync(string modelFile, int? cursorLine, CancellationToken ct)
    {
        var project = FindProject(modelFile)
            ?? throw new InvalidOperationException($"No .csproj found above {modelFile}.");
        var diagnostics = new List<CompileDiagnostic>();

        // ── Build ──────────────────────────────────────────────────────────────
        var (buildExit, buildOutput) = await RunDotnetAsync(Path.GetDirectoryName(project)!,
            ["build", project, "-nologo", "-v:q", "-clp:NoSummary;ErrorsOnly"], ct);
        diagnostics.AddRange(ParseDiagnostics(buildOutput));
        if (buildExit != 0)
        {
            if (diagnostics.Count == 0)
                diagnostics.Add(new CompileDiagnostic("Error", "CPN010", $"dotnet build failed:\n{buildOutput.Trim()}", 0, 0));
            return new LoadResult(null, null, [], diagnostics, project);
        }

        var (pathExit, targetPath) = await RunDotnetAsync(Path.GetDirectoryName(project)!,
            ["msbuild", project, "-getProperty:TargetPath", "-nologo"], ct);
        targetPath = targetPath.Trim().Split('\n').Last().Trim();
        if (pathExit != 0 || !File.Exists(targetPath))
        {
            diagnostics.Add(new CompileDiagnostic("Error", "CPN011", $"Could not locate the build output of {project}: {targetPath}", 0, 0));
            return new LoadResult(null, null, [], diagnostics, project);
        }

        // ── Which classes does the file declare, and which one is under the cursor? ──
        var declared = DeclaredClasses(await File.ReadAllTextAsync(modelFile, ct));

        // ── Load the assembly ─────────────────────────────────────────────────
        _previous?.Unload();
        var context = new ModelLoadContext(targetPath);
        _previous = context;
        Assembly assembly;
        try
        {
            assembly = context.LoadFromOutput(targetPath);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new CompileDiagnostic("Error", "CPN012", $"Could not load {targetPath}: {ex.Message}", 0, 0));
            return new LoadResult(null, null, [], diagnostics, project);
        }

        WarnOnCoreMismatch(targetPath, diagnostics);

        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        var modelTypes = types
            .Where(t => IsModel(t))
            .ToDictionary(t => t.FullName!, t => t);

        var modelsInFile = declared
            .Where(c => modelTypes.ContainsKey(c.FullName))
            .ToList();

        if (modelsInFile.Count == 0)
        {
            diagnostics.Add(new CompileDiagnostic("Error", "CPN001",
                $"{Path.GetFileName(modelFile)} declares no class inheriting CpnModel " +
                $"(the project has {modelTypes.Count}: {string.Join(", ", modelTypes.Keys.Take(8))}).", 0, 0));
            return new LoadResult(null, null, [], diagnostics, project);
        }

        // Innermost model class whose declaration spans the cursor line; else the first in the file.
        var chosen = cursorLine is int line
            ? modelsInFile.Where(c => c.StartLine <= line && line <= c.EndLine)
                          .OrderByDescending(c => c.StartLine).FirstOrDefault()
            : null;
        chosen ??= modelsInFile[0];

        var type = modelTypes[chosen.FullName];
        try
        {
            var model = (CpnModel)Activator.CreateInstance(type, nonPublic: true)!;
            return new LoadResult(model, type.Name, modelsInFile.Select(c => c.FullName).ToList(), diagnostics, project);
        }
        catch (MissingMethodException)
        {
            diagnostics.Add(new CompileDiagnostic("Error", "CPN002",
                $"{type.Name} has no parameterless constructor, so it cannot be instantiated for preview.", chosen.StartLine, 1));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new CompileDiagnostic("Error", "CPN002",
                $"Failed to instantiate {type.Name}: {ex.InnerException?.Message ?? ex.Message}", chosen.StartLine, 1));
        }
        return new LoadResult(null, null, modelsInFile.Select(c => c.FullName).ToList(), diagnostics, project);
    }

    private static bool IsModel(Type t) =>
        t.IsSubclassOf(typeof(CpnModel)) && !t.IsAbstract && !t.IsSubclassOf(typeof(CpnPage));

    // ── Source inspection ─────────────────────────────────────────────────────

    private sealed record DeclaredClass(string FullName, int StartLine, int EndLine);

    /// <summary>Every class declared in the source, with its reflection full name (nested classes use '+').</summary>
    private static List<DeclaredClass> DeclaredClasses(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var result = new List<DeclaredClass>();
        foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var span = cls.GetLocation().GetLineSpan();
            result.Add(new DeclaredClass(FullName(cls), span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
        }
        return result.OrderBy(c => c.StartLine).ToList();
    }

    private static string FullName(ClassDeclarationSyntax cls)
    {
        var parts = new List<string>();
        SyntaxNode? node = cls;
        while (node != null)
        {
            switch (node)
            {
                case ClassDeclarationSyntax c:
                    parts.Insert(0, parts.Count == 0 ? c.Identifier.Text : c.Identifier.Text + "+");
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Insert(0, ns.Name + ".");
                    break;
            }
            node = node.Parent;
        }
        return string.Concat(parts);
    }

    // ── dotnet CLI ────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(string workingDir, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // The server itself runs under `dotnet run`; the MSBuild node variables it inherits
        // must not leak into the nested build.
        foreach (var key in new[] { "MSBUILDNOINPROCNODE", "MSBuildLoadMicrosoftTargetsReadOnly", "MSBUILDUSESERVER" })
            psi.Environment.Remove(key);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static readonly Regex DiagnosticLine = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\): (?<sev>error|warning) (?<code>[A-Z]+\d+): (?<msg>.*?)( \[.*\])?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static IEnumerable<CompileDiagnostic> ParseDiagnostics(string output)
    {
        var seen = new HashSet<string>();
        foreach (Match m in DiagnosticLine.Matches(output))
        {
            var key = m.Value;
            if (!seen.Add(key)) continue;
            var severity = m.Groups["sev"].Value == "error" ? "Error" : "Warning";
            var file = Path.GetFileName(m.Groups["file"].Value);
            yield return new CompileDiagnostic(severity, m.Groups["code"].Value,
                $"{file}: {m.Groups["msg"].Value}", int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value));
        }
    }

    private static void WarnOnCoreMismatch(string targetPath, List<CompileDiagnostic> diagnostics)
    {
        var builtCore = Path.Combine(Path.GetDirectoryName(targetPath)!, "CSharPN.Core.dll");
        if (!File.Exists(builtCore)) return;
        var hostVersion  = typeof(CpnModel).Assembly.GetName().Version;
        var builtVersion = AssemblyName.GetAssemblyName(builtCore).Version;
        if (hostVersion != builtVersion)
            diagnostics.Add(new CompileDiagnostic("Warning", "CPN013",
                $"The project's CSharPN.Core is {builtVersion}; the visualizer runs {hostVersion}. The model is loaded against the visualizer's version.", 0, 0));
    }

    // ── Load context ──────────────────────────────────────────────────────────

    /// <summary>
    /// Collectible context for one build output. Any assembly the host already has loaded
    /// (CSharPN.Core, CSharPN.Visualizer, the framework) is shared; everything else comes
    /// from the project's output directory via its <c>.deps.json</c>.
    /// </summary>
    /// <remarks>
    /// Assemblies from the output directory are loaded from their bytes, not by path: the
    /// runtime caches PE images per path for as long as an earlier load of that path is
    /// alive, so loading a rebuilt assembly by path from a fresh context silently yields
    /// the previous build. Loading from a stream bypasses that cache. The matching .pdb is
    /// passed along so stack traces keep their line numbers.
    /// </remarks>
    private sealed class ModelLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);
        private readonly string _outputDir = Path.GetDirectoryName(mainAssemblyPath)!;

        public Assembly LoadFromOutput(string path)
        {
            using var dll = new MemoryStream(File.ReadAllBytes(path));
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            if (File.Exists(pdbPath))
            {
                using var pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
                return LoadFromStream(dll, pdb);
            }
            return LoadFromStream(dll);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name))
                return null;                                   // share the host's copy
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path == null) return null;
            return Path.GetDirectoryName(Path.GetFullPath(path))!.StartsWith(_outputDir, StringComparison.Ordinal)
                ? LoadFromOutput(path)
                : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
