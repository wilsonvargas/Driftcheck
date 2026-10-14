using Driftcheck.Mcp;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Tests;

/// <summary>
/// Capas sin texto: un botón redondeado, un campo con borde, una línea y un ícono sobre un fondo gris,
/// dibujados igual en el "PNG del diseño" y en la "captura", salvo lo que cambia cada prueba.
/// </summary>
[Collection(OutputDirectoryCollection.Name)]
public class ShapeComparisonTests : IDisposable
{
    static readonly Rgb Page = new(0xF5, 0xF5, 0xF5);
    static readonly Rgb Purple = new(0x9E, 0x56, 0xFF);
    static readonly Rgb Orange = new(0xFF, 0x8E, 0x56);
    static readonly Rgb Line = new(0xC8, 0xBE, 0xD4);
    static readonly Rgb Ink = new(0x2B, 0x2B, 0x2B);

    static readonly Rect ButtonBox = new(20, 600, 360, 48);
    static readonly Rect FieldBox = new(20, 500, 360, 46);
    static readonly Rect LineBox = new(20, 450, 360, 1);
    static readonly Rect IconBox = new(190, 300, 20, 20);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public ShapeComparisonTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    sealed record Look(
        Rgb Button, double ButtonRadius = 24, bool FieldBorder = true, Rgb? LineColor = null, bool Icon = true,
        double Width = 400, double LineWidthShare = 1);

    static readonly Look AsDesigned = new(Purple);

    [Fact]
    public void IdenticalShapesHaveNoDiffs()
    {
        var result = Compare(AsDesigned);

        Assert.Empty(result.Diffs);
        Assert.Equal(100, result.Fidelity.Percent);
        // Botón, campo, línea, ícono y el fondo de la pantalla.
        Assert.Equal(5, result.Fidelity.Total);
        Assert.Contains("Capas sin texto (formas, íconos, imágenes): 4", result.Report);
    }

    [Fact]
    public void DetectsBackgroundCornerRadiusBorderAndLineColor()
    {
        var result = Compare(AsDesigned, AsDesigned with { Button = Orange, ButtonRadius = 4, FieldBorder = false, LineColor = Orange });

        Assert.Contains(result.Diffs, d => d is { Name: "Botón", Property: "background", Expected: "#9E56FF", Actual: "#FF8E56" });
        Assert.Contains(result.Diffs, d => d is { Name: "Botón", Property: "corner radius" });
        Assert.Contains(result.Diffs, d => d is { Name: "Campo", Property: "border", Expected: "#FF8E56", Actual: "sin borde" });
        Assert.Contains(result.Diffs, d => d is { Name: "Línea", Property: "background", Actual: "#FF8E56" });

        // Sin el borde, el relleno ocupa su lugar: no es una diferencia de tamaño.
        Assert.DoesNotContain(result.Diffs, d => d.Name == "Campo" && d.Property is "width" or "height");
    }

    [Fact]
    public void MissingIconIsReportedAsMissing()
    {
        var result = Compare(AsDesigned, AsDesigned with { Icon = false });

        var icon = Assert.Single(result.Elements, e => e.Name == "Ícono");
        Assert.Equal(ElementStatus.Missing, icon.Status);
        Assert.Contains("Falta un ícono de 20×20 dp", icon.Fix);
        Assert.Contains("## Capas sin texto del diseño que no se encontraron en la app (1)", result.Report);
    }

    [Fact]
    public void LineThatGrowsInARowTakesItsShareOfTheMissingWidth()
    {
        // Dispositivo 20 dp más angosto; la línea es una de dos que crecen en su fila, así que pierde 10.
        var result = Compare(AsDesigned, AsDesigned with { Width = 380, LineWidthShare = 0.5 }, lineShare: 0.5);

        Assert.DoesNotContain(result.Diffs, d => d.Name == "Línea");
    }

