using System.Text.Json;
using System.Text.RegularExpressions;

namespace Driftcheck.Mcp.Figma;

/// <summary>Elemento del diseño, con coordenadas relativas al frame.</summary>
public sealed record DesignElement(
    string Name,
    string Type,
    Rect Bounds,
    Rgb? Fill,
    string? Text,
    double? FontSize,
    string? FontFamily,
    double? FontWeight,
    double? CornerRadius);

/// <param name="CachedAt">Fecha en que se descargó, si viene de la caché local; null si se acaba de descargar.</param>
public sealed record DesignFrame(
    string FileKey, string NodeId, string Name, Rect Bounds, IReadOnlyList<DesignElement> Elements, DateTime? CachedAt = null);

public sealed partial class FigmaClient(HttpClient http, Settings settings)
{
    const string Api = "https://api.figma.com/v1";

    /// <summary>
    /// Acepta URLs del tipo https://www.figma.com/design/{key}/{name}?node-id=12-34
    /// (también /file/ y /proto/).
    /// </summary>
    public static (string FileKey, string NodeId) ParseUrl(string url)
    {
        var m = FigmaUrlRegex().Match(url);
        if (!m.Success)
            throw new ArgumentException($"No reconozco la URL de Figma: {url}");

        var nodeMatch = NodeIdRegex().Match(url);
        if (!nodeMatch.Success)
            throw new ArgumentException("La URL no incluye ?node-id=. Selecciona el frame en Figma y copia el link (Copy link to selection).");

        var nodeId = Uri.UnescapeDataString(nodeMatch.Groups[1].Value).Replace('-', ':');
        return (m.Groups[1].Value, nodeId);
    }

    /// <summary>
    /// Descarga el frame, o lo lee de la caché local. La API de Figma tiene un cupo muy bajo en algunos
    /// tipos de cuenta (unas pocas llamadas por período), así que cada frame se descarga una sola vez
    /// salvo que se pida <paramref name="refresh"/>.
    /// </summary>
    public async Task<DesignFrame> GetFrameAsync(string url, bool refresh = false, CancellationToken ct = default)
    {
        var (fileKey, nodeId) = ParseUrl(url);
        var cachePath = CachePath(fileKey, nodeId, "json");

        DateTime? cachedAt = null;
        string json;
        if (!refresh && File.Exists(cachePath))
        {
            json = await File.ReadAllTextAsync(cachePath, ct);
            cachedAt = File.GetLastWriteTime(cachePath);
        }
        else
        {
            json = await GetStringAsync($"{Api}/files/{fileKey}/nodes?ids={Uri.EscapeDataString(nodeId)}", ct);
            await File.WriteAllTextAsync(cachePath, json, ct);
        }

        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("nodes").GetProperty(nodeId).GetProperty("document");
        var frameBounds = ReadBox(node) ?? throw new InvalidOperationException("El nodo no tiene absoluteBoundingBox.");

        var elements = new List<DesignElement>();
        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                Collect(child, frameBounds, elements);

        return new DesignFrame(fileKey, nodeId, node.GetProperty("name").GetString() ?? nodeId,
            frameBounds with { X = 0, Y = 0 }, elements, cachedAt);
    }

