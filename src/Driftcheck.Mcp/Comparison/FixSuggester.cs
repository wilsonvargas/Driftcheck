using System.Globalization;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Mcp.Comparison;

/// <summary>Framework de la app, para escribir las sugerencias con sus propiedades.</summary>
public enum UiFramework { Generic, Maui, AndroidViews, Compose, UIKit, SwiftUI, Flutter }

/// <summary>
/// Convierte una diferencia ("y: Figma 600, app 612") en lo que hay que cambiar en el código
/// ("Margin superior −12"). Es una pista para el agente y para quien lee el reporte, no un parche:
/// la misma diferencia puede venir del margen del elemento o del espacio del contenedor.
/// </summary>
public static class FixSuggester
{
    public static UiFramework ParseFramework(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" or "generic" => UiFramework.Generic,
        "maui" or ".net maui" or "xamarin" or "xamarin.forms" => UiFramework.Maui,
        "android" or "views" or "android views" or "xml" => UiFramework.AndroidViews,
        "compose" or "jetpack compose" => UiFramework.Compose,
        "uikit" => UiFramework.UIKit,
        "swiftui" => UiFramework.SwiftUI,
        "flutter" => UiFramework.Flutter,
        _ => throw new ArgumentException(
            $"Unknown framework: {name}. Use maui, android, compose, uikit, swiftui or flutter."),
    };

    /// <summary>Sugerencia para una diferencia de <see cref="ScreenComparer"/>, o null si no hay una útil.</summary>
    public static string? Suggest(UiFramework fw, string unit, DesignElement d, ElementDiff diff)
    {
        var isText = d.Type == "TEXT";
        return diff.Property switch
        {
            "x" => Move(fw, unit, Delta(diff), horizontal: true),
            "y" => Move(fw, unit, Delta(diff), horizontal: false),
            "width" or "height" when isText => Font(fw, d),
            "width" => Size(fw, unit, diff.Expected, width: true),
            "height" => Size(fw, unit, diff.Expected, width: false),
            "text" => SetText(fw, diff.Expected),
            "background" when Rgb.TryParse(diff.Expected, out var c) => FigmaColor(d.Fill, c, x => Background(fw, x)),
            "text color" when Rgb.TryParse(diff.Expected, out var c) => TextColor(fw, c),
            "corner radius" => CornerRadius(fw, unit, diff.Expected),
            "border" when Rgb.TryParse(diff.Expected, out var c) => Border(fw, c),
            "icon color" when Rgb.TryParse(diff.Expected, out var c) => FigmaColor(d.Fill, c, x => IconColor(fw, x)),
            "image" => d.Shape == ShapeKind.Icon
                ? "The icon doesn't look like in Figma: check that it is the same asset (export the layer's vector)"
                : "The image doesn't look like in Figma: check that it is the same asset and how it is scaled (Aspect)",
            _ => null,
        };
    }

    /// <summary>
    /// Sugerencia para una forma, un ícono o una imagen del diseño que no está en la app. <paramref name="visible"/>
    /// es lo que se ve en el render; un ícono suele ser una capa más grande con margen alrededor del dibujo.
    /// </summary>
    public static string AddShape(UiFramework fw, string unit, DesignElement d, Rect visible)
    {
        var size = $"{N(visible.Width)}×{N(visible.Height)} {unit}";
        var layer = d.Shape != ShapeKind.Shape && (N(d.Bounds.Width) != N(visible.Width) || N(d.Bounds.Height) != N(visible.Height))
            ? $" (the Figma layer is {N(d.Bounds.Width)}×{N(d.Bounds.Height)})"
            : "";
        return d.Shape switch
        {
            ShapeKind.Icon => $"Missing a {size} icon{layer}",
            ShapeKind.Image => $"Missing a {size} image{layer}",
            _ when Math.Min(visible.Width, visible.Height) <= 2 => $"Missing a {size} line" + (d.Fill is { } f ? $" ({f.Hex})" : ""),
            _ => $"Missing a {size} shape" + (d.Fill is { } f && fw != UiFramework.Generic ? $": {Background(fw, f)}" : d.Fill is { } g ? $" with background {g.Hex}" : ""),
        };
    }

