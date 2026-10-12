using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Driftcheck.Mcp.Devices;

namespace Driftcheck.Mcp.Android;

public sealed partial class AdbClient(Settings settings) : IDeviceClient
{
    public DevicePlatform Platform => DevicePlatform.Android;

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var output = await RunTextAsync(null, ["devices"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length == 2 && p[1] == "device")
            .Select(p => new DeviceInfo(p[0], "Android"))
            .ToList();
    }

    public async Task<ScreenCapture> CaptureAsync(string serial, CancellationToken ct = default)
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        var screenshotPath = Path.Combine(settings.OutputDirectory, $"device_{Sanitize(serial)}.png");
        var png = await RunBytesAsync(serial, ["exec-out", "screencap", "-p"], ct);
        await File.WriteAllBytesAsync(screenshotPath, png, ct);

        var density = await GetDensityAsync(serial, ct);
        var (w, h) = await GetSizeAsync(serial, ct);

        await RunTextAsync(serial, ["shell", "uiautomator", "dump", "/sdcard/window_dump.xml"], ct);
        var xml = await RunTextAsync(serial, ["exec-out", "cat", "/sdcard/window_dump.xml"], ct);

        var app = ParseForegroundPackage(xml) is { } package ? await GetAppInfoAsync(serial, package, ct) : null;
        return new ScreenCapture(serial, screenshotPath, w, h, density / 160, ParseHierarchy(xml, 160.0 / density),
            DevicePlatform.Android, app);
    }

    /// <summary>
    /// Paquete de la app en primer plano: el de los nodos del árbol de uiautomator que no son de la barra
    /// del sistema. Null si no hay ninguno.
    /// </summary>
    public static string? ParseForegroundPackage(string xml)
    {
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (start < 0) return null;

        return XDocument.Parse(xml[start..]).Descendants("node")
            .Select(n => (string?)n.Attribute("package"))
            .Where(p => !string.IsNullOrEmpty(p) && p != "com.android.systemui")
            .GroupBy(p => p)
            .MaxBy(g => g.Count())?.Key;
    }

    /// <summary>
    /// Versión y fecha de instalación de la app. Es solo para el reporte: si falla (un dispositivo raro, una
    /// app del sistema), la captura sigue sin esos datos.
    /// </summary>
    async Task<AppInfo?> GetAppInfoAsync(string serial, string package, CancellationToken ct)
    {
        if (!PackageRegex().IsMatch(package)) return null;

        // Todo en un solo "adb shell" para no sumar tiempo a cada captura. La fecha sale de stat (segundos
        // desde 1970), no de lastUpdateTime, que viene en la zona horaria del dispositivo. En MAUI Debug,
        // Fast Deployment copia los ensamblados a files/.__override__ sin reinstalar: run-as los lee si la
        // app es depurable, y si no, falla sin escribir nada.
        var script =
            $"dumpsys package {package} | grep -E 'versionName=|versionCode='; " +
            $"echo apk $(stat -c %Y $(pm path {package} | head -1 | cut -d: -f2)); " +
            $"echo override $(run-as {package} sh -c 'stat -c %Y files/.__override__/* files/.__override__/*/*' 2>/dev/null | sort -n | tail -1)";
        try
        {
            return ParseAppInfo(package, await RunTextAsync(serial, ["shell", script], ct));
        }
        catch (InvalidOperationException)
        {
            return new AppInfo(package, null, null);
        }
    }

    public static AppInfo ParseAppInfo(string package, string output)
    {
        var name = VersionNameRegex().Match(output) is { Success: true } n ? n.Groups[1].Value : null;
        var code = VersionCodeRegex().Match(output) is { Success: true } c ? c.Groups[1].Value : null;
        var version = (name, code) switch
        {
            (null, null) => null,
            (_, null) => name,
            (null, _) => $"({code})",
            _ => $"{name} ({code})",
        };

        var deployed = DeployTimeRegex().Matches(output)
            .Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty()
            .Max();

        return new AppInfo(package, version,
            deployed > 0 ? DateTimeOffset.FromUnixTimeSeconds(deployed).LocalDateTime : null);
    }

    /// <summary>
    /// MAUI publica el AutomationId como "{paquete de la app}:id/{AutomationId}". Los ids de otro paquete
    /// son de Android o de librerías ("android:id/content", "com.android.systemui:id/clock") y no
    /// representan nada del diseño: si se usaran, "Content" en Figma se emparejaría con la raíz de la ventana.
    /// </summary>
    public static string? ParseAutomationId(string? resourceId, string? package)
    {
        if (string.IsNullOrEmpty(resourceId)) return null;

        var idx = resourceId.IndexOf(":id/", StringComparison.Ordinal);
        if (idx < 0) return resourceId;

        var owner = resourceId[..idx];
        if (owner == "android" || owner.StartsWith("com.android.", StringComparison.Ordinal)) return null;
        if (!string.IsNullOrEmpty(package) && owner != package) return null;

        var id = resourceId[(idx + 4)..];
        return AppCompatDecorIds.Contains(id) ? null : id;
    }

    /// <summary>
    /// Ids del marco de ventana de AppCompat (layouts abc_screen_*). Los recursos de las librerías se
    /// fusionan en el paquete de la app, así que llegan como "{paquete}:id/action_bar_root".
    /// </summary>
    static readonly HashSet<string> AppCompatDecorIds =
    [
        "action_bar_root", "action_bar_container", "action_bar", "action_context_bar",
        "action_mode_bar_stub", "decor_content_parent", "split_action_bar",
    ];

    public static IReadOnlyList<ScreenElement> ParseHierarchy(string xml, double pxToDp)
    {
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"uiautomator no devolvió XML: {xml}");

        var doc = XDocument.Parse(xml[start..]);
        var result = new List<ScreenElement>();

        foreach (var node in doc.Descendants("node"))
        {
            var bounds = BoundsRegex().Match((string?)node.Attribute("bounds") ?? "");
            if (!bounds.Success) continue;

            int x1 = int.Parse(bounds.Groups[1].Value), y1 = int.Parse(bounds.Groups[2].Value);
            int x2 = int.Parse(bounds.Groups[3].Value), y2 = int.Parse(bounds.Groups[4].Value);
            if (x2 <= x1 || y2 <= y1) continue;

            var resourceId = (string?)node.Attribute("resource-id");
            var text = (string?)node.Attribute("text");
            var desc = (string?)node.Attribute("content-desc");

            var id = ParseAutomationId(resourceId, (string?)node.Attribute("package"));

            if (id is null && string.IsNullOrEmpty(text) && string.IsNullOrEmpty(desc))
                continue;

            result.Add(new ScreenElement(
                id,
                (string?)node.Attribute("class") ?? "",
                NullIfEmpty(text),
                NullIfEmpty(desc),
                new Rect(x1, y1, x2 - x1, y2 - y1).Scale(pxToDp)));
        }

        return result;
    }

    async Task<double> GetDensityAsync(string serial, CancellationToken ct)
    {
        // "Physical density: 420" y, si se cambió, "Override density: 480".
        var output = await RunTextAsync(serial, ["shell", "wm", "density"], ct);
        var matches = DensityRegex().Matches(output);
        if (matches.Count == 0) throw new InvalidOperationException($"No pude leer la densidad: {output}");
        return double.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    async Task<(int, int)> GetSizeAsync(string serial, CancellationToken ct)
    {
        var output = await RunTextAsync(serial, ["shell", "wm", "size"], ct);
        var matches = SizeRegex().Matches(output);
        if (matches.Count == 0) throw new InvalidOperationException($"No pude leer el tamaño: {output}");
        var m = matches[^1];
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    async Task<string> RunTextAsync(string? serial, string[] args, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await RunBytesAsync(serial, args, ct));

    async Task<byte[]> RunBytesAsync(string? serial, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(settings.AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (serial is not null)
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"No se pudo ejecutar {settings.AdbPath}");

        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, stderr, process.WaitForExitAsync(ct));

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"adb {string.Join(' ', args)} falló ({process.ExitCode}): {await stderr}");

        return stdout.ToArray();
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    [GeneratedRegex(@"\[(\d+),(\d+)\]\[(\d+),(\d+)\]")]
    private static partial Regex BoundsRegex();

    [GeneratedRegex(@"density:\s*(\d+)")]
    private static partial Regex DensityRegex();

    [GeneratedRegex(@"size:\s*(\d+)x(\d+)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"versionName=(\S+)")]
    private static partial Regex VersionNameRegex();

    [GeneratedRegex(@"versionCode=(\d+)")]
    private static partial Regex VersionCodeRegex();

    [GeneratedRegex(@"^(?:apk|override) (\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex DeployTimeRegex();
}
