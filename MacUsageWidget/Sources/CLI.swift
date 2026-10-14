import Foundation
import AppKit

enum CLI {
    static func directories(environment: [String: String] = ProcessInfo.processInfo.environment) -> [String] {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        var paths = (environment["PATH"] ?? "").split(separator: ":").map(String.init)
        paths += [home + "/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", home + "/.npm-global/bin",
                  home + "/.volta/bin", home + "/.asdf/shims", home + "/.bun/bin", "/usr/bin", "/bin"]
        let nvm = URL(fileURLWithPath: home + "/.nvm/versions/node")
        paths += ((try? FileManager.default.contentsOfDirectory(at: nvm, includingPropertiesForKeys: nil)) ?? [])
            .sorted { $0.lastPathComponent.compare($1.lastPathComponent, options: .numeric) == .orderedDescending }
            .map { $0.appendingPathComponent("bin").path }
        return paths
    }
    static func find(_ service: String, environment: [String: String] = ProcessInfo.processInfo.environment) throws -> URL {
        var candidates = [environment["AI_USAGE_\(service.uppercased())_PATH"]].compactMap { $0 }
        candidates += directories(environment: environment).map { $0 + "/" + service }
        if service == "codex" {
            candidates += ["/Applications/Codex.app/Contents/Resources/codex",
                "/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex"]
        }
        guard let path = candidates.first(where: { $0.hasPrefix("/") && FileManager.default.isExecutableFile(atPath: $0) }) else {
            throw WidgetError.message("\(service == "codex" ? "Codex" : "Claude Code") CLI를 찾을 수 없습니다. macOS용 공식 CLI를 설치해주세요.")
        }
        return URL(fileURLWithPath: path)
    }
    static func environment(profile: Profile, root: URL) throws -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = directories().joined(separator: ":")
        if !profile.useSystem {
            let home = profile.home(in: root)
            try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            env[profile.service == "codex" ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR"] = home.path
            let keys = profile.service == "codex" ? ["OPENAI_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL"] :
                env.keys.filter { $0.hasPrefix("ANTHROPIC_") || ["CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_SIMPLE"].contains($0) }
            for key in keys { env.removeValue(forKey: key) }
        }
        return env
    }
    static func quote(_ text: String) -> String { "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'" }
    static func officialLogin(_ string: String) -> URL? {
        guard let url = URL(string: string), url.scheme == "https", url.user == nil, url.password == nil,
              url.port == nil || url.port == 443, ["auth.openai.com", "chatgpt.com"].contains(url.host ?? "") else { return nil }
        return url
    }
}

// All RPC state stays on the main actor. Pipe callbacks only deliver bounded chunks.
@MainActor final class LineProcess {
    private let process = Process()
    private let input = Pipe(), output = Pipe(), errors = Pipe()
    private var buffer = Data()
    private var pending: [String: CheckedContinuation<Object, Error>] = [:]
    private var timers: [String: Task<Void, Never>] = [:]
    var event: ((Object) -> Void)?
    var running: Bool { process.isRunning }
    init(executable: URL, arguments: [String], environment: [String: String], directory: URL) throws {
        process.executableURL = executable; process.arguments = arguments; process.environment = environment
        process.currentDirectoryURL = directory
        process.standardInput = input; process.standardOutput = output; process.standardError = errors
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            Task { @MainActor [weak self] in self?.receive(data) }
        }
        // Never expose CLI stderr, which may contain authentication details.
        errors.fileHandleForReading.readabilityHandler = { handle in if handle.availableData.isEmpty { handle.readabilityHandler = nil } }
        process.terminationHandler = { [weak self] _ in
            Task { @MainActor [weak self] in self?.failAll(WidgetError.message("CLI 연결이 종료되었습니다. 다시 새로고침해주세요.")) }
        }
        do { try process.run() } catch { stop(); throw error }
    }
    func send(_ message: Object) throws {
        guard running else { throw WidgetError.message("CLI가 실행 중이 아닙니다.") }
        var data = try JSONSerialization.data(withJSONObject: message); data.append(10)
        try input.fileHandleForWriting.write(contentsOf: data)
    }
    func request(_ key: String, message: Object, timeout: Double = 30) async throws -> Object {
        try Task.checkCancellation()
        return try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { continuation in
                pending[key] = continuation
                timers[key] = Task { [weak self] in
                    do { try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000)) } catch { return }
                    self?.complete(key, .failure(WidgetError.message("CLI 응답 시간이 초과되었습니다. 로그인과 네트워크를 확인해주세요.")))
                }
                do { try send(message) } catch { complete(key, .failure(error)) }
            }
        }, onCancel: { Task { @MainActor [weak self] in self?.complete(key, .failure(CancellationError())) } })
    }
    private func receive(_ data: Data) {
        guard !data.isEmpty else { output.fileHandleForReading.readabilityHandler = nil; return }
        buffer.append(data)
        guard buffer.count < 8 * 1024 * 1024 else { stop(); return }
        while let end = buffer.firstIndex(of: 10) {
            let line = buffer[..<end]; buffer.removeSubrange(...end)
            guard let message = try? JSON.object(Data(line)) else { continue }
            if let id = message["id"] {
                let key = String(describing: id)
                if message["error"] != nil { complete(key, .failure(WidgetError.message("CLI 요청이 실패했습니다. CLI 업데이트와 로그인을 확인해주세요."))) }
                else { complete(key, .success(message["result"] as? Object ?? [:])) }
            } else if message["type"] as? String == "control_response", let response = message["response"] as? Object,
                      let key = response["request_id"] as? String {
                if response["subtype"] as? String == "success", let result = response["response"] as? Object { complete(key, .success(result)) }
                else { complete(key, .failure(WidgetError.message("Claude 자동 조회를 지원하지 않습니다. Claude Code를 업데이트해주세요."))) }
            } else { event?(message) }
        }
    }
    private func complete(_ key: String, _ result: Result<Object, Error>) {
        timers.removeValue(forKey: key)?.cancel(); pending.removeValue(forKey: key)?.resume(with: result)
    }
    private func failAll(_ error: Error) { for key in Array(pending.keys) { complete(key, .failure(error)) } }
    func stop() {
        output.fileHandleForReading.readabilityHandler = nil; errors.fileHandleForReading.readabilityHandler = nil
        process.terminationHandler = nil
        try? input.fileHandleForWriting.close()
        if process.isRunning {
            process.terminate()
            let ownedProcess = process
            DispatchQueue.global().asyncAfter(deadline: .now() + 2) { if ownedProcess.isRunning { kill(ownedProcess.processIdentifier, SIGKILL) } }
        }
        failAll(CancellationError())
    }
}

