// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "FinderSizes",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "FinderSizes", path: "Sources/FinderSizes")
    ]
)
