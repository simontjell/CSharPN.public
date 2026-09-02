using CSharPN.Core;

public class Simple : CpnModel
{
    public const int NumReaders = 3;
    public const int NumWriters = 2;
    public const int N = 3;   // total slot count; must equal NumReaders

    // Places
    public readonly Place<int> Input;
    public readonly Place<Result> Results;
    public readonly Place<int> Constants;

    public record Result(int Input, int Output);

    public Simple() : base("Simple")
    {
        Input = AddPlace("Input", Multiset.Of(1, 2, 3));
        Results = AddPlace<Result>("Output");
        Constants = AddPlace("Constants", Multiset.Of(1, 5, 10));

        // Variables
        var x = new Var<int>("x");
        var y = new Var<int>("y");
        var r = new Var<Result>("r");

        AddTransition("Double")
            .Input(Input, x)
            .Input(Constants, y)
            .Output(Results, () => new (x, x * 2 + y))
            .Output(Constants, () => y)
            .Build();

        // The same variable on two input arcs: x must be bound to the same value on both,
        // so Match is only enabled for values present in both Input and Constants (here 1).
        AddTransition("Match")
            .Input(Input, x)
            .Input(Constants, x)
            .Output(Results, () => new (x, x))
            .Output(Constants, () => x)
            .Build();

        AddTransition("Put back output")
            .Input(Results, r)
            .Output(Input, () => r.Val.Output)
            .Build();

        AddTransition("Put back input")
            .Input(Results, r)
            .Output(Input, () => r.Val.Input)
            .Build();

    }
}
