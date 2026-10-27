using System.Text.Json;
using Driftcheck.Mcp;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Tests;

/// <summary>Archivos .driftcheck.json del plugin de Figma: comparar sin la REST API.</summary>
[Collection(OutputDirectoryCollection.Name)]
public class DesignExportsTests : IDisposable
{
    const string Url = "https://www.figma.com/design/AbC123/Paylo?node-id=12-34";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));
    readonly string _downloads;

    public DesignExportsTests()
    {
        _downloads = Path.Combine(_dir, "Downloads");
        Directory.CreateDirectory(_downloads);
        Environment.SetEnvironmentVariable("DRIFTCHECK_OUTPUT", Path.Combine(_dir, "out"));
        Environment.SetEnvironmentVariable("DRIFTCHECK_EXPORTS", _downloads);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIFTCHECK_EXPORTS", null);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task FrameAndPngComeFromAnExportWithoutCallingFigma()
    {
        WriteExport("Login.driftcheck.json", fileKey: "AbC123", title: "Hola", png: [1, 2, 3]);
        var client = Client();

        var frame = await client.GetFrameAsync(Url);
        var png = await client.RenderPngAsync(Url, 2);

        Assert.Equal("Login", frame.Name);
        Assert.Equal("Hola", Assert.Single(frame.Elements).Text);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(png));
    }

    [Fact]
    public async Task ExportWithoutFileKeyIsRecognizedByNodeId()
    {
        // Un plugin publicado en la Community no recibe la clave del archivo.
        WriteExport("Login.driftcheck.json", fileKey: null, title: "Hola");

        Assert.Equal("Hola", Assert.Single((await Client().GetFrameAsync(Url)).Elements).Text);
    }

    [Fact]
    public async Task ExportOfAnotherFileIsIgnored()
    {
        WriteExport("Otro.driftcheck.json", fileKey: "OtroArchivo", title: "Hola");

        // Sin un export de este frame va a la API: falla por falta de token o porque la prueba no tiene red.
        await Assert.ThrowsAnyAsync<Exception>(() => Client().GetFrameAsync(Url));
        Assert.False(Directory.Exists(Path.Combine(_dir, "out", "cache")) &&
                     Directory.EnumerateFiles(Path.Combine(_dir, "out", "cache"), "*.json").Any());
    }

    [Fact]
    public async Task NewerExportReplacesTheCacheAndOlderOneDoesNot()
    {
        WriteExport("v1.driftcheck.json", fileKey: "AbC123", title: "Versión 1", exportedAt: "2026-10-01T10:00:00Z");
        var client = Client();
        Assert.Equal("Versión 1", Assert.Single((await client.GetFrameAsync(Url)).Elements).Text);

        // Volver a exportar en Figma actualiza el diseño sin la API.
        WriteExport("v2.driftcheck.json", fileKey: "AbC123", title: "Versión 2", exportedAt: "2026-10-02T10:00:00Z");
        Assert.Equal("Versión 2", Assert.Single((await client.GetFrameAsync(Url)).Elements).Text);

        File.Delete(Path.Combine(_downloads, "v2.driftcheck.json"));
        Assert.Equal("Versión 2", Assert.Single((await client.GetFrameAsync(Url)).Elements).Text);
    }

    [Fact]
    public async Task ImportCopiesAnExportFromAnotherFolder()
    {
        Environment.SetEnvironmentVariable("DRIFTCHECK_EXPORTS", null); // Descargas real + carpeta "exports" de la salida
        var elsewhere = Path.Combine(_dir, "otra-carpeta");
        Directory.CreateDirectory(elsewhere);
        var path = WriteExport("Login.driftcheck.json", fileKey: "AbC123", title: "Hola", folder: elsewhere);

        var export = new DesignExports(new Settings()).Import(path);

        Assert.Equal(("12:34", "Login"), (export.NodeId, export.NodeName));
        Assert.Equal("Hola", Assert.Single((await Client().GetFrameAsync(Url)).Elements).Text);
    }

    [Fact]
    public void ImportRejectsFilesThatAreNotExports()
    {
        var path = Path.Combine(_dir, "cualquier.json");
        File.WriteAllText(path, """{"nodes":{}}""");

        var error = Assert.Throws<InvalidOperationException>(() => new DesignExports(new Settings()).Import(path));
        Assert.Contains("is not a Driftcheck export", error.Message);
    }

    static FigmaClient Client() => new(new HttpClient(new NoNetwork()), new Settings());

    string WriteExport(string name, string? fileKey, string title, byte[]? png = null,
        string exportedAt = "2026-10-08T12:00:00Z", string? folder = null)
    {
        var data = new
        {
            driftcheck = 1,
            fileKey,
            fileName = "Paylo",
            nodeId = "12:34",
            nodeName = "Login",
            exportedAt,
            rest = new
            {
                document = new
                {
                    id = "12:34", name = "Login", type = "FRAME",
                    absoluteBoundingBox = new { x = 100, y = 200, width = 430, height = 932 },
                    children = new[]
                    {
                        new { id = "1:1", name = "Title", type = "TEXT", characters = title,
                              absoluteBoundingBox = new { x = 135, y = 300, width = 200, height = 30 } },
                    },
                },
            },
            png2x = Convert.ToBase64String(png ?? [9]),
        };

        var path = Path.Combine(folder ?? _downloads, name);
        File.WriteAllText(path, JsonSerializer.Serialize(data));
        return path;
    }

    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"No debe llamar a Figma: {request.RequestUri}");
    }
}
