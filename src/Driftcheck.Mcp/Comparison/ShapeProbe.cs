using SkiaSharp;

namespace Driftcheck.Mcp.Comparison;

/// <summary>Píxeles de una imagen (el PNG del diseño o la captura).</summary>
public readonly record struct PixelImage(SKColor[] Pixels, int Width, int Height)
{
    public Rgb this[int x, int y] { get { var c = Pixels[y * Width + x]; return new Rgb(c.Red, c.Green, c.Blue); } }
}

/// <summary>
/// Mide una capa sin texto (botón, campo, línea, ícono, imagen) por cómo se ve: la app no la expone en el
/// árbol de accesibilidad, así que se busca en la imagen. Se usa igual en el PNG del diseño y en la captura,
/// y se comparan las dos medidas, como con las letras de los textos.
/// </summary>
public static class ShapeProbe
{
    /// <summary>Distancia al fondo desde la que un píxel es parte de algo (los PNG no tienen ruido de compresión).</summary>
    const double ForegroundContrast = 10;

    /// <summary>Solapamiento mínimo entre lo encontrado y donde se buscaba.</summary>
    const double MinOverlap = 0.3;

    /// <summary>Distancia mínima al relleno y al fondo para que un píxel del contorno cuente como borde.</summary>
    const double BorderContrast = 24;

    /// <param name="Bounds">Caja en px de la imagen. En una forma rellena es la del relleno: sin la sombra ni el borde.</param>
    /// <param name="Background">Color alrededor (el del contenedor).</param>
    /// <param name="Fill">En una forma, el color dominante adentro; en un ícono, el de sus trazos.</param>
    /// <param name="CornerRadius">Radio de las esquinas en px; null si la forma es muy chica para medirlo.</param>
    /// <param name="Border">Color del borde, si se pidió y tiene uno.</param>
    public sealed record Sample(Rect Bounds, Rgb Background, Rgb Fill, double? CornerRadius, Rgb? Border);

    /// <summary>
    /// Busca la forma cerca de <paramref name="seeds"/> (las cajas donde podría estar, en px): toma el fondo del
    /// marco alrededor, separa lo que no es fondo en manchas conectadas y se queda con la que más se solapa con
    /// alguna de las cajas. Null si no hay ninguna (falta en la app, o no se distingue del fondo).
    /// </summary>
    /// <param name="margin">Cuánto más allá de las cajas buscar, en px.</param>
    /// <param name="solid">Forma rellena (botón, campo, línea): la caja se ajusta al relleno y se mide el radio.</param>
    /// <param name="border">Buscar el color del borde.</param>
    public static Sample? Measure(PixelImage img, IReadOnlyList<Rect> seeds, double margin, bool solid, bool border)
    {
        var union = seeds.Aggregate(Union);
        if (DominantInRing(img, union.Inflate(margin / 4), union.Inflate(margin)) is not { } background) return null;

        var area = Clamp(img, union.Inflate(margin));
        if (area.Width <= 0 || area.Height <= 0) return null;

        // El solapamiento se mide con las cajas agrandadas: en una línea de 2 px, un corrimiento de 2 px lo dejaría en cero.
        var component = BestComponent(img, area, background, seeds, slack: margin / 6);
        if (component is null) return null;
        var (box, mask) = component.Value;

        if (!solid)
        {
            var ink = InkColor(img, box, mask, background);
            return new Sample(box.ToRect(), background, ink, null, null);
        }

        var fill = Dominant(img, box.Inset(0.2)) ?? Dominant(img, box)!.Value;

        // Se queda con los píxeles más cerca del relleno que del fondo: así no cuentan la sombra (más oscura
        // que el fondo) ni un borde de otro color, que en la app pueden faltar sin que la forma cambie de tamaño.
        // Si el relleno es igual al fondo (una tarjeta blanca con borde sobre blanco), la mancha es el borde.
        Func<int, int, bool> inside = fill.DistanceTo(background) < ForegroundContrast
            ? (x, y) => mask[(y - box.Y) * box.Width + (x - box.X)]
            : (x, y) => Projection(img[x, y], background, fill) > 0.5;

        var solidBox = Bounds(box, inside) ?? box;
        var borderColor = border ? BorderColor(img, solidBox, background, fill, (int)Math.Ceiling(margin / 3)) : null;

        // Con borde, la caja y el radio son los de afuera del borde: en una app sin el borde, el relleno llega
        // hasta ahí, y la diferencia es el borde, no el tamaño.
        if (borderColor is { } b && b.DistanceTo(background) >= ForegroundContrast)
        {
            var fillOnly = inside;
            inside = (x, y) => fillOnly(x, y) ||
                (mask[(y - box.Y) * box.Width + (x - box.X)] && Projection(img[x, y], background, b) > 0.5);
            solidBox = Bounds(box, inside) ?? solidBox;
        }

        double? radius = solidBox.Width >= 12 && solidBox.Height >= 12 ? CornerRadius(solidBox, inside) : null;
        return new Sample(solidBox.ToRect(), background, fill, radius, borderColor);
    }

