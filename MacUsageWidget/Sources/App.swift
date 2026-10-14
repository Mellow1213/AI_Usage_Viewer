import AppKit
import WebKit

final class LocalWebContent: NSObject, WKURLSchemeHandler {
    let root: URL
    init(root: URL) { self.root = root.resolvingSymlinksInPath() }
    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        guard let url = task.request.url, url.scheme == "aiusage", url.host == "widget",
              task.request.httpMethod == "GET" else { task.didFailWithError(URLError(.badURL)); return }
        let path = url.path == "/" ? "index.html" : String(url.path.dropFirst())
        let file = root.appendingPathComponent(path).standardizedFileURL.resolvingSymlinksInPath()
        let types = ["html": "text/html", "js": "text/javascript", "css": "text/css", "svg": "image/svg+xml", "png": "image/png", "woff2": "font/woff2"]
        guard file.path.hasPrefix(root.path + "/"), let mime = types[file.pathExtension], let data = try? Data(contentsOf: file) else {
            task.didFailWithError(URLError(.fileDoesNotExist)); return
        }
        task.didReceive(URLResponse(url: url, mimeType: mime, expectedContentLength: data.count, textEncodingName: mime.hasPrefix("text/") ? "utf-8" : nil))
        task.didReceive(data); task.didFinish()
    }
    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) { }
}

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, WKScriptMessageHandler, WKNavigationDelegate, WKUIDelegate, NSPopoverDelegate {
    private var item: NSStatusItem!
    private let popover = NSPopover()
    private var web: WKWebView!
    private var store: WidgetStore!
    private var ready = false
    private var lastPayload = ""
    private var wakeObserver: NSObjectProtocol?
    private var iconTimer: Timer?
    private var iconPhase = 0.0
    private var iconRefresh = RefreshRotation()
    private var connected = false
    private var iconBusy = false
    private var lockDescriptor: Int32 = -1
    private var smokeDirectory: URL?
    private var smokeStarted = false
    private var maxHeight: Double { Double(max(260, min(880, (item?.button?.window?.screen ?? NSScreen.main)?.visibleFrame.height ?? 800) - 50)) }

    func applicationDidFinishLaunching(_ notification: Notification) {
        do {
            let args = ProcessInfo.processInfo.arguments
            func argument(_ flag: String) -> String? {
                guard let index = args.firstIndex(of: flag), index + 1 < args.count else { return nil }; return args[index + 1]
            }
            let root = argument("--data-dir").map { URL(fileURLWithPath: $0) } ??
                FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("AIUsageWidget")
            if let smoke = argument("--smoke-test") { smokeDirectory = URL(fileURLWithPath: smoke) }
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            lockDescriptor = open(root.appendingPathComponent("mac-app.lock").path, O_CREAT | O_RDWR, 0o600)
            guard lockDescriptor >= 0, flock(lockDescriptor, LOCK_EX | LOCK_NB) == 0 else {
                if let other = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "").first(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }) { other.activate() }
                NSApp.terminate(nil); return
            }
            store = try WidgetStore(root: root, fixture: args.contains("--fixture"))
            item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
            if let button = item.button {
                button.target = self; button.action = #selector(statusClicked(_:)); button.sendAction(on: [.leftMouseUp, .rightMouseUp])
                button.toolTip = "AI Usage · 클릭해서 사용량 보기 · 우클릭 메뉴"
                button.setAccessibilityLabel("AI Usage 메뉴 막대")
            }
            let configuration = WKWebViewConfiguration()
            configuration.websiteDataStore = .nonPersistent()
            configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
            configuration.setURLSchemeHandler(LocalWebContent(root: Bundle.main.resourceURL!.appendingPathComponent("Web")), forURLScheme: "aiusage")
            configuration.userContentController.add(self, name: "native")
            let bridge = """
            (() => {
              const listeners = new Set();
              window.aiUsageNative = {
                postMessage: value => window.webkit.messageHandlers.native.postMessage(value),
                addEventListener: (name, callback) => { if (name === 'message') listeners.add(callback); }
              };
              window.aiUsageReceive = value => { for (const callback of listeners) callback({ data: value }); };
              document.addEventListener('keydown', event => {
                if (event.key === 'Escape' && !['INPUT', 'TEXTAREA'].includes(event.target.tagName)) {
                  event.preventDefault(); window.aiUsageNative.postMessage({ action: 'toggleLogo' });
                } else if (event.key === 'F5' || (event.metaKey && event.key.toLowerCase() === 'r')) {
                  event.preventDefault(); window.aiUsageNative.postMessage({ action: 'refresh' });
                }
              });
            })();
            """
            configuration.userContentController.addUserScript(WKUserScript(source: bridge, injectionTime: .atDocumentStart, forMainFrameOnly: true))
            web = WKWebView(frame: NSRect(x: 0, y: 0, width: 304, height: 400), configuration: configuration)
            web.navigationDelegate = self; web.uiDelegate = self
            web.underPageBackgroundColor = NSColor(calibratedRed: 17/255, green: 19/255, blue: 22/255, alpha: 1)
            let controller = NSViewController(); controller.view = web
            popover.contentViewController = controller; popover.contentSize = NSSize(width: 304, height: 400)
            popover.behavior = .transient
            popover.animates = store.preferences.animateTransitions; popover.delegate = self
            store.changed = { [weak self] in self?.publish() }
            store.hide = { [weak self] in self?.popover.performClose(nil) }
            web.load(URLRequest(url: URL(string: "aiusage://widget/index.html")!))
            drawIcon()
            wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in await self?.store.refresh() }
            }
            if smokeDirectory != nil {
                showPopover()
                Task { [weak self] in
                    try? await Task.sleep(nanoseconds: 25_000_000_000)
                    guard let self, !self.smokeStarted else { return }
                    self.finishSmoke(["passed": false, "error": "WebKit did not complete the ready handshake."])
                }
            } else if store.preferences.visible.isEmpty { showPopover() }
        } catch {
            let alert = NSAlert(); alert.messageText = "AI Usage를 시작하지 못했습니다."
            alert.informativeText = "앱 파일과 저장 폴더의 권한을 확인해주세요."; alert.runModal(); NSApp.terminate(nil)
        }
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showPopover(); return true }
    func applicationWillTerminate(_ notification: Notification) {
        store?.stop(); iconTimer?.invalidate()
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
        web?.configuration.userContentController.removeScriptMessageHandler(forName: "native")
        if lockDescriptor >= 0 { flock(lockDescriptor, LOCK_UN); close(lockDescriptor) }
    }
    @objc private func statusClicked(_ sender: Any?) {
        if NSApp.currentEvent?.type == .rightMouseUp {
            let menu = NSMenu()
            for (title, selector, key) in [("사용량 보기", #selector(openUsage), ""), ("새로고침", #selector(refreshUsage), "r"), ("종료", #selector(quit), "q")] {
                let action = NSMenuItem(title: title, action: selector, keyEquivalent: key); action.target = self; menu.addItem(action)
            }
            item.menu = menu; item.button?.performClick(nil); item.menu = nil
        } else if popover.isShown { popover.performClose(nil) } else { showPopover() }
    }
    @objc private func openUsage() { showPopover() }
    @objc private func refreshUsage() { Task { await store.refresh(force: true) } }
    @objc private func quit() { NSApp.terminate(nil) }
    private func showPopover() {
        guard let button = item?.button else { return }
        NSApp.activate(ignoringOtherApps: true)
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        popover.contentViewController?.view.window?.makeKey()
        publish(force: true)
    }
    func popoverDidShow(_ notification: Notification) { publish(force: true) }
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.name == "native", message.frameInfo.isMainFrame,
              message.frameInfo.securityOrigin.protocol == "aiusage", message.frameInfo.securityOrigin.host == "widget",
              let body = message.body as? Object, let action = body["action"] as? String else { return }
        if action == "ready" { ready = true; publish(force: true); store.start(); runSmoke(); return }
        if action == "height" {
            guard let height = body["height"] as? Double, height.isFinite,
                  body["viewMode"] as? Int == store.preferences.viewMode, body["viewRevision"] as? Int == store.viewRevision else { return }
            let size = NSSize(width: 304, height: min(maxHeight, max(140, height + 2)))
            if abs(popover.contentSize.height - size.height) > 0.5 { popover.contentSize = size }
            return
        }
        Task { await store.action(body) }
    }
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        let url = navigationAction.request.url
        decisionHandler(url?.scheme == "aiusage" && url?.host == "widget" && url?.path == "/index.html" && navigationAction.targetFrame?.isMainFrame == true ? .allow : .cancel)
    }
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? { nil }
    func webView(_ webView: WKWebView, requestMediaCapturePermissionFor origin: WKSecurityOrigin, initiatedByFrame frame: WKFrameInfo, type: WKMediaCaptureType, decisionHandler: @escaping (WKPermissionDecision) -> Void) { decisionHandler(.deny) }
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) { ready = false; lastPayload = ""; webView.reload() }
    private func publish(force: Bool = false) {
        guard let store else { return }
        let state = store.state(maxHeight: maxHeight)
        let providers = state["providers"] as? [Object] ?? []
        connected = !providers.isEmpty && providers.allSatisfy { provider in
            guard let id = provider["id"] as? String else { return false }
            return store.accounts[id]?.status == "connected" && !["error", "disconnected", "signingIn"].contains(provider["status"] as? String ?? "")
        }
        let nextBusy = store.busy || store.loginProvider != nil
        if nextBusy && !iconBusy && store.preferences.animateTransitions && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            iconRefresh.begin(at: ProcessInfo.processInfo.systemUptime)
        }
        iconBusy = nextBusy
        updateIconAnimation(); drawIcon()
        let summary = providers.compactMap { provider -> String? in
            guard let first = (provider["windows"] as? [Object])?.first, let remaining = first["remainingPercent"] as? Double else { return nil }
            return "\(provider["name"] as? String ?? "AI"): \(Int(remaining))% 남음"
        }.joined(separator: " · ")
        item?.button?.toolTip = summary.isEmpty ? "AI Usage · 클릭해서 계정 연결" : summary
        popover.animates = store.preferences.animateTransitions && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        guard ready, let data = try? JSONSerialization.data(withJSONObject: state, options: [.sortedKeys]),
              let payload = String(data: data, encoding: .utf8), force || payload != lastPayload else { return }
        lastPayload = payload
        web.evaluateJavaScript("window.aiUsageReceive(\(payload));", completionHandler: nil)
    }
    private func updateIconAnimation() {
        let allowed = store.preferences.animateTransitions && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        if !allowed { iconRefresh = RefreshRotation() }
        let animate = (connected || iconBusy || iconRefresh.isRunning(at: ProcessInfo.processInfo.systemUptime)) && allowed
        if animate && iconTimer == nil {
            iconTimer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
                Task { @MainActor in guard let self else { return }; self.iconPhase += 0.1; self.updateIconAnimation(); self.drawIcon() }
            }
            iconTimer?.tolerance = 0.02
        } else if !animate { iconTimer?.invalidate(); iconTimer = nil; iconPhase = 0 }
    }
    private func drawIcon() {
        let phase = iconPhase, rotation = iconRefresh.angle(at: ProcessInfo.processInfo.systemUptime), active = connected
        let image = NSImage(size: NSSize(width: 20, height: 20), flipped: true) { rect in
            NSGraphicsContext.saveGraphicsState(); defer { NSGraphicsContext.restoreGraphicsState() }
            let transform = NSAffineTransform()
            transform.translateX(by: 10, yBy: 10)
            transform.rotate(byRadians: CGFloat(rotation * .pi / 180))
            transform.scale(by: 0.5); transform.translateX(by: -20, yBy: -20); transform.concat()
            let paths: [[NSPoint]] = [[NSPoint(x:20,y:4), NSPoint(x:27,y:16), NSPoint(x:13,y:16)],
                                     [NSPoint(x:30,y:21), NSPoint(x:37,y:33), NSPoint(x:23,y:33)],
                                     [NSPoint(x:10,y:21), NSPoint(x:17,y:33), NSPoint(x:3,y:33)]]
            for (index, points) in paths.enumerated() {
                let alpha = active && phase > 0 ? 0.55 + 0.45 * (sin(phase * 1.6 - Double(index) * 1.4) + 1) / 2 : [1.0, 0.65, 0.35][index]
                NSColor.black.withAlphaComponent(alpha).setFill()
                let path = NSBezierPath(); path.move(to: points[0]); path.line(to: points[1]); path.line(to: points[2]); path.close(); path.fill()
            }
            return true
        }
        image.isTemplate = true; item?.button?.image = image
    }
    private func verifyRefreshRotation(activeMilliseconds: Int) async throws -> Object {
        // WebKit suspends animation frames while the transient popover is closed.
        // Finish the startup turn before measuring a separate refresh request.
        showPopover()
        try await Task.sleep(nanoseconds: UInt64((RefreshRotation.duration + 0.25) * 1_000_000_000))
        let data = try JSONSerialization.data(withJSONObject: store.state(maxHeight: maxHeight))
        let state = String(decoding: data, as: UTF8.self)
        let sampleMilliseconds = max(2100, activeMilliseconds + 350)
        try await web.evaluateJavaScript("""
        (() => {
          const state = \(state), logo = document.querySelector('.triangle');
          const readAngle = () => {
            const transform = getComputedStyle(logo).transform;
            const matrix = transform === 'none' ? new DOMMatrixReadOnly() : new DOMMatrixReadOnly(transform);
            return Math.atan2(matrix.b, matrix.a) * 180 / Math.PI;
          };
          const result = window.__refreshRotation = { frames: 0, total: 0, minimumStep: 0, maximumTurn: 0, finished: false };
          let previous = readAngle();
          const started = performance.now();
          const sample = now => {
            const angle = readAngle(), step = ((angle - previous + 540) % 360) - 180;
            result.total += step; result.minimumStep = Math.min(result.minimumStep, step);
            result.maximumTurn = Math.max(result.maximumTurn, result.total); result.frames++; previous = angle;
            if (now - started < \(sampleMilliseconds)) requestAnimationFrame(sample);
            else result.finished = true;
          };
          requestAnimationFrame(sample);
          window.aiUsageReceive({ ...state, busy: true });
          setTimeout(() => window.aiUsageReceive({ ...state, busy: false }), \(activeMilliseconds));
        })();
        """)
        try await Task.sleep(nanoseconds: UInt64(sampleMilliseconds + 100) * 1_000_000)
        var result = try await web.evaluateJavaScript("window.__refreshRotation") as? Object ?? [:]
        result["busyMilliseconds"] = activeMilliseconds
        result["passed"] = result["finished"] as? Bool == true && (result["frames"] as? Int ?? 0) > 20
            && abs((result["total"] as? Double ?? 0) - 360) < 1
            && (result["minimumStep"] as? Double ?? -.infinity) >= -0.1
            && (result["maximumTurn"] as? Double ?? .infinity) <= 361
        return result
    }
    private func runSmoke() {
        guard smokeDirectory != nil, !smokeStarted else { return }; smokeStarted = true
        Task {
            do {
                try await Task.sleep(nanoseconds: 1_000_000_000)
                let first = try await web.evaluateJavaScript("({text: document.body.innerText, cards: document.querySelectorAll('.provider').length, platform: !!window.aiUsageNative})") as? Object ?? [:]
                let fastRefresh = try await verifyRefreshRotation(activeMilliseconds: 100)
                await store.action(["action": "toggleDetails"])
                try await Task.sleep(nanoseconds: 700_000_000)
                let compact = try await web.evaluateJavaScript("document.querySelector('.compact') !== null && !!document.querySelector('.header .refresh') && !document.querySelector('footer')") as? Bool ?? false
                let slowRefresh = try await verifyRefreshRotation(activeMilliseconds: 2400)
                await store.action(["action": "showFull"])
                showPopover()
                try await Task.sleep(nanoseconds: 700_000_000)
                let layout = try await web.evaluateJavaScript("({viewport: innerHeight, card: document.querySelector('.widget').getBoundingClientRect().bottom, refreshInHeader: !!document.querySelector('.header .refresh'), footerRemoved: !document.querySelector('footer')})") as? Object ?? [:]
                let snapshot = try await web.takeSnapshot(configuration: nil)
                if let data = snapshot.tiffRepresentation, let bitmap = NSBitmapImageRep(data: data), let png = bitmap.representation(using: .png, properties: [:]), let folder = smokeDirectory {
                    try JSON.writeData(png, to: folder.appendingPathComponent("menu-bar.png"))
                }
                let cardFits = (layout["card"] as? Double ?? .infinity) <= (layout["viewport"] as? Double ?? 0) + 2
                let passed = first["platform"] as? Bool == true && (first["cards"] as? Int ?? 0) == 2 && compact && cardFits
                    && layout["refreshInHeader"] as? Bool == true && layout["footerRemoved"] as? Bool == true
                    && fastRefresh["passed"] as? Bool == true && slowRefresh["passed"] as? Bool == true
                finishSmoke(["passed": passed, "readyHandshake": ready, "statusItem": item.button != nil,
                             "renderedCards": first["cards"] ?? 0, "compactMode": compact, "cardFits": cardFits, "layout": layout,
                             "fastRefresh": fastRefresh, "slowRefresh": slowRefresh, "text": first["text"] ?? ""])
            } catch { finishSmoke(["passed": false, "error": String(describing: error)]) }
        }
    }
    private func finishSmoke(_ result: Object) {
        if let smokeDirectory { try? JSON.writeObject(result, to: smokeDirectory.appendingPathComponent("smoke.json")) }
        NSApp.terminate(nil)
    }
}
