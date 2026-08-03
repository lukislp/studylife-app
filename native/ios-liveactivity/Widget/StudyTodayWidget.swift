import SwiftUI
import WidgetKit

// Home screen widget "Lernfortschritt": today's study time, streak, weekly total and the
// next scheduled session. Data source is a JSON snapshot that the app writes to the
// App Group container (Services/HomeWidgetSnapshot.cs) and refreshes via slla_reload_widgets -
// the widget itself deliberately makes no network calls (no session token outside the app).

private let appGroupId = "group.app.studylife.mobile"
private let snapshotFileName = "widget-snapshot.json"

// Same color scheme as the live activity card (StudyLifeWidgets.swift).
private let wAccent = Color(red: 204 / 255, green: 120 / 255, blue: 92 / 255)
private let wBg = Color(red: 14 / 255, green: 14 / 255, blue: 15 / 255)
private let wFg = Color(red: 232 / 255, green: 230 / 255, blue: 224 / 255)
private let wFgMuted = Color(red: 107 / 255, green: 105 / 255, blue: 101 / 255)

struct StudySnapshot: Decodable {
    /// Local date (yyyy-MM-dd) at write time - day/week values are zeroed out on read
    /// if the snapshot is from an earlier day/week.
    var day: String
    var todayMinutes: Int
    var weekMinutes: Int
    var streakDays: Int
    var nextTitle: String?
    var nextStartsAt: Double?
    /// Currently running planned study time (calendar session) - takes the "now" row in the
    /// stats view, and there it takes precedence over the "next session" row. Start+end let the
    /// widget keep extending the studied minutes on its own (baked(_:at:)).
    var currentTitle: String?
    var currentStartsAt: Double?
    var currentEndsAt: Double?
    /// Running focus timer (only when not paused): widgets count down on their own via
    /// Text(timerInterval:), the app doesn't need to be awake for this.
    var timerRunning: Bool?
    var timerIsBreak: Bool?
    var timerEndsAt: Double?
    /// Course progress widget: overall ECTS completion (same metric as the Dashboard) plus
    /// per-course all-time hours for the most-studied active courses - see HomeWidgetSnapshot.cs.
    var ectsEarned: Double?
    var ectsTotal: Double?
    var courses: [CourseHours]?

    var activeTimerEnd: Date? {
        guard timerRunning == true, let ends = timerEndsAt else { return nil }
        let date = Date(timeIntervalSince1970: ends)
        return date > Date() ? date : nil
    }
}

struct CourseHours: Decodable {
    var name: String
    var color: String
    var hours: Double
}

/// "#rrggbb" (as written by CourseCatalog/the settings UI) → SwiftUI Color.
private func courseColor(_ hex: String) -> Color {
    var value: UInt64 = 0
    Scanner(string: hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))).scanHexInt64(&value)
    return Color(
        red: Double((value >> 16) & 0xFF) / 255,
        green: Double((value >> 8) & 0xFF) / 255,
        blue: Double(value & 0xFF) / 255)
}

struct StudyTodayEntry: TimelineEntry {
    let date: Date
    let snapshot: StudySnapshot?
}

private func localDayString(_ date: Date) -> String {
    let formatter = DateFormatter()
    formatter.dateFormat = "yyyy-MM-dd"
    return formatter.string(from: date)
}

