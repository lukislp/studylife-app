import Foundation
import HealthKit

// C ABI exports for the .NET side (DllImport "__Internal" in Services/HealthBridge.cs).
// Write: Mindful Minutes (focus rounds). Read: Heart Rate Variability (SDNN) for the HRV
// readiness tile, Sleep Analysis for the sleep-consistency tile, Step Count for the Focus
// Timer's movement-break nudge, and VO2max (Cardio Fitness) for the Stats page trend chart
// (all INativeHealthData/studylife repo consumers) - all requested together in one
// authorization prompt at app startup, see slla_health_request_authorization below.

private let healthStore = HKHealthStore()

@_cdecl("slla_health_is_available")
public func slla_health_is_available() -> Int32 {
    HKHealthStore.isHealthDataAvailable() ? 1 : 0
}

@_cdecl("slla_health_request_authorization")
public func slla_health_request_authorization(_ handler: (@convention(c) (Int32) -> Void)?) {
    guard HKHealthStore.isHealthDataAvailable(),
          let mindfulType = HKObjectType.categoryType(forIdentifier: .mindfulSession),
          let hrvType = HKObjectType.quantityType(forIdentifier: .heartRateVariabilitySDNN),
          let sleepType = HKObjectType.categoryType(forIdentifier: .sleepAnalysis),
          let stepType = HKObjectType.quantityType(forIdentifier: .stepCount),
          let vo2MaxType = HKObjectType.quantityType(forIdentifier: .vo2Max) else {
        handler?(0)
        return
    }
    healthStore.requestAuthorization(toShare: [mindfulType], read: [hrvType, sleepType, stepType, vo2MaxType]) { success, _ in
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

/// Sleep onset time for the last `nights` nights, oldest first, most recent last, as minutes
/// after 6pm wrapping at 24h - matches INativeHealthData.GetRecentSleepOnsetMinutesAsync's doc
/// comment (studylife repo). Sleep Analysis is a HealthKit *category* type (unlike HRV's
/// quantity type), so HKStatisticsCollectionQuery does not apply here - a plain HKSampleQuery
/// over raw samples is clustered into nightly sessions by gap instead.
@_cdecl("slla_health_get_recent_sleep_onsets")
public func slla_health_get_recent_sleep_onsets(_ nights: Int32, _ handler: (@convention(c) (UnsafePointer<Double>?, Int32) -> Void)?) {
    guard let sleepType = HKObjectType.categoryType(forIdentifier: .sleepAnalysis) else {
        handler?(nil, 0)
        return
    }

    let calendar = Calendar.current
    let now = Date()
    let startDate = calendar.date(byAdding: .day, value: -Int(nights), to: now)!
    let predicate = HKQuery.predicateForSamples(withStart: startDate, end: now, options: .strictStartDate)
    let sort = NSSortDescriptor(key: HKSampleSortIdentifierStartDate, ascending: true)

    let query = HKSampleQuery(sampleType: sleepType, predicate: predicate, limit: HKObjectQueryNoLimit, sortDescriptors: [sort]) { _, samples, _ in
        guard let categorySamples = samples as? [HKCategorySample], !categorySamples.isEmpty else {
            handler?(nil, 0)
            return
        }

        // Keep only genuine "asleep" states, not "in bed" - "in bed" can start well before
        // actual sleep onset (reading, scrolling) and would skew the onset-time signal.
        // watchOS/iOS 16+ report distinct asleepCore/Deep/REM/unspecified sub-states, all of
        // which count as "asleep" here.
        let asleepValues: Set<Int> = [
            HKCategoryValueSleepAnalysis.asleepUnspecified.rawValue,
            HKCategoryValueSleepAnalysis.asleepCore.rawValue,
            HKCategoryValueSleepAnalysis.asleepDeep.rawValue,
            HKCategoryValueSleepAnalysis.asleepREM.rawValue,
        ]
        let asleep = categorySamples.filter { asleepValues.contains($0.value) }.sorted { $0.startDate < $1.startDate }
        guard !asleep.isEmpty else {
            handler?(nil, 0)
            return
        }

        // Cluster into nights by gap: consecutive asleep samples within ~1h of each other
        // belong to the same night's sleep session (a real overnight sleep has many short
        // stage-change samples with small gaps) - a gap bigger than that marks the boundary
        // between one night's sleep and the next (or a daytime nap).
        var nightOnsets: [Date] = [asleep[0].startDate]
        var previousEnd = asleep[0].endDate
        for sample in asleep.dropFirst() {
            if sample.startDate.timeIntervalSince(previousEnd) > 3600 {
                nightOnsets.append(sample.startDate)
            }
            previousEnd = max(previousEnd, sample.endDate)
        }

        // Minutes after 6pm, wrapping at 24h (see INativeHealthData doc comment) - keeps
        // normal bedtimes (21:00-03:00) in a contiguous range instead of wrapping at midnight.
        let values: [Double] = nightOnsets.map { onset in
            let comps = calendar.dateComponents([.hour, .minute], from: onset)
            let hour = comps.hour ?? 0
            let minute = comps.minute ?? 0
            return Double(((hour - 18 + 24) % 24) * 60 + minute)
        }

        values.withUnsafeBufferPointer { buffer in
            handler?(buffer.baseAddress, Int32(buffer.count))
        }
    }

    healthStore.execute(query)
}

/// Cumulative step count over the last `minutesAgo` minutes up to now - matches
/// INativeHealthData.GetStepsSinceAsync's doc comment (studylife repo), used by the Focus
/// Timer's movement-break nudge. Step Count is dense/daily-summable, so a single
/// HKStatisticsQuery with .cumulativeSum is enough (unlike HRV/sleep, no collection or sample
/// query needed). handler's first Int32 is a success flag (0 = denied/undetermined/error, in
/// which case the second value is meaningless) - lets the C# side distinguish "unavailable"
/// from a genuine zero steps, matching GetStepsSinceAsync's nullable-int contract.
@_cdecl("slla_health_get_steps_since")
public func slla_health_get_steps_since(_ minutesAgo: Int32, _ handler: (@convention(c) (Int32, Int32) -> Void)?) {
    guard let stepType = HKObjectType.quantityType(forIdentifier: .stepCount) else {
        handler?(0, 0)
        return
    }

    let now = Date()
    let startDate = now.addingTimeInterval(-Double(minutesAgo) * 60)
    let predicate = HKQuery.predicateForSamples(withStart: startDate, end: now, options: .strictStartDate)

    let query = HKStatisticsQuery(quantityType: stepType, quantitySamplePredicate: predicate, options: .cumulativeSum) { _, result, error in
        guard error == nil, let sum = result?.sumQuantity() else {
            handler?(0, 0)
            return
        }
        let steps = Int32(sum.doubleValue(for: HKUnit.count()))
        handler?(1, steps)
    }

    healthStore.execute(query)
}

/// Cardio Fitness (VO2max, ml/(kg*min)) history for the last `days` days, oldest first -
/// matches INativeHealthData.GetCardioFitnessHistoryAsync's doc comment (studylife repo).
/// watchOS computes these roughly monthly from outdoor walk/run workouts, so readings are
/// sparse (unlike HRV's daily cadence) - a plain HKSampleQuery sorted by date is the right
/// tool, not a collection query. Two parallel arrays are passed back (Unix-seconds dates,
/// values) since - unlike the single-value HRV/sleep-onset bridges - each reading needs both
/// its date AND its value on the C# side (INativeHealthData's tuple return type).
@_cdecl("slla_health_get_cardio_fitness_history")
public func slla_health_get_cardio_fitness_history(_ days: Int32, _ handler: (@convention(c) (UnsafePointer<Double>?, UnsafePointer<Double>?, Int32) -> Void)?) {
    guard let vo2MaxType = HKObjectType.quantityType(forIdentifier: .vo2Max) else {
        handler?(nil, nil, 0)
        return
    }

    let calendar = Calendar.current
    let now = Date()
    let startDate = calendar.date(byAdding: .day, value: -Int(days), to: now)!
    let predicate = HKQuery.predicateForSamples(withStart: startDate, end: now, options: .strictStartDate)
    let sort = NSSortDescriptor(key: HKSampleSortIdentifierStartDate, ascending: true)

    let query = HKSampleQuery(sampleType: vo2MaxType, predicate: predicate, limit: HKObjectQueryNoLimit, sortDescriptors: [sort]) { _, samples, _ in
        guard let quantitySamples = samples as? [HKQuantitySample], !quantitySamples.isEmpty else {
            handler?(nil, nil, 0)
            return
        }

        let unit = HKUnit(from: "ml/(kg*min)")
        let dates = quantitySamples.map { $0.startDate.timeIntervalSince1970 }
        let values = quantitySamples.map { $0.quantity.doubleValue(for: unit) }

        // Both pointers are only valid for the duration of this closure - see the matching
        // comment in HealthBridge.cs, which copies both arrays out synchronously.
        dates.withUnsafeBufferPointer { dateBuffer in
            values.withUnsafeBufferPointer { valueBuffer in
                handler?(dateBuffer.baseAddress, valueBuffer.baseAddress, Int32(quantitySamples.count))
            }
        }
    }

    healthStore.execute(query)
}
