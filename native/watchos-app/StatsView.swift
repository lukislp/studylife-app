import Charts
import SwiftUI

// "Auswertung" content for the Watch - embedded directly into ContentView's scrollable main
// view (not a separate screen behind a button), so it's simply there when you scroll down.
// Same data the phone's course-progress widget already gets (relayed verbatim via
// WatchBridge.PushSnapshot), just a Watch-sized presentation.

private let wAccent = Color(red: 204 / 255, green: 120 / 255, blue: 92 / 255)

struct StatsSection: View {
    let snapshot: WatchSnapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let upcoming = snapshot.upcomingSessions, !upcoming.isEmpty {
                weekPlanSection(upcoming)
            }
            if let daily = snapshot.dailyMinutes, daily.contains(where: { $0.minutes > 0 }) {
                weekChart(daily)
            }
            if let total = snapshot.ectsTotal, total > 0 {
                ectsSection(earned: snapshot.ectsEarned ?? 0, total: total)
            }
            if let courses = snapshot.courses, !courses.isEmpty {
                courseSection(courses)
            }
            if let allTime = snapshot.allTimeHours, allTime > 0 {
                HStack(spacing: 4) {
                    Image(systemName: "sum").foregroundStyle(.secondary)
                    Text("Insgesamt gelernt").font(.caption2).foregroundStyle(.secondary)
                    Spacer(minLength: 4)
                    Text(String(format: "%.1f h", allTime)).font(.caption2.weight(.semibold))
                }
            }
        }
    }

    @ViewBuilder
    private func weekPlanSection(_ upcoming: [WatchUpcomingSession]) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Plan diese Woche").font(.caption2).foregroundStyle(.secondary)
            ForEach(upcoming.prefix(6)) { entry in
                HStack(spacing: 6) {
                    Text(planDayLabel(entry.startsAt))
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(wAccent)
                        .frame(width: 50, alignment: .leading)
                    Text(entry.title).font(.caption2).lineLimit(1)
                    Spacer(minLength: 2)
                    Text(formatWatchMinutes(entry.minutes))
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    @ViewBuilder
    private func weekChart(_ daily: [WatchDailyMinutes]) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("Letzte 7 Tage").font(.caption2).foregroundStyle(.secondary)
                Spacer()
                trendLabel(daily)
            }
            Chart(daily) { entry in
                BarMark(
                    x: .value("Tag", dayLabel(entry.day)),
                    y: .value("Minuten", entry.minutes)
                )
                .foregroundStyle(wAccent)
                .cornerRadius(2)
            }
            .chartYAxis(.hidden)
            .chartXAxis {
                AxisMarks { _ in
                    AxisValueLabel().font(.system(size: 8))
                }
            }
            .frame(height: 70)
        }
    }

    /// "vs. letzte Woche": percentage change against weekMinutesPrevious - only shown once
    /// there actually WAS a previous week with time on record (avoids a meaningless "+∞%"
    /// the first week the app is used).
    @ViewBuilder
    private func trendLabel(_ daily: [WatchDailyMinutes]) -> some View {
        if let previous = snapshot.weekMinutesPrevious, previous > 0 {
            let current = daily.reduce(0) { $0 + $1.minutes }
            let change = Int((Double(current - previous) / Double(previous)) * 100)
            HStack(spacing: 2) {
                Image(systemName: change >= 0 ? "arrow.up.right" : "arrow.down.right")
                Text("\(abs(change))%")
            }
            .font(.caption2)
            .foregroundStyle(change >= 0 ? .green : .secondary)
        }
    }

    @ViewBuilder
    private func ectsSection(earned: Double, total: Double) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("Kursfortschritt").font(.caption2).foregroundStyle(.secondary)
                Spacer()
                Text("\(ectsLabel(earned))/\(ectsLabel(total)) ECTS")
                    .font(.caption2.weight(.semibold))
            }
            ProgressView(value: min(earned, total), total: total)
                .tint(wAccent)
        }
    }

    @ViewBuilder
    private func courseSection(_ courses: [WatchCourseHours]) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Meistgelernt").font(.caption2).foregroundStyle(.secondary)
            ForEach(courses.prefix(4)) { course in
                HStack(spacing: 6) {
                    Circle().fill(courseColor(course.color)).frame(width: 6, height: 6)
                    Text(course.name).font(.caption2).lineLimit(1)
                    Spacer(minLength: 2)
                    Text(String(format: "%.1fh", course.hours))
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private func planDayLabel(_ epoch: Double) -> String {
        let date = Date(timeIntervalSince1970: epoch)
        let formatter = DateFormatter()
        formatter.dateFormat = "EE HH:mm"
        formatter.locale = Locale(identifier: "de_DE")
        return formatter.string(from: date)
    }

    private func dayLabel(_ iso: String) -> String {
        let parser = DateFormatter()
        parser.dateFormat = "yyyy-MM-dd"
        guard let date = parser.date(from: iso) else { return "" }
        let out = DateFormatter()
        out.dateFormat = "EE"
        out.locale = Locale(identifier: "de_DE")
        return out.string(from: date)
    }

    private func ectsLabel(_ value: Double) -> String {
        value.rounded() == value ? String(format: "%.0f", value) : String(format: "%.1f", value)
    }

    /// "#rrggbb" (CourseCatalog's format) → SwiftUI Color - same parse as the iOS widget's
    /// courseColor(_:) in StudyTodayWidget.swift.
    private func courseColor(_ hex: String) -> Color {
        var value: UInt64 = 0
        Scanner(string: hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))).scanHexInt64(&value)
        return Color(
            red: Double((value >> 16) & 0xFF) / 255,
            green: Double((value >> 8) & 0xFF) / 255,
            blue: Double(value & 0xFF) / 255)
    }
}
