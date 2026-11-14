import AppKit
import ApplicationServices
import ServiceManagement

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let coordinator = Coordinator()

    func applicationDidFinishLaunching(_ notification: Notification) {
        LoginItem.enableOnFirstLaunch()
        // Shows the system Accessibility prompt if access has not been granted yet.
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        coordinator.start()
    }
}

/// Launch at login (System Settings → General → Login Items).
enum LoginItem {
    private static let configuredKey = "loginItemConfigured"

    /// Enables launch at login once; if the user later turns it off in Login Items, the app won't turn it back on.
    static func enableOnFirstLaunch() {
        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: configuredKey) else { return }
        defaults.set(true, forKey: configuredKey)
        do {
            try SMAppService.mainApp.register()
        } catch {
            NSLog("FinderSizes: failed to enable launch at login: %@", error.localizedDescription)
        }
    }
}

enum SystemSettings {
    enum Pane: String {
        case fullDiskAccess = "Privacy_AllFiles"
    }

    static func open(_ pane: Pane) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane.rawValue)") {
            NSWorkspace.shared.open(url)
        }
    }
}
