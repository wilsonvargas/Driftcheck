using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Driftcheck.Mcp.Figma;

/// <summary>
/// Archivos .driftcheck.json que genera el plugin de Figma (tools/figma-export-plugin): el frame con el
/// mismo JSON que la REST API y su render a 2x. Permiten usar Driftcheck sin token o sin cupo de la API:
/// el plugin corre dentro de Figma y no gasta llamadas.
/// <code>
/// { "driftcheck": 1, "fileKey": "…" | null, "fileName": "…", "nodeId": "2001:9332", "nodeName": "…",
///   "exportedAt": "2026-10-08T21:00:00Z", "rest": { "document": { … } }, "png2x": "&lt;base64&gt;" }
/// </code>
/// <c>fileKey</c> llega null cuando el plugin está publicado en la Community: Figma solo se lo da a plugins
/// privados o en desarrollo. Entonces el frame se reconoce solo por su id.
/// </summary>
public sealed class DesignExports(Settings settings)
{
    public const string Extension = ".driftcheck.json";
    const int FormatVersion = 1;

    public sealed record Export(string Path, string? FileKey, string NodeId, string NodeName, DateTime ExportedAtUtc);

    /// <summary>El export más reciente de ese frame en las carpetas de exportación, o null.</summary>
    public Export? Find(string fileKey, string nodeId) =>
        settings.ExportDirectories
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*" + Extension))
            .Select(TryReadHeader)
            .Where(e => e is not null && e.NodeId == nodeId && (e.FileKey is null || e.FileKey == fileKey))
            .MaxBy(e => e!.ExportedAtUtc);

    /// <summary>Escribe el JSON y el PNG del export en la caché, con la fecha del export.</summary>
    public async Task ImportAsync(Export export, string jsonCachePath, string pngCachePath, CancellationToken ct)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(export.Path, ct))!.AsObject();
        var rest = root["rest"] ?? throw Invalid(export.Path, "has no \"rest\"");
        var png = root["png2x"]?.GetValue<string>() ?? throw Invalid(export.Path, "has no \"png2x\"");

        // El mismo formato que GET /v1/files/{key}/nodes?ids={id}, que es lo que lee GetFrameAsync.
        var nodes = new JsonObject { [export.NodeId] = rest.DeepClone() };
        var cache = new JsonObject { ["name"] = root["fileName"]?.DeepClone(), ["nodes"] = nodes };

        await File.WriteAllTextAsync(jsonCachePath, cache.ToJsonString(), ct);
        await File.WriteAllBytesAsync(pngCachePath, Convert.FromBase64String(png), ct);
        File.SetLastWriteTimeUtc(jsonCachePath, export.ExportedAtUtc);
        File.SetLastWriteTimeUtc(pngCachePath, export.ExportedAtUtc);
    }

    /// <summary>
    /// Copia un export que está fuera de las carpetas que se revisan (Descargas) a la carpeta de la salida,
    /// para que compare_screen lo encuentre. Devuelve los datos del frame.
    /// </summary>
    public Export Import(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException($"File not found: {path}.");
        var export = TryReadHeader(path) ?? throw Invalid(path, $"is not a Driftcheck export (*{Extension})");

        Directory.CreateDirectory(settings.ImportedExportsDirectory);
        var target = System.IO.Path.Combine(settings.ImportedExportsDirectory, System.IO.Path.GetFileName(path));
        if (!string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(path, target, overwrite: true);
        return export with { Path = target };
    }

    static Export? TryReadHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("driftcheck", out var version) || version.GetInt32() != FormatVersion) return null;

            var exportedAt = DateTime.Parse(root.GetProperty("exportedAt").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            return new Export(path,
                root.TryGetProperty("fileKey", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null,
                root.GetProperty("nodeId").GetString()!,
                root.TryGetProperty("nodeName", out var name) ? name.GetString() ?? "" : "",
                exportedAt);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            return null;
        }
    }

    static InvalidOperationException Invalid(string path, string reason) =>
        new($"The file {path} {reason}. Export the frame again with the Driftcheck plugin.");
}
