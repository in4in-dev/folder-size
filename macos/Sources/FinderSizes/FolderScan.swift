import Foundation

struct CachedSize {
    let bytes: Int64
    let partial: Bool
}

/// Computes sizes of the items in one folder. Each subfolder at any depth is
/// a separate operation in a shared queue, so the tree is walked on all cores at once.
final class FolderScan {
    struct Entry {
        let url: URL
        let name: String
        let isDirectory: Bool
        let isPackage: Bool
    }

    struct Progress {
        var sizes: [Int64] = []
        /// How many folders inside the item have not been walked yet.
        var pending: [Int] = []
        /// The item contained folders that could not be read.
        var denied: [Bool] = []
        /// Count generation of the item: grows on recount, stale operations are dropped.
        var gens: [Int] = []

        var isFinished: Bool { !pending.contains { $0 > 0 } }
    }

    static let keys: [URLResourceKey] = [
        .isDirectoryKey, .isSymbolicLinkKey, .isPackageKey, .isVolumeKey,
        .totalFileAllocatedSizeKey, .fileAllocatedSizeKey,
    ]
    private static let keySet = Set(keys)

    let folder: URL
    /// Filled once before `ready` is called, read-only afterwards.
    private(set) var entries: [Entry] = []

    private var progress = Progress()
    private var cancelled = false
    private let lock = NSLock()
    private let queue: OperationQueue = {
        let q = OperationQueue()
        q.maxConcurrentOperationCount = max(4, ProcessInfo.processInfo.activeProcessorCount * 2)
        q.qualityOfService = .userInitiated
        return q
    }()

    init(folder: URL) {
        self.folder = folder
    }

    /// Reads the folder contents and starts walking subfolders; `ready` runs on the main thread.
    /// Subfolders found in `cached` are not walked again.
    func start(cached: [String: CachedSize], ready: @escaping () -> Void) {
        queue.addOperation { [self] in
            let fm = FileManager.default
            let urls = (try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: Self.keys, options: [.skipsHiddenFiles])) ?? []
            var list: [Entry] = []
            var p = Progress()
            var toWalk: [Int] = []

            for url in urls {
                let rv = try? url.resourceValues(forKeys: Self.keySet)
                let isDir = rv?.isDirectory == true && rv?.isSymbolicLink != true
                list.append(Entry(url: url, name: fm.displayName(atPath: url.path), isDirectory: isDir, isPackage: rv?.isPackage == true))
                p.gens.append(0)
                if !isDir {
                    p.sizes.append(Self.fileSize(rv)); p.pending.append(0); p.denied.append(false)
                } else if let c = cached[url.path] {
                    p.sizes.append(c.bytes); p.pending.append(0); p.denied.append(c.partial)
                } else {
                    p.sizes.append(0); p.pending.append(1); p.denied.append(false)
                    toWalk.append(list.count - 1)
                }
            }

            lock.lock()
            if cancelled { lock.unlock(); return }
            entries = list
            progress = p
            lock.unlock()

            for i in toWalk { enqueue(list[i].url, index: i, gen: 0) }
            DispatchQueue.main.async(execute: ready)
        }
    }

    func snapshot() -> Progress {
        lock.lock()
        defer { lock.unlock() }
        return progress
    }

    /// Stops all operations; results are no longer updated.
    func cancel() {
        lock.lock()
        cancelled = true
        lock.unlock()
        queue.cancelAllOperations()
    }

    /// Recounts a single subfolder (something inside it changed).
    func rescan(_ index: Int) {
        lock.lock()
        guard !cancelled, entries.indices.contains(index), entries[index].isDirectory else { lock.unlock(); return }
        progress.gens[index] += 1
        let gen = progress.gens[index]
        progress.sizes[index] = 0
        progress.pending[index] = 1
        progress.denied[index] = false
        lock.unlock()
        enqueue(entries[index].url, index: index, gen: gen)
    }

    /// Re-reads the sizes of top-level files.
    func restatFiles() {
        queue.addOperation { [self] in
            for (i, entry) in entries.enumerated() where !entry.isDirectory {
                var url = entry.url
                url.removeAllCachedResourceValues()
                let size = Self.fileSize(try? url.resourceValues(forKeys: Self.keySet))
                lock.lock()
                if !cancelled { progress.sizes[i] = size }
                lock.unlock()
            }
        }
    }

    private func enqueue(_ dir: URL, index: Int, gen: Int) {
        queue.addOperation { [weak self] in
            self?.walk(dir, index: index, gen: gen)
        }
    }

    private func isAlive(_ index: Int, _ gen: Int) -> Bool {
        !cancelled && progress.gens[index] == gen
    }

    private func walk(_ dir: URL, index: Int, gen: Int) {
        lock.lock()
        let alive = isAlive(index, gen)
        lock.unlock()
        guard alive else { return }

        var bytes: Int64 = 0
        var subdirs: [URL] = []
        var failed = false
        do {
            let urls = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: Self.keys, options: [])
            for url in urls {
                guard let rv = try? url.resourceValues(forKeys: Self.keySet) else { continue }
                if rv.isDirectory == true && rv.isSymbolicLink != true {
                    // Mount points (other disks, /System/Volumes/Data) are not counted.
                    if rv.isVolume != true { subdirs.append(url) }
                } else {
                    bytes += Self.fileSize(rv)
                }
            }
        } catch {
            failed = true
        }

        lock.lock()
        guard isAlive(index, gen) else { lock.unlock(); return }
        progress.sizes[index] += bytes
        if failed { progress.denied[index] = true }
        progress.pending[index] += subdirs.count - 1
        lock.unlock()

        for sub in subdirs { enqueue(sub, index: index, gen: gen) }
    }

    private static func fileSize(_ rv: URLResourceValues?) -> Int64 {
        Int64(rv?.totalFileAllocatedSize ?? rv?.fileAllocatedSize ?? 0)
    }
}