    [Fact]
    public void ColorFixUsesTheFigmaFillWhenTheRenderHasOpacity()
    {
        // En el diseño el botón se ve claro (al 30 %), pero su fill en Figma es el morado.
        var faded = new Rgb(0xDB, 0xC6, 0xF8);
        var result = Compare(new Look(faded), appLook: new Look(Orange), framework: UiFramework.Maui);

        var diff = Assert.Single(result.Diffs, d => d.Property == "background");
        Assert.Equal(faded.Hex, diff.Expected);
        Assert.StartsWith("`BackgroundColor=\"#9E56FF\"` (en el diseño se ve #DBC6F8", diff.Fix);
    }

    [Theory]
    [InlineData(862, 430, 2)]
    [InlineData(860, 430, 2)]
    [InlineData(1290, 430, 3)]
    [InlineData(400, 400, 1)]
    public void RenderScaleIsTheExportScale(int pngWidth, double frameWidth, double expected) =>
        Assert.Equal(expected, ScreenComparer.RenderScale(pngWidth, frameWidth));

    ComparisonResult Compare(Look designLook, Look? appLook = null, double lineShare = 1, UiFramework framework = UiFramework.Generic)
    {
        var app = appLook ?? designLook;
        var shapes = new List<DesignElement>
        {
            new("Botón", "INSTANCE", ButtonBox, Purple, null, null, null, null, 24, "1:1", ShapeKind.Shape),
            new("Campo", "FRAME", FieldBox, new Rgb(255, 255, 255), null, null, null, null, 8, "1:2", ShapeKind.Shape, Orange),
            new("Frame", "FRAME", LineBox, Line, null, null, null, null, null, "1:3", ShapeKind.Shape, WidthShare: lineShare < 1 ? lineShare : null),
            new("Vector", "VECTOR", IconBox, Ink, null, null, null, null, null, "1:4", ShapeKind.Icon),
        };
        // "Frame" y "Vector" son nombres automáticos: el reporte los muestra como "Línea" e "Ícono".
        var design = new DesignFrame("key", "1:0", "Login", new Rect(0, 0, 400, 800), [], Shapes: shapes, Background: Page);

        var designPng = Path.Combine(_dir, "design.png");
        Draw(designPng, designLook);
        var screenshot = Path.Combine(_dir, "device.png");
        Draw(screenshot, app);
        var capture = new ScreenCapture("test", screenshot, (int)app.Width, 800, 1, []);

        return new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24, framework);
    }

    /// <summary>Dibuja la pantalla: en un dispositivo más angosto, lo que va de borde a borde se achica.</summary>
    static void Draw(string path, Look look)
    {
        var narrower = 400 - look.Width;
        using var bitmap = new SKBitmap((int)look.Width, 800);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Sk(Page));
            using var paint = new SKPaint { IsAntialias = true };

            paint.Color = Sk(look.Button);
            canvas.DrawRoundRect(SKRect.Create((float)ButtonBox.X, (float)ButtonBox.Y, (float)(ButtonBox.Width - narrower), (float)ButtonBox.Height),
                (float)look.ButtonRadius, (float)look.ButtonRadius, paint);

            var field = SKRect.Create((float)FieldBox.X, (float)FieldBox.Y, (float)(FieldBox.Width - narrower), (float)FieldBox.Height);
            paint.Color = SKColors.White;
            canvas.DrawRoundRect(field, 8, 8, paint);
            if (look.FieldBorder)
            {
                using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Sk(Orange) };
                field.Inflate(-0.5f, -0.5f);
                canvas.DrawRoundRect(field, 7.5f, 7.5f, stroke);
            }

            paint.Color = Sk(look.LineColor ?? Line);
            canvas.DrawRect(SKRect.Create((float)LineBox.X, (float)LineBox.Y, (float)(LineBox.Width - narrower * look.LineWidthShare), 1), paint);

            if (look.Icon)
            {
                paint.Color = Sk(Ink);
                var center = (float)(IconBox.X + IconBox.Width / 2 - narrower / 2);
                canvas.DrawCircle(center, (float)(IconBox.Y + IconBox.Height / 2), (float)IconBox.Width / 2, paint);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    static SKColor Sk(Rgb c) => new(c.R, c.G, c.B);
}