private func loadSnapshot(now: Date) -> StudySnapshot? {
    guard let container = FileManager.default
        .containerURL(forSecurityApplicationGroupIdentifier: appGroupId) else { return nil }
    let url = container.appendingPathComponent(snapshotFileName)
    guard let data = try? Data(contentsOf: url),
          var snapshot = try? JSONDecoder().decode(StudySnapshot.self, from: data) else { return nil }

    // Day/week rollover without a new app launch: zero out stale counters instead of
    // incorrectly showing them as "today". The streak stays as-is (per StudyMetrics it
    // lives until midnight of the following day anyway).
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

/// Projects the raw snapshot onto a specific display point in time: the elapsed portion
/// of the running study time flows into today/week (the app only writes COMPLETED
/// time), expired portions (next session, running study time, timer) get hidden.
/// Timeline entries use this for pre-computed minute steps into the future.
private func baked(_ raw: StudySnapshot, at date: Date) -> StudySnapshot {
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

struct StudyTodayProvider: TimelineProvider {
    func placeholder(in context: Context) -> StudyTodayEntry {
        StudyTodayEntry(date: .now, snapshot: StudySnapshot(
            day: localDayString(.now), todayMinutes: 135, weekMinutes: 540,
            streakDays: 4, nextTitle: "Analysis", nextStartsAt: nil))
    }

    func getSnapshot(in context: Context, completion: @escaping (StudyTodayEntry) -> Void) {
        let now = Date()
        completion(StudyTodayEntry(date: now, snapshot: loadSnapshot(now: now).map { baked($0, at: now) }))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<StudyTodayEntry>) -> Void) {
        let now = Date()
        let raw = loadSnapshot(now: now)

        // Collect display points in time: now, per-minute steps during a running
        // study session (the widget keeps extending "Today X min studied" this way - start
        // and end are already known), plus the transition points timer-phase-end/study-time-end,
        // where expired portions disappear. The free tier still can only have the awake app
        // report the SWITCH into a new phase (same limitation as the live activity).
        var dates: Set<Date> = [now]
        if let raw {
            if let endsEpoch = raw.currentEndsAt {
                let end = Date(timeIntervalSince1970: endsEpoch)
                if end > now {
                    // Aligned to minute boundaries, capped (WidgetKit timelines should
                    // stay small; after a 4h continuous session the value freezes until the next
                    // app contact - an accepted edge case).
                    let cap = min(end, now.addingTimeInterval(4 * 3600))
                    var step = now.addingTimeInterval(
                        60 - now.timeIntervalSince1970.truncatingRemainder(dividingBy: 60))
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
            StudyTodayEntry(date: date, snapshot: raw.map { baked($0, at: date) })
        }
        // Next fixed rebuild point is midnight (zero out the daily value); in between,
        // the app keeps the widget current via slla_reload_widgets.
        var calendar = Calendar.current
        calendar.timeZone = .current
        let nextMidnight = calendar.nextDate(
            after: now, matching: DateComponents(hour: 0, minute: 0, second: 5),
            matchingPolicy: .nextTime) ?? now.addingTimeInterval(6 * 3600)
        completion(Timeline(entries: entries, policy: .after(nextMidnight)))
    }
}

struct StudyTodayWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "StudyLifeToday", provider: StudyTodayProvider()) { entry in
            StudyTodayView(entry: entry)
        }
        .configurationDisplayName("Lernfortschritt")
        .description("Heutige Lernzeit, Serie und nächste Session.")
        // accessory* = lock screen widgets (above/below the clock) - same data source,
        // iOS renders them monochrome/vibrant, colors barely matter there.
        .supportedFamilies([.systemSmall, .systemMedium,
                            .accessoryCircular, .accessoryRectangular, .accessoryInline])
    }
}

private struct StudyTodayView: View {
    @Environment(\.widgetFamily) private var family
    let entry: StudyTodayEntry

    private var isAccessory: Bool {
        family == .accessoryCircular || family == .accessoryRectangular || family == .accessoryInline
    }

    var body: some View {
        Group {
            if let snapshot = entry.snapshot {
                content(snapshot)
            } else if isAccessory {
                Text("✦ StudyLife").font(.caption2)
            } else {
                VStack(spacing: 6) {
                    Text("✦").font(.title2).foregroundStyle(wAccent)
                    Text("StudyLife öffnen,\num Daten zu laden")
                        .font(.caption)
                        .multilineTextAlignment(.center)
                        .foregroundStyle(wFgMuted)
                }
            }
        }
        // Lock screen families stay transparent (iOS applies the vibrant look on
        // top itself), only the home screen cards get the app background.
        .widgetBackground(isAccessory ? Color.clear : wBg)
        // Small + lock screen: the whole tap goes straight to the focus page.
        // Medium keeps the standard tap (app launch) and has the explicit focus button.
        .widgetURL(family == .systemMedium ? nil : Self.focusURL)
    }