    /// <summary>Sugerencia para un texto del diseño que no está en la app.</summary>
    public static string AddText(UiFramework fw, DesignElement d)
    {
        var text = Quote(d.Text ?? "");
        var font = d.FontSize is not null ? $" ({FontDescription(d)})" : "";
        return fw switch
        {
            UiFramework.Maui => $"Add `<Label Text=\"{text}\" />`{font}",
            UiFramework.AndroidViews => $"Add a `<TextView android:text=\"{text}\" />`{font}",
            UiFramework.Compose => $"Add `Text(\"{text}\")`{font}",
            UiFramework.UIKit => $"Add a `UILabel` with `text = \"{text}\"`{font}",
            UiFramework.SwiftUI => $"Add `Text(\"{text}\")`{font}",
            UiFramework.Flutter => $"Add `Text('{text}')`{font}",
            _ => $"Missing the text \"{d.Text}\"{font}",
        };
    }

    static double Delta(ElementDiff diff) => Parse(diff.Expected) - Parse(diff.Actual);

    static string Move(UiFramework fw, string unit, double delta, bool horizontal)
    {
        var direction = horizontal ? (delta > 0 ? "right" : "left") : (delta > 0 ? "down" : "up");
        var d = Signed(delta);
        var property = (fw, horizontal) switch
        {
            (UiFramework.Maui, true) => $"left `Margin` {d}",
            (UiFramework.Maui, false) => $"top `Margin` {d}",
            (UiFramework.AndroidViews, true) => $"`android:layout_marginStart` {d}dp",
            (UiFramework.AndroidViews, false) => $"`android:layout_marginTop` {d}dp",
            (UiFramework.Compose, true) => $"`Modifier.padding(start = …)` {d}.dp",
            (UiFramework.Compose, false) => $"`Modifier.padding(top = …)` {d}.dp",
            (UiFramework.UIKit, true) => $"`leadingAnchor` constant {d}",
            (UiFramework.UIKit, false) => $"`topAnchor` constant {d}",
            (UiFramework.SwiftUI, true) => $"`.padding(.leading, …)` {d}",
            (UiFramework.SwiftUI, false) => $"`.padding(.top, …)` {d}",
            (UiFramework.Flutter, true) => $"left `EdgeInsets` {d}",
            (UiFramework.Flutter, false) => $"top `EdgeInsets` {d}",
            (_, true) => $"left margin {d} {unit}",
            (_, false) => $"top margin {d} {unit}",
        };
        var container = horizontal ? "the container's padding" : "the spacing to the element above";
        return $"Move it {N(Math.Abs(delta))} {unit} {direction}: {property} (or {container})";
    }

    static string Size(UiFramework fw, string unit, string expected, bool width) => (fw, width) switch
    {
        (UiFramework.Maui, true) => $"`WidthRequest=\"{expected}\"`",
        (UiFramework.Maui, false) => $"`HeightRequest=\"{expected}\"`",
        (UiFramework.AndroidViews, true) => $"`android:layout_width=\"{expected}dp\"`",
        (UiFramework.AndroidViews, false) => $"`android:layout_height=\"{expected}dp\"`",
        (UiFramework.Compose, true) => $"`Modifier.width({expected}.dp)`",
        (UiFramework.Compose, false) => $"`Modifier.height({expected}.dp)`",
        (UiFramework.UIKit, true) => $"`widthAnchor.constraint(equalToConstant: {expected})`",
        (UiFramework.UIKit, false) => $"`heightAnchor.constraint(equalToConstant: {expected})`",
        (UiFramework.SwiftUI, true) => $"`.frame(width: {expected})`",
        (UiFramework.SwiftUI, false) => $"`.frame(height: {expected})`",
        (UiFramework.Flutter, true) => $"`SizedBox(width: {expected})`",
        (UiFramework.Flutter, false) => $"`SizedBox(height: {expected})`",
        (_, true) => $"Width of {expected} {unit}",
        (_, false) => $"Height of {expected} {unit}",
    };

