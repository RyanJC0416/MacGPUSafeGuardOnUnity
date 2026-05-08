import Foundation
import SwiftUI

enum BusyKind: String, Hashable, Sendable {
    case watchdog, p4, injector, apply
}

@MainActor
final class AppState: ObservableObject {
    @Published var p4Binary: String { didSet { UserDefaults.standard.set(p4Binary, forKey: DefaultsKey.p4Binary) } }
    @Published var unityProjectPath: String { didSet { UserDefaults.standard.set(unityProjectPath, forKey: DefaultsKey.unityProjectPath) } }
    @Published var unityEditorBinary: String { didSet { UserDefaults.standard.set(unityEditorBinary, forKey: DefaultsKey.unityEditorBinary) } }
    @Published var defaultChangelist: String { didSet { UserDefaults.standard.set(defaultChangelist, forKey: DefaultsKey.defaultChangelist) } }

    @Published var watchdogStatus: WatchdogStatus = WatchdogStatus(running: false, pid: nil, raw: "(unknown)")
    @Published var watchdogLogTail: String = ""
    @Published var watchdogLastError: String = ""
    @Published var lastKillEvent: KillEvent? = nil
    @Published var watchdogRunningConfig: WatchdogConfig? = nil

    @Published var p4Env: P4Env = P4Env()
    @Published var p4Error: String? = nil
    @Published var changelists: [P4Changelist] = []

    @Published var injectorTargets: [InjectorTarget] = UnityInjector.targetSpecs
    @Published var lastApplyResults: [InjectorResult] = []
    @Published var processList: String = ""
    @Published var updateStatus: UpdateStatus = .idle

    @Published private(set) var busy: Set<BusyKind> = []

    private var refreshTimer: Timer?

    var pathsDriftedFromRunningWatchdog: Bool {
        guard watchdogStatus.running, let cfg = watchdogRunningConfig else { return false }
        return cfg.project != unityProjectPath || cfg.editor != unityEditorBinary
    }

    var iconState: WatchdogIconState {
        if let evt = lastKillEvent, Date().timeIntervalSince(evt.date) < 60 {
            return .recentKill
        }
        return watchdogStatus.running ? .on : .off
    }

    init() {
        let d = UserDefaults.standard
        d.register(defaults: [
            DefaultsKey.p4Binary: "",
            DefaultsKey.unityProjectPath: "",
            DefaultsKey.unityEditorBinary: "",
            DefaultsKey.defaultChangelist: "",
        ])
        self.p4Binary = d.string(forKey: DefaultsKey.p4Binary) ?? ""
        self.unityProjectPath = d.string(forKey: DefaultsKey.unityProjectPath) ?? ""
        self.unityEditorBinary = d.string(forKey: DefaultsKey.unityEditorBinary) ?? ""
        self.defaultChangelist = d.string(forKey: DefaultsKey.defaultChangelist) ?? ""
        startBackgroundRefresh()
        checkForUpdates()
    }

    func makeP4() -> P4Manager {
        P4Manager(p4Binary: p4Binary, cwd: unityProjectPath.isEmpty ? nil : unityProjectPath)
    }

    func makeInjector() -> UnityInjector {
        UnityInjector(unityProjectPath: unityProjectPath, p4: makeP4())
    }

    func activateWatchdog() {
        guard !busy.contains(.watchdog) else { return }
        watchdogLastError = ""
        guard !unityProjectPath.isEmpty, !unityEditorBinary.isEmpty else {
            watchdogLastError = "Unity project path or Unity editor binary not set"
            return
        }
        busy.insert(.watchdog)
        let projectPath = unityProjectPath
        let editorBin = unityEditorBinary
        Task.detached(priority: .userInitiated) {
            // pre-check: don't re-render if already running, so on-disk script keeps reflecting the live process config
            let pre = Watchdog.status()
            let renderErr: String? = pre.running ? nil : {
                do {
                    try Watchdog.renderScript(unityProjectPath: projectPath, unityEditorBinary: editorBin)
                    return nil
                } catch {
                    return "render script failed: \(error.localizedDescription)"
                }
            }()
            let startErr: String? = {
                guard renderErr == nil else { return nil }
                let r = Watchdog.start()
                if !r.ok && !r.stdout.contains("already running") {
                    return "start failed: \((r.stderr.isEmpty ? r.stdout : r.stderr).trimmingCharacters(in: .whitespacesAndNewlines))"
                }
                return nil
            }()
            let status = Watchdog.status()
            let logTail = Watchdog.tailLog(lines: 80)
            let lastKill = Watchdog.parseLastKillEvent(from: logTail)
            let runningCfg = Watchdog.currentRunningConfig()
            await MainActor.run {
                if let renderErr {
                    self.watchdogLastError = renderErr
                } else if let startErr {
                    self.watchdogLastError = startErr
                }
                self.watchdogStatus = status
                self.watchdogLogTail = logTail
                self.lastKillEvent = lastKill
                self.watchdogRunningConfig = runningCfg
                self.busy.remove(.watchdog)
            }
        }
    }

