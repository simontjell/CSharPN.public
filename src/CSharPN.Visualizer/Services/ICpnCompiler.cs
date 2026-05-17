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
}
