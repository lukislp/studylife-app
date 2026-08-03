import SwiftUI
import WidgetKit

// Watch complication - reads the local snapshot WatchSnapshot.swift's saveWatchSnapshot()
// wrote (fed by the phone via WCSession, see StudyLifeWatchApp.swift's WatchSessionDelegate).
// Makes no network calls itself, same "extension only reads a local file" rule as the iOS
// widget (StudyTodayWidget.swift) - this file mirrors its accessory-family rendering.

struct StudyLifeWatchComplicationEntry: TimelineEntry {
    let date: Date
    let snapshot: WatchSnapshot?
}

struct StudyLifeWatchComplicationProvider: TimelineProvider {
    func placeholder(in context: Context) -> StudyLifeWatchComplicationEntry {
        StudyLifeWatchComplicationEntry(date: .now, snapshot: WatchSnapshot(
            day: "2026-01-01", todayMinutes: 90, weekMinutes: 400, streakDays: 3))
    }

    func getSnapshot(in context: Context, completion: @escaping (StudyLifeWatchComplicationEntry) -> Void) {
        let now = Date()
        completion(StudyLifeWatchComplicationEntry(date: now, snapshot: loadWatchSnapshot(now: now).map { bakedWatchSnapshot($0, at: now) }))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<StudyLifeWatchComplicationEntry>) -> Void) {
        let now = Date()
        let raw = loadWatchSnapshot(now: now)

        var dates: Set<Date> = [now]
        if let raw {
            if let endsEpoch = raw.currentEndsAt {
                let end = Date(timeIntervalSince1970: endsEpoch)
                if end > now {
                    let cap = min(end, now.addingTimeInterval(4 * 3600))
                    var step = now.addingTimeInterval(60 - now.timeIntervalSince1970.truncatingRemainder(dividingBy: 60))
                    while step < cap {
                        dates.insert(step)
                        step = step.addingTimeInterval(60)
                    }
                    dates.insert(end.addingTimeInterval(1))
                }
            }
            if let endsEpoch = raw.timerEndsAt {
                let end = Date(timeIntervalSince1970: endsEpoch)
                if end > now { dates.insert(end.addingTimeInterval(1)) }
            }
        }
        let entries = dates.sorted().map { date in
            StudyLifeWatchComplicationEntry(date: date, snapshot: raw.map { bakedWatchSnapshot($0, at: date) })
        }
        var calendar = Calendar.current
        calendar.timeZone = .current
        let nextMidnight = calendar.nextDate(after: now, matching: DateComponents(hour: 0, minute: 0, second: 5), matchingPolicy: .nextTime)
            ?? now.addingTimeInterval(6 * 3600)
        completion(Timeline(entries: entries, policy: .after(nextMidnight)))
    }
}

struct StudyLifeWatchComplicationView: View {
    @Environment(\.widgetFamily) private var family
    var entry: StudyLifeWatchComplicationEntry

    var body: some View {
        Group {
            if let snapshot = entry.snapshot {
                content(snapshot)
            } else {
                Text("✦").font(.caption2)
            }
        }
    }

    @ViewBuilder
    private func content(_ snapshot: WatchSnapshot) -> some View {
        switch family {
        case .accessoryCircular, .accessoryCorner:
            ZStack {
                AccessoryWidgetBackground()
                if let ends = snapshot.activeTimerEnd {
                    VStack(spacing: 0) {
                        Text(timerInterval: Date.now...ends, countsDown: true)
                            .font(.system(size: 13, weight: .semibold).monospacedDigit())
                            .minimumScaleFactor(0.6)
                        Text(snapshot.timerIsBreak == true ? "Pause" : "Fokus").font(.system(size: 9))
                    }
                } else {
                    VStack(spacing: 0) {
                        Text(formatWatchMinutes(snapshot.todayMinutes))
                            .font(.system(size: 13, weight: .semibold).monospacedDigit())
                            .minimumScaleFactor(0.7)
                        Text("heute").font(.system(size: 9))
                    }
                }
            }
        case .accessoryRectangular:
            VStack(alignment: .leading, spacing: 1) {
                if let ends = snapshot.activeTimerEnd {
                    Text(snapshot.timerIsBreak == true ? "✦ Pause" : "✦ Fokus läuft").font(.caption2.weight(.semibold))
                    (Text("endet in ") + Text(timerInterval: Date.now...ends, countsDown: true)).font(.caption2.monospacedDigit())
                } else {
                    Text("✦ StudyLife").font(.caption2.weight(.semibold))
                    Text("Heute \(formatWatchMinutes(snapshot.todayMinutes))").font(.caption2)
                }
                if snapshot.streakDays > 0 {
                    Label("\(snapshot.streakDays)", systemImage: "flame.fill").font(.caption2)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        case .accessoryInline:
            if let ends = snapshot.activeTimerEnd {
                Text("✦ \(snapshot.timerIsBreak == true ? "Pause" : "Fokus") ") + Text(timerInterval: Date.now...ends, countsDown: true)
            } else {
                Text("✦ \(formatWatchMinutes(snapshot.todayMinutes))"
                     + (snapshot.streakDays > 0 ? " · \(snapshot.streakDays)🔥" : ""))
            }
        default:
            Text(formatWatchMinutes(snapshot.todayMinutes))
        }
    }
}

struct StudyLifeWatchComplication: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "StudyLifeWatchToday", provider: StudyLifeWatchComplicationProvider()) { entry in
            StudyLifeWatchComplicationView(entry: entry)
        }
        .configurationDisplayName("StudyLife")
        .description("Lernfortschritt auf dem Ziffernblatt.")
        .supportedFamilies([.accessoryCircular, .accessoryRectangular, .accessoryInline, .accessoryCorner])
    }
}

@main
struct StudyLifeWatchWidgetBundle: WidgetBundle {
    var body: some Widget {
        StudyLifeWatchComplication()
    }
}
