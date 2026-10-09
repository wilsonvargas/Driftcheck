namespace Driftcheck.Mcp.Devices;

public enum DevicePlatform { Android, Ios }

/// <summary>Elemento de la pantalla del dispositivo, en unidades lógicas (dp en Android, pt en iOS).</summary>
/// <param name="Id">AutomationId: el resource-id en Android, el accessibilityIdentifier en iOS.</param>
public sealed record ScreenElement(string? Id, string ClassName, string? Text, string? ContentDescription, Rect Bounds);

/// <param name="PixelsPerUnit">Píxeles de la captura por unidad lógica: densidad / 160 en Android, la escala de la pantalla (2 o 3) en iOS.</param>
public sealed record ScreenCapture(
    string Serial,
    string ScreenshotPath,
    int WidthPx,
    int HeightPx,
    double PixelsPerUnit,
    IReadOnlyList<ScreenElement> Elements,
    DevicePlatform Platform = DevicePlatform.Android)
{
    /// <summary>Factor px → unidad lógica.</summary>
    public double PxToUnit => 1 / PixelsPerUnit;
    public double WidthUnits => WidthPx * PxToUnit;

    /// <summary>Nombre de la unidad lógica para los reportes. Las dos equivalen a una unidad de Figma.</summary>
    public string Unit => Platform == DevicePlatform.Ios ? "pt" : "dp";
}

public sealed record DeviceInfo(string Serial, string Description);

/// <summary>Captura la pantalla y los elementos de un tipo de dispositivo.</summary>
public interface IDeviceClient
{
    DevicePlatform Platform { get; }

    /// <summary>Dispositivos disponibles. Lanza una excepción si la herramienta de la plataforma no está instalada.</summary>
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default);

    Task<ScreenCapture> CaptureAsync(string serial, CancellationToken ct = default);
}

/// <summary>Reúne los dispositivos de todas las plataformas y elige el cliente que corresponde a cada uno.</summary>
public sealed class DeviceRegistry(IEnumerable<IDeviceClient> clients)
{
    public sealed record Listing(IReadOnlyList<(IDeviceClient Client, DeviceInfo Device)> Devices, IReadOnlyList<string> Errors);

    public async Task<Listing> ListAsync(CancellationToken ct = default)
    {
        var devices = new List<(IDeviceClient, DeviceInfo)>();
        var errors = new List<string>();
        foreach (var client in clients)
        {
            // Una Mac sin el SDK de Android (o un adb que no responde) no debe impedir usar el simulador, y al revés.
            try
            {
                devices.AddRange((await client.ListDevicesAsync(ct)).Select(d => (client, d)));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                errors.Add($"{client.Platform}: {e.Message}");
            }
        }
        return new Listing(devices, errors);
    }

    /// <summary>Captura el dispositivo con ese serial (o UDID), o el primero que haya si es null.</summary>
    public async Task<ScreenCapture> CaptureAsync(string? serial, CancellationToken ct = default)
    {
        var listing = await ListAsync(ct);
        var found = serial is null
            ? listing.Devices.FirstOrDefault()
            : listing.Devices.FirstOrDefault(d => string.Equals(d.Device.Serial, serial, StringComparison.OrdinalIgnoreCase));

        if (found.Client is null)
        {
            var reason = serial is null
                ? "No hay dispositivos conectados. Inicia un emulador de Android o un simulador de iOS, o conecta un teléfono."
                : $"No encuentro el dispositivo {serial}. Usa list_devices para ver los disponibles.";
            if (listing.Errors.Count > 0) reason += " " + string.Join(" ", listing.Errors);
            throw new InvalidOperationException(reason);
        }

        return await found.Client.CaptureAsync(found.Device.Serial, ct);
    }
}