    /// Deep link to the focus page (AppDelegate.OpenUrl → DeepLinkService).
    private static let focusURL = URL(string: "studylife://shortcut/focus")!

    @ViewBuilder
    private func content(_ snapshot: StudySnapshot) -> some View {
        switch family {
        case .accessoryCircular:
            // AccessoryWidgetBackground = the semi-transparent system frosted-glass look
            // (same look as e.g. the Snapchat lock screen widget).
            ZStack {
                AccessoryWidgetBackground()
                if let ends = snapshot.activeTimerEnd {
                    VStack(spacing: 0) {
                        Text(timerInterval: Date.now...ends, countsDown: true)
                            .font(.system(size: 13, weight: .semibold).monospacedDigit())
                            .multilineTextAlignment(.center)
                            .minimumScaleFactor(0.6)
                        Text(snapshot.timerIsBreak == true ? "Pause" : "Fokus")
                            .font(.system(size: 9))
                    }
                } else {
                    VStack(spacing: 0) {
                        Text(formatMinutes(snapshot.todayMinutes))
                            .font(.system(size: 13, weight: .semibold).monospacedDigit())
                            .minimumScaleFactor(0.7)
                        Text("heute").font(.system(size: 9))
                    }
                }
            }
        case .accessoryRectangular:
            ZStack {
                AccessoryWidgetBackground()
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    if let ends = snapshot.activeTimerEnd {
                        Text(snapshot.timerIsBreak == true ? "✦ Pause ☕" : "✦ Fokus läuft")
                            .font(.caption2.weight(.semibold))
                        (Text("endet in ") + Text(timerInterval: Date.now...ends, countsDown: true))
                            .font(.caption2.monospacedDigit())
                        Text("Heute \(formatMinutes(snapshot.todayMinutes)) gelernt")
                            .font(.caption2)
                    } else if let title = snapshot.currentTitle, let ends = snapshot.currentEndsAt {
                        // Planned study time is running right now (without a focus timer).
                        Text("▶ \(title)").font(.caption2.weight(.semibold)).lineLimit(1)
                        Text("Lernzeit bis \(clockLabel(ends))").font(.caption2)
                        Text("Heute \(formatMinutes(snapshot.todayMinutes)) gelernt")
                            .font(.caption2)
                    } else {
                        Text("✦ StudyLife").font(.caption2.weight(.semibold))
                        Text("Heute \(formatMinutes(snapshot.todayMinutes)) gelernt")
                            .font(.caption2)
                        HStack(spacing: 6) {
                            if snapshot.streakDays > 0 {
                                Label("\(snapshot.streakDays)", systemImage: "flame.fill")
                                    .font(.caption2)
                            }
                            if snapshot.nextTitle != nil {
                                Text("→ \(nextTimeLabel(snapshot))").font(.caption2)
                            }
                        }
                    }
                }
                .padding(.horizontal, 8)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        case .accessoryInline:
            if let ends = snapshot.activeTimerEnd {
                Text("✦ \(snapshot.timerIsBreak == true ? "Pause" : "Fokus") ")
                    + Text(timerInterval: Date.now...ends, countsDown: true)
            } else {
                Text("✦ \(formatMinutes(snapshot.todayMinutes))"
                     + (snapshot.streakDays > 0 ? " · \(snapshot.streakDays)🔥" : ""))
            }
        case .systemMedium:
            HStack(spacing: 16) {
                todayColumn(snapshot)
                Divider().overlay(wFgMuted.opacity(0.4))
                VStack(alignment: .leading, spacing: 8) {
                    // "Fokus starten" button: deliberately only opens the focus page
                    // (same semantics as the Siri shortcut), starting happens there.
                    Link(destination: Self.focusURL) {
                        HStack(spacing: 6) {
                            Image(systemName: "play.circle.fill").foregroundStyle(wAccent)
                            Text("Fokus starten").font(.caption.weight(.semibold)).foregroundStyle(wAccent)
                        }
                        .padding(.vertical, 4).padding(.horizontal, 10)
                        .background(wAccent.opacity(0.15), in: Capsule())
                    }
                    statRow(icon: "calendar", tint: wAccent,
                            value: formatMinutes(snapshot.weekMinutes), label: "diese Woche")
                    statRow(icon: "flame.fill", tint: .orange,
                            value: "\(snapshot.streakDays)",
                            label: snapshot.streakDays == 1 ? "Tag Serie" : "Tage Serie")
                    if let title = snapshot.currentTitle, let ends = snapshot.currentEndsAt {
                        // Planned study time is running RIGHT NOW - takes precedence over "next session".
                        statRow(icon: "play.circle.fill", tint: wAccent,
                                value: "bis \(clockLabel(ends))", label: title)
                    } else if let title = snapshot.nextTitle {
                        statRow(icon: "arrow.right.circle", tint: wFg,
                                value: nextTimeLabel(snapshot), label: title)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(14)
        case .systemSmall:
            // StandBy (iOS 17+, iPhone charging sideways) reuses the systemSmall family -
            // no separate widget kind, just a different rendering when the system tells us
            // it's supplying its own (black, edge-to-edge) background instead of ours.
            if #available(iOS 17.0, *) {
                StandByAwareTodayColumn(snapshot: snapshot)
            } else {
                todayColumn(snapshot)
                    .padding(14)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            }
        default:
            todayColumn(snapshot)
                .padding(14)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        }
    }

    private func todayColumn(_ snapshot: StudySnapshot) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            if let ends = snapshot.activeTimerEnd {
                HStack(spacing: 4) {
                    Text("✦").foregroundStyle(wAccent)
                    Text(snapshot.timerIsBreak == true ? "Pause ☕" : "Fokus läuft")
                        .font(.caption).foregroundStyle(wFgMuted)
                }
                Text(timerInterval: Date.now...ends, countsDown: true)
                    .font(.system(size: 30, weight: .light).monospacedDigit())
                    .foregroundStyle(wAccent)
                    .minimumScaleFactor(0.5)
                Text("heute schon \(formatMinutes(snapshot.todayMinutes))")
                    .font(.caption2).foregroundStyle(wFgMuted)
            } else {
                HStack(spacing: 4) {
                    Text("✦").foregroundStyle(wAccent)
                    Text("Heute").font(.caption).foregroundStyle(wFgMuted)
                }
                Text(formatMinutes(snapshot.todayMinutes))
                    .font(.system(size: 30, weight: .light).monospacedDigit())
                    .foregroundStyle(snapshot.todayMinutes > 0 ? wAccent : wFgMuted)
                    .minimumScaleFactor(0.6)
                Text("gelernt").font(.caption2).foregroundStyle(wFgMuted)
            }
            if family == .systemSmall {
                Spacer(minLength: 2)
                if snapshot.activeTimerEnd == nil,
                   let title = snapshot.currentTitle, let ends = snapshot.currentEndsAt {
                    // Planned study time is running right now - displaces the streak row in the small widget.
                    HStack(spacing: 4) {
                        Image(systemName: "play.circle.fill").font(.caption).foregroundStyle(wAccent)
                        Text("\(title) · bis \(clockLabel(ends))")
                            .font(.caption).foregroundStyle(wFg).lineLimit(1)
                    }
                } else {
                    HStack(spacing: 4) {
                        Image(systemName: "flame.fill")
                            .font(.caption)
                            .foregroundStyle(snapshot.streakDays > 0 ? .orange : wFgMuted)
                        Text(snapshot.streakDays == 1 ? "1 Tag" : "\(snapshot.streakDays) Tage")
                            .font(.caption)
                            .foregroundStyle(wFg)
                    }
                }
            }
        }
    }

