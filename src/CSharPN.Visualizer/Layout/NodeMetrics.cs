namespace CSharPN.Visualizer.Layout;

/// <summary>
/// Single source of truth for the on-screen size of places and transitions.
/// Used both by the SVG renderer (shape geometry, arc border points) and by the
/// <see cref="LayoutEngine"/> (node separation), so the two never disagree.
/// </summary>
public static class NodeMetrics
{
    // ── Places ──────────────────────────────────────────────────────────────
    public const double PlaceRX     = 38;    // minimum ellipse semi-axis horizontal
    public const double PlaceRY     = 25;    // minimum ellipse semi-axis vertical
    public const double PlacePadX   = 10;    // horizontal text padding inside the ellipse
    public const double PlacePadY   = 6;     // vertical text padding inside the ellipse
    public const double PlaceLineH  = 15;    // line height for the bold 12px name font
    public const double PlaceCharW  = 7.3;   // approx advance of bold 12px monospace

    // ── Transitions ─────────────────────────────────────────────────────────
    public const double TransMinW  = 80;    // minimum rect width
    public const double TransMinH  = 40;    // minimum rect height
    public const double TransMaxW  = 400;   // wrap the label once a line exceeds this width
    public const double TransPadX  = 14;    // horizontal text padding inside the rect
    public const double TransPadY  = 8;     // vertical text padding inside the rect
    public const double TransLineH = 14;    // line height for the bold 11px name font
    public const double NameCharW  = 6.8;   // approx advance of bold 11px monospace
    public const double GuardLineH = 13;    // line height for the guard label
    public const double GuardGap   = 4;     // gap between name block and guard line

    // ── Decorations drawn around the shapes (reserved by the layout) ────────
    public const double TokenBadgeR   = 12;  // token-count badge centred on the right edge of a place
    public const double PlaceLabelH   = 17;  // initial marking above / type name below a place
    public const double GuardLabelH   = GuardGap + GuardLineH;

    /// <summary>
    /// Greedy word-wrap of a label: a new line starts whenever adding the next word
    /// would push the line past <paramref name="maxTextW"/> px (estimated with
    /// <paramref name="charW"/> per character). A single word longer than the max is
    /// left on its own line (no mid-word break).
    /// </summary>
    public static List<string> WrapLabel(string label, double charW, double maxTextW)
    {
        var lines = new List<string>();
        var cur   = "";
        foreach (var word in label.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var cand = cur.Length == 0 ? word : cur + " " + word;
            if (cur.Length == 0 || cand.Length * charW <= maxTextW) cur = cand;
            else { lines.Add(cur); cur = word; }
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count == 0) lines.Add(label);
        return lines;
    }

    /// <summary>
    /// Wrapped name lines and ellipse semi-axes for a place, growing the ellipse to fit
    /// the word-wrapped name within the <see cref="PlaceRX"/>/<see cref="PlaceRY"/>
    /// minimums. The width solves the ellipse equation at the text block's top/bottom
    /// edge so the text stays inside the curve rather than only inside the (smaller)
    /// inscribed rectangle.
    /// </summary>
    public static (List<string> Lines, double Rx, double Ry, double NameBlockH) PlaceBox(string label)
    {
        var lines      = WrapLabel(label, PlaceCharW, TransMaxW - 2 * PlacePadX);
        var nameBlockH = lines.Count * PlaceLineH;

        var contentW = lines.Max(l => l.Length * PlaceCharW);
        var halfW    = contentW / 2 + PlacePadX;
        var halfH    = nameBlockH / 2;

        var ry = Math.Max(PlaceRY, halfH + PlacePadY);
        // At y = ±halfH the ellipse half-width is rx·√(1−(halfH/ry)²); require it ≥ halfW.
        var shrink = Math.Sqrt(Math.Max(0.15, 1 - halfH / ry * (halfH / ry)));
        var rx = Math.Max(PlaceRX, halfW / shrink);

        return (lines, rx, ry, nameBlockH);
    }

    /// <summary>
    /// Wrapped name lines and rectangle size for a transition, growing the box to fit
    /// the label within <see cref="TransMinW"/>..<see cref="TransMaxW"/>. The guard label
    /// is drawn outside the box and does not grow it.
    /// </summary>
    public static (List<string> Lines, double W, double H, double NameBlockH) TransBox(string label)
    {
        var lines      = WrapLabel(label, NameCharW, TransMaxW - 2 * TransPadX);
        var nameBlockH = lines.Count * TransLineH;
        var contentW   = lines.Max(l => l.Length * NameCharW);
        var w          = Math.Clamp(contentW + 2 * TransPadX, TransMinW, TransMaxW);
        var h          = Math.Max(TransMinH, nameBlockH + 2 * TransPadY);
        return (lines, w, h, nameBlockH);
    }

    /// <summary>
    /// The footprint the layout reserves for a node: the shape plus the labels and
    /// badges drawn around it, so that neighbouring nodes never overlap visually.
    /// </summary>
    public static (double W, double H) LayoutFootprint(string label, bool isPlace, bool hasGuard = false)
    {
        if (isPlace)
        {
            var (_, rx, ry, _) = PlaceBox(label);
            return (2 * rx + TokenBadgeR, 2 * ry + 2 * PlaceLabelH);
        }
        var (_, w, h, _) = TransBox(label);
        return (w, h + (hasGuard ? GuardLabelH : 0));
    }
}
