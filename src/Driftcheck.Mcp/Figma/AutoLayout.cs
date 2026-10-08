using System.Text.Json;

namespace Driftcheck.Mcp.Figma;

/// <summary>
/// Recalcula las cajas de los frames con auto layout a partir de sus reglas (dirección, padding,
/// separación, alineación y tamaño fijo / hug / fill).
/// <para>
/// La REST API de Figma puede devolver cajas desactualizadas dentro de instancias de componentes: en el
/// kit Paylo, un campo de texto tiene dos íconos ocultos y el JSON coloca el texto como si ocuparan
/// lugar (x=129 en vez de 49), mientras el render del frame lo muestra bien. Recalcular el layout con
/// los hijos visibles reproduce lo que se ve.
/// </para>
/// Los nodos sin auto layout (o con wrap) conservan su tamaño y se mueven junto con su padre.
/// </summary>
public static class AutoLayout
{
    /// <summary>Caja absoluta recalculada (en coordenadas de Figma) de cada nodo visible, por id.</summary>
    public static Dictionary<string, Rect> Reflow(JsonElement root)
    {
        var result = new Dictionary<string, Rect>();
        if (FigmaClient.ReadBox(root) is { } box)
            Place(root, box, result);
        return result;
    }

    static void Place(JsonElement node, Rect bounds, Dictionary<string, Rect> result)
    {
        if (node.TryGetProperty("id", out var id) && id.GetString() is { } key)
            result[key] = bounds;

        var original = FigmaClient.ReadBox(node) ?? bounds;
        var children = VisibleChildren(node).ToList();

        if (!IsAutoLayout(node))
        {
            foreach (var child in children)
                PlaceKeepingOffset(child, original, bounds, result);
            return;
        }

        var horizontal = Str(node, "layoutMode") == "HORIZONTAL";
        var flow = children.Where(c => Str(c, "layoutPositioning") != "ABSOLUTE").ToList();
        foreach (var child in children.Except(flow))
            PlaceKeepingOffset(child, original, bounds, result);

        double padLeft = Num(node, "paddingLeft"), padRight = Num(node, "paddingRight");
        double padTop = Num(node, "paddingTop"), padBottom = Num(node, "paddingBottom");
        var (padStart, padEnd) = horizontal ? (padLeft, padRight) : (padTop, padBottom);
        var (padCross, padCrossEnd) = horizontal ? (padTop, padBottom) : (padLeft, padRight);
        var innerMain = (horizontal ? bounds.Width : bounds.Height) - padStart - padEnd;
        var innerCross = (horizontal ? bounds.Height : bounds.Width) - padCross - padCrossEnd;
        var gap = Num(node, "itemSpacing");

        var mainSizes = new double[flow.Count];
        var crossSizes = new double[flow.Count];
        var fills = new bool[flow.Count];
        for (var i = 0; i < flow.Count; i++)
        {
            var (w, h) = Measure(flow[i]);
            (mainSizes[i], crossSizes[i]) = horizontal ? (w, h) : (h, w);
            fills[i] = FillsMainAxis(flow[i], horizontal);
            if (FillsCrossAxis(flow[i], horizontal)) crossSizes[i] = innerCross;
        }

        var fillCount = fills.Count(f => f);
        if (fillCount > 0)
        {
            var fixedTotal = mainSizes.Where((_, i) => !fills[i]).Sum() + gap * Math.Max(0, flow.Count - 1);
            var each = Math.Max(0, (innerMain - fixedTotal) / fillCount);
            for (var i = 0; i < flow.Count; i++)
                if (fills[i]) mainSizes[i] = each;
        }

        for (var i = 0; i < flow.Count; i++)
        {
            mainSizes[i] = Clamp(flow[i], horizontal, mainSizes[i]);
            crossSizes[i] = Clamp(flow[i], !horizontal, crossSizes[i]);
        }

        var used = mainSizes.Sum() + gap * Math.Max(0, flow.Count - 1);
        var cursor = padStart;
        switch (Str(node, "primaryAxisAlignItems"))
        {
            case "CENTER": cursor += (innerMain - used) / 2; break;
            case "MAX": cursor += innerMain - used; break;
            case "SPACE_BETWEEN" when flow.Count > 1:
                gap = (innerMain - mainSizes.Sum()) / (flow.Count - 1);
                break;
        }

        var crossAlign = Str(node, "counterAxisAlignItems");
        for (var i = 0; i < flow.Count; i++)
        {
            var cross = padCross + crossAlign switch
            {
                "CENTER" => (innerCross - crossSizes[i]) / 2,
                "MAX" => innerCross - crossSizes[i],
                _ => 0,
            };

            var rect = horizontal
                ? new Rect(bounds.X + cursor, bounds.Y + cross, mainSizes[i], crossSizes[i])
                : new Rect(bounds.X + cross, bounds.Y + cursor, crossSizes[i], mainSizes[i]);
            Place(flow[i], rect, result);
            cursor += mainSizes[i] + gap;
        }
    }

