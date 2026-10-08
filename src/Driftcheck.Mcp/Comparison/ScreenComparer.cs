using System.Globalization;
using System.Text;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Mcp.Comparison;

public sealed record ElementDiff(string Name, string Property, string Expected, string Actual);

public sealed record ComparisonResult(
    string Report,
    string SideBySidePath,
    string DiffPath,
    int Matched,
    IReadOnlyList<ElementDiff> Diffs);

public sealed class ScreenComparer(Settings settings)
{
    const double TextWidthTolerance = 0.025;

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

        var pxToDp = 160.0 / capture.Density;
        var deviceWidthDp = screenshot.Width * pxToDp;
        var scale = ChooseScale(design.Bounds.Width, deviceWidthDp);

        // Ancho que le sobra (o falta) al dispositivo respecto al frame. Con 1:1 en dp, un margen de 35
        // sigue siendo 35: lo que cambia es el ancho de lo que se estira o la x de lo centrado.
        var extraWidth = deviceWidthDp - design.Bounds.Width * scale;

        var match = ElementMatcher.Match(design.Elements, capture.Elements, scale);
        var (pairs, missing) = (match.Pairs, match.Missing);

        var pixels = screenshot.Pixels;
        var designPixels = designPng.Pixels;
        var designPxPerUnit = designPng.Width / design.Bounds.Width;

        // En un texto se comparan las letras visibles, no las cajas: la capa de Figma se ajusta al texto,
        // pero en la app el texto suele estar dentro de una vista más ancha (un TextView que ocupa toda la
        // fila, un botón). Si no se puede medir la letra (poco contraste), se comparan las cajas.
        var measured = pairs.Select(pair =>
        {
            var (d, s, _) = pair;
            if (d.Type != "TEXT") return new Measured(d, s, d.Bounds, s.Bounds, null, null);

            var designInk = MeasureInk(designPixels, designPng.Width, designPng.Height, TextSearchArea(d).Scale(designPxPerUnit));
            var appInk = MeasureInk(pixels, screenshot.Width, screenshot.Height, s.Bounds.Scale(1 / pxToDp));
            return designInk is not null && appInk is not null
                ? new Measured(d, s, designInk.Bounds.Scale(1 / designPxPerUnit), appInk.Bounds.Scale(pxToDp), designInk, appInk)
                : new Measured(d, s, d.Bounds, s.Bounds, designInk, appInk);
        }).ToList();

        // Desplazamiento global (barra de estado, notch) con las mismas cajas que se comparan: la de un
        // TextView que ocupa toda la fila no dice dónde está el texto.
        var (dx, dy) = measured.Count > 0
            ? (ElementMatcher.Median(measured.Select(m => m.Actual.X - m.DesignBox.X * scale)),
               ElementMatcher.Median(measured.Select(m => m.Actual.Y - m.DesignBox.Y * scale)))
            : (match.Dx, match.Dy);

        // Con anchos distintos, la mediana de dx mezcla elementos anclados a la izquierda con centrados
        // y no significa nada; el ajuste horizontal lo hace FitHorizontally por elemento.
        if (Math.Abs(extraWidth) > tolerance) dx = 0;

        var expectedBounds = new Dictionary<DesignElement, Rect>(ReferenceEqualityComparer.Instance);
        var actualBounds = new Dictionary<DesignElement, Rect>(ReferenceEqualityComparer.Instance);

        var diffs = new List<ElementDiff>();
        var badElements = new HashSet<DesignElement>(ReferenceEqualityComparer.Instance);

        foreach (var (d, s, designBox, actual, designInk, appInk) in measured)
        {
            if (designInk is not null && appInk is not null) actualBounds[d] = actual;

            var expected = FitHorizontally(designBox.Scale(scale).Offset(dx, dy), extraWidth, actual);
            expectedBounds[d] = expected;
            var label = Label(d);

            void Check(string prop, double exp, double act, double allowed)
            {
                if (Math.Abs(exp - act) > allowed)
                {
                    diffs.Add(new ElementDiff(label, prop, F(exp), F(act)));
                    badElements.Add(d);
                }
            }

            // El ancho de un mismo texto varía un poco entre Figma y Android (versión de la fuente, redondeo
            // del tamaño a píxeles): en las letras se tolera hasta un 2.5 %. Una fuente distinta (Lato en vez
            // de Inter) cambia el ancho bastante más y se sigue reportando.
            var widthTolerance = designInk is not null && appInk is not null
                ? Math.Max(tolerance, expected.Width * TextWidthTolerance)
                : tolerance;

            Check("x", expected.X, actual.X, tolerance);
            Check("y", expected.Y, actual.Y, tolerance);
            Check("width", expected.Width, actual.Width, widthTolerance);
            Check("height", expected.Height, actual.Height, tolerance);

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

            // El color de la letra se mide igual en el PNG del diseño y en la captura, no se toma del fill:
            // así cuenta la opacidad de la capa o de sus padres (un botón deshabilitado al 30 %).
            if (designInk is not null && appInk is not null && appInk.Color.DistanceTo(designInk.Color) > colorTolerance)
            {
                diffs.Add(new ElementDiff(label, "text color", designInk.Color.Hex, appInk.Color.Hex));
                badElements.Add(d);
            }
        }

