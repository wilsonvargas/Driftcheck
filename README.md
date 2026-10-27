<p align="center">
  <img src="https://raw.githubusercontent.com/wilsonvargas/Driftcheck/master/assets/icon-512.png" alt="Driftcheck" width="112">
</p>

<h1 align="center">Driftcheck</h1>

<p align="center">
  <strong>Catch UI drift before your users do.</strong><br>
  Your AI agent compares the app on the emulator with its Figma design, then fixes the differences.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Driftcheck"><img src="https://img.shields.io/nuget/v/Driftcheck.svg" alt="NuGet"></a>
  <a href="https://github.com/wilsonvargas/Driftcheck/actions/workflows/ci.yml"><img src="https://github.com/wilsonvargas/Driftcheck/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/wilsonvargas/Driftcheck/blob/master/LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License: MIT"></a>
</p>

<p align="center">
  <a href="https://wilsonvargas.github.io/Driftcheck/demo/">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/wilsonvargas/Driftcheck/master/assets/report-dark.png">
      <img src="https://raw.githubusercontent.com/wilsonvargas/Driftcheck/master/assets/report-light.png" alt="Driftcheck report: the Figma design and the app under a slider, a 75 % fidelity score and the list of differences with how to fix each one">
    </picture>
  </a>
  <br>
  <sub><a href="https://wilsonvargas.github.io/Driftcheck/demo/"><strong>Open the interactive report →</strong></a> · Design: <a href="https://www.figma.com/community/file/1428007748789833465/payment-app-free-ui-kit-by-marvilo">Paylo kit by Marvilo</a> (CC BY 4.0)</sub>
</p>

