using System.Text.Json;
using System.Text.RegularExpressions;

namespace FigmaCompare.Mcp.Figma;

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

public sealed record DesignFrame(string FileKey, string NodeId, string Name, Rect Bounds, IReadOnlyList<DesignElement> Elements);

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

    public async Task<DesignFrame> GetFrameAsync(string url, CancellationToken ct = default)
    {
        var (fileKey, nodeId) = ParseUrl(url);
        using var doc = await GetJsonAsync($"{Api}/files/{fileKey}/nodes?ids={Uri.EscapeDataString(nodeId)}", ct);

        var node = doc.RootElement.GetProperty("nodes").GetProperty(nodeId).GetProperty("document");
        var frameBounds = ReadBox(node) ?? throw new InvalidOperationException("El nodo no tiene absoluteBoundingBox.");

        var elements = new List<DesignElement>();
        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                Collect(child, frameBounds, elements);

        return new DesignFrame(fileKey, nodeId, node.GetProperty("name").GetString() ?? nodeId,
            frameBounds with { X = 0, Y = 0 }, elements);
    }

    /// <summary>Renderiza el frame a PNG y devuelve la ruta local.</summary>
    public async Task<string> RenderPngAsync(string url, double scale, CancellationToken ct = default)
    {
        var (fileKey, nodeId) = ParseUrl(url);
        using var doc = await GetJsonAsync(
            $"{Api}/images/{fileKey}?ids={Uri.EscapeDataString(nodeId)}&format=png&scale={scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct);

        var imageUrl = doc.RootElement.GetProperty("images").GetProperty(nodeId).GetString()
            ?? throw new InvalidOperationException("Figma no devolvió la imagen del frame.");

        Directory.CreateDirectory(settings.OutputDirectory);
        var path = Path.Combine(settings.OutputDirectory, $"figma_{fileKey}_{nodeId.Replace(':', '-')}.png");
        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(imageUrl, ct), ct);
        return path;
    }

    async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.FigmaToken))
            throw new InvalidOperationException("Falta la variable de entorno FIGMA_TOKEN (Personal Access Token de Figma con alcance File content: Read-only).");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Figma-Token", settings.FigmaToken);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Figma API {(int)res.StatusCode}: {body}");

        return JsonDocument.Parse(body);
    }

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
