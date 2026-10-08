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
