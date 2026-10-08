using System.Text.Json;
using Driftcheck.Mcp;
using Driftcheck.Mcp.Figma;

namespace Driftcheck.Tests;

public class AutoLayoutTests
{
    [Fact]
    public void HiddenChildrenDoNotTakeSpace()
    {
        // El caso real del kit Paylo: dos íconos ocultos de 40 antes del texto, y el JSON de Figma
        // coloca el texto como si ocuparan lugar (x=129) aunque el render lo muestra en x=49.
        var root = Node("field", "HORIZONTAL", new Rect(35, 496, 360, 46), """
            "paddingLeft": 14, "paddingRight": 14, "counterAxisAlignItems": "CENTER",
            "layoutSizingHorizontal": "FIXED", "layoutSizingVertical": "FIXED"
            """,
            Leaf("icon1", new Rect(49, 499, 40, 40), visible: false),
            Leaf("icon2", new Rect(89, 499, 40, 40), visible: false),
            Leaf("text", new Rect(129, 500, 97, 16)));

        var layout = AutoLayout.Reflow(root);

        Assert.Equal(new Rect(49, 511, 97, 16), layout["text"]);
        Assert.DoesNotContain("icon1", layout.Keys);
    }

    [Fact]
    public void HugSizeIsRecomputedFromVisibleChildren()
    {
        // El campo "hug" de alto: con el ícono oculto fuera, mide 8 + 38 + 8 = 54 (el JSON decía 46),
        // y lo que viene debajo en la columna baja en consecuencia.
        var field = $$"""
            { "id": "field", "name": "field", "type": "FRAME", "layoutMode": "HORIZONTAL",
              "paddingLeft": 14, "paddingRight": 14, "paddingTop": 8, "paddingBottom": 8,
              "layoutSizingHorizontal": "FILL", "layoutSizingVertical": "HUG",
              "absoluteBoundingBox": {{Box(new Rect(0, 0, 360, 46))}},
              "children": [{{Leaf("icon", new Rect(14, 3, 40, 40), visible: false)}}, {{Leaf("content", new Rect(54, 4, 212, 38))}}] }
            """;
        var root = Node("column", "VERTICAL", new Rect(0, 0, 360, 66), """
            "itemSpacing": 4, "layoutSizingHorizontal": "FIXED", "layoutSizingVertical": "HUG"
            """,
            field,
            Leaf("error", new Rect(0, 50, 360, 16), extra: "\"layoutSizingHorizontal\": \"FILL\""));

        var layout = AutoLayout.Reflow(root);

        Assert.Equal(new Rect(0, 0, 360, 54), layout["field"]);
        Assert.Equal(new Rect(14, 8, 212, 38), layout["content"]);
        Assert.Equal(new Rect(0, 58, 360, 16), layout["error"]);
    }

    [Fact]
    public void FillChildStopsAtMaxWidthAndIsAligned()
    {
        // Columna de 430 con padding 32: el hijo "fill" llegaría a 366, pero tiene maxWidth 360 y va centrado.
        var root = Node("frame", "VERTICAL", new Rect(0, 0, 430, 200), """
            "paddingLeft": 32, "paddingRight": 32, "counterAxisAlignItems": "CENTER",
            "layoutSizingHorizontal": "FIXED", "layoutSizingVertical": "FIXED"
            """,
            Leaf("column", new Rect(35, 0, 360, 100), extra: """
                "layoutSizingHorizontal": "FILL", "maxWidth": 360
                """));

        Assert.Equal(new Rect(35, 0, 360, 100), AutoLayout.Reflow(root)["column"]);
    }

    [Fact]
    public void FillSharesTheRemainingSpaceAndSpacingIsRespected()
    {
        // Fila "or": línea | texto de 80 | línea, con 0 de separación.
        var root = Node("row", "HORIZONTAL", new Rect(0, 0, 360, 24), """
            "itemSpacing": 0, "counterAxisAlignItems": "CENTER",
            "layoutSizingHorizontal": "FIXED", "layoutSizingVertical": "FIXED"
            """,
            Leaf("left", new Rect(0, 11.5, 10, 1), extra: "\"layoutSizingHorizontal\": \"FILL\""),
            Leaf("or", new Rect(10, 0, 80, 24)),
            Leaf("right", new Rect(90, 11.5, 10, 1), extra: "\"layoutSizingHorizontal\": \"FILL\""));

        var layout = AutoLayout.Reflow(root);

        Assert.Equal(new Rect(0, 11.5, 140, 1), layout["left"]);
        Assert.Equal(new Rect(140, 0, 80, 24), layout["or"]);
        Assert.Equal(new Rect(220, 11.5, 140, 1), layout["right"]);
    }

    [Fact]
    public void ChildrenOfFramesWithoutAutoLayoutMoveWithTheirParent()
    {
        var root = Node("row", "HORIZONTAL", new Rect(0, 0, 200, 40), """
            "layoutSizingHorizontal": "FIXED", "layoutSizingVertical": "FIXED"
            """,
            Leaf("hidden", new Rect(0, 0, 50, 40), visible: false),
            Leaf("group", new Rect(50, 0, 40, 40), children: [Leaf("dot", new Rect(60, 10, 4, 4))]));

        Assert.Equal(new Rect(10, 10, 4, 4), AutoLayout.Reflow(root)["dot"]);
    }

    [Fact]
    public void NodesWithoutStaleDataKeepTheirBoxes()
    {
        var root = Node("column", "VERTICAL", new Rect(35, 165, 360, 66), """
            "itemSpacing": 4, "counterAxisAlignItems": "CENTER",
            "layoutSizingHorizontal": "FIXED", "layoutSizingVertical": "HUG"
            """,
            Leaf("title", new Rect(35, 165, 360, 38), extra: "\"layoutSizingHorizontal\": \"FILL\""),
            Leaf("subtitle", new Rect(35, 207, 360, 24), extra: "\"layoutSizingHorizontal\": \"FILL\""));

        var layout = AutoLayout.Reflow(root);

        Assert.Equal(new Rect(35, 165, 360, 38), layout["title"]);
        Assert.Equal(new Rect(35, 207, 360, 24), layout["subtitle"]);
    }

    static JsonElement Node(string id, string mode, Rect box, string props, params string[] children) =>
        JsonDocument.Parse($$"""
            { "id": "{{id}}", "name": "{{id}}", "type": "FRAME", "layoutMode": "{{mode}}", {{props}},
              "absoluteBoundingBox": {{Box(box)}}, "children": [{{string.Join(",", children)}}] }
            """).RootElement;

    static string Leaf(string id, Rect box, bool visible = true, string? extra = null, string[]? children = null) =>
        $$"""
        { "id": "{{id}}", "name": "{{id}}", "type": "FRAME", "visible": {{(visible ? "true" : "false")}},
          {{(extra is null ? "" : extra + ",")}} "absoluteBoundingBox": {{Box(box)}},
          "children": [{{string.Join(",", children ?? [])}}] }
        """;

    static string Box(Rect r) =>
        FormattableString.Invariant($$"""{ "x": {{r.X}}, "y": {{r.Y}}, "width": {{r.Width}}, "height": {{r.Height}} }""");
}
