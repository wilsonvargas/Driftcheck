namespace Driftcheck.Mcp;

/// <summary>Ícono de Driftcheck (el mismo de assets/icon.svg): un check verde y su copia magenta corrida.</summary>
public static class Branding
{
    public const string IconSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="15" fill="#14161F"/>""" +
        """<polyline points="12,29 24,41 47,17" fill="none" stroke="#E8457C" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/>""" +
        """<polyline points="17,34 29,46 52,22" fill="none" stroke="#34D399" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/></svg>""";

    public static readonly string IconDataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(IconSvg));

    /// <summary>assets/icon-128.png, embebido en el ensamblado.</summary>
    public static readonly string IconPngDataUri = "data:image/png;base64," + Convert.ToBase64String(ReadResource("Driftcheck.Mcp.icon-128.png"));

    static byte[] ReadResource(string name)
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The {name} resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
