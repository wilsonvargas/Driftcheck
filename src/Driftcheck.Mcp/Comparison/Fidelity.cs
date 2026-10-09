using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Driftcheck.Mcp.Comparison;

/// <summary>
/// Qué tanto se parece la pantalla al diseño: elementos sin diferencias sobre los que se pudieron comparar
/// (los emparejados más los textos del diseño que faltan en la app). Es una cuenta que se puede explicar
/// en una línea, para seguir el avance entre una corrección y la siguiente.
/// </summary>
/// <param name="Previous">Fidelidad de la comparación anterior del mismo frame en el mismo dispositivo.</param>
public sealed record FidelityScore(int Correct, int Total, int? Previous = null)
{
    /// <summary>Redondeado hacia abajo: 100 % solo si todo coincide.</summary>
    public int Percent => Total == 0 ? 0 : 100 * Correct / Total;

    public int? Change => Previous is { } p ? Percent - p : null;
}

/// <summary>
/// Resumen de cada comparación (result.json en su carpeta), para mostrar cuánto cambió la fidelidad
/// respecto a la anterior del mismo frame en el mismo dispositivo.
/// </summary>
public static partial class RunHistory
{
    public sealed record RunSummary(string FileKey, string NodeId, string Serial, int Percent, int Correct, int Total, int Diffs);

    const string FileName = "result.json";

    /// <summary>Solo se revisan las últimas comparaciones: más atrás ya no dice nada del avance.</summary>
    const int MaxRunsToScan = 200;

    public static void Save(string runDir, RunSummary summary) =>
        File.WriteAllText(Path.Combine(runDir, FileName), JsonSerializer.Serialize(summary));

    public static RunSummary? FindPrevious(string outputDir, string currentRunDir, string fileKey, string nodeId, string serial)
    {
        if (!Directory.Exists(outputDir)) return null;

        var current = Path.GetFileName(currentRunDir);
        var runs = Directory.EnumerateDirectories(outputDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && RunDirRegex().IsMatch(n) && string.CompareOrdinal(n, current) < 0)
            .OrderDescending(StringComparer.Ordinal)
            .Take(MaxRunsToScan);

        foreach (var run in runs)
        {
            var path = Path.Combine(outputDir, run!, FileName);
            if (!File.Exists(path)) continue;
            try
            {
                var summary = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(path));
                if (summary is not null && summary.FileKey == fileKey && summary.NodeId == nodeId &&
                    string.Equals(summary.Serial, serial, StringComparison.OrdinalIgnoreCase))
                    return summary;
            }
            catch (JsonException)
            {
                // Un result.json a medio escribir (o de otra versión) no debe impedir la comparación.
            }
        }
        return null;
    }

    /// <summary>Nombre de la carpeta de una comparación: la fecha y hora en que se hizo (con milisegundos, para que dos seguidas no se pisen).</summary>
    public static string NewRunDirName(DateTime now) => now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\d{8}_\d{6}(_\d{3})?$")]
    private static partial Regex RunDirRegex();
}
