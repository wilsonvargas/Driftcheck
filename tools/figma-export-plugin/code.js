// Exporta el frame seleccionado para Driftcheck sin usar la REST API de Figma (no gasta cupo ni necesita token):
// un archivo .driftcheck.json con el JSON del frame (mismo formato que GET /v1/files/{key}/nodes) y su render a 2x.
// El servidor de Driftcheck lo encuentra solo en la carpeta Descargas.
figma.showUI(__html__, { width: 360, height: 220 });

const FORMAT_VERSION = 1;

async function exportSelection() {
  const selection = figma.currentPage.selection;
  if (selection.length !== 1) {
    figma.ui.postMessage({ error: "Select a single frame (the screen you're going to compare)." });
    return;
  }

  const node = selection[0];
  figma.ui.postMessage({ busy: `Exporting "${node.name}"…` });

  const rest = await node.exportAsync({ format: "JSON_REST_V1" });
  const png = await node.exportAsync({ format: "PNG", constraint: { type: "SCALE", value: 2 } });

  const data = {
    driftcheck: FORMAT_VERSION,
    // Figma solo da la clave del archivo a plugins privados o en desarrollo; publicado en la Community llega
    // vacía, y Driftcheck reconoce el frame por su id.
    fileKey: figma.fileKey || null,
    fileName: figma.root.name,
    nodeId: node.id,
    nodeName: node.name,
    exportedAt: new Date().toISOString(),
    // Según la versión, el resultado ya trae { document, components, ... } o es directamente el nodo.
    rest: rest && rest.document ? rest : { document: rest },
    png2x: figma.base64Encode(png),
  };

  figma.ui.postMessage({
    json: JSON.stringify(data),
    fileName: `${safeFileName(node.name)}.driftcheck.json`,
    nodeName: node.name,
  });
}

function safeFileName(name) {
  return name.replace(/[\\/:*?"<>|]+/g, "-").trim() || "frame";
}

figma.ui.onmessage = (message) => {
  if (message.type === "export") run();
};

function run() {
  exportSelection().catch((e) => figma.ui.postMessage({ error: String(e) }));
}

run();
