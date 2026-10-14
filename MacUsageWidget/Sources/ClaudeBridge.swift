import Foundation

struct ClaudeBridge {
    let directory: URL
    let settings: URL
    let executable: URL
    var metadata: URL { directory.appendingPathComponent("bridge.json") }
    var snapshot: URL { directory.appendingPathComponent("snapshot.json") }
    var command: String { CLI.quote(executable.path) + " --statusline " + CLI.quote(directory.path) }
    init(profile: Profile, root: URL, executable: URL = Bundle.main.executableURL!) {
        self.executable = executable
        let config = profile.useSystem ? URL(fileURLWithPath: ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"] ??
            FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude").path) : profile.home(in: root)
        settings = config.appendingPathComponent("settings.json")
        directory = profile.useSystem ? root.appendingPathComponent("SystemClaudeBridge") : config.appendingPathComponent("UsageBridge")
    }
    private func read(_ url: URL) throws -> Object {
        if !FileManager.default.fileExists(atPath: url.path) { return [:] }
        return try JSON.object(Data(contentsOf: url))
    }
    func install(replaceExisting: Bool = true) throws {
        var value = try read(settings)
        let old = try read(metadata)
        let current = (value["statusLine"] as? Object)?["command"] as? String
        if current == command { return }
        let owned = current != nil && current == old["command"] as? String
        // A periodic refresh may repair a moved app's path, but must not undo the user's later edits.
        if !replaceExisting && old["command"] != nil && !owned { return }
        let previous = owned ? old["previousStatusLine"] ?? NSNull() : value["statusLine"] ?? NSNull()
        if !owned { try JSON.writeObject(value, to: directory.appendingPathComponent("settings-backup.json")) }
        try JSON.writeObject(["command": command, "previousStatusLine": previous], to: metadata)
        value["statusLine"] = ["type": "command", "command": command]
        try JSON.writeObject(value, to: settings)
    }
    func uninstall() throws {
        guard FileManager.default.fileExists(atPath: metadata.path) else { return }
        let old = try read(metadata)
        var value = try read(settings)
        if let current = (value["statusLine"] as? Object)?["command"] as? String, current == old["command"] as? String {
            if let previous = old["previousStatusLine"], !(previous is NSNull) { value["statusLine"] = previous }
            else { value.removeValue(forKey: "statusLine") }
            try JSON.writeObject(value, to: settings)
        }
        try? FileManager.default.removeItem(at: metadata)
        try? FileManager.default.removeItem(at: snapshot)
    }
    func bind(_ identity: String) throws {
        let file = directory.appendingPathComponent("identity.json")
        if JSON.read(file, as: String.self) != identity {
            try? FileManager.default.removeItem(at: snapshot)
            try? FileManager.default.removeItem(at: directory.appendingPathComponent("usage-cache.json"))
            try JSON.write(identity, to: file)
        }
    }
    func cached() -> Usage? {
        let query = JSON.read(directory.appendingPathComponent("usage-cache.json"), as: Usage.self)
        let status = (try? JSON.object(Data(contentsOf: snapshot))).map { UsageParser.claude($0, snapshot: true) }
        return [query, status].compactMap { $0 }.filter { $0.status == "ready" && $0.updatedAt != nil }
            .max { $0.updatedAt! < $1.updatedAt! }
    }
    func save(_ usage: Usage) throws {
        if usage.status == "ready" { try JSON.write(usage, to: directory.appendingPathComponent("usage-cache.json")) }
    }
    // Invoked by Claude's documented statusLine hook. No prompts, credentials, or conversation content are saved.
    static func receive(directory: URL) {
        let data = FileHandle.standardInput.readDataToEndOfFile()
        if let input = try? JSON.object(data), let limits = input["rate_limits"] as? Object {
            var extracted: Object = [:]
            for key in ["five_hour", "seven_day"] {
                if let item = limits[key] as? Object, let used = UsageParser.percentage(item["used_percentage"]) {
                    var output: Object = ["used_percentage": used]
                    output["resets_at"] = UsageParser.timestamp(item["resets_at"])
                    extracted[key] = output
                }
            }
            try? JSON.writeObject(["updatedAt": ISO8601DateFormatter().string(from: Date()), "rate_limits": extracted],
                                  to: directory.appendingPathComponent("snapshot.json"))
        }
        guard let metadata = try? JSON.object(Data(contentsOf: directory.appendingPathComponent("bridge.json"))),
              let original = (metadata["previousStatusLine"] as? Object)?["command"] as? String, !original.isEmpty else {
            print("AI Usage connected", terminator: ""); return
        }
        // Preserve the original command's stdout without persisting the full statusLine payload.
        do {
            let input = Pipe()
            let process = Process(); process.executableURL = URL(fileURLWithPath: "/bin/sh")
            process.arguments = ["-c", original]; process.standardInput = input
            process.standardOutput = FileHandle.standardOutput; process.standardError = FileHandle.nullDevice
            try process.run()
            DispatchQueue.global().async {
                try? input.fileHandleForWriting.write(contentsOf: data)
                try? input.fileHandleForWriting.close()
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + 3) { if process.isRunning { process.terminate() } }
            DispatchQueue.global().asyncAfter(deadline: .now() + 4) { if process.isRunning { kill(process.processIdentifier, SIGKILL) } }
            process.waitUntilExit()
        } catch { }
    }
}
