import Foundation
import CryptoKit

/// Bundled template channel: runtime GPU scripts vs Mac SceneGuard editor tools.
enum InjectorChannel: String, Sendable {
    case innerSafe = "inner-safe"
    case sceneGuardTools = "scene-guard-tools"

    var bundleSubdirectory: String {
        switch self {
        case .innerSafe: return "templates"
        case .sceneGuardTools: return "scene-guard-tools"
        }
    }

    var displayName: String {
        switch self {
        case .innerSafe: return "Unity Inner Safe"
        case .sceneGuardTools: return "SceneGuard Tools"
        }
    }
}

struct UnityInjector {
    let channel: InjectorChannel
    let unityProjectPath: String
    let p4: P4Manager

    static let innerSafeSpecs: [InjectorTarget] = [
        InjectorTarget(
            id: "MacGPUSafeGuard",
            relativePath: "Assets/scripts/Performance/MacGPUSafeGuard.cs"
        ),
        InjectorTarget(
            id: "MacGPUConfig",
            relativePath: "Assets/scripts/Performance/MacGPUConfig.cs"
        ),
        InjectorTarget(
            id: "SetURPSettings",
            relativePath: "Assets/scripts/Framework/hot_update/ScriptLoader/Steps/SetURPSettings.cs"
        ),
    ]

    static let sceneGuardToolsSpecs: [InjectorTarget] = [
        InjectorTarget(id: "SceneGuardFolder", relativePath: "Assets/Editor/SceneGuard.meta"),
        InjectorTarget(id: "SceneGuardFallback", relativePath: "Assets/Editor/SceneGuardSceneViewFallbackRenderer.cs"),
        InjectorTarget(id: "SceneGuardFallbackMeta", relativePath: "Assets/Editor/SceneGuardSceneViewFallbackRenderer.cs.meta"),
        InjectorTarget(id: "SceneGuardLitShader", relativePath: "Assets/Editor/SceneGuardSceneViewLitFallback.shader"),
        InjectorTarget(id: "SceneGuardLitShaderMeta", relativePath: "Assets/Editor/SceneGuardSceneViewLitFallback.shader.meta"),
        InjectorTarget(id: "SceneGuardSkyShader", relativePath: "Assets/Editor/SceneGuardSceneViewSkyboxFallback.shader"),
        InjectorTarget(id: "SceneGuardSkyShaderMeta", relativePath: "Assets/Editor/SceneGuardSceneViewSkyboxFallback.shader.meta"),
        InjectorTarget(id: "SceneGuardWaterShader", relativePath: "Assets/Editor/SceneGuardSceneViewWaterFallback.shader"),
        InjectorTarget(id: "SceneGuardWaterShaderMeta", relativePath: "Assets/Editor/SceneGuardSceneViewWaterFallback.shader.meta"),
        InjectorTarget(id: "SceneGuardEcoHooks", relativePath: "Assets/Editor/SceneGuard/SceneGuardSceneViewEcoEngineHooks.cs"),
        InjectorTarget(id: "SceneGuardEcoHooksMeta", relativePath: "Assets/Editor/SceneGuard/SceneGuardSceneViewEcoEngineHooks.cs.meta"),
        InjectorTarget(id: "SceneGuardDisableFeatures", relativePath: "Assets/Editor/SceneGuardDisableAllFeatures.cs"),
        InjectorTarget(id: "SceneGuardDisableFeaturesMeta", relativePath: "Assets/Editor/SceneGuardDisableAllFeatures.cs.meta"),
        InjectorTarget(id: "SceneGuardGameTrace", relativePath: "Assets/Editor/SceneGuardGameVsSceneViewTrace.cs"),
        InjectorTarget(id: "SceneGuardGameTraceMeta", relativePath: "Assets/Editor/SceneGuardGameVsSceneViewTrace.cs.meta"),
        InjectorTarget(id: "SceneGuardPipelineTrace", relativePath: "Assets/Editor/SceneGuardSceneViewPipelineTrace.cs"),
        InjectorTarget(id: "SceneGuardPipelineTraceMeta", relativePath: "Assets/Editor/SceneGuardSceneViewPipelineTrace.cs.meta"),
    ]

    private var targetSpecs: [InjectorTarget] {
        switch channel {
        case .innerSafe: return Self.innerSafeSpecs
        case .sceneGuardTools: return Self.sceneGuardToolsSpecs
        }
    }

    private func bundledTemplateURL(for spec: InjectorTarget) -> URL? {
        guard let base = Bundle.main.resourceURL?
            .appendingPathComponent(channel.bundleSubdirectory, isDirectory: true) else { return nil }
        switch channel {
        case .innerSafe:
            return base.appendingPathComponent(spec.basename)
        case .sceneGuardTools:
            return base.appendingPathComponent(spec.relativePath)
        }
    }

    func check() -> [InjectorTarget] {
        guard !unityProjectPath.isEmpty else { return targetSpecs }
        let projectURL = URL(fileURLWithPath: unityProjectPath)
        return targetSpecs.map { spec in
            var t = spec
            guard let templateURL = bundledTemplateURL(for: spec) else {
                t.status = .templateMissing
                return t
            }
            let targetURL = projectURL.appendingPathComponent(spec.relativePath)
            if !FileManager.default.fileExists(atPath: targetURL.path) {
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

    func apply(changelist: String) -> [InjectorResult] {
        guard !unityProjectPath.isEmpty else {
            return [InjectorResult(basename: "(all)", action: "ERROR: Unity project path not set", ok: false)]
        }
        let projectURL = URL(fileURLWithPath: unityProjectPath)
        var results: [InjectorResult] = []
        for t in check() {
            guard let templateURL = bundledTemplateURL(for: t) else {
                results.append(InjectorResult(basename: t.basename, action: "ERROR: bundled template not found", ok: false))
                continue
            }
            let targetURL = projectURL.appendingPathComponent(t.relativePath)
            switch t.status {
            case .inSync:
                results.append(InjectorResult(basename: t.basename, action: "skipped (in sync)", ok: true))
            case .templateMissing:
                results.append(InjectorResult(basename: t.basename, action: "ERROR: bundled template not found", ok: false))
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

    private static func sha256(of url: URL) -> String? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}
