import AppKit

/// Связывает слежение за Finder, подсчёт и панель.
final class Coordinator: PanelActions {
    private let watcher = FinderWatcher()
    private let model = SizeModel()
    private let fsWatcher = FSWatcher()
    private lazy var panel = PanelController(model: model, actions: self)
    private var pollTimer: Timer?
    private var windowNumber: Int?

    func start() {
        watcher.onChange = { [weak self] change in
            switch change {
            case .geometry: self?.updateGeometry()
            case .content: self?.updateContent()
            }
        }
        watcher.start()
        model.onLayoutChange = { [weak self] in self?.updateGeometry() }
        fsWatcher.onEvents = { [weak self] paths in self?.model.filesChanged(paths) }
        fsWatcher.start()

        // Подстраховка к уведомлениям AX: переход в папку с тем же именем,
        // смена выделения в виде, где Finder не шлёт событий.
        let timer = Timer(timeInterval: 0.7, repeats: true) { [weak self] _ in self?.poll() }
        RunLoop.main.add(timer, forMode: .common)
        pollTimer = timer
        updateContent()
    }

    private func poll() {
        watcher.attachIfNeeded()
        if watcher.finderIsFrontmost {
            watcher.watchFocusedWindow()
            updateContent()
        } else {
            panel.hide()
        }
    }

    private func updateContent() {
        guard let state = watcher.currentState(), !state.isFullScreen else {
            panel.hide()
            return
        }
        guard let snapshot = FinderBridge.snapshot() else {
            panel.hide()
            return
        }
        guard let folder = snapshot.folder else {
            // Окно без обычной папки (Недавние, поиск) — подсчёт прошлой папки больше не нужен.
            panel.hide()
            model.leaveFolder()
            return
        }
        model.show(folder: folder)
        if model.selection != snapshot.selection { model.selection = snapshot.selection }
        windowNumber = watcher.windowNumber(for: state.axFrame)
        place(state, reorder: true)
    }

    private func updateGeometry() {
        guard model.folder != nil, let state = watcher.currentState(), !state.isFullScreen else {
            panel.hide()
            return
        }
        place(state, reorder: false)
    }

    private func place(_ state: FinderWindowState, reorder: Bool) {
        panel.place(
            nextTo: state.frame,
            height: PanelView.preferredHeight(rows: model.items.count),
            windowNumber: windowNumber,
            reorder: reorder
        )
    }

    // MARK: - PanelActions

    func select(_ item: SizeModel.Item, toggle: Bool) {
        var selection = toggle ? model.selection : []
        if toggle && selection.contains(item.id) {
            selection.remove(item.id)
        } else {
            selection.insert(item.id)
        }
        model.selection = selection
        FinderBridge.select(paths: Array(selection))
    }

    func open(_ item: SizeModel.Item) {
        if item.isFolder {
            FinderBridge.open(folder: item.id)
        } else {
            NSWorkspace.shared.open(URL(fileURLWithPath: item.id))
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { [weak self] in self?.updateContent() }
    }

    func openFullDiskAccess() {
        SystemSettings.open(.fullDiskAccess)
    }
}