    /// <summary>
    /// Qué tanto se parecen dos recortes (un ícono o una imagen) llevados a la misma grilla: la fracción de
    /// celdas de color distinto, de 0 a 1.
    /// </summary>
    public static double Difference(SKBitmap a, Rect boxA, SKBitmap b, Rect boxB)
    {
        const int Grid = 24;
        const double CellDistance = 60;

        using var cellsA = Resample(a, boxA, Grid);
        using var cellsB = Resample(b, boxB, Grid);
        var differing = 0;
        for (var y = 0; y < Grid; y++)
        for (var x = 0; x < Grid; x++)
        {
            var p = cellsA.GetPixel(x, y);
            var q = cellsB.GetPixel(x, y);
            if (new Rgb(p.Red, p.Green, p.Blue).DistanceTo(new Rgb(q.Red, q.Green, q.Blue)) > CellDistance) differing++;
        }
        return differing / (double)(Grid * Grid);
    }

    static SKBitmap Resample(SKBitmap source, Rect box, int size)
    {
        var output = new SKBitmap(size, size);
        using var canvas = new SKCanvas(output);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, SKRect.Create((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height),
            SKRect.Create(0, 0, size, size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return output;
    }

    /// <summary>Caja entera en px; Right y Bottom son exclusivos.</summary>
    readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public Rect ToRect() => new(X, Y, Width, Height);

        public Box Inset(double share)
        {
            int dx = (int)(Width * share), dy = (int)(Height * share);
            return new Box(X + dx, Y + dy, Math.Max(1, Width - 2 * dx), Math.Max(1, Height - 2 * dy));
        }
    }

    static Box Clamp(PixelImage img, Rect r)
    {
        int x1 = Math.Clamp((int)Math.Floor(r.X), 0, img.Width), x2 = Math.Clamp((int)Math.Ceiling(r.X + r.Width), 0, img.Width);
        int y1 = Math.Clamp((int)Math.Floor(r.Y), 0, img.Height), y2 = Math.Clamp((int)Math.Ceiling(r.Y + r.Height), 0, img.Height);
        return new Box(x1, y1, x2 - x1, y2 - y1);
    }

    static Rect Union(Rect a, Rect b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }

    /// <summary>
    /// Mancha conectada (8 vecinos) de píxeles distintos del fondo con más solapamiento con alguna de las cajas.
    /// Devuelve su caja y su máscara (del tamaño de la caja).
    /// </summary>
    static (Box Box, bool[] Mask)? BestComponent(PixelImage img, Box area, Rgb background, IReadOnlyList<Rect> seeds, double slack)
    {
        const int MinPixels = 3;
        var foreground = new bool[area.Width * area.Height];
        for (var y = 0; y < area.Height; y++)
        for (var x = 0; x < area.Width; x++)
            foreground[y * area.Width + x] = img[area.X + x, area.Y + y].DistanceTo(background) > ForegroundContrast;

        var label = new int[foreground.Length];
        var stack = new Stack<int>();
        (Box Box, int Label, double Overlap)? best = null;
        var next = 0;

        for (var start = 0; start < foreground.Length; start++)
        {
            if (!foreground[start] || label[start] != 0) continue;
            next++;
            label[start] = next;
            stack.Push(start);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;

            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % area.Width, y = i / area.Width;
                count++;
                (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));

                for (var ny = Math.Max(0, y - 1); ny <= Math.Min(area.Height - 1, y + 1); ny++)
                for (var nx = Math.Max(0, x - 1); nx <= Math.Min(area.Width - 1, x + 1); nx++)
                {
                    var j = ny * area.Width + nx;
                    if (!foreground[j] || label[j] != 0) continue;
                    label[j] = next;
                    stack.Push(j);
                }
            }

            if (count < MinPixels) continue;
            var box = new Box(area.X + minX, area.Y + minY, maxX - minX + 1, maxY - minY + 1);
            var overlap = seeds.Max(s => Overlap(box.ToRect().Inflate(slack), s.Inflate(slack)));
            if (overlap >= MinOverlap && (best is null || overlap > best.Value.Overlap))
                best = (box, next, overlap);
        }

