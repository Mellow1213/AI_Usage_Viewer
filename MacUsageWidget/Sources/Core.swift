import Foundation
import CoreFoundation
import CryptoKit

typealias Object = [String: Any]

enum WidgetError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case .message(let value) = self { return value }; return nil }
}

enum JSON {
    static let encoder: JSONEncoder = {
        let value = JSONEncoder(); value.dateEncodingStrategy = .iso8601
        value.outputFormatting = [.sortedKeys]; return value
    }()
    static let decoder: JSONDecoder = {
        let value = JSONDecoder(); value.dateDecodingStrategy = .iso8601; return value
    }()
    static func object(_ data: Data) throws -> Object {
        guard let value = try JSONSerialization.jsonObject(with: data) as? Object else {
            throw WidgetError.message("응답 형식이 올바르지 않습니다.")
        }
        return value
    }
    static func object<T: Encodable>(_ value: T) -> Object {
        (try? object(encoder.encode(value))) ?? [:]
    }
    static func read<T: Decodable>(_ url: URL, as type: T.Type) -> T? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? decoder.decode(type, from: data)
    }
    static func write<T: Encodable>(_ value: T, to url: URL) throws { try writeData(encoder.encode(value), to: url) }
    static func writeObject(_ value: Object, to url: URL) throws {
        try writeData(JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]), to: url)
    }
    static func writeData(_ data: Data, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true,
                                               attributes: [.posixPermissions: 0o700])
        try data.write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
    static func date(_ string: String?) -> Date? {
        guard let string else { return nil }
        let parser = ISO8601DateFormatter()
        if let value = parser.date(from: string) { return value }
        parser.formatOptions.insert(.withFractionalSeconds); return parser.date(from: string)
    }
}

struct Profile: Codable {
    let id: String
    let service: String
    var name: String
    var enabled = false
    var useSystem = false
    var nickname: String?
    var displayName: String { nickname ?? name }
    static func primary(_ service: String) -> Profile {
        Profile(id: service, service: service, name: service == "codex" ? "Codex" : "Claude")
    }
    static func valid(_ id: String, service: String) -> Bool {
        guard ["codex", "claude"].contains(service) else { return false }
        if id == service { return true }
        guard id.hasPrefix(service + "-") else { return false }
        let suffix = id.dropFirst(service.count + 1)
        return suffix.count == 32 && suffix.allSatisfy { $0.isHexDigit && $0.isASCII }
    }
    func home(in root: URL) -> URL {
        let serviceName = service == "codex" ? "Codex" : "Claude"
        return root.appendingPathComponent("Accounts").appendingPathComponent(
            id == service ? serviceName : serviceName + "Profiles/" + id.dropFirst(service.count + 1))
    }
}

struct Preferences: Codable {
    var profiles = [Profile.primary("codex"), Profile.primary("claude")]
    var visible: [String] = []
    var viewMode = 0
    var animateTransitions = true
    var intervalMinutes = 30
    var windowMinutes = 30
    mutating func normalize() {
        var seen = Set<String>()
        profiles = profiles.filter { Profile.valid($0.id, service: $0.service) && seen.insert($0.id).inserted }
        for service in ["codex", "claude"] where !profiles.contains(where: { $0.id == service }) { profiles.append(.primary(service)) }
        for index in profiles.indices { profiles[index].useSystem = profiles[index].id == profiles[index].service && profiles[index].useSystem }
        seen.removeAll()
        visible = visible.filter { id in profiles.contains { $0.id == id } && seen.insert(id).inserted }
        viewMode = viewMode == 1 ? 1 : 0
        intervalMinutes = min(180, max(1, intervalMinutes))
        if ![30, 60, 180, 360, 720, 1440].contains(windowMinutes) { windowMinutes = 30 }
    }
}

struct Account: Codable {
    var id: String
    var source = "widget"
    var status = "disconnected"
    var email: String?
    var plan: String?
    var message: String?
}
struct DailyUsage: Codable {
    var usedPercentagePoints: Double
    var cyclePercentagePoints: Double
    var observedSince: Date
    var measuredAt: Date
    var sourceAt: Date
    var nextMeasurementAt: Date
    var recentPercentagePoints: Double
    var windowMinutes: Int
    var isCurrentDay: Bool
}
struct UsageTrend: Codable {
    var points: [[Double]] = []
    var coverage: [[Double]] = []
    var error: String?
}
struct Quota: Codable {
    var key: String
    var label: String
    var usedPercent: Double
    var resetsAt: Double?
    var today: DailyUsage?
    var trend: UsageTrend?
    var remainingPercent: Double { max(0, min(100, 100 - usedPercent)) }
    var object: Object { var value = JSON.object(self); value["remainingPercent"] = remainingPercent; return value }
}
struct Usage: Codable {
    var id: String
    var status = "disconnected"
    var message = "계정을 연결해주세요."
    var windows: [Quota] = []
    var updatedAt: Date?
    var plan: String?
    var object: Object { var value = JSON.object(self); value["windows"] = windows.map(\.object); return value }
}

