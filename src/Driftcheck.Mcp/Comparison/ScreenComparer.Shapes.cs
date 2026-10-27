using System.Text.RegularExpressions;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Mcp.Comparison;

public sealed partial class ScreenComparer
{
    /// <summary>
    /// Fracción de la grilla de un ícono o una imagen que puede cambiar sin reportarlo: el antialiasing y el
    /// cambio de densidad (2x en el diseño, 2.625x en un Pixel 7) mueven los bordes de los trazos.
    /// </summary>
    const double ImageDifference = 0.2;

    /// <summary>Franjas del frame donde los diseños dibujan el teléfono: la barra de estado y el indicador de inicio.</summary>
    const double StatusBarHeight = 44, HomeIndicatorHeight = 34;

    /// <summary>Cuánto más allá de donde debería estar se busca una forma, en dp o pt.</summary>
    const double ShapeSearchMargin = 12;

    sealed record ShapeContext(
        PixelImage Design, SKBitmap DesignBitmap, double DesignPxPerUnit,
        PixelImage Screen, SKBitmap ScreenBitmap, double PxToUnit,
        double Scale, double Dx, double Dy, double ExtraWidth, double Tolerance, double ColorTolerance);

    /// <param name="DesignBox">Caja medida en el PNG del diseño, en unidades del frame.</param>
    /// <param name="Actual">Caja medida en la captura; null si no está.</param>
    /// <param name="Background">Color alrededor en el diseño, para borrarla del diseño alineado.</param>
    sealed record ShapeCheck(
        DesignElement Design, string Label, Rect DesignBox, Rect Expected, Rect? Actual, Rgb Background,
        List<(string Property, string Expected, string Actual)> Diffs);

    sealed record ShapeComparison(List<ShapeCheck> Checks, int DeviceChrome, int NotMeasurable);

    /// <summary>
    /// Capas sin texto (botones, campos, líneas, íconos, imágenes): la app no las lista, así que se buscan en
    /// la captura por cómo se ven y se miden con el mismo algoritmo que en el PNG del diseño. Se compara la
    /// caja, el color de relleno, el radio de las esquinas y el borde; en íconos e imágenes, el contenido.
    /// </summary>
    static ShapeComparison CompareShapes(DesignFrame design, ShapeContext c)
    {
        var checks = new List<ShapeCheck>();
        var chrome = 0;
        var notMeasurable = 0;

        foreach (var shape in design.Shapes ?? [])
        {
            if (IsDeviceChrome(shape, design)) { chrome++; continue; }

            var solid = shape.Shape == ShapeKind.Shape;
            var border = shape.Stroke is not null;
            var designSample = ShapeProbe.Measure(c.Design, [shape.Bounds.Scale(c.DesignPxPerUnit)],
                ShapeSearchMargin * c.DesignPxPerUnit, solid, border);

            // Una forma que no se distingue de su fondo en el diseño (blanca sobre blanco, sin borde ni sombra)
            // tampoco se puede buscar en la app.
            if (designSample is null) { notMeasurable++; continue; }

            var designBox = designSample.Bounds.Scale(1 / c.DesignPxPerUnit);

            // Un contenedor y su hijo pueden pintar lo mismo (una instancia con fondo y su frame con el mismo fondo).
            if (checks.Any(k => ShapeProbe.Overlap(k.DesignBox, designBox) > 0.9)) continue;

            var at = designBox.Scale(c.Scale).Offset(c.Dx, c.Dy);
            // Un ícono o una imagen tienen tamaño fijo: no se estiran con el ancho del dispositivo.
            var options = HorizontalOptions(at, c.ExtraWidth, solid ? shape.WidthShare : null)
                .Where(o => solid || o.Width == at.Width).ToList();
            var appSample = ShapeProbe.Measure(c.Screen, options.Select(o => o.Scale(1 / c.PxToUnit)).ToList(),
                ShapeSearchMargin / c.PxToUnit, solid, border);

            // Si falta un ícono, lo más cercano suele ser otra cosa (la primera letra del texto que se corrió a
            // su lugar): con la mitad o el doble del área, no es el ícono.
            if (!solid && appSample is not null)
            {
                var ratio = appSample.Bounds.Width * appSample.Bounds.Height * c.PxToUnit * c.PxToUnit / (at.Width * at.Height);
                if (ratio is < 0.5 or > 2) appSample = null;
            }

            var label = ShapeLabel(shape, design.Elements);
            var diffs = new List<(string, string, string)>();
            if (appSample is null)
            {
                // Sin la app no se sabe cómo se ancla: se ubica como el PNG del diseño, centrado.
                checks.Add(new ShapeCheck(shape, label, designBox, at.Offset(c.ExtraWidth / 2, 0), null, designSample.Background, diffs));
                continue;
            }

            var actual = appSample.Bounds.Scale(c.PxToUnit);
            var expected = Closest(options, actual);
            void Check(string prop, double exp, double act, double allowed)
            {
                if (Math.Abs(exp - act) > allowed) diffs.Add((prop, F(exp), F(act)));
            }

            Check("x", expected.X, actual.X, c.Tolerance);
            Check("y", expected.Y, actual.Y, c.Tolerance);
            Check("width", expected.Width, actual.Width, c.Tolerance);
            Check("height", expected.Height, actual.Height, c.Tolerance);

            if (solid && shape.Fill is not null && appSample.Fill.DistanceTo(designSample.Fill) > c.ColorTolerance)
                diffs.Add(("background", designSample.Fill.Hex, appSample.Fill.Hex));

            if (designSample.CornerRadius is { } designRadius && appSample.CornerRadius is { } appRadius)
            {
                var exp = designRadius / c.DesignPxPerUnit * c.Scale;
                Check("corner radius", exp, appRadius * c.PxToUnit, Math.Max(c.Tolerance, exp * 0.2));
            }

            if (designSample.Border is { } designBorder)
            {
                if (appSample.Border is not { } appBorder) diffs.Add(("border", designBorder.Hex, "no border"));
                else if (appBorder.DistanceTo(designBorder) > c.ColorTolerance) diffs.Add(("border", designBorder.Hex, appBorder.Hex));
            }

            if (shape.Shape == ShapeKind.Icon && !shape.MultiColor && appSample.Fill.DistanceTo(designSample.Fill) > c.ColorTolerance)
                diffs.Add(("icon color", designSample.Fill.Hex, appSample.Fill.Hex));

            // El contenido solo se compara si la caja coincide: si no, la diferencia ya está reportada y el
            // recorte deformado siempre daría distinto.
            if (!solid && diffs.Count == 0)
            {
                var difference = ShapeProbe.Difference(c.DesignBitmap, designSample.Bounds, c.ScreenBitmap, appSample.Bounds);
                if (difference > ImageDifference) diffs.Add(("image", "as in Figma", $"{difference:P0} different"));
            }

            checks.Add(new ShapeCheck(shape, label, designBox, expected, actual, designSample.Background, diffs));
        }

        // Nombres repetidos ("Línea" ×2): se les agrega la posición para distinguirlos en el reporte.
        foreach (var group in checks.GroupBy(k => k.Label).Where(g => g.Count() > 1))
            foreach (var k in group.ToList())
                checks[checks.IndexOf(k)] = k with { Label = $"{k.Label} (x={F(k.DesignBox.X)}, y={F(k.DesignBox.Y)})" };

        return new ShapeComparison(checks, chrome, notMeasurable);
    }

