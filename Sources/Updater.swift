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
    
    static func isAppTranslocated() -> Bool {
        guard let appPath = Bundle.main.bundleURL.path as String? else { return false }
        return appPath.contains("/AppTranslocation/") || (appPath.contains("/private/var/folders/") && appPath.contains("/T/"))
    }

    private static func githubToken() -> String? {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        task.arguments = ["find-internet-password", "-s", "github.com", "-w"]
        let pipe = Pipe()
        task.standardOutput = pipe
        task.standardError = Pipe()
        try? task.run()
        task.waitUntilExit()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        return String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func check() async -> UpdateResult {
        let apiURL = "https://api.github.com/repos/\(repo)/releases/latest"
        var curlArgs = ["curl", "-sfL", "-H", "Accept: application/vnd.github+json"]
        if let token = githubToken(), !token.isEmpty {
            curlArgs += ["-H", "Authorization: token \(token)"]
        }
        curlArgs.append(apiURL)
        let api = shell(curlArgs)
        guard !api.failed else {
            return UpdateResult(hasUpdate: false, currentVersion: currentVersion(), latestVersion: "", downloadURL: nil, error: "API error: \(api.errorText)")
        }
        if let msg = extractMessage(from: api.out), msg.lowercased().contains("rate limit") {
            return UpdateResult(hasUpdate: false, currentVersion: currentVersion(), latestVersion: "", downloadURL: nil, error: "GitHub API rate limit exceeded. Retry later.")
        }
        guard let tag = extractTag(from: api.out) else {
            return UpdateResult(hasUpdate: false, currentVersion: currentVersion(), latestVersion: "", downloadURL: nil, error: "Cannot parse release info")
        }
        let latest = tag.replacingOccurrences(of: "v", with: "")
        let current = currentVersion()
        let hasUpdate = isVersionGreater(latest, current)
        let url = extractDownloadURL(from: api.out, assetName: assetName)
        if hasUpdate && (url == nil || url!.isEmpty) {
            return UpdateResult(hasUpdate: true, currentVersion: current, latestVersion: latest, downloadURL: nil, error: "Update \(latest) found but \(assetName) is missing from the release")
        }
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
        let fm = FileManager.default
        var lastError = "Download failed"

        for attempt in 1...3 {
            try? fm.removeItem(atPath: zipPath)
            try? fm.removeItem(atPath: extractDir)

            var curlArgs = [
                "curl", "-fL", "--retry", "3", "--retry-delay", "2",
                "--connect-timeout", "20", "--max-time", "180",
                "-A", "GpuSafeGuard-Updater/\(currentVersion())",
                "-o", zipPath, url
            ]
            if let token = githubToken(), !token.isEmpty {
                curlArgs.insert(contentsOf: ["-H", "Authorization: token \(token)"], at: 1)
            }

            let dl = shell(curlArgs)
            if dl.failed {
                lastError = "Download failed (attempt \(attempt)/3): \(dl.errorText)"
                continue
            }

            guard fm.fileExists(atPath: zipPath) else {
                lastError = "Download failed (attempt \(attempt)/3): zip was not written"
                continue
            }
            let size = (try? fm.attributesOfItem(atPath: zipPath)[.size] as? NSNumber)?.intValue ?? 0
            if size < 1000 {
                lastError = "Download failed (attempt \(attempt)/3): zip too small (\(size) bytes)"
                continue
            }

            try? fm.createDirectory(atPath: extractDir, withIntermediateDirectories: true)
            let unzip = shell(["unzip", "-o", "-q", zipPath, "-d", extractDir])
            if unzip.failed {
                lastError = "Unzip failed (attempt \(attempt)/3): \(unzip.errorText)"
                continue
            }

            let newApp = (extractDir as NSString).appendingPathComponent("GpuSafeGuard.app")
            if fm.fileExists(atPath: newApp) {
                return nil
            }
            lastError = "Unzip failed (attempt \(attempt)/3): GpuSafeGuard.app missing in archive"
        }

        return lastError
    }

    static func install(version: String) -> String? {
        guard let appPath = Bundle.main.bundleURL.path as String? else {
            return "Cannot locate current app bundle"
        }
        
        // Detect App Translocation (macOS quarantine mechanism)
        if appPath.contains("/AppTranslocation/") || appPath.contains("/private/var/folders/") && appPath.contains("/T/") {
            return """
            Cannot update: App is running from a quarantined location.
            
            To fix:
            1. Move GpuSafeGuard.app to /Applications/
            2. Restart the app from /Applications/
            3. Try updating again
            
            This is a macOS security feature (App Translocation) that prevents updates from temporary locations.
            """
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

    private static func extractMessage(from json: String) -> String? {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let msg = obj["message"] as? String else {
            return nil
        }
        return msg
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

    private struct ShellResult {
        let out: String
        let err: String
        let status: Int32

        var failed: Bool { status != 0 }

        var errorText: String {
            let e = err.trimmingCharacters(in: .whitespacesAndNewlines)
            if !e.isEmpty { return e }
            let o = out.trimmingCharacters(in: .whitespacesAndNewlines)
            if !o.isEmpty { return o }
            return "exit \(status)"
        }
    }

    private static func shell(_ args: [String]) -> ShellResult {
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
            return ShellResult(out: "", err: error.localizedDescription, status: -1)
        }
        let out = String(data: outPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        let err = String(data: errPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        return ShellResult(out: out, err: err, status: task.terminationStatus)
    }
}
