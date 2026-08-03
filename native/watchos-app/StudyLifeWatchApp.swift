import SwiftUI
import UserNotifications
import WatchConnectivity
import WatchKit
import WidgetKit

// Watch-side counterpart to native/ios-liveactivity/WatchBridge.swift: receives the phone's
// pushed snapshot via WCSession.updateApplicationContext, writes it to the Watch's OWN local
// App Group container (WatchSnapshot.swift - a separate device, separate filesystem from the
// phone's own container even though the group id string is the same), then reloads the
// complication's timeline so it picks up the new data on its own.
final class WatchSessionDelegate: NSObject, WCSessionDelegate, ObservableObject {
    static let shared = WatchSessionDelegate()
    @Published var snapshot: WatchSnapshot?
    /// Set to true exactly once per genuine session completion (see sessionCompletedAt) -
    /// ContentView presents the rating prompt while this is true and clears it on submit/dismiss.
    @Published var showRatingPrompt = false

    override init() {
        super.init()
        snapshot = loadWatchSnapshot(now: .now)
    }

    func activate() {
        guard WCSession.isSupported() else { return }
        WCSession.default.delegate = self
        WCSession.default.activate()
    }

    func session(_ session: WCSession, activationDidCompleteWith activationState: WCSessionActivationState, error: Error?) {}

    func session(_ session: WCSession, didReceiveApplicationContext applicationContext: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: applicationContext) else { return }
        saveWatchSnapshot(data)
        WidgetCenter.shared.reloadTimelines(ofKind: "StudyLifeWatchToday")
        let previous = snapshot
        let decoded = try? JSONDecoder().decode(WatchSnapshot.self, from: data)
        DispatchQueue.main.async {
            self.snapshot = decoded
            // Extended runtime session: start exactly on the running-state transition (not on
            // every context push, and not on a focus<->break flip while staying "running") so
            // the Watch stays awake for the duration of an actual phase, then release it the
            // moment the phone reports the timer stopped/paused/completed.
            let wasRunning = previous?.timerRunning ?? false
            let isRunning = decoded?.timerRunning ?? false
            if isRunning && !wasRunning {
                ExtendedRuntimeSessionManager.shared.start()
            } else if !isRunning && wasRunning {
                ExtendedRuntimeSessionManager.shared.stop()
            }
            Self.playTransitionHaptic(from: previous, to: decoded)
            // sessionCompletedAt (unlike the running/not-running diff above) unambiguously
            // means "a focus round just finished" - not "paused"/"stopped" - so only here is
            // it correct to claim completion and ask for a rating.
            if let completedAt = decoded?.sessionCompletedAt, completedAt != previous?.sessionCompletedAt {
                Self.postLocalNotification(title: "Fokuszeit vorbei", body: "Wie lief die Session?")
                self.showRatingPrompt = true
            }
            // standNudgeAt: same one-shot-timestamp pattern - fires a distinct haptic (not
            // reused from playTransitionHaptic, since this isn't a phase transition at all).
            if let nudgeAt = decoded?.standNudgeAt, nudgeAt != previous?.standNudgeAt {
                WKInterfaceDevice.current().play(.notification)
                Self.postLocalNotification(title: "Kurz aufstehen?", body: "Zeit für eine kurze Dehnpause.")
            }
        }
    }

    /// No dedicated "phase changed" push exists (see docs/plan) - every new context IS a
    /// phase-change signal often enough to detect the transition by diffing the two
    /// snapshots client-side: focus started, break started, or the timer stopped running
    /// entirely (paused/stopped/completed - ambiguous, hence no wording here, just a haptic).
    private static func playTransitionHaptic(from previous: WatchSnapshot?, to current: WatchSnapshot?) {
        let wasRunning = previous?.timerRunning ?? false
        let isRunning = current?.timerRunning ?? false
        guard wasRunning != isRunning || previous?.timerIsBreak != current?.timerIsBreak else { return }
        if isRunning {
            WKInterfaceDevice.current().play(current?.timerIsBreak == true ? .stop : .start)
        } else if wasRunning {
            WKInterfaceDevice.current().play(.success)
        }
    }

    private static func postLocalNotification(title: String, body: String) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }
}

@main
struct StudyLifeWatchApp: App {
    @StateObject private var session = WatchSessionDelegate.shared

    init() {
        WatchSessionDelegate.shared.activate()
        // Best-effort, same "ignore the result" pattern as the rest of the bridge - a denied/
        // undetermined permission just means postLocalNotification silently does nothing.
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    var body: some Scene {
        WindowGroup {
            ContentView().environmentObject(session)
        }
    }
}