        if (best is not { } b) return null;
        var mask = new bool[b.Box.Width * b.Box.Height];
        for (var y = 0; y < b.Box.Height; y++)
        for (var x = 0; x < b.Box.Width; x++)
            mask[y * b.Box.Width + x] = label[(b.Box.Y - area.Y + y) * area.Width + (b.Box.X - area.X + x)] == b.Label;
        return (b.Box, mask);
    }

    /// <summary>Cuánto se parece un píxel al relleno: 0 en el fondo, 1 en el relleno (proyección sobre fondo→relleno).</summary>
    static double Projection(Rgb p, Rgb background, Rgb fill)
    {
        double fr = fill.R - background.R, fg = fill.G - background.G, fb = fill.B - background.B;
        var length = fr * fr + fg * fg + fb * fb;
        return ((p.R - background.R) * fr + (p.G - background.G) * fg + (p.B - background.B) * fb) / length;
    }

    static Box? Bounds(Box box, Func<int, int, bool> inside)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = box.Y; y < box.Bottom; y++)
        for (var x = box.X; x < box.Right; x++)
        {
            if (!inside(x, y)) continue;
            (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
        }
        return maxX < 0 ? null : new Box(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// En una esquina redondeada de radio r, la diagonal desde la esquina de la caja entra al relleno a
    /// r·(1 − 1/√2) de cada lado. Se mide en las cuatro esquinas y se toma la mediana.
    /// </summary>
    static double CornerRadius(Box box, Func<int, int, bool> inside)
    {
        var limit = Math.Min(box.Width, box.Height) / 2;
        int Inset(int x0, int y0, int sx, int sy)
        {
            var k = 0;
            while (k < limit && !inside(x0 + sx * k, y0 + sy * k)) k++;
            return k;
        }

        double[] insets =
        [
            Inset(box.X, box.Y, 1, 1), Inset(box.Right - 1, box.Y, -1, 1),
            Inset(box.X, box.Bottom - 1, 1, -1), Inset(box.Right - 1, box.Bottom - 1, -1, -1),
        ];
        return Math.Min(limit, ElementMatcher.Median(insets) / (1 - 1 / Math.Sqrt(2)));
    }

    /// <summary>
    /// Color del borde: desde el medio de cada lado del relleno hacia afuera, el píxel que más se aleja del
    /// relleno y del fondo. Cuenta si aparece en al menos dos lados (una sombra solo se ve abajo); de los que
    /// aparecen, el que más se parece a los demás.
    /// </summary>
    static Rgb? BorderColor(PixelImage img, Box box, Rgb background, Rgb fill, int steps)
    {
        (int X, int Y, int Dx, int Dy)[] sides =
        [
            (box.X + box.Width / 2, box.Y - 1, 0, -1), (box.X + box.Width / 2, box.Bottom, 0, 1),
            (box.X - 1, box.Y + box.Height / 2, -1, 0), (box.Right, box.Y + box.Height / 2, 1, 0),
        ];

        var found = new List<Rgb>();
        foreach (var (x0, y0, dx, dy) in sides)
        {
            Rgb? best = null;
            var bestScore = BorderContrast;
            for (var k = 0; k < steps; k++)
            {
                int x = x0 + dx * k, y = y0 + dy * k;
                if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) break;
                var p = img[x, y];
                var score = Math.Min(p.DistanceTo(background), p.DistanceTo(fill));
                if (score > bestScore) (best, bestScore) = (p, score);
            }
            if (best is { } c) found.Add(c);
        }

        return found.Count >= 2 ? found.MinBy(c => found.Sum(o => c.DistanceTo(o))) : null;
    }

    /// <summary>Color de los trazos de un ícono: el 20 % de sus píxeles más alejados del fondo, como en las letras.</summary>
    static Rgb InkColor(PixelImage img, Box box, bool[] mask, Rgb background)
    {
        var ink = new List<(double Distance, Rgb Color)>();
        for (var y = 0; y < box.Height; y++)
        for (var x = 0; x < box.Width; x++)
        {
            if (!mask[y * box.Width + x]) continue;
            var c = img[box.X + x, box.Y + y];
            ink.Add((c.DistanceTo(background), c));
        }

        var core = ink.OrderByDescending(p => p.Distance).Take(Math.Max(1, ink.Count / 5)).ToList();
        return new Rgb((byte)core.Average(p => p.Color.R), (byte)core.Average(p => p.Color.G), (byte)core.Average(p => p.Color.B));
    }

    /// <summary>Color más frecuente en el marco entre <paramref name="inner"/> y <paramref name="outer"/>.</summary>
    static Rgb? DominantInRing(PixelImage img, Rect inner, Rect outer)
    {
        var o = Clamp(img, outer);
        var i = Clamp(img, inner);
        var counts = new Dictionary<int, (int Count, long R, long G, long B)>();
        for (var y = o.Y; y < o.Bottom; y++)
        for (var x = o.X; x < o.Right; x++)
        {
            if (x >= i.X && x < i.Right && y >= i.Y && y < i.Bottom) continue;
            Count(counts, img[x, y]);
        }
        return Top(counts);
    }

    /// <summary>Color más frecuente en la caja, cuantizado para absorber el antialiasing.</summary>
    static Rgb? Dominant(PixelImage img, Box box)
    {
        var counts = new Dictionary<int, (int Count, long R, long G, long B)>();
        for (var y = box.Y; y < box.Bottom; y++)
        for (var x = box.X; x < box.Right; x++)
            Count(counts, img[x, y]);
        return Top(counts);
    }

    static void Count(Dictionary<int, (int Count, long R, long G, long B)> counts, Rgb c)
    {
        var key = (c.R >> 3) << 10 | (c.G >> 3) << 5 | (c.B >> 3);
        counts.TryGetValue(key, out var acc);
        counts[key] = (acc.Count + 1, acc.R + c.R, acc.G + c.G, acc.B + c.B);
    }

    static Rgb? Top(Dictionary<int, (int Count, long R, long G, long B)> counts)
    {
        if (counts.Count == 0) return null;
        var top = counts.Values.MaxBy(v => v.Count);
        return new Rgb((byte)(top.R / top.Count), (byte)(top.G / top.Count), (byte)(top.B / top.Count));
    }

    internal static double Overlap(Rect a, Rect b)
    {
        var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0) return 0;
        var intersection = w * h;
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }
}
