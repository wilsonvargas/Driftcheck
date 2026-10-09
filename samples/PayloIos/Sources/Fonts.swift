import CoreText
import UIKit

/// Fuentes del kit, registradas al iniciar desde los .ttf del bundle. El nombre para UIFont es el
/// PostScript de cada archivo, que se lee del propio archivo en vez de escribirlo a mano.
enum Fonts {
    private static var names: [String: String] = [:]

    static func register() {
        for file in ["inter_regular", "inter_semibold", "lato_regular"] {
            guard let url = Bundle.main.url(forResource: file, withExtension: "ttf"),
                  let descriptors = CTFontManagerCreateFontDescriptorsFromURL(url as CFURL) as? [CTFontDescriptor],
                  let name = descriptors.first.flatMap({ CTFontDescriptorCopyAttribute($0, kCTFontNameAttribute) as? String })
            else { fatalError("No se pudo leer \(file).ttf") }
            CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil)
            names[file] = name
        }
    }

    static func interRegular(_ size: CGFloat) -> UIFont { font("inter_regular", size) }
    static func interSemiBold(_ size: CGFloat) -> UIFont { font("inter_semibold", size) }
    static func latoRegular(_ size: CGFloat) -> UIFont { font("lato_regular", size) }

    private static func font(_ file: String, _ size: CGFloat) -> UIFont {
        guard let name = names[file], let font = UIFont(name: name, size: size) else {
            fatalError("Fuente \(file) no registrada")
        }
        return font
    }
}
