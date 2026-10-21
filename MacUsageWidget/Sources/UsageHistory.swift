import Foundation
import CryptoKit

// Same on-disk and renderer contract as Windows. No account identifiers or conversation data.
final class UsageHistory {
    struct Series: Codable {
        var id: String; var providerId: String; var label: String; var plan: String?
        var startedAt: Double; var lastObservedAt: Double
        var points: [[Double]] = []; var coverage: [[Double]] = []
    }
    private struct Baseline { var seriesId: String; var used: Double; var reset: Double?; var at: Date }
    private let file: URL
    private var startedAt: Date
    private var barriers: [String: Date] = [:]
    private(set) var records: [Series]
    private var baselines: [String: Baseline] = [:]
    private var error: String?
    private var lastPruned = Date.distantPast
    init(root: URL, startedAt: Date = Date()) {
        file = root.appendingPathComponent("usage-history.json"); self.startedAt = startedAt
        records = JSON.read(file, as: [Series].self) ?? []
        if FileManager.default.fileExists(atPath: file.path), JSON.read(file, as: [Series].self) == nil {
            error = "사용 기록을 읽지 못했습니다. 기존 파일을 보존하고 있습니다."
        }
        if records.contains(where: { series in
            series.points.contains { $0.count != 2 || !$0.allSatisfy(\.isFinite) || $0[1] < 0 }
                || series.coverage.contains { $0.count != 2 || !$0.allSatisfy(\.isFinite) || $0[1] < $0[0] }
        }) { error = "사용 기록을 읽지 못했습니다. 기존 파일을 보존하고 있습니다."; records = [] }
    }
    func observe(_ usage: Usage, identity: String?, now: Date = Date()) {
        guard error == nil else { return }
        let prefix = usage.id + ":"
        guard let identity, usage.status == "ready", let sample = usage.updatedAt, sample >= startedAt,
              sample >= (barriers[usage.id] ?? .distantPast), sample <= now, now.timeIntervalSince(sample) <= 600 else {
            baselines = baselines.filter { !$0.key.hasPrefix(prefix) }; barriers[usage.id] = now; return
        }
        let present = Set(usage.windows.map { prefix + $0.key })
        baselines = baselines.filter { !$0.key.hasPrefix(prefix) || present.contains($0.key) }
        var changed = false
        for quota in usage.windows {
            let key = prefix + quota.key
            guard quota.usedPercent.isFinite, quota.usedPercent >= 0, quota.resetsAt == nil || quota.resetsAt! > sample.timeIntervalSince1970 else {
                baselines.removeValue(forKey: key); continue
            }
            let id = seriesId(usage.id, identity: identity, quota: quota)
            var used = min(100, quota.usedPercent)
            let previous = baselines[key]
            if let previous, sample <= previous.at { continue }
            let at = (sample.timeIntervalSince1970 * 1000).rounded(.down) / 1000
            let index: Int
            if let found = records.firstIndex(where: { $0.id == id }) { index = found }
            else {
                records.append(Series(id: id, providerId: usage.id, label: quota.label, plan: usage.plan, startedAt: at, lastObservedAt: at))
                index = records.count - 1
            }
            records[index].lastObservedAt = at
            if let previous, previous.seriesId == id, sample.timeIntervalSince(previous.at) <= 600 {
                let reset = (used == 0 && previous.used > 0) || (previous.reset.map { old in
                    old <= sample.timeIntervalSince1970 || (quota.resetsAt.map { used < previous.used && abs($0 - old) >= 60 } ?? false)
                } ?? false)
                if !reset {
                    let from = (previous.at.timeIntervalSince1970 * 1000).rounded(.down) / 1000
                    let last = records[index].coverage.count - 1
                    if last >= 0 && records[index].coverage[last][1] == from { records[index].coverage[last][1] = at }
                    else { records[index].coverage.append([from, at]) }
                    let delta = max(0, used - previous.used)
                    if delta > 0 { records[index].points.append([at, delta]) }
                    used = max(used, previous.used)
                }
            }
            baselines[key] = Baseline(seriesId: id, used: used, reset: quota.resetsAt, at: sample)
            changed = true
        }
        if now.timeIntervalSince(lastPruned) >= 3600 {
            let cutoff = now.addingTimeInterval(-90 * 86400).timeIntervalSince1970
            for index in records.indices {
                records[index].points.removeAll { $0[0] < cutoff }
                records[index].coverage = records[index].coverage.filter { $0[1] > cutoff }.map { [max($0[0], cutoff), $0[1]] }
            }
            records.removeAll { $0.lastObservedAt < cutoff }; lastPruned = now; changed = true
        }
        if changed {
            do { try JSON.write(records, to: file) }
            catch { self.error = "사용 기록을 저장하지 못했습니다. 저장 폴더를 확인해주세요." }
        }
    }
    func snapshot(providers: [String]) -> Object {
        ["retentionDays": 90, "error": error as Any? ?? NSNull(),
         "series": records.filter { providers.contains($0.providerId) }.map { JSON.object($0) }]
    }
    private func seriesId(_ providerId: String, identity: String, quota: Quota) -> String {
        let key = "\(providerId)\n\(identity)\n\(quota.key)\n\(quota.label)"
        return SHA256.hash(data: Data(key.utf8)).map { String(format: "%02X", $0) }.joined()
    }
    func withTrends(_ usage: Usage, identity: String?, now: Date = Date()) -> Usage {
        let cutoff = now.addingTimeInterval(-25 * 3600).timeIntervalSince1970
        var result = usage
        result.windows = usage.windows.map { quota in
            let record = identity.flatMap { identity in
                error == nil ? records.first { $0.id == seriesId(usage.id, identity: identity, quota: quota) } : nil
            }
            var output = quota
            output.trend = UsageTrend(points: record?.points.filter { $0[0] > cutoff } ?? [],
                                      coverage: record?.coverage.filter { $0[1] > cutoff } ?? [], error: error)
            return output
        }
        return result
    }
    func retainProviders(_ providers: [String], now: Date = Date()) {
        for key in baselines.keys where !providers.contains(String(key.split(separator: ":")[0])) {
            barriers[String(key.split(separator: ":")[0])] = now
        }
        baselines = baselines.filter { providers.contains(String($0.key.split(separator: ":")[0])) }
    }
    func interrupt(now: Date = Date()) { baselines.removeAll(); startedAt = now }
}
