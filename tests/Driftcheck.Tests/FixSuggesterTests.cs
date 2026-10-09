using Driftcheck.Mcp;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Tests;

public class FixSuggesterTests
{
    static readonly DesignElement Button = new("Sign up", "FRAME", new Rect(35, 760, 332, 52), new Rgb(0x0D, 0x00, 0x1F), null, null, null, null, 12);
    static readonly DesignElement Title = new("Text", "TEXT", new Rect(35, 200, 210, 38), null, "Create account", 32, "Inter", 600, null);

    [Theory]
    [InlineData(UiFramework.Compose, "Muévelo 4 dp a la izquierda: `Modifier.padding(start = …)` −4.dp (o el padding del contenedor)")]
    [InlineData(UiFramework.AndroidViews, "Muévelo 4 dp a la izquierda: `android:layout_marginStart` −4dp (o el padding del contenedor)")]
    [InlineData(UiFramework.Generic, "Muévelo 4 dp a la izquierda: margen izquierdo −4 dp (o el padding del contenedor)")]
    public void PositionUsesFrameworkProperty(UiFramework fw, string expected)
    {
        var fix = FixSuggester.Suggest(fw, "dp", Button, new ElementDiff("Sign up", "x", "35", "39"));
        Assert.Equal(expected, fix);
    }

    [Fact]
    public void ColorsUseEachPlatformSyntax()
    {
        var diff = new ElementDiff("Sign up", "background", "#0D001F", "#1976D2");

        Assert.Equal("`Modifier.background(Color(0xFF0D001F))` o `ButtonDefaults.buttonColors(containerColor = Color(0xFF0D001F))`",
            FixSuggester.Suggest(UiFramework.Compose, "dp", Button, diff));
        Assert.Equal("`backgroundColor = UIColor(red: 0.051, green: 0, blue: 0.122, alpha: 1)`",
            FixSuggester.Suggest(UiFramework.UIKit, "pt", Button, diff));
    }

    [Fact]
    public void TextSizeDiffPointsToTheFont()
    {
        var fix = FixSuggester.Suggest(UiFramework.Maui, "dp", Title, new ElementDiff("Text", "width", "210", "196"));
        Assert.Equal(fix, FixSuggester.Suggest(UiFramework.Maui, "dp", Title, new ElementDiff("Text", "height", "38", "30")));

        Assert.Equal("Las letras no miden lo mismo que en Figma; revisa la fuente: en Figma es Inter 32 peso 600. " +
                     "`FontFamily=\"Inter\"`, `FontSize=\"32\"`, `CharacterSpacing`", fix);
    }

    [Fact]
    public void MissingTextSuggestsAddingIt()
    {
        Assert.Equal("Agrega `Text(\"Create account\")` (Inter 32 peso 600)", FixSuggester.AddText(UiFramework.SwiftUI, Title));
    }

    [Theory]
    [InlineData(null, UiFramework.Generic)]
    [InlineData("MAUI", UiFramework.Maui)]
    [InlineData("Jetpack Compose", UiFramework.Compose)]
    [InlineData("swiftui", UiFramework.SwiftUI)]
    public void ParsesFrameworkNames(string? name, UiFramework expected) =>
        Assert.Equal(expected, FixSuggester.ParseFramework(name));

    [Fact]
    public void UnknownFrameworkIsRejected() =>
        Assert.Throws<ArgumentException>(() => FixSuggester.ParseFramework("react native"));
}
