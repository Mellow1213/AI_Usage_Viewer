import AppKit

signal(SIGPIPE, SIG_IGN)

if let index = CommandLine.arguments.firstIndex(of: "--statusline"), index + 1 < CommandLine.arguments.count {
    ClaudeBridge.receive(directory: URL(fileURLWithPath: CommandLine.arguments[index + 1]))
} else {
    MainActor.assumeIsolated {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let delegate = AppDelegate()
        app.delegate = delegate
        app.run()
    }
}
