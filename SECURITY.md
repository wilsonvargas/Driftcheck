# Security policy

## Reporting a vulnerability

Please don't open a public issue for security problems. Report them privately through
[GitHub's private vulnerability reporting](https://github.com/wilsonvargas/Driftcheck/security/advisories/new).

Include what you found, how to reproduce it and what impact you think it has. You'll get an answer as soon as
possible, and a fix will be published before the details are made public.

## Scope

Driftcheck runs locally: it reads your `FIGMA_TOKEN`, calls the Figma REST API, runs `adb` and `xcrun`, and writes
captures and reports to a local folder. Issues that are especially relevant:

- leaking the Figma token (in logs, reports or error messages);
- running unexpected commands through `adb`, `xcrun` or the iOS accessibility helper;
- reading or writing files outside the output, cache and exports folders;
- the Figma plugin sending data over the network (it is declared with `networkAccess: none`).

## Supported versions

Only the latest release receives security fixes.
