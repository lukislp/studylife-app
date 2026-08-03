import AppIntents
import Foundation

// Compiled into BOTH targets: the widget extension needs the type for
// Button(intent:), but perform() actually runs in the APP process (LiveActivityIntent -
// iOS launches the app in the background for this if necessary). From there it goes through
// the C function pointer registered via slla_set_toggle_handler into the .NET TimerService.
// So the app can find the intent at runtime, build-ios-ipa.sh generates the
// Metadata.appintents bundle via appintentsmetadataprocessor (otherwise Xcode-exclusive).
public enum StudyLifeTimerIntentHub {
    // Only set in the app process; in the widget process the handler stays nil (no-op).
    // Same buffering pattern as openFocusHandler/pendingOpenFocus: on a cold start
    // (app fully terminated, iOS only briefly launches it in the background for the intent)
    // perform() can fire BEFORE .NET/Blazor has finished booting and registered the handler -
    // without buffering, the pause/resume button press would then have no effect (observed live:
    // the card still updates locally via the countdown, but the button did nothing).
    static var toggleHandler: (@convention(c) () -> Void)?
    static var pendingToggle = false
    public static func invokeToggle() {
        if let handler = toggleHandler { handler() } else { pendingToggle = true }
    }

    // No buffering needed (unlike toggleHandler/openFocusHandler) - see the
    // slla_set_push_token_handler comment in LiveActivityBridge.swift.
    static var pushTokenHandler: (@convention(c) (UnsafePointer<CChar>?) -> Void)?
    public static func invokePushToken(_ token: String) {
        token.withCString { cstr in pushTokenHandler?(cstr) }
    }

    // "Open Focus" (Siri/Shortcuts/widget button): can fire on a cold start,
    // BEFORE .NET has registered the handler - buffer it then and replay it once
    // the handler is registered (slla_set_open_focus_handler in LiveActivityBridge.swift).
    static var openFocusHandler: (@convention(c) () -> Void)?
    static var pendingOpenFocus = false
    public static func invokeOpenFocus() {
        if let handler = openFocusHandler { handler() } else { pendingOpenFocus = true }
    }
}

/// Siri & Shortcuts: "Start focus timer" DELIBERATELY only opens the focus page
/// (user decision: starting happens manually there, no blind-starting a mode).
/// openAppWhenRun brings the app to the foreground, perform() then runs in the
/// app process and navigates via the hub callback (DeepLinkService → /focus).
public struct StudyLifeOpenFocusIntent: AppIntent {
    public static var title: LocalizedStringResource = "Fokus-Timer öffnen"
    public static var description = IntentDescription("Öffnet die Fokus-Seite von StudyLife.")
    public static var openAppWhenRun: Bool = true

    public init() {}

    @MainActor
    public func perform() async throws -> some IntentResult {
        StudyLifeTimerIntentHub.invokeOpenFocus()
        return .result()
    }
}

// MARK: - Read-only stat queries

/// Same snapshot file/fields the widgets read (Services/HomeWidgetSnapshot.cs writes it) -
/// a private, minimal decode here rather than sharing StudyTodayWidget.swift's full
/// StudySnapshot, since that file compiles into the widget extension target only while this
/// one compiles into the app's own static lib (see build.sh's swiftc file list).
private struct SiriStatsSnapshot: Decodable {
    var day: String
    var todayMinutes: Int
    var weekMinutes: Int
    var streakDays: Int
}

private func loadSiriStatsSnapshot() -> SiriStatsSnapshot? {
    guard let container = FileManager.default
        .containerURL(forSecurityApplicationGroupIdentifier: "group.app.studylife.mobile") else { return nil }
    let url = container.appendingPathComponent("widget-snapshot.json")
    guard let data = try? Data(contentsOf: url) else { return nil }
    return try? JSONDecoder().decode(SiriStatsSnapshot.self, from: data)
}

/// Same day/week rollover reasoning as StudyTodayWidget.swift's loadSnapshot: without a fresh
/// app contact yet today/this week, the snapshot's own "day" field tells us the counters are stale.
private func rolledOver(_ raw: SiriStatsSnapshot, now: Date) -> SiriStatsSnapshot {
    var snapshot = raw
    let formatter = DateFormatter()
    formatter.dateFormat = "yyyy-MM-dd"
    guard let snapshotDay = formatter.date(from: raw.day) else { return snapshot }
    var calendar = Calendar(identifier: .iso8601)
    calendar.firstWeekday = 2
    if !calendar.isDate(snapshotDay, inSameDayAs: now) {
        snapshot.todayMinutes = 0
        if !calendar.isDate(snapshotDay, equalTo: now, toGranularity: .weekOfYear) {
            snapshot.weekMinutes = 0
        }
    }
    return snapshot
}

