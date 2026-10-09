import UIKit

@main
final class AppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?

    func application(_ application: UIApplication,
                     didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?) -> Bool {
        Fonts.register()
        window = UIWindow(frame: UIScreen.main.bounds)
        window?.rootViewController = SignUpViewController()
        window?.makeKeyAndVisible()
        return true
    }
}