private final class CapturedOutput: @unchecked Sendable {
    private let lock = NSLock(); private var bytes = Data()
    func append(_ data: Data) { lock.lock(); defer { lock.unlock() }; if bytes.count < 8 * 1024 * 1024 { bytes.append(data) } }
    var data: Data { lock.lock(); defer { lock.unlock() }; return bytes }
}

@MainActor enum Command {
    static func run(_ executable: URL, _ arguments: [String], environment: [String: String], directory: URL,
                    timeout: Double = 25) async throws -> (Int32, Data) {
        let process = Process(), output = Pipe(), errors = Pipe(), captured = CapturedOutput()
        process.executableURL = executable; process.arguments = arguments; process.environment = environment
        process.currentDirectoryURL = directory; process.standardInput = FileHandle.nullDevice
        process.standardOutput = output; process.standardError = errors
        output.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil } else { captured.append(data) }
        }
        errors.fileHandleForReading.readabilityHandler = { handle in if handle.availableData.isEmpty { handle.readabilityHandler = nil } }
        defer {
            output.fileHandleForReading.readabilityHandler = nil; errors.fileHandleForReading.readabilityHandler = nil
            if process.isRunning { process.terminate(); let owned = process; DispatchQueue.global().asyncAfter(deadline: .now() + 2) { if owned.isRunning { kill(owned.processIdentifier, SIGKILL) } } }
        }
        try process.run()
        let end = Date().addingTimeInterval(timeout)
        while process.isRunning {
            try Task.checkCancellation()
            if Date() >= end { throw WidgetError.message("CLI 응답 시간이 초과되었습니다.") }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        output.fileHandleForReading.readabilityHandler = nil
        captured.append(output.fileHandleForReading.readDataToEndOfFile())
        return (process.terminationStatus, captured.data)
    }
}

