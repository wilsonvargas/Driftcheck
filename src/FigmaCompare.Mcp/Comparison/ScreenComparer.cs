using System.Globalization;
using System.Text;
using FigmaCompare.Mcp.Android;
using FigmaCompare.Mcp.Figma;
using SkiaSharp;

namespace FigmaCompare.Mcp.Comparison;

public sealed record ElementDiff(string Name, string Property, string Expected, string Actual);

public sealed record ComparisonResult(
    string Report,
    string SideBySidePath,
    string DiffPath,
    int Matched,
    IReadOnlyList<ElementDiff> Diffs);

public sealed class ScreenComparer(Settings settings)
{
    public ComparisonResult Compare(
        DesignFrame design,
        string designPngPath,
        ScreenCapture capture,
        double tolerance,
        double colorTolerance)
    {
        using var screenshot = SKBitmap.Decode(capture.ScreenshotPath)
            ?? throw new InvalidOperationException("No pude leer la captura del dispositivo.");
        using var designPng = SKBitmap.Decode(designPngPath)
            ?? throw new InvalidOperationException("No pude leer el PNG de Figma.");

        // 1 unidad de Figma = 1 dp cuando el frame mide lo mismo que el dispositivo;
        // si no, escalamos el diseño al ancho lógico del dispositivo.
        var pxToDp = 160.0 / capture.Density;
        var deviceWidthDp = screenshot.Width * pxToDp;
        var scale = deviceWidthDp / design.Bounds.Width;

        var match = ElementMatcher.Match(design.Elements, capture.Elements, scale);
        var (pairs, missing, dx, dy) = (match.Pairs, match.Missing, match.Dx, match.Dy);

        var diffs = new List<ElementDiff>();
        var badElements = new HashSet<DesignElement>(ReferenceEqualityComparer.Instance);
        var pixels = screenshot.Pixels;

        foreach (var (d, s, _) in pairs)
        {
            var expected = d.Bounds.Scale(scale).Offset(dx, dy);
            var actual = s.Bounds;
            var label = Label(d);

            void Check(string prop, double exp, double act)
            {
                if (Math.Abs(exp - act) > tolerance)
                {
                    diffs.Add(new ElementDiff(label, prop, F(exp), F(act)));
                    badElements.Add(d);
                }
            }

            Check("x", expected.X, actual.X);
            Check("y", expected.Y, actual.Y);
            Check("width", expected.Width, actual.Width);
            Check("height", expected.Height, actual.Height);

            var screenText = ElementMatcher.ScreenText(s);
            if (d.Text is not null && screenText is not null && !string.Equals(d.Text.Trim(), screenText.Trim(), StringComparison.Ordinal))
            {
                diffs.Add(new ElementDiff(label, "text", d.Text, screenText));
                badElements.Add(d);
            }

            // El color de relleno solo se compara en formas; en un TEXT el fill es el color de la letra
            // y el muestreo del fondo daría falsos positivos.
            if (d.Fill is { } fill && d.Type != "TEXT")
            {
                var sampled = SampleDominantColor(pixels, screenshot.Width, screenshot.Height, actual.Scale(1 / pxToDp));
                if (sampled is { } c && c.DistanceTo(fill) > colorTolerance)
                {
                    diffs.Add(new ElementDiff(label, "background", fill.Hex, c.Hex));
                    badElements.Add(d);
                }
            }
        }

        var runDir = Path.Combine(settings.OutputDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);

        // El PNG de Figma, escalado y desplazado para superponerse a la captura.
        var dxPx = (float)(dx / pxToDp);
        var dyPx = (float)(dy / pxToDp);
        using var alignedDesign = new SKBitmap(screenshot.Width, screenshot.Height);
        using (var canvas = new SKCanvas(alignedDesign))
        {
            canvas.Clear(SKColors.White);
            var destHeight = designPng.Height * (float)screenshot.Width / designPng.Width;
            using var image = SKImage.FromBitmap(designPng);
            canvas.DrawImage(image, SKRect.Create(dxPx, dyPx, screenshot.Width, destHeight), new SKSamplingOptions(SKFilterMode.Linear));
        }

        var mismatchRatio = WriteDiffImage(screenshot, alignedDesign, pairs, badElements, scale, dx, dy, pxToDp,
            Path.Combine(runDir, "diff.png"));
        WriteSideBySide(alignedDesign, screenshot, Path.Combine(runDir, "side_by_side.png"));

        var report = BuildReport(design, capture, scale, dx, dy, pairs, missing, diffs, mismatchRatio, runDir);
        File.WriteAllText(Path.Combine(runDir, "report.md"), report);

        return new ComparisonResult(report,
            Path.Combine(runDir, "side_by_side.png"),
            Path.Combine(runDir, "diff.png"),
            pairs.Count, diffs);
    }