    /// <summary>Tamaño propio del nodo: el del JSON, salvo en los ejes "hug" de un auto layout, que se recalculan.</summary>
    static (double Width, double Height) Measure(JsonElement node)
    {
        var box = FigmaClient.ReadBox(node) ?? new Rect(0, 0, 0, 0);
        if (!IsAutoLayout(node)) return (box.Width, box.Height);

        var horizontal = Str(node, "layoutMode") == "HORIZONTAL";
        var flow = VisibleChildren(node).Where(c => Str(c, "layoutPositioning") != "ABSOLUTE").Select(Measure).ToList();
        var gap = Num(node, "itemSpacing") * Math.Max(0, flow.Count - 1);
        var padX = Num(node, "paddingLeft") + Num(node, "paddingRight");
        var padY = Num(node, "paddingTop") + Num(node, "paddingBottom");

        var width = box.Width;
        var height = box.Height;
        if (Str(node, "layoutSizingHorizontal") == "HUG" && flow.Count > 0)
            width = padX + (horizontal ? flow.Sum(s => s.Width) + gap : flow.Max(s => s.Width));
        if (Str(node, "layoutSizingVertical") == "HUG" && flow.Count > 0)
            height = padY + (horizontal ? flow.Max(s => s.Height) : flow.Sum(s => s.Height) + gap);
        return (Clamp(node, true, width), Clamp(node, false, height));
    }

    /// <summary>Aplica minWidth/maxWidth (o minHeight/maxHeight): un hijo "fill" con maxWidth deja de crecer ahí.</summary>
    static double Clamp(JsonElement node, bool width, double value)
    {
        var (min, max) = width ? ("minWidth", "maxWidth") : ("minHeight", "maxHeight");
        if (node.TryGetProperty(max, out var hi) && hi.ValueKind == JsonValueKind.Number) value = Math.Min(value, hi.GetDouble());
        if (node.TryGetProperty(min, out var lo) && lo.ValueKind == JsonValueKind.Number) value = Math.Max(value, lo.GetDouble());
        return value;
    }

    static void PlaceKeepingOffset(JsonElement child, Rect parentOriginal, Rect parentNow, Dictionary<string, Rect> result)
    {
        if (FigmaClient.ReadBox(child) is { } box)
            Place(child, box.Offset(parentNow.X - parentOriginal.X, parentNow.Y - parentOriginal.Y), result);
    }

    static bool IsAutoLayout(JsonElement node) =>
        Str(node, "layoutMode") is "HORIZONTAL" or "VERTICAL" && Str(node, "layoutWrap") != "WRAP";

    static bool FillsMainAxis(JsonElement child, bool horizontal) =>
        Str(child, horizontal ? "layoutSizingHorizontal" : "layoutSizingVertical") == "FILL" || Num(child, "layoutGrow") > 0;

    static bool FillsCrossAxis(JsonElement child, bool horizontal) =>
        Str(child, horizontal ? "layoutSizingVertical" : "layoutSizingHorizontal") == "FILL" || Str(child, "layoutAlign") == "STRETCH";

    static IEnumerable<JsonElement> VisibleChildren(JsonElement node) =>
        node.TryGetProperty("children", out var children)
            ? children.EnumerateArray().Where(c => !c.TryGetProperty("visible", out var v) || v.GetBoolean())
            : [];

    static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static double Num(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
}
