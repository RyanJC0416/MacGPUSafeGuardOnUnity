import Foundation
import CryptoKit

struct UnityInjector {
    let unityProjectPath: String
    let p4: P4Manager

    static let targetSpecs: [InjectorTarget] = [
        InjectorTarget(
            id: "MacGPUSafeGuard",
            basename: "MacGPUSafeGuard.cs",
            relativePath: "Assets/scripts/Performance/MacGPUSafeGuard.cs"
        ),
        InjectorTarget(
            id: "MacGPUConfig",
            basename: "MacGPUConfig.cs",
            relativePath: "Assets/scripts/Performance/MacGPUConfig.cs"
        ),
        InjectorTarget(
            id: "SetURPSettings",
            basename: "SetURPSettings.cs",
            relativePath: "Assets/scripts/Framework/hot_update/ScriptLoader/Steps/SetURPSettings.cs"
        ),
    ]

    func check() -> [InjectorTarget] {
        guard !unityProjectPath.isEmpty else { return Self.targetSpecs }
        let projectURL = URL(fileURLWithPath: unityProjectPath)
        return Self.targetSpecs.map { spec in
            var t = spec
            let templateURL = AppPaths.templatesDir.appendingPathComponent(spec.basename)
            let targetURL = projectURL.appendingPathComponent(spec.relativePath)
            if !FileManager.default.fileExists(atPath: templateURL.path) {
                t.status = .templateMissing
            } else if !FileManager.default.fileExists(atPath: targetURL.path) {
                t.status = .missing
            } else {
                let a = Self.sha256(of: templateURL)
                let b = Self.sha256(of: targetURL)
                if let a, let b, a == b {
                    t.status = .inSync
                } else {
                    t.status = .drift
                }
            }
            return t
        }
    }

    func captureTemplates() throws {
        guard !unityProjectPath.isEmpty else {
            throw NSError(domain: "Injector", code: 1, userInfo: [NSLocalizedDescriptionKey: "Unity project path not set"])
        }
        let projectURL = URL(fileURLWithPath: unityProjectPath)
        try FileManager.default.createDirectory(at: AppPaths.templatesDir, withIntermediateDirectories: true)
        for spec in Self.targetSpecs {
            let src = projectURL.appendingPathComponent(spec.relativePath)
            let dst = AppPaths.templatesDir.appendingPathComponent(spec.basename)
            guard FileManager.default.fileExists(atPath: src.path) else { continue }
            if FileManager.default.fileExists(atPath: dst.path) {
                try FileManager.default.removeItem(at: dst)
            }
            try FileManager.default.copyItem(at: src, to: dst)
        }
    }

    func apply(changelist: String) -> [InjectorResult] {
        guard !unityProjectPath.isEmpty else {
            return [InjectorResult(basename: "(all)", action: "ERROR: Unity project path not set", ok: false)]
        }
        let projectURL = URL(fileURLWithPath: unityProjectPath)
        var results: [InjectorResult] = []
        for t in check() {
            let templateURL = AppPaths.templatesDir.appendingPathComponent(t.basename)
            let targetURL = projectURL.appendingPathComponent(t.relativePath)
            switch t.status {
            case .inSync:
                results.append(InjectorResult(basename: t.basename, action: "skipped (in sync)", ok: true))
            case .templateMissing:
                results.append(InjectorResult(basename: t.basename, action: "ERROR: template not captured; press Capture first", ok: false))
            case .missing:
                do {
                    try FileManager.default.createDirectory(
                        at: targetURL.deletingLastPathComponent(),
                        withIntermediateDirectories: true
                    )
                    let data = try Data(contentsOf: templateURL)
                    try data.write(to: targetURL, options: .atomic)
                    let addR = p4.add(file: targetURL.path, changelist: changelist)
                    if addR.ok {
                        results.append(InjectorResult(basename: t.basename, action: "wrote + p4 add ok", ok: true))
                    } else {
                        let snippet = (addR.stderr.isEmpty ? addR.stdout : addR.stderr)
                            .prefix(240)
                        results.append(InjectorResult(basename: t.basename, action: "wrote, p4 add FAILED: \(snippet)", ok: false))
                    }
                } catch {
                    results.append(InjectorResult(basename: t.basename, action: "ERROR (write): \(error.localizedDescription)", ok: false))
                }
            case .drift:
                let editR = p4.edit(file: targetURL.path, changelist: changelist)
                if !editR.ok {
                    let snippet = (editR.stderr.isEmpty ? editR.stdout : editR.stderr).prefix(240)
                    results.append(InjectorResult(basename: t.basename, action: "p4 edit FAILED: \(snippet)", ok: false))
                    continue
                }
                do {
                    let data = try Data(contentsOf: templateURL)
                    try data.write(to: targetURL, options: .atomic)
                    results.append(InjectorResult(basename: t.basename, action: "p4 edit ok + content overwritten", ok: true))
                } catch {
                    results.append(InjectorResult(basename: t.basename, action: "p4 edit ok but write FAILED: \(error.localizedDescription)", ok: false))
                }
            }
        }
        return results
    }

    static func seedDefaultTemplates() {
        let fm = FileManager.default
        let files = try? fm.contentsOfDirectory(atPath: AppPaths.templatesDir.path)
        if let files, !files.isEmpty { return }
        guard let bundleTemplates = Bundle.main.resourceURL?.appendingPathComponent("templates", isDirectory: true) else { return }
        guard fm.fileExists(atPath: bundleTemplates.path) else { return }
        try? fm.createDirectory(at: AppPaths.templatesDir, withIntermediateDirectories: true)
        for spec in targetSpecs {
            let src = bundleTemplates.appendingPathComponent(spec.basename)
            let dst = AppPaths.templatesDir.appendingPathComponent(spec.basename)
            guard fm.fileExists(atPath: src.path) else { continue }
            try? fm.copyItem(at: src, to: dst)
        }
    }

    private static func sha256(of url: URL) -> String? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}
