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
    /// <summary>
    /// Si la captura parece una pantalla de carga se vuelve a tomar: el splash de .NET dura unos segundos
    /// después de abrir la app, más con el emulador recién arrancado.
    /// </summary>
    const int MaxCaptureAttempts = 3;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2.5);

    [McpServerTool(Name = "import_design"), Description(
        "Imports a .driftcheck.json file exported with the Driftcheck plugin in Figma, to compare without the Figma API " +
        "(no token or no quota). Exports in the Downloads folder are picked up on their own; this is for the ones that " +
        "ended up in another folder. Then compare with the frame link, as usual.")]
    public string ImportDesign(
        [Description("Path of the .driftcheck.json file")] string path)
    {
        var export = new DesignExports(settings).Import(path);
        var file = export.FileKey is null ? "" : $" from file {export.FileKey}";
        return $"Imported \"{export.NodeName}\" (node-id={export.NodeId.Replace(':', '-')}){file}, exported on " +
               $"{export.ExportedAtUtc:yyyy-MM-dd HH:mm} UTC. Use get_design_specs or compare_screen with that " +
               "frame's link (Copy link to selection in Figma).";
    }

    [McpServerTool(Name = "list_devices"), Description(
        "Lists the Android devices and emulators connected through adb and, on macOS, the booted iOS simulators.")]
    public async Task<string> ListDevices(CancellationToken ct)
    {
        var listing = await devices.ListAsync(ct);
        var sb = new StringBuilder();
        if (listing.Devices.Count == 0)
            sb.AppendLine("No devices connected. Start an Android emulator or an iOS simulator, or connect a phone with USB debugging.");
        foreach (var (_, d) in listing.Devices)
            sb.AppendLine($"{d.Serial} — {d.Description}");
        foreach (var error in listing.Errors)
            sb.AppendLine($"(Could not query {error})");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_design_specs"), Description(
        "Returns the measurements of the Figma frame: position, size, colors, texts and typography of every named layer. " +
        "Use it BEFORE writing a screen's UI code. If deviceWidth is passed, values are scaled to that width in dp or pt.")]
    public async Task<string> GetDesignSpecs(
        [Description("Figma frame URL (Copy link to selection), must include ?node-id=")] string figmaUrl,
        [Description("Logical width of the target device in dp or pt (e.g. 412 for Pixel 7, 402 for iPhone 17). Optional.")] double? deviceWidth = null,
        [Description("true to download the Figma frame again instead of using the local cache. Use it only if the design changed: each download uses Figma API quota.")] bool refresh = false,
        CancellationToken ct = default)
    {
        var frame = await figma.GetFrameAsync(figmaUrl, refresh, ct);
        var scale = deviceWidth is > 0 ? deviceWidth.Value / frame.Bounds.Width : 1;

        var sb = new StringBuilder();
        sb.AppendLine($"# {frame.Name} ({frame.Bounds.Width:0.#}×{frame.Bounds.Height:0.#}, scale {scale:0.###})");
        sb.AppendLine();
        if (frame.CachedAt is { } cachedAt)
            sb.AppendLine($"Design taken from the local cache (downloaded on {cachedAt:yyyy-MM-dd HH:mm}). Use refresh=true if it changed in Figma.");
        sb.AppendLine("compare_screen matches texts by visible text and finds non-text layers (buttons, fields, lines, " +
                      "icons, images) by how they look; it also matches by layer name = AutomationId.");
        sb.AppendLine();
        sb.AppendLine("| Layer | Type | x | y | width | height | background | radius | text | font |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var e in frame.Elements)
        {
            var b = e.Bounds.Scale(scale);
            var font = e.FontSize is { } fs
                ? $"{e.FontFamily} {F(fs * scale)} w{e.FontWeight:0}"
                : "";
            sb.AppendLine($"| {e.Name} | {e.Type} | {F(b.X)} | {F(b.Y)} | {F(b.Width)} | {F(b.Height)} | {e.Fill?.Hex} | {(e.CornerRadius is { } r ? F(r * scale) : "")} | {Escape(e.Text)} | {font} |");
        }

        if (frame.Shapes is { Count: > 0 } shapes)
        {
            sb.AppendLine();
            sb.AppendLine("## Visible non-text layers");
            sb.AppendLine();
            sb.AppendLine("| Layer | Kind | x | y | width | height | background | border | radius |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var e in shapes)
            {
                var b = e.Bounds.Scale(scale);
                var kind = e.Shape switch { ShapeKind.Icon => e.MultiColor ? "multicolor icon" : "icon", ShapeKind.Image => "image", _ => "shape" };
                sb.AppendLine($"| {Escape(e.Name)} | {kind} | {F(b.X)} | {F(b.Y)} | {F(b.Width)} | {F(b.Height)} | {e.Fill?.Hex} | {e.Stroke?.Hex} | {(e.CornerRadius is { } r ? F(r * scale) : "")} |");
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "capture_screen"), Description(
        "Takes a screenshot of the device (Android or iOS Simulator) and lists the visible elements with their AutomationId " +
        "(resource-id on Android, accessibilityIdentifier on iOS), text and bounds in dp or pt. On iOS it takes a few more seconds: " +
        "it reads the accessibility tree with a UI test, which is built the first time.")]
    public async Task<string> CaptureScreen(
        [Description("Device serial or UDID (from list_devices). Optional: uses the first one.")] string? deviceSerial = null,
        CancellationToken ct = default)
    {
        var capture = await devices.CaptureAsync(deviceSerial, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Screenshot: {capture.ScreenshotPath}");
        sb.AppendLine($"Device: {capture.Serial}, {capture.WidthPx}×{capture.HeightPx} px, {capture.PixelsPerUnit:0.##} px per {capture.Unit} ({capture.WidthUnits:0.#} {capture.Unit} wide)");
        if (capture.App is { } app) sb.AppendLine($"App: {app}");
        sb.AppendLine();
        sb.AppendLine("| AutomationId | Class | Text | x | y | width | height |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var e in capture.Elements)
            sb.AppendLine($"| {e.Id} | {e.ClassName.Split('.').Last()} | {Escape(e.Text ?? e.ContentDescription)} | {F(e.Bounds.X)} | {F(e.Bounds.Y)} | {F(e.Bounds.Width)} | {F(e.Bounds.Height)} |");

        return sb.ToString();
    }

    [McpServerTool(Name = "compare_screen"), Description(
        "Compares the app's current screen on the device (Android or iOS Simulator) with a Figma frame. " +
        "Matches Figma layers to app elements by visible text, name = AutomationId or position, and reports differences in position, size, " +
        "text and background color. Non-text layers (buttons, fields, lines, icons, images) are found in the screenshot by " +
        "how they look, comparing box, fill, corner radius, border and content. Reports fidelity (% of elements as in Figma, and how much it changed since the previous comparison) " +
        "and how to fix each difference with the framework's properties. Generates report.html (interactive report to " +
        "show the user), side_by_side.png and diff.png (in diff.png: green = matches, orange = differs, " +
        "dotted blue = where Figma says it should be). Open the images for a visual review. " +
        "If the screenshot doesn't look like the design (a loading screen), it waits and captures again up to 3 times.")]
    public async Task<string> CompareScreen(
        [Description("Figma frame URL (Copy link to selection), must include ?node-id=")] string figmaUrl,
        [Description("Device serial or UDID. Optional: uses the first one.")] string? deviceSerial = null,
        [Description("Position/size tolerance in dp or pt. Default 2.")] double tolerance = 2,
        [Description("Color tolerance (RGB distance 0–441). Default 24.")] double colorTolerance = 24,
        [Description("true to download the Figma frame again instead of using the local cache. Use it only if the design changed: each download uses Figma API quota.")] bool refresh = false,
        [Description("App framework, to write the suggestions with its properties: maui, android (XML views), compose, uikit, swiftui or flutter. Optional.")] string? framework = null,
        [Description("App project folder. Optional: if it has files newer than the installed app, the report warns that the app needs to be rebuilt and reinstalled.")] string? projectPath = null,
        CancellationToken ct = default)
    {
        var fw = FixSuggester.ParseFramework(framework);
        if (projectPath is not null && !Directory.Exists(projectPath))
            throw new ArgumentException($"Project folder not found: {projectPath}");

        var frameTask = figma.GetFrameAsync(figmaUrl, refresh, ct);
        var pngTask = figma.RenderPngAsync(figmaUrl, 2, refresh, ct);
        var captureTask = devices.CaptureAsync(deviceSerial, ct);
        await Task.WhenAll(frameTask, pngTask, captureTask);
        var (design, designPng, capture) = (await frameTask, await pngTask, await captureTask);

        ComparisonResult result;
        var retries = 0;
        while (true)
        {
            List<string> warnings = [];
            if (projectPath is not null && ProjectFreshness.Check(projectPath, capture.App) is { } stale) warnings.Add(stale);

            result = comparer.Compare(design, designPng, capture, tolerance, colorTolerance, fw, warnings);
            if (!result.LooksLikeLoadingScreen || retries == MaxCaptureAttempts - 1) break;

            // Se borra para que la próxima comparación no la tome como la anterior.
            ScreenComparer.Discard(result);
            await Task.Delay(RetryDelay, ct);
            capture = await devices.CaptureAsync(capture.Serial, ct);
            retries++;
        }

        var retryNote = retries switch
        {
            0 => "",
            1 => "(The first screenshot didn't look like the design, so Driftcheck waited and captured again.)\n\n",
            _ => $"(The first {retries} screenshots didn't look like the design, so Driftcheck waited and captured again.)\n\n",
        };
        return retryNote + result.Report +
               $"\n\nreport.html: {result.HtmlReportPath}\nside_by_side: {result.SideBySidePath}\ndiff: {result.DiffPath}";
    }

    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    static string Escape(string? s) =>
        s is null ? "" : s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ⏎ ");
}
