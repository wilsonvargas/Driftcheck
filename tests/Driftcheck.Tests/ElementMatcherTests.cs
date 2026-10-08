using Driftcheck.Mcp;
using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Comparison;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Tests;

public class ElementMatcherTests
{
    [Fact]
    public void MatchesByTextWhenLayerNamesAreGeneric()
    {
        // Como en los kits reales: todas las capas de texto se llaman "Text" y la app no tiene AutomationId.
        // La app además está 24 dp más abajo (barra de estado).
        var design = new[]
        {
            Text("Text", "Nice to meet you", new Rect(32, 266, 300, 36)),
            Text("Text", "Sign up", new Rect(170, 650, 70, 15)),
        };
        var screen = new[]
        {
            Screen(null, "Nice to meet you", new Rect(32, 290, 300, 36)),
            Screen(null, "Sign up", new Rect(170, 674, 70, 15)),
        };

        var result = ElementMatcher.Match(design, screen, scale: 1);

        Assert.Equal(2, result.Pairs.Count);
        Assert.All(result.Pairs, p => Assert.Equal(MatchMethod.Text, p.Method));
        Assert.Equal("Sign up", result.Pairs.Single(p => p.Design.Text == "Sign up").Screen.Text);
        Assert.Equal(24, result.Dy);
    }

    [Fact]
    public void TextComparisonIgnoresCaseAndWhitespace()
    {
        var design = new[] { Text("Title", "Before we begin,\nwe need some details.", new Rect(0, 0, 300, 40)) };
        var screen = new[] { Screen(null, "before we begin, we need  some details.", new Rect(0, 0, 300, 40)) };

        var result = ElementMatcher.Match(design, screen, scale: 1);

        Assert.Equal(MatchMethod.Text, Assert.Single(result.Pairs).Method);
    }

    [Fact]
    public void RepeatedTextPicksTheNearestElement()
    {
        var design = new[]
        {
            Text("Anchor", "Header", new Rect(0, 0, 100, 20)),
            Text("Text", "or", new Rect(10, 100, 20, 20)),
            Text("Text", "or", new Rect(10, 300, 20, 20)),
        };
        // Orden invertido a propósito: emparejar por orden de aparición daría las parejas cruzadas.
        var lower = Screen(null, "or", new Rect(10, 300, 20, 20));
        var upper = Screen(null, "or", new Rect(10, 100, 20, 20));
        var screen = new[] { Screen(null, "Header", new Rect(0, 0, 100, 20)), lower, upper };

        var result = ElementMatcher.Match(design, screen, scale: 1);

        Assert.Same(upper, result.Pairs.Single(p => p.Design == design[1]).Screen);
        Assert.Same(lower, result.Pairs.Single(p => p.Design == design[2]).Screen);
    }

    [Fact]
    public void MatchesByPositionWhenTextChanged()
    {
        var design = new[]
        {
            Text("Text", "Nice to meet you", new Rect(32, 266, 300, 36)),
            Text("Text", "Sign up", new Rect(34, 631, 344, 52)),
        };
        var signIn = Screen(null, "Sign in", new Rect(34, 633, 344, 52));
        var screen = new[] { Screen(null, "Nice to meet you", new Rect(32, 266, 300, 36)), signIn };

        var result = ElementMatcher.Match(design, screen, scale: 1);

        var pair = result.Pairs.Single(p => p.Design.Text == "Sign up");
        Assert.Equal(MatchMethod.Position, pair.Method);
        Assert.Same(signIn, pair.Screen);
    }

    [Fact]
    public void DoesNotPairUnrelatedElementsByPosition()
    {
        var design = new[] { Text("Text", "Sign up", new Rect(34, 631, 344, 52)) };
        var screen = new[] { Screen(null, "Click me", new Rect(34, 1000, 344, 52)) };

        var result = ElementMatcher.Match(design, screen, scale: 1);

        Assert.Empty(result.Pairs);
        Assert.Single(result.Missing);
    }

    [Fact]
    public void RepeatedLayerNamesAreNotMatchedByName()
    {
        // Dos capas "Button" en el diseño: el AutomationId "Button" no dice a cuál corresponde.
        var design = new[]
        {
            new DesignElement("Button", "FRAME", new Rect(0, 0, 100, 40), null, null, null, null, null, null),
            new DesignElement("Button", "FRAME", new Rect(0, 100, 100, 40), null, null, null, null, null, null),
        };
        var screen = new[] { Screen("Button", null, new Rect(0, 0, 100, 40)) };

        var result = ElementMatcher.Match(design, screen, scale: 1);

        Assert.Empty(result.Pairs);
    }

    [Theory]
    [InlineData("com.companyname.paylomaui:id/LoginButton", "com.companyname.paylomaui", "LoginButton")]
    [InlineData("android:id/content", "com.companyname.paylomaui", null)]
    [InlineData("android:id/content", null, null)]
    [InlineData("com.android.systemui:id/clock", "com.android.systemui", null)]
    [InlineData("com.google.material:id/toolbar", "com.companyname.paylomaui", null)]
    [InlineData("com.companyname.paylomaui:id/action_bar_root", "com.companyname.paylomaui", null)]
    [InlineData("com.driftcheck.paylonative:id/Image", "com.driftcheck.paylonative", "Image")]
    [InlineData("", "com.companyname.paylomaui", null)]
    public void IgnoresSystemResourceIds(string resourceId, string? package, string? expected) =>
        Assert.Equal(expected, AdbClient.ParseAutomationId(resourceId, package));

    [Fact]
    public void SystemContainerIsNotMatchedToDesignLayerNamedContent()
    {
        // El caso real que falló: la capa "Content" de Figma se emparejaba con android:id/content.
        const string xml = """
            <?xml version='1.0' encoding='UTF-8' standalone='yes' ?>
            <hierarchy rotation="0">
              <node resource-id="android:id/content" class="android.widget.FrameLayout" package="com.companyname.paylomaui" text="" content-desc="" bounds="[0,0][1080,2400]">
                <node resource-id="com.companyname.paylomaui:id/SignUp" class="android.widget.Button" package="com.companyname.paylomaui" text="Sign up" content-desc="" bounds="[88,1660][992,1798]" />
              </node>
            </hierarchy>
            """;

        var screen = AdbClient.ParseHierarchy(xml, pxToDp: 160.0 / 420);
        var design = new[] { new DesignElement("Content", "FRAME", new Rect(0, -48, 202, 36), null, null, null, null, null, null) };

        Assert.DoesNotContain(screen, s => s.Id == "content");
        Assert.Equal("SignUp", Assert.Single(screen).Id);
        Assert.Empty(ElementMatcher.Match(design, screen, scale: 1).Pairs);
    }

    static DesignElement Text(string name, string text, Rect bounds) =>
        new(name, "TEXT", bounds, new Rgb(0, 0, 0), text, 16, "Inter", 400, null);

    static ScreenElement Screen(string? id, string? text, Rect bounds) =>
        new(id, "android.widget.TextView", text, null, bounds);
}
