import Foundation
import HealthKit

// C ABI exports for the .NET side (DllImport "__Internal" in Services/HealthBridge.cs).
// Write: Mindful Minutes (focus rounds). Read: Heart Rate Variability (SDNN), for the
// Dashboard's HRV readiness-score tile (INativeHealthData/BuildReadinessScore, studylife
// repo) - both requested together in one authorization prompt at app startup, see
// slla_health_request_authorization below.

private let healthStore = HKHealthStore()

@_cdecl("slla_health_is_available")
public func slla_health_is_available() -> Int32 {
    HKHealthStore.isHealthDataAvailable() ? 1 : 0
}

@_cdecl("slla_health_request_authorization")
public func slla_health_request_authorization(_ handler: (@convention(c) (Int32) -> Void)?) {
    guard HKHealthStore.isHealthDataAvailable(),
          let mindfulType = HKObjectType.categoryType(forIdentifier: .mindfulSession),
          let hrvType = HKObjectType.quantityType(forIdentifier: .heartRateVariabilitySDNN) else {
        handler?(0)
        return
    }
    healthStore.requestAuthorization(toShare: [mindfulType], read: [hrvType]) { success, _ in
        handler?(success ? 1 : 0)
    }
}

/// Logs a completed focus round as a Mindful Session - startEpoch/endEpoch are Unix seconds.
/// Best-effort: a denied/undetermined authorization just means the save silently no-ops,
/// same "widget update is best effort" philosophy as HomeWidgetSnapshot.cs.
@_cdecl("slla_health_log_mindful_session")
public func slla_health_log_mindful_session(_ startEpoch: Double, _ endEpoch: Double) {
    guard let mindfulType = HKObjectType.categoryType(forIdentifier: .mindfulSession) else { return }
    let start = Date(timeIntervalSince1970: startEpoch)
    let end = Date(timeIntervalSince1970: endEpoch)
    let sample = HKCategorySample(type: mindfulType, value: 0, start: start, end: end)
    healthStore.save(sample) { _, _ in }
}

/// Daily HRV (SDNN, ms) for the last `days` days, oldest first, most recent last - matches
/// INativeHealthData.GetRecentHrvAsync's doc comment (studylife repo): days without a sample
/// are simply absent from the array, not zero-filled, so the C# side's baseline/today split
/// (last element = today) stays correct regardless of gaps. Best-effort: a denied/undetermined
/// authorization or a query error both just report zero results, same as the write path never
/// throwing - the Dashboard tile's own INativeHealthData.IsAvailable/min-sample-count gating
/// (studylife repo) already handles "nothing to show" gracefully.
@_cdecl("slla_health_get_recent_hrv")
public func slla_health_get_recent_hrv(_ days: Int32, _ handler: (@convention(c) (UnsafePointer<Double>?, Int32) -> Void)?) {
    guard let hrvType = HKObjectType.quantityType(forIdentifier: .heartRateVariabilitySDNN) else {
        handler?(nil, 0)
        return
    }

    let calendar = Calendar.current
    let now = Date()
    let startDate = calendar.date(byAdding: .day, value: -(Int(days) - 1), to: calendar.startOfDay(for: now))!
    let anchorDate = calendar.startOfDay(for: startDate)
    let predicate = HKQuery.predicateForSamples(withStart: startDate, end: now, options: .strictStartDate)

    let query = HKStatisticsCollectionQuery(
        quantityType: hrvType,
        quantitySamplePredicate: predicate,
        options: .discreteAverage,
        anchorDate: anchorDate,
        intervalComponents: DateComponents(day: 1)
    )

    query.initialResultsHandler = { _, results, _ in
        guard let results = results else {
            handler?(nil, 0)
            return
        }
        var values: [Double] = []
        results.enumerateStatistics(from: startDate, to: now) { stats, _ in
            if let avg = stats.averageQuantity() {
                values.append(avg.doubleValue(for: HKUnit.secondUnit(with: .milli)))
            }
        }
        // The pointer is only valid for the duration of this closure - the C# callback
        // (UnmanagedCallersOnly, see HealthBridge.cs) must copy it into a managed array
        // synchronously during the call, not retain the raw pointer afterward.
        values.withUnsafeBufferPointer { buffer in
            handler?(buffer.baseAddress, Int32(buffer.count))
        }
    }

    healthStore.execute(query)
}
