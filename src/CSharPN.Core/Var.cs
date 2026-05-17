namespace CSharPN.Core;

/// <summary>
/// Non-generic interface used internally to apply and clear variable bindings
/// without knowing the concrete type.
/// </summary>
internal interface IVar
{
    string Name { get; }
    void BindObject(object value);
    void Unbind();
    bool IsBound { get; }
    object GetValue();
}

/// <summary>
/// A typed binding variable for CPN transition inscriptions.
/// Declare one per logical variable in a transition; the framework binds it
/// to concrete token values during enabled-binding enumeration and firing.
/// </summary>
/// <example>
/// <code>
/// var p = new Var&lt;Philosopher&gt;("p");
/// AddTransition("StartEating")
///     .Input(Hungry, p)
///     .Guard(() => p.Val.Id > 0)
///     .Output(Eating, () => Multiset.Of(p.Val))
///     .Build();
/// </code>
/// </example>
public sealed class Var<T> : IVar
    where T : notnull
{
    public string Name { get; }

    private T _val = default!;

    /// <summary>
    /// The currently bound value. Only valid while the framework is enumerating
    /// bindings or firing a transition; throws otherwise.
    /// </summary>
    public T Val => IsBound
        ? _val
        : throw new InvalidOperationException(
            $"Variable '{Name}' is not bound. " +
            "Access Var.Val only inside arc expressions or guards.");

    public bool IsBound { get; private set; }

    public Var(string name = "")
    {
        Name = name;
    }

    internal void Bind(T value)
    {
        _val = value;
        IsBound = true;
    }

    internal void Unbind()
    {
        _val = default!;
        IsBound = false;
    }

    // IVar
    void IVar.BindObject(object value) => Bind((T)value);
    void IVar.Unbind() => Unbind();
    object IVar.GetValue() => Val!;

    /// <summary>
    /// Implicit conversion to <typeparamref name="T"/> so that arc expressions can be
    /// written as <c>() => x * 2</c> instead of <c>() => x.Val * 2</c>.
    /// Only valid while the variable is bound (i.e. inside arc expressions / guards).
    /// </summary>
    public static implicit operator T(Var<T> v) => v.Val;

    public override string ToString() => IsBound ? $"{Name}={Val}" : $"{Name}=<unbound>";
}
