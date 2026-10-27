using System.Globalization;
using System.Text;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Mcp.Comparison;

/// <param name="Fix">Qué cambiar en el código, con las propiedades del framework de la app.</param>
public sealed record ElementDiff(string Name, string Property, string Expected, string Actual, string? Fix = null);

public enum ElementStatus { Ok, Differs, Missing }

/// <summary>Una capa del diseño en la comparación, con sus cajas en unidades lógicas.</summary>
/// <param name="Expected">Dónde debería estar según Figma (ya ajustada al dispositivo).</param>
/// <param name="Actual">Dónde está en la app; null si falta.</param>
/// <param name="Fix">Solo en un texto que falta: cómo agregarlo.</param>
public sealed record ElementResult(
    string Name, string? AutomationId, ElementStatus Status, Rect Expected, Rect? Actual,
    IReadOnlyList<ElementDiff> Diffs, string? Fix = null);

public sealed record ComparisonResult(
    string Report,
    string SideBySidePath,
    string DiffPath,
    int Matched,
    IReadOnlyList<ElementDiff> Diffs,
    string HtmlReportPath,
    FidelityScore Fidelity,
    IReadOnlyList<ElementResult> Elements,
    double MismatchRatio,
    bool LooksLikeLoadingScreen,
    IReadOnlyList<string> Warnings);

public sealed partial class ScreenComparer(Settings settings)
{
    const double TextWidthTolerance = 0.025;

    /// <summary>
    /// Con más de la mitad de los píxeles distintos, la captura no es la pantalla del diseño: una pantalla de
    /// carga (el splash de .NET tapa la app aunque uiautomator ya liste sus elementos), un diálogo u otra
    /// pantalla. Una app mal maquetada da 5–30 %; una que coincide, 1–3 %.
    /// </summary>
    const double LoadingScreenMismatch = 0.5;

