import WatchKit

// Keeps the Watch app alive in the background while a focus/break phase is actively running,
// so haptics, the rating prompt, and complication updates stay reliable instead of depending on
// watchOS opportunistically waking the app when a new WCSession context arrives. This is the
// generic WKExtendedRuntimeSession (no sessionType) - unlike HKWorkoutSession or the
// .mindfulness/.smartAlarm session types, the OS does NOT guarantee a long background window
// for it, so this reduces but doesn't eliminate the chance of the app suspending mid-session.
final class ExtendedRuntimeSessionManager: NSObject, WKExtendedRuntimeSessionDelegate {
    static let shared = ExtendedRuntimeSessionManager()
    private var session: WKExtendedRuntimeSession?

    func start() {
        guard session == nil else { return }
        let newSession = WKExtendedRuntimeSession()
        newSession.delegate = self
        newSession.start()
        session = newSession
    }

    func stop() {
        session?.invalidate()
        session = nil
    }

    func extendedRuntimeSessionDidStart(_ extendedRuntimeSession: WKExtendedRuntimeSession) {}

    /// Called ~1 min before the system will invalidate the session - nothing to do here, the
    /// timer keeps running on the phone regardless; the Watch simply loses its background grant.
    func extendedRuntimeSessionWillExpire(_ extendedRuntimeSession: WKExtendedRuntimeSession) {}

    func extendedRuntimeSession(_ extendedRuntimeSession: WKExtendedRuntimeSession,
                                 didInvalidateWith reason: WKExtendedRuntimeSessionInvalidationReason,
                                 error: Error?) {
        session = nil
    }
}
