import Foundation

struct ShellResult {
    let exitCode: Int32
    let stdout: String
    let stderr: String
    var ok: Bool { exitCode == 0 }
}

enum Shell {
    static func run(
        _ executable: String,
        args: [String] = [],
        cwd: String? = nil,
        env: [String: String]? = nil
    ) -> ShellResult {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: executable)
        p.arguments = args
        if let cwd, !cwd.isEmpty {
            p.currentDirectoryURL = URL(fileURLWithPath: cwd)
        }
        if let env { p.environment = env }
        p.standardInput = FileHandle.nullDevice
        let outPipe = Pipe()
        let errPipe = Pipe()
        p.standardOutput = outPipe
        p.standardError = errPipe

        var outData = Data()
        var errData = Data()
        let outQ = DispatchQueue(label: "sh.out")
        let errQ = DispatchQueue(label: "sh.err")

        outPipe.fileHandleForReading.readabilityHandler = { fh in
            let d = fh.availableData
            if d.isEmpty {
                fh.readabilityHandler = nil
            } else {
                outQ.sync { outData.append(d) }
            }
        }
        errPipe.fileHandleForReading.readabilityHandler = { fh in
            let d = fh.availableData
            if d.isEmpty {
                fh.readabilityHandler = nil
            } else {
                errQ.sync { errData.append(d) }
            }
        }

        do {
            try p.run()
        } catch {
            outPipe.fileHandleForReading.readabilityHandler = nil
            errPipe.fileHandleForReading.readabilityHandler = nil
            return ShellResult(exitCode: -1, stdout: "", stderr: "spawn failed: \(error.localizedDescription)")
        }
        p.waitUntilExit()

        outPipe.fileHandleForReading.readabilityHandler = nil
        errPipe.fileHandleForReading.readabilityHandler = nil
        let outRem = outPipe.fileHandleForReading.availableData
        let errRem = errPipe.fileHandleForReading.availableData
        outQ.sync { outData.append(outRem) }
        errQ.sync { errData.append(errRem) }

        let outString = outQ.sync { String(data: outData, encoding: .utf8) ?? "" }
        let errString = errQ.sync { String(data: errData, encoding: .utf8) ?? "" }
        return ShellResult(
            exitCode: p.terminationStatus,
            stdout: outString,
            stderr: errString
        )
    }

    static func bash(_ script: String, args: [String] = [], cwd: String? = nil) -> ShellResult {
        run("/bin/bash", args: [script] + args, cwd: cwd)
    }
}
