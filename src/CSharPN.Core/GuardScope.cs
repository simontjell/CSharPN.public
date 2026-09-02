namespace CSharPN.Core;

/// <summary>
/// Runtime backstop for the guard rule, which <see cref="GuardRule"/> states and
/// enforces when a transition is built. This type exists only for what reading the
/// guard's expression tree cannot show: a method called from the guard that reaches
/// a place's marking inside its own body — the tree shows a call, not what it does.
/// </summary>
/// <remarks>
/// While <see cref="Strict"/> is on, every marking read made during a guard evaluation
/// is recorded and reported. It is off by default: the build-time check already covers
/// everything reachable syntactically.
/// </remarks>
public static class GuardScope
{
    /// <summary>
    /// When true, a guard that reads any place's marking throws — including through
    /// a method call, which the build-time check cannot see.
    /// Process-wide; intended for development and tests.
    /// </summary>
    public static bool Strict { get; set; }

    [ThreadStatic] private static bool    _recording;
    [ThreadStatic] private static IPlace? _offender;

    /// <summary>Called from <see cref="Place{T}.Marking"/>. No-op unless a guard is running.</summary>
    internal static void RecordRead(IPlace place)
    {
        if (_recording) _offender ??= place;
    }

    /// <summary>
    /// Evaluates <paramref name="guard"/>, reporting the first place it read, if any.
    /// When <see cref="Strict"/> is off this is a plain delegate call.
    /// </summary>
    internal static bool Evaluate(Func<bool> guard, out IPlace? readPlace)
    {
        if (!Strict)
        {
            readPlace = null;
            return guard();
        }

        // Guards run one at a time per thread and never nest, so a single
        // per-thread slot is enough.
        _offender  = null;
        _recording = true;
        try
        {
            return guard();
        }
        finally
        {
            _recording = false;
            readPlace  = _offender;
            _offender  = null;
        }
    }
}
