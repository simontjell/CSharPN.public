# CSharPN.Core

**A code-first framework for Coloured Petri Net modelling with C# as the inscription language.**

CSharPN lets you model, simulate, and visualise [Coloured Petri Nets](https://en.wikipedia.org/wiki/Coloured_Petri_net) (CPNs) without a dedicated graphical editor or a separate inscription language. A model is an ordinary C# class: colour sets are C# types, arc expressions and guards are lambda expressions, and the whole model enjoys IntelliSense, compile-time type checking, and the full .NET ecosystem.

`CSharPN.Core` is the core library: places, transitions, arcs, multisets, and the untimed, timed, and hierarchical simulators.

## Install

```bash
dotnet add package CSharPN.Core
```

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

## Links

- Source & documentation: <https://github.com/simontjell/CSharPN.public>
- Hosted visualiser: <https://simontjell.github.io/CSharPN.public/>
- Binding, enabling and occurrence semantics mapped to Jensen & Kristensen (2009): `SEMANTICS.md` in the repository root

## License

Released under the [MIT License](https://github.com/simontjell/CSharPN.public/blob/main/LICENSE).