@MainActor final class AccountClient {
    let profile: Profile
    let root: URL
    private var rpc: LineProcess?
    private var sequence = 0
    private var loginID: String?
    private var loginResults: [String: Bool] = [:]
    private var loginProcessID: Int32?
    private var terminalDirectory: URL?
    var loginURL: URL?
    init(profile: Profile, root: URL) { self.profile = profile; self.root = root }
    func stop() {
        rpc?.stop(); rpc = nil
        if let directory = terminalDirectory {
            try? Data().write(to: directory.appendingPathComponent("cancelled"))
            if !FileManager.default.fileExists(atPath: directory.appendingPathComponent("result").path), let pid = loginProcessID, pid > 0 { kill(pid, SIGTERM) }
        }
    }
    private func codex() async throws -> LineProcess {
        if let rpc, rpc.running { return rpc }
        var arguments = [String]()
        if !profile.useSystem { arguments += ["-c", "cli_auth_credentials_store=\"file\""] }
        arguments += ["app-server"]
        let client = try LineProcess(executable: CLI.find("codex"), arguments: arguments,
            environment: CLI.environment(profile: profile, root: root), directory: root)
        rpc = client
        client.event = { [weak self] message in
            guard message["method"] as? String == "account/login/completed", let params = message["params"] as? Object,
                  let id = params["loginId"] as? String else { return }
            self?.loginResults[id] = params["success"] as? Bool ?? false
        }
        do {
            _ = try await call(client, "initialize", ["clientInfo": ["name": "ai_usage_widget", "title": "AI Usage Widget", "version": "1.0.0"]])
            try client.send(["method": "initialized", "params": [:]])
        } catch { stop(); throw error }
        return client
    }
    private func call(_ client: LineProcess, _ method: String, _ params: Object = [:]) async throws -> Object {
        sequence += 1
        return try await client.request(String(sequence), message: ["id": sequence, "method": method, "params": params])
    }
    func account() async throws -> Account {
        let source = profile.useSystem ? "system" : "widget"
        if profile.service == "codex" {
            let result = try await call(codex(), "account/read", ["refreshToken": false])
            guard let account = result["account"] as? Object else { return Account(id: profile.id, source: source) }
            guard account["type"] as? String == "chatgpt" else {
                return Account(id: profile.id, source: source, status: "unsupported", message: "ChatGPT 구독 계정으로 로그인해주세요.")
            }
            return Account(id: profile.id, source: source, status: "connected", email: account["email"] as? String, plan: account["planType"] as? String)
        }
        let (code, data) = try await Command.run(CLI.find("claude"), ["auth", "status", "--json"],
            environment: CLI.environment(profile: profile, root: root), directory: root)
        let result = try JSON.object(data)
        if !profile.useSystem, let directory = result["configDirectory"] as? String,
           URL(fileURLWithPath: directory).standardizedFileURL != profile.home(in: root).standardizedFileURL {
            throw WidgetError.message("Claude Code가 선택한 계정 폴더를 사용하지 않았습니다.")
        }
        guard code == 0, result["loggedIn"] as? Bool == true else { return Account(id: profile.id, source: source) }
        guard result["authMethod"] as? String == "claude.ai" else {
            return Account(id: profile.id, source: source, status: "unsupported", message: "Claude 구독 계정으로 로그인해주세요.")
        }
        return Account(id: profile.id, source: source, status: "connected", email: result["email"] as? String, plan: result["subscriptionType"] as? String)
    }
    func usage() async throws -> Usage {
        if profile.service == "codex" { return UsageParser.codex(try await call(codex(), "account/rateLimits/read")) }
        let client = try LineProcess(executable: CLI.find("claude"), arguments: ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--setting-sources", "", "--disable-slash-commands", "--no-session-persistence"],
            environment: CLI.environment(profile: profile, root: root), directory: root)
        defer { client.stop() }
        var result: Object = [:]
        for subtype in ["initialize", "get_usage"] {
            let id = UUID().uuidString
            result = try await client.request(id, message: ["type": "control_request", "request_id": id, "request": ["subtype": subtype]])
        }
        return UsageParser.claude(result)
    }
    func login(open: (URL) -> Void) async throws {
        guard !profile.useSystem else { throw WidgetError.message("전용 계정을 선택해주세요.") }
        if profile.service == "codex" {
            let client = try await codex()
            loginResults.removeAll()
            let result = try await call(client, "account/login/start", ["type": "chatgpt"])
            guard let id = result["loginId"] as? String else { throw WidgetError.message("로그인을 시작하지 못했습니다.") }
            loginID = id
            guard let url = CLI.officialLogin(result["authUrl"] as? String ?? "") else { await cancelLogin(); throw WidgetError.message("공식 로그인 주소를 확인하지 못했습니다.") }
            loginURL = url; open(url)
            let end = Date().addingTimeInterval(300)
            while loginResults[id] == nil {
                try Task.checkCancellation()
                if Date() >= end { throw WidgetError.message("로그인 시간이 초과되었습니다.") }
                try await Task.sleep(nanoseconds: 200_000_000)
            }
            guard loginResults[id] == true else { throw WidgetError.message("로그인이 완료되지 않았습니다.") }
            loginID = nil; loginURL = nil
        } else {
            let folder = try launchTerminal(arguments: ["auth", "login", "--claudeai"], directory: root, completion: true)
            terminalDirectory = folder
            defer {
                // Removing the private directory also tells the Terminal watcher to stop an unfinished login.
                try? FileManager.default.removeItem(at: folder); terminalDirectory = nil; loginProcessID = nil
            }
            let end = Date().addingTimeInterval(300)
            while !FileManager.default.fileExists(atPath: folder.appendingPathComponent("result").path) {
                if let pid = try? String(contentsOf: folder.appendingPathComponent("pid"), encoding: .utf8) { loginProcessID = Int32(pid.trimmingCharacters(in: .whitespacesAndNewlines)) }
                try Task.checkCancellation()
                if Date() >= end { await cancelLogin(); throw WidgetError.message("로그인 시간이 초과되었습니다.") }
                try await Task.sleep(nanoseconds: 250_000_000)
            }
            let code = try String(contentsOf: folder.appendingPathComponent("result"), encoding: .utf8)
            guard code.trimmingCharacters(in: .whitespacesAndNewlines) == "0" else { throw WidgetError.message("Claude 로그인이 완료되지 않았습니다.") }
        }
    }
    func cancelLogin() async {
        if let id = loginID, let rpc { _ = try? await call(rpc, "account/login/cancel", ["loginId": id]) }
        loginID = nil; loginURL = nil
        // PID is produced only by our private Terminal launcher; never kill a shared CLI.
        if let pid = loginProcessID, pid > 0 { kill(pid, SIGTERM) }
        if let terminalDirectory { try? Data().write(to: terminalDirectory.appendingPathComponent("cancelled")) }
    }
    func logout() async throws {
        guard !profile.useSystem else { return }
        if profile.service == "codex" { _ = try await call(codex(), "account/logout") }
        else {
            let (code, _) = try await Command.run(CLI.find("claude"), ["auth", "logout"],
                environment: CLI.environment(profile: profile, root: root), directory: root)
            guard code == 0 else { throw WidgetError.message("Claude 로그아웃에 실패했습니다.") }
        }
    }
    @discardableResult func launchTerminal(arguments: [String] = [], directory: URL, completion: Bool = false) throws -> URL {
        let executable = try CLI.find("claude"), env = try CLI.environment(profile: profile, root: root)
        let folder = root.appendingPathComponent("Terminal/" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let script = folder.appendingPathComponent("Claude Code.command")
        // Do not serialize inherited credentials into a shell script.
        var text = "#!/bin/zsh\ncd \(CLI.quote(directory.path)) || exit 1\nexport PATH=\(CLI.quote(env["PATH"] ?? "/usr/bin:/bin"))\n"
        if !profile.useSystem {
            text += "unset ${(k)parameters[(I)ANTHROPIC_*]} CLAUDE_CODE_OAUTH_TOKEN CLAUDE_CODE_USE_BEDROCK CLAUDE_CODE_USE_VERTEX CLAUDE_CODE_USE_FOUNDRY CLAUDE_CODE_SIMPLE\n"
            text += "export CLAUDE_CONFIG_DIR=\(CLI.quote(profile.home(in: root).path))\n"
        } else if let config = env["CLAUDE_CONFIG_DIR"] { text += "export CLAUDE_CONFIG_DIR=\(CLI.quote(config))\n" }
        let command = ([executable.path] + arguments).map(CLI.quote).joined(separator: " ")
        if completion {
            text += "[[ -d \(CLI.quote(folder.path)) && ! -f \(CLI.quote(folder.appendingPathComponent("cancelled").path)) ]] || exit 1\n"
            // Backgrounding normally disconnects stdin. Keep the official CLI's fallback code entry interactive.
            text += "\(command) < /dev/tty &\nchild=$!\nprint -r -- $child > \(CLI.quote(folder.appendingPathComponent("pid").path))\n"
            text += "( while kill -0 $child 2>/dev/null; do\n"
            text += "if [[ ! -d \(CLI.quote(folder.path)) || -f \(CLI.quote(folder.appendingPathComponent("cancelled").path)) ]] || ! kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; then\nkill $child 2>/dev/null\nexit\nfi\n/bin/sleep 0.25\ndone ) &\nwatcher=$!\n"
            text += "trap 'kill $child $watcher 2>/dev/null' EXIT HUP INT TERM\nwait $child\nresult=$?\nkill $watcher 2>/dev/null\ntrap - EXIT HUP INT TERM\n"
            text += "[[ -d \(CLI.quote(folder.path)) ]] && print -r -- $result > \(CLI.quote(folder.appendingPathComponent("result").path))\n"
            text += "print 'AI Usage 로그인 창을 닫아도 됩니다.'\n"
        } else { text += "rm -f \(CLI.quote(script.path))\nrmdir \(CLI.quote(folder.path))\nexec \(command)\n" }
        try text.write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        guard let terminal = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Terminal") else { throw WidgetError.message("Terminal 앱을 찾을 수 없습니다.") }
        NSWorkspace.shared.open([script], withApplicationAt: terminal, configuration: NSWorkspace.OpenConfiguration())
        return folder
    }
}
