import AppKit

/// Talks to Finder via Apple Events: which folder is open, what is selected,
/// opening a folder in the same window, selecting items.
enum FinderBridge {
    struct Snapshot {
        /// nil if the front window has no regular folder (Recents, AirDrop, search, Get Info).
        let folder: URL?
        /// Paths of the selected items, without a trailing "/".
        let selection: Set<String>
    }

    private static let snapshotSource = """
    tell application "Finder"
        if (count of Finder windows) is 0 then return ""
        set w to front window
        if class of w is not Finder window then return ""
        try
            set out to POSIX path of (target of w as alias)
        on error
            return ""
        end try
        try
            repeat with i in (get selection as alias list)
                set out to out & linefeed & POSIX path of i
            end repeat
        end try
        return out
    end tell
    """

    private static let snapshotScript: NSAppleScript? = {
        let script = NSAppleScript(source: snapshotSource)
        script?.compileAndReturnError(nil)
        return script
    }()

    private static var lastError: Int?

    /// nil if Finder did not respond (e.g. Automation permission for Finder was not granted).
    static func snapshot() -> Snapshot? {
        var error: NSDictionary?
        guard let result = snapshotScript?.executeAndReturnError(&error) else {
            report(error)
            return nil
        }
        let text = result.stringValue ?? ""
        if text.isEmpty { return Snapshot(folder: nil, selection: []) }

        let lines = text.components(separatedBy: "\n")
        let folder = URL(fileURLWithPath: lines[0], isDirectory: true)
        let selection = Set(lines.dropFirst().filter { !$0.isEmpty }.map { URL(fileURLWithPath: $0).path })
        return Snapshot(folder: folder, selection: selection)
    }

    /// Opens the folder in the front Finder window, like double-clicking it.
    static func open(folder path: String) {
        run("tell application \"Finder\" to set target of front Finder window to \(alias(path))")
    }

    static func select(paths: [String]) {
        let items = paths.map(alias).joined(separator: ", ")
        run("tell application \"Finder\" to set selection to {\(items)}")
    }

    private static func alias(_ path: String) -> String {
        let escaped = path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        return "(POSIX file \"\(escaped)\" as alias)"
    }

    private static func run(_ source: String) {
        var error: NSDictionary?
        NSAppleScript(source: source)?.executeAndReturnError(&error)
        report(error)
    }

    private static func report(_ error: NSDictionary?) {
        guard let error else { return }
        let code = error[NSAppleScript.errorNumber] as? Int ?? 0
        guard code != lastError else { return }
        lastError = code
        if code == -1743 {
            NSLog("FinderSizes: no permission to control Finder (System Settings → Privacy & Security → Automation)")
        } else {
            NSLog("FinderSizes: AppleScript error %@", error)
        }
    }
}
