import Foundation

struct SnapshotSizes: Equatable {
    var totalBytes: Int64 = 0
    var editorLogBytes: Int64 = 0
    var sampleBytes: Int64 = 0
    var summaryBytes: Int64 = 0
    var olderThan7DaysBytes: Int64 = 0
    var totalCount: Int = 0
    var editorLogCount: Int = 0
    var sampleCount: Int = 0
    var summaryCount: Int = 0
    var olderThan7DaysCount: Int = 0

    func formatted(_ bytes: Int64) -> String {
        let mb = Double(bytes) / 1024 / 1024
        if mb >= 1.0 { return String(format: "%.1f MB", mb) }
        let kb = Double(bytes) / 1024
        return String(format: "%.0f KB", kb)
    }
}

struct SnapshotDeleteResult {
    let deletedCount: Int
    let freedBytes: Int64
    let error: String?
}

enum SnapshotManager {
    static let baseDir: URL = FileManager.default
        .homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/MacGPUSafeGuard/watchdog", isDirectory: true)

    private static let editorLogPrefix = "Editor.log.snapshot_"
    private static let samplePrefix = "Unity.sample_"
    private static let summaryPrefix = "watchdog_summary_"

    static func computeSizes() -> SnapshotSizes {
        var sizes = SnapshotSizes()
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(atPath: baseDir.path) else { return sizes }

        let cutoff = Date(timeIntervalSinceNow: -7 * 24 * 3600)
        for name in entries {
            guard isSnapshotFile(name) else { continue }
            let url = baseDir.appendingPathComponent(name)
            guard let attrs = try? fm.attributesOfItem(atPath: url.path) else { continue }
            let size = (attrs[.size] as? Int64) ?? 0
            let mtime = (attrs[.modificationDate] as? Date) ?? Date()

            sizes.totalBytes += size
            sizes.totalCount += 1

            if name.hasPrefix(editorLogPrefix) {
                sizes.editorLogBytes += size
                sizes.editorLogCount += 1
            } else if name.hasPrefix(samplePrefix) {
                sizes.sampleBytes += size
                sizes.sampleCount += 1
            } else if name.hasPrefix(summaryPrefix) {
                sizes.summaryBytes += size
                sizes.summaryCount += 1
            }

            if mtime < cutoff {
                sizes.olderThan7DaysBytes += size
                sizes.olderThan7DaysCount += 1
            }
        }
        return sizes
    }

    static func deleteAll() -> SnapshotDeleteResult {
        return delete { _, _ in true }
    }

    static func deleteOlderThan(days: Int) -> SnapshotDeleteResult {
        let cutoff = Date(timeIntervalSinceNow: -Double(days) * 24 * 3600)
        return delete { _, mtime in mtime < cutoff }
    }

    static func deleteEditorLogsOnly() -> SnapshotDeleteResult {
        return delete { name, _ in name.hasPrefix(editorLogPrefix) }
    }

    private static func delete(filter: (String, Date) -> Bool) -> SnapshotDeleteResult {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(atPath: baseDir.path) else {
            return SnapshotDeleteResult(deletedCount: 0, freedBytes: 0, error: "Cannot read directory")
        }

        var deletedCount = 0
        var freedBytes: Int64 = 0
        var lastError: String?
        for name in entries {
            guard isSnapshotFile(name) else { continue }
            let url = baseDir.appendingPathComponent(name)
            guard let attrs = try? fm.attributesOfItem(atPath: url.path) else { continue }
            let size = (attrs[.size] as? Int64) ?? 0
            let mtime = (attrs[.modificationDate] as? Date) ?? Date()
            guard filter(name, mtime) else { continue }

            do {
                try fm.removeItem(at: url)
                deletedCount += 1
                freedBytes += size
            } catch {
                lastError = error.localizedDescription
            }
        }
        return SnapshotDeleteResult(deletedCount: deletedCount, freedBytes: freedBytes, error: lastError)
    }

    private static func isSnapshotFile(_ name: String) -> Bool {
        return name.hasPrefix(editorLogPrefix)
            || name.hasPrefix(samplePrefix)
            || name.hasPrefix(summaryPrefix)
    }
}