    /// <summary>Renderiza el frame a PNG (o lo toma de la caché) y devuelve la ruta local.</summary>
    public async Task<string> RenderPngAsync(string url, double scale, bool refresh = false, CancellationToken ct = default)
    {
        var (fileKey, nodeId) = ParseUrl(url);
        var scaleText = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = CachePath(fileKey, nodeId, $"{scaleText}x.png");
        if (!refresh && File.Exists(path)) return path;

        using var doc = JsonDocument.Parse(await GetStringAsync(
            $"{Api}/images/{fileKey}?ids={Uri.EscapeDataString(nodeId)}&format=png&scale={scaleText}", ct));

        var imageUrl = doc.RootElement.GetProperty("images").GetProperty(nodeId).GetString()
            ?? throw new InvalidOperationException("Figma no devolvió la imagen del frame.");

        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(imageUrl, ct), ct);
        return path;
    }

    string CachePath(string fileKey, string nodeId, string suffix)
    {
        var dir = Path.Combine(settings.OutputDirectory, "cache");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{fileKey}_{nodeId.Replace(':', '-')}.{suffix}");
    }

    async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.FigmaToken))
            throw new InvalidOperationException("Falta la variable de entorno FIGMA_TOKEN (Personal Access Token de Figma con alcance File content: Read-only).");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Figma-Token", settings.FigmaToken);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var wait = res.Headers.RetryAfter?.Delta is { } delta ? $" Figma pide esperar {FormatWait(delta)}." : "";
            throw new InvalidOperationException(
                $"Figma rechazó la solicitud por límite de uso (429).{wait} Los frames ya descargados siguen " +
                "funcionando desde la caché; el cupo depende del tipo de asiento de la cuenta dueña del token.");
        }

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Figma API {(int)res.StatusCode}: {body}");

        return body;
    }

    static string FormatWait(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{t.TotalDays:0.#} días" : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} horas" : $"{Math.Ceiling(t.TotalMinutes)} minutos";

    static void Collect(JsonElement node, Rect frame, List<DesignElement> output)
    {
        if (node.TryGetProperty("visible", out var visible) && !visible.GetBoolean())
            return;

        var name = node.GetProperty("name").GetString() ?? "";
        var type = node.GetProperty("type").GetString() ?? "";
        var box = ReadBox(node);

        // Solo nos interesan las capas nombradas a propósito: los nombres automáticos
        // de Figma ("Frame 12", "Rectangle 3", "Vector") no se pueden emparejar con AutomationId.
        if (box is { } b && !AutoNameRegex().IsMatch(name))
        {
            string? text = null;
            double? fontSize = null, fontWeight = null;
            string? fontFamily = null;

            if (type == "TEXT")
            {
                text = node.TryGetProperty("characters", out var chars) ? chars.GetString() : null;
                if (node.TryGetProperty("style", out var style))
                {
                    fontSize = TryDouble(style, "fontSize");
                    fontWeight = TryDouble(style, "fontWeight");
                    fontFamily = style.TryGetProperty("fontFamily", out var ff) ? ff.GetString() : null;
                }
            }

            output.Add(new DesignElement(
                name, type,
                b.Offset(-frame.X, -frame.Y),
                ReadSolidFill(node),
                text, fontSize, fontFamily, fontWeight,
                TryDouble(node, "cornerRadius")));
        }

        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                Collect(child, frame, output);
    }

    static Rect? ReadBox(JsonElement node)
    {
        if (!node.TryGetProperty("absoluteBoundingBox", out var box) || box.ValueKind != JsonValueKind.Object)
            return null;

        return new Rect(box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble(),
            box.GetProperty("width").GetDouble(), box.GetProperty("height").GetDouble());
    }

    static Rgb? ReadSolidFill(JsonElement node)
    {
        if (!node.TryGetProperty("fills", out var fills) || fills.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var fill in fills.EnumerateArray())
        {
            if (fill.TryGetProperty("visible", out var v) && !v.GetBoolean()) continue;
            if (fill.GetProperty("type").GetString() != "SOLID") continue;

            var c = fill.GetProperty("color");
            return new Rgb(ToByte(c, "r"), ToByte(c, "g"), ToByte(c, "b"));
        }

        return null;
    }

    static byte ToByte(JsonElement color, string channel) =>
        (byte)Math.Round(color.GetProperty(channel).GetDouble() * 255);

    static double? TryDouble(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    [GeneratedRegex(@"figma\.com/(?:design|file|proto)/([A-Za-z0-9]+)")]
    private static partial Regex FigmaUrlRegex();

    [GeneratedRegex(@"[?&]node-id=([^&#]+)")]
    private static partial Regex NodeIdRegex();

    [GeneratedRegex(@"^(Frame|Group|Rectangle|Ellipse|Vector|Line|Polygon|Star|Union|Subtract|Intersect|Exclude|Image|Component|Instance|Auto layout)( \d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AutoNameRegex();
}
