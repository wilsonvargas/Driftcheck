namespace FigmaCompare.Mcp;

public sealed class Settings
{
    public string? FigmaToken => ReadEnv("FIGMA_TOKEN");

    public string OutputDirectory { get; } =
        Environment.GetEnvironmentVariable("FIGMA_COMPARE_OUTPUT")
        ?? Path.Combine(Path.GetTempPath(), "figma-compare");

    public string AdbPath { get; } = ResolveAdb();

    // En Windows, una variable creada después de abrir el cliente MCP no llega al proceso;
    // se lee del registro del usuario para no tener que reiniciar. Se evalúa en cada llamada.
    static string? ReadEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value) && OperatingSystem.IsWindows())
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        return value;
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
