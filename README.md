# Driftcheck

Servidor MCP local que compara la pantalla de una app móvil (Android, o el simulador de iOS en macOS) con su diseño en Figma, para que un agente de IA pueda verificar si lo que implementó se ve como el diseño.

## Cómo compara

1. Descarga el frame de Figma (propiedades de cada capa + PNG) con la REST API y lo guarda en una caché local: cada frame se descarga una sola vez (ver *Límites de la API de Figma*).
   Las cajas del JSON se recalculan con las reglas de auto layout de cada frame (dirección, padding, separación, alineación, fijo / hug / fill, min/max), sin contar los hijos ocultos: dentro de instancias de componentes, Figma puede devolver cajas desactualizadas (un texto en x=129 que el render muestra en x=49 porque dos íconos ocultos "seguían ocupando lugar").
2. Captura el dispositivo:
   - **Android**: `adb` (`screencap` + `uiautomator dump`). El `AutomationId` es el `resource-id`.
   - **Simulador de iOS** (solo macOS): `xcrun simctl io screenshot` y el árbol de accesibilidad de la app en primer plano, que lee un UI test de XCUITest (`tools/ios-accessibility-helper`). Se compila solo la primera vez (unos 15 s); después cada captura tarda unos 3 s. El `AutomationId` es el `accessibilityIdentifier`.
3. Compara 1:1 en dp o pt (1 unidad de Figma = 1 dp en Android = 1 pt en iOS). Si el dispositivo es algo más ancho o angosto que el frame (411 vs 430), cada elemento se compara anclado a la izquierda, a la derecha, centrado o estirado, según cuál se ajuste mejor. Solo si la diferencia de ancho supera ~33 % (diseño @2x, tablet) se escala el diseño.
4. Empareja capas de Figma con elementos de la app en tres pasadas:
   1. **Nombre de capa = `AutomationId`** (sin distinguir espacios ni mayúsculas: `Login Button` ↔ `LoginButton`), solo si el nombre no se repite en el diseño.
   2. **Texto visible**: la capa "Sign up" con el botón que dice "Sign up". Es lo que funciona con kits reales, donde las capas se llaman `Text` o `Auto Layout`.
   3. **Posición**: una capa de texto sobre un elemento con otro texto se empareja igual, para reportar el texto distinto.

   Los ids de Android (`android:id/content`, etc.) y del marco de AppCompat (`action_bar_root`, etc.) se ignoran.
5. En los textos compara las letras visibles, medidas en el render del diseño y en la captura, en vez de las cajas: la capa de Figma se ajusta al texto, pero en la app el texto suele estar dentro de una vista más ancha (un `TextView` de toda la fila, un botón). En el ancho de las letras tolera un 2.5 % (la misma fuente se dibuja un poco distinta en Figma y en Android); una fuente distinta se sigue reportando.
6. Estima el desplazamiento global (barra de estado, notch) con la mediana y reporta diferencias de posición, tamaño, texto, color de fondo y color de los textos (medido igual que las letras, así cuenta la opacidad).
7. Genera `side_by_side.png`, `diff.png` y `report.md`.

En `diff.png`: verde = coincide, naranja = difiere, punteado azul = dónde debería estar según Figma, magenta = píxeles distintos. Para el magenta, cada elemento emparejado del diseño se superpone sobre su posición en la app si coincide (así marca solo diferencias de forma o color) o donde debería estar si no; un corrimiento de un píxel no cuenta como diferencia.

## Herramientas MCP

| Herramienta | Uso |
|---|---|
| `list_devices` | Dispositivos Android conectados y simuladores de iOS encendidos |
| `get_design_specs` | Medidas, colores, textos y fuentes del frame (antes de escribir el XAML) |
| `capture_screen` | Captura + elementos de la app con su `AutomationId` |
| `compare_screen` | Comparación completa diseño vs. app |
| `import_design` | Importa un `.driftcheck.json` del plugin de Figma que quedó fuera de Descargas |

## Límites de la API de Figma

Figma limita cuántas llamadas puede hacer un token, y en algunos tipos de asiento el cupo es muy bajo: al agotarse, responde `429` con esperas de días. Por eso:

