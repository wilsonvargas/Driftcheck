using System.Text.Json;
using System.Text.RegularExpressions;

namespace Driftcheck.Mcp.Figma;

/// <summary>Qué es una capa sin texto que se compara por cómo se ve.</summary>
public enum ShapeKind { Shape, Icon, Image }

/// <summary>Elemento del diseño, con coordenadas relativas al frame.</summary>
/// <param name="Shape">Solo en las capas de <see cref="DesignFrame.Shapes"/>.</param>
/// <param name="Stroke">Color del borde, si tiene uno.</param>
/// <param name="MultiColor">Un ícono de varios colores (el de Google): no tiene un color para comparar.</param>
/// <param name="WidthShare">
/// En una fila de Auto Layout, la parte del ancho sobrante que le toca si crece (Fill): 0.5 si son dos los que crecen.
/// </param>
public sealed record DesignElement(
    string Name,
    string Type,
    Rect Bounds,
    Rgb? Fill,
    string? Text,
    double? FontSize,
    string? FontFamily,
    double? FontWeight,
    double? CornerRadius,
    string? NodeId = null,
    ShapeKind? Shape = null,
    Rgb? Stroke = null,
    bool MultiColor = false,
    double? WidthShare = null);

/// <param name="CachedAt">Fecha en que se descargó, si viene de la caché local; null si se acaba de descargar.</param>
/// <param name="Shapes">Capas sin texto que se ven (formas, íconos, imágenes), tengan el nombre que tengan.</param>
/// <param name="Background">Color de fondo del frame.</param>
public sealed record DesignFrame(
    string FileKey, string NodeId, string Name, Rect Bounds, IReadOnlyList<DesignElement> Elements, DateTime? CachedAt = null,
    IReadOnlyList<DesignElement>? Shapes = null, Rgb? Background = null);

public sealed partial class FigmaClient(HttpClient http, Settings settings)
{
    const string Api = "https://api.figma.com/v1";

    /// <summary>Escala del PNG que trae un export del plugin (y la que usa compare_screen).</summary>
    const double ExportScale = 2;

    readonly DesignExports exports = new(settings);

    const string PluginHint =
        "Without the API, export the frame with the Driftcheck plugin in Figma (tools/figma-export-plugin) and leave " +
        "the .driftcheck.json file in Downloads, or pass its path to import_design.";

    /// <summary>
    /// Acepta URLs del tipo https://www.figma.com/design/{key}/{name}?node-id=12-34
    /// (también /file/ y /proto/).
    /// </summary>
    public static (string FileKey, string NodeId) ParseUrl(string url)
    {
        var m = FigmaUrlRegex().Match(url);
        if (!m.Success)
            throw new ArgumentException($"Unrecognized Figma URL: {url}");

        var nodeMatch = NodeIdRegex().Match(url);
        if (!nodeMatch.Success)
            throw new ArgumentException("The URL doesn't include ?node-id=. Select the frame in Figma and copy its link (Copy link to selection).");

        var nodeId = Uri.UnescapeDataString(nodeMatch.Groups[1].Value).Replace('-', ':');
        return (m.Groups[1].Value, nodeId);
    }

    /// <summary>
    /// Descarga el frame, o lo lee de la caché local. La API de Figma tiene un cupo muy bajo en algunos
    /// tipos de cuenta (unas pocas llamadas por período), así que cada frame se descarga una sola vez
    /// salvo que se pida <paramref name="refresh"/>. Un export del plugin más nuevo que la caché la reemplaza.
    /// </summary>
    public async Task<DesignFrame> GetFrameAsync(string url, bool refresh = false, CancellationToken ct = default)
    {
        var (fileKey, nodeId) = ParseUrl(url);
        var cachePath = CachePath(fileKey, nodeId, "json");
        await ImportNewerExportAsync(fileKey, nodeId, ct);

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
        var frameBounds = ReadBox(node) ?? throw new InvalidOperationException("The node has no absoluteBoundingBox.");

        // Las cajas del JSON pueden estar desactualizadas dentro de instancias; ver AutoLayout.
        var layout = AutoLayout.Reflow(node);

        var elements = new List<DesignElement>();
        var shapes = new List<DesignElement>();
        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
            {
                Collect(child, frameBounds, layout, elements);
                CollectShapes(child, frameBounds, layout, shapes, null);
            }

        return new DesignFrame(fileKey, nodeId, node.GetProperty("name").GetString() ?? nodeId,
            frameBounds with { X = 0, Y = 0 }, elements, cachedAt, shapes, ReadSolidFill(node));
    }