    private func statRow(icon: String, tint: Color, value: String, label: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: icon).font(.caption).foregroundStyle(tint).frame(width: 16)
            Text(value).font(.caption.weight(.semibold).monospacedDigit()).foregroundStyle(wFg)
            Text(label).font(.caption).foregroundStyle(wFgMuted).lineLimit(1)
        }
    }

    private func formatMinutes(_ minutes: Int) -> String {
        minutes >= 60 ? String(format: "%d:%02d h", minutes / 60, minutes % 60) : "\(minutes) min"
    }

    private func clockLabel(_ epoch: Double) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm"
        return formatter.string(from: Date(timeIntervalSince1970: epoch))
    }

    private func nextTimeLabel(_ snapshot: StudySnapshot) -> String {
        guard let starts = snapshot.nextStartsAt else { return "" }
        let date = Date(timeIntervalSince1970: starts)
        let formatter = DateFormatter()
        var calendar = Calendar.current
        calendar.timeZone = .current
        formatter.dateFormat = calendar.isDateInToday(date) ? "HH:mm" : "EE HH:mm"
        formatter.locale = Locale(identifier: "de_DE")
        return formatter.string(from: date)
    }
}

private extension View {
    /// iOS 17 requires containerBackground for widgets (otherwise a placeholder error message);
    /// on 16.x the API doesn't exist - a normal background suffices there.
    @ViewBuilder
    func widgetBackground(_ color: Color) -> some View {
        if #available(iOS 17.0, *) {
            containerBackground(for: .widget) { color }
        } else {
            background(color)
        }
    }
}

