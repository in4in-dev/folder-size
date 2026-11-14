import Foundation

/// `FinderSizes --scan <папка>`: тот же подсчёт, что и в панели, но с выводом в терминал.
enum ScanCLI {
    static func run(path: String) -> Never {
        let started = Date()
        let scan = FolderScan(folder: URL(fileURLWithPath: path, isDirectory: true))
        scan.start(cached: [:]) {
            let timer = Timer(timeInterval: 0.02, repeats: true) { t in
                let p = scan.snapshot()
                guard p.isFinished else { return }
                t.invalidate()
                let elapsed = Date().timeIntervalSince(started)
                for i in scan.entries.indices.sorted(by: { p.sizes[$0] > p.sizes[$1] }) {
                    let size = Format.bytes(p.sizes[i])
                    let mark = p.denied[i] ? "  (не всё доступно)" : ""
                    print(String(repeating: " ", count: max(0, 12 - size.count)) + size + "  " + scan.entries[i].name + mark)
                }
                let total = p.sizes.reduce(0, +)
                print("Итого: \(Format.bytes(total)) (\(total) байт) за \(String(format: "%.2f", elapsed)) с")
                exit(0)
            }
            RunLoop.main.add(timer, forMode: .common)
        }
        RunLoop.main.run()
        exit(0)
    }
}

enum Format {
    private static let formatter: ByteCountFormatter = {
        let f = ByteCountFormatter()
        f.countStyle = .file
        f.allowsNonnumericFormatting = false
        return f
    }()

    static func bytes(_ value: Int64) -> String {
        formatter.string(fromByteCount: value)
    }
}
