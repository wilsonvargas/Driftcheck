using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Driftcheck.Mcp.Devices;
using SkiaSharp;

namespace Driftcheck.Mcp.Ios;

/// <summary>
/// Simuladores de iOS (solo en macOS). La captura sale de <c>xcrun simctl io screenshot</c> y los elementos,
/// de un UI test (tools/ios-accessibility-helper) que se adjunta a la app en primer plano y vuelca su árbol
/// de accesibilidad: XCUITest es lo único de Xcode que lo expone, sin instalar idb ni WebDriverAgent.
/// Las coordenadas vienen en puntos, que equivalen a las unidades de Figma.
/// </summary>
public sealed partial class SimulatorClient(Settings settings) : IDeviceClient
{
    const string HelperFolder = "ios-accessibility-helper";
    const string HelperProject = "DriftcheckAccessibility.xcodeproj";
    const string HelperScheme = "DriftcheckAccessibility";

    // Límites para que un simulador trabado (o un xcodebuild esperando algo que nunca llega) no deje la
    // llamada MCP colgada. Compilar el helper la primera vez tarda unos 15 s y correrlo unos 3 s.
    static readonly TimeSpan SimctlTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan DumpTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Que dos capturas a la vez no compilen el helper dos veces.</summary>
    static readonly SemaphoreSlim BuildLock = new(1, 1);

    public DevicePlatform Platform => DevicePlatform.Ios;

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return [];

