import Foundation
import AppKit

@MainActor final class WidgetStore {
    let root: URL
    var preferences: Preferences
    private(set) var accounts: [String: Account] = [:]
    private(set) var usages: [String: Usage] = [:]
    private var clients: [String: AccountClient] = [:]
    private var lastClaudeAttempt: [String: Date] = [:]
    private let tracker: DailyTracker
    private var timer: Timer?
    private var refreshTask: Task<Void, Never>?
    private var loginTask: Task<Void, Never>?
    private var stopped = false
    private(set) var busy = false
    private(set) var loginProvider: String?
    private(set) var notice: String?
    var changed: (() -> Void)?
    var hide: (() -> Void)?
    let fixture: Bool
    var viewRevision = 0
    init(root: URL, fixture: Bool = false) throws {
        self.root = root; self.fixture = fixture
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        preferences = JSON.read(root.appendingPathComponent("mac-settings.json"), as: Preferences.self) ?? Preferences()
        preferences.normalize(); tracker = DailyTracker(root: root)
        for profile in preferences.profiles { reset(profile) }
        if fixture {
            preferences.visible = ["codex", "claude"]
            for profile in preferences.profiles {
                accounts[profile.id] = Account(id: profile.id, status: "connected", email: "preview@example.test", plan: profile.service == "codex" ? "plus" : "max")
                usages[profile.id] = Usage(id: profile.id, status: "ready", message: "검증용 예시 데이터",
                    windows: [Quota(key: "five_hour", label: "5시간", usedPercent: 23, resetsAt: Date().addingTimeInterval(7200).timeIntervalSince1970),
                              Quota(key: "seven_day", label: "주간", usedPercent: 42, resetsAt: Date().addingTimeInterval(172800).timeIntervalSince1970)], updatedAt: Date())
            }
        }
    }
    func start() {
        guard timer == nil, !fixture else { return }
        refreshTask = Task { await refresh() }
        timer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            Task { @MainActor in guard let self, !self.stopped else { return }; self.refreshTask = Task { await self.refresh() } }
        }
        timer?.tolerance = 5
    }
    func stop() {
        stopped = true; timer?.invalidate(); timer = nil; refreshTask?.cancel(); loginTask?.cancel()
        for client in clients.values { client.stop() }
    }
    private func reset(_ profile: Profile) {
        clients.removeValue(forKey: profile.id)?.stop()
        accounts[profile.id] = Account(id: profile.id, source: profile.useSystem ? "system" : "widget")
        usages[profile.id] = Usage(id: profile.id)
        lastClaudeAttempt.removeValue(forKey: profile.id)
    }
    private func client(_ profile: Profile) -> AccountClient {
        if let value = clients[profile.id] { return value }
        let value = AccountClient(profile: profile, root: root); clients[profile.id] = value; return value
    }
    private func save() {
        do { try JSON.write(preferences, to: root.appendingPathComponent("mac-settings.json")) }
        catch { notice = "설정을 저장하지 못했습니다. 저장 폴더의 권한을 확인해주세요." }
    }
    private func removeProfile(_ id: String) throws {
        guard let index = preferences.profiles.firstIndex(where: { $0.id == id }) else { return }
        let profile = preferences.profiles[index]
        var next = preferences
        next.visible.removeAll { $0 == id }
        if id == profile.service { next.profiles[index] = .primary(profile.service) }
        else { next.profiles.remove(at: index) }
        if profile.service == "claude", !fixture { try ClaudeBridge(profile: profile, root: root).uninstall() }
        // Remove the app's account entry, preserving CLI login files and local history.
        try JSON.write(next, to: root.appendingPathComponent("mac-settings.json"))
        preferences = next
        clients.removeValue(forKey: id)?.stop()
        accounts.removeValue(forKey: id); usages.removeValue(forKey: id)
        lastClaudeAttempt.removeValue(forKey: id)
        if id == profile.service { reset(.primary(profile.service)) }
    }
    private func errorMessage(_ error: Error) -> String {
        (error as? WidgetError)?.errorDescription ?? "작업을 완료하지 못했습니다. CLI 설치와 계정 연결을 확인해주세요."
    }
    func refresh(force: Bool = false) async {
        guard !busy, loginProvider == nil, !stopped, !fixture else { return }
        busy = true; changed?()
        defer { busy = false; changed?() }
        // Bound subprocess concurrency while letting each account fail independently.
        let profiles = preferences.profiles.filter { preferences.visible.contains($0.id) && $0.enabled }
        await withTaskGroup(of: Void.self) { group in
            for (index, profile) in profiles.enumerated() {
                if index >= 3 { await group.next() }
                group.addTask { @MainActor in await self.refresh(profile, force: force) }
            }
        }
    }
    private func refresh(_ profile: Profile, force: Bool) async {
        do {
            let reader = client(profile)
            let account = try await reader.account()
            try Task.checkCancellation()
            let oldEmail = accounts[profile.id]?.email
            accounts[profile.id] = account
            guard account.status == "connected" else {
                usages[profile.id] = Usage(id: profile.id, status: account.status, message: account.message ?? "계정을 연결해주세요.")
                return
            }
            if oldEmail != account.email { usages[profile.id] = Usage(id: profile.id); lastClaudeAttempt.removeValue(forKey: profile.id) }
            if profile.service == "claude" {
                let bridge = ClaudeBridge(profile: profile, root: root)
                try bridge.bind(account.email?.lowercased() ?? "unknown")
                try bridge.install(replaceExisting: false)
                if force || Date().timeIntervalSince(lastClaudeAttempt[profile.id] ?? .distantPast) >= 300 {
                    lastClaudeAttempt[profile.id] = Date()
                    do { let usage = try await reader.usage(); try bridge.save(usage); usages[profile.id] = usage }
                    catch { if let cached = bridge.cached() { usages[profile.id] = cached } else { throw error } }
                } else if let cached = bridge.cached() { usages[profile.id] = cached }
            } else { usages[profile.id] = try await reader.usage() }
            usages[profile.id]?.id = profile.id
        } catch is CancellationError { }
        catch {
            let message = errorMessage(error)
            usages[profile.id] = Usage(id: profile.id, status: "error", message: message)
            if accounts[profile.id]?.status != "connected" { accounts[profile.id]?.message = message }
        }
    }
    func state(maxHeight: Double) -> Object {
        let providers: [Object] = preferences.visible.compactMap { id in
            guard let profile = preferences.profiles.first(where: { $0.id == id }) else { return nil }
            var usage = usages[id] ?? Usage(id: id); usage.id = id
            let account = accounts[id]
            let identity = account?.status == "connected" ? account?.email.map { "\(account!.source):\($0.lowercased()):\(usage.plan ?? account?.plan ?? "")" } : nil
            usage = tracker.observe(usage, identity: identity, interval: preferences.intervalMinutes, window: preferences.windowMinutes)
            var result = usage.object
            result["name"] = profile.displayName; result["defaultName"] = profile.name; result["service"] = profile.service
            return result
        }
        return ["platform": "macos", "busy": busy, "authBusy": loginProvider != nil, "loginProvider": loginProvider as Any? ?? NSNull(),
            "notice": notice as Any? ?? NSNull(), "viewMode": preferences.viewMode, "viewRevision": viewRevision,
            "collapsed": preferences.viewMode == 1, "lastCardView": preferences.viewMode, "transitionPhase": "settled",
            "animateTransitions": preferences.animateTransitions, "logoRotation": 0, "maxCardHeight": maxHeight,
            "measurementIntervalMinutes": preferences.intervalMinutes, "measurementWindowMinutes": preferences.windowMinutes,
            "accounts": preferences.profiles.map { profile -> Object in
                var result = JSON.object(accounts[profile.id] ?? Account(id: profile.id))
                result["name"] = profile.displayName; result["defaultName"] = profile.name; result["service"] = profile.service
                result["nickname"] = profile.nickname; result["enabled"] = profile.enabled; return result
            }, "providers": providers,
            "serviceCatalog": [["id": "codex", "name": "Codex", "symbol": "⌘", "description": "ChatGPT 구독 한도 · 자동 조회", "loginName": "ChatGPT"],
                               ["id": "claude", "name": "Claude", "symbol": "✳", "description": "Claude 구독 한도 · 자동 조회", "loginName": "Claude Code"]]]
    }
    func action(_ message: Object) async {
        guard let action = message["action"] as? String, !stopped else { return }
        if action == "cancelLogin" {
            if let id = loginProvider, let client = clients[id] { await client.cancelLogin() }
            loginTask?.cancel(); return
        }
        if action == "reopenLogin" { if let id = loginProvider, let url = clients[id]?.loginURL { NSWorkspace.shared.open(url) }; return }
        switch action {
        case "ready": changed?(); start(); return
        case "toggleLogo", "collapse": hide?(); return
        case "close": NSApplication.shared.terminate(nil); return
        case "toggleMotion": preferences.animateTransitions.toggle()
        case "toggleDetails": preferences.viewMode = preferences.viewMode == 0 ? 1 : 0; viewRevision += 1
        case "showFull": preferences.viewMode = 0; viewRevision += 1
        default:
            guard !busy, loginProvider == nil else { return }
            switch action {
            case "refresh": await refresh(force: true); return
            case "setMeasurementInterval": if let minutes = message["minutes"] as? Int, (1...180).contains(minutes) { preferences.intervalMinutes = minutes }
            case "setMeasurementWindow": if let minutes = message["minutes"] as? Int, [30, 60, 180, 360, 720, 1440].contains(minutes) { preferences.windowMinutes = minutes }
            case "addProvider":
                guard let service = message["provider"] as? String, ["codex", "claude"].contains(service) else { return }
                var profile = preferences.profiles.first { $0.id == service }!
                if preferences.visible.contains(service) || profile.enabled || preferences.profiles.contains(where: { $0.service == service && $0.id != service }) {
                    let name = service == "codex" ? "Codex" : "Claude"
                    var number = 2
                    while preferences.profiles.contains(where: { $0.service == service && $0.name == "\(name) \(number)" }) { number += 1 }
                    profile = Profile(id: service + "-" + UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased(), service: service,
                                      name: "\(name) \(number)")
                    preferences.profiles.append(profile); reset(profile)
                }
                preferences.visible.append(profile.id)
            case "removeProvider":
                guard let id = message["provider"] as? String else { return }
                do { try removeProfile(id); notice = nil }
                catch { notice = "계정 항목을 삭제하지 못했습니다. 저장 폴더와 Claude 상태줄 설정을 확인해주세요."; changed?(); return }
            case "showProvider":
                if let id = message["provider"] as? String, preferences.profiles.contains(where: { $0.id == id }), !preferences.visible.contains(id) { preferences.visible.append(id) }
            case "setAccountNickname":
                if let id = message["accountId"] as? String, let index = preferences.profiles.firstIndex(where: { $0.id == id }), let text = message["nickname"] as? String {
                    let alias = String(text.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }.map(String.init).joined().trimmingCharacters(in: .whitespacesAndNewlines).prefix(24))
                    preferences.profiles[index].nickname = alias.isEmpty ? nil : alias
                }
            case "loginAccount", "useExistingAccount", "logoutAccount", "launchClaudeCode":
                guard let id = message["accountId"] as? String, let index = preferences.profiles.firstIndex(where: { $0.id == id }), !fixture else { return }
                if action == "loginAccount" { beginLogin(index); return }
                busy = true; changed?()
                do {
                    let old = preferences.profiles[index]
                    if action == "useExistingAccount" {
                        guard old.id == old.service else { busy = false; return }
                        if old.service == "claude" { try ClaudeBridge(profile: old, root: root).uninstall() }
                        preferences.profiles[index].useSystem = true; preferences.profiles[index].enabled = true
                        let profile = preferences.profiles[index]; reset(profile)
                        let account = try await client(profile).account(); accounts[id] = account
                        if account.status == "connected", profile.service == "claude" { try ClaudeBridge(profile: profile, root: root).install() }
                        notice = account.status == "connected" ? "기존 CLI 계정을 연결했습니다." : "CLI에서 먼저 로그인한 후 새로고침해주세요."
                    } else if action == "logoutAccount" {
                        if old.enabled { try await client(old).logout() }
                        if old.service == "claude" { try ClaudeBridge(profile: old, root: root).uninstall() }
                        preferences.profiles[index].enabled = false; preferences.profiles[index].useSystem = false; reset(preferences.profiles[index])
                        notice = "계정 연결을 해제했습니다."
                    } else {
                        guard old.service == "claude", accounts[id]?.status == "connected" else { busy = false; return }
                        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.allowsMultipleSelection = false
                        panel.title = "Claude Code로 열 프로젝트 폴더"
                        if await panel.begin() == .OK, let url = panel.url { try client(old).launchTerminal(directory: url) }
                    }
                } catch { notice = errorMessage(error) }
                busy = false; save(); await refresh(force: true); changed?(); return
            default: return
            }
        }
        save(); changed?()
        if action == "showProvider" { await refresh() }
    }
    private func beginLogin(_ index: Int) {
        let old = preferences.profiles[index]
        do { if old.service == "claude" { try ClaudeBridge(profile: old, root: root).uninstall() } }
        catch { notice = errorMessage(error); changed?(); return }
        preferences.profiles[index].useSystem = false
        preferences.profiles[index].enabled = false
        let profile = preferences.profiles[index]; reset(profile)
        loginProvider = profile.id; accounts[profile.id]?.status = "signingIn"; notice = nil; save(); changed?()
        loginTask = Task { [weak self] in
            guard let self else { return }
            let reader = client(profile)
            do {
                try await reader.login { NSWorkspace.shared.open($0) }
                try Task.checkCancellation()
                let account = try await reader.account()
                guard account.status == "connected" else { throw WidgetError.message("로그인된 구독 계정을 확인하지 못했습니다.") }
                preferences.profiles[index].enabled = true; accounts[profile.id] = account
                if profile.service == "claude" { try ClaudeBridge(profile: profile, root: root).install() }
                notice = "계정이 연결되었습니다."
            } catch {
                await reader.cancelLogin()
                accounts[profile.id]?.status = "disconnected"
                notice = error is CancellationError ? "로그인을 취소했습니다." : errorMessage(error)
            }
            loginProvider = nil; loginTask = nil; save(); changed?()
            await refresh(force: true)
        }
    }
}