    /// <summary>
    /// En un texto se miden las letras: si el ancho o el alto no coinciden, casi siempre es la fuente
    /// (familia, tamaño, peso o espaciado), no la caja. Es la misma sugerencia para los dos, así el
    /// reporte la muestra una sola vez.
    /// </summary>
    static string Font(UiFramework fw, DesignElement d)
    {
        var expected = d.FontSize is null ? "" : $": in Figma it is {FontDescription(d)}";
        var size = d.FontSize is { } s ? N(s) : "…";
        var family = d.FontFamily ?? "…";
        var props = fw switch
        {
            UiFramework.Maui => $"`FontFamily=\"{family}\"`, `FontSize=\"{size}\"`, `CharacterSpacing`",
            UiFramework.AndroidViews => $"`android:fontFamily`, `android:textSize=\"{size}sp\"`, `android:letterSpacing`",
            UiFramework.Compose => $"`fontFamily`, `fontSize = {size}.sp`, `letterSpacing`",
            UiFramework.UIKit => $"`UIFont(name: \"{family}\", size: {size})`, `kern`",
            UiFramework.SwiftUI => $"`.font(.custom(\"{family}\", size: {size}))`, `.kerning`",
            UiFramework.Flutter => $"`TextStyle(fontFamily: '{family}', fontSize: {size}, letterSpacing: …)`",
            _ => "font family, size, weight and spacing",
        };
        return $"The glyphs don't measure the same as in Figma; check the font{expected}. {props}";
    }

    static string SetText(UiFramework fw, string expected)
    {
        var t = Quote(expected);
        return fw switch
        {
            UiFramework.Maui => $"`Text=\"{t}\"`",
            UiFramework.AndroidViews => $"`android:text=\"{t}\"`",
            UiFramework.Compose => $"`Text(\"{t}\")`",
            UiFramework.UIKit => $"`text = \"{t}\"`",
            UiFramework.SwiftUI => $"`Text(\"{t}\")`",
            UiFramework.Flutter => $"`Text('{t}')`",
            _ => $"Change the text to \"{expected}\"",
        };
    }

    static string Background(UiFramework fw, Rgb c) => fw switch
    {
        UiFramework.Maui => $"`BackgroundColor=\"{c.Hex}\"`",
        UiFramework.AndroidViews => $"`android:background=\"{c.Hex}\"` (on a Material button, `app:backgroundTint`)",
        UiFramework.Compose => $"`Modifier.background({ComposeColor(c)})` or `ButtonDefaults.buttonColors(containerColor = {ComposeColor(c)})`",
        UiFramework.UIKit => $"`backgroundColor = {UIColor(c)}`",
        UiFramework.SwiftUI => $"`.background({SwiftUIColor(c)})`",
        UiFramework.Flutter => $"`color: {FlutterColor(c)}`",
        _ => $"Background color {c.Hex}",
    };

    static string TextColor(UiFramework fw, Rgb c) => fw switch
    {
        UiFramework.Maui => $"`TextColor=\"{c.Hex}\"`",
        UiFramework.AndroidViews => $"`android:textColor=\"{c.Hex}\"`",
        UiFramework.Compose => $"`color = {ComposeColor(c)}`",
        UiFramework.UIKit => $"`textColor = {UIColor(c)}`",
        UiFramework.SwiftUI => $"`.foregroundStyle({SwiftUIColor(c)})`",
        UiFramework.Flutter => $"`TextStyle(color: {FlutterColor(c)})`",
        _ => $"Text color {c.Hex}",
    };

