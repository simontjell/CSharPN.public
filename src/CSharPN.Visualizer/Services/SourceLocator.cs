using System.Text.RegularExpressions;

namespace CSharPN.Visualizer.Services;

/// <summary>
/// Locates the declarations of a model's variables in its source text, and finds the
/// variables inside an arc inscription, so a double-click on a variable can open its
/// declaration. Text-based, like the element source map: best effort, no compilation.
/// </summary>
public static partial class SourceLocator
{
    // `var k = new Var<int>(…)`, `Var<int> k = new(…)`, `Var<int> k = new Var<int>(…)`,
    // optionally with an explicit name as the first argument: `new Var<int>("n")`.
    [GeneratedRegex("""\bvar\s+@?(?<id>[A-Za-z_]\w*)\s*=\s*new\s+Var\s*<[^;]*?>\s*\(\s*(?:"(?<name>[^"]*)")?""")]
    private static partial Regex VarLocal();

    [GeneratedRegex("""\bVar\s*<[^;=]*>\s+@?(?<id>[A-Za-z_]\w*)\s*=\s*new\s*(?:Var\s*<[^;]*?>\s*)?\(\s*(?:"(?<name>[^"]*)")?""")]
    private static partial Regex VarTyped();

    // An identifier not preceded by '.', or a string literal (skipped).
    [GeneratedRegex("""(?<str>"(?:[^"\\]|\\.)*")|(?<![.\w])(?<id>[A-Za-z_]\w*)""")]
    private static partial Regex Token();

    /// <summary>
    /// Variable name → 1-based lines of its declarations, in source order. The name is the one
    /// bindings use: the explicit name argument if given, otherwise the C# identifier.
    /// </summary>
    public static Dictionary<string, List<int>> FindVariableDeclarations(string source)
    {
        var map = new Dictionary<string, List<int>>();
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            foreach (var regex in new[] { VarLocal(), VarTyped() })
                foreach (Match m in regex.Matches(lines[i]))
                {
                    var name = m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["id"].Value;
                    if (!map.TryGetValue(name, out var list)) map[name] = list = [];
                    if (!list.Contains(i + 1)) list.Add(i + 1);
                }
        return map;
    }

    /// <summary>
    /// The declaration of <paramref name="name"/> to open: the last one at or before
    /// <paramref name="nearLine"/> (the transition using it — a local of the same constructor),
    /// otherwise the first. Null when the variable is not declared in the source.
    /// </summary>
    public static int? DeclarationLine(IReadOnlyDictionary<string, List<int>> declarations, string name, int? nearLine)
    {
        if (!declarations.TryGetValue(name, out var lines) || lines.Count == 0) return null;
        if (nearLine is int near && lines.Where(l => l <= near).DefaultIfEmpty(0).Max() is var before and > 0)
            return before;
        return lines[0];
    }

    /// <summary>
    /// Splits an inscription into consecutive parts, marking the identifiers that are among
    /// <paramref name="variables"/>. Member names (after '.') and string literals are never
    /// variables. Concatenating the parts gives back the inscription.
    /// </summary>
    public static List<(string Text, bool IsVariable)> SplitInscription(string inscription, IReadOnlySet<string> variables)
    {
        var parts = new List<(string, bool)>();
        int pos = 0;
        foreach (Match m in Token().Matches(inscription))
        {
            if (!m.Groups["id"].Success || !variables.Contains(m.Value)) continue;
            if (m.Index > pos) parts.Add((inscription[pos..m.Index], false));
            parts.Add((m.Value, true));
            pos = m.Index + m.Length;
        }
        if (pos < inscription.Length) parts.Add((inscription[pos..], false));
        return parts;
    }
}
