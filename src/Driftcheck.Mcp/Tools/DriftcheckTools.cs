using System.ComponentModel;
using System.Globalization;
using System.Text;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using ModelContextProtocol.Server;

namespace Driftcheck.Mcp.Tools;

[McpServerToolType]
public sealed class DriftcheckTools(FigmaClient figma, DeviceRegistry devices, ScreenComparer comparer, Settings settings)
{
    [McpServerTool(Name = "import_design"), Description(
        "Importa un archivo .driftcheck.json exportado con el plugin de Driftcheck en Figma, para comparar sin la API " +
        "de Figma (sin token o sin cupo). Los que están en la carpeta Descargas se usan solos; esto es para los que " +
        "quedaron en otra carpeta. Después se compara con el link del frame, como siempre.")]
    public string ImportDesign(
        [Description("Ruta del archivo .driftcheck.json")] string path)
    {
        var export = new DesignExports(settings).Import(path);
        var file = export.FileKey is null ? "" : $" del archivo {export.FileKey}";
        return $"Importado «{export.NodeName}» (node-id={export.NodeId.Replace(':', '-')}){file}, exportado el " +
               $"{export.ExportedAtUtc:yyyy-MM-dd HH:mm} UTC. Usa get_design_specs o compare_screen con el link de ese " +
               "frame (Copy link to selection en Figma).";
    }

    [McpServerTool(Name = "list_devices"), Description(
        "Lista los dispositivos y emuladores Android conectados por adb y, en macOS, los simuladores de iOS encendidos.")]
    public async Task<string> ListDevices(CancellationToken ct)
    {
        var listing = await devices.ListAsync(ct);
        var sb = new StringBuilder();
        if (listing.Devices.Count == 0)
            sb.AppendLine("No hay dispositivos conectados. Inicia un emulador de Android o un simulador de iOS, o conecta un teléfono con depuración USB.");
        foreach (var (_, d) in listing.Devices)
            sb.AppendLine($"{d.Serial} — {d.Description}");
        foreach (var error in listing.Errors)
            sb.AppendLine($"(No se pudo consultar {error})");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_design_specs"), Description(
        "Devuelve las medidas del frame de Figma: posición, tamaño, colores, textos y tipografía de cada capa con nombre. " +
        "Úsalo ANTES de escribir el XAML de una pantalla. Si se pasa deviceWidth, los valores se escalan a ese ancho en dp o pt.")]
    public async Task<string> GetDesignSpecs(
        [Description("URL del frame en Figma (Copy link to selection), debe incluir ?node-id=")] string figmaUrl,
        [Description("Ancho lógico del dispositivo destino en dp o pt (ej. 412 para Pixel 7, 402 para iPhone 17). Opcional.")] double? deviceWidth = null,
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
        "Toma una captura del dispositivo (Android o simulador de iOS) y lista los elementos visibles con su AutomationId " +
        "(resource-id en Android, accessibilityIdentifier en iOS), texto y bounds en dp o pt. En iOS tarda unos segundos más: " +
        "lee el árbol de accesibilidad con un UI test, que se compila la primera vez.")]
    public async Task<string> CaptureScreen(
        [Description("Serial o UDID del dispositivo (de list_devices). Opcional: usa el primero.")] string? deviceSerial = null,
        CancellationToken ct = default)
    {
        var capture = await devices.CaptureAsync(deviceSerial, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Captura: {capture.ScreenshotPath}");
        sb.AppendLine($"Dispositivo: {capture.Serial}, {capture.WidthPx}×{capture.HeightPx} px, {capture.PixelsPerUnit:0.##} px por {capture.Unit} ({capture.WidthUnits:0.#} {capture.Unit} de ancho)");
        sb.AppendLine();
        sb.AppendLine("| AutomationId | Clase | Texto | x | y | ancho | alto |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var e in capture.Elements)
            sb.AppendLine($"| {e.Id} | {e.ClassName.Split('.').Last()} | {Escape(e.Text ?? e.ContentDescription)} | {F(e.Bounds.X)} | {F(e.Bounds.Y)} | {F(e.Bounds.Width)} | {F(e.Bounds.Height)} |");

        return sb.ToString();
    }

    [McpServerTool(Name = "compare_screen"), Description(
        "Compara la pantalla actual de la app en el dispositivo (Android o simulador de iOS) con un frame de Figma. " +
        "Empareja capas de Figma con elementos de la app por texto visible, nombre = AutomationId o posición, y reporta diferencias de posición, tamaño, " +
        "texto y color de fondo. Genera side_by_side.png y diff.png (en diff.png: verde = coincide, naranja = difiere, " +
        "punteado azul = dónde debería estar según Figma). Abre las imágenes para una revisión visual.")]
    public async Task<string> CompareScreen(
        [Description("URL del frame en Figma (Copy link to selection), debe incluir ?node-id=")] string figmaUrl,
        [Description("Serial o UDID del dispositivo. Opcional: usa el primero.")] string? deviceSerial = null,
        [Description("Tolerancia de posición/tamaño en dp o pt. Por defecto 2.")] double tolerance = 2,
        [Description("Tolerancia de color (distancia RGB 0–441). Por defecto 24.")] double colorTolerance = 24,
        [Description("true para volver a descargar el frame de Figma en vez de usar la caché local. Úsalo solo si el diseño cambió: cada descarga gasta cupo de la API de Figma.")] bool refresh = false,
        CancellationToken ct = default)
    {
        var frameTask = figma.GetFrameAsync(figmaUrl, refresh, ct);
        var pngTask = figma.RenderPngAsync(figmaUrl, 2, refresh, ct);
        var captureTask = devices.CaptureAsync(deviceSerial, ct);
        await Task.WhenAll(frameTask, pngTask, captureTask);

        var result = comparer.Compare(await frameTask, await pngTask, await captureTask, tolerance, colorTolerance);
        return result.Report + $"\n\nside_by_side: {result.SideBySidePath}\ndiff: {result.DiffPath}";
    }

    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    static string Escape(string? s) =>
        s is null ? "" : s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ⏎ ");
}
