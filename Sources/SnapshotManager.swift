import Foundation

struct SnapshotSizes: Equatable {
    var totalBytes: Int64 = 0
    var editorLogBytes: Int64 = 0
    var sampleBytes: Int64 = 0
    var summaryBytes: Int64 = 0
    var olderThan7DaysBytes: Int64 = 0
    var killDumpBytes: Int64 = 0
    var totalCount: Int = 0
    var editorLogCount: Int = 0
    var sampleCount: Int = 0
    var summaryCount: Int = 0
    var olderThan7DaysCount: Int = 0
    var killDumpCount: Int = 0

    func formatted(_ bytes: Int64) -> String {
        let gb = Double(bytes) / 1024 / 1024 / 1024
        if gb >= 1.0 { return String(format: "%.1f GB", gb) }
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
    static let watchdogDir: URL = FileManager.default
        .homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/MacGPUSafeGuard/watchdog", isDirectory: true)

    static let killDumpDir: URL = FileManager.default
        .homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/MacGPUSafeGuard/snapshots", isDirectory: true)

    private static let editorLogPrefix = "Editor.log.snapshot_"
    private static let samplePrefix = "Unity.sample_"
    private static let summaryPrefix = "watchdog_summary_"

    static func computeSizes() -> SnapshotSizes {
        var sizes = SnapshotSizes()
        let fm = FileManager.default
        let cutoff = Date(timeIntervalSinceNow: -7 * 24 * 3600)

        // 1) Watchdog snapshot files (flat files in watchdog/)
        if let entries = try? fm.contentsOfDirectory(atPath: watchdogDir.path) {
            for name in entries {
                guard isWatchdogSnapshotFile(name) else { continue }
                let url = watchdogDir.appendingPathComponent(name)
                guard let attrs = try? fm.attributesOfItem(atPath: url.path) else { continue }
                let size = (attrs[.size] as? Int64) ?? 0
                let mtime = (attrs[.modificationDate] as? Date) ?? Date()

                sizes.totalBytes += size
                sizes.totalCount += 1
                if name.hasPrefix(editorLogPrefix) {
                    sizes.editorLogBytes += size; sizes.editorLogCount += 1
                } else if name.hasPrefix(samplePrefix) {
                    sizes.sampleBytes += size; sizes.sampleCount += 1
                } else if name.hasPrefix(summaryPrefix) {
                    sizes.summaryBytes += size; sizes.summaryCount += 1
                }
                if mtime < cutoff {
                    sizes.olderThan7DaysBytes += size; sizes.olderThan7DaysCount += 1
                }
            }
        }

        // 2) Kill dump directories (Hub_* / Editor_* under snapshots/)
        if let entries = try? fm.contentsOfDirectory(atPath: killDumpDir.path) {
            for name in entries {
                guard isKillDumpDir(name) else { continue }
                let dirUrl = killDumpDir.appendingPathComponent(name)
                let (dirSize, dirMtime) = directorySizeAndMtime(dirUrl)
                sizes.totalBytes += dirSize
                sizes.totalCount += 1
                sizes.killDumpBytes += dirSize
                sizes.killDumpCount += 1
                if dirMtime < cutoff {
                    sizes.olderThan7DaysBytes += dirSize
                    sizes.olderThan7DaysCount += 1
                }
            }
        }

        return sizes
    }

    static func deleteAll() -> SnapshotDeleteResult {
        let a = deleteWatchdog { _, _ in true }
        let b = deleteKillDumps { _, _ in true }
        return combine(a, b)
    }

    static func deleteOlderThan(days: Int) -> SnapshotDeleteResult {
        let cutoff = Date(timeIntervalSinceNow: -Double(days) * 24 * 3600)
        let a = deleteWatchdog { _, mtime in mtime < cutoff }
        let b = deleteKillDumps { _, mtime in mtime < cutoff }
        return combine(a, b)
    }

    static func deleteWatchdogSnapshots() -> SnapshotDeleteResult {
        return deleteWatchdog { _, _ in true }
    }

    static func deleteKillDumpsOnly() -> SnapshotDeleteResult {
        return deleteKillDumps { _, _ in true }
    }

    static func deleteEditorLogsOnly() -> SnapshotDeleteResult {
        return deleteWatchdog { name, _ in name.hasPrefix(editorLogPrefix) }
    }

    private static func deleteWatchdog(filter: (String, Date) -> Bool) -> SnapshotDeleteResult {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(atPath: watchdogDir.path) else {
            return SnapshotDeleteResult(deletedCount: 0, freedBytes: 0, error: nil)
        }
        var deletedCount = 0
        var freedBytes: Int64 = 0
        var lastError: String?
        for name in entries {
            guard isWatchdogSnapshotFile(name) else { continue }
            let url = watchdogDir.appendingPathComponent(name)
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

    private static func deleteKillDumps(filter: (String, Date) -> Bool) -> SnapshotDeleteResult {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(atPath: killDumpDir.path) else {
            return SnapshotDeleteResult(deletedCount: 0, freedBytes: 0, error: nil)
        }
        var deletedCount = 0
        var freedBytes: Int64 = 0
        var lastError: String?
        for name in entries {
            guard isKillDumpDir(name) else { continue }
            let dirUrl = killDumpDir.appendingPathComponent(name)
            let (dirSize, dirMtime) = directorySizeAndMtime(dirUrl)
            guard filter(name, dirMtime) else { continue }
            do {
                try fm.removeItem(at: dirUrl)
                deletedCount += 1
                freedBytes += dirSize
            } catch {
                lastError = error.localizedDescription
            }
        }
        return SnapshotDeleteResult(deletedCount: deletedCount, freedBytes: freedBytes, error: lastError)
    }

    private static func combine(_ a: SnapshotDeleteResult, _ b: SnapshotDeleteResult) -> SnapshotDeleteResult {
        let err = a.error ?? b.error
        return SnapshotDeleteResult(
            deletedCount: a.deletedCount + b.deletedCount,
            freedBytes: a.freedBytes + b.freedBytes,
            error: err
        )
    }

    private static func isWatchdogSnapshotFile(_ name: String) -> Bool {
        return name.hasPrefix(editorLogPrefix)
            || name.hasPrefix(samplePrefix)
            || name.hasPrefix(summaryPrefix)
    }

    private static func isKillDumpDir(_ name: String) -> Bool {
        return name.hasPrefix("Hub_") || name.hasPrefix("Editor_")
    }

    private static func directorySizeAndMtime(_ dir: URL) -> (Int64, Date) {
        let fm = FileManager.default
        var total: Int64 = 0
        var newest = Date(timeIntervalSince1970: 0)
        guard let enumerator = fm.enumerator(at: dir, includingPropertiesForKeys: [.fileSizeKey, .contentModificationDateKey]) else {
            return (0, Date())
        }
        for case let url as URL in enumerator {
            let attrs = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
            if let s = attrs?.fileSize { total += Int64(s) }
            if let m = attrs?.contentModificationDate, m > newest { newest = m }
        }
        return (total, newest)
    }
}

