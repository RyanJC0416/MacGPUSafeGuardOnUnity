import Foundation

struct P4Manager {
    let p4Binary: String
    let p4Port: String
    let p4Client: String
    let p4User: String
    let p4Password: String
    let cwd: String?

    private var p4Env: [String: String] {
        var e = ProcessInfo.processInfo.environment
        // GUI apps may have a truncated environment; backfill critical keys so p4 finds ~/.p4tickets etc.
        if e["HOME"] == nil || e["HOME"]!.isEmpty { e["HOME"] = NSHomeDirectory() }
        if e["PATH"] == nil || e["PATH"]!.isEmpty { e["PATH"] = "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin" }
        if e["USER"] == nil || e["USER"]!.isEmpty { e["USER"] = NSUserName() }
        if e["LOGNAME"] == nil || e["LOGNAME"]!.isEmpty { e["LOGNAME"] = NSUserName() }
        if !p4Port.isEmpty { e["P4PORT"] = p4Port }
        if !p4Client.isEmpty { e["P4CLIENT"] = p4Client }
        // auto-detect P4USER from ~/.p4tickets if not manually set
        let user = p4User.isEmpty ? Self.userFromTickets(for: p4Port) : p4User
        if !user.isEmpty { e["P4USER"] = user }
        if !p4Password.isEmpty { e["P4PASSWD"] = p4Password }
        return e
    }

    private static func userFromTickets(for p4Port: String) -> String {
        guard !p4Port.isEmpty else { return "" }
        let ticketsPath = NSHomeDirectory() + "/.p4tickets"
        guard let content = try? String(contentsOfFile: ticketsPath, encoding: .utf8) else { return "" }
        let normalizedPort = p4Port.hasPrefix("ssl:") ? String(p4Port.dropFirst(4)) : p4Port
        for line in content.split(separator: "\n") {
            let parts = line.split(separator: "=", maxSplits: 1)
            guard parts.count == 2 else { continue }
            let server = String(parts[0]).trimmingCharacters(in: .whitespaces)
            let userTicket = String(parts[1]).trimmingCharacters(in: .whitespaces)
            let userParts = userTicket.split(separator: ":", maxSplits: 1)
            guard userParts.count == 2 else { continue }
            let user = String(userParts[0])
            if server == normalizedPort || server == p4Port {
                return user
            }
        }
        return ""
    }

    func readEnv() -> (P4Env, String?) {
        var env = P4Env()
        let infoR = Shell.run(p4Binary, args: ["info"], cwd: cwd, env: p4Env)
        guard infoR.ok else {
            return (env, "p4 info: \(infoR.stderr.isEmpty ? infoR.stdout : infoR.stderr)")
        }
        let setR = Shell.run(p4Binary, args: ["set"], cwd: cwd, env: p4Env)
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
        let r = Shell.run(p4Binary, args: args, cwd: cwd, env: p4Env)
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
        return Shell.run(p4Binary, args: args, cwd: cwd, env: p4Env)
    }

    func add(file: String, changelist: String) -> ShellResult {
        var args = ["add"]
        if !changelist.isEmpty { args += ["-c", changelist] }
        args.append(file)
        return Shell.run(p4Binary, args: args, cwd: cwd, env: p4Env)
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