// MARK: - StandBy

/// iOS 17's StandBy (iPhone charging in landscape) displays existing systemSmall widgets
/// full-screen on a system-supplied black background - showsWidgetContainerBackground is
/// false in that case (true on the normal home screen). Bigger/bolder/no-card layout so the
/// widget reads well from a distance, matching how e.g. the built-in Clock/Weather widgets adapt.
@available(iOS 17.0, *)
private struct StandByAwareTodayColumn: View {
    @Environment(\.showsWidgetContainerBackground) private var showsBackground
    let snapshot: StudySnapshot

    var body: some View {
        if showsBackground {
            homeScreenContent
        } else {
            standByContent
        }
    }

    private var homeScreenContent: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 4) {
                Text("✦").foregroundStyle(wAccent)
                Text("Heute").font(.caption).foregroundStyle(wFgMuted)
            }
            Text(formatMinutesStatic(snapshot.todayMinutes))
                .font(.system(size: 30, weight: .light).monospacedDigit())
                .foregroundStyle(snapshot.todayMinutes > 0 ? wAccent : wFgMuted)
                .minimumScaleFactor(0.6)
            Text("gelernt").font(.caption2).foregroundStyle(wFgMuted)
            Spacer(minLength: 2)
            HStack(spacing: 4) {
                Image(systemName: "flame.fill")
                    .font(.caption)
                    .foregroundStyle(snapshot.streakDays > 0 ? .orange : wFgMuted)
                Text(snapshot.streakDays == 1 ? "1 Tag" : "\(snapshot.streakDays) Tage")
                    .font(.caption).foregroundStyle(wFg)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    private var standByContent: some View {
        VStack(spacing: 6) {
            Text(formatMinutesStatic(snapshot.todayMinutes))
                .font(.system(size: 56, weight: .light).monospacedDigit())
                .foregroundStyle(.white)
                .minimumScaleFactor(0.5)
            HStack(spacing: 8) {
                Text("heute gelernt").font(.callout).foregroundStyle(.white.opacity(0.6))
                if snapshot.streakDays > 0 {
                    Label("\(snapshot.streakDays)", systemImage: "flame.fill")
                        .font(.callout).foregroundStyle(.orange)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func formatMinutesStatic(_ minutes: Int) -> String {
        minutes >= 60 ? String(format: "%d:%02d h", minutes / 60, minutes % 60) : "\(minutes) min"
    }
}

// MARK: - Course progress widget

/// Second home screen widget: overall ECTS completion + the most-studied active courses.
/// Same snapshot file/reload path as StudyTodayWidget (HomeWidgetSnapshot.cs writes both
/// widgets' data in one JSON), just a different kind/view onto it.
struct CourseProgressProvider: TimelineProvider {
    func placeholder(in context: Context) -> StudyTodayEntry {
        StudyTodayEntry(date: .now, snapshot: StudySnapshot(
            day: localDayString(.now), todayMinutes: 0, weekMinutes: 0, streakDays: 0,
            ectsEarned: 42, ectsTotal: 180,
            courses: [CourseHours(name: "Analysis", color: "#cc785c", hours: 12.5)]))
    }

    func getSnapshot(in context: Context, completion: @escaping (StudyTodayEntry) -> Void) {
        let now = Date()
        completion(StudyTodayEntry(date: now, snapshot: loadSnapshot(now: now)))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<StudyTodayEntry>) -> Void) {
        let now = Date()
        // Course hours/ECTS only change when the app is open (a completed session/settings
        // change) - the app's own slla_reload_widgets call after every HomeWidgetSnapshot
        // update keeps this current, no per-minute rebuild needed like the countdown widget.
        var calendar = Calendar.current
        calendar.timeZone = .current
        let nextMidnight = calendar.nextDate(
            after: now, matching: DateComponents(hour: 0, minute: 0, second: 5),
            matchingPolicy: .nextTime) ?? now.addingTimeInterval(6 * 3600)
        completion(Timeline(
            entries: [StudyTodayEntry(date: now, snapshot: loadSnapshot(now: now))],
            policy: .after(nextMidnight)))
    }
}

struct CourseProgressWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "StudyLifeCourses", provider: CourseProgressProvider()) { entry in
            CourseProgressView(entry: entry)
        }
        .configurationDisplayName("Kursfortschritt")
        .description("ECTS-Fortschritt und meistgelernte Kurse.")
        .supportedFamilies([.systemMedium, .systemLarge])
    }
}