enum UsageParser {
    static func percentage(_ value: Any?) -> Double? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let result = number.doubleValue
        return result.isFinite && result >= 0 ? result : nil
    }
    static func timestamp(_ value: Any?) -> Double? {
        guard let number = percentage(value), number <= 253402300799 else { return nil }; return number
    }
    static func codex(_ result: Object, now: Date = Date()) -> Usage {
        let buckets = result["rateLimitsByLimitId"] as? Object
        guard let limits = (buckets?["codex"] as? Object) ?? (result["rateLimits"] as? Object) else {
            return Usage(id: "codex", status: "unavailable", message: "이 계정의 구독 한도 정보가 없습니다.")
        }
        let windows: [Quota] = ["primary", "secondary"].compactMap { key in
            guard let item = limits[key] as? Object, let used = percentage(item["usedPercent"]) else { return nil }
            let minutes = (item["windowDurationMins"] as? NSNumber)?.intValue ?? 0
            let label = minutes == 300 ? "5시간" : minutes == 10080 ? "주간" : minutes > 0 ? "\(Double(minutes) / 60)시간" : "한도"
            return Quota(key: key, label: label, usedPercent: used, resetsAt: timestamp(item["resetsAt"]))
        }
        return Usage(id: "codex", status: windows.isEmpty ? "unavailable" : "ready",
                     message: windows.isEmpty ? "이 계정은 잔여 비율을 제공하지 않습니다." : "계정 사용량",
                     windows: windows, updatedAt: now, plan: limits["planType"] as? String)
    }
    static func claude(_ result: Object, snapshot: Bool = false, now: Date = Date()) -> Usage {
        let limits = result["rate_limits"] as? Object ?? [:]
        let available = snapshot || result["rate_limits_available"] as? Bool == true
        let windows: [Quota] = available ? [("five_hour", "5시간"), ("seven_day", "주간")].compactMap { key, label in
            guard let item = limits[key] as? Object,
                  let used = percentage(item[snapshot ? "used_percentage" : "utilization"]) else { return nil }
            let reset = snapshot ? timestamp(item["resets_at"]) : JSON.date(item["resets_at"] as? String)?.timeIntervalSince1970
            return Quota(key: key, label: label, usedPercent: used, resetsAt: reset)
        } : []
        return Usage(id: "claude", status: windows.isEmpty ? "unavailable" : "ready",
                     message: windows.isEmpty ? "구독 한도를 조회할 수 없습니다. Claude Code를 업데이트해주세요." : "Claude 사용량 조회",
                     windows: windows, updatedAt: snapshot ? JSON.date(result["updatedAt"] as? String) : now)
    }
}

