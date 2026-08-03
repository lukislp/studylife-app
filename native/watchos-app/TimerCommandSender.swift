import WatchConnectivity

// Sends start/pause/stop/loadMode taps to the phone, which relays them into TimerService
// (Services/WatchBridge.cs's WatchTimerCoordinator) - the Watch never talks to the server or
// runs its own timer, see docs/plan for why. Command codes must match WatchTimerCoordinator:
// 0 = start, 1 = pause, 2 = stop, 3 = loadModeAndStart (modeId required).
enum TimerCommand: Int {
    case start = 0
    case pause = 1
    case stop = 2
    case loadModeAndStart = 3
}

func sendTimerCommand(_ command: TimerCommand, modeId: Int = 0) {
    sendTimerPayload(["command": command.rawValue, "modeId": modeId])
}

// Post-session rating (2 = 👍, 1 = 😐, 0 = 👎) - a separate "rating" key, not "command", so
// WatchBridge.swift's didReceiveMessage can tell it apart from a timer command.
func sendSessionRating(_ rating: Int) {
    sendTimerPayload(["rating": rating])
}

private func sendTimerPayload(_ payload: [String: Any]) {
    guard WCSession.isSupported() else { return }
    let session = WCSession.default
    guard session.activationState == .activated else { return }
    if session.isReachable {
        session.sendMessage(payload, replyHandler: nil) { _ in
            // sendMessage can still fail even when isReachable was true a moment ago
            // (e.g. the phone's app got suspended in between) - fall back to the queued
            // transfer so the tap isn't silently lost.
            session.transferUserInfo(payload)
        }
    } else {
        // Phone not currently reachable (locked/out of Bluetooth-Wi-Fi range/app not running):
        // queues the command, delivered once the phone's WCSession wakes up again.
        session.transferUserInfo(payload)
    }
}
