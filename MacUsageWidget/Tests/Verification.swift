import Foundation
import AppKit

@main enum Verification {
    @MainActor static func main() async throws {
        signal(SIGPIPE, SIG_IGN)
        if CommandLine.arguments.contains("--live-codex") {
            let root = URL(fileURLWithPath: CommandLine.arguments.last!)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            var profile = Profile.primary("codex"); profile.useSystem = true
            let reader = AccountClient(profile: profile, root: root)
            defer { reader.stop() }
            var durations = [Double](), processIDs = [Int32]()
            for _ in 0..<3 {
                let started = ProcessInfo.processInfo.systemUptime
                let account = try await reader.account()
                guard account.status == "connected" else { throw WidgetError.message("No existing ChatGPT CLI login is available.") }
                let usage = try await reader.usage()
                guard usage.status == "ready", !usage.windows.isEmpty else { throw WidgetError.message("Live CLI returned no quota windows.") }
                if let pid = reader.readerProcessIdentifier { processIDs.append(pid) }
                reader.releaseIdleResources()
                guard reader.readerProcessIdentifier == nil else { throw WidgetError.message("Live reader was not released.") }
                durations.append((ProcessInfo.processInfo.systemUptime - started) * 1000)
            }
            guard Set(processIDs).count == 3 else { throw WidgetError.message("Expected a fresh reader for each live polling batch.") }
            try JSON.writeObject(["passed": true, "batches": durations.count, "milliseconds": durations, "distinctReaders": Set(processIDs).count],
                                 to: root.appendingPathComponent("results.json"))
            print("Three live Codex refresh/reconnect batches passed. No model turn was created.")
            return
        }
        if CommandLine.arguments.contains("app-server") { codexFixture(); return }
        if CommandLine.arguments.contains("--fixture-hang") { try await Task.sleep(nanoseconds: 20_000_000_000); return }
        if CommandLine.arguments.contains("auth") {
            print("{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"email\":\"claude@example.test\",\"subscriptionType\":\"max\"}"); return
        }
        if CommandLine.arguments.contains("-p") { claudeFixture(); return }
        let root = URL(fileURLWithPath: CommandLine.arguments.dropFirst().first ?? "/tmp/aiusage-mac-tests")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        var checks = 0
        func check(_ condition: @autoclosure () -> Bool, _ label: String) throws {
            guard condition() else { throw WidgetError.message("FAIL: " + label) }; checks += 1; print("PASS: " + label)
        }
        var rotation = RefreshRotation()
        try check(rotation.angle(at: 100) == 0 && !rotation.isRunning(at: 100), "Idle menu-bar icon does not rotate")
        rotation.begin(at: 100)
        let angles = (0...20).map { rotation.angle(at: 100 + Double($0) / 10) }
        try check(zip(angles, angles.dropFirst()).allSatisfy { $0.0 <= $0.1 } && abs(angles.last! - 360) < 0.001,
                  "A refresh completes exactly one forward turn")
        try check(rotation.angle(at: 110) == 360 && !rotation.isRunning(at: 110), "Completed rotation stays at its endpoint without reversing or repeating")
        rotation.begin(at: 111)
        try check(rotation.angle(at: 111) == 360 && rotation.angle(at: 113) == 720, "The next refresh continues forward for one more turn")
        rotation.begin(at: 114)
        let interruptedAngle = rotation.angle(at: 114.4)
        rotation.begin(at: 114.4)
        try check(abs(rotation.angle(at: 114.4) - interruptedAngle) < 0.001 && rotation.angle(at: 117) == 1440,
                  "Closely spaced refreshes preserve the current angle and complete both turns")
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let usage = UsageParser.codex(["rateLimits": ["primary": ["usedPercent": 99]],
            "rateLimitsByLimitId": ["codex": ["primary": ["usedPercent": 23, "windowDurationMins": 300],
                "secondary": ["usedPercent": NSNull()], "planType": "plus"]]], now: now)
        try check(usage.windows.count == 1 && usage.windows[0].remainingPercent == 77, "Codex-specific limit bucket wins; null percentages stay absent")
        try check(UsageParser.codex([:]).windows.isEmpty, "Missing usage is never displayed as 100 percent")
        try check(UsageParser.percentage(true) == nil && UsageParser.percentage(-1) == nil, "Reject invalid percentage types and negative values")
        let claude = UsageParser.claude(["rate_limits_available": true, "rate_limits": ["seven_day": ["utilization": 62, "resets_at": "2027-01-20T00:00:00.000Z"]]])
        try check(claude.windows.first?.remainingPercent == 38 && claude.windows.first?.resetsAt != nil, "Claude utilization is percent and accepts fractional ISO timestamps")
        try check(!Profile.valid("codex-../../x", service: "codex"), "Profile IDs cannot escape storage")
        let extra = Profile(id: "codex-" + String(repeating: "a", count: 32), service: "codex", name: "Codex 2")
        try check(extra.home(in: root) != Profile.primary("codex").home(in: root), "Additional accounts have independent homes")
        try check(CLI.officialLogin("https://chatgpt.com/auth") != nil && CLI.officialLogin("https://chatgpt.com.attacker.test") == nil
                  && CLI.officialLogin("https://user@auth.openai.com/") == nil && CLI.officialLogin("file:///tmp/login") == nil, "Only official HTTPS login origins are opened")
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let tracker = DailyTracker(root: root.appendingPathComponent("tracker"), calendar: calendar)
        func sample(_ used: Double, _ seconds: Double, reset: Double = 1_800_100_000) -> Usage {
            Usage(id: "codex", status: "ready", windows: [Quota(key: "primary", label: "5시간", usedPercent: used, resetsAt: reset)], updatedAt: now.addingTimeInterval(seconds))
        }
        func observe(_ used: Double, _ seconds: Double, reset: Double = 1_800_100_000, identity: String = "account-a") -> Usage {
            tracker.observe(sample(used, seconds, reset: reset), identity: identity, now: now.addingTimeInterval(seconds), interval: 1, window: 30)
        }
        _ = observe(20, 0)
        try check(observe(25, 30).windows[0].today?.usedPercentagePoints == 0, "Observations wait for the chosen measurement interval")
        try check(observe(24, 60).windows[0].today?.usedPercentagePoints == 5, "Corrections preserve the consumption high-water mark")
        try check(observe(26, 120).windows[0].today?.usedPercentagePoints == 6, "Corrections never double-count recovered consumption")
        let reset = observe(1, 180, reset: 1_800_200_000)
        try check(reset.windows[0].today?.usedPercentagePoints == 6 && reset.windows[0].today?.cyclePercentagePoints == 0, "Quota reset clears the painted cycle, preserving the daily total")
        try check(observe(90, 240, identity: "account-b").windows[0].today?.usedPercentagePoints == 0, "Changing identity resets observation history")
        let restored = DailyTracker(root: root.appendingPathComponent("tracker"), calendar: calendar)
        let restoredUsage = restored.observe(sample(93, 300), identity: "account-b", now: now.addingTimeInterval(300), interval: 1, window: 30)
        try check(restoredUsage.windows[0].today?.usedPercentagePoints == 3, "Observation history survives relaunch")

        let bridge = ClaudeBridge(profile: .primary("claude"), root: root.appendingPathComponent("bridge"), executable: URL(fileURLWithPath: "/Applications/AI Usage's.app/Contents/MacOS/AIUsageWidget"))
        try JSON.writeObject(["statusLine": ["type": "command", "command": "printf original"], "theme": "dark"], to: bridge.settings)
        try bridge.install(); try bridge.install()
        var settings = try JSON.object(Data(contentsOf: bridge.settings)); settings["theme"] = "light"
        try JSON.writeObject(settings, to: bridge.settings); try bridge.install(replaceExisting: false); try bridge.uninstall()
        settings = try JSON.object(Data(contentsOf: bridge.settings))
        try check((settings["statusLine"] as? Object)?["command"] as? String == "printf original" && settings["theme"] as? String == "light", "Status line restoration preserves later unrelated settings")
        try bridge.install()
        settings = try JSON.object(Data(contentsOf: bridge.settings)); settings["statusLine"] = ["command": "printf changed-by-user"]
        try JSON.writeObject(settings, to: bridge.settings); try bridge.uninstall()
        let changedSettings = try JSON.object(Data(contentsOf: bridge.settings))
        try check((changedSettings["statusLine"] as? Object)?["command"] as? String == "printf changed-by-user", "Disconnect does not overwrite a user's newer status line")

        let executable = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
        setenv("AI_USAGE_CODEX_PATH", executable.path, 1); setenv("AI_USAGE_CLAUDE_PATH", executable.path, 1)
        let client = AccountClient(profile: .primary("codex"), root: root)
        let account = try await client.account(), limits = try await client.usage()
        try check(account.status == "connected" && limits.windows.first?.remainingPercent == 77, "Codex subprocess initialize/account/rateLimits round trip")
        let firstPID = client.readerProcessIdentifier
        client.releaseIdleResources()
        try check(firstPID != nil && client.readerProcessIdentifier == nil, "Completed polling releases the owned Codex reader")
        if let firstPID {
            for _ in 0..<60 { if kill(firstPID, 0) != 0 { break }; try await Task.sleep(nanoseconds: 50_000_000) }
            try check(kill(firstPID, 0) != 0, "The released reader actually exits the operating system")
        }
        let reread = try await client.account(), rereadUsage = try await client.usage()
        try check(reread.status == "connected" && rereadUsage.windows.first?.remainingPercent == 77
                  && client.readerProcessIdentifier != firstPID, "The next refresh starts a new reader and preserves account and quota results")
        client.releaseIdleResources()
        setenv("AI_USAGE_FIXTURE_LOGIN_DELAY", "1", 1)
        let slowLogin = AccountClient(profile: .primary("codex"), root: root)
        let loginTask = Task { try await slowLogin.login { _ in } }
        try await Task.sleep(nanoseconds: 150_000_000)
        let loginPID = slowLogin.readerProcessIdentifier
        slowLogin.releaseIdleResources()
        try check(loginPID != nil && slowLogin.readerProcessIdentifier == loginPID, "Idle cleanup never interrupts a pending login")
        try await loginTask.value
        slowLogin.releaseIdleResources()
        try check(slowLogin.readerProcessIdentifier == nil, "A completed login can release its reader without logging out")
        slowLogin.stop(); unsetenv("AI_USAGE_FIXTURE_LOGIN_DELAY")
        var opened: URL?
        try await client.login { opened = $0 }
        try check(opened?.host == "auth.openai.com", "Login handles completion arriving before the request response")
        try await client.logout(); client.stop()
        let claudeClient = AccountClient(profile: .primary("claude"), root: root)
        let claudeAccount = try await claudeClient.account(), claudeUsage = try await claudeClient.usage()
        try check(claudeAccount.status == "connected" && claudeUsage.windows.first?.remainingPercent == 65, "Claude auth and control requests run without a model turn")
        claudeClient.stop()
        let fallbackRoot = root.appendingPathComponent("claude-unavailable")
        var fallbackPreferences = Preferences()
        fallbackPreferences.visible = ["claude"]
        fallbackPreferences.profiles[1].enabled = true
        try JSON.write(fallbackPreferences, to: fallbackRoot.appendingPathComponent("mac-settings.json"))
        let fallbackBridge = ClaudeBridge(profile: .primary("claude"), root: fallbackRoot)
        try fallbackBridge.bind("claude@example.test")
        let cachedAt = Date(timeIntervalSince1970: 1_800_000_000)
        try fallbackBridge.save(Usage(id: "claude", status: "ready", windows: [Quota(key: "five_hour", label: "5시간", usedPercent: 41)], updatedAt: cachedAt))
        setenv("AI_USAGE_FIXTURE_USAGE_UNAVAILABLE", "1", 1)
        let fallbackStore = try WidgetStore(root: fallbackRoot)
        await fallbackStore.refresh(force: true)
        try check(fallbackStore.usages["claude"]?.status == "ready" && fallbackStore.usages["claude"]?.windows.first?.usedPercent == 41
                  && fallbackStore.usages["claude"]?.updatedAt == cachedAt, "Unavailable automatic usage preserves the cached quota and original receipt time")
        try FileManager.default.removeItem(at: fallbackBridge.directory.appendingPathComponent("usage-cache.json"))
        await fallbackStore.refresh(force: true)
        try check(fallbackStore.usages["claude"]?.status == "unavailable" && fallbackStore.usages["claude"]?.windows.isEmpty == true,
                  "Unavailable usage without a cache does not invent quota values")
        unsetenv("AI_USAGE_FIXTURE_USAGE_UNAVAILABLE")
        await fallbackStore.refresh(force: true)
        try check(fallbackStore.usages["claude"]?.status == "ready" && fallbackStore.usages["claude"]?.windows.first?.remainingPercent == 65,
                  "Automatic usage recovers after an unavailable response")
        fallbackStore.stop()
        let hung = try LineProcess(executable: executable, arguments: ["--fixture-hang"], environment: ProcessInfo.processInfo.environment, directory: root)
        var timedOut = false
        do { _ = try await hung.request("1", message: ["id": 1], timeout: 0.1) } catch { timedOut = true }
        try check(timedOut, "Unresponsive CLI requests time out")
        let cancelled = Task { try await hung.request("2", message: ["id": 2]) }
        try await Task.sleep(nanoseconds: 50_000_000); cancelled.cancel()
        var wasCancelled = false
        do { _ = try await cancelled.value } catch is CancellationError { wasCancelled = true }
        try check(wasCancelled, "Cancellation releases waiting requests")
        hung.stop()

        let storeRoot = root.appendingPathComponent("store")
        let store = try WidgetStore(root: storeRoot)
        await store.action(["action": "addProvider", "provider": "codex"])
        await store.action(["action": "addProvider", "provider": "codex"])
        try check(store.preferences.visible.count == 2, "Adding the same service creates two account cards")
        let second = store.preferences.visible[1]
        await store.action(["action": "setAccountNickname", "accountId": second, "nickname": "업무용"])
        await store.action(["action": "addProvider", "provider": "codex"])
        let third = store.preferences.visible[2]
        let retainedFile = store.preferences.profiles.first { $0.id == second }!.home(in: storeRoot).appendingPathComponent("local-history.json")
        try JSON.writeObject(["fixture": true], to: retainedFile)
        await store.action(["action": "removeProvider", "provider": second])
        await store.action(["action": "showProvider", "provider": second])
        let removedState = store.state(maxHeight: 700)
        try check(!store.preferences.visible.contains(second) && !(removedState["accounts"] as! [Object]).contains { $0["id"] as? String == second }
                  && store.accounts[second] == nil && store.usages[second] == nil, "Deleted extra account leaves no saved tag or restorable entry")
        try check(FileManager.default.fileExists(atPath: retainedFile.path) && store.preferences.visible == ["codex", third], "Removing an account preserves local files and other cards")
        let afterDelete = try WidgetStore(root: storeRoot)
        try check(!afterDelete.preferences.profiles.contains { $0.id == second }, "Deleted account stays absent after relaunch")
        afterDelete.stop()
        await store.action(["action": "addProvider", "provider": "codex"])
        let replacement = store.preferences.visible.last!
        let codexNames = store.preferences.profiles.filter { $0.service == "codex" }.map(\.name)
        try check(replacement != second && Set(codexNames).count == codexNames.count
                  && store.preferences.profiles.first { $0.id == replacement }?.nickname == nil, "Re-adding creates a fresh account without duplicate names")
        store.preferences.visible.removeAll { $0 == replacement }
        await store.action(["action": "removeProvider", "provider": replacement])
        try check(!store.preferences.profiles.contains { $0.id == replacement }, "Accounts hidden by older versions can also be deleted")
        await store.action(["action": "useExistingAccount", "accountId": "codex"])
        try check(store.accounts["codex"]?.status == "connected" && store.usages["codex"]?.status == "ready", "Existing CLI selection fetches real protocol-shaped usage")
        await store.action(["action": "setAccountNickname", "accountId": "codex", "nickname": "개인용"])
        await store.action(["action": "removeProvider", "provider": "codex"])
        let primary = store.preferences.profiles.first { $0.id == "codex" }!
        try check(!store.preferences.visible.contains("codex") && !primary.enabled && !primary.useSystem && primary.nickname == nil
                  && store.accounts["codex"]?.status == "disconnected", "Removing the primary account clears its connected tag and alias")
        await store.action(["action": "addProvider", "provider": "claude"])
        let accountBridge = ClaudeBridge(profile: .primary("claude"), root: storeRoot)
        try JSON.writeObject(["statusLine": ["command": "printf original"], "theme": "dark"], to: accountBridge.settings)
        try accountBridge.install()
        await store.action(["action": "removeProvider", "provider": "claude"])
        let restoredSettings = try JSON.object(Data(contentsOf: accountBridge.settings))
        try check((restoredSettings["statusLine"] as? Object)?["command"] as? String == "printf original"
                  && !store.preferences.visible.contains("claude"), "Deleting a Claude entry restores its previous status line")
        let persisted = try WidgetStore(root: storeRoot)
        try check(persisted.preferences.visible == [third] && persisted.preferences.profiles[0].enabled == false, "Remaining cards and deleted primary state survive relaunch")
        let stateData = try JSONSerialization.data(withJSONObject: store.state(maxHeight: 700))
        try check(!stateData.isEmpty, "Native state is valid JSON")
        store.stop(); persisted.stop()
        try JSON.writeObject(["passed": true, "checks": checks], to: root.appendingPathComponent("results.json"))
        print("\(checks) macOS checks passed.")
    }
    static func emit(_ value: Object) {
        let data = try! JSONSerialization.data(withJSONObject: value)
        FileHandle.standardOutput.write(data + Data([10]))
    }
    static func codexFixture() {
        while let line = readLine(), let data = line.data(using: .utf8), let message = try? JSON.object(data) {
            guard let id = message["id"] else { continue }
            let method = message["method"] as? String ?? ""
            var result: Object = [:]
            switch method {
            case "account/read": result = ["account": ["type": "chatgpt", "email": "codex@example.test", "planType": "plus"]]
            case "account/rateLimits/read": result = ["rateLimits": ["primary": ["usedPercent": 23, "windowDurationMins": 300]]]
            case "account/login/start":
                if ProcessInfo.processInfo.environment["AI_USAGE_FIXTURE_LOGIN_DELAY"] == "1" { Thread.sleep(forTimeInterval: 0.5) }
                emit(["method": "account/login/completed", "params": ["loginId": "fixture-login", "success": true]])
                result = ["loginId": "fixture-login", "authUrl": "https://auth.openai.com/fixture"]
            case "initialize", "account/logout", "account/login/cancel": break
            default: emit(["id": id, "error": ["message": "Unexpected model or tool request"]]); continue
            }
            emit(["id": id, "result": result])
        }
    }
    static func claudeFixture() {
        while let line = readLine(), let data = line.data(using: .utf8), let message = try? JSON.object(data) {
            guard message["type"] as? String == "control_request", let id = message["request_id"], let request = message["request"] as? Object else { exit(2) }
            let usage = request["subtype"] as? String == "get_usage"
            let available = ProcessInfo.processInfo.environment["AI_USAGE_FIXTURE_USAGE_UNAVAILABLE"] != "1"
            let result: Object = usage ? ["rate_limits_available": available, "rate_limits": ["five_hour": ["utilization": 35]]] : [:]
            emit(["type": "control_response", "response": ["request_id": id, "subtype": "success", "response": result]])
        }
    }
}
