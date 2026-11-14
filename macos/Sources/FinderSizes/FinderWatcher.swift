import AppKit
import ApplicationServices

struct FinderWindowState {
    /// AX/CG coordinates: origin at the top-left corner of the main screen.
    let axFrame: CGRect
    /// The same window in Cocoa coordinates (origin at the bottom left).
    let frame: CGRect
    let isFullScreen: Bool
}

/// Watches Finder windows via the Accessibility API and reports when a window
/// has moved (geometry) or its contents may have changed (content).
final class FinderWatcher {
    enum Change { case geometry, content }

    static let bundleID = "com.apple.finder"
    private static let appNotifications = [
        kAXFocusedWindowChangedNotification,
        kAXMainWindowChangedNotification,
        kAXWindowCreatedNotification,
        kAXSelectedChildrenChangedNotification,
        kAXSelectedRowsChangedNotification,
    ]
    private static let windowNotifications = [
        kAXMovedNotification,
        kAXResizedNotification,
        kAXTitleChangedNotification,
        kAXUIElementDestroyedNotification,
        kAXWindowMiniaturizedNotification,
    ]

    var onChange: ((Change) -> Void)?

    private(set) var pid: pid_t = 0
    private var app: AXUIElement?
    private var observer: AXObserver?
    private var window: AXUIElement?

    var finderIsFrontmost: Bool {
        NSWorkspace.shared.frontmostApplication?.bundleIdentifier == Self.bundleID
    }

    func start() {
        let nc = NSWorkspace.shared.notificationCenter
        let names = [
            NSWorkspace.didActivateApplicationNotification,
            NSWorkspace.didLaunchApplicationNotification,
            NSWorkspace.activeSpaceDidChangeNotification,
        ]
        for name in names {
            nc.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                self?.attachIfNeeded()
                self?.onChange?(.content)
            }
        }
        attachIfNeeded()
    }

    /// Attaches to the Finder process (again if Finder was relaunched or
    /// Accessibility access was granted after launch).
    func attachIfNeeded() {
        guard AXIsProcessTrusted(),
              let finder = NSRunningApplication.runningApplications(withBundleIdentifier: Self.bundleID).first
        else { return }
        if observer != nil, finder.processIdentifier == pid { return }

        detach()
        pid = finder.processIdentifier
        var created: AXObserver?
        guard AXObserverCreate(pid, Self.callback, &created) == .success, let obs = created else { return }
        let appElement = AXUIElementCreateApplication(pid)
        for n in Self.appNotifications {
            AXObserverAddNotification(obs, appElement, n as CFString, refcon)
        }
        CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(obs), .commonModes)
        app = appElement
        observer = obs
        watchFocusedWindow()
    }

    /// Re-subscribes to the current window's moves if focus switched to another window.
    func watchFocusedWindow() {
        guard let observer, let app else { return }
        let focused = Self.element(app, kAXFocusedWindowAttribute)
        if let old = window, let focused, CFEqual(old, focused) { return }
        if let old = window {
            for n in Self.windowNotifications { AXObserverRemoveNotification(observer, old, n as CFString) }
        }
        window = focused
        if let focused {
            for n in Self.windowNotifications { AXObserverAddNotification(observer, focused, n as CFString, refcon) }
        }
    }

    /// The active regular Finder window, or nil (Finder not frontmost, minimized, Get Info, etc.).
    func currentState() -> FinderWindowState? {
        guard finderIsFrontmost, let app, let win = Self.element(app, kAXFocusedWindowAttribute) else { return nil }
        if let subrole = Self.string(win, kAXSubroleAttribute), subrole != kAXStandardWindowSubrole { return nil }
        if Self.bool(win, kAXMinimizedAttribute) == true { return nil }
        guard let pos = Self.point(win, kAXPositionAttribute),
              let size = Self.size(win, kAXSizeAttribute)
        else { return nil }

        let ax = CGRect(origin: pos, size: size)
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        let frame = CGRect(x: ax.minX, y: primaryHeight - ax.maxY, width: ax.width, height: ax.height)
        return FinderWindowState(axFrame: ax, frame: frame, isFullScreen: Self.bool(win, "AXFullScreen") == true)
    }

    /// Window Server number of the window, used to order the panel right above the Finder window.
    func windowNumber(for axFrame: CGRect) -> Int? {
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { return nil }
        for info in list {
            guard (info[kCGWindowOwnerPID as String] as? pid_t) == pid,
                  (info[kCGWindowLayer as String] as? Int) == 0,
                  let dict = info[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: dict)
            else { continue }
            if abs(bounds.minX - axFrame.minX) < 2, abs(bounds.minY - axFrame.minY) < 2,
               abs(bounds.width - axFrame.width) < 2, abs(bounds.height - axFrame.height) < 2 {
                return info[kCGWindowNumber as String] as? Int
            }
        }
        return nil
    }

    private func detach() {
        if let observer {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes)
        }
        observer = nil
        app = nil
        window = nil
    }

    private var refcon: UnsafeMutableRawPointer { Unmanaged.passUnretained(self).toOpaque() }

    private static let callback: AXObserverCallback = { _, _, notification, refcon in
        guard let refcon else { return }
        Unmanaged<FinderWatcher>.fromOpaque(refcon).takeUnretainedValue().handle(notification as String)
    }

    private func handle(_ notification: String) {
        switch notification {
        case kAXMovedNotification, kAXResizedNotification:
            onChange?(.geometry)
        case kAXFocusedWindowChangedNotification, kAXMainWindowChangedNotification,
             kAXWindowCreatedNotification, kAXUIElementDestroyedNotification:
            watchFocusedWindow()
            onChange?(.content)
        default:
            onChange?(.content)
        }
    }

    // MARK: - AX helpers

    private static func raw(_ el: AXUIElement, _ attr: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, attr as CFString, &value) == .success else { return nil }
        return value
    }

    private static func element(_ el: AXUIElement, _ attr: String) -> AXUIElement? {
        guard let v = raw(el, attr), CFGetTypeID(v) == AXUIElementGetTypeID() else { return nil }
        return (v as! AXUIElement)
    }

    private static func string(_ el: AXUIElement, _ attr: String) -> String? {
        raw(el, attr) as? String
    }

    private static func bool(_ el: AXUIElement, _ attr: String) -> Bool? {
        raw(el, attr) as? Bool
    }

    private static func axValue(_ el: AXUIElement, _ attr: String) -> AXValue? {
        guard let v = raw(el, attr), CFGetTypeID(v) == AXValueGetTypeID() else { return nil }
        return (v as! AXValue)
    }

    private static func point(_ el: AXUIElement, _ attr: String) -> CGPoint? {
        guard let v = axValue(el, attr) else { return nil }
        var out = CGPoint.zero
        return AXValueGetValue(v, .cgPoint, &out) ? out : nil
    }

    private static func size(_ el: AXUIElement, _ attr: String) -> CGSize? {
        guard let v = axValue(el, attr) else { return nil }
        var out = CGSize.zero
        return AXValueGetValue(v, .cgSize, &out) ? out : nil
    }
}
