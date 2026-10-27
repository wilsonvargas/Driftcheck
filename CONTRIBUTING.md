# Contributing to Driftcheck

Thanks for your interest in improving Driftcheck. Bug reports, ideas and pull requests are all welcome.

## Before you start

- For bugs, open an issue with the steps to reproduce it, the platform (Android / iOS Simulator, OS, device size) and, if you can, the generated `report.md` or `report.html`.
- For larger changes (a new platform, a new comparison, a new MCP tool), open an issue first so we can agree on the approach before you invest time in it.
- Please don't attach Figma tokens, private designs or screenshots of apps you're not allowed to share.

## Building and testing

You need the .NET 10 SDK.

```bash
dotnet build src/Driftcheck.Mcp
dotnet test tests/Driftcheck.Tests
```

The tests use synthetic screens, so they don't need a device, an emulator or a Figma token.

If the MCP server is running from `src/Driftcheck.Mcp/bin` (for example through the repo's `.mcp.json`), it locks the output folder. Either stop it, or build and test into another folder:

```bash
dotnet test tests/Driftcheck.Tests --artifacts-path <some temp folder>
```

To try a change end to end, the sample apps in `samples/` reproduce a screen of the Paylo kit on .NET MAUI, native Android and native iOS (see the README).

## Pull requests

1. Fork the repo and create a branch from `master` (`feature/...` for features, `hotfix/...` for fixes).
2. Keep each pull request focused on one change, and add or update tests for any change in the comparison.
3. Make sure `dotnet test` passes. CI runs the build and the tests on Windows for every pull request, and on Windows and macOS after merging to `master`.
4. Describe what changed and why; for comparison changes, include the before/after fidelity or differences on a sample screen when possible.

Code comments are currently written in Spanish; new comments in either Spanish or English are fine. Match the style of the surrounding code.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