    public ComparisonResult Compare(
        DesignFrame design,
        string designPngPath,
        ScreenCapture capture,
        double tolerance,
        double colorTolerance,
        UiFramework framework = UiFramework.Generic,
        IReadOnlyList<string>? warnings = null)
    {
        using var screenshot = SKBitmap.Decode(capture.ScreenshotPath)
            ?? throw new InvalidOperationException("Could not read the device screenshot.");
        using var designPng = SKBitmap.Decode(designPngPath)
            ?? throw new InvalidOperationException("Could not read the Figma PNG.");

        var pxToDp = capture.PxToUnit;
        var deviceWidthDp = screenshot.Width * pxToDp;
        var scale = ChooseScale(design.Bounds.Width, deviceWidthDp);

        // Ancho que le sobra (o falta) al dispositivo respecto al frame. Con 1:1 en dp, un margen de 35
        // sigue siendo 35: lo que cambia es el ancho de lo que se estira o la x de lo centrado.
        var extraWidth = deviceWidthDp - design.Bounds.Width * scale;

        var match = ElementMatcher.Match(design.Elements, capture.Elements, scale);
        var (pairs, missing) = (match.Pairs, match.Missing);

        // Una forma con AutomationId se compara como forma (radio, borde, contenido); del emparejamiento solo
        // queda el AutomationId para el reporte.
        var shapeIds = (design.Shapes ?? []).Select(s => s.NodeId).OfType<string>().ToHashSet();
        var shapeAutomationIds = pairs.Where(p => p.Design.NodeId is { } id && shapeIds.Contains(id))
            .ToDictionary(p => p.Design.NodeId!, p => p.Screen.Id);
        pairs = pairs.Where(p => p.Design.NodeId is not { } id || !shapeIds.Contains(id)).ToList();

        var pixels = screenshot.Pixels;
        var designPixels = designPng.Pixels;
        var designPxPerUnit = RenderScale(designPng.Width, design.Bounds.Width);

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

        var diffs = new List<ElementDiff>();
        var badElements = new HashSet<DesignElement>(ReferenceEqualityComparer.Instance);
        var elements = new List<ElementResult>();

        void AddDiff(DesignElement d, string label, List<ElementDiff> elementDiffs, string prop, string exp, string act)
        {
            var diff = new ElementDiff(label, prop, exp, act);
            diff = diff with { Fix = FixSuggester.Suggest(framework, capture.Unit, d, diff) };
            diffs.Add(diff);
            elementDiffs.Add(diff);
            badElements.Add(d);
        }

        foreach (var (d, s, designBox, actual, designInk, appInk) in measured)
        {
            var expected = FitHorizontally(designBox.Scale(scale).Offset(dx, dy), extraWidth, actual);
            expectedBounds[d] = expected;
            var label = Label(d);
            var elementDiffs = new List<ElementDiff>();

            void Add(string prop, string exp, string act) => AddDiff(d, label, elementDiffs, prop, exp, act);

            void Check(string prop, double exp, double act, double allowed)
            {
                if (Math.Abs(exp - act) > allowed) Add(prop, F(exp), F(act));
            }

            // El ancho de un mismo texto varía un poco entre Figma y Android (versión de la fuente, redondeo
            // del tamaño a píxeles): en las letras se tolera hasta un 2.5 %. Una fuente distinta (Lato en vez
            // de Inter) cambia el ancho bastante más y se sigue reportando.
            var widthTolerance = designInk is not null && appInk is not null
                ? Math.Max(tolerance, expected.Width * TextWidthTolerance)
                : tolerance;

            // Si el ancho cambia dentro de lo tolerado, el borde izquierdo se corre hasta esa diferencia según
            // la alineación (nada a la izquierda, la mitad centrado, todo a la derecha).
            var xTolerance = designInk is not null && appInk is not null
                ? tolerance + Math.Min(Math.Abs(expected.Width - actual.Width), widthTolerance)
                : tolerance;

            Check("x", expected.X, actual.X, xTolerance);
            Check("y", expected.Y, actual.Y, tolerance);
            Check("width", expected.Width, actual.Width, widthTolerance);
            Check("height", expected.Height, actual.Height, tolerance);

            var screenText = ElementMatcher.ScreenText(s);
            if (d.Text is not null && screenText is not null && !string.Equals(d.Text.Trim(), screenText.Trim(), StringComparison.Ordinal))
                Add("text", d.Text, screenText);

            // El color de relleno solo se compara en formas; en un TEXT el fill es el color de la letra
            // y el muestreo del fondo daría falsos positivos.
            if (d.Fill is { } fill && d.Type != "TEXT")
            {
                var sampled = SampleDominantColor(pixels, screenshot.Width, screenshot.Height, actual.Scale(1 / pxToDp));
                if (sampled is { } c && c.DistanceTo(fill) > colorTolerance)
                    Add("background", fill.Hex, c.Hex);
            }

            // El color de la letra se mide igual en el PNG del diseño y en la captura, no se toma del fill:
            // así cuenta la opacidad de la capa o de sus padres (un botón deshabilitado al 30 %).
            if (designInk is not null && appInk is not null && appInk.Color.DistanceTo(designInk.Color) > colorTolerance)
                Add("text color", designInk.Color.Hex, appInk.Color.Hex);

            elements.Add(new ElementResult(label, s.Id, elementDiffs.Count == 0 ? ElementStatus.Ok : ElementStatus.Differs,
                expected, actual, elementDiffs));
        }

        // Un texto del diseño que no aparece en la app casi siempre es algo que falta implementar;
        // los contenedores (Auto Layout, instancias) rara vez tienen equivalente y solo harían ruido.
        // La hora de la barra de estado que los diseños dibujan como maqueta ("12:30" arriba) no es de la app.
        var statusBarClock = missing.Where(IsStatusBarClock).ToList();
        var missingText = missing.Where(m => m.Text is not null).Except(statusBarClock).ToList();
        foreach (var m in missingText)
        {
            // Sin la app no se sabe cómo se ancla: se ubica como el PNG del diseño, centrado.
            var expected = m.Bounds.Scale(scale).Offset(dx + extraWidth / 2, dy);
            elements.Add(new ElementResult(Label(m), null, ElementStatus.Missing, expected, null, [],
                FixSuggester.AddText(framework, m)));
        }

        // Lo que se redibuja en el diseño alineado: dónde está en el diseño, dónde va y el color con que se borra
        // (null: el dominante del recorte, que en un texto es el fondo detrás de las letras).
        var placed = measured.Select(m => (m.Design, m.DesignBox, m.Actual, Erase: (Rgb?)null)).ToList();

        var shapeContext = new ShapeContext(
            new PixelImage(designPixels, designPng.Width, designPng.Height), designPng, designPxPerUnit,
            new PixelImage(pixels, screenshot.Width, screenshot.Height), screenshot, pxToDp,
            scale, dx, dy, extraWidth, tolerance, colorTolerance);

        if (ScreenBackground(design, shapeContext) is var (designBackground, appBackground))
        {
            var page = new DesignElement("Screen background", "FRAME", design.Bounds, design.Background, null, null, null, null, null);
            var screenBox = new Rect(0, 0, screenshot.Width * pxToDp, screenshot.Height * pxToDp);
            var elementDiffs = new List<ElementDiff>();
            if (appBackground.DistanceTo(designBackground) > colorTolerance)
                AddDiff(page, page.Name, elementDiffs, "background", designBackground.Hex, appBackground.Hex);
            elements.Add(new ElementResult(page.Name, null, elementDiffs.Count == 0 ? ElementStatus.Ok : ElementStatus.Differs,
                screenBox, screenBox, elementDiffs));
        }

        var shapes = CompareShapes(design, shapeContext);
        foreach (var check in shapes.Checks)
        {
            var d = check.Design;
            var automationId = d.NodeId is { } nodeId ? shapeAutomationIds.GetValueOrDefault(nodeId) : null;
            if (check.Actual is not { } actual)
            {
                elements.Add(new ElementResult(check.Label, automationId, ElementStatus.Missing, check.Expected, null, [],
                    FixSuggester.AddShape(framework, capture.Unit, d, check.Expected)));
                continue;
            }

            expectedBounds[d] = check.Expected;
            var elementDiffs = new List<ElementDiff>();
            foreach (var (prop, exp, act) in check.Diffs) AddDiff(d, check.Label, elementDiffs, prop, exp, act);
            elements.Add(new ElementResult(check.Label, automationId, elementDiffs.Count == 0 ? ElementStatus.Ok : ElementStatus.Differs,
                check.Expected, actual, elementDiffs));
            placed.Add((d, check.DesignBox, actual, check.Background));
        }

        var runDir = Path.Combine(settings.OutputDirectory, RunHistory.NewRunDirName(DateTime.Now));
        Directory.CreateDirectory(runDir);

        var previous = RunHistory.FindPrevious(settings.OutputDirectory, runDir, design.FileKey, design.NodeId, capture.Serial);
        var fidelity = new FidelityScore(
            elements.Count(e => e.Status == ElementStatus.Ok), elements.Count, previous?.Percent);

        // El PNG de Figma, al tamaño en dp del diseño, centrado y desplazado para superponerse a la captura.
        var destWidth = (float)(designPng.Width / designPxPerUnit * scale / pxToDp);
        var destLeft = (float)((dx + extraWidth / 2) / pxToDp);
        var dyPx = (float)(dy / pxToDp);
        using var alignedDesign = new SKBitmap(screenshot.Width, screenshot.Height);
        using (var canvas = new SKCanvas(alignedDesign))
        {
            canvas.Clear(SKColors.White);
            var destHeight = designPng.Height * destWidth / designPng.Width;
            using var image = SKImage.FromBitmap(designPng);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear);
            canvas.DrawImage(image, SKRect.Create(destLeft, dyPx, destWidth, destHeight), sampling);

            // Centrado, lo que en la app está anclado a la izquierda (o a la derecha) queda corrido la mitad de
            // la diferencia de ancho y aparece duplicado. Cada elemento emparejado se borra de ahí (con el fondo
            // de su recorte) y se vuelve a dibujar: si coincide, sobre su posición en la app, para que el magenta
            // muestre solo diferencias de forma o color; si no, donde debería estar (el punteado azul).
            // Primero los grandes, encima los textos.
            const double Margin = 2;
            var baseScale = destWidth / designPng.Width;
            var byArea = placed.OrderByDescending(m => m.DesignBox.Width * m.DesignBox.Height).ToList();
            var crops = byArea.Select(m => m.DesignBox.Inflate(Margin).Scale(designPxPerUnit)).ToList();

            for (var i = 0; i < byArea.Count; i++)
            {
                var source = crops[i];
                if ((byArea[i].Erase ?? SampleDominantColor(designPixels, designPng.Width, designPng.Height, source, inset: 0)) is not { } background) continue;
                using var fill = new SKPaint { Color = new SKColor(background.R, background.G, background.B) };
                canvas.DrawRect(SKRect.Create(destLeft + (float)(source.X * baseScale), dyPx + (float)(source.Y * baseScale),
                    (float)(source.Width * baseScale), (float)(source.Height * baseScale)), fill);
            }

            for (var i = 0; i < byArea.Count; i++)
            {
                var m = byArea[i];
                var at = badElements.Contains(m.Design) ? expectedBounds[m.Design] : m.Actual;
                var target = at.Inflate(Margin * scale).Scale(1 / pxToDp);
                canvas.DrawImage(image, ToSk(crops[i]), ToSk(target), sampling);
            }
        }

