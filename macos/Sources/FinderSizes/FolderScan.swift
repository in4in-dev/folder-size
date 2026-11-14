import Foundation

struct CachedSize {
    let bytes: Int64
    let partial: Bool
}

/// Подсчёт размеров элементов одной папки. Каждая подпапка на любой глубине —
/// отдельная операция в общей очереди, поэтому дерево обходится на всех ядрах сразу.
final class FolderScan {
    struct Entry {
        let url: URL
        let name: String
        let isDirectory: Bool
        let isPackage: Bool
    }

    struct Progress {
        var sizes: [Int64] = []
        /// Сколько папок внутри элемента ещё не обойдено.
        var pending: [Int] = []
        /// В элементе были папки, которые не удалось прочитать.
        var denied: [Bool] = []
        /// Поколение подсчёта элемента: растёт при пересчёте, старые операции отбрасываются.
        var gens: [Int] = []

        var isFinished: Bool { !pending.contains { $0 > 0 } }
    }

    static let keys: [URLResourceKey] = [
        .isDirectoryKey, .isSymbolicLinkKey, .isPackageKey, .isVolumeKey,
        .totalFileAllocatedSizeKey, .fileAllocatedSizeKey,
    ]
    private static let keySet = Set(keys)

    let folder: URL
    /// Заполняется один раз перед вызовом `ready`, дальше только читается.
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

    /// Читает содержимое папки и запускает обход подпапок; `ready` — на главном потоке.
    /// Подпапки из `cached` повторно не обходятся.
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

    /// Останавливает все операции; результаты больше не обновляются.
    func cancel() {
        lock.lock()
        cancelled = true
        lock.unlock()
        queue.cancelAllOperations()
    }

    /// Пересчитать одну подпапку заново (внутри что-то поменялось).
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

    /// Перечитать размеры файлов верхнего уровня.
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
                    // Точки монтирования (другие диски, /System/Volumes/Data) не считаем.
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
