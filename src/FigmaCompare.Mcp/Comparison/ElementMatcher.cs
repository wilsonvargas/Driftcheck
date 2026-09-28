using FigmaCompare.Mcp.Android;
using FigmaCompare.Mcp.Figma;

namespace FigmaCompare.Mcp.Comparison;

public enum MatchMethod { Name, Text, Position }

public sealed record ElementPair(DesignElement Design, ScreenElement Screen, MatchMethod Method);

public sealed record MatchResult(IReadOnlyList<ElementPair> Pairs, IReadOnlyList<DesignElement> Missing, double Dx, double Dy);

/// <summary>
/// Empareja capas de Figma con elementos de la pantalla. Los diseños reales casi nunca tienen
/// las capas nombradas ("Text", "Auto Layout Vertical"), así que el nombre = AutomationId es solo
/// la primera de tres pasadas:
/// 1. Nombre = AutomationId, cuando el nombre es único en el diseño.
/// 2. Texto visible igual ("Sign up" ↔ el botón que dice "Sign up").
/// 3. Posición: capas de texto que caen sobre un elemento con texto distinto (para reportar la diferencia).
/// </summary>
public static class ElementMatcher
{
    /// <summary>Solapamiento mínimo (intersección / unión) para emparejar por posición.</summary>
    const double MinOverlap = 0.5;

    public static MatchResult Match(IReadOnlyList<DesignElement> design, IReadOnlyList<ScreenElement> screen, double scale)
    {
        var pairs = new List<ElementPair>();
        var usedDesign = new HashSet<DesignElement>(ReferenceEqualityComparer.Instance);
        var usedScreen = new HashSet<ScreenElement>(ReferenceEqualityComparer.Instance);

        void Add(DesignElement d, ScreenElement s, MatchMethod method)
        {
            pairs.Add(new ElementPair(d, s, method));
            usedDesign.Add(d);
            usedScreen.Add(s);
        }

        // 1. Nombre = AutomationId. Un nombre repetido en el diseño ("Text" ×5) no identifica a nadie.
        var uniqueNames = design.GroupBy(d => Normalize(d.Name)).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        var screenById = screen
            .Where(s => s.Id is not null)
            .GroupBy(s => Normalize(s.Id!))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var d in design)
        {
            var key = Normalize(d.Name);
            if (key.Length > 0 && uniqueNames.Contains(key) && screenById.TryGetValue(key, out var s) && !usedScreen.Contains(s))
                Add(d, s, MatchMethod.Name);
        }

        // 2a. Texto único en ambos lados: son anclas seguras para estimar el desplazamiento global.
        var designByText = design.Where(d => !usedDesign.Contains(d) && TextKey(d.Text) is not null)
            .GroupBy(d => TextKey(d.Text)!).ToDictionary(g => g.Key, g => g.ToList());
        var screenByText = screen.Where(s => !usedScreen.Contains(s) && TextKey(ScreenText(s)) is not null)
            .GroupBy(s => TextKey(ScreenText(s))!).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (text, ds) in designByText)
            if (ds.Count == 1 && screenByText.TryGetValue(text, out var ss) && ss.Count == 1)
                Add(ds[0], ss[0], MatchMethod.Text);

        var (dx, dy) = EstimateOffset(pairs, scale);

        // 2b. Texto repetido ("Text", "or"…): el candidato más cercano a donde debería estar.
        foreach (var (text, ds) in designByText)
        {
            if (!screenByText.TryGetValue(text, out var ss)) continue;
            foreach (var d in ds.Where(d => !usedDesign.Contains(d)))
            {
                var expected = d.Bounds.Scale(scale).Offset(dx, dy);
                var nearest = ss.Where(s => !usedScreen.Contains(s)).MinBy(s => CenterDistance(expected, s.Bounds));
                if (nearest is not null) Add(d, nearest, MatchMethod.Text);
            }
        }

        // 3. Posición: solo entre elementos con texto, para detectar textos cambiados sin inventar
        // parejas entre contenedores que no tienen nada que ver.
        var candidates =
            from d in design
            where !usedDesign.Contains(d) && TextKey(d.Text) is not null
            from s in screen
            where !usedScreen.Contains(s) && TextKey(ScreenText(s)) is not null
            let overlap = Overlap(d.Bounds.Scale(scale).Offset(dx, dy), s.Bounds)
            where overlap >= MinOverlap
            orderby overlap descending
            select (d, s);

        foreach (var (d, s) in candidates.ToList())
            if (!usedDesign.Contains(d) && !usedScreen.Contains(s))
                Add(d, s, MatchMethod.Position);

        // Con todas las parejas el desplazamiento es más fiable; la mediana ignora las que estén mal.
        (dx, dy) = EstimateOffset(pairs, scale);

        var missing = design.Where(d => !usedDesign.Contains(d)).ToList();
        return new MatchResult(pairs, missing, dx, dy);
    }

    /// <summary>La app y el diseño pueden estar desplazados en bloque (barra de estado, notch).</summary>
    static (double Dx, double Dy) EstimateOffset(List<ElementPair> pairs, double scale) =>
        (Median(pairs.Select(p => p.Screen.Bounds.X - p.Design.Bounds.X * scale)),
         Median(pairs.Select(p => p.Screen.Bounds.Y - p.Design.Bounds.Y * scale)));

    public static string? ScreenText(ScreenElement s) => s.Text ?? s.ContentDescription;

    /// <summary>Texto comparable: espacios colapsados y sin distinguir mayúsculas.</summary>
    static string? TextKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    static string Normalize(string s) =>
        string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    static double CenterDistance(Rect a, Rect b) =>
        Math.Pow(a.X + a.Width / 2 - (b.X + b.Width / 2), 2) + Math.Pow(a.Y + a.Height / 2 - (b.Y + b.Height / 2), 2);

    static double Overlap(Rect a, Rect b)
    {
        var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0) return 0;
        var intersection = w * h;
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
