import Foundation
import WatchConnectivity

// C ABI exports for the .NET side (DllImport "__Internal" in Services/WatchBridge.cs) -
// mirrors the LiveActivityBridge.swift pattern. Phase 1: read-only push of the same JSON
// snapshot HomeWidgetSnapshot.cs already writes to the iOS App Group container, relayed to
// the paired Watch via WCSession.updateApplicationContext - the Watch's own local copy of
// the app (native/watchos-app) decodes it and writes it to ITS OWN local App Group container,
// which the Watch's complication (native/watchos-app/Widgets) then reads, mirroring the
// iOS widget's "phone computes, extension only reads a local file" pattern one hop further.

// Phase 2: start/pause/stop taps on the Watch arrive here via WCSession's plain (non-reply)
// sendMessage and get relayed into .NET through a registered C function pointer - same
// buffering pattern as StudyLifeTimerIntentHub.pendingToggle (TimerControlIntent.swift):
// a background-launched app process can receive the message before .NET has finished
// booting and registered its handler, so buffer it and replay once registered.
enum StudyLifeWatchCommandHub {
    static var commandHandler: (@convention(c) (Int32, Int32) -> Void)?
    private static var pendingCommand: (command: Int32, modeId: Int32)?

    // modeId is only meaningful for command 3 (loadModeAndStart) - 0 otherwise.
    static func invoke(_ command: Int32, _ modeId: Int32) {
        if let handler = commandHandler { handler(command, modeId) } else { pendingCommand = (command, modeId) }
    }

    static func replayPendingIfAny() {
        if let handler = commandHandler, let pending = pendingCommand {
            pendingCommand = nil
            handler(pending.command, pending.modeId)
        }
    }
}

// Post-session rating: a separate top-level key ("rating", not "command") so it can't be
// confused with a timer command in the same message dictionary shape.
enum StudyLifeWatchRatingHub {
    static var ratingHandler: (@convention(c) (Int32) -> Void)?
    private static var pendingRating: Int32?

    static func invoke(_ rating: Int32) {
        if let handler = ratingHandler { handler(rating) } else { pendingRating = rating }
    }

    static func replayPendingIfAny() {
        if let handler = ratingHandler, let rating = pendingRating {
            pendingRating = nil
            handler(rating)
        }
    }
}

@available(iOS 9.0, *)
private final class WatchSessionDelegate: NSObject, WCSessionDelegate {
    static let shared = WatchSessionDelegate()

    func session(_ session: WCSession, activationDidCompleteWith activationState: WCSessionActivationState, error: Error?) {}
    func sessionDidBecomeInactive(_ session: WCSession) {}
    func sessionDidDeactivate(_ session: WCSession) { session.activate() }

    // 0 = start, 1 = pause, 2 = stop, 3 = loadModeAndStart (modeId set) - see WatchTimerCoordinator.cs.
    func session(_ session: WCSession, didReceiveMessage message: [String: Any]) {
        if let rating = message["rating"] as? Int {
            StudyLifeWatchRatingHub.invoke(Int32(rating))
            return
        }
        guard let command = message["command"] as? Int else { return }
        StudyLifeWatchCommandHub.invoke(Int32(command), Int32(message["modeId"] as? Int ?? 0))
    }

    // Queued fallback (TimerCommandSender.swift's transferUserInfo) for when the Watch
    // couldn't reach the phone directly - delivered once the session wakes up again.
    func session(_ session: WCSession, didReceiveUserInfo userInfo: [String: Any] = [:]) {
        if let rating = userInfo["rating"] as? Int {
            StudyLifeWatchRatingHub.invoke(Int32(rating))
            return
        }
        guard let command = userInfo["command"] as? Int else { return }
        StudyLifeWatchCommandHub.invoke(Int32(command), Int32(userInfo["modeId"] as? Int ?? 0))
    }
}

@_cdecl("slla_watch_is_supported")
public func slla_watch_is_supported() -> Int32 {
    guard WCSession.isSupported() else { return 0 }
    return 1
}

/// Activates the WCSession once at app startup (no-op if the paired device has no Watch,
/// or on hardware where WatchConnectivity isn't supported at all).
@_cdecl("slla_watch_activate")
public func slla_watch_activate() {
    guard WCSession.isSupported() else { return }
    let session = WCSession.default
    session.delegate = WatchSessionDelegate.shared
    session.activate()
}

/// Pushes the same snapshot JSON HomeWidgetSnapshot.cs already wrote to the iOS App Group
/// container - updateApplicationContext keeps only the LATEST payload (superseding any
/// undelivered previous one), which matches the widget snapshot's own "always show the
/// latest known state" semantics. Silently does nothing if the session isn't activated/
/// no Watch is paired - same best-effort semantics as HomeWidgetSnapshot.UpdateAsync itself.
@_cdecl("slla_watch_push_context")
public func slla_watch_push_context(_ json: UnsafePointer<UInt8>?, _ length: Int32) {
    guard WCSession.isSupported(), WCSession.default.activationState == .activated,
          let json, length > 0 else { return }
    let data = Data(bytes: json, count: Int(length))
    guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
    try? WCSession.default.updateApplicationContext(object)
}

/// Registers the .NET callback for start/pause/stop commands sent from the Watch and
/// replays any command that arrived before registration (cold start via a background wake).
@_cdecl("slla_watch_set_command_handler")
public func slla_watch_set_command_handler(_ handler: (@convention(c) (Int32, Int32) -> Void)?) {
    StudyLifeWatchCommandHub.commandHandler = handler
    StudyLifeWatchCommandHub.replayPendingIfAny()
}

/// Registers the .NET callback for a post-session rating tap from the Watch and replays
/// any rating that arrived before registration (cold start via a background wake).
@_cdecl("slla_watch_set_rating_handler")
public func slla_watch_set_rating_handler(_ handler: (@convention(c) (Int32) -> Void)?) {
    StudyLifeWatchRatingHub.ratingHandler = handler
    StudyLifeWatchRatingHub.replayPendingIfAny()
}
