# FigmaCompare

Servidor MCP local que compara la pantalla de una app móvil (Android) con su diseño en Figma, para que un agente de IA pueda verificar si lo que implementó se ve como el diseño.

## Cómo compara

1. Descarga el frame de Figma (propiedades de cada capa + PNG) con la REST API y lo guarda en una caché local: cada frame se descarga una sola vez (ver *Límites de la API de Figma*).
2. Captura el dispositivo con `adb` (`screencap` + `uiautomator dump`).
3. Compara 1:1 en dp (1 unidad de Figma = 1 dp). Si el dispositivo es algo más ancho o angosto que el frame (411 vs 430), cada elemento se compara anclado a la izquierda, a la derecha, centrado o estirado, según cuál se ajuste mejor. Solo si la diferencia de ancho supera ~33 % (diseño @2x, tablet) se escala el diseño.
4. Empareja capas de Figma con elementos de la app en tres pasadas:
   1. **Nombre de capa = `AutomationId`** (sin distinguir espacios ni mayúsculas: `Login Button` ↔ `LoginButton`), solo si el nombre no se repite en el diseño.
   2. **Texto visible**: la capa "Sign up" con el botón que dice "Sign up". Es lo que funciona con kits reales, donde las capas se llaman `Text` o `Auto Layout`.
   3. **Posición**: una capa de texto sobre un elemento con otro texto se empareja igual, para reportar el texto distinto.

   Los ids de Android (`android:id/content`, etc.) se ignoran.
5. Estima el desplazamiento global (barra de estado, notch) con la mediana y reporta diferencias de posición, tamaño, texto y color de fondo.
6. Genera `side_by_side.png`, `diff.png` y `report.md`.

En `diff.png`: verde = coincide, naranja = difiere, punteado azul = dónde debería estar según Figma, magenta = píxeles distintos.

## Herramientas MCP

| Herramienta | Uso |
|---|---|
| `list_devices` | Dispositivos Android conectados |
| `get_design_specs` | Medidas, colores, textos y fuentes del frame (antes de escribir el XAML) |
| `capture_screen` | Captura + elementos de la app con su `AutomationId` |
| `compare_screen` | Comparación completa diseño vs. app |

## Límites de la API de Figma

Figma limita cuántas llamadas puede hacer un token, y en algunos tipos de asiento el cupo es muy bajo: al agotarse, responde `429` con esperas de días. Por eso:

- El JSON y el PNG de cada frame se guardan en `%TEMP%\figma-compare\cache` y se reutilizan en cada comparación.
- `get_design_specs` y `compare_screen` aceptan `refresh=true` para volver a descargar un frame que cambió en Figma. Úsalo solo cuando haga falta.
- Si Figma responde `429`, el error dice cuánto hay que esperar.

## Requisitos

- .NET 10 SDK
- Android SDK con `adb` (se detecta en `ANDROID_HOME`, `ANDROID_SDK_ROOT` o las rutas por defecto)
- Variable de entorno `FIGMA_TOKEN` con un Personal Access Token de Figma (alcance *File content: Read-only*)
- Opcional: `FIGMA_COMPARE_OUTPUT` para cambiar la carpeta de salida (por defecto `%TEMP%\figma-compare`)

## Uso

```bash
dotnet build src/FigmaCompare.Mcp
```

El `.mcp.json` de la raíz registra el servidor para los clientes MCP que lo leen al abrir esta carpeta. Para usarlo desde otro proyecto, agrega el servidor a la configuración MCP de tu cliente:

```json
{
  "mcpServers": {
    "figma-compare": {
      "command": "dotnet",
      "args": ["D:/Proyectos/FigmaCompare/src/FigmaCompare.Mcp/bin/Debug/net10.0/FigmaCompare.Mcp.dll"]
    }
  }
}
```

## Estructura

- `src/FigmaCompare.Mcp` — servidor MCP
- `samples/PayloMaui` — app MAUI de prueba con pantallas del kit [Paylo de Marvilo](https://www.figma.com/community/file/1428007748789833465/payment-app-free-ui-kit-by-marvilo) (CC BY 4.0)
- `tests/FigmaCompare.Tests` — pruebas del comparador con pantallas sintéticas

## Limitaciones actuales (MVP)

- Solo Android.
- Solo el área visible (sin scroll).
- No mide tamaño de fuente en la app (uiautomator no lo expone); `get_design_specs` lo da como referencia.
- El color se compara solo en capas que no son texto.
