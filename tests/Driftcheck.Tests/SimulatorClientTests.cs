using System.Text.Json;
using Driftcheck.Mcp;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Ios;

namespace Driftcheck.Tests;

public class SimulatorClientTests
{
    [Fact]
    public void ParsesAccessibilityDumpInPoints()
    {
        // Lo que escribe tools/ios-accessibility-helper para una pantalla de MAUI en un iPhone de 402 pt.
        const string json = """
            { "bundleId": "com.companyname.paylomaui", "frame": { "x": 0, "y": 0, "width": 402, "height": 874 },
              "elements": [
                { "type": "Application", "label": "PayloMaui", "frame": { "x": 0, "y": 0, "width": 402, "height": 874 } },
                { "type": "Window", "frame": { "x": 0, "y": 0, "width": 402, "height": 874 } },
                { "type": "Other", "frame": { "x": 0, "y": 0, "width": 402, "height": 874 } },
                { "type": "Other", "identifier": "Header", "frame": { "x": 0, "y": 62, "width": 402, "height": 120 } },
                { "type": "StaticText", "label": "Create account", "frame": { "x": 35, "y": 200, "width": 210.5, "height": 38.33 } },
                { "type": "TextField", "identifier": "Email", "label": "Email", "placeholder": "you@mail.com", "frame": { "x": 35, "y": 300, "width": 332, "height": 46 } },
                { "type": "TextField", "identifier": "Name", "value": "Wilson", "placeholder": "Your name", "frame": { "x": 35, "y": 360, "width": 332, "height": 46 } },
                { "type": "Button", "identifier": "SignUp", "label": "Sign up", "frame": { "x": 35, "y": 760, "width": 332, "height": 52 } },
                { "type": "Image", "frame": { "x": 10, "y": 10, "width": 20, "height": 20 } },
                { "type": "StaticText", "label": "Below the fold", "frame": { "x": 35, "y": 900, "width": 200, "height": 20 } },
                { "type": "StaticText", "label": "Collapsed", "frame": { "x": 35, "y": 500, "width": 0, "height": 20 } }
              ] }
            """;

        using var doc = JsonDocument.Parse(json);
        var elements = SimulatorClient.ParseElements(doc.RootElement, new Rect(0, 0, 402, 874));

        Assert.Equal(["Header", null, "Email", "Name", "SignUp"], elements.Select(e => e.Id));

        var title = elements[1];
        Assert.Equal("Create account", title.Text);
        Assert.Equal(new Rect(35, 200, 210.5, 38.33), title.Bounds);

        // Un campo vacío muestra el placeholder, como el hint de un EditText en uiautomator.
        Assert.Equal("you@mail.com", elements[2].Text);
        Assert.Equal("Email", elements[2].ContentDescription);
        Assert.Equal("Wilson", elements[3].Text);
        Assert.Equal("Sign up", elements[4].Text);
    }

    [Fact]
    public void CaptureUsesPointsAsUnits()
    {
        var capture = new ScreenCapture("udid", "device.png", 1206, 2622, 3, [], DevicePlatform.Ios);

        Assert.Equal(402, capture.WidthUnits, precision: 6);
        Assert.Equal("pt", capture.Unit);
    }
}
