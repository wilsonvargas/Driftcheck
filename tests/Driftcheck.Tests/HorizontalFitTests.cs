using Driftcheck.Mcp;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Tests;

[Collection(OutputDirectoryCollection.Name)]
public class HorizontalFitTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public HorizontalFitTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(430, 411.4, 1)]      // Frame de 430 en un Pixel de 411 dp: mismas unidades.
    [InlineData(375, 411.4, 1)]
    [InlineData(200, 400, 2)]        // Frame a la mitad: se escala.
    [InlineData(860, 411.4, 411.4 / 860)] // Diseño exportado a @2x.
    public void ScalesOnlyWhenWidthsAreVeryDifferent(double design, double device, double expected) =>
        Assert.Equal(expected, ScreenComparer.ChooseScale(design, device), precision: 6);

    [Fact]
    public void NarrowerDeviceKeepsMarginsAndCentersInDp()
    {
        // Caso real: frame de 430 dp en un dispositivo de 411 dp. En MAUI el margen de 35 sigue siendo 35,
        // lo estirado pierde 19 dp de ancho y lo centrado se corre 9,5 dp. Nada de eso es un error.
        var design = Frame(430,
            Element("Field", new Rect(35, 500, 360, 46)),
            Element("Title", new Rect(150, 278, 130, 38)),
            Element("Close", new Rect(380, 20, 20, 20)));
        var capture = Capture(411,
            new ScreenElement("Field", "EditText", null, null, new Rect(35, 500, 341, 46)),
            new ScreenElement("Title", "TextView", null, null, new Rect(140.5, 278, 130, 38)),
            new ScreenElement("Close", "ImageView", null, null, new Rect(361, 20, 20, 20)));

        var result = new ScreenComparer(new Settings()).Compare(design, Png(430), capture, tolerance: 2, colorTolerance: 24);

        Assert.Equal(3, result.Matched);
        Assert.Empty(result.Diffs);
        Assert.Contains("1:1 en dp", result.Report);
        Assert.Contains("19 dp más angosto", result.Report);
    }

    [Fact]
    public void StillReportsRealHorizontalErrors()
    {
        // Margen de 60 en vez de 35: ninguna forma de anclaje lo explica.
        var design = Frame(430, Element("Field", new Rect(35, 500, 360, 46)));
        var capture = Capture(411, new ScreenElement("Field", "EditText", null, null, new Rect(60, 500, 316, 46)));

        var result = new ScreenComparer(new Settings()).Compare(design, Png(430), capture, tolerance: 2, colorTolerance: 24);

        Assert.Contains(result.Diffs, d => d is { Name: "Field", Property: "x" });
    }

    static DesignElement Element(string name, Rect bounds) =>
        new(name, "FRAME", bounds, null, null, null, null, null, null);

    static DesignFrame Frame(double width, params DesignElement[] elements) =>
        new("key", "1:2", "Frame", new Rect(0, 0, width, 800), elements);

    // 1 px = 1 dp.
    ScreenCapture Capture(int width, params ScreenElement[] elements) =>
        new("test", Png(width, "device"), width, 800, 1, elements);

    string Png(int width, string name = "design")
    {
        var path = Path.Combine(_dir, $"{name}.png");
        using var bitmap = new SKBitmap(width, 800);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