        var runDir = Path.Combine(settings.OutputDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);

        // El PNG de Figma, al tamaño en dp del diseño, centrado y desplazado para superponerse a la captura.
        var destWidth = (float)(design.Bounds.Width * scale / pxToDp);
        var destLeft = (float)((dx + extraWidth / 2) / pxToDp);
        var dyPx = (float)(dy / pxToDp);
        using var alignedDesign = new SKBitmap(screenshot.Width, screenshot.Height);
        using (var canvas = new SKCanvas(alignedDesign))
        {
            canvas.Clear(SKColors.White);
            var destHeight = designPng.Height * destWidth / designPng.Width;
            using var image = SKImage.FromBitmap(designPng);
            canvas.DrawImage(image, SKRect.Create(destLeft, dyPx, destWidth, destHeight), new SKSamplingOptions(SKFilterMode.Linear));
        }

        var mismatchRatio = WriteDiffImage(screenshot, alignedDesign, pairs, badElements, expectedBounds, actualBounds, pxToDp,
            Path.Combine(runDir, "diff.png"));
        WriteSideBySide(alignedDesign, screenshot, Path.Combine(runDir, "side_by_side.png"));

        var report = BuildReport(design, capture, scale, extraWidth, dx, dy, pairs, missing, diffs, mismatchRatio, runDir);
        File.WriteAllText(Path.Combine(runDir, "report.md"), report);

