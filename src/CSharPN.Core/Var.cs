namespace CSharPN.Core;

/// <summary>
/// Thrown when <see cref="Var{T}.Val"/> is read while the variable is not bound.
/// Inside the framework this is caught and re-thrown with the name of the
/// offending transition and a hint on how to fix the model.
/// </summary>
public sealed class UnboundVariableException : InvalidOperationException
{
    /// <summary>Name of the variable that was read while unbound.</summary>
    public string VariableName { get; }

    public UnboundVariableException(string variableName, string message) : base(message)
        => VariableName = variableName;
}

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

    /// <summary>
    /// The colour set of the variable as an enumerable set of values, or <see langword="null"/>
    /// when the colour set is not enumerable. Used to bind <em>free variables</em>
    /// (variables occurring only in output-arc expressions or the guard) by trying every value,
    /// exactly as CPN Tools does for variables of small colour sets.
    /// </summary>
    IEnumerable<object>? DomainObjects { get; }
}

/// <summary>
/// A typed binding variable for CPN transition inscriptions (Jensen &amp; Kristensen 2009,
/// Definition 4.2 (5): a variable <c>v ∈ V</c> with <c>Type[v] ∈ Σ</c>).
/// Declare one per logical variable; the framework binds it to concrete token values
/// during enabled-binding enumeration and firing.
/// </summary>
/// <remarks>
/// <para>
/// A variable is bound by the input arcs that carry it (pattern arcs). A variable that appears
/// only in output-arc expressions or in the guard is a <em>free variable</em>; it is bound by
/// enumerating its <see cref="Domain"/> (its colour set), so every value in the domain gives a
/// separate binding element. Variables of type <see cref="bool"/> and enum types get their
/// domain automatically; other types must be given one explicitly:
/// <c>new Var&lt;int&gt;("n", Enumerable.Range(0, 10))</c>.
/// </para>
/// </remarks>
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

    /// <summary>
    /// The colour set of this variable as an enumerable set of values, or <see langword="null"/>
    /// when the colour set is not enumerable (e.g. <c>int</c> without an explicit range).
    /// Only needed when the variable is used as a free variable.
    /// </summary>
    public IReadOnlyCollection<T>? Domain { get; }

    private T _val = default!;

    /// <summary>
    /// The currently bound value. Only valid while the framework is enumerating
    /// bindings or firing a transition; throws otherwise.
    /// </summary>
    public T Val => IsBound
        ? _val
        : throw new UnboundVariableException(Name,
            $"Variable '{Name}' is not bound. " +
            "Access Var.Val only inside arc expressions or guards.");

    public bool IsBound { get; private set; }

    /// <param name="name">Display name (must be unique within a model).</param>
    /// <param name="domain">
    /// Explicit colour-set enumeration used when the variable occurs as a free variable.
    /// Defaults to all values for <see cref="bool"/> and enum types, otherwise <see langword="null"/>.
    /// </param>
    public Var(string name = "", IEnumerable<T>? domain = null)
    {
        Name   = name;
        Domain = domain is not null ? domain.ToList().AsReadOnly() : DefaultDomain();
    }

    private static IReadOnlyCollection<T>? DefaultDomain()
    {
        if (typeof(T) == typeof(bool))
            return (IReadOnlyCollection<T>)(object)new[] { false, true };
        if (typeof(T).IsEnum)
            return Enum.GetValues(typeof(T)).Cast<T>().ToList().AsReadOnly();
        return null;
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
    IEnumerable<object>? IVar.DomainObjects => Domain?.Cast<object>();

    /// <summary>
    /// Implicit conversion to <typeparamref name="T"/> so that arc expressions can be
    /// written as <c>() => x * 2</c> instead of <c>() => x.Val * 2</c>.
    /// Only valid while the variable is bound (i.e. inside arc expressions / guards).
    /// </summary>
    public static implicit operator T(Var<T> v) => v.Val;

    public override string ToString() => IsBound ? $"{Name}={Val}" : $"{Name}=<unbound>";
}
