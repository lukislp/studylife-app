import Foundation

// Shared between StudyLifeWatchShell (app, receives via WCSession and writes this file) and
// StudyLifeWatchWidgetsShell (complication, only reads it) - same "compute once, extension
// just reads a local file" split as the iOS widget (StudyTodayWidget.swift), just with the
// write side fed by WatchConnectivity instead of a same-device App Group write.

let watchAppGroupId = "group.app.studylife.mobile"
let watchSnapshotFileName = "watch-snapshot.json"

struct WatchTimerModeInfo: Codable, Identifiable {
    var id: Int
    var name: String
    var emoji: String
}

struct WatchRecentSession: Codable, Identifiable {
    var title: String
    var startsAt: Double
    var minutes: Int
    var id: Double { startsAt }
}

/// Course progress (StatsView): same fields as the iOS course-progress widget's CourseHours,
/// relayed to the watch as part of the very same JSON payload (WatchBridge.PushSnapshot uses
/// the identical bytes HomeWidgetSnapshot writes for the phone widgets).
struct WatchCourseHours: Codable, Identifiable {
    var name: String
    var color: String
    var hours: Double
    var id: String { name }
}

/// Weekly bar chart (StatsView): last 7 days' totals, oldest first.
struct WatchDailyMinutes: Codable, Identifiable {
    var day: String
    var minutes: Int
    var id: String { day }
}

/// "Plan for the rest of the week" list (StatsView) - every remaining planned session
/// through the end of the current week, not just the single nearest "next" one.
struct WatchUpcomingSession: Codable, Identifiable {
    var title: String
    var startsAt: Double
    var minutes: Int
    var id: Double { startsAt }
}

struct WatchSnapshot: Codable {
    /// Local date (yyyy-MM-dd) at write time - day/week values are zeroed out on read
    /// if the snapshot is from an earlier day/week (same rollover rule as the iOS widget).
    var day: String
    var todayMinutes: Int
    var weekMinutes: Int
    var weeklyGoalMinutes: Int?
    var streakDays: Int
    var nextTitle: String?
    var nextStartsAt: Double?
    var currentTitle: String?
    var currentStartsAt: Double?
    var currentEndsAt: Double?
    var timerRunning: Bool?
    var timerIsBreak: Bool?
    var timerEndsAt: Double?
    var modes: [WatchTimerModeInfo]?
    var recentSessions: [WatchRecentSession]?
    /// Stats view: overall ECTS completion + most-studied active courses (same metric as the
    /// Dashboard/iOS course-progress widget) and the last 7 days' totals for the bar chart.
    var ectsEarned: Double?
    var ectsTotal: Double?
    var courses: [WatchCourseHours]?
    var dailyMinutes: [WatchDailyMinutes]?
    /// Last week's total (for the "vs. last week" trend) and all-time hours across every
    /// studied session, not just the top-4 active courses in `courses`.
    var weekMinutesPrevious: Int?
    var allTimeHours: Double?
    var upcomingSessions: [WatchUpcomingSession]?
    /// Unix seconds, only present on the ONE snapshot pushed right after a focus round
    /// completed (HomeWidgetSnapshot's justCompleted flag) - unlike diffing timerRunning
    /// (which can't tell "completed" apart from "paused"/"stopped"), this is unambiguous.
    var sessionCompletedAt: Double?
    /// Unix seconds, only present on the ONE snapshot pushed every 45 elapsed minutes of a
    /// long focus round (HomeWidgetSnapshot's standNudge flag) - same one-shot-timestamp
    /// pattern as sessionCompletedAt.
    var standNudgeAt: Double?

    var activeTimerEnd: Date? {
        guard timerRunning == true, let ends = timerEndsAt else { return nil }
        let date = Date(timeIntervalSince1970: ends)
        return date > Date() ? date : nil
    }
}

func watchSnapshotContainerURL() -> URL? {
    FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: watchAppGroupId)
}

/// Called from the watch app's WCSessionDelegate when a new context arrives from the phone -
/// writes the raw JSON as-is (same bytes the phone sent, no re-encoding round trip).
func saveWatchSnapshot(_ data: Data) {
    guard let container = watchSnapshotContainerURL() else { return }
    try? data.write(to: container.appendingPathComponent(watchSnapshotFileName))
}

func loadWatchSnapshot(now: Date) -> WatchSnapshot? {
    guard let container = watchSnapshotContainerURL() else { return nil }
    let url = container.appendingPathComponent(watchSnapshotFileName)
    guard let data = try? Data(contentsOf: url),
          var snapshot = try? JSONDecoder().decode(WatchSnapshot.self, from: data) else { return nil }

    let formatter = DateFormatter()
    formatter.dateFormat = "yyyy-MM-dd"
    if let snapshotDay = formatter.date(from: snapshot.day) {
        var calendar = Calendar(identifier: .iso8601)
        calendar.firstWeekday = 2
        if !calendar.isDate(snapshotDay, inSameDayAs: now) {
            snapshot.todayMinutes = 0
            if !calendar.isDate(snapshotDay, equalTo: now, toGranularity: .weekOfYear) {
                snapshot.weekMinutes = 0
            }
        }
    }
    return snapshot
}

/// Projects the raw snapshot onto "now" - same semantics as StudyTodayWidget.swift's
/// baked(_:at:): the elapsed portion of a running study session flows into today/week,
/// expired next-session/timer fields get hidden.
func bakedWatchSnapshot(_ raw: WatchSnapshot, at date: Date) -> WatchSnapshot {
    var snapshot = raw
    if let startsEpoch = snapshot.currentStartsAt, let endsEpoch = snapshot.currentEndsAt {
        let start = Date(timeIntervalSince1970: startsEpoch)
        let end = Date(timeIntervalSince1970: endsEpoch)
        let accrued = Int(max(0, min(date, end).timeIntervalSince(start)) / 60)
        snapshot.todayMinutes += accrued
        snapshot.weekMinutes += accrued
    }
    if let starts = snapshot.nextStartsAt, Date(timeIntervalSince1970: starts) < date {
        snapshot.nextTitle = nil
        snapshot.nextStartsAt = nil
    }
    if let ends = snapshot.currentEndsAt, Date(timeIntervalSince1970: ends) <= date {
        snapshot.currentTitle = nil
        snapshot.currentStartsAt = nil
        snapshot.currentEndsAt = nil
    }
    if let ends = snapshot.timerEndsAt, Date(timeIntervalSince1970: ends) <= date {
        snapshot.timerRunning = false
    }
    return snapshot
}

func formatWatchMinutes(_ minutes: Int) -> String {
    minutes >= 60 ? String(format: "%d:%02d h", minutes / 60, minutes % 60) : "\(minutes) min"
}
