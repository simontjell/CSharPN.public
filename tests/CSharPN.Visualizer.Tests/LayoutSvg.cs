using System.Globalization;
using System.Text;
using CSharPN.Visualizer.Layout;

namespace CSharPN.Visualizer.Tests;

/// <summary>Renders a <see cref="LayoutResult"/> as a stand-alone SVG for visual inspection.</summary>
internal static class LayoutSvg
{
    public static string Render(LayoutResult r)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{r.Width}\" height=\"{r.Height}\" font-family=\"monospace\" font-size=\"11\">\n");
        sb.Append("<defs><marker id=\"ah\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"8\" markerHeight=\"8\" orient=\"auto\"><path d=\"M0,0 L10,5 L0,10 z\" fill=\"#333\"/></marker></defs>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{r.Width}\" height=\"{r.Height}\" fill=\"white\"/>\n");

        var byId = r.Nodes.ToDictionary(n => n.Id);
        foreach (var a in r.Arcs)
        {
            var pts = LayoutGeometry.Polyline(r, a);
            // Trim the ends to the node borders.
            var from = byId[a.FromId]; var to = byId[a.ToId];
            pts[0]  = Border(from, pts[1]);
            pts[^1] = Border(to, pts[^2]);
            var d = string.Join(" ", pts.Select((p, i) => $"{(i == 0 ? "M" : "L")} {F(p.X)} {F(p.Y)}"));
            sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{d}\" fill=\"none\" stroke=\"#333\" stroke-width=\"1.5\" marker-end=\"url(#ah)\"/>\n");
        }
        foreach (var n in r.Nodes)
        {
            if (n.IsPlace)
            {
                var (lines, rx, ry, _) = NodeMetrics.PlaceBox(n.Label);
                sb.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"{F(n.X)}\" cy=\"{F(n.Y)}\" rx=\"{F(rx)}\" ry=\"{F(ry)}\" fill=\"#eef4ff\" stroke=\"#2a5db0\" stroke-width=\"1.5\"/>\n");
                Text(sb, n.X, n.Y, lines, NodeMetrics.PlaceLineH);
            }
            else
            {
                var (lines, w, h, _) = NodeMetrics.TransBox(n.Label);
                sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(n.X - w / 2)}\" y=\"{F(n.Y - h / 2)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"3\" fill=\"#fff6e5\" stroke=\"#c07a1a\" stroke-width=\"1.5\"/>\n");
                Text(sb, n.X, n.Y, lines, NodeMetrics.TransLineH);
            }
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static (double X, double Y) Border(LayoutNode n, (double X, double Y) towards)
    {
        if (n.IsPlace)
        {
            var (_, rx, ry, _) = NodeMetrics.PlaceBox(n.Label);
            return LayoutEngine.EllipseBorderPoint(n.X, n.Y, rx, ry, towards.X, towards.Y);
        }
        var (_, w, h, _) = NodeMetrics.TransBox(n.Label);
        return LayoutEngine.RectBorderPoint(n.X, n.Y, w, h, towards.X, towards.Y);
    }

    private static void Text(StringBuilder sb, double x, double y, List<string> lines, double lineH)
    {
        double top = y - lines.Count * lineH / 2 + lineH / 2;
        for (int i = 0; i < lines.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(x)}\" y=\"{F(top + i * lineH)}\" text-anchor=\"middle\" dominant-baseline=\"central\" font-weight=\"bold\">{System.Net.WebUtility.HtmlEncode(lines[i])}</text>\n");
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
