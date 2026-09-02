namespace CSharPN.Core;

/// <summary>Public read-only view of a CPN place.</summary>
public interface IPlace
{
    string Name { get; }
    void Reset();
    /// <summary>Total number of tokens currently in this place.</summary>
    int TotalTokenCount { get; }
    /// <summary>Total number of tokens in the initial marking.</summary>
    int InitialTokenCount { get; }
    /// <summary>CPN-notation string of the current marking, e.g. <c>1`a + 2`b</c>.</summary>
    string MarkingString { get; }
    /// <summary>CPN-notation string of the initial marking (as declared in the model).</summary>
    string InitialMarkingString { get; }
    /// <summary>Display name of the colour-set (C# type), e.g. INT, STRING, Packet.</summary>
    string TypeName { get; }
}

/// <summary>Internal interface used by the framework to access markings as boxed objects.</summary>
internal interface IPlaceInternal : IPlace
{
    object GetMarkingObject();
    void SetMarkingObject(object marking);
    /// <summary>Multiset sum <c>a + b</c> of two boxed markings of this place's colour set.</summary>
    object AddMarkingObject(object a, object b);
}

/// <summary>
/// A CPN place whose colour set is the C# type <typeparamref name="T"/>.
/// Holds a <see cref="Multiset{T}"/> as its current marking.
/// </summary>
public sealed class Place<T> : IPlaceInternal
    where T : notnull, IEquatable<T>
{
    public string Name { get; }

    /// <summary>The marking the simulator resets to on <see cref="CpnModel.Reset"/>.</summary>
    public Multiset<T> InitialMarking { get; }

    private Multiset<T> _marking;

    /// <summary>The current marking. Updated atomically by the simulator during transition firing.</summary>
    public Multiset<T> Marking
    {
        get { GuardScope.RecordRead(this); return _marking; }
        internal set => _marking = value;
    }

    public Place(string name, Multiset<T>? initial = null)
    {
        Name = name;
        InitialMarking = initial ?? Multiset<T>.Empty;
        _marking = InitialMarking;
    }

    public void Reset() => Marking = InitialMarking;

    public int    TotalTokenCount      => Marking.TotalCount;
    public int    InitialTokenCount    => InitialMarking.TotalCount;
    public string MarkingString        => Marking.ToString();
    public string InitialMarkingString => InitialMarking.ToString();
    public string TypeName             => NiceTypeName(typeof(T));

    object IPlaceInternal.GetMarkingObject() => Marking;
    void IPlaceInternal.SetMarkingObject(object marking) => Marking = (Multiset<T>)marking;
    object IPlaceInternal.AddMarkingObject(object a, object b) => (Multiset<T>)a + (Multiset<T>)b;

    /// <summary>
    /// Adds one token to the current marking.
    /// Intended for use by external API drivers that inject request tokens into the model.
    /// </summary>
    public void Enqueue(T token) => Marking = Marking.Add(token);

    /// <summary>
    /// Removes and returns one token from the marking.
    /// Returns false (and sets <paramref name="token"/> to default) when the marking is empty.
    /// Intended for use by external API drivers that read response tokens from the model.
    /// </summary>
    public bool TryDequeue([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T token)
    {
        foreach (var item in Marking.DistinctItems())
        {
            Marking = Marking.Remove(item, 1);
            token   = item;
            return true;
        }
        token = default;
        return false;
    }

    public override string ToString() => $"Place<{typeof(T).Name}>(\"{Name}\", {Marking})";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NiceTypeName(Type t)
    {
        if (!t.IsGenericType)
            return t.Name switch
            {
                "Int32"   => "INT",
                "Int64"   => "INT64",
                "String"  => "STRING",
                "Boolean" => "BOOL",
                "Double"  => "REAL",
                "Single"  => "REAL",
                _         => t.Name
            };

        var baseName = t.Name[..t.Name.IndexOf('`')];
        var args     = string.Join(", ", t.GetGenericArguments().Select(NiceTypeName));
        return $"{baseName}<{args}>";
    }
}

