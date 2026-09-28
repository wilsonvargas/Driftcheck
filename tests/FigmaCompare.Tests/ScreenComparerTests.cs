using FigmaCompare.Mcp;
using FigmaCompare.Mcp.Android;
using FigmaCompare.Mcp.Comparison;
using FigmaCompare.Mcp.Figma;
using SkiaSharp;

namespace FigmaCompare.Tests;

[Collection(OutputDirectoryCollection.Name)]
public class ScreenComparerTests : IDisposable
{
    // Densidad 160 → 1 px = 1 dp, así las cuentas del test son directas.
    const double Density = 160;
    static readonly Rgb Blue = new(0x19, 0x76, 0xD2);
    static readonly Rgb Red = new(0xE5, 0x39, 0x35);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "figma-compare-tests", Guid.NewGuid().ToString("N"));

    public ScreenComparerTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("FIGMA_COMPARE_OUTPUT", _dir);
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

    (DesignFrame, string) Design(double scale)
    {
        var elements = new List<DesignElement>
        {
            new("Title", "TEXT", new Rect(20, 100, 200, 30).Scale(scale), new Rgb(0, 0, 0), "Bienvenido", 24 * scale, "Inter", 700, null),
            new("Subtitle", "TEXT", new Rect(20, 140, 300, 20).Scale(scale), new Rgb(90, 90, 90), null, 14 * scale, "Inter", 400, null),
            new("Login Button", "FRAME", new Rect(20, 600, 360, 48).Scale(scale), Blue, null, null, null, null, 12 * scale),
        };
        var frame = new DesignFrame("key", "1:2", "Login", new Rect(0, 0, 400 * scale, 800 * scale), elements);

        var png = Path.Combine(_dir, "design.png");
        Draw(png, 400, 800, (Blue, new Rect(20, 600, 360, 48)));
        return (frame, png);
    }

    ScreenCapture Capture(double loginY, Rgb loginColor, string title, double globalDy = 0)
    {
        var login = new Rect(20, loginY, 360, 48);
        var png = Path.Combine(_dir, "device.png");
        Draw(png, 400, 800, (loginColor, login));

        return new ScreenCapture("test", png, 400, 800, Density,
        [
            new ScreenElement("Title", "android.widget.TextView", title, null, new Rect(20, 100 + globalDy, 200, 30)),
            new ScreenElement("Subtitle", "android.widget.TextView", "Hola", null, new Rect(20, 140 + globalDy, 300, 20)),
            new ScreenElement("LoginButton", "android.widget.Button", null, null, login),
        ]);
    }

    static void Draw(string path, int width, int height, params (Rgb Color, Rect Rect)[] shapes)
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
