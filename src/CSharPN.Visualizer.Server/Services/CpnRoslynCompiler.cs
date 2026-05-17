using System.Reflection;
using System.Runtime.Loader;
using CSharPN.Core;
using CSharPN.Visualizer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharPN.Visualizer.Server.Services;

/// <summary>
/// Server-side Roslyn compiler. Builds metadata references from loaded
/// assemblies (Assembly.Location works on the server, not in WASM).
/// Registered as a singleton so references are built only once.
/// </summary>
public sealed class CpnRoslynCompiler : ICpnCompiler
{
    private readonly IReadOnlyList<MetadataReference> _refs;

    public CpnRoslynCompiler()
    {
        _refs = BuildReferences();
    }

    private static List<MetadataReference> BuildReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs  = new List<MetadataReference>();

        void Add(Assembly asm)
        {
            if (asm.IsDynamic) return;
            var loc = asm.Location;
            if (string.IsNullOrEmpty(loc) || !seen.Add(loc)) return;
            try { refs.Add(MetadataReference.CreateFromFile(loc)); } catch { }
        }

        // Pin known assemblies explicitly — under dotnet watch their Location
        // may be empty when enumerated via AppDomain, so we anchor them first.
        Add(typeof(CpnModel).Assembly);                                    // CSharPN.Core
        Add(typeof(object).Assembly);                                      // System.Private.CoreLib
        Add(typeof(Enumerable).Assembly);                                  // System.Linq
        Add(typeof(Console).Assembly);                                     // System.Console
        Add(typeof(System.Linq.Expressions.Expression).Assembly);         // System.Linq.Expressions

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            Add(asm);

        return refs;
    }

    public CompileResult Compile(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable);

        var compilation = CSharpCompilation.Create(
            assemblyName: $"UserModel_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: _refs,
            options: options);

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        var diagnostics = emitResult.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new CompileDiagnostic(
                    Severity: d.Severity.ToString(),
                    Code: d.Id,
                    Message: d.GetMessage(),
                    Line:   span.StartLinePosition.Line + 1,
                    Column: span.StartLinePosition.Character + 1);
            })
            .ToList();

        if (!emitResult.Success)
            return new CompileResult(null, diagnostics);

        ms.Seek(0, SeekOrigin.Begin);
        var asm = AssemblyLoadContext.Default.LoadFromStream(ms);

        var modelType = asm.GetTypes()
            .FirstOrDefault(t => t.IsSubclassOf(typeof(CpnModel))
                              && !t.IsAbstract
                              && !t.IsSubclassOf(typeof(CpnPage)));

        if (modelType == null)
        {
            diagnostics.Add(new CompileDiagnostic(
                "Error", "CPN001",
                "No public class inheriting CpnModel was found in the compiled code.",
                0, 0));
            return new CompileResult(null, diagnostics);
        }

        try
        {
            var model = (CpnModel)Activator.CreateInstance(modelType)!;
            return new CompileResult(model, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new CompileDiagnostic(
                "Error", "CPN002",
                $"Failed to instantiate {modelType.Name}: {ex.InnerException?.Message ?? ex.Message}",
                0, 0));
            return new CompileResult(null, diagnostics);
        }
    }
}
