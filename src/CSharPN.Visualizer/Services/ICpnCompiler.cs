using CSharPN.Core;

namespace CSharPN.Visualizer.Services;

public sealed record CompileDiagnostic(
    string Severity,
    string Code,
    string Message,
    int Line,
    int Column);

public sealed record CompileResult(
    CpnModel? Model,
    IReadOnlyList<CompileDiagnostic> Diagnostics);

/// <summary>
/// Optional service — registered only on the Server host.
/// Absent in WASM builds; resolved via IServiceProvider.GetService.
/// </summary>
public interface ICpnCompiler
{
    CompileResult Compile(string source);

    /// <summary>
    /// Compiles a model that may be split across several source files (e.g.
    /// <c>Domain.cs</c> + <c>Model.cs</c>). Files using top-level statements (a driver
    /// <c>Program.cs</c>) are ignored.
    /// </summary>
    CompileResult Compile(IReadOnlyList<string> sources);
}
