# Plugin de Figma: Driftcheck Export

Exporta un frame para compararlo con Driftcheck **sin usar la REST API de Figma**: no necesita token ni gasta cupo
(en algunas cuentas la API permite muy pocas llamadas y después bloquea por días).

Genera un archivo `<frame>.driftcheck.json` con:

- el JSON del frame, con el mismo formato que `GET /v1/files/{key}/nodes` (`exportAsync({ format: "JSON_REST_V1" })`);
- el render del frame a 2x, en base64;
- el id del frame, el nombre del archivo de Figma y la fecha del export.

## Uso

1. En Figma, selecciona el frame (la pantalla que vas a comparar) y ejecuta **Driftcheck Export**.
2. El archivo se descarga solo. Déjalo en la carpeta **Descargas**.
3. Pídele a tu asistente que compare la app con el link de ese frame (*Copy link to selection*), como siempre:
   Driftcheck encuentra el export en Descargas y no llama a la API.

Si el archivo quedó en otra carpeta, pídele al asistente que lo importe con su ruta (herramienta `import_design`).
Si el diseño cambia, vuelve a exportar el frame: el export más nuevo reemplaza al anterior.

## Instalarlo

**Mientras no esté publicado** (para desarrollo y pruebas): en la **app de escritorio de Figma**,
*Plugins → Development → Import plugin from manifest…* y elige `manifest.json` de esta carpeta. Figma en el
navegador no permite importar plugins en desarrollo.

**Para usuarios finales** hay que publicarlo en la Figma Community (*Plugins → Development → Publish*), o como
plugin privado de una organización (requiere plan Organization). Antes de publicar, cambia el `id` del
`manifest.json` por el que Figma asigna al crear el plugin.

## Detalles

- **Clave del archivo**: Figma solo se la da (`figma.fileKey`) a plugins privados o en desarrollo. Publicado en la
  Community llega vacía y Driftcheck reconoce el frame solo por su id (`node-id` del link). Si dos archivos
  distintos tuvieran exports de un frame con el mismo id, se usa el más reciente.
- **Permisos**: `networkAccess` está en `none`: el plugin no se conecta a ningún servidor. Todo queda en el
  archivo que descargas.
- **Tamaño**: el render a 2x de una pantalla de teléfono pesa entre 100 KB y 1 MB.
