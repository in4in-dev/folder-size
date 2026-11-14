import AppKit

// `FinderSizes --scan <folder>`: count a folder in the terminal without UI (for speed checks).
let args = CommandLine.arguments
if let i = args.firstIndex(of: "--scan"), i + 1 < args.count {
    ScanCLI.run(path: args[i + 1])
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
