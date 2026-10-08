using Driftcheck.Mcp;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Tests;

/// <summary>
/// Los textos se comparan por sus letras visibles. Las "letras" de estas pruebas son trazos verticales
/// de 4×18 separados por 6 (como palabras sin espacios grandes).
/// </summary>
[Collection(OutputDirectoryCollection.Name)]
public class TextInkTests : IDisposable
{
    const double Density = 160;
    static readonly SKColor Page = new(0xF5, 0xF5, 0xF5);
    static readonly SKColor Ink = new(0x0D, 0x00, 0x1F);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public TextInkTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TextInsideAWiderViewMatchesItsTightLayer()
    {
        // Figma: capa ajustada al texto. App: un TextView que ocupa toda la fila, con el texto centrado.
        var letters = new Rect(150, 100, 100, 20);
        var design = Frame(Text("Title", "Hola", letters));
        var designPng = Draw("design.png", canvas => Letters(canvas, letters, Ink));
        var capture = Capture(Draw("device.png", canvas => Letters(canvas, letters, Ink)),
            new ScreenElement(null, "android.widget.TextView", "Hola", null, new Rect(20, 95, 360, 30)));

        var result = Compare(design, designPng, capture);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void RoundedButtonCornersAreNotLetters()
    {
        // Botón redondeado: en las esquinas se ve la página, y al redondear los límites de la vista entra
        // una fila entera de página arriba. Nada de eso es parte del texto "Sign up".
        var letters = new Rect(165, 518, 70, 20);
        var button = new Rect(20, 500, 360, 56);
        var design = Frame(Text("Primary", "Sign up", letters with { Y = 520, Height = 15 }));
        var designPng = Draw("design.png", canvas => { Button(canvas, button); Letters(canvas, letters, SKColors.White); });
        var capture = Capture(Draw("device.png", canvas => { Button(canvas, button); Letters(canvas, letters, SKColors.White); }),
            new ScreenElement(null, "android.widget.Button", "Sign up", null, button with { Y = 499, Height = 57 }));

        var result = Compare(design, designPng, capture);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void LinesBesideTheTextAreNotLetters()
    {
        // Fila "or": las líneas a los lados llegan justo hasta el borde del TextView de 80.
        var letters = new Rect(193, 452, 14, 18);
        var design = Frame(Text("or", "or", letters));
        void DrawRow(SKCanvas canvas)
        {
            using var line = new SKPaint { Color = new SKColor(0xC8, 0xBE, 0xD4) };
            canvas.DrawRect(SKRect.Create(20, 460, 141, 2), line);
            canvas.DrawRect(SKRect.Create(239, 460, 141, 2), line);
            Letters(canvas, letters, Ink);
        }
        var capture = Capture(Draw("device.png", DrawRow),
            new ScreenElement(null, "android.widget.TextView", "or", null, new Rect(160, 448, 80, 24)));

        var result = Compare(design, Draw("design.png", DrawRow), capture);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void FieldBorderUnderTheTextIsNotLetters()
    {
        // El EditText llega hasta el borde inferior del campo: una línea naranja y, debajo, una fila tenue
        // de antialiasing que no alcanza a ser tinta. Ni una ni otra son parte del texto.
        var letters = new Rect(50, 520, 150, 16);
        var design = Frame(Text("Text", "Name@marvilo.net", letters));
        var capture = Capture(Draw("device.png", canvas =>
            {
                Letters(canvas, letters, Ink);
                using var border = new SKPaint { Color = new SKColor(0xFF, 0x8E, 0x56) };
                using var faint = new SKPaint { Color = new SKColor(0xF5, 0xE6, 0xDE) };
                canvas.DrawRect(SKRect.Create(20, 541, 360, 1), border);
                canvas.DrawRect(SKRect.Create(20, 542, 360, 1), faint);
            }),
            new ScreenElement(null, "android.widget.EditText", "Name@marvilo.net", null, new Rect(50, 518, 312, 25)));

        var result = Compare(design, Draw("design.png", canvas => Letters(canvas, letters, Ink)), capture);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void TailOfTheLabelAboveIsNotLetters()
    {
        // La etiqueta se monta 2 sobre el campo (margen -2): la cola de su "y" entra por arriba en la caja
        // del EditText, separada de "Name@marvilo.net" por filas vacías.
        var letters = new Rect(50, 524, 150, 16);
        var design = Frame(Text("Text", "Name@marvilo.net", letters));
        var capture = Capture(Draw("device.png", canvas =>
            {
                Letters(canvas, letters, Ink);
                using var tail = new SKPaint { Color = new SKColor(0xFF, 0x8E, 0x56) };
                canvas.DrawRect(SKRect.Create(110, 515, 4, 5), tail);
            }),
            new ScreenElement(null, "android.widget.EditText", "Name@marvilo.net", null, new Rect(50, 518, 312, 24)));

        var result = Compare(design, Draw("design.png", canvas => Letters(canvas, letters, Ink)), capture);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void LettersOutOfPlaceAreReported()
    {
        // Dos textos en su lugar fijan el desplazamiento global; el tercero está 8 más abajo.
        Rect title = new(150, 100, 100, 20), subtitle = new(120, 140, 160, 16), body = new(60, 300, 280, 20);
        var design = Frame(Text("Title", "Hola", title), Text("Subtitle", "Sub", subtitle), Text("Body", "Cuerpo", body));
        var designPng = Draw("design.png", canvas =>
        {
            Letters(canvas, title, Ink);
            Letters(canvas, subtitle, Ink);
            Letters(canvas, body, Ink);
        });
        var capture = Capture(Draw("device.png", canvas =>
            {
                Letters(canvas, title, Ink);
                Letters(canvas, subtitle, Ink);
                Letters(canvas, body with { Y = 308 }, Ink);
            }),
            new ScreenElement(null, "android.widget.TextView", "Hola", null, new Rect(20, 95, 360, 30)),
            new ScreenElement(null, "android.widget.TextView", "Sub", null, new Rect(20, 136, 360, 24)),
            new ScreenElement(null, "android.widget.TextView", "Cuerpo", null, new Rect(20, 296, 360, 40)));

        var result = Compare(design, designPng, capture);

        var diff = Assert.Single(result.Diffs);
        Assert.Equal(("Body «Cuerpo»", "y", "300", "308"), (diff.Name, diff.Property, diff.Expected, diff.Actual));
    }

    [Fact]
    public void SmallWidthDifferencesFromFontRenderingAreTolerated()
    {
        // 2 % más ancho (100 → 102): misma fuente dibujada distinto. Un 8 % ya es otra fuente.
        var design = Frame(Text("Title", "Hola", new Rect(150, 100, 100, 20)), Text("Error", "Error", new Rect(150, 200, 100, 20)));
        var designPng = Draw("design.png", canvas =>
        {
            Letters(canvas, new Rect(150, 100, 100, 20), Ink);
            Letters(canvas, new Rect(150, 200, 100, 20), Ink);
        });
        var capture = Capture(Draw("device.png", canvas =>
            {
                Letters(canvas, new Rect(150, 100, 102, 20), Ink);
                Letters(canvas, new Rect(150, 200, 108, 20), Ink);
            }),
            new ScreenElement(null, "android.widget.TextView", "Hola", null, new Rect(20, 95, 360, 30)),
            new ScreenElement(null, "android.widget.TextView", "Error", null, new Rect(20, 195, 360, 30)));

        var result = Compare(design, designPng, capture);

        var diff = Assert.Single(result.Diffs);
        Assert.Equal(("Error", "width"), (diff.Name, diff.Property));
    }

    [Fact]
    public void CenteredTextSlightlyNarrowerIsNotMisplaced()
    {
        // Texto centrado 4 más angosto (dentro del 2.5 %): su borde izquierdo se corre 2, más la tolerancia
        // de 1 que se pide. No es un error de posición.
        var design = Frame(Text("Subtitle", "Before we begin", new Rect(42, 320, 316, 17)));
        var designPng = Draw("design.png", canvas => Letters(canvas, new Rect(42, 320, 316, 17), Ink));
        var capture = Capture(Draw("device.png", canvas => Letters(canvas, new Rect(44, 320, 312, 17), Ink)),
            new ScreenElement(null, "android.widget.TextView", "Before we begin", null, new Rect(20, 316, 360, 24)));

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 1, colorTolerance: 24);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void StatusBarClockOfTheDesignIsNotReportedAsMissing()
    {
        // Los diseños dibujan una barra de estado de maqueta con la hora; no es un texto de la app.
        var letters = new Rect(150, 100, 100, 20);
        var design = Frame(Text("Title", "Hola", letters), Text("12:30", "12:30", new Rect(16, 8, 34, 16)), Text("Footer", "Pie", new Rect(150, 700, 60, 16)));
        var designPng = Draw("design.png", canvas => Letters(canvas, letters, Ink));
        var capture = Capture(Draw("device.png", canvas => Letters(canvas, letters, Ink)),
            new ScreenElement(null, "android.widget.TextView", "Hola", null, new Rect(20, 95, 360, 30)));

        var report = Compare(design, designPng, capture).Report;

        Assert.Contains("Se ignoró la hora de la barra de estado del diseño (12:30)", report);
        Assert.Contains("## Textos del diseño que no están en la app (1)", report);
        Assert.Contains("- Footer «Pie»", report);
    }

    ComparisonResult Compare(DesignFrame design, string designPng, ScreenCapture capture) =>
        new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24);

    static DesignFrame Frame(params DesignElement[] elements) =>
        new("key", "1:2", "Pantalla", new Rect(0, 0, 400, 800), elements);

    static DesignElement Text(string name, string text, Rect bounds) =>
        new(name, "TEXT", bounds, new Rgb(0x0D, 0x00, 0x1F), text, 16, "Inter", 400, null);

    static ScreenCapture Capture(string png, params ScreenElement[] elements) =>
        new("test", png, 400, 800, Density, elements);

    /// <summary>Trazos verticales de 4 de ancho cada 10 que ocupan exactamente <paramref name="area"/>.</summary>
    static void Letters(SKCanvas canvas, Rect area, SKColor color)
    {
        using var paint = new SKPaint { Color = color };
        for (var x = area.X; x + 4 <= area.X + area.Width; x += 10)
            canvas.DrawRect(SKRect.Create((float)x, (float)area.Y, 4, (float)area.Height), paint);
        canvas.DrawRect(SKRect.Create((float)(area.X + area.Width - 4), (float)area.Y, 4, (float)area.Height), paint);
    }

    static void Button(SKCanvas canvas, Rect r)
    {
        using var paint = new SKPaint { Color = new SKColor(0xDB, 0xC6, 0xF8), IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height), 28, 28, paint);
    }

    string Draw(string name, Action<SKCanvas> paint)
    {
        var path = Path.Combine(_dir, name);
        using var bitmap = new SKBitmap(400, 800);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Page);
            paint(canvas);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
