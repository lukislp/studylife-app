import Foundation
import HealthKit

// C ABI exports for the .NET side (DllImport "__Internal" in Services/HealthBridge.cs).
// Write-only (Mindful Minutes) - no HealthKit read access requested anywhere.

private let healthStore = HKHealthStore()

@_cdecl("slla_health_is_available")
public func slla_health_is_available() -> Int32 {
    HKHealthStore.isHealthDataAvailable() ? 1 : 0
}

@_cdecl("slla_health_request_authorization")
public func slla_health_request_authorization(_ handler: (@convention(c) (Int32) -> Void)?) {
    guard HKHealthStore.isHealthDataAvailable(),
          let mindfulType = HKObjectType.categoryType(forIdentifier: .mindfulSession) else {
        handler?(0)
        return
    }
    healthStore.requestAuthorization(toShare: [mindfulType], read: []) { success, _ in
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
