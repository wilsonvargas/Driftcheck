# Driftcheck

Servidor MCP local que compara la pantalla de una app móvil (Android, o el simulador de iOS en macOS) con su diseño en Figma, para que un agente de IA pueda verificar si lo que implementó se ve como el diseño.

## Cómo compara

1. Descarga el frame de Figma (propiedades de cada capa + PNG) con la REST API y lo guarda en una caché local: cada frame se descarga una sola vez (ver *Límites de la API de Figma*).
   Las cajas del JSON se recalculan con las reglas de auto layout de cada frame (dirección, padding, separación, alineación, fijo / hug / fill, min/max), sin contar los hijos ocultos: dentro de instancias de componentes, Figma puede devolver cajas desactualizadas (un texto en x=129 que el render muestra en x=49 porque dos íconos ocultos "seguían ocupando lugar").
2. Captura el dispositivo:
   - **Android**: `adb` (`screencap` + `uiautomator dump`). El `AutomationId` es el `resource-id`.
   - **Simulador de iOS** (solo macOS): `xcrun simctl io screenshot` y el árbol de accesibilidad de la app en primer plano, que lee un UI test de XCUITest (`tools/ios-accessibility-helper`). Se compila solo la primera vez (unos 15 s); después cada captura tarda unos 3 s. El `AutomationId` es el `accessibilityIdentifier`.
   - Lee también qué app está en primer plano, su versión y cuándo se instaló (en MAUI Debug, cuándo Fast Deployment copió los ensamblados). Si `compare_screen` recibe `projectPath` y el proyecto tiene archivos más nuevos que la app instalada, el reporte avisa que falta compilar e instalar.
   - Si la captura no se parece al diseño (más de la mitad de los píxeles distintos, o ningún elemento emparejado), es casi siempre una pantalla de carga: el splash de .NET sigue encima unos segundos aunque `uiautomator` ya liste la app. Se espera y se vuelve a capturar hasta 3 veces; si sigue igual, el reporte lo avisa.
3. Compara 1:1 en dp o pt (1 unidad de Figma = 1 dp en Android = 1 pt en iOS). Si el dispositivo es algo más ancho o angosto que el frame (411 vs 430), cada elemento se compara anclado a la izquierda, a la derecha, centrado o estirado, según cuál se ajuste mejor; una capa que crece (Fill) en una fila de auto layout se estira solo su parte (la mitad, si son dos las que crecen). Solo si la diferencia de ancho supera ~33 % (diseño @2x, tablet) se escala el diseño.
4. Empareja capas de Figma con elementos de la app en tres pasadas:
   1. **Nombre de capa = `AutomationId`** (sin distinguir espacios ni mayúsculas: `Login Button` ↔ `LoginButton`), solo si el nombre no se repite en el diseño.
   2. **Texto visible**: la capa "Sign up" con el botón que dice "Sign up". Es lo que funciona con kits reales, donde las capas se llaman `Text` o `Auto Layout`.
   3. **Posición**: una capa de texto sobre un elemento con otro texto se empareja igual, para reportar el texto distinto.

   Los ids de Android (`android:id/content`, etc.) y del marco de AppCompat (`action_bar_root`, etc.) se ignoran.
5. En los textos compara las letras visibles, medidas en el render del diseño y en la captura, en vez de las cajas: la capa de Figma se ajusta al texto, pero en la app el texto suele estar dentro de una vista más ancha (un `TextView` de toda la fila, un botón). En el ancho de las letras tolera un 2.5 % (la misma fuente se dibuja un poco distinta en Figma y en Android); una fuente distinta se sigue reportando.
6. Las **capas sin texto que se ven** (botones, campos, líneas, íconos, imágenes) no están en el árbol de accesibilidad, así que se buscan en la captura por cómo se ven, tengan el nombre que tengan (`Frame`, `Vector`): el mismo algoritmo mide cada una en el PNG del diseño y en la captura (la mancha que no es fondo más solapada con donde debería estar) y se comparan la caja, el color de relleno, el radio de las esquinas, el borde y, en íconos e imágenes, el contenido (y el color, si el ícono es de un color). La caja de una forma es la de su relleno con el borde, sin la sombra. Si falta, se reporta con lo que hay que agregar. Las capas de la barra de estado y del indicador de inicio que dibuja el diseño se ignoran, y el fondo de la pantalla se compara aparte.
7. Estima el desplazamiento global (barra de estado, notch) con la mediana y reporta diferencias de posición, tamaño, texto, color de fondo y color de los textos (medido igual que las letras, así cuenta la opacidad).
8. Calcula la **fidelidad**: elementos sin diferencias (textos emparejados, capas sin texto y el fondo), sobre todos los comparados más los que faltan en la app. El reporte la compara con la comparación anterior del mismo frame en el mismo dispositivo (`antes 72 % (+16)`), para seguir el avance entre una corrección y la siguiente.
9. Para cada diferencia sugiere qué cambiar, con las propiedades del framework si se pasa `framework` (`maui`, `android`, `compose`, `uikit`, `swiftui` o `flutter`): `Margin` superior −12, `BackgroundColor="#0D001F"` (el color de Figma aunque en el render se vea más claro por la opacidad), `StrokeShape="RoundRectangle 8"`, la fuente del diseño si las letras no miden lo mismo. Son pistas: la misma diferencia puede venir del elemento o de su contenedor.
10. Genera `report.html`, `report.md`, `side_by_side.png` y `diff.png`. `report.html` es un solo archivo con las imágenes adentro, para abrirlo o mandarlo: deslizador diseño/app, transparencia, diff, lado a lado y la lista de diferencias con sus sugerencias; al elegir una se resalta en la imagen.

En `diff.png`: verde = coincide, naranja = difiere, punteado azul = dónde debería estar según Figma, magenta = píxeles distintos. Para el magenta, cada elemento emparejado del diseño se superpone sobre su posición en la app si coincide (así marca solo diferencias de forma o color) o donde debería estar si no; un corrimiento de un píxel no cuenta como diferencia.

## Herramientas MCP

| Herramienta | Uso |
|---|---|
| `list_devices` | Dispositivos Android conectados y simuladores de iOS encendidos |
| `get_design_specs` | Medidas, colores, textos y fuentes del frame (antes de escribir el XAML) |
| `capture_screen` | Captura + elementos de la app con su `AutomationId` |
| `compare_screen` | Comparación completa diseño vs. app: fidelidad, diferencias, cómo corregirlas y `report.html` |
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
- `samples/PayloIos` — la misma pantalla con vistas nativas de iOS (UIKit en Swift, sin MAUI ni storyboard). `./build.sh` la compila con `swiftc` (sin proyecto de Xcode) y la instala en el simulador encendido
- `tests/Driftcheck.Tests` — pruebas del comparador con pantallas sintéticas

## Limitaciones actuales (MVP)

- iOS solo en el simulador (no en dispositivos físicos).
- Solo el área visible (sin scroll).
- No mide tamaño de fuente en la app (uiautomator y XCUITest no lo exponen); `get_design_specs` lo da como referencia.

## Licencia

Software propietario, todos los derechos reservados (ver `LICENSE`). Los componentes de terceros de las apps de ejemplo conservan su licencia: ver `THIRD_PARTY_NOTICES.md`.