    static double WriteDiffImage(
        SKBitmap screenshot, SKBitmap design,
        IReadOnlyList<ElementPair> pairs, HashSet<DesignElement> bad,
        double scale, double dx, double dy, double pxToDp, string path)
    {
        const int threshold = 40;
        var a = screenshot.Pixels;
        var b = design.Pixels;
        var mismatched = 0;

        using var output = new SKBitmap(screenshot.Width, screenshot.Height);
        var result = new SKColor[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            var d = Math.Abs(a[i].Red - b[i].Red) + Math.Abs(a[i].Green - b[i].Green) + Math.Abs(a[i].Blue - b[i].Blue);
            if (d > threshold)
            {
                mismatched++;
                result[i] = new SKColor(255, 0, 80);
            }
            else
            {
                // Captura desvanecida para que las diferencias resalten.
                var gray = (byte)(200 + (a[i].Red + a[i].Green + a[i].Blue) / 3 * 55 / 255);
                result[i] = new SKColor(gray, gray, gray);
            }
        }
        output.Pixels = result;

        using (var canvas = new SKCanvas(output))
        using (var ok = new SKPaint { Color = new SKColor(0, 160, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 3 })
        using (var ko = new SKPaint { Color = new SKColor(230, 120, 0), Style = SKPaintStyle.Stroke, StrokeWidth = 5 })
        using (var dashed = new SKPaint { Color = new SKColor(40, 90, 230), Style = SKPaintStyle.Stroke, StrokeWidth = 3, PathEffect = SKPathEffect.CreateDash([12, 8], 0) })
        {
            foreach (var (d, s, _) in pairs)
            {
                var actual = s.Bounds.Scale(1 / pxToDp);
                canvas.DrawRect(ToSk(actual), bad.Contains(d) ? ko : ok);

                if (bad.Contains(d))
                    canvas.DrawRect(ToSk(d.Bounds.Scale(scale).Offset(dx, dy).Scale(1 / pxToDp)), dashed);
            }
        }

        using var data = output.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return (double)mismatched / a.Length;
    }

    static void WriteSideBySide(SKBitmap design, SKBitmap screenshot, string path)
    {
        const int gap = 24;
        using var output = new SKBitmap(design.Width + gap + screenshot.Width, Math.Max(design.Height, screenshot.Height));
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(new SKColor(230, 230, 230));
            using var designImage = SKImage.FromBitmap(design);
            using var screenshotImage = SKImage.FromBitmap(screenshot);
            canvas.DrawImage(designImage, 0, 0, SKSamplingOptions.Default);
            canvas.DrawImage(screenshotImage, design.Width + gap, 0, SKSamplingOptions.Default);
        }

        using var data = output.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>Color más frecuente dentro del elemento (ignorando bordes), cuantizado para absorber antialiasing.</summary>
    static Rgb? SampleDominantColor(SKColor[] pixels, int width, int height, Rect px)
    {
        var insetX = px.Width * 0.2;
        var insetY = px.Height * 0.2;
        int x1 = Math.Clamp((int)(px.X + insetX), 0, width - 1), x2 = Math.Clamp((int)(px.X + px.Width - insetX), 0, width - 1);
        int y1 = Math.Clamp((int)(px.Y + insetY), 0, height - 1), y2 = Math.Clamp((int)(px.Y + px.Height - insetY), 0, height - 1);
        if (x2 <= x1 || y2 <= y1) return null;

        var counts = new Dictionary<int, (int Count, long R, long G, long B)>();
        for (var y = y1; y <= y2; y += 2)
        for (var x = x1; x <= x2; x += 2)
        {
            var c = pixels[y * width + x];
            var key = (c.Red >> 3) << 10 | (c.Green >> 3) << 5 | (c.Blue >> 3);
            counts.TryGetValue(key, out var acc);
            counts[key] = (acc.Count + 1, acc.R + c.Red, acc.G + c.Green, acc.B + c.Blue);
        }

        var top = counts.Values.MaxBy(v => v.Count);
        return new Rgb((byte)(top.R / top.Count), (byte)(top.G / top.Count), (byte)(top.B / top.Count));
    }

    static string BuildReport(
        DesignFrame design, ScreenCapture capture, double scale, double dx, double dy, IReadOnlyList<ElementPair> pairs,
        IReadOnlyList<DesignElement> missing, List<ElementDiff> diffs, double mismatchRatio, string runDir)
    {
        var byMethod = pairs.GroupBy(p => p.Method).ToDictionary(g => g.Key, g => g.Count());
        int Count(MatchMethod m) => byMethod.GetValueOrDefault(m);

        var sb = new StringBuilder();
        sb.AppendLine($"# Comparación: {design.Name}");
        sb.AppendLine();
        sb.AppendLine($"- Dispositivo: {capture.Serial} ({capture.WidthDp:0.#} dp de ancho, densidad {capture.Density})");
        sb.AppendLine($"- Frame de Figma: {design.Bounds.Width:0.#}×{design.Bounds.Height:0.#} → escala {scale:0.###}");
        sb.AppendLine($"- Desplazamiento global estimado: dx={dx:0.#} dp, dy={dy:0.#} dp");
        sb.AppendLine($"- Elementos emparejados: {pairs.Count} de {design.Elements.Count} capas " +
                      $"(por AutomationId {Count(MatchMethod.Name)}, por texto {Count(MatchMethod.Text)}, por posición {Count(MatchMethod.Position)})");
        sb.AppendLine($"- Píxeles distintos: {mismatchRatio:P1}");
        sb.AppendLine($"- Imágenes: {runDir}");
        sb.AppendLine();

        if (pairs.Count == 0)
        {
            sb.AppendLine("## No se emparejó ningún elemento");
            sb.AppendLine();
            sb.AppendLine("Ningún texto ni AutomationId de la app coincide con el diseño. " +
                          "Revisa que la app esté mostrando la pantalla de este frame (mira side_by_side.png).");
        }
        else if (diffs.Count == 0)
        {
            sb.AppendLine("## Sin diferencias en los elementos emparejados");
        }
        else
        {
            sb.AppendLine($"## Diferencias ({diffs.Count})");
            sb.AppendLine();
            sb.AppendLine("| Elemento | Propiedad | Figma | App |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var d in diffs)
                sb.AppendLine($"| {d.Name} | {d.Property} | {d.Expected} | {d.Actual} |");
        }

        // Un texto del diseño que no aparece en la app casi siempre es algo que falta implementar;
        // los contenedores (Auto Layout, instancias) rara vez tienen equivalente y solo harían ruido.
        var missingText = missing.Where(m => m.Text is not null).ToList();
        if (missingText.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Textos del diseño que no están en la app ({missingText.Count})");
            sb.AppendLine();
            foreach (var m in missingText.Take(40))
                sb.AppendLine($"- {Label(m)} ({m.Bounds.Scale(scale)})");
            if (missingText.Count > 40) sb.AppendLine($"- … y {missingText.Count - 40} más");
        }

        var missingOther = missing.Count - missingText.Count;
        if (missingOther > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{missingOther} capas sin texto (contenedores, íconos, formas) no se emparejaron. " +
                          "Para comparar una, pon su nombre de capa como AutomationId en la app.");
        }

        return sb.ToString();
    }

    /// <summary>Nombre para el reporte: las capas de texto suelen llamarse "Text", así que se muestra su contenido.</summary>
    static string Label(DesignElement d)
    {
        if (d.Text is null || d.Text.Trim() == d.Name.Trim()) return d.Name;
        var text = d.Text.ReplaceLineEndings(" ").Trim();
        if (text.Length > 40) text = text[..40] + "…";
        return $"{d.Name} «{text}»";
    }

    static SKRect ToSk(Rect r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
