import AppKit
import SwiftUI

/// A panel that never takes focus away from Finder.
final class SizePanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// A click on the inactive panel reaches the row right away, without a "focus click" first.
final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

final class PanelController {
    static let width: CGFloat = 210
    static let gap: CGFloat = 6

    private let panel: SizePanel

    init(model: SizeModel, actions: PanelActions) {
        panel = SizePanel(
            contentRect: NSRect(x: 0, y: 0, width: Self.width, height: 300),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .normal
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary, .ignoresCycle]

        let hosting = FirstMouseHostingView(rootView: PanelView(model: model, actions: actions))
        hosting.sizingOptions = []
        panel.contentView = hosting
    }

    /// Places the panel right next to the Finder window. If there is no room on the right
    /// (the window spans the screen width), the panel is hidden.
    func place(nextTo frame: CGRect, height: CGFloat, windowNumber: Int?, reorder: Bool) {
        let center = CGPoint(x: frame.midX, y: frame.midY)
        guard let screen = NSScreen.screens.first(where: { $0.frame.contains(center) }) ?? NSScreen.main else {
            hide()
            return
        }
        let x = frame.maxX + Self.gap
        guard x + Self.width <= screen.visibleFrame.maxX else {
            hide()
            return
        }

        let h = min(height, frame.height)
        let rect = NSRect(x: x, y: frame.maxY - h, width: Self.width, height: h)
        if panel.frame != rect {
            panel.setFrame(rect, display: true)
            panel.invalidateShadow()
        }
        if !panel.isVisible || reorder {
            if let windowNumber {
                panel.order(.above, relativeTo: windowNumber)
            } else {
                panel.orderFrontRegardless()
            }
        }
    }

    func hide() {
        if panel.isVisible { panel.orderOut(nil) }
    }
}
