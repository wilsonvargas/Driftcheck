using Driftcheck.Mcp;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Tests;

[Collection(OutputDirectoryCollection.Name)]
public class ScreenComparerTests : IDisposable
{
    // Densidad 160 → 1 px = 1 dp, así las cuentas del test son directas.
    const double PixelsPerDp = 1;
    static readonly Rgb Blue = new(0x19, 0x76, 0xD2);
    static readonly Rgb Red = new(0xE5, 0x39, 0x35);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public ScreenComparerTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void IdenticalScreenHasNoDiffs()
    {
        var (design, designPng) = Design(scale: 1);
        var capture = Capture(loginY: 600, loginColor: Blue, title: "Bienvenido");

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24);

        Assert.Equal(3, result.Matched);
        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void DetectsPositionColorAndTextDiffs()
    {
        var (design, designPng) = Design(scale: 1);
        var capture = Capture(loginY: 612, loginColor: Red, title: "Bienvenida");

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24);

        Assert.Contains(result.Diffs, d => d is { Name: "Login Button", Property: "y", Expected: "600", Actual: "612" });
        Assert.Contains(result.Diffs, d => d is { Name: "Login Button", Property: "background" } && d.Actual == Red.Hex);
        Assert.Contains(result.Diffs, d => d is { Name: "Title «Bienvenido»", Property: "text", Actual: "Bienvenida" });
        Assert.DoesNotContain(result.Diffs, d => d.Name == "Subtitle");
        Assert.True(File.Exists(result.DiffPath));
        Assert.True(File.Exists(result.SideBySidePath));
    }

    [Fact]
    public void ScalesDesignToDeviceWidthAndAbsorbsGlobalOffset()
    {
        // Frame de 200 de ancho (la mitad del dispositivo) y la app entera desplazada 24 dp hacia abajo
        // (como una barra de estado que el diseño no incluye).
        var (design, designPng) = Design(scale: 0.5);
        var capture = Capture(loginY: 624, loginColor: Blue, title: "Bienvenido", globalDy: 24);

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24);

        Assert.Empty(result.Diffs);
        Assert.Contains("dy=24", result.Report);
    }

    [Fact]
    public void DetectsTextColorDiff()
    {
        var (design, designPng) = Design(scale: 1, titleInk: Black);
        var capture = Capture(loginY: 600, loginColor: Blue, title: "Bienvenido", titleInk: Red);

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24);

        var diff = Assert.Single(result.Diffs);
        Assert.Equal(("Title «Bienvenido»", "text color", Black.Hex, Red.Hex), (diff.Name, diff.Property, diff.Expected, diff.Actual));
    }

    [Fact]
    public void TextColorComesFromDesignRenderNotFromFill()
    {
        // Un texto con fill negro dentro de un contenedor al 30 % se ve gris claro en el diseño;
        // si la app lo pinta igual, no hay diferencia aunque no coincida con el fill.
        var lightGray = new Rgb(0xB3, 0xB3, 0xB3);
        var (design, designPng) = Design(scale: 1, titleInk: lightGray);
        var capture = Capture(loginY: 600, loginColor: Blue, title: "Bienvenido", titleInk: lightGray);

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void FidelityCountsElementsWithoutDiffsAndShowsChangeFromPreviousRun()
    {
        var (design, designPng) = Design(scale: 1);
        var comparer = new ScreenComparer(new Settings());

        var before = comparer.Compare(design, designPng, Capture(loginY: 612, loginColor: Red, title: "Bienvenida"), tolerance: 2, colorTolerance: 24);
        var after = comparer.Compare(design, designPng, Capture(loginY: 600, loginColor: Blue, title: "Bienvenido"), tolerance: 2, colorTolerance: 24);

        // Antes solo coincidía Subtitle; ahora los tres.
        Assert.Equal((1, 3, 33, (int?)null), (before.Fidelity.Correct, before.Fidelity.Total, before.Fidelity.Percent, before.Fidelity.Previous));
        Assert.Equal((3, 3, 100, (int?)33), (after.Fidelity.Correct, after.Fidelity.Total, after.Fidelity.Percent, after.Fidelity.Previous));
        Assert.Contains("**Fidelidad: 100 %** · antes 33 % (+67)", after.Report);
    }

    [Fact]
    public void WritesSelfContainedHtmlReport()
    {
        var (design, designPng) = Design(scale: 1);
        var capture = Capture(loginY: 612, loginColor: Red, title: "Bienvenida </script>");

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24);

        var html = File.ReadAllText(result.HtmlReportPath);
        Assert.DoesNotContain("__DATA__", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("\"name\":\"Login Button\"", html);
        // Un texto de la app con </script> no puede cerrar el script de la página.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "</script>"));
        Assert.Contains(result.HtmlReportPath, result.Report);
    }

    [Fact]
    public void SuggestsFixesWithFrameworkProperties()
    {
        var (design, designPng) = Design(scale: 1);
        var capture = Capture(loginY: 612, loginColor: Red, title: "Bienvenida");

        var result = new ScreenComparer(new Settings()).Compare(design, designPng, capture, tolerance: 2, colorTolerance: 24, UiFramework.Maui);

        Assert.Contains(result.Diffs, d => d is { Property: "y", Fix: "Muévelo 12 dp hacia arriba: `Margin` superior −12 (o el espacio con el elemento de arriba)" });
        Assert.Contains(result.Diffs, d => d is { Property: "background", Fix: "`BackgroundColor=\"#1976D2\"`" });
        Assert.Contains(result.Diffs, d => d is { Property: "text", Fix: "`Text=\"Bienvenido\"`" });
        Assert.Contains("## Cómo corregirlo", result.Report);
        Assert.Contains("- **Login Button** (AutomationId `LoginButton`)", result.Report);
    }

    static readonly Rgb Black = new(0, 0, 0);

    (DesignFrame, string) Design(double scale, Rgb? titleInk = null)
    {
        var elements = new List<DesignElement>
        {
            new("Title", "TEXT", new Rect(20, 100, 200, 30).Scale(scale), new Rgb(0, 0, 0), "Bienvenido", 24 * scale, "Inter", 700, null),
            new("Subtitle", "TEXT", new Rect(20, 140, 300, 20).Scale(scale), new Rgb(90, 90, 90), null, 14 * scale, "Inter", 400, null),
            new("Login Button", "FRAME", new Rect(20, 600, 360, 48).Scale(scale), Blue, null, null, null, null, 12 * scale),
        };
        var frame = new DesignFrame("key", "1:2", "Login", new Rect(0, 0, 400 * scale, 800 * scale), elements);

        var png = Path.Combine(_dir, "design.png");
        Draw(png, 400, 800, [(Blue, new Rect(20, 600, 360, 48)), .. Glyphs(titleInk, new Rect(20, 100, 200, 30))]);
        return (frame, png);
    }

    ScreenCapture Capture(double loginY, Rgb loginColor, string title, double globalDy = 0, Rgb? titleInk = null)
    {
        var login = new Rect(20, loginY, 360, 48);
        var png = Path.Combine(_dir, "device.png");
        Draw(png, 400, 800, [(loginColor, login), .. Glyphs(titleInk, new Rect(20, 100 + globalDy, 200, 30))]);

        return new ScreenCapture("test", png, 400, 800, PixelsPerDp,
        [
            new ScreenElement("Title", "android.widget.TextView", title, null, new Rect(20, 100 + globalDy, 200, 30)),
            new ScreenElement("Subtitle", "android.widget.TextView", "Hola", null, new Rect(20, 140 + globalDy, 300, 20)),
            new ScreenElement("LoginButton", "android.widget.Button", null, null, login),
        ]);
    }

    /// <summary>Trazos verticales que hacen de letras dentro de un texto; ninguno si no hay color.</summary>
    static IEnumerable<(Rgb, Rect)> Glyphs(Rgb? ink, Rect text) =>
        ink is { } c ? Enumerable.Range(0, 8).Select(i => (c, new Rect(text.X + 10 + i * 20, text.Y + 6, 4, 18))) : [];

    static void Draw(string path, int width, int height, IEnumerable<(Rgb Color, Rect Rect)> shapes)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            foreach (var (color, r) in shapes)
            {
                using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B) };
                canvas.DrawRect(SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height), paint);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
