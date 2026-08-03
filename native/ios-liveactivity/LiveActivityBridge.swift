import ActivityKit
import Foundation
import WidgetKit

// C ABI exports for the .NET side (DllImport "__Internal" in TimerLiveActivity.cs) -
// ActivityKit is Swift-only and unreachable via the ObjC bindings. Linked as a
// static library into the main app (build.sh); the lock screen UI itself
// lives in the widget extension (Widget/StudyLifeWidgets.swift).

// Last error from the activity request - for the diagnostic query from .NET (slla_last_error).
private let lastErrorLock = NSLock()
private var lastErrorMessage: String = ""

private func setLastError(_ message: String) {
    lastErrorLock.lock()
    lastErrorMessage = message
    lastErrorLock.unlock()
}

@available(iOS 16.2, *)
private final class LiveActivityController {
    static let shared = LiveActivityController()
    private var activity: Activity<TimerActivityAttributes>?
    private var activityTitle: String?

    func update(title: String, state: TimerActivityAttributes.ContentState) {
        Task {
            // Mode change = new title: end the old activity, request a new one (the title
            // is part of the immutable attributes).
            if let current = self.activity, self.activityTitle != title {
                await current.end(nil, dismissalPolicy: .immediate)
                self.activity = nil
            }

            // staleDate = phase end + grace period: if the suspended app can't report the
            // phase change (iOS suspension while the device is locked), the card honestly
            // shows "stale" afterwards (context.isStale in the widget) instead of freezing at 0:00.
            // Paused: no staleDate - the static display stays valid indefinitely.
            // 12s instead of the earlier 5s since step D (live activity push): the worker only
            // checks for phase changes every ~5s (BackgroundTaskService.TickInterval), plus some
            // buffer for the actual APNs delivery - a 5s grace period would have briefly shown
            // the card as stale on EVERY phase change, even though the push was simply in transit.
            let content = ActivityContent(
                state: state,
                staleDate: state.isPaused ? nil : state.endsAt.addingTimeInterval(12))

            if let current = self.activity, current.activityState == .active {
                await current.update(content)
            } else {
                do {
                    // pushType: .token activates Activity.pushTokenUpdates (step D) -
                    // without the aps-environment entitlement (free signing) the stream
                    // simply never delivers a token, harmless.
                    self.activity = try Activity.request(
                        attributes: TimerActivityAttributes(title: title),
                        content: content,
                        pushType: .token)
                    setLastError("")
                    observePushTokenUpdates()
                } catch {
                    setLastError("request: \(error)")
                }
                self.activityTitle = title
            }
        }
    }

    /// Reports every new ActivityKit push token to the .NET side (StudyLifeTimerIntentHub) -
    /// from there it goes via HTTP to the server (TimerLiveActivityCoordinator). Usually one
    /// token per activity instance is enough, but Apple can rarely reissue it - hence a
    /// permanently running observer instead of a one-time fetch.
    private func observePushTokenUpdates() {
        guard let activity else { return }
        Task {
            for await tokenData in activity.pushTokenUpdates {
                let token = tokenData.map { String(format: "%02x", $0) }.joined()
                StudyLifeTimerIntentHub.invokePushToken(token)
            }
        }
    }

    var hasActive: Bool {
        Activity<TimerActivityAttributes>.activities.contains { $0.activityState == .active }
    }

    func endAll() {
        Task {
            // Iterate over all activities of this type rather than just the own reference -
            // also cleans up leftovers from a previous app launch.
            for activity in Activity<TimerActivityAttributes>.activities {
                await activity.end(nil, dismissalPolicy: .immediate)
            }
            self.activity = nil
            self.activityTitle = nil
        }
    }
}

@available(iOS 16.2, *)
private final class UpcomingSessionActivityController {
    static let shared = UpcomingSessionActivityController()
    private var activity: Activity<UpcomingSessionActivityAttributes>?
    private var activityKey: String?

    /// key = "title|startsAtEpoch": identifies which planned session is currently shown, so a
    /// repeat call for the SAME session (e.g. every snapshot update while the app is open) is
    /// a no-op instead of tearing the card down and recreating it each time.
    func start(title: String, startsAt: Date) {
        let key = "\(title)|\(startsAt.timeIntervalSince1970)"
        if activityKey == key, let current = activity, current.activityState == .active { return }
        Task {
            for old in Activity<UpcomingSessionActivityAttributes>.activities {
                await old.end(nil, dismissalPolicy: .immediate)
            }
            do {
                self.activity = try Activity.request(
                    attributes: UpcomingSessionActivityAttributes(title: title),
                    content: ActivityContent(
                        state: UpcomingSessionActivityAttributes.ContentState(startsAt: startsAt),
                        staleDate: startsAt.addingTimeInterval(60)))
                self.activityKey = key
            } catch {
                self.activity = nil
                self.activityKey = nil
            }
        }
    }

