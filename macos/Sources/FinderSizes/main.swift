import AppKit

// `FinderSizes --scan <папка>` — посчитать папку в терминале без интерфейса (для проверки скорости).
let args = CommandLine.arguments
if let i = args.firstIndex(of: "--scan"), i + 1 < args.count {
    ScanCLI.run(path: args[i + 1])
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
