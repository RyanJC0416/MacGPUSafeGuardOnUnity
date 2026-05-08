import Foundation

enum AppPaths {
    static let supportDir: URL = {
        let url = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MacGPUSafeGuard", isDirectory: true)
        // fail loud: if Application Support is unwritable, every feature breaks downstream
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()

    static let watchdogScript = supportDir.appendingPathComponent("watchdog.sh")
    static let watchdogBaseDir = supportDir.appendingPathComponent("watchdog", isDirectory: true)
    static let watchdogLog = watchdogBaseDir.appendingPathComponent("watchdog.log")

    static let templatesDir: URL = {
        let url = supportDir.appendingPathComponent("templates", isDirectory: true)
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()
}

enum DefaultsKey {
    static let p4Binary = "gsg.p4Binary"
    static let unityProjectPath = "gsg.unityProjectPath"
    static let unityEditorBinary = "gsg.unityEditorBinary"
    static let defaultChangelist = "gsg.defaultChangelist"
}

struct WatchdogStatus: Equatable {
    var running: Bool
    var pid: Int?
    var raw: String
}

struct WatchdogConfig: Equatable {
    let project: String
    let editor: String
}

struct KillEvent: Equatable {
    let date: Date
    let reason: String
}

enum WatchdogIconState {
    case off, on, recentKill
}

struct P4Env: Equatable {
    var user: String = ""
    var client: String = ""
    var host: String = ""
    var serverAddress: String = ""
    var clientRoot: String = ""
}

struct P4Changelist: Identifiable, Hashable {
    let id: String
    let description: String
    let isMacAdaptation: Bool
    var label: String {
        let trimmed = description.count > 60 ? String(description.prefix(60)) + "…" : description
        return isMacAdaptation ? "\(id)  ★ \(trimmed)" : "\(id)  \(trimmed)"
    }
}

enum FileStatus: String {
    case inSync = "in sync"
    case drift = "drift"
    case missing = "missing"
    case templateMissing = "template missing"
}

struct InjectorTarget: Identifiable, Hashable {
    let id: String
    let basename: String
    let relativePath: String
    var status: FileStatus = .templateMissing
}

struct InjectorResult: Identifiable, Hashable {
    let id = UUID()
    let basename: String
    let action: String
    let ok: Bool
}
