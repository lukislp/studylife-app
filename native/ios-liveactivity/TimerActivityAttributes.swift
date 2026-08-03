import ActivityKit
import Foundation

// Shared attributes definition for the app AND the widget extension - ActivityKit matches
// the live activity via this type, so the SAME file is compiled into both targets
// (see build.sh). Field changes here are protocol changes between both binaries.
struct TimerActivityAttributes: ActivityAttributes {
    public struct ContentState: Hashable {
        /// Phase end - the countdown on the lock screen counts down on its own
        /// (Text(timerInterval:)), so it doesn't need continuous updates.
        var endsAt: Date
        var isBreak: Bool
        var isPaused: Bool
        /// Only relevant for the paused display (static remainder while the timer is stopped).
        var secondsLeft: Int
        /// Total length of the running phase in seconds - basis for the progress bar
        /// (ProgressView(timerInterval:) fills on its own from endsAt-total to endsAt).
        var phaseTotalSeconds: Int
        var round: Int
        var totalRounds: Int
    }

    /// Mode name (e.g. "Pomodoro") - fixed per activity; a mode change starts a new one.
    var title: String
}

// Explicit Codable implementation instead of the synthesized one: the local Activity.update()
// calls (LiveActivityBridge.swift) build the struct directly in Swift, no JSON involved -
// but the APNs remote push path (step D, BackgroundTaskService.LiveActivity.cs) delivers
// content-state as JSON, which ActivityKit decodes with EXACTLY this Codable implementation.
// Swift's synthesized date codec encodes/decodes as seconds since the reference date
// 2001-01-01 (timeIntervalSinceReferenceDate), NOT as Unix epoch - the server (C#) doesn't
// know about this Swift internals quirk and sends perfectly ordinary Unix timestamps. Without
// this override, every remote push update would arrive with an endsAt that's off by ~31 years
// (or, depending on the value range, get silently discarded) - no error visible on our
// side, yet Apple still confirms the delivery itself as successful.
extension TimerActivityAttributes.ContentState: Codable {
    private enum CodingKeys: String, CodingKey {
        case endsAt, isBreak, isPaused, secondsLeft, phaseTotalSeconds, round, totalRounds
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        endsAt = Date(timeIntervalSince1970: try c.decode(Double.self, forKey: .endsAt))
        isBreak = try c.decode(Bool.self, forKey: .isBreak)
        isPaused = try c.decode(Bool.self, forKey: .isPaused)
        secondsLeft = try c.decode(Int.self, forKey: .secondsLeft)
        phaseTotalSeconds = try c.decode(Int.self, forKey: .phaseTotalSeconds)
        round = try c.decode(Int.self, forKey: .round)
        totalRounds = try c.decode(Int.self, forKey: .totalRounds)
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(endsAt.timeIntervalSince1970, forKey: .endsAt)
        try c.encode(isBreak, forKey: .isBreak)
        try c.encode(isPaused, forKey: .isPaused)
        try c.encode(secondsLeft, forKey: .secondsLeft)
        try c.encode(phaseTotalSeconds, forKey: .phaseTotalSeconds)
        try c.encode(round, forKey: .round)
        try c.encode(totalRounds, forKey: .totalRounds)
    }
}
