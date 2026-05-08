import Foundation

struct P4Manager {
    let p4Binary: String
    let cwd: String?

    func readEnv() -> (P4Env, String?) {
        var env = P4Env()
        let setR = Shell.run(p4Binary, args: ["set"], cwd: cwd)
        let infoR = Shell.run(p4Binary, args: ["info"], cwd: cwd)
        if !setR.ok && !infoR.ok {
            let err = "p4 set: \(setR.stderr.isEmpty ? setR.stdout : setR.stderr)\np4 info: \(infoR.stderr.isEmpty ? infoR.stdout : infoR.stderr)"
            return (env, err)
        }
        for raw in setR.stdout.split(separator: "\n") {
            let s = String(raw)
            if s.hasPrefix("P4USER=") { env.user = parseSetValue(s) }
            else if s.hasPrefix("P4CLIENT=") { env.client = parseSetValue(s) }
            else if s.hasPrefix("P4HOST=") { env.host = parseSetValue(s) }
        }
        for raw in infoR.stdout.split(separator: "\n") {
            let s = String(raw)
            if let v = strip(prefix: "User name: ", from: s) { env.user = v }
            else if let v = strip(prefix: "Client name: ", from: s) { env.client = v }
            else if let v = strip(prefix: "Client host: ", from: s) { env.host = v }
            else if let v = strip(prefix: "Server address: ", from: s) { env.serverAddress = v }
            else if let v = strip(prefix: "Client root: ", from: s) { env.clientRoot = v }
        }
        return (env, nil)
    }

    func listPendingChangelists(env: P4Env) -> ([P4Changelist], String?) {
        var args = ["changes", "-s", "pending", "-l"]
        if !env.user.isEmpty { args += ["-u", env.user] }
        if !env.client.isEmpty { args += ["-c", env.client] }
        let r = Shell.run(p4Binary, args: args, cwd: cwd)
        if !r.ok {
            return ([], r.stderr.isEmpty ? r.stdout : r.stderr)
        }

        var result: [P4Changelist] = []
        var currentId: String? = nil
        var currentDesc: [String] = []

        func commit() {
            guard let id = currentId else { return }
            let desc = currentDesc.joined(separator: " ")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let isMac = desc.contains("Mac 适配") || desc.contains("[Mac")
            result.append(P4Changelist(id: id, description: desc, isMacAdaptation: isMac))
            currentId = nil
            currentDesc = []
        }

        for raw in r.stdout.split(separator: "\n", omittingEmptySubsequences: false) {
            let s = String(raw)
            if s.hasPrefix("Change ") {
                commit()
                let parts = s.split(separator: " ")
                if parts.count > 1 { currentId = String(parts[1]) }
            } else {
                let trimmed = s.trimmingCharacters(in: .whitespaces)
                if !trimmed.isEmpty, currentId != nil {
                    currentDesc.append(trimmed)
                }
            }
        }
        commit()
        return (result, nil)
    }

    func edit(file: String, changelist: String) -> ShellResult {
        var args = ["edit"]
        if !changelist.isEmpty { args += ["-c", changelist] }
        args.append(file)
        return Shell.run(p4Binary, args: args, cwd: cwd)
    }

    func add(file: String, changelist: String) -> ShellResult {
        var args = ["add"]
        if !changelist.isEmpty { args += ["-c", changelist] }
        args.append(file)
        return Shell.run(p4Binary, args: args, cwd: cwd)
    }

    private func parseSetValue(_ line: String) -> String {
        let afterEq = line.split(separator: "=", maxSplits: 1).last.map(String.init) ?? ""
        let firstToken = afterEq.split(separator: " ").first.map(String.init) ?? afterEq
        return firstToken
    }

    private func strip(prefix: String, from s: String) -> String? {
        guard s.hasPrefix(prefix) else { return nil }
        return String(s.dropFirst(prefix.count)).trimmingCharacters(in: .whitespaces)
    }
}
