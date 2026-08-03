import ActivityKit
import Foundation

// Shared attributes definition for the app AND the widget extension - same convention as
// TimerActivityAttributes.swift (the bridge/@_cdecl side lives in LiveActivityBridge.swift,
// not here, so this file compiles cleanly into the widget extension target too).
//
// "Next session starts soon" Live Activity - a separate, independently-active Live Activity
// kind from the focus timer's. Unlike that one, this needs NO remote push: startsAt never
// changes once the card is up, so the countdown ticks itself via Text(timerInterval:) - the
// card is simply ended once the session starts or the plan changes (HomeWidgetSnapshot.cs on
// the .NET side decides when to start/end it).
struct UpcomingSessionActivityAttributes: ActivityAttributes {
    public struct ContentState: Codable, Hashable {
        var startsAt: Date
    }
    /// Session title (course/topic) - fixed at creation; if the "next" session's title
    /// changes, .NET calls start() again which tears down and recreates the card.
    var title: String
}