- El JSON y el PNG de cada frame se guardan en `driftcheck/cache` dentro de la carpeta temporal (`%TEMP%` en Windows, `$TMPDIR` en macOS) y se reutilizan en cada comparación.
- `get_design_specs` y `compare_screen` aceptan `refresh=true` para volver a descargar un frame que cambió en Figma. Úsalo solo cuando haga falta.
- Si Figma responde `429`, el error dice cuánto hay que esperar.
- **Sin token o sin cupo**, el plugin de Figma de `tools/figma-export-plugin` exporta el frame (JSON + render a 2x) a un archivo `.driftcheck.json` sin usar la REST API. Si queda en **Descargas**, `get_design_specs` y `compare_screen` lo usan solos con el link del frame; si queda en otra carpeta, se importa con `import_design`. Volver a exportar el frame actualiza el diseño. Ver [el README del plugin](tools/figma-export-plugin/README.md).

## Requisitos

- .NET 10 SDK
- Para Android: Android SDK con `adb` (se detecta en `ANDROID_HOME`, `ANDROID_SDK_ROOT` o las rutas por defecto de Windows y macOS)
- Para iOS: macOS con Xcode y un simulador encendido. No hace falta idb ni WebDriverAgent
- Variable de entorno `FIGMA_TOKEN` con un Personal Access Token de Figma (alcance *File content: Read-only*), o el plugin de Figma de `tools/figma-export-plugin` para trabajar sin la API
- Opcional: `DRIFTCHECK_OUTPUT` para cambiar la carpeta de salida (por defecto `driftcheck` en la carpeta temporal)
- Opcional: `DRIFTCHECK_EXPORTS` para cambiar dónde se buscan los exports del plugin (por defecto, Descargas)

## Uso

```bash
dotnet build src/Driftcheck.Mcp
```

El `.mcp.json` de la raíz registra el servidor para los clientes MCP que lo leen al abrir esta carpeta. Para usarlo desde otro proyecto, agrega el servidor a la configuración MCP de tu cliente:

```json
{
  "mcpServers": {
    "driftcheck": {
      "command": "dotnet",
      "args": ["<ruta del repo>/src/Driftcheck.Mcp/bin/Debug/net10.0/Driftcheck.Mcp.dll"]
    }
  }
}
```

## Estructura

- `src/Driftcheck.Mcp` — servidor MCP
- `tools/ios-accessibility-helper` — UI test que vuelca el árbol de accesibilidad del simulador de iOS (se copia junto al servidor al compilar)
- `samples/PayloMaui` — app MAUI de prueba con pantallas del kit [Paylo de Marvilo](https://www.figma.com/community/file/1428007748789833465/payment-app-free-ui-kit-by-marvilo) (CC BY 4.0)
  Para el simulador de iOS: `dotnet build -f net10.0-ios -p:TargetFrameworks=net10.0-ios` (limitar `TargetFrameworks` evita pedir el workload de Mac Catalyst) y luego `xcrun simctl install booted bin/Debug/net10.0-ios/iossimulator-arm64/PayloMaui.app` y `xcrun simctl launch booted com.companyname.paylomaui`. La versión de Xcode tiene que ser la que pide el workload de iOS; si hay varias instaladas, se elige con `DEVELOPER_DIR`.
- `samples/PayloNative` — la misma pantalla de registro con vistas nativas de Android (Java, sin MAUI ni AndroidX), para comprobar que la comparación no depende de MAUI. `./build.ps1` la compila con las herramientas del SDK (sin Gradle) y la instala en el emulador
- `tests/Driftcheck.Tests` — pruebas del comparador con pantallas sintéticas

## Limitaciones actuales (MVP)

- iOS solo en el simulador (no en dispositivos físicos).
- Solo el área visible (sin scroll).
- No mide tamaño de fuente en la app (uiautomator y XCUITest no lo exponen); `get_design_specs` lo da como referencia.

## Licencia

Software propietario, todos los derechos reservados (ver `LICENSE`). Los componentes de terceros de las apps de ejemplo conservan su licencia: ver `THIRD_PARTY_NOTICES.md`.