private func durationLabel(_ minutes: Int) -> String {
    minutes >= 60
        ? "\(minutes / 60) Stunden und \(minutes % 60) Minuten"
        : "\(minutes) Minuten"
}

private let statsUnavailableDialog: IntentDialog =
    "Ich konnte deine Daten gerade nicht abrufen. Öffne StudyLife einmal, dann sind sie aktuell."

/// Siri/Shortcuts: read-only, answered straight from the local snapshot file the widgets also
/// use - no app launch needed (openAppWhenRun stays at its default false), same
/// "never blind-start/change anything" design as StudyLifeOpenFocusIntent.
public struct StudyLifeStreakQueryIntent: AppIntent {
    public static var title: LocalizedStringResource = "Lernserie abfragen"
    public static var description = IntentDescription("Sagt dir deine aktuelle Lernserie in StudyLife.")

    public init() {}

    public func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let snapshot = loadSiriStatsSnapshot() else {
            return .result(dialog: statsUnavailableDialog)
        }
        let days = snapshot.streakDays
        if days == 0 {
            return .result(dialog: "Du hast aktuell keine Lernserie in StudyLife.")
        }
        return .result(dialog: "Deine Lernserie in StudyLife beträgt \(days) \(days == 1 ? "Tag" : "Tage").")
    }
}

public struct StudyLifeTodayQueryIntent: AppIntent {
    public static var title: LocalizedStringResource = "Heutige Lernzeit abfragen"
    public static var description = IntentDescription("Sagt dir, wie viel du heute schon in StudyLife gelernt hast.")

    public init() {}

    public func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let raw = loadSiriStatsSnapshot() else {
            return .result(dialog: statsUnavailableDialog)
        }
        let minutes = rolledOver(raw, now: Date()).todayMinutes
        if minutes == 0 {
            return .result(dialog: "Du hast heute noch nicht in StudyLife gelernt.")
        }
        return .result(dialog: "Du hast heute \(durationLabel(minutes)) in StudyLife gelernt.")
    }
}

public struct StudyLifeWeekQueryIntent: AppIntent {
    public static var title: LocalizedStringResource = "Wöchentliche Lernzeit abfragen"
    public static var description = IntentDescription("Sagt dir, wie viel du diese Woche schon in StudyLife gelernt hast.")

    public init() {}

    public func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let raw = loadSiriStatsSnapshot() else {
            return .result(dialog: statsUnavailableDialog)
        }
        let minutes = rolledOver(raw, now: Date()).weekMinutes
        if minutes == 0 {
            return .result(dialog: "Du hast diese Woche noch nicht in StudyLife gelernt.")
        }
        return .result(dialog: "Du hast diese Woche \(durationLabel(minutes)) in StudyLife gelernt.")
    }
}

/// Registers the Siri phrases without manual setup in the Shortcuts app.
public struct StudyLifeAppShortcuts: AppShortcutsProvider {
    public static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: StudyLifeOpenFocusIntent(), phrases: [
            "Starte Fokus-Timer in \(.applicationName)",
            "Fokus in \(.applicationName) starten",
            "Öffne den Fokus-Timer in \(.applicationName)",
            "Start focus timer in \(.applicationName)",
        ])
        AppShortcut(intent: StudyLifeStreakQueryIntent(), phrases: [
            "Wie ist meine Lernserie in \(.applicationName)",
            "Wie lang ist meine Serie in \(.applicationName)",
            "What's my streak in \(.applicationName)",
        ])
        AppShortcut(intent: StudyLifeTodayQueryIntent(), phrases: [
            "Wie viel habe ich heute in \(.applicationName) gelernt",
            "Meine heutige Lernzeit in \(.applicationName)",
            "How much did I study today in \(.applicationName)",
        ])
        AppShortcut(intent: StudyLifeWeekQueryIntent(), phrases: [
            "Wie viel habe ich diese Woche in \(.applicationName) gelernt",
            "Meine wöchentliche Lernzeit in \(.applicationName)",
            "How much did I study this week in \(.applicationName)",
        ])
    }
}

@available(iOS 17.0, *)
public struct StudyLifeTimerToggleIntent: LiveActivityIntent {
    public static var title: LocalizedStringResource = "Fokus-Timer pausieren oder fortsetzen"
    public static var isDiscoverable: Bool = false

    public init() {}

    public func perform() async throws -> some IntentResult {
        StudyLifeTimerIntentHub.invokeToggle()
        return .result()
    }
}
