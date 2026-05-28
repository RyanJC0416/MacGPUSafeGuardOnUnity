import Foundation

struct UnityTmpSizes: Equatable {
    var sampleCount: Int = 0
    var sampleBytes: Int64 = 0
    var logBytes: Int64 = 0

    var totalCount: Int { sampleCount + (logBytes > 0 ? 1 : 0) }
    var totalBytes: Int64 { sampleBytes + logBytes }

    func formatted(_ bytes: Int64) -> String {
        let gb = Double(bytes) / 1024 / 1024 / 1024
        if gb >= 1.0 { return String(format: "%.1f GB", gb) }
        let mb = Double(bytes) / 1024 / 1024
        if mb >= 1.0 { return String(format: "%.1f MB", mb) }
        let kb = Double(bytes) / 1024
        return String(format: "%.0f KB", kb)
    }
}

struct UnityTmpDeleteResult {
    let deletedCount: Int
    let freedBytes: Int64
    let error: String?
}

enum UnityTmpCleaner {
    static let tmpDir = URL(fileURLWithPath: "/private/tmp")
    private static let samplePrefix = "Unity_"
    private static let sampleSuffix = ".sample.txt"
    private static let logName = "unity_console_mirror.log"

    static func computeSizes() -> UnityTmpSizes {
        var sizes = UnityTmpSizes()
        let fm = FileManager.default

        guard let entries = try? fm.contentsOfDirectory(atPath: tmpDir.path) else {
            return sizes
        }

        for name in entries {
            let url = tmpDir.appendingPathComponent(name)
            guard let attrs = try? fm.attributesOfItem(atPath: url.path) else { continue }
            let size = (attrs[.size] as? Int64) ?? 0

            if name == logName {
                sizes.logBytes += size
            } else if name.hasPrefix(samplePrefix) && name.hasSuffix(sampleSuffix) {
                sizes.sampleCount += 1
                sizes.sampleBytes += size
            }
        }

        return sizes
    }

    static func clean() -> UnityTmpDeleteResult {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(atPath: tmpDir.path) else {
            return UnityTmpDeleteResult(deletedCount: 0, freedBytes: 0, error: nil)
        }

        var deletedCount = 0
        var freedBytes: Int64 = 0
        var lastError: String?

        for name in entries {
            let url = tmpDir.appendingPathComponent(name)
            guard let attrs = try? fm.attributesOfItem(atPath: url.path) else { continue }
            let size = (attrs[.size] as? Int64) ?? 0

            let shouldDelete = (name == logName) || (name.hasPrefix(samplePrefix) && name.hasSuffix(sampleSuffix))
            guard shouldDelete else { continue }

            do {
                try fm.removeItem(at: url)
                deletedCount += 1
                freedBytes += size
            } catch {
                lastError = error.localizedDescription
            }
        }

        return UnityTmpDeleteResult(deletedCount: deletedCount, freedBytes: freedBytes, error: lastError)
    }
}