    /// <summary>Renderiza el frame a PNG (o lo toma de la caché) y devuelve la ruta local.</summary>
    public async Task<string> RenderPngAsync(string url, double scale, bool refresh = false, CancellationToken ct = default)
    {
        var (fileKey, nodeId) = ParseUrl(url);
        var scaleText = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = CachePath(fileKey, nodeId, $"{scaleText}x.png");
        if (scale == ExportScale) await ImportNewerExportAsync(fileKey, nodeId, ct);
        if (!refresh && File.Exists(path)) return path;

        using var doc = JsonDocument.Parse(await GetStringAsync(
            $"{Api}/images/{fileKey}?ids={Uri.EscapeDataString(nodeId)}&format=png&scale={scaleText}", ct));

        var imageUrl = doc.RootElement.GetProperty("images").GetProperty(nodeId).GetString()
            ?? throw new InvalidOperationException("Figma didn't return the frame image.");

        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(imageUrl, ct), ct);
        return path;
    }

    /// <summary>
    /// Si hay un export del plugin de este frame más nuevo que la caché (o no hay caché), lo pasa a la caché:
    /// volver a exportar el frame en Figma es la forma de actualizarlo sin la API.
    /// </summary>
    async Task ImportNewerExportAsync(string fileKey, string nodeId, CancellationToken ct)
    {
        if (exports.Find(fileKey, nodeId) is not { } export) return;

        var jsonPath = CachePath(fileKey, nodeId, "json");
        if (File.Exists(jsonPath) && File.GetLastWriteTimeUtc(jsonPath) >= export.ExportedAtUtc) return;

        var pngPath = CachePath(fileKey, nodeId, $"{ExportScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}x.png");
        await exports.ImportAsync(export, jsonPath, pngPath, ct);
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
            throw new InvalidOperationException("The FIGMA_TOKEN environment variable is missing (a Figma Personal Access Token with the File content: Read-only scope). " + PluginHint);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Figma-Token", settings.FigmaToken);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var wait = res.Headers.RetryAfter?.Delta is { } delta ? $" Figma asks to wait {FormatWait(delta)}." : "";
            throw new InvalidOperationException(
                $"Figma rejected the request because of its rate limit (429).{wait} Frames already downloaded keep " +
                "working from the cache; the quota depends on the seat type of the account that owns the token. " + PluginHint);
        }

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Figma API {(int)res.StatusCode}: {body}");

        return body;
    }

    static string FormatWait(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{t.TotalDays:0.#} days" : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} hours" : $"{Math.Ceiling(t.TotalMinutes)} minutes";

    static void Collect(JsonElement node, Rect frame, Dictionary<string, Rect> layout, List<DesignElement> output)
    {
        if (node.TryGetProperty("visible", out var visible) && !visible.GetBoolean())
            return;

        var name = node.GetProperty("name").GetString() ?? "";
        var type = node.GetProperty("type").GetString() ?? "";
        var box = node.TryGetProperty("id", out var id) && layout.TryGetValue(id.GetString() ?? "", out var laidOut)
            ? laidOut
            : ReadBox(node);

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
                TryDouble(node, "cornerRadius"),
                NodeId(node)));
        }

        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                Collect(child, frame, layout, output);
    }

    /// <summary>
    /// Capas sin texto que se ven: formas con relleno o borde (botones, campos, líneas), imágenes e íconos.
    /// Se toman aunque tengan nombres automáticos ("Frame", "Vector"): se comparan por cómo se ven en el PNG,
    /// no por nombre. Un ícono hecho de varios vectores se toma entero.
    /// </summary>
    static void CollectShapes(JsonElement node, Rect frame, Dictionary<string, Rect> layout, List<DesignElement> output, double? widthShare)
    {
        const double MaxIconSize = 64;
        const double MinArea = 24;

        var type = node.GetProperty("type").GetString() ?? "";
        if (!IsVisible(node) || type == "TEXT") return;

        var name = node.GetProperty("name").GetString() ?? "";
        var box = node.TryGetProperty("id", out var id) && layout.TryGetValue(id.GetString() ?? "", out var laidOut)
            ? laidOut
            : ReadBox(node);

        // Una línea tiene alto 0: cuenta como de 1.
        if (box is { } b && Math.Max(b.Width, 1) * Math.Max(b.Height, 1) >= MinArea)
        {
            var bounds = b.Offset(-frame.X, -frame.Y);
            if (b.Width <= MaxIconSize && b.Height <= MaxIconSize && IconColors(node) is { } colors)
            {
                output.Add(new DesignElement(name, type, bounds, colors.Count == 1 ? colors[0] : null, null, null, null, null, null,
                    NodeId(node), ShapeKind.Icon, MultiColor: colors.Count > 1, WidthShare: widthShare));
                return;
            }

            var image = HasImageFill(node);
            var fill = ReadSolidFill(node);
            var stroke = ReadSolidStroke(node);
            if (image || fill is not null || stroke is not null)
                output.Add(new DesignElement(name, type, bounds, fill, null, null, null, null, TryDouble(node, "cornerRadius"),
                    NodeId(node), image ? ShapeKind.Image : ShapeKind.Shape, stroke, WidthShare: widthShare));
        }

        if (!node.TryGetProperty("children", out var children)) return;

        // En una fila, el ancho que sobra o falta se reparte entre los hijos que crecen, según su layoutGrow.
        var row = node.TryGetProperty("layoutMode", out var mode) && mode.GetString() == "HORIZONTAL";
        var growTotal = row ? children.EnumerateArray().Where(IsVisible).Sum(Grow) : 0;
        foreach (var child in children.EnumerateArray())
            CollectShapes(child, frame, layout, output, growTotal > 0 && Grow(child) > 0 ? Grow(child) / growTotal : null);
    }

    static double Grow(JsonElement node)
    {
        if (TryDouble(node, "layoutGrow") is double grow && grow > 0) return grow;
        return node.TryGetProperty("layoutSizingHorizontal", out var sizing) && sizing.GetString() == "FILL" ? 1 : 0;
    }

    /// <summary>
    /// Colores de un ícono: una capa hecha solo de vectores (sin textos, imágenes ni contenedores con fondo
    /// propio), con al menos uno. Null si no es un ícono.
    /// </summary>
    static List<Rgb>? IconColors(JsonElement node)
    {
        var colors = new List<Rgb>();
        var vectors = 0;

        bool Visit(JsonElement n)
        {
            if (!IsVisible(n)) return true;
            var type = n.GetProperty("type").GetString();
            if (type == "TEXT" || HasImageFill(n)) return false;

            if (type is "VECTOR" or "BOOLEAN_OPERATION" or "ELLIPSE" or "STAR" or "REGULAR_POLYGON" or "LINE")
            {
                vectors++;
                foreach (var c in new[] { ReadSolidFill(n), ReadSolidStroke(n) })
                    if (c is { } color && !colors.Contains(color)) colors.Add(color);
                return true;
            }

            // Un contenedor con fondo (el círculo de un botón de ícono) es una forma; su ícono se toma aparte.
            if (ReadSolidFill(n) is not null || ReadSolidStroke(n) is not null) return false;
            return !n.TryGetProperty("children", out var children) || children.EnumerateArray().All(Visit);
        }

        return Visit(node) && vectors > 0 ? colors : null;
    }

    static bool IsVisible(JsonElement node) =>
        !node.TryGetProperty("visible", out var visible) || visible.GetBoolean();

    static string? NodeId(JsonElement node) =>
        node.TryGetProperty("id", out var id) ? id.GetString() : null;

    internal static Rect? ReadBox(JsonElement node)
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

    static Rgb? ReadSolidStroke(JsonElement node)
    {
        if (TryDouble(node, "strokeWeight") is not > 0) return null;
        if (!node.TryGetProperty("strokes", out var strokes) || strokes.ValueKind != JsonValueKind.Array) return null;

        foreach (var stroke in strokes.EnumerateArray())
        {
            if (stroke.TryGetProperty("visible", out var v) && !v.GetBoolean()) continue;
            if (stroke.GetProperty("type").GetString() != "SOLID") continue;

            var c = stroke.GetProperty("color");
            return new Rgb(ToByte(c, "r"), ToByte(c, "g"), ToByte(c, "b"));
        }

        return null;
    }

    static bool HasImageFill(JsonElement node) =>
        node.TryGetProperty("fills", out var fills) && fills.ValueKind == JsonValueKind.Array &&
        fills.EnumerateArray().Any(f => (!f.TryGetProperty("visible", out var v) || v.GetBoolean()) && f.GetProperty("type").GetString() == "IMAGE");

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