// Matches the Windows tracker: observed percentage-point changes, not token usage.
final class DailyTracker {
    struct Point: Codable { var at: Date; var points: Double }
    struct Window: Codable {
        var label: String; var lastUsed: Double; var resetsAt: Double?
        var lastSample: Date; var observedSince: Date
        var spent = 0.0; var cycleSpent = 0.0; var reportedSpent = 0.0; var reportedCycleSpent = 0.0
        var reportedAt: Date?; var reportedSourceAt: Date?
        var changes: [Point] = []; var reportedRecentSpent = 0.0; var reportedWindowMinutes = 30
    }
    struct History: Codable { var identity: String; var day: Date; var windows: [String: Window] = [:] }
    private var accounts: [String: History]
    private let file: URL
    private var calendar: Calendar
    init(root: URL, calendar: Calendar = .current) {
        file = root.appendingPathComponent("daily-usage-mac.json"); self.calendar = calendar
        accounts = JSON.read(file, as: [String: History].self) ?? [:]
    }
    func observe(_ original: Usage, identity: String?, now: Date = Date(), interval: Int, window: Int) -> Usage {
        guard let identity, original.status == "ready", let sample = original.updatedAt,
              sample <= now.addingTimeInterval(60), calendar.isDate(sample, inSameDayAs: now) else { return original }
        let fingerprint = SHA256.hash(data: Data(identity.utf8)).map { String(format: "%02x", $0) }.joined()
        let day = calendar.startOfDay(for: sample)
        let previous = accounts[original.id]
        var history = previous?.identity == fingerprint && previous?.day == day ? previous! : History(identity: fingerprint, day: day)
        var usage = original
        usage.windows = original.windows.map { quota in
            guard quota.resetsAt == nil || quota.resetsAt! > now.timeIntervalSince1970 else { return quota }
            let used = min(100, max(0, quota.usedPercent))
            var item = history.windows[quota.key] ?? Window(label: quota.label, lastUsed: used,
                resetsAt: quota.resetsAt, lastSample: sample, observedSince: sample)
            if history.windows[quota.key] == nil, previous?.identity == fingerprint,
               let old = previous?.windows[quota.key], old.label == quota.label,
               !replenished(old, used: used, reset: quota.resetsAt, sample: sample) {
                item.changes = old.changes; item.reportedAt = old.reportedAt; item.reportedSourceAt = old.reportedSourceAt
                item.reportedRecentSpent = old.reportedRecentSpent; item.reportedWindowMinutes = old.reportedWindowMinutes
            }
            if sample > item.lastSample {
                if item.label != quota.label || replenished(item, used: used, reset: quota.resetsAt, sample: sample) {
                    // Clear measured and pending consumption immediately, even between measurements.
                    item = Window(label: quota.label, lastUsed: used, resetsAt: quota.resetsAt, lastSample: sample, observedSince: sample)
                } else {
                    let delta = max(0, used - item.lastUsed)
                    item.spent += delta; item.cycleSpent += delta
                    if delta > 0 { item.changes.append(Point(at: sample, points: delta)) }
                    item.lastUsed = max(item.lastUsed, used)
                    item.resetsAt = quota.resetsAt
                }
                item.lastSample = sample
            }
            item.changes.removeAll { $0.at < now.addingTimeInterval(-172800) }
            let fresh = item.reportedSourceAt == nil || item.lastSample > item.reportedSourceAt!
            if item.reportedAt == nil || (fresh && now.timeIntervalSince(item.reportedAt!) >= Double(interval * 60)) {
                item.reportedSpent = item.spent; item.reportedCycleSpent = item.cycleSpent
                item.reportedAt = now; item.reportedSourceAt = item.lastSample
                item.reportedRecentSpent = sum(item, at: now, minutes: window); item.reportedWindowMinutes = window
            } else if item.reportedWindowMinutes != window {
                item.reportedRecentSpent = sum(item, at: item.reportedAt!, minutes: window); item.reportedWindowMinutes = window
            }
            history.windows[quota.key] = item
            var result = quota
            if sample >= item.lastSample, let measured = item.reportedAt, let source = item.reportedSourceAt {
                result.today = DailyUsage(usedPercentagePoints: item.reportedSpent, cyclePercentagePoints: item.reportedCycleSpent,
                    observedSince: item.observedSince, measuredAt: measured, sourceAt: source,
                    nextMeasurementAt: measured.addingTimeInterval(Double(interval * 60)), recentPercentagePoints: item.reportedRecentSpent,
                    windowMinutes: window, isCurrentDay: calendar.isDate(measured, inSameDayAs: sample))
            }
            return result
        }
        let before = try? JSON.encoder.encode(accounts[original.id])
        accounts[original.id] = history
        if before != (try? JSON.encoder.encode(history)) { try? JSON.write(accounts, to: file) }
        return usage
    }
    private func sum(_ value: Window, at: Date, minutes: Int) -> Double {
        value.changes.filter { $0.at > at.addingTimeInterval(Double(-minutes * 60)) && $0.at <= at }.reduce(0) { $0 + $1.points }
    }
    private func replenished(_ item: Window, used: Double, reset: Double?, sample: Date) -> Bool {
        // Full refills need no changed deadline; small corrections/jitter keep the high-water mark.
        if used == 0 && (item.lastUsed > 0 || item.spent > 0 || !item.changes.isEmpty) { return true }
        guard let previous = item.resetsAt, let next = reset else { return false }
        if previous <= sample.timeIntervalSince1970 && next > previous { return true }
        return used < item.lastUsed && abs(next - previous) >= 60
    }
}
