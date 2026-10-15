import AppKit
import WebKit

@MainActor extension WKWebView {
    // Older SDK async overlays force-unwrap undefined JavaScript results.
    // The callback API preserves nil and also works for read-only object results.
    @discardableResult func widgetEvaluate(_ script: String) async throws -> Any? {
        try await withCheckedThrowingContinuation { continuation in
            evaluateJavaScript(script) { result, error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume(returning: result) }
            }
        }
    }
}

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
    private var contentVisible = false
    private var lastPayload = ""
    private var releaseTask: Task<Void, Never>?
    private var savedUI: Object = [:]
    private var savedSize = NSSize(width: 304, height: 400)
    private var wakeObserver: NSObjectProtocol?
    private var iconTimer: Timer?
    private var iconPhase = 0.0
    private var iconRefresh = RefreshRotation()
    private var connected = false
    private var iconBusy = false
    private var lockDescriptor: Int32 = -1
    private var smokeDirectory: URL?
    private var benchmarkDirectory: URL?
    private var benchmarkStarted = false
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
            if args.contains("--fixture"), let directory = argument("--memory-benchmark") { benchmarkDirectory = URL(fileURLWithPath: directory) }
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
            popover.behavior = smokeDirectory == nil && benchmarkDirectory == nil ? .transient : .applicationDefined
            popover.animates = store.preferences.animateTransitions; popover.delegate = self
            store.changed = { [weak self] in self?.publish() }
            store.hide = { [weak self] in self?.popover.performClose(nil) }
            // Polling and menu-bar updates must not depend on a loaded web page.
            store.start()
            publish()
            wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor [weak self] in await self?.store.refresh() }
            }
            if benchmarkDirectory != nil { runMemoryBenchmark() }
            else if smokeDirectory != nil {
                showPopover()
                Task { [weak self] in
                    try? await Task.sleep(nanoseconds: 25_000_000_000)
                    guard let self, !self.smokeStarted, !self.benchmarkStarted else { return }
                    self.finishSmoke(["passed": false, "error": "WebKit did not complete the ready handshake."])
                }
            } else if store.preferences.visible.isEmpty { showPopover() }
        } catch {
            let alert = NSAlert(); alert.messageText = "AI Usage를 시작하지 못했습니다."
            alert.informativeText = "앱 파일과 저장 폴더의 권한을 확인해주세요."; alert.runModal(); NSApp.terminate(nil)
        }
    }
    private func ensureWebContent() {
        guard web == nil else { return }
        let state = (try? JSONSerialization.data(withJSONObject: store.state(maxHeight: maxHeight))) ?? Data("{}".utf8)
        let session = (try? JSONSerialization.data(withJSONObject: savedUI)) ?? Data("{}".utf8)
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        configuration.setURLSchemeHandler(LocalWebContent(root: Bundle.main.resourceURL!.appendingPathComponent("Web")), forURLScheme: "aiusage")
        configuration.userContentController.add(self, name: "native")
        let bridge = """
        (() => {
          const listeners = new Set();
          window.aiUsageNative = {
            visible: false,
            motionOverride: \(store.fixture && (smokeDirectory != nil || benchmarkDirectory != nil) ? "false" : "null"),
            initialState: \(String(decoding: state, as: UTF8.self)),
            uiState: \(String(decoding: session, as: UTF8.self)),
            postMessage: value => window.webkit.messageHandlers.native.postMessage(value),
            addEventListener: (name, callback) => { if (name === 'message') listeners.add(callback); }
          };
          window.aiUsageReceive = value => { for (const callback of listeners) callback({ data: value }); };
          window.aiUsageSetVisible = visible => {
            if (window.aiUsageNative.visible === visible) return;
            window.aiUsageNative.visible = visible;
            window.dispatchEvent(new Event('usage-visibility'));
          };
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
        web = WKWebView(frame: NSRect(origin: .zero, size: savedSize), configuration: configuration)
        web.navigationDelegate = self; web.uiDelegate = self
        web.underPageBackgroundColor = NSColor(calibratedRed: 17/255, green: 19/255, blue: 22/255, alpha: 1)
        let controller = NSViewController(); controller.view = web
        popover.contentViewController = controller; popover.contentSize = savedSize
        web.load(URLRequest(url: URL(string: "aiusage://widget/index.html")!))
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showPopover(); return true }
    func applicationWillTerminate(_ notification: Notification) {
        releaseTask?.cancel(); store?.stop(); iconTimer?.invalidate()
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
        releaseTask?.cancel(); releaseTask = nil
        ensureWebContent()
        NSApp.activate(ignoringOtherApps: true)
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        contentVisible = popover.isShown
        popover.contentViewController?.view.window?.makeKey()
        publish(force: true)
    }
    func popoverWillShow(_ notification: Notification) { contentVisible = true }
    func popoverDidShow(_ notification: Notification) { contentVisible = true; publish(force: true) }
    func popoverWillClose(_ notification: Notification) {
        contentVisible = false
        guard ready else { return }
        web.evaluateJavaScript("window.aiUsageSetVisible(false);", completionHandler: nil)
    }
    func popoverDidClose(_ notification: Notification) {
        releaseTask?.cancel()
        releaseTask = Task { [weak self] in
            do {
                // Brief reopenings stay instant; an idle card releases its web processes.
                try await Task.sleep(nanoseconds: 5_000_000_000)
                guard let self, !self.popover.isShown, self.ready, let page = self.web else { return }
                guard let snapshot = try await page.widgetEvaluate("window.aiUsageCaptureUI?.()") as? Object else { return }
                guard !Task.isCancelled, !self.popover.isShown, self.web === page else { return }
                self.savedUI = snapshot
                self.discardWebContent()
            } catch { /* Keep the live page if its editing state could not be captured. */ }
        }
    }
    private func discardWebContent() {
        guard let page = web else { return }
        ready = false; lastPayload = ""
        page.stopLoading()
        page.configuration.userContentController.removeScriptMessageHandler(forName: "native")
        page.navigationDelegate = nil; page.uiDelegate = nil
        popover.contentViewController = nil
        page.removeFromSuperview(); web = nil
    }
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.webView === web, message.name == "native", message.frameInfo.isMainFrame,
              message.frameInfo.securityOrigin.protocol == "aiusage", message.frameInfo.securityOrigin.host == "widget",
              let body = message.body as? Object, let action = body["action"] as? String else { return }
        if action == "ready" { ready = true; publish(force: true); store.start(); runSmoke(); runMemoryBenchmark(); return }
        if action == "height" {
            guard let height = body["height"] as? Double, height.isFinite,
                  body["viewMode"] as? Int == store.preferences.viewMode, body["viewRevision"] as? Int == store.viewRevision else { return }
            let size = NSSize(width: 304, height: min(maxHeight, max(140, height + 2)))
            if abs(popover.contentSize.height - size.height) > 0.5 { savedSize = size; popover.contentSize = size }
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
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        guard webView === web else { return }
        ready = false; lastPayload = ""
        if popover.isShown { webView.reload() } else { discardWebContent() }
    }
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
        // Keep polling in the background, but deliver only the latest state when reopened.
        guard ready, contentVisible, let data = try? JSONSerialization.data(withJSONObject: state, options: [.sortedKeys]),
              let payload = String(data: data, encoding: .utf8), force || payload != lastPayload else { return }
        lastPayload = payload
        web.evaluateJavaScript("window.aiUsageSetVisible(true); window.aiUsageReceive(\(payload));", completionHandler: nil)
    }
    private func updateIconAnimation() {
        let allowed = store.preferences.animateTransitions && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        if !allowed { iconRefresh = RefreshRotation() }
        let animate = (connected || iconBusy || iconRefresh.isRunning(at: ProcessInfo.processInfo.systemUptime)) && allowed
        if animate && iconTimer == nil {
            iconTimer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
                Task { @MainActor [weak self] in guard let self else { return }; self.iconPhase += 0.1; self.updateIconAnimation(); self.drawIcon() }
            }
            iconTimer?.tolerance = 0.02
        } else if !animate { iconTimer?.invalidate(); iconTimer = nil; iconPhase = 0 }
    }
    private func drawIcon() {
        let phase = iconPhase, rotation = iconRefresh.angle(at: ProcessInfo.processInfo.systemUptime), active = connected
        let image = NSImage(size: NSSize(width: 20, height: 20), flipped: true) { rect in
            NSGraphicsContext.saveGraphicsState(); defer { NSGraphicsContext.restoreGraphicsState() }
            let transform = NSAffineTransform()
            transform.translateX(by: rect.midX, yBy: rect.midY)
            transform.rotate(byRadians: CGFloat(rotation * .pi / 180))
            // Center the three triangles' centroid on the image before rotating.
            transform.scale(by: 0.5); transform.translateX(by: -20, yBy: -70.0 / 3.0); transform.concat()
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
    private func waitForScriptCompletion(_ name: String) async throws {
        for _ in 0..<200 {
            if try await web.widgetEvaluate("window.\(name)?.finished === true") as? Bool == true { return }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        throw WidgetError.message("WebKit verification did not finish: \(name)")
    }
    private func verifyRefreshRotation(activeMilliseconds: Int, burst: Bool = false) async throws -> Object {
        // WebKit suspends animation frames while the transient popover is closed.
        // Finish the startup turn before measuring a separate refresh request.
        showPopover()
        try await Task.sleep(nanoseconds: UInt64((RefreshRotation.duration + 0.25) * 1_000_000_000))
        let data = try JSONSerialization.data(withJSONObject: store.state(maxHeight: maxHeight))
        let state = String(decoding: data, as: UTF8.self)
        let sampleMilliseconds = max(2100, activeMilliseconds + 350)
        try await web.widgetEvaluate("""
        (() => {
          const state = \(state), logo = document.querySelector('.triangle');
          const readAngle = () => {
            const transform = getComputedStyle(logo).transform;
            const matrix = transform === 'none' ? new DOMMatrixReadOnly() : new DOMMatrixReadOnly(transform);
            return Math.atan2(matrix.b, matrix.a) * 180 / Math.PI;
          };
          const result = window.__refreshRotation = { frames: 0, total: 0, minimumStep: 0, maximumTurn: 0, finished: false,
            initial: { visible: window.aiUsageNative.visible, hidden: document.hidden, reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
              transitioning: !!document.querySelector('.is-logo-moving') }, sawBusy: false, sawSyncing: false, maximumAnimations: 0 };
          let previous = readAngle();
          const started = performance.now();
          const sample = now => {
            const angle = readAngle(), step = ((angle - previous + 540) % 360) - 180;
            result.total += step; result.minimumStep = Math.min(result.minimumStep, step);
            result.maximumTurn = Math.max(result.maximumTurn, result.total); result.frames++; previous = angle;
            result.sawBusy ||= logo.getAttribute('aria-label') === '갱신 중';
            result.sawSyncing ||= logo.classList.contains('syncing');
            result.maximumAnimations = Math.max(result.maximumAnimations, logo.getAnimations().length);
            if (now - started < \(sampleMilliseconds)) requestAnimationFrame(sample);
            else result.finished = true;
          };
          requestAnimationFrame(sample);
          window.aiUsageReceive({ ...state, busy: true });
          setTimeout(() => window.aiUsageReceive({ ...state, busy: false }), \(activeMilliseconds));
          if (\(burst)) for (let i = 1; i < 10; i++) {
            setTimeout(() => window.aiUsageReceive({ ...state, busy: true }), i * 140);
            setTimeout(() => window.aiUsageReceive({ ...state, busy: false }), i * 140 + 40);
          }
        })();
        """)
        try await waitForScriptCompletion("__refreshRotation")
        var result = try await web.widgetEvaluate("window.__refreshRotation") as? Object ?? [:]
        result["busyMilliseconds"] = activeMilliseconds
        result["refreshes"] = burst ? 10 : 1
        result["passed"] = result["finished"] as? Bool == true && (result["frames"] as? Int ?? 0) > 20
            && abs((result["total"] as? Double ?? 0) - 360) < 1
            && (result["minimumStep"] as? Double ?? -.infinity) >= -0.1
            && (result["maximumTurn"] as? Double ?? .infinity) <= 361
        return result
    }
    private func verifyHiddenRefreshRotation(interrupt: Bool = false) async throws -> Object {
        showPopover()
        try await Task.sleep(nanoseconds: 1_850_000_000)
        let data = try JSONSerialization.data(withJSONObject: store.state(maxHeight: maxHeight))
        let state = String(decoding: data, as: UTF8.self)
        var interruptedAngle = 0.0
        if interrupt {
            let readAngle = "(() => { const m = new DOMMatrixReadOnly(getComputedStyle(document.querySelector('.triangle')).transform); return Math.atan2(m.b, m.a) * 180 / Math.PI; })()"
            let initialAngle = try await web.widgetEvaluate(readAngle) as? Double ?? 0
            try await web.widgetEvaluate("window.aiUsageReceive({ ...\(state), busy: true });")
            try await Task.sleep(nanoseconds: 350_000_000)
            let angle = try await web.widgetEvaluate(readAngle) as? Double ?? 0
            interruptedAngle = abs((angle - initialAngle + 540).truncatingRemainder(dividingBy: 360) - 180)
        }
        popover.performClose(nil)
        try await Task.sleep(nanoseconds: 250_000_000)
        try await web.widgetEvaluate("window.__hiddenHostUpdates = 0; window.aiUsageNative.addEventListener('message', () => window.__hiddenHostUpdates++);")
        for _ in 0..<3 { publish(force: true) }
        let hidden = try await web.widgetEvaluate("({visible: window.aiUsageNative.visible, hostUpdates: window.__hiddenHostUpdates})") as? Object ?? [:]
        // Accelerate several background polling cycles while WebKit cannot paint.
        for _ in 0..<(interrupt ? 1 : 12) {
            if !interrupt {
                try await web.widgetEvaluate("window.aiUsageReceive({ ...\(state), busy: true });")
                try await Task.sleep(nanoseconds: 80_000_000)
            }
            try await web.widgetEvaluate("window.aiUsageReceive({ ...\(state), busy: false });")
            try await Task.sleep(nanoseconds: 80_000_000)
        }
        try await web.widgetEvaluate("""
        (() => {
          const logo = document.querySelector('.triangle');
          const readAngle = () => {
            const transform = getComputedStyle(logo).transform;
            const matrix = transform === 'none' ? new DOMMatrixReadOnly() : new DOMMatrixReadOnly(transform);
            return Math.atan2(matrix.b, matrix.a) * 180 / Math.PI;
          };
          const result = window.__hiddenRefreshRotation = { frames: 0, travel: 0, finished: false };
          let previous = readAngle(), started;
          const sample = now => {
            started ??= now;
            const angle = readAngle();
            result.travel += Math.abs(((angle - previous + 540) % 360) - 180);
            result.frames++; previous = angle;
            if (now - started < 2100) requestAnimationFrame(sample);
            else result.finished = true;
          };
          requestAnimationFrame(sample);
        })();
        """)
        showPopover()
        try await waitForScriptCompletion("__hiddenRefreshRotation")
        var result = try await web.widgetEvaluate("window.__hiddenRefreshRotation") as? Object ?? [:]
        result["backgroundRefreshes"] = interrupt ? 0 : 12
        result["interrupted"] = interrupt
        result["interruptedAngle"] = interruptedAngle
        result["hidden"] = hidden
        result["passed"] = result["finished"] as? Bool == true && (result["frames"] as? Int ?? 0) > 20
            && (result["travel"] as? Double ?? .infinity) < 1
            && hidden["visible"] as? Bool == false && hidden["hostUpdates"] as? Int == 0
            && (!interrupt || interruptedAngle > 1)
        return result
    }
    private func verifyViewRotation(action: String, clockwise: Bool) async throws -> Object {
        try await web.widgetEvaluate("""
        (() => {
          const result = window.__viewRotation = { frames: 0, total: 0, minimumStep: 0, maximumStep: 0, finished: false };
          let previous;
          const started = performance.now();
          const sample = now => {
            const logo = document.querySelector('.transition-mark .triangle');
            if (logo) {
              const matrix = new DOMMatrixReadOnly(getComputedStyle(logo).transform);
              const angle = Math.atan2(matrix.b, matrix.a) * 180 / Math.PI;
              if (previous !== undefined) {
                const step = ((angle - previous + 540) % 360) - 180;
                result.total += step; result.minimumStep = Math.min(result.minimumStep, step); result.maximumStep = Math.max(result.maximumStep, step);
              }
              previous = angle; result.frames++;
            }
            if (now - started < 800) requestAnimationFrame(sample);
            else result.finished = true;
          };
          requestAnimationFrame(sample);
        })();
        """)
        await store.action(["action": action])
        try await waitForScriptCompletion("__viewRotation")
        var result = try await web.widgetEvaluate("window.__viewRotation") as? Object ?? [:]
        let total = result["total"] as? Double ?? 0
        result["clockwise"] = clockwise
        result["passed"] = result["finished"] as? Bool == true && (result["frames"] as? Int ?? 0) > 10
            && abs(total) <= 121 && (clockwise ? total > 100 : total < -100)
            && (clockwise ? (result["minimumStep"] as? Double ?? -.infinity) >= -0.1 : (result["maximumStep"] as? Double ?? .infinity) <= 0.1)
        return result
    }
    private func verifyIdleWebRelease() async throws -> Object {
        func pause(_ seconds: Double) async throws { try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000)) }
        @MainActor func reopen() async throws {
            showPopover()
            for _ in 0..<100 { if ready { break }; try await pause(0.05) }
            guard ready else { throw WidgetError.message("Released WebKit did not become ready again.") }
            try await pause(0.4)
        }
        await store.action(["action": "toggleDetails"])
        try await pause(0.7)
        weak var warmPage = web
        popover.performClose(nil); try await pause(0.2); try await reopen()
        let quickReopen = web === warmPage
        warmPage = nil
        let initialIDs = Set(store.preferences.visible)
        for _ in 0..<12 { await store.action(["action": "addProvider", "provider": "codex"]) }
        _ = try await web.widgetEvaluate("document.querySelector('[aria-label=\"설정\"]').click()")
        try await pause(0.1)
        _ = try await web.widgetEvaluate("[...document.querySelectorAll('.settings-link')].find(e => e.textContent.includes('계정 관리')).click()")
        try await pause(0.1)
        _ = try await web.widgetEvaluate("document.querySelector('.account-summary').click()")
        try await pause(0.1)
        _ = try await web.widgetEvaluate("""
        (() => {
          const input = document.getElementById('nickname-codex');
          Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, '저장 전 별명');
          input.dispatchEvent(new Event('input', { bubbles: true }));
          input.focus(); input.setSelectionRange(2, 4);
        })();
        """)
        try await pause(0.1)
        _ = try await web.widgetEvaluate("document.querySelector('.settings-content').scrollTop = 80")
        let before = try await web.widgetEvaluate("window.aiUsageCaptureUI()") as? Object ?? [:]
        weak var previousPage = web
        popover.performClose(nil); try await pause(6)
        let released = web == nil && previousPage == nil
        previousPage = nil
        try await reopen()
        let restored = try await web.widgetEvaluate("window.aiUsageCaptureUI()") as? Object ?? [:]
        let draft = (restored["form"] as? Object)?["nickname-codex"] as? Object
        let restoredSettings = restored["menu"] as? Bool == true && restored["menuPage"] as? String == "accounts"
            && restored["expandedAccountId"] as? String == "codex" && draft?["value"] as? String == "저장 전 별명"
        let beforeScroll = (before["scroll"] as? [Object])?.first?["top"] as? Double ?? 0
        let afterScroll = (restored["scroll"] as? [Object])?.first?["top"] as? Double ?? -1
        let scrollRestored = beforeScroll > 0 && abs(beforeScroll - afterScroll) < 1
        let selection = restored["focus"] as? Object
        let focusRestored = selection?["id"] as? String == "nickname-codex" && selection?["start"] as? Int == 2 && selection?["end"] as? Int == 4
        let noImplicitSave = store.preferences.profiles.first { $0.id == "codex" }?.nickname == nil
        let noReplay = try await web.widgetEvaluate("!document.querySelector('.is-logo-moving')") as? Bool == true
        _ = try await web.widgetEvaluate("document.querySelector('[aria-label=\"설정 닫기\"]').click()")
        try await pause(0.1)
        let compactRestored = try await web.widgetEvaluate("document.querySelector('.compact') !== null") as? Bool == true
        for id in store.preferences.visible where !initialIDs.contains(id) { await store.action(["action": "removeProvider", "provider": id]) }
        _ = try await web.widgetEvaluate("document.querySelector('[aria-label=\"설정\"]').click()")
        try await pause(0.1)
        _ = try await web.widgetEvaluate("[...document.querySelectorAll('.settings-link')].find(e => e.textContent.includes('사용량 측정')).click()")
        await store.action(["action": "setMeasurementInterval", "minutes": 17])
        try await pause(0.1)
        popover.performClose(nil); try await pause(6); try await reopen()
        let measurementRestored = try await web.widgetEvaluate("document.getElementById('settings-title')?.textContent === '사용량 측정' && document.getElementById('measurement-interval')?.value === '17'") as? Bool == true
        await store.action(["action": "setMeasurementInterval", "minutes": 30])
        _ = try await web.widgetEvaluate("document.querySelector('[aria-label=\"설정 닫기\"]').click()")
        let refresh = try await verifyRefreshRotation(activeMilliseconds: 100)
        let direction = try await verifyViewRotation(action: "showFull", clockwise: false)
        let passed = quickReopen && released && restoredSettings && noImplicitSave && noReplay && compactRestored
            && scrollRestored && focusRestored && measurementRestored
            && refresh["passed"] as? Bool == true && direction["passed"] as? Bool == true
        return ["passed": passed, "quickReopenReusesPage": quickReopen, "releasedPage": released,
            "restoredSettingsAndDraft": restoredSettings, "noImplicitSave": noImplicitSave,
            "compactModeRestored": compactRestored, "noReplayedTransition": noReplay,
            "scrollRestored": scrollRestored, "focusAndSelectionRestored": focusRestored, "measurementRestored": measurementRestored,
            "before": before, "restored": restored, "refreshAfterRelease": refresh, "directionAfterRelease": direction]
    }
    private func runMemoryBenchmark() {
        guard let folder = benchmarkDirectory, !benchmarkStarted else { return }; benchmarkStarted = true
        Task {
            do {
                func pause(_ seconds: Double) async throws { try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000)) }
                @MainActor func phase(_ name: String) async throws {
                    try JSON.writeObject(["phase": name, "pid": ProcessInfo.processInfo.processIdentifier,
                        "webLoaded": web != nil, "visible": popover.isShown], to: folder.appendingPathComponent("phase.json"))
                    try await pause(7)
                    try? FileManager.default.removeItem(at: folder.appendingPathComponent("phase.json"))
                }
                try await pause(3); try await phase("cold-hidden")
                showPopover()
                for _ in 0..<100 { if ready { break }; try await pause(0.05) }
                guard ready else { throw WidgetError.message("The initial card did not become ready.") }
                try await pause(3); try await phase("expanded")
                await store.action(["action": "toggleDetails"])
                try await pause(2); try await phase("compact")
                popover.performClose(nil)
                try await pause(8); try await phase("hidden")
                var reopenTimes = [Double]()
                for cycle in 1...4 {
                    let start = ProcessInfo.processInfo.systemUptime
                    showPopover()
                    for _ in 0..<100 {
                        if ready, let page = web,
                           try await page.widgetEvaluate("document.querySelector('.compact') !== null") as? Bool == true { break }
                        try await pause(0.05)
                    }
                    guard ready, let page = web,
                          try await page.widgetEvaluate("document.querySelector('.compact') !== null") as? Bool == true else {
                        throw WidgetError.message("The card did not recover after idle release.")
                    }
                    reopenTimes.append((ProcessInfo.processInfo.systemUptime - start) * 1000)
                    try await pause(2)
                    if cycle == 1 { try await phase("reopened") }
                    popover.performClose(nil); try await pause(8)
                }
                try await phase("hidden-after-cycles")
                try JSON.writeObject(["passed": true, "reopenMilliseconds": reopenTimes,
                    "webLoadedWhileHidden": web != nil], to: folder.appendingPathComponent("memory-benchmark.json"))
            } catch { try? JSON.writeObject(["passed": false, "error": String(describing: error)], to: folder.appendingPathComponent("memory-benchmark.json")) }
            NSApp.terminate(nil)
        }
    }
    private func runSmoke() {
        guard smokeDirectory != nil, !smokeStarted else { return }; smokeStarted = true
        Task {
            do {
                @MainActor func checkpoint(_ phase: String) {
                    if let smokeDirectory { try? JSON.writeObject(["phase": phase], to: smokeDirectory.appendingPathComponent("progress.json")) }
                }
                checkpoint("show")
                showPopover()
                try await Task.sleep(nanoseconds: 1_000_000_000)
                let first = try await web.widgetEvaluate("({text: document.body.innerText, cards: document.querySelectorAll('.provider').length, platform: !!window.aiUsageNative})") as? Object ?? [:]
                let fastRefresh = try await verifyRefreshRotation(activeMilliseconds: 100)
                checkpoint("fast-refresh-complete")
                try await web.widgetEvaluate("window.aiUsageNative.motionOverride = true; window.dispatchEvent(new Event('usage-motion'));")
                try await Task.sleep(nanoseconds: 200_000_000)
                let reducedRefresh = try await verifyRefreshRotation(activeMilliseconds: 100)
                let reducedMotionHonored = abs(reducedRefresh["total"] as? Double ?? .infinity) < 1 && reducedRefresh["sawBusy"] as? Bool == true
                    && reducedRefresh["sawSyncing"] as? Bool == false
                try await web.widgetEvaluate("window.aiUsageNative.motionOverride = false; window.dispatchEvent(new Event('usage-motion'));")
                try await Task.sleep(nanoseconds: 200_000_000)
                let compactRotation = try await verifyViewRotation(action: "toggleDetails", clockwise: true)
                let compact = try await web.widgetEvaluate("document.querySelector('.compact') !== null && !!document.querySelector('.header .refresh') && !document.querySelector('footer')") as? Bool ?? false
                let slowRefresh = try await verifyRefreshRotation(activeMilliseconds: 2400)
                checkpoint("slow-refresh-complete")
                let hiddenRefresh = try await verifyHiddenRefreshRotation()
                let interruptedRefresh = try await verifyHiddenRefreshRotation(interrupt: true)
                let reopenedRefresh = try await verifyRefreshRotation(activeMilliseconds: 100, burst: true)
                let expandedRotation = try await verifyViewRotation(action: "showFull", clockwise: false)
                checkpoint("rotations-complete")
                let layout = try await web.widgetEvaluate("({viewport: innerHeight, card: document.querySelector('.widget').getBoundingClientRect().bottom, refreshInHeader: !!document.querySelector('.header .refresh'), footerRemoved: !document.querySelector('footer')})") as? Object ?? [:]
                let snapshot = try await web.takeSnapshot(configuration: nil)
                checkpoint("snapshot-complete")
                if let data = snapshot.tiffRepresentation, let bitmap = NSBitmapImageRep(data: data), let png = bitmap.representation(using: .png, properties: [:]), let folder = smokeDirectory {
                    try JSON.writeData(png, to: folder.appendingPathComponent("menu-bar.png"))
                }
                let cardFits = (layout["card"] as? Double ?? .infinity) <= (layout["viewport"] as? Double ?? 0) + 2
                let memory = CommandLine.arguments.contains("--verify-memory") ? try await verifyIdleWebRelease() : ["passed": true, "skipped": true]
                checkpoint("memory-complete")
                let passed = reducedMotionHonored && memory["passed"] as? Bool == true && first["platform"] as? Bool == true && (first["cards"] as? Int ?? 0) == 2 && compact && cardFits
                    && layout["refreshInHeader"] as? Bool == true && layout["footerRemoved"] as? Bool == true
                    && fastRefresh["passed"] as? Bool == true && slowRefresh["passed"] as? Bool == true
                    && hiddenRefresh["passed"] as? Bool == true
                    && interruptedRefresh["passed"] as? Bool == true && reopenedRefresh["passed"] as? Bool == true
                    && compactRotation["passed"] as? Bool == true && expandedRotation["passed"] as? Bool == true
                finishSmoke(["passed": passed, "readyHandshake": ready, "statusItem": item.button != nil,
                             "renderedCards": first["cards"] ?? 0, "compactMode": compact, "cardFits": cardFits, "layout": layout, "memory": memory,
                             "fastRefresh": fastRefresh, "reducedMotionHonored": reducedMotionHonored, "slowRefresh": slowRefresh, "hiddenRefresh": hiddenRefresh,
                             "compactRotation": compactRotation, "expandedRotation": expandedRotation,
                             "interruptedRefresh": interruptedRefresh, "reopenedRefresh": reopenedRefresh, "text": first["text"] ?? ""])
            } catch { finishSmoke(["passed": false, "error": String(describing: error)]) }
        }
    }
    private func finishSmoke(_ result: Object) {
        if let smokeDirectory { try? JSON.writeObject(result, to: smokeDirectory.appendingPathComponent("smoke.json")) }
        NSApp.terminate(nil)
    }
}
