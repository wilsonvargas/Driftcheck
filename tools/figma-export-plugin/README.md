# Figma plugin: Driftcheck Export

Exports a frame to compare it with Driftcheck **without using the Figma REST API**: it needs no token and uses no quota
(on some accounts the API allows very few calls and then blocks for days).

It generates a `<frame>.driftcheck.json` file with:

- the frame JSON, in the same format as `GET /v1/files/{key}/nodes` (`exportAsync({ format: "JSON_REST_V1" })`);
- the frame rendered at 2x, in base64;
- the frame id, the Figma file name and the export date.

## Usage

1. In Figma, select the frame (the screen you're going to compare) and run **Driftcheck Export**.
2. The file downloads on its own. Leave it in the **Downloads** folder.
3. Ask your assistant to compare the app with that frame's link (*Copy link to selection*), as usual:
   Driftcheck finds the export in Downloads and doesn't call the API.

If the file ended up in another folder, ask the assistant to import it by its path (`import_design` tool).
If the design changes, export the frame again: the newest export replaces the previous one.

## Installing it

**While it isn't published** (for development and testing): in the **Figma desktop app**,
*Plugins → Development → Import plugin from manifest…* and pick `manifest.json` from this folder. Figma in the
browser doesn't allow importing development plugins.

**For end users** it has to be published to the Figma Community (*Plugins → Development → Publish*), or as a
private organization plugin (requires the Organization plan). Before publishing, replace the `id` in
`manifest.json` with the one Figma assigns when the plugin is created.

## Details

- **File key**: Figma only provides it (`figma.fileKey`) to private or development plugins. When published to the
  Community it is empty and Driftcheck recognizes the frame by its id alone (the link's `node-id`). If two
  different files had exports of a frame with the same id, the most recent one is used.
- **Permissions**: `networkAccess` is set to `none`: the plugin doesn't connect to any server. Everything stays in
  the file you download.
- **Size**: a 2x render of a phone screen weighs between 100 KB and 1 MB.
