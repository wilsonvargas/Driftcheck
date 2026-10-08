using System.ComponentModel;
using System.Globalization;
using System.Text;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Figma;
using ModelContextProtocol.Server;

namespace Driftcheck.Mcp.Tools;

[McpServerToolType]
public sealed class DriftcheckTools(FigmaClient figma, AdbClient adb, ScreenComparer comparer)
{
    [McpServerTool(Name = "list_devices"), Description("Lista los dispositivos y emuladores Android conectados por adb.")]
    public async Task<string> ListDevices(CancellationToken ct)
    {
        var devices = await adb.ListDevicesAsync(ct);
        return devices.Count == 0
            ? "No hay dispositivos conectados. Inicia el emulador o conecta un teléfono con depuración USB."
            : string.Join('\n', devices);
    }

    [McpServerTool(Name = "get_design_specs"), Description(
        "Devuelve las medidas del frame de Figma: posición, tamaño, colores, textos y tipografía de cada capa con nombre. " +
        "Úsalo ANTES de escribir el XAML de una pantalla. Si se pasa deviceWidth, los valores se escalan a ese ancho en dp.")]
    public async Task<string> GetDesignSpecs(
        [Description("URL del frame en Figma (Copy link to selection), debe incluir ?node-id=")] string figmaUrl,
        [Description("Ancho lógico del dispositivo destino en dp (ej. 412 para Pixel 7). Opcional.")] double? deviceWidth = null,
        [Description("true para volver a descargar el frame de Figma en vez de usar la caché local. Úsalo solo si el diseño cambió: cada descarga gasta cupo de la API de Figma.")] bool refresh = false,
        CancellationToken ct = default)
    {
        var frame = await figma.GetFrameAsync(figmaUrl, refresh, ct);
        var scale = deviceWidth is > 0 ? deviceWidth.Value / frame.Bounds.Width : 1;

        var sb = new StringBuilder();
        sb.AppendLine($"# {frame.Name} ({frame.Bounds.Width:0.#}×{frame.Bounds.Height:0.#}, escala {scale:0.###})");
        sb.AppendLine();
        if (frame.CachedAt is { } cachedAt)
            sb.AppendLine($"Diseño tomado de la caché local (descargado el {cachedAt:yyyy-MM-dd HH:mm}). Usa refresh=true si cambió en Figma.");
        sb.AppendLine("compare_screen empareja por texto visible; para capas sin texto usa el nombre de la capa como AutomationId.");
        sb.AppendLine();
        sb.AppendLine("| Capa | Tipo | x | y | ancho | alto | fondo | radio | texto | fuente |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var e in frame.Elements)
        {
            var b = e.Bounds.Scale(scale);
            var font = e.FontSize is { } fs
                ? $"{e.FontFamily} {F(fs * scale)} w{e.FontWeight:0}"
                : "";
            sb.AppendLine($"| {e.Name} | {e.Type} | {F(b.X)} | {F(b.Y)} | {F(b.Width)} | {F(b.Height)} | {e.Fill?.Hex} | {(e.CornerRadius is { } r ? F(r * scale) : "")} | {Escape(e.Text)} | {font} |");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "capture_screen"), Description(
        "Toma una captura del dispositivo Android y lista los elementos visibles con su AutomationId (resource-id), texto y bounds en dp.")]
    public async Task<string> CaptureScreen(
        [Description("Serial del dispositivo (de list_devices). Opcional: usa el primero.")] string? deviceSerial = null,
        CancellationToken ct = default)
    {
        var capture = await adb.CaptureAsync(deviceSerial, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Captura: {capture.ScreenshotPath}");
        sb.AppendLine($"Dispositivo: {capture.Serial}, {capture.WidthPx}×{capture.HeightPx} px, densidad {capture.Density} ({capture.WidthDp:0.#} dp de ancho)");
        sb.AppendLine();
        sb.AppendLine("| AutomationId | Clase | Texto | x | y | ancho | alto |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var e in capture.Elements)
            sb.AppendLine($"| {e.Id} | {e.ClassName.Split('.').Last()} | {Escape(e.Text ?? e.ContentDescription)} | {F(e.Bounds.X)} | {F(e.Bounds.Y)} | {F(e.Bounds.Width)} | {F(e.Bounds.Height)} |");

        return sb.ToString();
    }

    [McpServerTool(Name = "compare_screen"), Description(
        "Compara la pantalla actual de la app en el dispositivo Android con un frame de Figma. " +
        "Empareja capas de Figma con elementos de la app por texto visible, nombre = AutomationId o posición, y reporta diferencias de posición, tamaño, " +
        "texto y color de fondo. Genera side_by_side.png y diff.png (en diff.png: verde = coincide, naranja = difiere, " +
        "punteado azul = dónde debería estar según Figma). Abre las imágenes para una revisión visual.")]
    public async Task<string> CompareScreen(
        [Description("URL del frame en Figma (Copy link to selection), debe incluir ?node-id=")] string figmaUrl,
        [Description("Serial del dispositivo. Opcional: usa el primero.")] string? deviceSerial = null,
        [Description("Tolerancia de posición/tamaño en dp. Por defecto 2.")] double tolerance = 2,
        [Description("Tolerancia de color (distancia RGB 0–441). Por defecto 24.")] double colorTolerance = 24,
        [Description("true para volver a descargar el frame de Figma en vez de usar la caché local. Úsalo solo si el diseño cambió: cada descarga gasta cupo de la API de Figma.")] bool refresh = false,
        CancellationToken ct = default)
    {
        var frameTask = figma.GetFrameAsync(figmaUrl, refresh, ct);
        var pngTask = figma.RenderPngAsync(figmaUrl, 2, refresh, ct);
        var captureTask = adb.CaptureAsync(deviceSerial, ct);
        await Task.WhenAll(frameTask, pngTask, captureTask);

        var result = comparer.Compare(await frameTask, await pngTask, await captureTask, tolerance, colorTolerance);
        return result.Report + $"\n\nside_by_side: {result.SideBySidePath}\ndiff: {result.DiffPath}";
    }

    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    static string Escape(string? s) =>
        s is null ? "" : s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ⏎ ");
}
