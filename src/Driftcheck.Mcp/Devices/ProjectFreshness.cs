namespace Driftcheck.Mcp.Devices;

/// <summary>
/// Avisa si la app instalada es más vieja que los últimos cambios del proyecto: comparar contra una versión
/// anterior hace que el agente "corrija" lo que ya estaba corregido.
/// </summary>
public static class ProjectFreshness
{
    /// <summary>Archivos que cambian lo que se ve en pantalla.</summary>
    static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".java", ".kt", ".xml", ".axml", ".gradle", ".kts",
        ".swift", ".m", ".storyboard", ".xib", ".plist", ".dart", ".yaml",
        ".png", ".svg", ".ttf", ".otf",
    };

    /// <summary>Salidas de compilación, dependencias y carpetas de los IDE.</summary>
    static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "build", ".build", ".git", ".vs", ".vscode", ".idea", ".gradle", "node_modules",
        "DerivedData", "Pods", ".dart_tool", "xcuserdata", ".claude",
    };

    /// <summary>
    /// Margen para el redondeo de las fechas y el reloj del dispositivo: un archivo guardado unos segundos
    /// después de compilar casi siempre es el mismo cambio.
    /// </summary>
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>El aviso para el reporte, o null si la app está al día.</summary>
    public static string? Check(string projectPath, AppInfo? app)
    {
        if (!Directory.Exists(projectPath))
            throw new ArgumentException($"Project folder not found: {projectPath}");

        if (app?.DeployedAt is not { } deployed)
            return $"Could not read when the app{(app is null ? "" : $" ({app.Id})")} was installed, so it's unknown whether it has " +
                   "the project's latest changes.";

        if (FindNewestSource(projectPath) is not { } newest || newest.Time <= deployed + Grace) return null;

        var file = Path.GetRelativePath(projectPath, newest.Path);
        return $"The installed app is from {deployed:yyyy-MM-dd HH:mm}, but `{file}` changed on {newest.Time:yyyy-MM-dd HH:mm}: " +
               "build and install the app before comparing, or this comparison is of the previous version.";
    }

    public static (string Path, DateTime Time)? FindNewestSource(string root)
    {
        (string, DateTime)? newest = null;
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var dir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (!SourceExtensions.Contains(Path.GetExtension(file))) continue;
                    var time = File.GetLastWriteTime(file);
                    if (newest is null || time > newest.Value.Item2) newest = (file, time);
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!IgnoredFolders.Contains(Path.GetFileName(sub))) pending.Push(sub);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Una carpeta sin permiso no debe impedir la comparación.
            }
        }
        return newest;
    }
}