    func end() {
        Task {
            for activity in Activity<UpcomingSessionActivityAttributes>.activities {
                await activity.end(nil, dismissalPolicy: .immediate)
            }
            self.activity = nil
            self.activityKey = nil
        }
    }
}

@_cdecl("slla_upcoming_start")
public func slla_upcoming_start(_ title: UnsafePointer<CChar>?, _ startsAtEpoch: Double) {
    guard #available(iOS 16.2, *) else { return }
    let titleString = title.map { String(cString: $0) } ?? "StudyLife"
    UpcomingSessionActivityController.shared.start(
        title: titleString, startsAt: Date(timeIntervalSince1970: startsAtEpoch))
}

@_cdecl("slla_upcoming_end")
public func slla_upcoming_end() {
    guard #available(iOS 16.2, *) else { return }
    UpcomingSessionActivityController.shared.end()
}

@_cdecl("slla_is_supported")
public func slla_is_supported() -> Int32 {
    guard #available(iOS 16.2, *) else { return 0 }
    return ActivityAuthorizationInfo().areActivitiesEnabled ? 1 : 0
}

@_cdecl("slla_update")
public func slla_update(
    _ title: UnsafePointer<CChar>?,
    _ endsAtEpoch: Double,
    _ isBreak: Int32,
    _ isPaused: Int32,
    _ secondsLeft: Int32,
    _ phaseTotalSeconds: Int32,
    _ round: Int32,
    _ totalRounds: Int32
) {
    guard #available(iOS 16.2, *) else { return }
    let state = TimerActivityAttributes.ContentState(
        endsAt: Date(timeIntervalSince1970: endsAtEpoch),
        isBreak: isBreak != 0,
        isPaused: isPaused != 0,
        secondsLeft: Int(secondsLeft),
        phaseTotalSeconds: Int(phaseTotalSeconds),
        round: Int(round),
        totalRounds: Int(totalRounds))
    let titleString = title.map { String(cString: $0) } ?? "StudyLife"
    LiveActivityController.shared.update(title: titleString, state: state)
}

@_cdecl("slla_end")
public func slla_end() {
    guard #available(iOS 16.2, *) else { return }
    LiveActivityController.shared.endAll()
}

/// Registers the .NET callback for the live activity's pause/resume button
/// (StudyLifeTimerToggleIntent.perform calls it in the app process) and replays any
/// button press that arrived before registration (cold start via the intent).
@_cdecl("slla_set_toggle_handler")
public func slla_set_toggle_handler(_ handler: (@convention(c) () -> Void)?) {
    StudyLifeTimerIntentHub.toggleHandler = handler
    if handler != nil && StudyLifeTimerIntentHub.pendingToggle {
        StudyLifeTimerIntentHub.pendingToggle = false
        handler?()
    }
}

/// Registers the .NET callback for newly issued ActivityKit push tokens (step D) -
/// no buffering needed: the token is only created AFTER Activity.request() has run, i.e.
/// within the already-running app process (unlike the toggle button, which can arrive via
/// a possible cold-start background launch).
@_cdecl("slla_set_push_token_handler")
public func slla_set_push_token_handler(_ handler: (@convention(c) (UnsafePointer<CChar>?) -> Void)?) {
    StudyLifeTimerIntentHub.pushTokenHandler = handler
}

/// Registers the .NET callback for "open focus" (Siri/Shortcuts) and replays any
/// call that arrived before registration (cold start via Siri).
@_cdecl("slla_set_open_focus_handler")
public func slla_set_open_focus_handler(_ handler: (@convention(c) () -> Void)?) {
    StudyLifeTimerIntentHub.openFocusHandler = handler
    if handler != nil && StudyLifeTimerIntentHub.pendingOpenFocus {
        StudyLifeTimerIntentHub.pendingOpenFocus = false
        handler?()
    }
}

/// Triggers a reload of the home screen widget timelines after the app has
/// updated the snapshot in the App Group container (Services/HomeWidgetSnapshot.cs).
@_cdecl("slla_reload_widgets")
public func slla_reload_widgets() {
    WidgetCenter.shared.reloadTimelines(ofKind: "StudyLifeToday")
    WidgetCenter.shared.reloadTimelines(ofKind: "StudyLifeCourses")
}

/// Diagnostic: 1 = at least one activity visibly active.
@_cdecl("slla_has_active")
public func slla_has_active() -> Int32 {
    guard #available(iOS 16.2, *) else { return 0 }
    return LiveActivityController.shared.hasActive ? 1 : 0
}

/// Diagnostic: last error from Activity.request as a C string (caller copies immediately;
/// the buffer lives until the next call). Empty string = no error.
private var lastErrorCBuffer: UnsafeMutablePointer<CChar>?

@_cdecl("slla_last_error")
public func slla_last_error() -> UnsafePointer<CChar>? {
    lastErrorLock.lock()
    let message = lastErrorMessage
    lastErrorLock.unlock()
    lastErrorCBuffer?.deallocate()
    lastErrorCBuffer = strdup(message)
    return UnsafePointer(lastErrorCBuffer)
}
