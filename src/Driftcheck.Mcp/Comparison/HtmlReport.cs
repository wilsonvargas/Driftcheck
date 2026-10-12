using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Driftcheck.Mcp.Comparison;

/// <summary>
/// report.html: un solo archivo, con las imágenes adentro, para abrirlo o mandarlo sin la carpeta.
/// Tiene un deslizador diseño/app, transparencia, el diff y la lista de diferencias; al elegir una se
/// resalta en la imagen. La plantilla es ReportTemplate.html (recurso incrustado).
/// </summary>
public static class HtmlReport
{
    /// <param name="PixelsPerUnit">Para pasar las cajas (en dp o pt) a píxeles de las imágenes.</param>
    public sealed record Model(
        string Title, string Device, string? App, DateTime At, double PixelsPerUnit, int Width, int Height,
        FidelityScore Fidelity, double MismatchRatio, IReadOnlyList<string> Warnings, IReadOnlyList<ElementResult> Elements,
        byte[] DesignPng, byte[] AppPng, byte[] DiffPng);

    static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(HtmlReport).Assembly.GetManifestResourceStream("Driftcheck.Mcp.Comparison.ReportTemplate.html")
            ?? throw new InvalidOperationException("Falta la plantilla del reporte HTML.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static void Write(string path, Model m)
    {
        double[] Px(Rect r) => [r.X * m.PixelsPerUnit, r.Y * m.PixelsPerUnit, r.Width * m.PixelsPerUnit, r.Height * m.PixelsPerUnit];
        static string Png(byte[] bytes) => "data:image/png;base64," + Convert.ToBase64String(bytes);

        var data = new
        {
            title = m.Title,
            device = m.Device,
            app = m.App,
            warnings = m.Warnings,
            date = m.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            width = m.Width,
            height = m.Height,
            fidelity = new { percent = m.Fidelity.Percent, correct = m.Fidelity.Correct, total = m.Fidelity.Total, previous = m.Fidelity.Previous },
            mismatch = m.MismatchRatio,
            images = new { design = Png(m.DesignPng), app = Png(m.AppPng), diff = Png(m.DiffPng) },
            elements = m.Elements.Select(e => new
            {
                name = e.Name,
                id = e.AutomationId,
                status = e.Status.ToString().ToLowerInvariant(),
                expected = Px(e.Expected),
                actual = e.Actual is { } a ? Px(a) : null,
                diffs = e.Diffs.Select(d => new { property = d.Property, figma = d.Expected, app = d.Actual, fix = d.Fix }),
                fix = e.Fix,
            }),
        };

        // El serializador escapa < y > (<), así que un texto con "</script>" no rompe la página.
        var html = Template.Value
            .Replace("__TITLE__", WebUtility.HtmlEncode(m.Title))
            .Replace("__DATA__", JsonSerializer.Serialize(data));
        File.WriteAllText(path, html);
    }
}
