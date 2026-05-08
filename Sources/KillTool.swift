import Foundation

enum KillTool {
    static func killEditor() -> ShellResult {
        run(arg: "--editor")
    }

    static func killHub() -> ShellResult {
        run(arg: "--hub")
    }

    static func list() -> ShellResult {
        run(arg: "--list")
    }

    private static func run(arg: String) -> ShellResult {
        guard let url = Bundle.main.url(forResource: "kill-unity", withExtension: "sh") else {
            return ShellResult(exitCode: -1, stdout: "", stderr: "kill-unity.sh missing from app bundle")
        }
        return Shell.bash(url.path, args: [arg])
    }
}