private struct CourseProgressView: View {
    @Environment(\.widgetFamily) private var family
    let entry: StudyTodayEntry

    var body: some View {
        Group {
            if let snapshot = entry.snapshot, let total = snapshot.ectsTotal, total > 0 {
                content(snapshot, total: total)
            } else {
                VStack(spacing: 6) {
                    Text("✦").font(.title2).foregroundStyle(wAccent)
                    Text("StudyLife öffnen,\num Daten zu laden")
                        .font(.caption)
                        .multilineTextAlignment(.center)
                        .foregroundStyle(wFgMuted)
                }
            }
        }
        .widgetBackground(wBg)
    }

    @ViewBuilder
    private func content(_ snapshot: StudySnapshot, total: Double) -> some View {
        let earned = snapshot.ectsEarned ?? 0
        let maxCourses = family == .systemLarge ? 4 : 2
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text("Kursfortschritt").font(.caption.weight(.semibold)).foregroundStyle(wFgMuted)
                Spacer()
                Text("\(ectsLabel(earned)) / \(ectsLabel(total)) ECTS")
                    .font(.caption.weight(.semibold).monospacedDigit())
                    .foregroundStyle(wFg)
            }
            ProgressView(value: earned, total: total)
                .progressViewStyle(.linear)
                .tint(wAccent)
            if let courses = snapshot.courses, !courses.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(courses.prefix(maxCourses), id: \.name) { course in
                        courseRow(course, maxHours: courses.map(\.hours).max() ?? 1)
                    }
                }
            } else {
                Spacer(minLength: 0)
                Text("Noch keine aktiven Kurse mit Lernzeit")
                    .font(.caption2).foregroundStyle(wFgMuted)
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    private func courseRow(_ course: CourseHours, maxHours: Double) -> some View {
        HStack(spacing: 8) {
            Circle().fill(courseColor(course.color)).frame(width: 8, height: 8)
            Text(course.name).font(.caption2).foregroundStyle(wFg).lineLimit(1)
            Spacer(minLength: 4)
            GeometryReader { geo in
                RoundedRectangle(cornerRadius: 2)
                    .fill(courseColor(course.color).opacity(0.9))
                    .frame(width: geo.size.width * max(0.06, course.hours / max(maxHours, 1)))
                    .frame(maxHeight: .infinity)
            }
            .frame(width: 60, height: 6)
            Text(hoursLabel(course.hours))
                .font(.caption2.monospacedDigit())
                .foregroundStyle(wFgMuted)
                .frame(width: 38, alignment: .trailing)
        }
    }

    private func ectsLabel(_ value: Double) -> String {
        value.rounded() == value ? String(format: "%.0f", value) : String(format: "%.1f", value)
    }

    private func hoursLabel(_ hours: Double) -> String {
        String(format: "%.1fh", hours)
    }
}
