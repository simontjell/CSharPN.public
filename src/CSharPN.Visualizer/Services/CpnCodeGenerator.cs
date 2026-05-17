using System.Text;
using CSharPN.Visualizer.Layout;

namespace CSharPN.Visualizer.Services;

/// <summary>
/// Generates a compilable C# CpnModel subclass from visual net structure
/// and per-element annotation dictionaries.
/// </summary>
public static class CpnCodeGenerator
{
    /// <param name="nodes">From LayoutEngine.</param>
    /// <param name="arcs">From LayoutEngine.</param>
    /// <param name="placeTypes">placeId → C# type, e.g. "int"</param>
    /// <param name="placeInits">placeId → C# init expression, e.g. "Multiset.Of(1,2,3)"</param>
    /// <param name="transGuards">transId → C# bool expression, e.g. "x.Val > 0"</param>
    /// <param name="arcInscriptions">(fromId,toId) → inscription:
    ///   input arc  (place→trans): variable name, e.g. "x"
    ///   output arc (trans→place): C# expression,  e.g. "x.Val + 1"
    /// </param>
    /// <param name="declarations">Free-text C# placed before the class (usings, type aliases, helpers).</param>
    public static string Generate(
        IEnumerable<LayoutNode> nodes,
        IEnumerable<LayoutArc>  arcs,
        IReadOnlyDictionary<string, string>           placeTypes,
        IReadOnlyDictionary<string, string>           placeInits,
        IReadOnlyDictionary<string, string>           transGuards,
        IReadOnlyDictionary<(string, string), string> arcInscriptions,
        string declarations = "",
        string modelName    = "EditedModel")
    {
        var nodeMap = nodes.ToDictionary(n => n.Id);
        var arcList = arcs.ToList();
        var sb      = new StringBuilder();

        // ── Preamble ──
        sb.AppendLine("using CSharPN.Core;");
        if (!string.IsNullOrWhiteSpace(declarations))
            sb.AppendLine(declarations);
        sb.AppendLine();
        sb.AppendLine($"public class {modelName} : CpnModel");
        sb.AppendLine("{");
        sb.AppendLine($"    public {modelName}()");
        sb.AppendLine("    {");

        // ── Place variables ──
        foreach (var p in nodeMap.Values.Where(n => n.IsPlace))
        {
            var type = placeTypes.GetValueOrDefault(p.Id, "int");
            var init = placeInits.GetValueOrDefault(p.Id, "");
            var pv   = PVar(p.Id);
            if (string.IsNullOrWhiteSpace(init))
                sb.AppendLine($"        var {pv} = AddPlace<{type}>(\"{p.Label}\");");
            else
                sb.AppendLine($"        var {pv} = AddPlace<{type}>(\"{p.Label}\", {init});");
        }

        // ── Var<T> declarations (one per unique variable name on input arcs) ──
        var seenVars = new Dictionary<string, string>(); // varName → C# type
        foreach (var arc in arcList)
        {
            if (!nodeMap.TryGetValue(arc.FromId, out var fn) || !fn.IsPlace) continue;
            if (!nodeMap.TryGetValue(arc.ToId,   out var tn) || tn.IsPlace)  continue;
            // place → transition = input arc
            var insc = arcInscriptions.GetValueOrDefault((arc.FromId, arc.ToId), "");
            if (!IsVarName(insc) || seenVars.ContainsKey(insc)) continue;
            seenVars[insc] = placeTypes.GetValueOrDefault(arc.FromId, "int");
        }

        if (seenVars.Count > 0)
        {
            sb.AppendLine();
            foreach (var (varName, varType) in seenVars)
                sb.AppendLine($"        var {varName} = new Var<{varType}>(\"{varName}\");");
        }

        // ── Transitions ──
        sb.AppendLine();
        foreach (var t in nodeMap.Values.Where(n => !n.IsPlace))
        {
            sb.AppendLine($"        AddTransition(\"{t.Label}\")");

            // Input arcs (place → transition)
            foreach (var arc in arcList.Where(a => a.ToId == t.Id))
            {
                if (!nodeMap.TryGetValue(arc.FromId, out var fn2) || !fn2.IsPlace) continue;
                var insc = arcInscriptions.GetValueOrDefault((arc.FromId, arc.ToId), "");
                if (!string.IsNullOrWhiteSpace(insc))
                    sb.AppendLine($"            .Input({PVar(arc.FromId)}, {insc})");
            }

            // Guard
            var guard = transGuards.GetValueOrDefault(t.Id, "");
            if (!string.IsNullOrWhiteSpace(guard))
                sb.AppendLine($"            .Guard(() => {guard})");

            // Output arcs (transition → place)
            foreach (var arc in arcList.Where(a => a.FromId == t.Id))
            {
                if (!nodeMap.TryGetValue(arc.ToId, out var tn2) || !tn2.IsPlace) continue;
                var insc = arcInscriptions.GetValueOrDefault((arc.FromId, arc.ToId), "");
                if (!string.IsNullOrWhiteSpace(insc) && insc != "?")
                    sb.AppendLine($"            .Output({PVar(arc.ToId)}, () => {insc})");
            }

            sb.AppendLine("            .Build();");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // CPN net IDs may contain spaces or special chars; sanitise for C# identifiers.
    internal static string PVar(string id)
        => "p_" + new string(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    // A "simple" inscription is a single identifier (variable name), not an expression.
    internal static bool IsVarName(string s)
        => s.Length > 0 && char.IsLetter(s[0]) && s.All(c => char.IsLetterOrDigit(c) || c == '_');
}
