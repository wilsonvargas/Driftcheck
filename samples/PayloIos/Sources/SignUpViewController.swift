import UIKit

/// Pantalla "Component 113" del kit Paylo de Marvilo (CC BY 4.0), igual que SignUpPage.xaml de PayloMaui
/// pero con vistas nativas de UIKit: sirve para comprobar que Driftcheck no depende de MAUI en iOS.
/// https://www.figma.com/community/file/1428007748789833465/payment-app-free-ui-kit-by-marvilo (duplicar el archivo y comparar con el frame "Component 113")
/// Medidas en pt (1 pt = 1 unidad de Figma), las mismas que en PayloMaui y PayloNative.
final class SignUpViewController: UIViewController {
    override var preferredStatusBarStyle: UIStatusBarStyle { .darkContent }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = UIColor(hex: 0xF5F5F5)

        // En el diseño el contenido (603 de alto) está centrado verticalmente en el área segura.
        let content = stack(.vertical, spacing: 32, [header(), form(), footer()])
        content.alignment = .fill
        view.addSubview(content)
        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 35),
            content.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -35),
            content.centerYAnchor.constraint(equalTo: view.safeAreaLayoutGuide.centerYAnchor),
        ])
    }

    // MARK: - Encabezado: logo + textos (180 de alto)

    func header() -> UIView {
        let logo = UIImageView(image: UIImage(named: "paylo_logo.png"))
        logo.accessibilityIdentifier = "Image"
        logo.size(81, 81)

        // Sin alto fijo: la letra de 32 necesita ~39 y con 38 se recortaría la "y".
        let title = label("Nice to meet you", Fonts.interSemiBold(32), 0x0D001F, align: .center)
        let subtitle = label("Before we begin, we need some details.", Fonts.interRegular(17), 0x564E60, align: .center)
        subtitle.height(24)

        return stack(.vertical, spacing: 32, [
            centered(logo),
            stack(.vertical, spacing: 4, [title, subtitle]),
        ])
    }

    // MARK: - Formulario (339 de alto)

    func form() -> UIView {
        stack(.vertical, spacing: 24, [socialButton(), divider(), fields(), signUpButton()])
    }

    func socialButton() -> UIView {
        let box = UIView()
        box.accessibilityIdentifier = "Social button"
        box.backgroundColor = .white
        box.layer.cornerRadius = 24
        box.layer.borderWidth = 1
        box.layer.borderColor = UIColor(hex: 0xEBE7F1).cgColor
        box.height(48)

        let icon = UIImageView(image: UIImage(named: "google_icon.png"))
        icon.size(20, 20)
        let text = label("Sign up with Google", Fonts.interSemiBold(17), 0xE6E6E6)
        text.height(24)

        let row = stack(.horizontal, spacing: 12, [icon, text])
        row.alignment = .center
        box.addSubview(row)
        NSLayoutConstraint.activate([
            row.centerXAnchor.constraint(equalTo: box.centerXAnchor),
            row.centerYAnchor.constraint(equalTo: box.centerYAnchor),
        ])
        return box
    }

    func divider() -> UIView {
        let or = label("or", Fonts.latoRegular(14), 0x1B1B1B, align: .center, kern: 0.08)
        or.widthAnchor.constraint(equalToConstant: 80).isActive = true

        let left = line(), right = line()
        let row = stack(.horizontal, spacing: 0, [left, or, right])
        row.alignment = .center
        row.height(24)
        left.widthAnchor.constraint(equalTo: right.widthAnchor).isActive = true
        return row
    }

    func line() -> UIView {
        let v = UIView()
        v.backgroundColor = UIColor(hex: 0xC8BED4)
        v.height(1)
        return v
    }

    func fields() -> UIView {
        // Email con error: campo 54 (8 + etiqueta 16 - 2 + texto 24 + 8) + 4 + mensaje 16 = 74
        let emailLabel = label("Enter your email", Fonts.interRegular(12), 0xFF8E56, kern: 0.4)
        emailLabel.height(16)
        let email = field()
        email.text = "Name@marvilo.net"
        email.keyboardType = .emailAddress

        let emailBox = UIView()
        emailBox.backgroundColor = .white
        emailBox.layer.cornerRadius = 8
        emailBox.layer.borderWidth = 1
        emailBox.layer.borderColor = UIColor(hex: 0xFF8E56).cgColor
        emailBox.height(54)
        pin(stack(.vertical, spacing: -2, [emailLabel, email]), in: emailBox, x: 14, y: 8)

        let error = label("Error message", Fonts.latoRegular(12), 0xFF8E56, kern: 0.4)
        error.height(16)
        let errorRow = UIView()
        pin(error, in: errorRow, x: 0, y: 0, leading: 14)

        let password = field()
        password.isSecureTextEntry = true
        password.attributedPlaceholder = NSAttributedString(string: "Enter your password", attributes: [
            .font: Fonts.interRegular(17), .foregroundColor: UIColor(hex: 0x8D839A),
        ])

        let passwordBox = UIView()
        passwordBox.backgroundColor = .white
        passwordBox.layer.cornerRadius = 8
        passwordBox.layer.shadowColor = UIColor.black.cgColor
        passwordBox.layer.shadowOpacity = 0.1
        passwordBox.layer.shadowOffset = CGSize(width: 0, height: 2)
        passwordBox.layer.shadowRadius = 4
        passwordBox.height(46)
        passwordBox.addSubview(password)
        NSLayoutConstraint.activate([
            password.leadingAnchor.constraint(equalTo: passwordBox.leadingAnchor, constant: 14),
            password.trailingAnchor.constraint(equalTo: passwordBox.trailingAnchor, constant: -14),
            password.centerYAnchor.constraint(equalTo: passwordBox.centerYAnchor),
        ])

        return stack(.vertical, spacing: 20, [
            stack(.vertical, spacing: 4, [emailBox, errorRow]),
            passwordBox,
        ])
    }

    func field() -> UITextField {
        let f = UITextField()
        f.translatesAutoresizingMaskIntoConstraints = false
        f.borderStyle = .none
        f.font = Fonts.interRegular(17)
        f.textColor = UIColor(hex: 0x3D3744)
        f.height(24)
        return f
    }

    /// Estado "Disabled" del kit: el mismo morado al 30 %.
    func signUpButton() -> UIView {
        let button = UIButton(type: .custom)
        button.setTitle("Sign up", for: .normal)
        button.setTitleColor(.white, for: .normal)
        button.setTitleColor(.white, for: .disabled)
        button.titleLabel?.font = Fonts.interSemiBold(20)
        button.backgroundColor = UIColor(hex: 0x9E56FF)
        button.layer.cornerRadius = 27.5
        button.alpha = 0.3
        button.isEnabled = false
        button.height(55)
        return button
    }

    // MARK: - Pie

    func footer() -> UIView {
        let question = label("Already have an account?", Fonts.interRegular(14), 0x564E60)
        let login = label("Log in", Fonts.interSemiBold(14), 0x9E56FF)
        let row = stack(.horizontal, spacing: 4, [question, login])
        row.alignment = .center
        row.height(20)
        return centered(row)
    }

    // MARK: - Ayudas de layout

    func stack(_ axis: NSLayoutConstraint.Axis, spacing: CGFloat, _ views: [UIView]) -> UIStackView {
        let s = UIStackView(arrangedSubviews: views)
        s.translatesAutoresizingMaskIntoConstraints = false
        s.axis = axis
        s.spacing = spacing
        return s
    }

    func label(_ text: String, _ font: UIFont, _ color: UInt32, align: NSTextAlignment = .natural, kern: CGFloat = 0) -> UILabel {
        let l = UILabel()
        l.translatesAutoresizingMaskIntoConstraints = false
        l.attributedText = NSAttributedString(string: text, attributes: [
            .font: font, .foregroundColor: UIColor(hex: color), .kern: kern,
        ])
        l.textAlignment = align
        return l
    }

    /// Una vista con su ancho propio, centrada horizontalmente en una fila de todo el ancho.
    func centered(_ view: UIView) -> UIView {
        let row = UIView()
        row.translatesAutoresizingMaskIntoConstraints = false
        row.addSubview(view)
        NSLayoutConstraint.activate([
            view.topAnchor.constraint(equalTo: row.topAnchor),
            view.bottomAnchor.constraint(equalTo: row.bottomAnchor),
            view.centerXAnchor.constraint(equalTo: row.centerXAnchor),
        ])
        return row
    }

    func pin(_ view: UIView, in box: UIView, x: CGFloat, y: CGFloat, leading: CGFloat? = nil) {
        box.translatesAutoresizingMaskIntoConstraints = false
        view.translatesAutoresizingMaskIntoConstraints = false
        box.addSubview(view)
        NSLayoutConstraint.activate([
            view.leadingAnchor.constraint(equalTo: box.leadingAnchor, constant: leading ?? x),
            view.trailingAnchor.constraint(equalTo: box.trailingAnchor, constant: -x),
            view.topAnchor.constraint(equalTo: box.topAnchor, constant: y),
            view.bottomAnchor.constraint(lessThanOrEqualTo: box.bottomAnchor, constant: -y),
        ])
        if y == 0 { view.bottomAnchor.constraint(equalTo: box.bottomAnchor).isActive = true }
    }
}

extension UIView {
    func height(_ h: CGFloat) {
        translatesAutoresizingMaskIntoConstraints = false
        heightAnchor.constraint(equalToConstant: h).isActive = true
    }

    func size(_ w: CGFloat, _ h: CGFloat) {
        height(h)
        widthAnchor.constraint(equalToConstant: w).isActive = true
    }
}

extension UIColor {
    convenience init(hex: UInt32) {
        self.init(red: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
}