        var json = await RunTextAsync("xcrun", ["simctl", "list", "devices", "booted", "--json"], null, SimctlTimeout, ct);
        return ParseDevices(json);
    }

    /// <summary>
    /// Convierte la salida de <c>simctl list devices --json</c> en dispositivos. Si el formato no es el
    /// esperado (otra versión de Xcode, salida cortada), lanza InvalidOperationException para que
    /// DeviceRegistry lo informe como error de iOS sin impedir usar Android.
    /// </summary>
    public static IReadOnlyList<DeviceInfo> ParseDevices(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new List<DeviceInfo>();
            foreach (var runtime in doc.RootElement.GetProperty("devices").EnumerateObject())
            {
                // "com.apple.CoreSimulator.SimRuntime.iOS-26-3" → "iOS 26.3"
                var os = RuntimeRegex().Match(runtime.Name) is { Success: true } m
                    ? $"{m.Groups[1].Value} {m.Groups[2].Value.Replace('-', '.')}"
                    : runtime.Name;
                foreach (var device in runtime.Value.EnumerateArray())
                {
                    if (device.GetProperty("state").GetString() != "Booted") continue;
                    result.Add(new DeviceInfo(device.GetProperty("udid").GetString()!,
                        $"Simulador de iOS, {device.GetProperty("name").GetString()} ({os})"));
                }
            }
            return result;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"No entiendo la lista de simuladores de xcrun simctl: {e.Message}", e);
        }
    }

    public async Task<ScreenCapture> CaptureAsync(string udid, CancellationToken ct = default)
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        var dump = await DumpAccessibilityAsync(udid, ct);
        using var root = JsonDocument.Parse(dump);
        if (root.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString());

        // La captura va después del árbol: leerlo tarda unos segundos y, si la app recién se abrió, una
        // captura tomada antes puede mostrar todavía la pantalla de inicio.
        var screenshotPath = Path.Combine(settings.OutputDirectory, $"device_{udid}.png");
        await RunTextAsync("xcrun", ["simctl", "io", udid, "screenshot", "--type=png", screenshotPath], null, SimctlTimeout, ct);

        using var codec = SKCodec.Create(screenshotPath)
            ?? throw new InvalidOperationException("No pude leer la captura del simulador.");
        var screen = ReadRect(root.RootElement.GetProperty("frame"));
        var pixelsPerPoint = codec.Info.Width / screen.Width;

        return new ScreenCapture(udid, screenshotPath, codec.Info.Width, codec.Info.Height, pixelsPerPoint,
            ParseElements(root.RootElement, screen), DevicePlatform.Ios);
    }

    /// <summary>
    /// Convierte el JSON del helper en elementos. Como en Android, quedan solo los que tienen AutomationId
    /// (accessibilityIdentifier) o texto, y que se ven en pantalla.
    /// </summary>
    public static IReadOnlyList<ScreenElement> ParseElements(JsonElement dump, Rect screen)
    {
        var result = new List<ScreenElement>();
        foreach (var e in dump.GetProperty("elements").EnumerateArray())
        {
            var type = e.GetProperty("type").GetString() ?? "";
            if (type is "Application" or "Window" or "Keyboard") continue;

            var bounds = ReadRect(e.GetProperty("frame"));
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            if (bounds.X >= screen.Width || bounds.Y >= screen.Height || bounds.X + bounds.Width <= 0 || bounds.Y + bounds.Height <= 0)
                continue;

            var id = Get(e, "identifier");
            var label = Get(e, "label");

            // El texto visible: en un campo es lo escrito, o el placeholder si está vacío (igual que el hint
            // de un EditText en uiautomator); en etiquetas y botones, la etiqueta de accesibilidad.
            string? text, description = null;
            if (type is "TextField" or "SecureTextField" or "TextView" or "SearchField")
            {
                text = Get(e, "value") ?? Get(e, "placeholder");
                description = label;
            }
            else if (type is "StaticText" or "Button" or "Link")
                text = label;
            else
            {
                text = null;
                description = label;
            }

            if (id is null && text is null && description is null) continue;
            result.Add(new ScreenElement(id, type, text, description, bounds));
        }
        return result;
    }

    async Task<string> DumpAccessibilityAsync(string udid, CancellationToken ct)
    {
        var xctestrun = await EnsureHelperBuiltAsync(ct);

        // launchctl muestra las apps que están corriendo como "UIKitApplication:{bundle id}[…]"; el helper
        // usa la que está en primer plano.
        var services = await RunTextAsync("xcrun", ["simctl", "spawn", udid, "launchctl", "list"], null, SimctlTimeout, ct);
        var bundleIds = AppServiceRegex().Matches(services).Select(m => m.Groups[1].Value).Distinct();

        var output = Path.Combine(settings.OutputDirectory, $"accessibility_{udid}.json");
        File.Delete(output);

        // xcodebuild le pasa al test las variables con prefijo TEST_RUNNER_, sin el prefijo.
        await RunTextAsync("xcodebuild",
            ["test-without-building", "-xctestrun", xctestrun, "-destination", $"id={udid}"],
            new Dictionary<string, string>
            {
                ["TEST_RUNNER_DRIFTCHECK_OUTPUT"] = output,
                ["TEST_RUNNER_DRIFTCHECK_BUNDLE_IDS"] = string.Join(',', bundleIds),
            }, DumpTimeout, ct);

        if (!File.Exists(output))
            throw new InvalidOperationException("El helper de accesibilidad de iOS terminó sin escribir el árbol de la app.");
        return await File.ReadAllTextAsync(output, ct);
    }

    /// <summary>
    /// Compila el helper la primera vez (unos segundos) en la carpeta de salida. La carpeta lleva un hash
    /// de las fuentes, así que un cambio en el helper lo vuelve a compilar.
    /// </summary>
    async Task<string> EnsureHelperBuiltAsync(CancellationToken ct)
    {
        var source = Path.Combine(AppContext.BaseDirectory, HelperFolder);
        if (!Directory.Exists(source))
            throw new InvalidOperationException($"No encuentro el helper de accesibilidad de iOS en {source}. Vuelve a compilar Driftcheck.Mcp.");

        var derivedData = Path.Combine(settings.OutputDirectory, "ios-helper", HashSources(source));
        var products = Path.Combine(derivedData, "Build", "Products");

        await BuildLock.WaitAsync(ct);
        try
        {
            if (FindXctestrun(products) is { } existing) return existing;

            await RunTextAsync("xcodebuild",
                ["build-for-testing", "-project", Path.Combine(source, HelperProject), "-scheme", HelperScheme,
                 "-destination", "generic/platform=iOS Simulator", "-derivedDataPath", derivedData, "-quiet"], null, BuildTimeout, ct);

            return FindXctestrun(products)
                ?? throw new InvalidOperationException($"xcodebuild no generó el .xctestrun del helper en {products}.");
        }
        finally
        {
            BuildLock.Release();
        }
    }

    static string? FindXctestrun(string products) =>
        Directory.Exists(products) ? Directory.EnumerateFiles(products, "*.xctestrun").FirstOrDefault() : null;

    static string HashSources(string dir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, file)));
            sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }

    static Rect ReadRect(JsonElement r) => new(
        r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(),
        r.GetProperty("width").GetDouble(), r.GetProperty("height").GetDouble());

    static string? Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// Ejecuta un proceso y devuelve su salida. Si pasa <paramref name="timeout"/> o se cancela, lo mata
    /// (con sus hijos: xcodebuild arranca el runner del test) para no dejarlo corriendo.
    /// </summary>
    public static async Task<string> RunTextAsync(string fileName, string[] args, IDictionary<string, string>? env,
        TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"No se pudo ejecutar {fileName}");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(limit.Token));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // ya terminó
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args.Take(2))} no terminó en {timeout.TotalSeconds:0} s. " +
                "Revisa que el simulador responda (que no esté bloqueado ni mostrando un diálogo) y vuelve a intentarlo.");
        }

        if (process.ExitCode != 0)
        {
            // xcodebuild escribe mucho; para el agente bastan las últimas líneas con el error.
            var lines = (await stderr + "\n" + await stdout)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.Contains("DVTDeviceOperation") && !l.Contains("IDERunDestination"))
                .TakeLast(15);
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args.Take(2))} falló ({process.ExitCode}):\n{string.Join('\n', lines)}");
        }

        return await stdout;
    }

    [GeneratedRegex(@"SimRuntime\.(\w+)-([\d-]+)$")]
    private static partial Regex RuntimeRegex();

    [GeneratedRegex(@"UIKitApplication:([^\[\s]+)\[")]
    private static partial Regex AppServiceRegex();
}
