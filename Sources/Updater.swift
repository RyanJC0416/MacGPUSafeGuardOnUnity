import Foundation

enum UpdateStatus: Equatable {
    case idle
    case checking
    case available(version: String, url: String)
    case downloading(progress: String)
    case downloaded(version: String)
    case installing
    case upToDate
    case error(String)
}

struct UpdateResult {
    let hasUpdate: Bool
    let currentVersion: String
    let latestVersion: String
    let downloadURL: String?
    let error: String?
}

enum Updater {
    static let repo = "RyanJC0416/MacGPUSafeGuardOnUnity"
    static let assetName = "GpuSafeGuard.app.zip"

    static func currentVersion() -> String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0"
    }

    static func check() async -> UpdateResult {
        let apiURL = "https://api.github.com/repos/\(repo)/releases/latest"
        let (output, err) = shell("curl", "-sL", "-H", "Accept: application/vnd.github+json", apiURL)
        guard err == nil || err!.isEmpty else {
            return UpdateResult(hasUpdate: false, currentVersion: currentVersion(), latestVersion: "", downloadURL: nil, error: "API error: \(err!)")
        }
        guard let tag = extractTag(from: output) else {
            return UpdateResult(hasUpdate: false, currentVersion: currentVersion(), latestVersion: "", downloadURL: nil, error: "Cannot parse release info")
        }
        let latest = tag.replacingOccurrences(of: "v", with: "")
        let current = currentVersion()
        let hasUpdate = isVersionGreater(latest, current)
        let url = extractDownloadURL(from: output, assetName: assetName)
        return UpdateResult(
            hasUpdate: hasUpdate,
            currentVersion: current,
            latestVersion: latest,
            downloadURL: url,
            error: nil
        )
    }

    static func download(version: String, url: String) async -> String? {
        let updatesDir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/MacGPUSafeGuard/updates")
        try? FileManager.default.createDirectory(at: updatesDir, withIntermediateDirectories: true)

        let zipPath = updatesDir.appendingPathComponent("GpuSafeGuard_\(version).zip").path
        let extractDir = updatesDir.appendingPathComponent("GpuSafeGuard_\(version)").path

        let (_, err) = shell("curl", "-sL", "-o", zipPath, url)
        guard err == nil || err!.isEmpty else { return "Download failed: \(err!)" }

        let fm = FileManager.default
        try? fm.removeItem(atPath: extractDir)
        try? fm.createDirectory(atPath: extractDir, withIntermediateDirectories: true)

        let (_, unzipErr) = shell("unzip", "-o", "-q", zipPath, "-d", extractDir)
        guard unzipErr == nil || unzipErr!.isEmpty else { return "Unzip failed: \(unzipErr!)" }

        return nil
    }

    static func install(version: String) -> String? {
        guard let appPath = Bundle.main.bundleURL.path as String? else {
            return "Cannot locate current app bundle"
        }
        let appDir = (appPath as NSString).deletingLastPathComponent
        let updatesDir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/MacGPUSafeGuard/updates")
        let extractDir = updatesDir.appendingPathComponent("GpuSafeGuard_\(version)").path

        let script = """
        #!/bin/bash
        LOG="\(updatesDir.path.replacingOccurrences(of: "\"", with: "\\\""))/install.log"
        exec > "$LOG" 2>&1
        echo "[$(date)] updater started"
        sleep 2

        APP="\(appPath.replacingOccurrences(of: "\"", with: "\\\""))"
        APPDIR="\(appDir.replacingOccurrences(of: "\"", with: "\\\""))"
        NEWAPP="\(extractDir.replacingOccurrences(of: "\"", with: "\\\""))/GpuSafeGuard.app"

        # Remove any stale backup
        rm -rf "$APP.old"

        # Move old app out of the way
        if ! mv "$APP" "$APP.old"; then
            echo "ERROR: mv old app failed"
            exit 1
        fi

        # Copy new app
        if ! cp -R "$NEWAPP" "$APPDIR/"; then
            echo "ERROR: cp -R failed"
            # Try to restore old app
            mv "$APP.old" "$APP" || true
            exit 1
        fi

        # Remove quarantine
        xattr -cr "$APP"

        # Open new app
        if ! open "$APP"; then
            echo "ERROR: open failed"
            exit 1
        fi

        echo "[$(date)] new app opened, cleaning up old"
        rm -rf "$APP.old" || true
        echo "[$(date)] updater done"
        """
        let scriptPath = updatesDir.appendingPathComponent("install.sh").path
        do {
            try script.write(toFile: scriptPath, atomically: true, encoding: String.Encoding.utf8)
            chmod(scriptPath, 0o755)
            let task = Process()
            task.executableURL = URL(fileURLWithPath: "/bin/bash")
            task.arguments = [scriptPath]
            try task.run()
        } catch {
            return "Install script failed: \(error.localizedDescription)"
        }
        return nil
    }

    private static func extractTag(from json: String) -> String? {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = obj["tag_name"] as? String else {
            return nil
        }
        return tag
    }

    private static func extractDownloadURL(from json: String, assetName: String) -> String? {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let assets = obj["assets"] as? [[String: Any]] else {
            return nil
        }
        for asset in assets {
            if let name = asset["name"] as? String, name == assetName,
               let url = asset["browser_download_url"] as? String {
                return url
            }
        }
        return nil
    }

    private static func isVersionGreater(_ lhs: String, _ rhs: String) -> Bool {
        let a = lhs.split(separator: ".").compactMap { Int($0) }
        let b = rhs.split(separator: ".").compactMap { Int($0) }
        for i in 0..<max(a.count, b.count) {
            let av = i < a.count ? a[i] : 0
            let bv = i < b.count ? b[i] : 0
            if av != bv { return av > bv }
        }
        return false
    }

    private static func shell(_ args: String...) -> (String, String?) {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        task.arguments = args
        let outPipe = Pipe()
        let errPipe = Pipe()
        task.standardOutput = outPipe
        task.standardError = errPipe
        do {
            try task.run()
            task.waitUntilExit()
        } catch {
            return ("", error.localizedDescription)
        }
        let out = String(data: outPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        let err = String(data: errPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)
        return (out, err?.isEmpty == true ? nil : err)
    }
}