        return new ComparisonResult(report,
            Path.Combine(runDir, "side_by_side.png"),
            Path.Combine(runDir, "diff.png"),
            pairs.Count, diffs);
    }

    /// <summary>
    /// Un frame de Figma está en las mismas unidades que los dp, así que se compara 1:1 aunque el
    /// dispositivo sea algo más ancho o angosto (430 vs 411): los márgenes y alturas de un layout MAUI
    /// no se encogen. Solo si la diferencia es grande (un diseño exportado a @2x, o de tablet)
    /// se escala el diseño al ancho del dispositivo.
    /// </summary>
    public static double ChooseScale(double designWidth, double deviceWidth)
    {
        var ratio = deviceWidth / designWidth;
        return ratio is >= 0.75 and <= 1.33 ? 1 : ratio;
    }

    /// <summary>
    /// Posición horizontal esperada cuando el dispositivo tiene <paramref name="extraWidth"/> dp más que el
    /// frame. Un elemento puede estar anclado a la izquierda, a la derecha, centrado o estirado de borde a
    /// borde; como el diseño no lo dice de forma fiable, se usa la interpretación que mejor explica la app.
    /// </summary>
    public static Rect FitHorizontally(Rect design, double extraWidth, Rect actual)
    {
        Rect[] options =
        [
            design,
            design.Offset(extraWidth, 0),
            design.Offset(extraWidth / 2, 0),
            design with { Width = design.Width + extraWidth },
        ];
        return options.MinBy(o => Math.Abs(o.X - actual.X) + Math.Abs(o.Width - actual.Width));
    }

    static double WriteDiffImage(
        SKBitmap screenshot, SKBitmap design,
        IReadOnlyList<ElementPair> pairs, HashSet<DesignElement> bad,
        Dictionary<DesignElement, Rect> expected, Dictionary<DesignElement, Rect> actualOverrides, double pxToDp, string path)
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
                var actual = actualOverrides.GetValueOrDefault(d, s.Bounds).Scale(1 / pxToDp);
                canvas.DrawRect(ToSk(actual), bad.Contains(d) ? ko : ok);

                if (bad.Contains(d))
                    canvas.DrawRect(ToSk(expected[d].Scale(1 / pxToDp)), dashed);
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

    /// <summary>
    /// Zona del PNG del diseño donde buscar las letras de un texto. La caja de una capa de texto puede ser
    /// más baja que la letra (un "Sign up" de 20 con interlineado de 15), así que se amplía en vertical a
    /// 1.4 veces el tamaño de fuente; en horizontal la caja ya cubre el ancho de las letras.
    /// </summary>
    static Rect TextSearchArea(DesignElement d)
    {
        const double HorizontalMargin = 1;
        var height = Math.Max(d.Bounds.Height, (d.FontSize ?? 0) * 1.4);
        return new Rect(
            d.Bounds.X - HorizontalMargin,
            d.Bounds.Y + d.Bounds.Height / 2 - height / 2,
            d.Bounds.Width + 2 * HorizontalMargin,
            height);
    }

    sealed record InkSample(Rect Bounds, Rgb Color);

    /// <summary>Pareja diseño/app con las cajas que se comparan (las letras, en los textos medibles).</summary>
    sealed record Measured(DesignElement Design, ScreenElement Screen, Rect DesignBox, Rect Actual, InkSample? DesignInk, InkSample? AppInk);

    /// <summary>
    /// Letras dentro de un texto: el fondo es el color dominante y la letra, los píxeles que se alejan de él.
    /// Devuelve la caja de esos píxeles (en px de la imagen) y el color de la letra, tomado de los más alejados
    /// (los del borde de cada glifo son mezclas con el fondo por el antialiasing).
    /// Null si casi no hay píxeles distintos del fondo (texto vacío o de muy poco contraste).
    /// </summary>
    static InkSample? MeasureInk(SKColor[] pixels, int width, int height, Rect px)
    {
        const double MinContrast = 32;
        const int MinInkPixels = 4;

        if (SampleDominantColor(pixels, width, height, px, inset: 0) is not { } background) return null;

        int x1 = Math.Clamp((int)px.X, 0, width - 1), x2 = Math.Clamp((int)(px.X + px.Width), 0, width - 1);
        int y1 = Math.Clamp((int)px.Y, 0, height - 1), y2 = Math.Clamp((int)(px.Y + px.Height), 0, height - 1);

        var ink = new List<(int X, int Y, double Distance, SKColor Color)>();
        for (var y = y1; y <= y2; y++)
        for (var x = x1; x <= x2; x++)
        {
            var c = pixels[y * width + x];
            var distance = new Rgb(c.Red, c.Green, c.Blue).DistanceTo(background);
            if (distance > MinContrast) ink.Add((x, y, distance, c));
        }

        ink = DropSolidEdges(ink, x1, y1, x2, y2);
        if (ink.Count < MinInkPixels) return null;

        ink = KeepTextColumns(ink, x1, x2);

        // El 20 % más alejado del fondo: el centro de los trazos, donde el color es puro.
        var core = ink.OrderByDescending(p => p.Distance).Take(Math.Max(MinInkPixels, ink.Count / 5)).ToList();
        var color = new Rgb(
            (byte)core.Average(p => p.Color.Red),
            (byte)core.Average(p => p.Color.Green),
            (byte)core.Average(p => p.Color.Blue));

        int minX = ink.Min(p => p.X), maxX = ink.Max(p => p.X), minY = ink.Min(p => p.Y), maxY = ink.Max(p => p.Y);
        return new InkSample(new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1), color);
    }

    /// <summary>
    /// Quita las filas y columnas del borde de la región que son casi enteras de "tinta": son una línea
    /// sólida, no texto. Pasa cuando, al redondear los límites de una vista, entra una fila del fondo de
    /// la página (y deja unidas todas las columnas en un solo bloque).
    /// </summary>
    static List<(int X, int Y, double Distance, SKColor Color)> DropSolidEdges(
        List<(int X, int Y, double Distance, SKColor Color)> ink, int x1, int y1, int x2, int y2)
    {
        const double SolidShare = 0.9;
        var rows = ink.GroupBy(p => p.Y).ToDictionary(g => g.Key, g => g.Count());
        var cols = ink.GroupBy(p => p.X).ToDictionary(g => g.Key, g => g.Count());
        bool SolidRow(int y) => rows.GetValueOrDefault(y) >= SolidShare * (x2 - x1 + 1);
        bool SolidCol(int x) => cols.GetValueOrDefault(x) >= SolidShare * (y2 - y1 + 1);

        int top = y1, bottom = y2, left = x1, right = x2;
        while (top < bottom && SolidRow(top)) top++;
        while (bottom > top && SolidRow(bottom)) bottom--;
        while (left < right && SolidCol(left)) left++;
        while (right > left && SolidCol(right)) right--;

        return ink.Where(p => p.Y >= top && p.Y <= bottom && p.X >= left && p.X <= right).ToList();
    }

    /// <summary>
    /// Separa la tinta en bloques de columnas (un espacio entre palabras no los corta; uno mayor que
    /// 0.6 veces el alto de la tinta, sí) y se queda con el texto. Lo que toca el borde de la vista suele
    /// ser de otra cosa: las esquinas redondeadas de un botón dejan ver el fondo de la página, y las líneas
    /// a los lados de un "or" llegan hasta su borde. Si todo toca el borde, se queda con el bloque mayor.
    /// </summary>
    static List<(int X, int Y, double Distance, SKColor Color)> KeepTextColumns(
        List<(int X, int Y, double Distance, SKColor Color)> ink, int left, int right)
    {
        var maxGap = Math.Max(2, 0.6 * (ink.Max(p => p.Y) - ink.Min(p => p.Y) + 1));
        var columns = ink.GroupBy(p => p.X).OrderBy(g => g.Key).ToList();

        var blocks = new List<List<IGrouping<int, (int X, int Y, double Distance, SKColor Color)>>> { new() { columns[0] } };
        foreach (var column in columns.Skip(1))
        {
            if (column.Key - blocks[^1][^1].Key > maxGap) blocks.Add([]);
            blocks[^1].Add(column);
        }
        if (blocks.Count == 1) return ink;

        bool TouchesEdge(List<IGrouping<int, (int X, int Y, double Distance, SKColor Color)>> b) =>
            b[0].Key <= left || b[^1].Key >= right;
        var candidates = blocks.Where(b => !TouchesEdge(b)).ToList();
        var best = (candidates.Count > 0 ? candidates : blocks).MaxBy(b => b.Sum(c => c.Count()))!;
        return best.SelectMany(c => c).ToList();
    }

    /// <summary>Color más frecuente dentro del elemento (ignorando bordes), cuantizado para absorber antialiasing.</summary>
    static Rgb? SampleDominantColor(SKColor[] pixels, int width, int height, Rect px, double inset = 0.2)
    {
        var insetX = px.Width * inset;
        var insetY = px.Height * inset;
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
        DesignFrame design, ScreenCapture capture, double scale, double extraWidth, double dx, double dy, IReadOnlyList<ElementPair> pairs,
        IReadOnlyList<DesignElement> missing, List<ElementDiff> diffs, double mismatchRatio, string runDir)
    {
        var byMethod = pairs.GroupBy(p => p.Method).ToDictionary(g => g.Key, g => g.Count());
        int Count(MatchMethod m) => byMethod.GetValueOrDefault(m);

        var sb = new StringBuilder();
        sb.AppendLine($"# Comparación: {design.Name}");
        sb.AppendLine();
        sb.AppendLine($"- Dispositivo: {capture.Serial} ({capture.WidthDp:0.#} dp de ancho, densidad {capture.Density})");
        sb.AppendLine(scale == 1
            ? $"- Frame de Figma: {design.Bounds.Width:0.#}×{design.Bounds.Height:0.#}, comparado 1:1 en dp"
            : $"- Frame de Figma: {design.Bounds.Width:0.#}×{design.Bounds.Height:0.#}, escalado ×{scale:0.###} al ancho del dispositivo");
        if (Math.Abs(extraWidth) >= 0.5)
            sb.AppendLine($"- El dispositivo es {Math.Abs(extraWidth):0.#} dp más {(extraWidth > 0 ? "ancho" : "angosto")} que el frame: " +
                          "cada elemento se compara anclado a la izquierda, a la derecha, centrado o estirado, según cuál se ajuste mejor");
        if (design.CachedAt is { } cachedAt)
            sb.AppendLine($"- Diseño de la caché local (descargado el {cachedAt:yyyy-MM-dd HH:mm}); usa refresh=true si cambió en Figma");
        sb.AppendLine($"- Desplazamiento global estimado: dx={dx:0.#} dp, dy={dy:0.#} dp");
        sb.AppendLine("- Los textos se comparan por sus letras visibles, medidas en el render del diseño y en la captura " +
                      "(no por las cajas: en la app suelen estar dentro de vistas más anchas). En el ancho se tolera un 2.5 %.");
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
