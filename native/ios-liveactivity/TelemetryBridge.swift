import Foundation
import MetricKit
import CryptoKit

// C ABI exports for the .NET side (DllImport "__Internal" in Services/TelemetryBridge.cs).
// Subscribes to MXMetricManager at launch and converts MetricKit's crash/hang diagnostics and
// per-day launch/resource metrics into the wire-shaped telemetry events (see
// StudyLife.Shared.TelemetryEventDto / the contract in the studylife repo's ARCHITECTURE.md) -
// appended to a JSON file in Application Support so they survive until TelemetryService's next
// flush, which may be days later (MetricKit itself only delivers about once a day for metrics,
// and at the NEXT launch after a crash/hang - never during the crash itself).

private let telemetryQueue = DispatchQueue(label: "app.studylife.mobile.telemetry-queue")

private let queueFileURL: URL = {
    let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir.appendingPathComponent("slla-telemetry-metrickit-queue.json")
}()

private final class TelemetrySubscriber: NSObject, MXMetricManagerSubscriber {
    func didReceive(_ payloads: [MXDiagnosticPayload]) {
        var events: [[String: Any]] = []
        for payload in payloads {
            let at = Int64(payload.timeStampEnd.timeIntervalSince1970 * 1000)
            for crash in payload.crashDiagnostics ?? [] {
                events.append(makeCrashEvent(crash, at: at))
            }
            for hang in payload.hangDiagnostics ?? [] {
                events.append(makeHangEvent(hang, at: at))
            }
        }
        if !events.isEmpty { appendEvents(events) }
    }

    func didReceive(_ payloads: [MXMetricPayload]) {
        var events: [[String: Any]] = []
        for payload in payloads {
            let at = Int64(payload.timeStampEnd.timeIntervalSince1970 * 1000)
            if let launch = makeLaunchEvent(payload, at: at) {
                events.append(launch)
            }
            if let resource = makeResourceEvent(payload, at: at) {
                events.append(resource)
            }
        }
        if !events.isEmpty { appendEvents(events) }
    }
}

// Kept alive for the process lifetime - MXMetricManager.add(_:) does not retain the subscriber.
private let subscriber = TelemetrySubscriber()

@_cdecl("slla_telemetry_start")
public func slla_telemetry_start() {
    MXMetricManager.shared.add(subscriber)
}

/// Hands the queued MetricKit-derived events over to the C# side as a UTF-8 JSON array and
/// clears the file - same "copy synchronously inside the callback" rule as HealthBridge.swift's
/// query handlers (the pointer is only valid for the duration of this call).
@_cdecl("slla_telemetry_drain")
public func slla_telemetry_drain(_ handler: (@convention(c) (UnsafePointer<UInt8>?, Int32) -> Void)?) {
    telemetryQueue.sync {
        guard let data = try? Data(contentsOf: queueFileURL), !data.isEmpty else {
            handler?(nil, 0)
            return
        }
        try? FileManager.default.removeItem(at: queueFileURL)
        data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let ptr = raw.bindMemory(to: UInt8.self).baseAddress
            handler?(ptr, Int32(data.count))
        }
    }
}

private func appendEvents(_ newEvents: [[String: Any]]) {
    telemetryQueue.sync {
        var existing: [[String: Any]] = []
        if let data = try? Data(contentsOf: queueFileURL),
           let decoded = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] {
            existing = decoded
        }
        existing.append(contentsOf: newEvents)
        if let out = try? JSONSerialization.data(withJSONObject: existing) {
            try? out.write(to: queueFileURL, options: .atomic)
        }
    }
}

// MARK: - Diagnostic -> event mapping (error: native_crash / native_hang)

private func makeCrashEvent(_ crash: MXCrashDiagnostic, at: Int64) -> [String: Any] {
    let errorType = shortErrorType(for: crash)
    let stack = flattenedStack(crash.callStackTree, maxBytes: 4096)
    let stackHash = sha256Hex(stack)
    return [
        "type": "error",
        "at": at,
        "kind": "native_crash",
        "errorType": errorType,
        "stack": stack,
        "stackHash": stackHash,
        "fatal": true,
    ]
}

private func makeHangEvent(_ hang: MXHangDiagnostic, at: Int64) -> [String: Any] {
    return [
        "type": "error",
        "at": at,
        "kind": "native_hang",
        "durationMs": hang.hangDuration.converted(to: .milliseconds).value,
        "fatal": false,
    ]
}

