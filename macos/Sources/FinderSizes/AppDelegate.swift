import AppKit
import ApplicationServices
import ServiceManagement

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let coordinator = Coordinator()

    func applicationDidFinishLaunching(_ notification: Notification) {
        LoginItem.enableOnFirstLaunch()
        // Покажет системный запрос на Универсальный доступ, если его ещё нет.
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        coordinator.start()
    }
}

/// Автозапуск при входе в систему (Настройки → Основные → Объекты входа).
enum LoginItem {
    private static let configuredKey = "loginItemConfigured"

    /// Включает автозапуск один раз; если потом выключить его в «Объектах входа», приложение не вернёт.
    static func enableOnFirstLaunch() {
        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: configuredKey) else { return }
        defaults.set(true, forKey: configuredKey)
        do {
            try SMAppService.mainApp.register()
        } catch {
            NSLog("FinderSizes: не удалось включить автозапуск: %@", error.localizedDescription)
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
