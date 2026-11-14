import AppKit

/// Panel state: the current folder, its items with sizes, the selection.
/// Lives on the main thread.
final class SizeModel: ObservableObject {
    struct Item: Identifiable {
        /// Path without a trailing "/".
        let id: String
        let name: String
        /// A regular folder opens in Finder. Packages (.app etc.) open like files.
        let isFolder: Bool
        let bytes: Int64
        let computing: Bool
        let partial: Bool
    }

    @Published private(set) var folder: URL?
    @Published private(set) var items: [Item] = []
    @Published private(set) var isComputing = false
    @Published var selection: Set<String> = []

    /// The number of rows changed, so the panel needs a different height.
    var onLayoutChange: (() -> Void)?

    var folderName: String { folder.map { FileManager.default.displayName(atPath: $0.path) } ?? "" }
    var total: Int64 { items.reduce(0) { $0 + $1.bytes } }
    var hasPartial: Bool { items.contains { $0.partial } }
    var selectedItems: [Item] { items.filter { selection.contains($0.id) } }

    private var scan: FolderScan?
    private var timer: Timer?
    /// Computed subfolder sizes: path → size. Invalidated by FSEvents.
    private var cache: [String: CachedSize] = [:]
    /// Index → generation whose result is already cached.
    private var recorded: [Int: Int] = [:]
    /// Last completed size, shown while the subfolder is being recounted.
    private var stable: [Int: Int64] = [:]
    private var icons: [String: NSImage] = [:]

    private var dirty = Set<Int>()
    private var relistNeeded = false
    private var lastRescan: [Int: Date] = [:]
    private var flushScheduled = false

    func show(folder url: URL) {
        guard url.path != folder?.path else { return }
        scan?.cancel()  // left the folder, its count is no longer needed
        folder = url
        items = []
        selection = []
        icons = [:]
        startScan()
    }

    func leaveFolder() {
        guard folder != nil else { return }
        scan?.cancel()
        scan = nil
        folder = nil
        items = []
        isComputing = false
        stopTimer()
    }

    func icon(for item: Item) -> NSImage {
        if let cached = icons[item.id] { return cached }
        let image = NSWorkspace.shared.icon(forFile: item.id)
        icons[item.id] = image
        return image
    }

    // MARK: - Counting

    private func startScan() {
        guard let folder else { return }
        let s = FolderScan(folder: folder)
        scan = s
        recorded = [:]
        stable = [:]
        dirty = []
        relistNeeded = false
        lastRescan = [:]
        isComputing = true
        s.start(cached: cache) { [weak self, weak s] in
            guard let self, let s, self.scan === s else { return }
            self.tick()
            self.startTimer()
        }
    }

    private func tick() {
        guard let s = scan else { return }
        let p = s.snapshot()
        var list: [Item] = []
        list.reserveCapacity(s.entries.count)

        for (i, entry) in s.entries.enumerated() {
            let busy = p.pending[i] > 0
            if entry.isDirectory && !busy && recorded[i] != p.gens[i] {
                cache[entry.url.path] = CachedSize(bytes: p.sizes[i], partial: p.denied[i])
                recorded[i] = p.gens[i]
                stable[i] = p.sizes[i]
            }
            let shown = busy ? max(p.sizes[i], stable[i] ?? 0) : p.sizes[i]
            list.append(Item(
                id: entry.url.path,
                name: entry.name,
                isFolder: entry.isDirectory && !entry.isPackage,
                bytes: shown,
                computing: busy,
                partial: p.denied[i]
            ))
        }
        list.sort {
            $0.bytes != $1.bytes ? $0.bytes > $1.bytes : $0.name.localizedStandardCompare($1.name) == .orderedAscending
        }

        let countChanged = list.count != items.count
        items = list
        isComputing = !p.isFinished
        if p.isFinished { stopTimer() }
        if countChanged { onLayoutChange?() }
    }

    private func startTimer() {
        guard timer == nil else { return }
        let t = Timer(timeInterval: 0.15, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }

    // MARK: - Disk changes

    func filesChanged(_ paths: [String]) {
        let changed = paths.map { $0.hasSuffix("/") ? $0 : $0 + "/" }

        for key in Array(cache.keys) {
            let prefix = key.hasSuffix("/") ? key : key + "/"
            if changed.contains(where: { $0.hasPrefix(prefix) }) { cache[key] = nil }
        }

        guard let s = scan, let folder else { return }
        let folderPrefix = folder.path == "/" ? "/" : folder.path + "/"
        if changed.contains(folderPrefix) { relistNeeded = true }
        for (i, entry) in s.entries.enumerated() where entry.isDirectory {
            let prefix = entry.url.path + "/"
            if changed.contains(where: { $0.hasPrefix(prefix) }) { dirty.insert(i) }
        }
        scheduleFlush()
    }

    private func scheduleFlush() {
        guard !flushScheduled, relistNeeded || !dirty.isEmpty else { return }
        flushScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.flush() }
    }

    private func flush() {
        flushScheduled = false
        guard let s = scan else { return }

        if relistNeeded {
            relistNeeded = false
            let now = (try? FileManager.default.contentsOfDirectory(at: s.folder, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
            if Set(now.map(\.lastPathComponent)) != Set(s.entries.map { $0.url.lastPathComponent }) {
                // Items appeared or disappeared: re-read the folder, completed subfolders come from the cache.
                s.cancel()
                startScan()
                return
            }
            s.restatFiles()
        }

        let p = s.snapshot()
        var later = Set<Int>()
        for i in dirty {
            // Still counting: the changes will be picked up by the current walk.
            guard p.pending[i] == 0 else { continue }
            if let last = lastRescan[i], Date().timeIntervalSince(last) < 10 {
                later.insert(i)
                continue
            }
            lastRescan[i] = Date()
            s.rescan(i)
        }
        dirty = later
        isComputing = true
        startTimer()
        if !later.isEmpty {
            flushScheduled = true
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in self?.flush() }
        }
    }
}