        var diffPath = Path.Combine(runDir, "diff.png");
        var mismatchRatio = WriteDiffImage(screenshot, alignedDesign, placed.Select(p => (p.Design, p.Actual)).ToList(),
            badElements, expectedBounds, pxToDp, diffPath);
        WriteSideBySide(alignedDesign, screenshot, Path.Combine(runDir, "side_by_side.png"));

        var looksLikeLoadingScreen = mismatchRatio > LoadingScreenMismatch || (pairs.Count == 0 && missingText.Count > 0);
        var allWarnings = new List<string>();
        if (looksLikeLoadingScreen)
            allWarnings.Add($"The screenshot doesn't look like the design ({mismatchRatio:P0} of pixels differ" +
                            (pairs.Count == 0 ? ", no element matched" : "") +
                            "): it may be a loading screen, a dialog, the keyboard or another screen. " +
                            "Look at side_by_side.png before fixing anything.");
        if (warnings is not null) allWarnings.AddRange(warnings);

        var htmlPath = Path.Combine(runDir, "report.html");
        var report = BuildReport(design, capture, scale, extraWidth, dx, dy, pairs, missingText, statusBarClock, shapes,
            diffs, elements, fidelity, mismatchRatio, allWarnings, runDir, htmlPath);
        File.WriteAllText(Path.Combine(runDir, "report.md"), report);

