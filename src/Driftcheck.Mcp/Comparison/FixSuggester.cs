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
            $"Framework desconocido: {name}. Usa maui, android, compose, uikit, swiftui o flutter."),
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
                ? "El ícono no se ve como en Figma: revisa que sea el mismo recurso (exporta el vector de la capa)"
                : "La imagen no se ve como en Figma: revisa que sea el mismo recurso y cómo se escala (Aspect)",
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
        if (d.Shape != ShapeKind.Shape && (N(d.Bounds.Width) != N(visible.Width) || N(d.Bounds.Height) != N(visible.Height)))
            size += $" (la capa en Figma mide {N(d.Bounds.Width)}×{N(d.Bounds.Height)})";
        return d.Shape switch
        {
            ShapeKind.Icon => $"Falta un ícono de {size}",
            ShapeKind.Image => $"Falta una imagen de {size}",
            _ when Math.Min(visible.Width, visible.Height) <= 2 => $"Falta una línea de {size}" + (d.Fill is { } f ? $" ({f.Hex})" : ""),
            _ => $"Falta una forma de {size}" + (d.Fill is { } f && fw != UiFramework.Generic ? $": {Background(fw, f)}" : d.Fill is { } g ? $" con fondo {g.Hex}" : ""),
        };
    }

    /// <summary>Sugerencia para un texto del diseño que no está en la app.</summary>
    public static string AddText(UiFramework fw, DesignElement d)
    {
        var text = Quote(d.Text ?? "");
        var font = d.FontSize is not null ? $" ({FontDescription(d)})" : "";
        return fw switch
        {
            UiFramework.Maui => $"Agrega `<Label Text=\"{text}\" />`{font}",
            UiFramework.AndroidViews => $"Agrega un `<TextView android:text=\"{text}\" />`{font}",
            UiFramework.Compose => $"Agrega `Text(\"{text}\")`{font}",
            UiFramework.UIKit => $"Agrega un `UILabel` con `text = \"{text}\"`{font}",
            UiFramework.SwiftUI => $"Agrega `Text(\"{text}\")`{font}",
            UiFramework.Flutter => $"Agrega `Text('{text}')`{font}",
            _ => $"Falta el texto «{d.Text}»{font}",
        };
    }

    static double Delta(ElementDiff diff) => Parse(diff.Expected) - Parse(diff.Actual);

    static string Move(UiFramework fw, string unit, double delta, bool horizontal)
    {
        var direction = horizontal ? (delta > 0 ? "a la derecha" : "a la izquierda") : (delta > 0 ? "hacia abajo" : "hacia arriba");
        var d = Signed(delta);
        var property = (fw, horizontal) switch
        {
            (UiFramework.Maui, true) => $"`Margin` izquierdo {d}",
            (UiFramework.Maui, false) => $"`Margin` superior {d}",
            (UiFramework.AndroidViews, true) => $"`android:layout_marginStart` {d}dp",
            (UiFramework.AndroidViews, false) => $"`android:layout_marginTop` {d}dp",
            (UiFramework.Compose, true) => $"`Modifier.padding(start = …)` {d}.dp",
            (UiFramework.Compose, false) => $"`Modifier.padding(top = …)` {d}.dp",
            (UiFramework.UIKit, true) => $"constante del `leadingAnchor` {d}",
            (UiFramework.UIKit, false) => $"constante del `topAnchor` {d}",
            (UiFramework.SwiftUI, true) => $"`.padding(.leading, …)` {d}",
            (UiFramework.SwiftUI, false) => $"`.padding(.top, …)` {d}",
            (UiFramework.Flutter, true) => $"`EdgeInsets` izquierdo {d}",
            (UiFramework.Flutter, false) => $"`EdgeInsets` superior {d}",
            (_, true) => $"margen izquierdo {d} {unit}",
            (_, false) => $"margen superior {d} {unit}",
        };
        var container = horizontal ? "el padding del contenedor" : "el espacio con el elemento de arriba";
        return $"Muévelo {N(Math.Abs(delta))} {unit} {direction}: {property} (o {container})";
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
        (_, true) => $"Ancho de {expected} {unit}",
        (_, false) => $"Alto de {expected} {unit}",
    };

    /// <summary>
    /// En un texto se miden las letras: si el ancho o el alto no coinciden, casi siempre es la fuente
    /// (familia, tamaño, peso o espaciado), no la caja. Es la misma sugerencia para los dos, así el
    /// reporte la muestra una sola vez.
    /// </summary>
    static string Font(UiFramework fw, DesignElement d)
    {
        var expected = d.FontSize is null ? "" : $": en Figma es {FontDescription(d)}";
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
            _ => "familia, tamaño, peso y espaciado de la fuente",
        };
        return $"Las letras no miden lo mismo que en Figma; revisa la fuente{expected}. {props}";
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
            _ => $"Cambia el texto a «{expected}»",
        };
    }

    static string Background(UiFramework fw, Rgb c) => fw switch
    {
        UiFramework.Maui => $"`BackgroundColor=\"{c.Hex}\"`",
        UiFramework.AndroidViews => $"`android:background=\"{c.Hex}\"` (en un botón de Material, `app:backgroundTint`)",
        UiFramework.Compose => $"`Modifier.background({ComposeColor(c)})` o `ButtonDefaults.buttonColors(containerColor = {ComposeColor(c)})`",
        UiFramework.UIKit => $"`backgroundColor = {UIColor(c)}`",
        UiFramework.SwiftUI => $"`.background({SwiftUIColor(c)})`",
        UiFramework.Flutter => $"`color: {FlutterColor(c)}`",
        _ => $"Color de fondo {c.Hex}",
    };

    static string TextColor(UiFramework fw, Rgb c) => fw switch
    {
        UiFramework.Maui => $"`TextColor=\"{c.Hex}\"`",
        UiFramework.AndroidViews => $"`android:textColor=\"{c.Hex}\"`",
        UiFramework.Compose => $"`color = {ComposeColor(c)}`",
        UiFramework.UIKit => $"`textColor = {UIColor(c)}`",
        UiFramework.SwiftUI => $"`.foregroundStyle({SwiftUIColor(c)})`",
        UiFramework.Flutter => $"`TextStyle(color: {FlutterColor(c)})`",
        _ => $"Color de texto {c.Hex}",
    };

    /// <summary>
    /// El color se compara como se ve en el render, pero en el código va el de Figma: un botón deshabilitado al
    /// 30 % se ve #DBC6F8 aunque su fondo sea #9E56FF.
    /// </summary>
    static string FigmaColor(Rgb? figma, Rgb rendered, Func<Rgb, string> suggest) =>
        figma is { } f && f.DistanceTo(rendered) > 8
            ? $"{suggest(f)} (en el diseño se ve {rendered.Hex}: la capa o un contenedor tiene opacidad)"
            : suggest(rendered);

    static string CornerRadius(UiFramework fw, string unit, string r) => fw switch
    {
        UiFramework.Maui => $"`CornerRadius=\"{r}\"` (en un `Border`, `StrokeShape=\"RoundRectangle {r}\"`)",
        UiFramework.AndroidViews => $"`app:cornerRadius=\"{r}dp\"` (o `android:radius` en el `<corners>` del drawable)",
        UiFramework.Compose => $"`RoundedCornerShape({r}.dp)`",
        UiFramework.UIKit => $"`layer.cornerRadius = {r}`",
        UiFramework.SwiftUI => $"`.clipShape(RoundedRectangle(cornerRadius: {r}))`",
        UiFramework.Flutter => $"`BorderRadius.circular({r})`",
        _ => $"Radio de las esquinas de {r} {unit}",
    };

    static string Border(UiFramework fw, Rgb c) => fw switch
    {
        UiFramework.Maui => $"`Stroke=\"{c.Hex}\"` en el `Border` (o `BorderColor` en un `Button`)",
        UiFramework.AndroidViews => $"`app:strokeColor=\"{c.Hex}\"` (o `<stroke android:color=\"{c.Hex}\" />` en el drawable)",
        UiFramework.Compose => $"`Modifier.border(1.dp, {ComposeColor(c)}, shape)`",
        UiFramework.UIKit => $"`layer.borderColor = {UIColor(c)}.cgColor`",
        UiFramework.SwiftUI => $"`.overlay(RoundedRectangle(cornerRadius: …).stroke({SwiftUIColor(c)}))`",
        UiFramework.Flutter => $"`Border.all(color: {FlutterColor(c)})`",
        _ => $"Borde de color {c.Hex}",
    };

    static string IconColor(UiFramework fw, Rgb c) => fw switch
    {
        UiFramework.Maui => $"`Color=\"{c.Hex}\"` en el `FontImageSource` (o `IconTintColorBehavior`)",
        UiFramework.AndroidViews => $"`app:tint=\"{c.Hex}\"`",
        UiFramework.Compose => $"`tint = {ComposeColor(c)}`",
        UiFramework.UIKit => $"`tintColor = {UIColor(c)}`",
        UiFramework.SwiftUI => $"`.foregroundStyle({SwiftUIColor(c)})`",
        UiFramework.Flutter => $"`Icon(…, color: {FlutterColor(c)})`",
        _ => $"Color del ícono {c.Hex}",
    };

    static string FontDescription(DesignElement d)
    {
        var parts = new List<string>();
        if (d.FontFamily is { } f) parts.Add(f);
        if (d.FontSize is { } s) parts.Add(N(s));
        if (d.FontWeight is { } w && w != 400) parts.Add($"peso {N(w)}");
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
