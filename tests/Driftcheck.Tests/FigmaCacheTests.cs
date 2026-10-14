using Driftcheck.Mcp;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Tests;

[Collection(OutputDirectoryCollection.Name)]
public class FigmaCacheTests : IDisposable
{
    const string Url = "https://www.figma.com/design/AbC123/Paylo?node-id=12-34";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public FigmaCacheTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "cache"));
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", _dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ReadsFrameAndPngFromCacheWithoutCallingFigma()
    {
        File.WriteAllText(Path.Combine(_dir, "cache", "AbC123_12-34.json"), """
            {"nodes":{"12:34":{"document":{"id":"12:34","name":"Login","type":"FRAME",
              "absoluteBoundingBox":{"x":100,"y":200,"width":430,"height":932},
              "children":[{"id":"1:1","name":"Title","type":"TEXT","characters":"Hola",
                "absoluteBoundingBox":{"x":135,"y":300,"width":200,"height":30}}]}}}}
            """);
        var png = Path.Combine(_dir, "cache", "AbC123_12-34.2x.png");
        File.WriteAllBytes(png, [1, 2, 3]);

        var client = new FigmaClient(new HttpClient(new NoNetwork()), new Settings());

        var frame = await client.GetFrameAsync(Url);
        Assert.Equal("Login", frame.Name);
        Assert.NotNull(frame.CachedAt);
        Assert.Equal(new Rect(35, 100, 200, 30), Assert.Single(frame.Elements).Bounds);

        Assert.Equal(png, await client.RenderPngAsync(Url, 2));
    }

    [Fact]
    public async Task CollectsVisibleLayersWithoutTextEvenWithAutomaticNames()
    {
        File.WriteAllText(Path.Combine(_dir, "cache", "AbC123_12-34.json"), """
            {"nodes":{"12:34":{"document":{"id":"12:34","name":"Login","type":"FRAME",
              "absoluteBoundingBox":{"x":0,"y":0,"width":430,"height":932},
              "fills":[{"type":"SOLID","color":{"r":0.96,"g":0.96,"b":0.96,"a":1}}],
              "children":[
                {"id":"1:1","name":"Type=Primary, State=Default","type":"INSTANCE","absoluteBoundingBox":{"x":35,"y":660,"width":360,"height":55},
                 "fills":[{"type":"SOLID","color":{"r":0.62,"g":0.337,"b":1,"a":1}}],"cornerRadius":100,
                 "children":[{"id":"1:2","name":"Sign up","type":"TEXT","characters":"Sign up","absoluteBoundingBox":{"x":170,"y":678,"width":70,"height":20}}]},
                {"id":"2:1","name":"Auto Layout Horizontal","type":"FRAME","layoutMode":"HORIZONTAL","absoluteBoundingBox":{"x":35,"y":448,"width":360,"height":24},
                 "children":[
                   {"id":"2:2","name":"Frame","type":"FRAME","layoutGrow":1,"absoluteBoundingBox":{"x":35,"y":460,"width":140,"height":1},"fills":[{"type":"SOLID","color":{"r":0.784,"g":0.745,"b":0.831,"a":1}}]},
                   {"id":"2:3","name":"or","type":"TEXT","characters":"or","absoluteBoundingBox":{"x":199,"y":452,"width":12,"height":16}},
                   {"id":"2:4","name":"Frame","type":"FRAME","layoutGrow":1,"absoluteBoundingBox":{"x":255,"y":460,"width":140,"height":1},"fills":[{"type":"SOLID","color":{"r":0.784,"g":0.745,"b":0.831,"a":1}}]}]},
                {"id":"3:1","name":"Social icon","type":"INSTANCE","absoluteBoundingBox":{"x":53,"y":388,"width":20,"height":20},
                 "children":[
                   {"id":"3:2","name":"Vector","type":"VECTOR","absoluteBoundingBox":{"x":63,"y":396,"width":9,"height":9},"fills":[{"type":"SOLID","color":{"r":0.62,"g":0.337,"b":1,"a":1}}]},
                   {"id":"3:3","name":"Vector","type":"VECTOR","absoluteBoundingBox":{"x":54,"y":400,"width":15,"height":8},"fills":[{"type":"SOLID","color":{"r":0.784,"g":0.745,"b":0.831,"a":1}}]}]},
                {"id":"4:1","name":"Image","type":"RECTANGLE","absoluteBoundingBox":{"x":174,"y":165,"width":81,"height":81},
                 "fills":[{"type":"IMAGE","imageRef":"abc"}]},
                {"id":"5:1","name":"Field","type":"FRAME","absoluteBoundingBox":{"x":35,"y":496,"width":360,"height":46},
                 "fills":[{"type":"SOLID","color":{"r":1,"g":1,"b":1,"a":1}}],
                 "strokes":[{"type":"SOLID","color":{"r":1,"g":0.557,"b":0.337,"a":1}}],"strokeWeight":1,
                 "children":[{"id":"5:2","name":"Cursor","type":"RECTANGLE","absoluteBoundingBox":{"x":250,"y":518,"width":1,"height":17},"fills":[{"type":"SOLID","color":{"r":0.784,"g":0.745,"b":0.831,"a":1}}]}]},
                {"id":"6:1","name":"Hidden","type":"RECTANGLE","visible":false,"absoluteBoundingBox":{"x":0,"y":800,"width":100,"height":40},"fills":[{"type":"SOLID","color":{"r":0.784,"g":0.745,"b":0.831,"a":1}}]}]}}}}
            """);

        var frame = await new FigmaClient(new HttpClient(new NoNetwork()), new Settings()).GetFrameAsync(Url);

        Assert.Equal("#F5F5F5", frame.Background?.Hex);
        var shapes = frame.Shapes!.ToDictionary(s => s.NodeId!);
        Assert.Equal(["1:1", "2:2", "2:4", "3:1", "4:1", "5:1"], shapes.Keys.Order());

        // Un botón con nombre de variante, una línea "Frame" que crece en una fila con otra, un ícono de dos
        // colores, una imagen y un campo con borde. El cursor (17 de área) es muy chico; la capa oculta no cuenta.
        Assert.Equal(ShapeKind.Shape, shapes["1:1"].Shape);
        Assert.Equal("#9E56FF", shapes["1:1"].Fill?.Hex);
        Assert.Equal(0.5, shapes["2:2"].WidthShare);
        Assert.Equal(ShapeKind.Icon, shapes["3:1"].Shape);
        Assert.True(shapes["3:1"].MultiColor);
        Assert.Equal(new Rect(53, 388, 20, 20), shapes["3:1"].Bounds);
        Assert.Equal(ShapeKind.Image, shapes["4:1"].Shape);
        Assert.Equal("#FF8E56", shapes["5:1"].Stroke?.Hex);
    }

    [Fact]
    public async Task RefreshIgnoresTheCache()
    {
        File.WriteAllText(Path.Combine(_dir, "cache", "AbC123_12-34.json"), "{}");
        var client = new FigmaClient(new HttpClient(new NoNetwork()), new Settings());

        // Sin token ni red: si intenta descargar, falla, que es lo que se espera con refresh.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetFrameAsync(Url, refresh: true));
    }

    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"La prueba no debe llamar a Figma: {request.RequestUri}");
    }
}