Driftcheck is a local [MCP](https://modelcontextprotocol.io) server. It captures the screen of an Android device or the iOS Simulator, compares it 1:1 with the Figma frame and gives your agent what it needs to close the gap:

- **What's different**: position, size, text, font, colors, corner radius, borders, and missing icons or images, including layers with no text, found by how they look.
- **How to fix it**, written with your framework's properties: `Margin`, `android:layout_marginTop`, `Modifier.padding`, `.frame`, `EdgeInsets`…
- **A fidelity score** that tracks progress from one fix to the next, and an HTML report to share with your team.

It works with real-world design kits, where layers are called `Frame` or `Text`, and without a Figma token through the [Driftcheck Figma plugin](#figma-api-limits).

<!-- mcp-name: io.github.wilsonvargas/driftcheck -->

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), plus `adb` for Android or Xcode for the iOS Simulator. Add Driftcheck to your MCP client; `dotnet dnx` downloads it from NuGet the first time:

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

Then run your app and ask your agent:

> Compare the app on the emulator with https://www.figma.com/design/…?node-id=… and fix the differences.

## What your agent gets back

`compare_screen` answers with a report the agent can act on (excerpt from the [demo](https://wilsonvargas.github.io/Driftcheck/demo/)):

```markdown
**Fidelity: 75 %** — 15 of 20 elements as in Figma

| Element                    | Property   | Figma   | App     |
|----------------------------|------------|---------|---------|
| Nice to meet you           | height     | 31.5    | 25.5    |
| Text "Sign up with Google" | x          | 131.9   | 125     |
| Shape "Sign up"            | background | #DBC6F8 | #BAD1BB |

## How to fix it

- **Nice to meet you**
  - The glyphs don't measure the same as in Figma; check the font: in Figma it is Inter 32 weight 600.
    `FontFamily="Inter"`, `FontSize="32"`, `CharacterSpacing`
- **Shape "Sign up"**
  - `BackgroundColor="#9E56FF"` (it renders as #DBC6F8 in the design: the layer or a container has opacity)

## Design non-text layers not found in the app (1)

- Icon (x=108.2 y=392.3 w=20 h=20) — Missing a 20×20 dp icon (the Figma layer is 24×24)
```

Along with it come `report.html` (the interactive report above: slider, opacity, diff and side by side), `side_by_side.png` and `diff.png`.

## Supported platforms

| Device | How it reads the screen |
|---|---|
| Android emulator or device | `adb`: `screencap` + `uiautomator dump` (the `AutomationId` is the `resource-id`) |
| iOS Simulator (macOS) | `simctl` + an XCUITest helper that reads the accessibility tree (the `AutomationId` is the `accessibilityIdentifier`) |

Fix suggestions are written for **.NET MAUI**, **Android views**, **Jetpack Compose**, **UIKit**, **SwiftUI** and **Flutter**, or in plain terms if you don't pass a `framework`. The comparison itself doesn't depend on the framework: it works from what's on the screen.

## MCP tools

| Tool | Use |
|---|---|
| `list_devices` | Connected Android devices and booted iOS simulators |
| `get_design_specs` | Sizes, colors, texts and fonts of the frame (before writing the UI code) |
| `capture_screen` | Capture + app elements with their `AutomationId` |
| `compare_screen` | Full design vs. app comparison: fidelity, differences, how to fix them and `report.html` |
| `import_design` | Imports a `.driftcheck.json` from the Figma plugin that ended up outside Downloads |

## How it works

Driftcheck downloads the frame once and keeps it in a local cache, recomputes Figma's auto layout, and matches layers to app elements by `AutomationId`, by visible text or by position. Texts are compared by their visible glyphs rather than their boxes, non-text layers are found in the screenshot by how they look, and a device slightly wider or narrower than the frame is handled by anchoring each element where it fits best. The full walkthrough is in [How Driftcheck compares](https://github.com/wilsonvargas/Driftcheck/blob/master/docs/how-it-compares.md).

## Figma API limits

Figma limits how many calls a token can make, and on some seat types the quota is very low: once it runs out, it responds `429` with waits of days. That's why:

- The JSON and PNG of each frame are stored in a local cache and reused on every comparison. Pass `refresh=true` to `get_design_specs` or `compare_screen` to download a frame that changed in Figma.
- If Figma responds `429`, the error says how long to wait.
- **Without a token or quota**, the Figma plugin in `tools/figma-export-plugin` exports the frame (JSON + 2x render) to a `.driftcheck.json` file without using the REST API. If it lands in **Downloads**, Driftcheck picks it up on its own from the frame link; if it lands in another folder, import it with `import_design`. See [the plugin README](https://github.com/wilsonvargas/Driftcheck/blob/master/tools/figma-export-plugin/README.md).

## Requirements

- .NET 10 SDK
- For Android: Android SDK with `adb` (detected from `ANDROID_HOME`, `ANDROID_SDK_ROOT` or the default paths on Windows and macOS)
- For iOS: macOS with Xcode and a booted simulator. No idb or WebDriverAgent needed
- A `FIGMA_TOKEN` environment variable with a Figma Personal Access Token (scope *File content: Read-only*), or the Figma plugin to work without the API
- Optional: `DRIFTCHECK_OUTPUT` to change the output folder (default: `driftcheck` in the temp folder)
- Optional: `DRIFTCHECK_EXPORTS` to change where plugin exports are looked up (default: Downloads)

## Current limitations

- iOS on the simulator only (not on physical devices).
- Visible area only (no scrolling).
- Doesn't measure font size in the app (uiautomator and XCUITest don't expose it); `get_design_specs` provides it as a reference.

## Running from source

```bash
git clone https://github.com/wilsonvargas/Driftcheck.git
cd Driftcheck
dotnet build src/Driftcheck.Mcp
```

The `.mcp.json` at the root registers the server for MCP clients that read it when opening this folder. To use it from another project, add the server to your client's MCP configuration:

```json
{
  "mcpServers": {
    "driftcheck": {
      "command": "dotnet",
      "args": ["<repo path>/src/Driftcheck.Mcp/bin/Debug/net10.0/Driftcheck.Mcp.dll"]
    }
  }
}
```

### Repository layout

- `src/Driftcheck.Mcp` — MCP server
- `tools/ios-accessibility-helper` — UI test that dumps the iOS Simulator accessibility tree (copied next to the server on build)
- `tools/figma-export-plugin` — Figma plugin that exports a frame without the REST API
- `samples/PayloMaui` — MAUI test app with screens from the [Paylo kit by Marvilo](https://www.figma.com/community/file/1428007748789833465/payment-app-free-ui-kit-by-marvilo) (CC BY 4.0)
  For the iOS Simulator: `dotnet build -f net10.0-ios -p:TargetFrameworks=net10.0-ios` (limiting `TargetFrameworks` avoids requiring the Mac Catalyst workload), then `xcrun simctl install booted bin/Debug/net10.0-ios/iossimulator-arm64/PayloMaui.app` and `xcrun simctl launch booted com.companyname.paylomaui`. The Xcode version must be the one the iOS workload asks for; if several are installed, pick it with `DEVELOPER_DIR`.
- `samples/PayloNative` — the same sign-up screen with native Android views (Java, no MAUI or AndroidX), to check that the comparison doesn't depend on MAUI. `./build.ps1` builds it with the SDK tools (no Gradle) and installs it on the emulator
- `samples/PayloIos` — the same screen with native iOS views (UIKit in Swift, no MAUI or storyboard). `./build.sh` builds it with `swiftc` (no Xcode project) and installs it on the booted simulator
- `tests/Driftcheck.Tests` — comparer tests with synthetic screens
- `docs` — the detailed comparison walkthrough and the demo report published on GitHub Pages

To compare the sample apps, open the [Paylo kit](https://www.figma.com/community/file/1428007748789833465/payment-app-free-ui-kit-by-marvilo) in the Figma Community, duplicate it to your drafts, select the frame **Component 113** (the sign-up screen) and use *Copy link to selection*. Then run a sample on the emulator or simulator and ask your agent to compare it with that link.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](https://github.com/wilsonvargas/Driftcheck/blob/master/CONTRIBUTING.md) for how to build, test and propose changes, and [SECURITY.md](https://github.com/wilsonvargas/Driftcheck/blob/master/SECURITY.md) to report a vulnerability privately.

## License

Driftcheck is released under the [MIT License](https://github.com/wilsonvargas/Driftcheck/blob/master/LICENSE). Third-party material in the sample apps keeps its own license: see [THIRD_PARTY_NOTICES.md](https://github.com/wilsonvargas/Driftcheck/blob/master/THIRD_PARTY_NOTICES.md).