        using (var designData = alignedDesign.Encode(SKEncodedImageFormat.Png, 100))
            HtmlReport.Write(htmlPath, new HtmlReport.Model(
                design.Name, $"{capture.Serial} · {capture.WidthUnits:0.#} {capture.Unit} wide", capture.App?.ToString(), DateTime.Now,
                capture.PixelsPerUnit, screenshot.Width, screenshot.Height, fidelity, mismatchRatio, allWarnings, elements,
                designData.ToArray(), File.ReadAllBytes(capture.ScreenshotPath), File.ReadAllBytes(diffPath)));

        RunHistory.Save(runDir, new RunHistory.RunSummary(design.FileKey, design.NodeId, capture.Serial,
            fidelity.Percent, fidelity.Correct, fidelity.Total, diffs.Count));

        return new ComparisonResult(report,
            Path.Combine(runDir, "side_by_side.png"),
            diffPath,
            pairs.Count, diffs, htmlPath, fidelity, elements, mismatchRatio, looksLikeLoadingScreen, allWarnings);
    }

    /// <summary>Borra la carpeta de una comparación que no sirve (se va a repetir), para que no cuente como la anterior.</summary>
    public static void Discard(ComparisonResult result)
    {
        if (Path.GetDirectoryName(result.HtmlReportPath) is { } runDir && Directory.Exists(runDir))
            Directory.Delete(runDir, recursive: true);
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
    /// Píxeles del PNG del diseño por unidad. Es la escala con que se exportó (2x), redondeada a medios: si una
    /// capa se sale del frame sin recorte (una barra de estado de 430 puesta en x=1), Figma agranda el render
    /// y dividir los anchos daría 2.005, que corre medio píxel por cada 100 unidades.
    /// </summary>
    public static double RenderScale(int pngWidth, double frameWidth) =>
        Math.Max(0.5, Math.Round(pngWidth / frameWidth * 2) / 2);

    /// <summary>
    /// Posición horizontal esperada cuando el dispositivo tiene <paramref name="extraWidth"/> dp más que el
    /// frame. Un elemento puede estar anclado a la izquierda, a la derecha, centrado o estirado de borde a
    /// borde; como el diseño no lo dice de forma fiable, se usa la interpretación que mejor explica la app.
    /// </summary>
    public static Rect FitHorizontally(Rect design, double extraWidth, Rect actual) =>
        Closest(HorizontalOptions(design, extraWidth), actual);

    static Rect Closest(IEnumerable<Rect> options, Rect actual) =>
        options.MinBy(o => Math.Abs(o.X - actual.X) + Math.Abs(o.Width - actual.Width));

    /// <summary>
    /// Dónde puede estar un elemento según cómo se ancle. Uno que crece en una fila se estira su parte del
    /// sobrante y se corre lo que crecieron los de su izquierda (en partes iguales, la i-ésima parte).
    /// </summary>
    /// <param name="widthShare">Si crece en una fila de Auto Layout, la parte del sobrante que le toca (ver <see cref="DesignElement.WidthShare"/>).</param>
    public static IReadOnlyList<Rect> HorizontalOptions(Rect design, double extraWidth, double? widthShare = null)
    {
        List<Rect> options =
        [
            design,
            design.Offset(extraWidth, 0),
            design.Offset(extraWidth / 2, 0),
            design with { Width = design.Width + extraWidth },
        ];
        if (widthShare is > 0 and < 1 and { } share)
        {
            var part = extraWidth * share;
            for (var i = 0; i < Math.Round(1 / share); i++)
                options.Add(new Rect(design.X + i * part, design.Y, design.Width + part, design.Height));
        }
        return options;
    }

    static double WriteDiffImage(
        SKBitmap screenshot, SKBitmap design,
        IReadOnlyList<(DesignElement Design, Rect Actual)> boxes, HashSet<DesignElement> bad,
        Dictionary<DesignElement, Rect> expected, double pxToDp, string path)
    {
        const int threshold = 40;
        var a = screenshot.Pixels;
        var b = design.Pixels;
        var mismatched = 0;

        using var output = new SKBitmap(screenshot.Width, screenshot.Height);
        var result = new SKColor[a.Length];
        var width = screenshot.Width;
        int Distance(SKColor p, SKColor q) => Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);

        // Un píxel solo es distinto si ninguno de sus vecinos en el diseño se le parece: un corrimiento de un
        // píxel (antialiasing, redondeo entre densidades) no es una diferencia y pintaba el contorno de todo.
        bool Differs(int i)
        {
            if (Distance(a[i], b[i]) <= threshold) return false;
            int x = i % width, y = i / width;
            for (var ny = Math.Max(0, y - 1); ny <= Math.Min(screenshot.Height - 1, y + 1); ny++)
            for (var nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                if (Distance(a[i], b[ny * width + nx]) <= threshold) return false;
            return true;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (Differs(i))
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
            foreach (var (d, box) in boxes)
            {
                var actual = box.Scale(1 / pxToDp);
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

    /// <summary>
    /// Píxeles desde el borde de la región que todavía cuentan como "tocar el borde": al pasar los límites de
    /// una vista de dp a píxeles, la región puede empezar una fila o columna antes que la vista.
    /// </summary>
    const int EdgeMargin = 2;

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

        ink = KeepTextRows(ink, y1, y2);
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
    /// la página (y deja unidas todas las columnas en un solo bloque), o cuando la vista de un campo de
    /// texto llega hasta el borde del campo.
    /// </summary>
    static List<(int X, int Y, double Distance, SKColor Color)> DropSolidEdges(
        List<(int X, int Y, double Distance, SKColor Color)> ink, int x1, int y1, int x2, int y2)
    {
        const double SolidShare = 0.9;
        var rows = ink.GroupBy(p => p.Y).ToDictionary(g => g.Key, g => g.Count());
        var cols = ink.GroupBy(p => p.X).ToDictionary(g => g.Key, g => g.Count());
        bool SolidRow(int y) => rows.GetValueOrDefault(y) >= SolidShare * (x2 - x1 + 1);
        bool SolidCol(int x) => cols.GetValueOrDefault(x) >= SolidShare * (y2 - y1 + 1);

        // Las filas vacías del borde se saltan: el antialiasing de una línea deja una fila tenue (que no llega
        // a ser tinta) entre la línea y el borde de la región, y no debe frenar el recorte.
        bool EmptyRow(int y) => !rows.ContainsKey(y);
        bool EmptyCol(int x) => !cols.ContainsKey(x);

        int top = y1, bottom = y2, left = x1, right = x2;
        while (top < bottom && (EmptyRow(top) || SolidRow(top))) top++;
        while (bottom > top && (EmptyRow(bottom) || SolidRow(bottom))) bottom--;
        while (left < right && (EmptyCol(left) || SolidCol(left))) left++;
        while (right > left && (EmptyCol(right) || SolidCol(right))) right--;

        return ink.Where(p => p.Y >= top && p.Y <= bottom && p.X >= left && p.X <= right).ToList();
    }

    /// <summary>
    /// Separa la tinta en franjas de filas (divididas por filas vacías) y quita las que tocan el borde de
    /// arriba o de abajo, salvo la principal: son partes de otra vista que se monta sobre esta,
    /// como la cola de la "y" de una etiqueta que se pega 2 dp encima de un campo. Un texto de varias líneas
    /// se conserva entero: sus líneas no tocan el borde.
    /// </summary>
    static List<(int X, int Y, double Distance, SKColor Color)> KeepTextRows(
        List<(int X, int Y, double Distance, SKColor Color)> ink, int top, int bottom)
    {
        var rows = ink.Select(p => p.Y).Distinct().Order().ToList();
        var bands = new List<(int First, int Last)> { (rows[0], rows[0]) };
        foreach (var y in rows.Skip(1))
        {
            if (y > bands[^1].Last + 1) bands.Add((y, y));
            else bands[^1] = (bands[^1].First, y);
        }

        if (bands.Count == 1) return ink;

        // La franja con más tinta es el texto aunque toque el borde (una "@" puede llegar a la última fila
        // de un campo ajustado); de las demás se quitan las que tocan el borde.
        int InkIn((int First, int Last) b) => ink.Count(p => p.Y >= b.First && p.Y <= b.Last);
        var main = bands.MaxBy(InkIn);
        var keep = bands.Where(b => b == main || (b.First > top + EdgeMargin && b.Last < bottom - EdgeMargin)).ToList();
        return ink.Where(p => keep.Any(b => p.Y >= b.First && p.Y <= b.Last)).ToList();
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
            b[0].Key <= left + EdgeMargin || b[^1].Key >= right - EdgeMargin;
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
        List<DesignElement> missingText, List<DesignElement> statusBarClock, ShapeComparison shapes,
        List<ElementDiff> diffs, List<ElementResult> elements, FidelityScore fidelity, double mismatchRatio, List<string> warnings,
        string runDir, string htmlPath)
    {
        var byMethod = pairs.GroupBy(p => p.Method).ToDictionary(g => g.Key, g => g.Count());
        int Count(MatchMethod m) => byMethod.GetValueOrDefault(m);

        var sb = new StringBuilder();
        sb.AppendLine($"# Comparison: {design.Name}");
        sb.AppendLine();
        if (fidelity.Total > 0)
        {
            var change = fidelity.Change switch
            {
                null => "",
                0 => " · same as the previous comparison",
                > 0 and var c => $" · before {fidelity.Previous} % (+{c})",
                var c => $" · before {fidelity.Previous} % ({c})",
            };
            sb.AppendLine($"**Fidelity: {fidelity.Percent} %**{change} — {fidelity.Correct} of {fidelity.Total} elements as in Figma " +
                          "(texts and visible non-text layers with no differences, over all compared elements plus those missing from the app)");
            sb.AppendLine();
        }
        foreach (var warning in warnings)
        {
            sb.AppendLine($"> ⚠️ {warning}");
            sb.AppendLine();
        }
        sb.AppendLine($"Interactive report: {htmlPath}");
        sb.AppendLine();
        var u = capture.Unit;
        sb.AppendLine($"- Device: {capture.Serial} ({capture.WidthUnits:0.#} {u} wide, {capture.PixelsPerUnit:0.##} px per {u})");
        if (capture.App is { } app) sb.AppendLine($"- App: {app}");
        sb.AppendLine(scale == 1
            ? $"- Figma frame: {design.Bounds.Width:0.#}×{design.Bounds.Height:0.#}, compared 1:1 in {u}"
            : $"- Figma frame: {design.Bounds.Width:0.#}×{design.Bounds.Height:0.#}, scaled ×{scale:0.###} to the device width");
        if (Math.Abs(extraWidth) >= 0.5)
            sb.AppendLine($"- The device is {Math.Abs(extraWidth):0.#} {u} {(extraWidth > 0 ? "wider" : "narrower")} than the frame: " +
                          "each element is compared anchored left, right, centered or stretched, whichever fits best");
        if (design.CachedAt is { } cachedAt)
            sb.AppendLine($"- Design from the local cache (downloaded on {cachedAt:yyyy-MM-dd HH:mm}); use refresh=true if it changed in Figma");
        sb.AppendLine($"- Estimated global offset: dx={dx:0.#} {u}, dy={dy:0.#} {u}");
        sb.AppendLine("- Texts are compared by their visible glyphs, measured in the design render and in the screenshot " +
                      "(not by their boxes: in the app they usually sit inside wider views). Width has a 2.5 % tolerance.");
        sb.AppendLine($"- Matched elements: {pairs.Count} of {design.Elements.Count} layers " +
                      $"(by AutomationId {Count(MatchMethod.Name)}, by text {Count(MatchMethod.Text)}, by position {Count(MatchMethod.Position)})");
        if (shapes.Checks.Count > 0)
            sb.AppendLine($"- Non-text layers (shapes, icons, images): {shapes.Checks.Count}, found in the screenshot by how they look " +
                          "and measured the same way as in the design render (box, fill, radius, border and content)");
        if (shapes.DeviceChrome > 0)
            sb.AppendLine($"- Ignored {shapes.DeviceChrome} status bar and home indicator layers drawn in the design");
        sb.AppendLine($"- Different pixels: {mismatchRatio:P1}");
        sb.AppendLine($"- Images: {runDir}");
        sb.AppendLine();

        if (pairs.Count == 0)
        {
            sb.AppendLine("## No element matched");
            sb.AppendLine();
            sb.AppendLine("No text or AutomationId in the app matches the design. " +
                          "Check that the app is showing this frame's screen (look at side_by_side.png).");
        }
        else if (diffs.Count == 0)
        {
            sb.AppendLine("## No differences in the matched elements");
        }
        else
        {
            sb.AppendLine($"## Differences ({diffs.Count})");
            sb.AppendLine();
            sb.AppendLine("| Element | Property | Figma | App |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var d in diffs)
                sb.AppendLine($"| {d.Name} | {d.Property} | {d.Expected} | {d.Actual} |");

            sb.AppendLine();
            sb.AppendLine("## How to fix it");
            sb.AppendLine();
            foreach (var e in elements.Where(e => e.Status == ElementStatus.Differs))
            {
                var fixes = e.Diffs.Select(d => d.Fix).OfType<string>().Distinct().ToList();
                if (fixes.Count == 0) continue;
                sb.AppendLine(e.AutomationId is { } id ? $"- **{e.Name}** (AutomationId `{id}`)" : $"- **{e.Name}**");
                foreach (var fix in fixes) sb.AppendLine($"  - {fix}");
            }
        }

        if (statusBarClock.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Ignored the design's status bar clock ({string.Join(", ", statusBarClock.Select(m => m.Text))}).");
        }

        if (missingText.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Design texts missing from the app ({missingText.Count})");
            sb.AppendLine();
            // Los elementos que faltan están en el mismo orden que missingText.
            var missingElements = elements.Where(e => e.Status == ElementStatus.Missing);
            foreach (var (m, e) in missingText.Zip(missingElements).Take(40))
                sb.AppendLine($"- {Label(m)} ({m.Bounds.Scale(scale)}) — {e.Fix}");
            if (missingText.Count > 40) sb.AppendLine($"- … and {missingText.Count - 40} more");
        }

        var missingShapes = shapes.Checks.Where(k => k.Actual is null).ToList();
        if (missingShapes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Design non-text layers not found in the app ({missingShapes.Count})");
            sb.AppendLine();
            var fixes = elements.Where(e => e.Status == ElementStatus.Missing).ToLookup(e => e.Name, e => e.Fix);
            foreach (var k in missingShapes)
                sb.AppendLine($"- {k.Label} ({k.Expected}) — {fixes[k.Label].FirstOrDefault()}");
        }

        if (shapes.NotMeasurable > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{shapes.NotMeasurable} non-text layers can't be told apart from their background in the design and weren't compared. " +
                          "To compare one, use its layer name as the AutomationId in the app.");
        }

        return sb.ToString();
    }

    /// <summary>Texto con forma de hora ("9:41", "12:30") en la franja de la barra de estado (los primeros 48 dp).</summary>
    static bool IsStatusBarClock(DesignElement d) =>
        d.Text is { } t && d.Bounds.Y < 48 && StatusBarClockRegex().IsMatch(t.Trim());

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,2}:\d{2}$")]
    private static partial System.Text.RegularExpressions.Regex StatusBarClockRegex();

    /// <summary>Nombre para el reporte: las capas de texto suelen llamarse "Text", así que se muestra su contenido.</summary>
    static string Label(DesignElement d)
    {
        if (d.Text is null || d.Text.Trim() == d.Name.Trim()) return d.Name;
        var text = d.Text.ReplaceLineEndings(" ").Trim();
        if (text.Length > 40) text = text[..40] + "…";
        return $"{d.Name} \"{text}\"";
    }

    static SKRect ToSk(Rect r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
