import Foundation

struct RefreshRotation {
    static let duration = 1.6
    private var start: TimeInterval?
    private var from = 0.0
    private var target = 0.0

    mutating func begin(at now: TimeInterval) {
        from = angle(at: now)
        target += 360
        start = now
    }

    func angle(at now: TimeInterval) -> Double {
        guard let start else { return 0 }
        let progress = min(1, max(0, (now - start) / Self.duration))
        return from + (target - from) * (0.5 - 0.5 * cos(.pi * progress))
    }

    func isRunning(at now: TimeInterval) -> Bool {
        guard let start else { return false }
        return now - start < Self.duration
    }
}
