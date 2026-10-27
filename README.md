<img src="https://raw.githubusercontent.com/wilsonvargas/Driftcheck/master/assets/icon-512.png" alt="" width="72" align="right">

# Driftcheck

[![CI](https://github.com/wilsonvargas/Driftcheck/actions/workflows/ci.yml/badge.svg)](https://github.com/wilsonvargas/Driftcheck/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Driftcheck.svg)](https://www.nuget.org/packages/Driftcheck)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/wilsonvargas/Driftcheck/blob/master/LICENSE)

A local MCP server that compares a mobile app's screen (Android, or the iOS Simulator on macOS) with its design in Figma, so an AI agent can check whether what it built looks like the design.

<!-- mcp-name: io.github.wilsonvargas/driftcheck -->

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), plus `adb` for Android or Xcode for the iOS Simulator (see [Requirements](#requirements)). Add Driftcheck to your MCP client; `dotnet dnx` downloads it from NuGet the first time:

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

Then run your app on an emulator, device or simulator and ask your agent something like *"compare the app on the emulator with https://www.figma.com/design/…?node-id=…"*. Without a Figma token, use the [Figma plugin](#figma-api-limits) instead.

## How it compares

1. Downloads the Figma frame (properties of every layer + PNG) with the REST API and stores it in a local cache: each frame is downloaded only once (see *Figma API limits*).
   Boxes from the JSON are recomputed with each frame's auto layout rules (direction, padding, spacing, alignment, fixed / hug / fill, min/max), ignoring hidden children: inside component instances, Figma can return stale boxes (a text at x=129 that the render shows at x=49 because two hidden icons "were still taking up space").
2. Captures the device:
   - **Android**: `adb` (`screencap` + `uiautomator dump`). The `AutomationId` is the `resource-id`.
   - **iOS Simulator** (macOS only): `xcrun simctl io screenshot` and the accessibility tree of the foreground app, read by an XCUITest UI test (`tools/ios-accessibility-helper`). It is built only the first time (about 15 s); after that each capture takes about 3 s. The `AutomationId` is the `accessibilityIdentifier`.
   - It also reads which app is in the foreground, its version and when it was installed (in MAUI Debug, when Fast Deployment copied the assemblies). If `compare_screen` gets a `projectPath` and the project has files newer than the installed app, the report warns that the app needs to be rebuilt and reinstalled.
   - If the capture doesn't look like the design (more than half of the pixels differ, or no element matched), it is almost always a loading screen: the .NET splash stays on top for a few seconds even though `uiautomator` already lists the app. Driftcheck waits and captures again up to 3 times; if nothing changes, the report says so.
3. Compares 1:1 in dp or pt (1 Figma unit = 1 dp on Android = 1 pt on iOS). If the device is somewhat wider or narrower than the frame (411 vs 430), each element is compared anchored left, right, centered or stretched, whichever fits best; a layer that grows (Fill) in an auto layout row stretches only by its share (half, if two layers grow). The design is scaled only if the width difference exceeds ~33 % (@2x designs, tablets).
4. Matches Figma layers to app elements in three passes:
   1. **Layer name = `AutomationId`** (ignoring spaces and case: `Login Button` ↔ `LoginButton`), only if the name is unique in the design.
   2. **Visible text**: the "Sign up" layer with the button that says "Sign up". This is what works with real-world kits, where layers are named `Text` or `Auto Layout`.
   3. **Position**: a text layer over an element with a different text is still matched, so the different text is reported.

   Android ids (`android:id/content`, etc.) and AppCompat frame ids (`action_bar_root`, etc.) are ignored.
5. For texts it compares the visible glyphs, measured in the design render and in the capture, instead of the boxes: the Figma layer hugs the text, but in the app the text usually sits inside a wider view (a full-row `TextView`, a button). Glyph width has a 2.5 % tolerance (the same font renders slightly differently in Figma and on Android); a different font is still reported.
6. **Visible non-text layers** (buttons, fields, lines, icons, images) are not in the accessibility tree, so they are found in the capture by how they look, whatever their name (`Frame`, `Vector`): the same algorithm measures each one in the design PNG and in the capture (the non-background blob that overlaps most with where it should be) and compares the box, fill color, corner radius, border and, for icons and images, the content (and the color, if the icon is a single color). A shape's box is its fill plus its border, without the shadow. If it is missing, it is reported with what needs to be added. Status bar and home indicator layers drawn in the design are ignored, and the screen background is compared separately.
7. Estimates the global offset (status bar, notch) with the median and reports differences in position, size, text, background color and text color (measured the same way as glyphs, so opacity counts).
8. Computes **fidelity**: elements with no differences (matched texts, non-text layers and the background) over all compared elements plus those missing from the app. The report compares it with the previous comparison of the same frame on the same device (`before 72 % (+16)`), to track progress from one fix to the next.
9. Suggests what to change for every difference, using the framework's properties if `framework` is passed (`maui`, `android`, `compose`, `uikit`, `swiftui` or `flutter`): top `Margin` −12, `BackgroundColor="#0D001F"` (the Figma color, even if the render looks lighter because of opacity), `StrokeShape="RoundRectangle 8"`, the design's font if the glyphs don't measure the same. These are hints: the same difference can come from the element or from its container.
10. Generates `report.html`, `report.md`, `side_by_side.png` and `diff.png`. `report.html` is a single file with the images embedded, to open or share: design/app slider, transparency, diff, side by side and the list of differences with their suggestions; selecting one highlights it in the image.

In `diff.png`: green = matches, orange = differs, dotted blue = where Figma says it should be, magenta = different pixels. For the magenta layer, each matched design element is overlaid on its position in the app if it matches (so it only marks shape or color differences) or where it should be if it doesn't; a one-pixel shift doesn't count as a difference.

## MCP tools

| Tool | Use |
|---|---|
| `list_devices` | Connected Android devices and booted iOS simulators |
| `get_design_specs` | Sizes, colors, texts and fonts of the frame (before writing the UI code) |
| `capture_screen` | Capture + app elements with their `AutomationId` |
| `compare_screen` | Full design vs. app comparison: fidelity, differences, how to fix them and `report.html` |
| `import_design` | Imports a `.driftcheck.json` from the Figma plugin that ended up outside Downloads |

## Figma API limits

Figma limits how many calls a token can make, and on some seat types the quota is very low: once it runs out, it responds `429` with waits of days. That's why:

- The JSON and PNG of each frame are stored in `driftcheck/cache` inside the temp folder (`%TEMP%` on Windows, `$TMPDIR` on macOS) and reused on every comparison.
- `get_design_specs` and `compare_screen` accept `refresh=true` to download again a frame that changed in Figma. Use it only when needed.
- If Figma responds `429`, the error says how long to wait.
- **Without a token or quota**, the Figma plugin in `tools/figma-export-plugin` exports the frame (JSON + 2x render) to a `.driftcheck.json` file without using the REST API. If it lands in **Downloads**, `get_design_specs` and `compare_screen` pick it up on their own from the frame link; if it lands in another folder, import it with `import_design`. Exporting the frame again updates the design. See [the plugin README](https://github.com/wilsonvargas/Driftcheck/blob/master/tools/figma-export-plugin/README.md).

## Requirements

- .NET 10 SDK
- For Android: Android SDK with `adb` (detected from `ANDROID_HOME`, `ANDROID_SDK_ROOT` or the default paths on Windows and macOS)
- For iOS: macOS with Xcode and a booted simulator. No idb or WebDriverAgent needed
- A `FIGMA_TOKEN` environment variable with a Figma Personal Access Token (scope *File content: Read-only*), or the Figma plugin in `tools/figma-export-plugin` to work without the API
- Optional: `DRIFTCHECK_OUTPUT` to change the output folder (default: `driftcheck` in the temp folder)
- Optional: `DRIFTCHECK_EXPORTS` to change where plugin exports are looked up (default: Downloads)

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

## Repository layout

- `src/Driftcheck.Mcp` — MCP server
- `tools/ios-accessibility-helper` — UI test that dumps the iOS Simulator accessibility tree (copied next to the server on build)
- `tools/figma-export-plugin` — Figma plugin that exports a frame without the REST API
- `samples/PayloMaui` — MAUI test app with screens from the [Paylo kit by Marvilo](https://www.figma.com/community/file/1428007748789833465/payment-app-free-ui-kit-by-marvilo) (CC BY 4.0)
  For the iOS Simulator: `dotnet build -f net10.0-ios -p:TargetFrameworks=net10.0-ios` (limiting `TargetFrameworks` avoids requiring the Mac Catalyst workload), then `xcrun simctl install booted bin/Debug/net10.0-ios/iossimulator-arm64/PayloMaui.app` and `xcrun simctl launch booted com.companyname.paylomaui`. The Xcode version must be the one the iOS workload asks for; if several are installed, pick it with `DEVELOPER_DIR`.
- `samples/PayloNative` — the same sign-up screen with native Android views (Java, no MAUI or AndroidX), to check that the comparison doesn't depend on MAUI. `./build.ps1` builds it with the SDK tools (no Gradle) and installs it on the emulator
- `samples/PayloIos` — the same screen with native iOS views (UIKit in Swift, no MAUI or storyboard). `./build.sh` builds it with `swiftc` (no Xcode project) and installs it on the booted simulator
- `tests/Driftcheck.Tests` — comparer tests with synthetic screens

To compare the sample apps, open the [Paylo kit](https://www.figma.com/community/file/1428007748789833465/payment-app-free-ui-kit-by-marvilo) in the Figma Community, duplicate it to your drafts, select the frame **Component 113** (the sign-up screen) and use *Copy link to selection*. Then run a sample on the emulator or simulator and ask your agent to compare it with that link.

## Current limitations

- iOS on the simulator only (not on physical devices).
- Visible area only (no scrolling).
- Doesn't measure font size in the app (uiautomator and XCUITest don't expose it); `get_design_specs` provides it as a reference.

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](https://github.com/wilsonvargas/Driftcheck/blob/master/CONTRIBUTING.md) for how to build, test and propose changes, and [SECURITY.md](https://github.com/wilsonvargas/Driftcheck/blob/master/SECURITY.md) to report a vulnerability privately.

## License

Driftcheck is released under the [MIT License](https://github.com/wilsonvargas/Driftcheck/blob/master/LICENSE). Third-party material in the sample apps keeps its own license: see [THIRD_PARTY_NOTICES.md](https://github.com/wilsonvargas/Driftcheck/blob/master/THIRD_PARTY_NOTICES.md).
