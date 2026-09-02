using System.Linq.Expressions;
using System.Reflection;

namespace CSharPN.Core;

/// <summary>
/// Checks the rule that a guard is an expression over the values of its transition's
/// variables — and over nothing that could carry state — and reports which variables
/// it reads.
/// </summary>
/// <remarks>
/// <para>
/// This is CPN Tools' rule that guards must not depend on reference variables, and the
/// reason is not stylistic: if a guard reads a marking, enabledness stops being a
/// property of the binding. Two binding elements could both be enabled although firing
/// one must disable the other, a step containing both could occur, and the read leaves
/// no arc, so the net claims an independence the model does not have.
/// </para>
/// <para>
/// Every capture in a lambda surfaces in the expression tree as a member read on a
/// constant — the closure display class, or the model itself when only <c>this</c> was
/// captured. Each is classified by what it actually holds:
/// </para>
/// <list type="bullet">
///   <item><description>a <see cref="Var{T}"/> — the intended form; recorded as used;</description></item>
///   <item><description>a value type or string — a constant of the net, like a CPN declaration;</description></item>
///   <item><description>anything else — rejected. A place, the model, a sub-page or any other
///   reference could carry state.</description></item>
/// </list>
/// <para>
/// A variable the guard reads that no input arc binds is not rejected here: it is a
/// <em>free variable</em> and is bound by enumerating its colour set, or rejected by
/// <see cref="TransitionBuilder.Build"/> when the colour set is not enumerable
/// (see <c>SEMANTICS.md</c>). The one thing the tree cannot show is a method that reads
/// model state inside its own body; <see cref="GuardScope"/> covers that at runtime.
/// </para>
/// </remarks>
internal static class GuardRule
{
    /// <summary>The rule and its remedy, appended to every message that reports a violation.</summary>
    public const string Requirement =
        "A guard may only be expressed over the values of the transition's variables. " +
        "Bind what the condition needs on an input arc and test the bound value — for a " +
        "condition over a whole collection, such as the absence of a row, hold that " +
        "collection as a single token.";

    /// <summary>
    /// Inspects a guard expression. <c>Problem</c> is null when it complies;
    /// <c>Variables</c> lists the binding variables it reads, in order of first use.
    /// </summary>
    public static (string? Problem, IReadOnlyList<IVar> Variables) Inspect(LambdaExpression guard)
    {
        var walker = new Walker();
        walker.Visit(guard.Body);
        return (walker.Problem, walker.Variables);
    }

    private sealed class Walker : ExpressionVisitor
    {
        public string? Problem;
        public readonly List<IVar> Variables = [];

        protected override Expression VisitMember(MemberExpression node)
        {
            // Captures are always a member read rooted at a constant.
            if (node.Expression is ConstantExpression { Value: not null } owner)
            {
                Classify(ReadMember(node.Member, owner.Value), node.Member.Name);
                return node;
            }
            return base.VisitMember(node);
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            // A bare `this` reaches here when the model was used directly rather than
            // through one of its members.
            if (node.Value is CpnModel) Problem ??= "it captures the model";
            return node;
        }

        private void Classify(object? value, string name)
        {
            switch (value)
            {
                case IVar v:
                    if (!Variables.Contains(v)) Variables.Add(v);
                    return;

                case IPlace p:
                    Problem ??= $"it reads the marking of place \"{p.Name}\"";
                    return;

                case CpnModel:
                    Problem ??= $"it captures the model, through \"{name}\"";
                    return;

                case null:
                    return;

                default:
                    var type = value.GetType();
                    if (!type.IsValueType && type != typeof(string))
                        Problem ??= $"it captures \"{name}\", a {NiceName(type)}, which could carry state";
                    return;
            }
        }

        private static object? ReadMember(MemberInfo member, object target) => member switch
        {
            FieldInfo f    => f.GetValue(target),
            PropertyInfo p => p.GetValue(target),
            _              => null
        };

        private static string NiceName(Type t) =>
            t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] : t.Name;
    }
}
