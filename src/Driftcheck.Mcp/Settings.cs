namespace Driftcheck.Mcp;

public sealed class Settings
{
    public string? FigmaToken => ReadEnv("FIGMA_TOKEN");

    public string OutputDirectory { get; } =
        Environment.GetEnvironmentVariable("DRIFTCHECK_OUTPUT")
        ?? Path.Combine(Path.GetTempPath(), "driftcheck");

    public string AdbPath { get; } = ResolveAdb();

    /// <summary>
    /// Carpetas donde se buscan los archivos .driftcheck.json que exporta el plugin de Figma: Descargas (donde
    /// los deja el navegador o la app de Figma) y la carpeta "exports" de la salida (donde los copia
    /// import_design). DRIFTCHECK_EXPORTS las reemplaza (varias, separadas por ';' en Windows o ':').
    /// </summary>
    public IReadOnlyList<string> ExportDirectories =>
        Environment.GetEnvironmentVariable("DRIFTCHECK_EXPORTS") is { Length: > 0 } custom
            ? custom.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), ImportedExportsDirectory];

    /// <summary>Donde import_design copia los archivos exportados que el usuario tenía en otra carpeta.</summary>
    public string ImportedExportsDirectory => Path.Combine(OutputDirectory, "exports");

    // En Windows, una variable creada o cambiada después de abrir el cliente MCP no llega al proceso
    // (lo hereda con el valor viejo, por ejemplo un token vencido). Por eso manda la del registro
    // del usuario, y se evalúa en cada llamada para no tener que reiniciar.
    static string? ReadEnv(string name)
    {
        if (OperatingSystem.IsWindows()
            && Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is { Length: > 0 } user)
            return user;
        return Environment.GetEnvironmentVariable(name);
    }

    static string ResolveAdb()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
            @"C:\Program Files (x86)\Android\android-sdk",
            @"C:\Program Files\Android\android-sdk",
        };

        var exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        foreach (var sdk in candidates)
        {
            if (string.IsNullOrEmpty(sdk)) continue;
            var path = Path.Combine(sdk, "platform-tools", exe);
            if (File.Exists(path)) return path;
        }

        // Último recurso: que esté en el PATH.
        return exe;
    }
}