    /// <summary>Fondo de la pantalla: el color más frecuente en el PNG del diseño y en la captura.</summary>
    static (Rgb Design, Rgb App)? ScreenBackground(DesignFrame design, ShapeContext c)
    {
        if (design.Background is null) return null;
        var designColor = SampleDominantColor(c.Design.Pixels, c.Design.Width, c.Design.Height,
            new Rect(0, 0, c.Design.Width, c.Design.Height), inset: 0);
        var appColor = SampleDominantColor(c.Screen.Pixels, c.Screen.Width, c.Screen.Height,
            new Rect(0, 0, c.Screen.Width, c.Screen.Height), inset: 0);
        return designColor is { } d && appColor is { } a ? (d, a) : null;
    }

    /// <summary>
    /// Una capa entera dentro de la barra de estado o del indicador de inicio que el diseño dibuja como
    /// maqueta (la batería, la señal, la barrita de abajo): en la app los pone el sistema.
    /// </summary>
    static bool IsDeviceChrome(DesignElement shape, DesignFrame design) =>
        design.Bounds.Height > design.Bounds.Width &&
        (shape.Bounds.Y + shape.Bounds.Height <= StatusBarHeight || shape.Bounds.Y >= design.Bounds.Height - HomeIndicatorHeight);

    /// <summary>
    /// Nombre para el reporte. Los nombres automáticos ("Frame", "Auto Layout Horizontal") y los de variantes
    /// ("Type=Primary, State=Disabled") no dicen qué es, así que se usa el tipo; si tiene un texto adentro
    /// (el de un botón o un campo), se agrega.
    /// </summary>
    static string ShapeLabel(DesignElement shape, IReadOnlyList<DesignElement> elements)
    {
        var b = shape.Bounds;
        var generic = shape.Name.Contains('=') || GenericShapeNameRegex().IsMatch(shape.Name.Trim());
        var name = !generic ? shape.Name : shape.Shape switch
        {
            ShapeKind.Icon => "Icon",
            ShapeKind.Image => "Image",
            _ when Math.Min(b.Width, b.Height) <= 2 => "Line",
            _ => "Shape",
        };

        var text = elements
            .Where(e => e.Text is not null && e.Bounds.X >= b.X && e.Bounds.Y >= b.Y &&
                        e.Bounds.X + e.Bounds.Width <= b.X + b.Width && e.Bounds.Y + e.Bounds.Height <= b.Y + b.Height)
            .OrderBy(e => e.Bounds.Y).ThenBy(e => e.Bounds.X)
            .Select(e => e.Text!.ReplaceLineEndings(" ").Trim())
            .FirstOrDefault(t => t.Length > 0);
        if (text is null) return name;
        if (text.Length > 40) text = text[..40] + "…";
        return $"{name} \"{text}\"";
    }

    [GeneratedRegex(@"^(Frame|Group|Rectangle|Ellipse|Vector|Line|Polygon|Star|Union|Subtract|Intersect|Exclude|Image|Component|Instance|Auto layout( (horizontal|vertical))?|Content|Container)( \d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericShapeNameRegex();
}
