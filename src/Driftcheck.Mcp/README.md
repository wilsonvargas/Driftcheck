# Driftcheck

**Catch UI drift before your users do.** Driftcheck is a local MCP server: your AI agent compares the app on an Android device or the iOS Simulator with its Figma design, gets the differences with how to fix them in your framework (.NET MAUI, Android views, Jetpack Compose, UIKit, SwiftUI or Flutter), and fixes them.

<!-- mcp-name: io.github.wilsonvargas/driftcheck -->

![Driftcheck report: the Figma design and the app under a slider, a 75 % fidelity score and the list of differences](https://raw.githubusercontent.com/wilsonvargas/Driftcheck/master/assets/report-light.png)

[Open the interactive report](https://wilsonvargas.github.io/Driftcheck/demo/) · Design: Paylo kit by Marvilo (CC BY 4.0)

## Quick start

You need the .NET 10 SDK, plus `adb` for Android or Xcode for the iOS Simulator. Add Driftcheck to your MCP client:

```json
{
  "mcpServers": {
    "driftcheck": {
      "command": "dotnet",
      "args": ["dnx", "Driftcheck", "--yes"],
      "env": { "FIGMA_TOKEN": "<your Figma token>" }
    }
  }
}
```

With Claude Code:

```bash
claude mcp add driftcheck -e FIGMA_TOKEN=<your Figma token> -- dotnet dnx Driftcheck --yes
```

Then run your app and ask your agent: *"Compare the app on the emulator with https://www.figma.com/design/…?node-id=… and fix the differences."* Without a Figma token, export the frame with the Driftcheck Figma plugin.

## Tools

| Tool | Use |
|---|---|
| `list_devices` | Connected Android devices and booted iOS simulators |
| `get_design_specs` | Sizes, colors, texts and fonts of the frame |
| `capture_screen` | Capture + app elements with their `AutomationId` |
| `compare_screen` | Fidelity, differences, how to fix them and an interactive `report.html` |
| `import_design` | Imports a `.driftcheck.json` exported with the Figma plugin |

Documentation, the Figma plugin and the source code are on [GitHub](https://github.com/wilsonvargas/Driftcheck).
