#:package SkiaSharp@4.152.1
using SkiaSharp;

// Regenera los PNG desde la geometría de icon.svg:
//   dotnet run assets/render.cs -- assets samples/PayloMaui/Resources/Fonts

var assets = args[0];
var fonts = args[1];

var ink = SKColor.Parse("#14161F");
var green = SKColor.Parse("#34D399");
var magenta = SKColor.Parse("#E8457C");

void DrawIcon(SKCanvas c, float x, float y, float size)
{
    c.Save();
    c.Translate(x, y);
    c.Scale(size / 64f);
    using var bg = new SKPaint { Color = ink, IsAntialias = true };
    c.DrawRoundRect(0, 0, 64, 64, 15, 15, bg);
    Check(c, magenta, [(12, 29), (24, 41), (47, 17)]);
    Check(c, green, [(17, 34), (29, 46), (52, 22)]);
    c.Restore();
}

void Check(SKCanvas c, SKColor color, (float X, float Y)[] p)
{
    using var paint = new SKPaint
    {
        Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7,
        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
    };
    using var path = new SKPath();
    path.MoveTo(p[0].X, p[0].Y);
    foreach (var (px, py) in p[1..]) path.LineTo(px, py);
    c.DrawPath(path, paint);
}

void Save(SKSurface s, string name)
{
    using var img = s.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(Path.Combine(assets, name), data.ToArray());
    Console.WriteLine(name);
}

foreach (var size in new[] { 128, 512 })
{
    using var s = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    s.Canvas.Clear(SKColors.Transparent);
    DrawIcon(s.Canvas, 0, 0, size);
    Save(s, $"icon-{size}.png");
}

// Vista previa social de GitHub: 1280 × 640, con margen para el recorte que hacen algunas redes.
{
    using var s = SKSurface.Create(new SKImageInfo(1280, 640));
    var c = s.Canvas;
    c.Clear(SKColor.Parse("#232836"));
    DrawIcon(c, 104, 200, 240);

    using var semibold = SKTypeface.FromFile(Path.Combine(fonts, "Inter-SemiBold.ttf"));
    using var regular = SKTypeface.FromFile(Path.Combine(fonts, "Inter-Regular.ttf"));
    using var title = new SKFont(semibold, 100);
    using var tagline = new SKFont(regular, 34);
    using var meta = new SKFont(regular, 28);
    using var white = new SKPaint { Color = SKColor.Parse("#F4F5F8"), IsAntialias = true };
    using var muted = new SKPaint { Color = SKColor.Parse("#9AA3B0"), IsAntialias = true };
    using var accent = new SKPaint { Color = green, IsAntialias = true };

    c.DrawText("Driftcheck", 398, 318, SKTextAlign.Left, title, white);
    c.DrawText("Compare your mobile app with its Figma design", 402, 378, SKTextAlign.Left, tagline, muted);
    c.DrawText("MCP server · Android · iOS Simulator", 402, 432, SKTextAlign.Left, meta, accent);
    Save(s, "social-preview.png");
}