/// Short (<=120 char, wire-contract limit) description of a crash - preferring the OS's own
/// termination reason (usually the most legible - e.g. contains the signal name and a short
/// description already), falling back to the raw signal/exception codes.
private func shortErrorType(for crash: MXCrashDiagnostic) -> String {
    if let reason = crash.terminationReason, !reason.isEmpty { return String(reason.prefix(120)) }
    if let signal = crash.signal { return "signal:\(signal)" }
    if let exceptionType = crash.exceptionType { return "exception:\(exceptionType)" }
    return "unknown"
}

/// Flattens MXCallStackTree's JSON representation to plain per-frame lines (already
/// symbolicated where MetricKit provides it - see the README's "Telemetry (native)" section for
/// how to symbolicate the rest with `atos` + the matching dSYM), capped at maxBytes. No message
/// text anywhere here - only binary name/offset/address, matching the "no message text in
/// errors" privacy rule.
private func flattenedStack(_ tree: MXCallStackTree, maxBytes: Int) -> String {
    let data = tree.jsonRepresentation()
    guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let threads = root["callStacks"] as? [[String: Any]] else {
        return ""
    }

    var lines: [String] = []
    for thread in threads {
        guard let rootFrames = thread["callStackRootFrames"] as? [[String: Any]] else { continue }
        for frame in rootFrames { collectFrames(frame, into: &lines) }
    }

    var result = ""
    for line in lines {
        let candidate = result.isEmpty ? line : result + "\n" + line
        if candidate.utf8.count > maxBytes { break }
        result = candidate
    }
    return result
}

private func collectFrames(_ frame: [String: Any], into lines: inout [String]) {
    let binary = frame["binaryName"] as? String ?? "?"
    let address = (frame["address"] as? NSNumber)?.uint64Value ?? 0
    let offset = (frame["offsetIntoBinaryTextSegment"] as? NSNumber)?.uint64Value ?? 0
    lines.append("\(binary) + \(offset) (0x\(String(address, radix: 16)))")
    if let subFrames = frame["subFrames"] as? [[String: Any]] {
        for sub in subFrames { collectFrames(sub, into: &lines) }
    }
}

private func sha256Hex(_ text: String) -> String {
    let digest = SHA256.hash(data: Data(text.utf8))
    return digest.map { String(format: "%02x", $0) }.joined()
}

// MARK: - Metric -> event mapping (app_launch / app_resource)

private func makeLaunchEvent(_ payload: MXMetricPayload, at: Int64) -> [String: Any]? {
    guard let launch = payload.applicationLaunchMetrics else { return nil }
    var fields: [String: Any] = ["type": "app_launch", "at": at]
    if let coldMs = averageMs(launch.histogrammedTimeToFirstDraw) { fields["coldMs"] = coldMs }
    if let warmMs = averageMs(launch.histogrammedApplicationResumeTime) { fields["warmMs"] = warmMs }
    return fields.count > 2 ? fields : nil
}

private func makeResourceEvent(_ payload: MXMetricPayload, at: Int64) -> [String: Any]? {
    var fields: [String: Any] = ["type": "app_resource", "at": at]
    if let peak = payload.memoryMetrics?.peakMemoryUsage {
        fields["peakMemoryMb"] = peak.converted(to: .megabytes).value
    }
    if let cpu = payload.cpuMetrics?.cumulativeCPUTime {
        fields["cpuSeconds"] = cpu.converted(to: .seconds).value
    }
    if let network = payload.networkTransferMetrics {
        let cellular = network.cumulativeCellularDownload.converted(to: .bytes).value
            + network.cumulativeCellularUpload.converted(to: .bytes).value
        let wifi = network.cumulativeWifiDownload.converted(to: .bytes).value
            + network.cumulativeWifiUpload.converted(to: .bytes).value
        fields["cellularBytes"] = cellular
        fields["wifiBytes"] = wifi
    }
    return fields.count > 2 ? fields : nil
}

/// Weighted-mean estimate from an MXHistogram's buckets (MetricKit never hands out raw samples,
/// only bucketed counts) - each bucket contributes its midpoint value, weighted by its count.
private func averageMs(_ histogram: MXHistogram<UnitDuration>?) -> Double? {
    guard let histogram = histogram else { return nil }
    var totalCount = 0
    var weightedSum = 0.0
    let enumerator = histogram.bucketEnumerator
    while let bucket = enumerator.nextObject() as? MXHistogramBucket<UnitDuration> {
        let mid = (bucket.bucketStart.converted(to: .milliseconds).value + bucket.bucketEnd.converted(to: .milliseconds).value) / 2
        weightedSum += mid * Double(bucket.bucketCount)
        totalCount += bucket.bucketCount
    }
    guard totalCount > 0 else { return nil }
    return weightedSum / Double(totalCount)
}
