import Foundation

enum Watchdog {
    static func renderScript(unityProjectPath: String, unityEditorBinary: String) throws {
        guard let url = Bundle.main.url(forResource: "watchdog.sh", withExtension: "tmpl") else {
            throw NSError(domain: "Watchdog", code: 1, userInfo: [NSLocalizedDescriptionKey: "watchdog.sh.tmpl not found in bundle Resources"])
        }
        let tmpl = try String(contentsOf: url, encoding: .utf8)
        let script = tmpl
            .replacingOccurrences(of: "@@PROJECT_PATH@@", with: unityProjectPath)
            .replacingOccurrences(of: "@@UNITY_BIN@@", with: unityEditorBinary)
            .replacingOccurrences(of: "@@BASE_DIR@@", with: AppPaths.watchdogBaseDir.path)
        try FileManager.default.createDirectory(at: AppPaths.watchdogBaseDir, withIntermediateDirectories: true)
        try script.write(to: AppPaths.watchdogScript, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o755)],
            ofItemAtPath: AppPaths.watchdogScript.path
        )
    }

    static func start() -> ShellResult {
        Shell.bash(AppPaths.watchdogScript.path, args: ["start"])
    }

    static func stop() -> ShellResult {
        Shell.bash(AppPaths.watchdogScript.path, args: ["stop"])
    }

    static func status() -> WatchdogStatus {
        guard FileManager.default.fileExists(atPath: AppPaths.watchdogScript.path) else {
            return WatchdogStatus(running: false, pid: nil, raw: "(script not initialized)")
        }
        let r = Shell.bash(AppPaths.watchdogScript.path, args: ["status"])
        let raw = r.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        var pid: Int? = nil
        var running = false
        if raw.contains("watchdog running") {
            running = true
            if let range = raw.range(of: "pid=") {
                let after = raw[range.upperBound...]
                let digits = after.prefix { $0.isNumber }
                pid = Int(digits)
            }
        }
        return WatchdogStatus(running: running, pid: pid, raw: raw.isEmpty ? "(no output)" : raw)
    }

    static func tailLog(lines: Int = 50) -> String {
        guard FileManager.default.fileExists(atPath: AppPaths.watchdogLog.path) else { return "" }
        let r = Shell.run("/usr/bin/tail", args: ["-n", String(lines), AppPaths.watchdogLog.path])
        return r.stdout
    }

    private static let killEventFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone.current
        return f
    }()

    static func parseLastKillEvent(from logTail: String) -> KillEvent? {
        // log lines look like: [2026-05-07 20:34:48] freeze detected: <reason>
        for raw in logTail.split(separator: "\n").reversed() {
            let line = String(raw)
            guard line.hasPrefix("["), let rb = line.firstIndex(of: "]") else { continue }
            let dateStr = String(line[line.index(after: line.startIndex)..<rb])
            guard let date = killEventFormatter.date(from: dateStr) else { continue }
            let rest = line[line.index(after: rb)...]
            guard let r = rest.range(of: "freeze detected:") else { continue }
            let reason = rest[r.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
            return KillEvent(date: date, reason: reason)
        }
        return nil
    }

    static func currentRunningConfig() -> WatchdogConfig? {
        guard let text = try? String(contentsOf: AppPaths.watchdogScript, encoding: .utf8) else { return nil }
        guard let project = extractShellValue(text, key: "PROJECT_PATH"),
              let editor = extractShellValue(text, key: "UNITY_BIN") else { return nil }
        return WatchdogConfig(project: project, editor: editor)
    }

    private static func extractShellValue(_ text: String, key: String) -> String? {
        let pattern = "^\(NSRegularExpression.escapedPattern(for: key))=\"([^\"]*)\""
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.anchorsMatchLines]) else {
            return nil
        }
        let range = NSRange(text.startIndex..., in: text)
        guard let match = regex.firstMatch(in: text, options: [], range: range),
              match.numberOfRanges >= 2,
              let valRange = Range(match.range(at: 1), in: text) else {
            return nil
        }
        return String(text[valRange])
    }
}