    /// <summary>
    /// El color se compara como se ve en el render, pero en el código va el de Figma: un botón deshabilitado al
    /// 30 % se ve #DBC6F8 aunque su fondo sea #9E56FF.
    /// </summary>
    static string FigmaColor(Rgb? figma, Rgb rendered, Func<Rgb, string> suggest) =>
        figma is { } f && f.DistanceTo(rendered) > 8
            ? $"{suggest(f)} (it renders as {rendered.Hex} in the design: the layer or a container has opacity)"
            : suggest(rendered);

    static string CornerRadius(UiFramework fw, string unit, string r) => fw switch
    {
        UiFramework.Maui => $"`CornerRadius=\"{r}\"` (on a `Border`, `StrokeShape=\"RoundRectangle {r}\"`)",
        UiFramework.AndroidViews => $"`app:cornerRadius=\"{r}dp\"` (or `android:radius` in the drawable's `<corners>`)",
        UiFramework.Compose => $"`RoundedCornerShape({r}.dp)`",
        UiFramework.UIKit => $"`layer.cornerRadius = {r}`",
        UiFramework.SwiftUI => $"`.clipShape(RoundedRectangle(cornerRadius: {r}))`",
        UiFramework.Flutter => $"`BorderRadius.circular({r})`",
        _ => $"Corner radius of {r} {unit}",
    };

    static string Border(UiFramework fw, Rgb c) => fw switch
    {
        UiFramework.Maui => $"`Stroke=\"{c.Hex}\"` on the `Border` (or `BorderColor` on a `Button`)",
        UiFramework.AndroidViews => $"`app:strokeColor=\"{c.Hex}\"` (or `<stroke android:color=\"{c.Hex}\" />` in the drawable)",
        UiFramework.Compose => $"`Modifier.border(1.dp, {ComposeColor(c)}, shape)`",
        UiFramework.UIKit => $"`layer.borderColor = {UIColor(c)}.cgColor`",
        UiFramework.SwiftUI => $"`.overlay(RoundedRectangle(cornerRadius: …).stroke({SwiftUIColor(c)}))`",
        UiFramework.Flutter => $"`Border.all(color: {FlutterColor(c)})`",
        _ => $"Border color {c.Hex}",
    };

    static string IconColor(UiFramework fw, Rgb c) => fw switch
    {
        UiFramework.Maui => $"`Color=\"{c.Hex}\"` on the `FontImageSource` (or `IconTintColorBehavior`)",
        UiFramework.AndroidViews => $"`app:tint=\"{c.Hex}\"`",
        UiFramework.Compose => $"`tint = {ComposeColor(c)}`",
        UiFramework.UIKit => $"`tintColor = {UIColor(c)}`",
        UiFramework.SwiftUI => $"`.foregroundStyle({SwiftUIColor(c)})`",
        UiFramework.Flutter => $"`Icon(…, color: {FlutterColor(c)})`",
        _ => $"Icon color {c.Hex}",
    };

    static string FontDescription(DesignElement d)
    {
        var parts = new List<string>();
        if (d.FontFamily is { } f) parts.Add(f);
        if (d.FontSize is { } s) parts.Add(N(s));
        if (d.FontWeight is { } w && w != 400) parts.Add($"weight {N(w)}");
        return string.Join(" ", parts);
    }

    static string ComposeColor(Rgb c) => $"Color(0xFF{c.Hex[1..]})";
    static string FlutterColor(Rgb c) => $"Color(0xFF{c.Hex[1..]})";
    static string UIColor(Rgb c) => $"UIColor(red: {C(c.R)}, green: {C(c.G)}, blue: {C(c.B)}, alpha: 1)";
    static string SwiftUIColor(Rgb c) => $"Color(red: {C(c.R)}, green: {C(c.G)}, blue: {C(c.B)})";
    static string C(byte v) => (v / 255.0).ToString("0.###", CultureInfo.InvariantCulture);

    static string Quote(string s) => s.ReplaceLineEndings("\\n").Replace("\"", "\\\"");
    static string Signed(double v) => (v > 0 ? "+" : "−") + N(Math.Abs(v));
    static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
