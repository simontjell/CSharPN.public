using System.Collections;
using System.Text;

namespace CSharPN.Core;

/// <summary>
/// Immutable multiset (bag) of typed tokens.
/// The fundamental data structure for CPN place markings.
/// </summary>
public sealed class Multiset<T> : IEnumerable<T>, IEquatable<Multiset<T>>
    where T : notnull
{
    private static readonly EqualityComparer<T> Eq = EqualityComparer<T>.Default;

    public static readonly Multiset<T> Empty = new(new Dictionary<T, int>(Eq));

    private readonly Dictionary<T, int> _data;

    private Multiset(Dictionary<T, int> data) => _data = data;

    /// <summary>How many copies of <paramref name="item"/> are in this multiset.</summary>
    public int Count(T item) => _data.TryGetValue(item, out var n) ? n : 0;

    /// <summary>Total number of tokens (sum of all multiplicities).</summary>
    public int TotalCount => _data.Values.Sum();

    public bool IsEmpty => _data.Count == 0;

    /// <summary>Each distinct token value (without repetition).</summary>
    public IEnumerable<T> DistinctItems() => _data.Keys;

    // ── Builders ─────────────────────────────────────────────────────────────

    public Multiset<T> Add(T item, int count = 1)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive.");
        var d = new Dictionary<T, int>(_data, Eq);
        d[item] = (d.TryGetValue(item, out var n) ? n : 0) + count;
        return new Multiset<T>(d);
    }

    public Multiset<T> Remove(T item, int count = 1)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive.");
        var current = Count(item);
        if (current < count)
            throw new InvalidOperationException(
                $"Cannot remove {count} copies of '{item}' from multiset that only has {current}.");
        var d = new Dictionary<T, int>(_data, Eq);
        if (current == count) d.Remove(item);
        else d[item] = current - count;
        return new Multiset<T>(d);
    }

    // ── Operators ─────────────────────────────────────────────────────────────

    /// <summary>Multiset union (add all tokens from both sides).</summary>
    public static Multiset<T> operator +(Multiset<T> a, Multiset<T> b)
    {
        var d = new Dictionary<T, int>(a._data, Eq);
        foreach (var kvp in b._data)
            d[kvp.Key] = (d.TryGetValue(kvp.Key, out var n) ? n : 0) + kvp.Value;
        return new Multiset<T>(d);
    }

    /// <summary>Multiset difference. Throws if b is not a subset of a.</summary>
    public static Multiset<T> operator -(Multiset<T> a, Multiset<T> b)
    {
        var d = new Dictionary<T, int>(a._data, Eq);
        foreach (var kvp in b._data)
        {
            var have = d.TryGetValue(kvp.Key, out var c) ? c : 0;
            if (have < kvp.Value)
                throw new InvalidOperationException(
                    $"Multiset subtraction underflow for '{kvp.Key}': have {have}, removing {kvp.Value}.");
            if (have == kvp.Value) d.Remove(kvp.Key);
            else d[kvp.Key] = have - kvp.Value;
        }
        return new Multiset<T>(d);
    }

    /// <summary>Scalar multiplication.</summary>
    public static Multiset<T> operator *(int n, Multiset<T> m)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "Scalar must be non-negative.");
        if (n == 0) return Empty;
        var d = new Dictionary<T, int>(Eq);
        foreach (var kvp in m._data) d[kvp.Key] = kvp.Value * n;
        return new Multiset<T>(d);
    }

    /// <summary>True if every token in <paramref name="a"/> also exists in <paramref name="b"/> with at least as high multiplicity.</summary>
    public static bool operator <=(Multiset<T> a, Multiset<T> b)
    {
        foreach (var kvp in a._data)
            if (b.Count(kvp.Key) < kvp.Value)
                return false;
        return true;
    }

    public static bool operator >=(Multiset<T> a, Multiset<T> b) => b <= a;

    // ── Enumeration ───────────────────────────────────────────────────────────

    /// <summary>Flat iteration: each token yielded once per multiplicity.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        foreach (var kvp in _data)
            for (int i = 0; i < kvp.Value; i++)
                yield return kvp.Key;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ── Equality ──────────────────────────────────────────────────────────────

    public bool Equals(Multiset<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_data.Count != other._data.Count) return false;
        foreach (var kvp in _data)
            if (!other._data.TryGetValue(kvp.Key, out var n) || n != kvp.Value)
                return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Multiset<T> m && Equals(m);

    /// <summary>
    /// Order-independent hash code: XOR of all (token, count) pair hashes.
    /// Suitable for use as dictionary key in state-space exploration.
    /// </summary>
    public override int GetHashCode()
    {
        int hash = 0;
        foreach (var kvp in _data)
            hash ^= HashCode.Combine(kvp.Key, kvp.Value);
        return hash;
    }

    public static bool operator ==(Multiset<T> a, Multiset<T> b) => a.Equals(b);
    public static bool operator !=(Multiset<T> a, Multiset<T> b) => !a.Equals(b);

    // ── Display ───────────────────────────────────────────────────────────────

    /// <summary>CPN-style notation: <c>1`a + 2`b</c></summary>
    public override string ToString()
    {
        if (_data.Count == 0) return "∅";
        var sb = new StringBuilder();
        bool first = true;
        foreach (var kvp in _data)
        {
            if (!first) sb.Append(" + ");
            if (kvp.Value != 1) sb.Append(kvp.Value).Append('`');
            sb.Append(kvp.Key);
            first = false;
        }
        return sb.ToString();
    }
}

/// <summary>Non-generic factory and helper methods for <see cref="Multiset{T}"/>.</summary>
public static class Multiset
{
    /// <summary>Creates a multiset from a sequence of tokens (duplicates become multiplicity).</summary>
    public static Multiset<T> Of<T>(params T[] items) where T : notnull
    {
        var result = Multiset<T>.Empty;
        foreach (var item in items) result = result.Add(item);
        return result;
    }

    /// <summary>Creates a multiset from an enumerable.</summary>
    public static Multiset<T> Of<T>(IEnumerable<T> items) where T : notnull
    {
        var result = Multiset<T>.Empty;
        foreach (var item in items) result = result.Add(item);
        return result;
    }

    /// <summary>Creates a multiset with <paramref name="times"/> copies of <paramref name="item"/>.</summary>
    public static Multiset<T> Repeat<T>(T item, int times) where T : notnull =>
        times == 0 ? Multiset<T>.Empty : Multiset<T>.Empty.Add(item, times);

    public static Multiset<T> Empty<T>() where T : notnull => Multiset<T>.Empty;
}
