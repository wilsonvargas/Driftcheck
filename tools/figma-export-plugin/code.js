// Exporta el frame seleccionado con el mismo formato que GET /v1/files/{key}/nodes?ids={id},
// para dejarlo en la caché de Driftcheck sin gastar cupo de la REST API.
figma.showUI(__html__, { width: 480, height: 420 });

async function exportSelection() {
  const node = figma.currentPage.selection[0];
  if (!node) {
    figma.ui.postMessage({ error: "Selecciona un frame y vuelve a ejecutar el plugin." });
    return;
  }

  const rest = await node.exportAsync({ format: "JSON_REST_V1" });
  // Según la versión, el resultado ya trae { document, components, ... } o es directamente el nodo.
  const entry = rest && rest.document ? rest : { document: rest };
  const json = JSON.stringify({ name: figma.root.name, nodes: { [node.id]: entry } });

  // figma.fileKey solo está disponible en plugins privados o en desarrollo.
  const fileKey = figma.fileKey || "FILEKEY";
  const fileName = `${fileKey}_${node.id.replace(/:/g, "-")}.json`;

  figma.ui.postMessage({ json, fileName, nodeName: node.name, nodeId: node.id });
}

exportSelection().catch((e) => figma.ui.postMessage({ error: String(e) }));