    func deactivateWatchdog() {
        guard !busy.contains(.watchdog) else { return }
        watchdogLastError = ""
        busy.insert(.watchdog)
        Task.detached(priority: .userInitiated) {
            let r = Watchdog.stop()
            let stopErr: String? = r.ok ? nil :
                "stop failed: \((r.stderr.isEmpty ? r.stdout : r.stderr).trimmingCharacters(in: .whitespacesAndNewlines))"
            let status = Watchdog.status()
            let logTail = Watchdog.tailLog(lines: 80)
            let lastKill = Watchdog.parseLastKillEvent(from: logTail)
            let runningCfg = Watchdog.currentRunningConfig()
            await MainActor.run {
                if let stopErr {
                    self.watchdogLastError = stopErr
                }
                self.watchdogStatus = status
                self.watchdogLogTail = logTail
                self.lastKillEvent = lastKill
                self.watchdogRunningConfig = runningCfg
                self.busy.remove(.watchdog)
            }
        }
    }

    func refreshWatchdog() {
        guard !busy.contains(.watchdog) else { return }
        busy.insert(.watchdog)
        Task.detached(priority: .userInitiated) {
            let s = Watchdog.status()
            let t = Watchdog.tailLog(lines: 80)
            let lastKill = Watchdog.parseLastKillEvent(from: t)
            let runningCfg = Watchdog.currentRunningConfig()
            await MainActor.run {
                self.watchdogStatus = s
                self.watchdogLogTail = t
                self.lastKillEvent = lastKill
                self.watchdogRunningConfig = runningCfg
                self.busy.remove(.watchdog)
            }
        }
    }

    func killUnityEditor() {
        Task.detached(priority: .userInitiated) {
            _ = KillTool.killEditor()
        }
    }

    func killUnityHub() {
        Task.detached(priority: .userInitiated) {
            _ = KillTool.killHub()
            await MainActor.run {
                self.refreshProcessList()
            }
        }
    }

    func refreshProcessList() {
        Task.detached(priority: .userInitiated) {
            let r = KillTool.list()
            let output = r.stdout.isEmpty ? (r.stderr.isEmpty ? "(no output)" : r.stderr) : r.stdout
            await MainActor.run {
                self.processList = output
            }
        }
    }

    func refreshP4() {
        guard !busy.contains(.p4) else { return }
        busy.insert(.p4)
        let p4 = makeP4()
        Task.detached(priority: .userInitiated) {
            let (envR, envErr) = p4.readEnv()
            let cls: [P4Changelist]
            let finalErr: String?
            if envErr == nil {
                let (list, listErr) = p4.listPendingChangelists(env: envR)
                cls = list
                finalErr = listErr
            } else {
                cls = []
                finalErr = envErr
            }
            await MainActor.run {
                self.p4Env = envR
                self.p4Error = finalErr
                self.changelists = cls
                self.busy.remove(.p4)
            }
        }
    }

    func refreshInjector() {
        guard !busy.contains(.injector) else { return }
        busy.insert(.injector)
        let inj = makeInjector()
        Task.detached(priority: .userInitiated) {
            let targets = inj.check()
            await MainActor.run {
                self.injectorTargets = targets
                self.busy.remove(.injector)
            }
        }
    }

    func captureTemplates() {
        guard !busy.contains(.injector) else { return }
        busy.insert(.injector)
        let inj = makeInjector()
        Task.detached(priority: .userInitiated) {
            let captureErr: String? = {
                do {
                    try inj.captureTemplates()
                    return nil
                } catch {
                    return error.localizedDescription
                }
            }()
            let targets = inj.check()
            await MainActor.run {
                if let captureErr {
                    self.lastApplyResults = [
                        InjectorResult(basename: "(capture)", action: "ERROR: \(captureErr)", ok: false)
                    ]
                }
                self.injectorTargets = targets
                self.busy.remove(.injector)
            }
        }
    }

    func applyInnerSafe() {
        guard !busy.contains(.apply) else { return }
        busy.insert(.apply)
        let inj = makeInjector()
        let cl = defaultChangelist
        Task.detached(priority: .userInitiated) {
            let results = inj.apply(changelist: cl)
            let targets = inj.check()
            await MainActor.run {
                self.lastApplyResults = results
                self.injectorTargets = targets
                self.busy.remove(.apply)
            }
        }
    }

    func checkForUpdates() {
        guard updateStatus != .checking && updateStatus != .downloading(progress: "") && updateStatus != .installing else { return }
        updateStatus = .checking
        Task.detached(priority: .background) {
            let result = await Updater.check()
            await MainActor.run {
                if let err = result.error {
                    self.updateStatus = .error(err)
                } else if result.hasUpdate, let url = result.downloadURL {
                    self.updateStatus = .available(version: result.latestVersion, url: url)
                } else {
                    self.updateStatus = .upToDate
                }
            }
        }
    }

    func downloadAndInstallUpdate(version: String, url: String) {
        guard updateStatus != .downloading(progress: "") && updateStatus != .installing else { return }
        updateStatus = .downloading(progress: "0%")
        Task.detached(priority: .userInitiated) {
            let err = await Updater.download(version: version, url: url)
            await MainActor.run {
                if let err = err {
                    self.updateStatus = .error(err)
                    return
                }
                self.updateStatus = .downloaded(version: version)
                let installErr = Updater.install(version: version)
                if let installErr = installErr {
                    self.updateStatus = .error(installErr)
                } else {
                    self.updateStatus = .installing
                    NSApplication.shared.terminate(nil)
                }
            }
        }
    }

    func startBackgroundRefresh() {
        guard refreshTimer == nil else { return }
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 3.0, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refreshWatchdog() }
        }
    }

    func stopBackgroundRefresh() {
        refreshTimer?.invalidate()
        refreshTimer = nil
    }
}
