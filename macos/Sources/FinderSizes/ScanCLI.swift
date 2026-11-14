import Foundation

/// `FinderSizes --scan <folder>`: the same counting as in the panel, printed to the terminal.
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
                    let mark = p.denied[i] ? "  (partially inaccessible)" : ""
                    print(String(repeating: " ", count: max(0, 12 - size.count)) + size + "  " + scan.entries[i].name + mark)
                }
                let total = p.sizes.reduce(0, +)
                print("Total: \(Format.bytes(total)) (\(total) bytes) in \(String(format: "%.2f", elapsed)) s")
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
