# CSharPN

**A code-first framework for Coloured Petri Net modelling with C# as the inscription language.**

CSharPN lets you model, simulate, and visualise [Coloured Petri Nets](https://en.wikipedia.org/wiki/Coloured_Petri_net) (CPNs) without a dedicated graphical editor or a separate inscription language. A model is an ordinary C# class: colour sets are C# types, arc expressions and guards are lambda expressions, and the whole model enjoys IntelliSense, compile-time type checking, and the full .NET ecosystem.

An interactive Blazor-based visualiser provides animation and step-by-step simulation — including directly inside VS Code via a companion extension.

This repository accompanies the paper *“CSharPN: A Code-First Framework for Coloured Petri Net Modelling with C# as Inscription Language”*, accepted at **PNSE'26** (International Workshop on Petri Nets and Software Engineering, satellite workshop of Petri Nets 2026).

## A model at a glance

```csharp
using CSharPN.Core;

public class Simple : CpnModel
{
    public readonly Place<int> Input;
    public readonly Place<Result> Results;

    public record Result(int Input, int Output);

    public Simple() : base("Simple")
    {
        Input   = AddPlace("Input", Multiset.Of(1, 2, 3));
        Results = AddPlace<Result>("Output");

        var x = new Var<int>("x");

        AddTransition("Double")
            .Input(Input, x)
            .Output(Results, () => new(x, x * 2))
            .Build();
    }
}
```

CSharPN supports **untimed**, **timed**, and **hierarchical** CPNs within a single framework.

## Repository layout

| Path | Description |
|------|-------------|
| `src/CSharPN.Core` | The core framework: places, transitions, arcs, multisets, simulator (untimed, timed, hierarchical). |
| `src/CSharPN.Visualizer` | Blazor visualiser components and layout engine. |
| `src/CSharPN.Visualizer.Server` | Blazor Server host with the server-side C# editor (Roslyn). |
| `src/CSharPN.Wasm` | Blazor WebAssembly build of the visualiser. |
| `examples/` | Classic, timed, hierarchical, and user-API example models. |
| `tests/` | xUnit test suite for the core framework. |
| `tools/` | Standalone packing/test tooling. |
| `scripts/` | Build and serve scripts. |
| `vscode-extension/` | VS Code extension for live in-editor CPN preview. |

## Requirements

- [.NET SDK 10.0+](https://dotnet.microsoft.com/)

## Build & test

```bash
dotnet build CSharPN.sln --configuration Release
dotnet test  CSharPN.sln --configuration Release
```

## Run an example

```bash
dotnet run --project examples/ClassicExamples
```

## Run the visualiser

The Blazor Server visualiser (full features, including the in-browser C# editor):

```bash
./scripts/serve.sh                 # http://localhost:5000
./scripts/serve.sh examples/ClassicExamples/Simple.cs   # single model, hot-reload
```

The WebAssembly build (no server, static hosting):

```bash
./scripts/build-and-serve-wasm.sh  # http://localhost:8080
```

A hosted WebAssembly build is available at <https://simontjell.github.io/CSharPN.public/>.

## VS Code extension

The `vscode-extension/` folder contains *CPN Preview*, which opens the visualiser in a side panel next to your model source and hot-reloads on save. See [`vscode-extension/README.md`](vscode-extension/README.md) for build and install instructions.

## Citing

If you use CSharPN in academic work, please cite the PNSE'26 paper. A BibTeX entry will be added here once the proceedings are published.

## License

Released under the [MIT License](LICENSE).
