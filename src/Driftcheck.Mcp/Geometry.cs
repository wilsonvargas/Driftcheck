namespace Driftcheck.Mcp;

/// <summary>Rectángulo en unidades lógicas (dp en Android, pt en Figma).</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public Rect Scale(double s) => new(X * s, Y * s, Width * s, Height * s);
    public Rect Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
    public override string ToString() => $"x={X:0.#} y={Y:0.#} w={Width:0.#} h={Height:0.#}";
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Distancia euclidiana simple en RGB (0–441).</summary>
    public double DistanceTo(Rgb o) =>
        Math.Sqrt(Math.Pow(R - o.R, 2) + Math.Pow(G - o.G, 2) + Math.Pow(B - o.B, 2));

    public override string ToString() => Hex;
}
