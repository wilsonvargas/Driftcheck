import XCTest

/// Vuelca el árbol de accesibilidad de la app que está en primer plano en el simulador, en puntos.
/// Driftcheck lo ejecuta con `xcodebuild test-without-building` y le pasa (con el prefijo TEST_RUNNER_):
/// - DRIFTCHECK_BUNDLE_IDS: apps que están corriendo, separadas por comas; se usa la que está en primer plano.
/// - DRIFTCHECK_OUTPUT: archivo donde escribir el JSON. El simulador comparte el disco con la Mac.
final class DumpTests: XCTestCase {
    func testDump() throws {
        let env = ProcessInfo.processInfo.environment
        let output = try XCTUnwrap(env["DRIFTCHECK_OUTPUT"], "Falta DRIFTCHECK_OUTPUT")
        let candidates = (env["DRIFTCHECK_BUNDLE_IDS"] ?? "")
            .split(separator: ",").map { String($0) }.filter { !$0.isEmpty }

        guard let (bundleId, app) = candidates.lazy
            .map({ ($0, XCUIApplication(bundleIdentifier: $0)) })
            .first(where: { $0.1.state == .runningForeground })
        else {
            try write(["error": "No hay ninguna app en primer plano en el simulador."], to: output)
            return
        }

        let root = try app.snapshot()
        var elements: [[String: Any]] = []
        collect(root, into: &elements)

        try write([
            "bundleId": bundleId,
            "frame": frame(root.frame),
            "elements": elements,
        ], to: output)
    }

    func collect(_ snapshot: XCUIElementSnapshot, into elements: inout [[String: Any]]) {
        var element: [String: Any] = [
            "type": typeName(snapshot.elementType),
            "frame": frame(snapshot.frame),
        ]
        if !snapshot.identifier.isEmpty { element["identifier"] = snapshot.identifier }
        if !snapshot.label.isEmpty { element["label"] = snapshot.label }
        if let value = snapshot.value as? String, !value.isEmpty { element["value"] = value }
        if let placeholder = snapshot.placeholderValue, !placeholder.isEmpty { element["placeholder"] = placeholder }
        elements.append(element)

        for child in snapshot.children { collect(child, into: &elements) }
    }

    func frame(_ r: CGRect) -> [String: Double] {
        ["x": r.origin.x, "y": r.origin.y, "width": r.size.width, "height": r.size.height]
    }

    func write(_ object: [String: Any], to path: String) throws {
        let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
        try data.write(to: URL(fileURLWithPath: path))
    }

    func typeName(_ type: XCUIElement.ElementType) -> String {
        switch type {
        case .application: return "Application"
        case .window: return "Window"
        case .other: return "Other"
        case .button: return "Button"
        case .staticText: return "StaticText"
        case .textField: return "TextField"
        case .secureTextField: return "SecureTextField"
        case .textView: return "TextView"
        case .image: return "Image"
        case .switch: return "Switch"
        case .toggle: return "Toggle"
        case .link: return "Link"
        case .cell: return "Cell"
        case .table: return "Table"
        case .collectionView: return "CollectionView"
        case .scrollView: return "ScrollView"
        case .navigationBar: return "NavigationBar"
        case .tabBar: return "TabBar"
        case .toolbar: return "Toolbar"
        case .searchField: return "SearchField"
        case .slider: return "Slider"
        case .picker: return "Picker"
        case .activityIndicator: return "ActivityIndicator"
        case .progressIndicator: return "ProgressIndicator"
        case .segmentedControl: return "SegmentedControl"
        case .keyboard: return "Keyboard"
        case .alert: return "Alert"
        case .webView: return "WebView"
        default: return "Type\(type.rawValue)"
        }
    }
}
