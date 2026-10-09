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
    public void ParsesBootedSimulators()
    {
        const string json = """
            { "devices": {
                "com.apple.CoreSimulator.SimRuntime.iOS-26-3": [
                  { "udid": "A1B2", "name": "iPhone 17", "state": "Booted" },
                  { "udid": "C3D4", "name": "iPhone 17 Pro", "state": "Shutdown" }
                ] } }
            """;

        var devices = SimulatorClient.ParseDevices(json);

        Assert.Equal([new DeviceInfo("A1B2", "Simulador de iOS, iPhone 17 (iOS 26.3)")], devices);
    }

    [Theory]
    [InlineData("xcrun: error: unable to find utility \"simctl\"")]
    [InlineData("""{ "devices": { "com.apple.CoreSimulator.SimRuntime.iOS-26-3": [ { "udid": "A1B2", "na""")]
    [InlineData("""{ "runtimes": [] }""")]
    [InlineData("""{ "devices": [] }""")]
    public void UnexpectedSimctlOutputIsAnInvalidOperation(string json)
    {
        // DeviceRegistry solo atrapa InvalidOperationException: otra excepción haría fallar también la lista de Android.
        var e = Assert.Throws<InvalidOperationException>(() => SimulatorClient.ParseDevices(json));
        Assert.Contains("simctl", e.Message);
    }

    [Fact]
    public async Task ProcessThatHangsIsKilledAfterTimeout()
    {
        (string file, string[] args) sleep = OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"])
            : ("sleep", ["30"]);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SimulatorClient.RunTextAsync(sleep.file, sleep.args, null, TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Contains("no terminó en 1 s", e.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Tardó {watch.Elapsed}");
    }

    [Fact]
    public void CaptureUsesPointsAsUnits()
    {
        var capture = new ScreenCapture("udid", "device.png", 1206, 2622, 3, [], DevicePlatform.Ios);

        Assert.Equal(402, capture.WidthUnits, precision: 6);
        Assert.Equal("pt", capture.Unit);
    }
}
