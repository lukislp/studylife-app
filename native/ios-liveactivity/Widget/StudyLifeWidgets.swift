import ActivityKit
import SwiftUI
import WidgetKit

// Lock screen/Dynamic Island UI of the focus timer. Colors based on the app
// (#0e0e0f background, #CC785C accent, #e8e6e0 text).

private let accent = Color(red: 204 / 255, green: 120 / 255, blue: 92 / 255)
private let bg = Color(red: 14 / 255, green: 14 / 255, blue: 15 / 255)
private let fg = Color(red: 232 / 255, green: 230 / 255, blue: 224 / 255)
private let fgMuted = Color(red: 107 / 255, green: 105 / 255, blue: 101 / 255)

@main
struct StudyLifeWidgetBundle: WidgetBundle {
    var body: some Widget {
        StudyLifeTimerLiveActivity()
        UpcomingSessionLiveActivity()
        StudyTodayWidget()
        CourseProgressWidget()
    }
}

struct UpcomingSessionLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: UpcomingSessionActivityAttributes.self) { context in
            UpcomingSessionLockScreenView(title: context.attributes.title, state: context.state, isStale: context.isStale)
                .activityBackgroundTint(bg)
                .activitySystemActionForegroundColor(accent)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Nächste Session").font(.caption).foregroundStyle(fgMuted)
                        Text(context.attributes.title).font(.headline).foregroundStyle(fg).lineLimit(1)
                    }
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text(timerInterval: Date.now...max(Date.now, context.state.startsAt), countsDown: true)
                        .font(.title2.monospacedDigit())
                        .foregroundStyle(accent)
                        .multilineTextAlignment(.trailing)
                }
            } compactLeading: {
                Image(systemName: "clock").foregroundStyle(accent)
            } compactTrailing: {
                Text(timerInterval: Date.now...max(Date.now, context.state.startsAt), countsDown: true)
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(accent)
                    .frame(maxWidth: 52)
            } minimal: {
                Image(systemName: "clock").foregroundStyle(accent)
            }
        }
    }
}

private struct UpcomingSessionLockScreenView: View {
    let title: String
    let state: UpcomingSessionActivityAttributes.ContentState
    var isStale: Bool = false

    var body: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text("✦").foregroundStyle(accent)
                    Text("Nächste Session").font(.caption).foregroundStyle(fgMuted)
                }
                Text(title).font(.headline).foregroundStyle(fg).lineLimit(2)
            }
            Spacer()
            if isStale {
                Text("Jetzt").font(.system(size: 24, weight: .light)).foregroundStyle(fgMuted)
            } else {
                Text(timerInterval: Date.now...max(Date.now, state.startsAt), countsDown: true)
                    .font(.system(size: 32, weight: .light).monospacedDigit())
                    .foregroundStyle(accent)
                    .multilineTextAlignment(.trailing)
            }
        }
        .padding(16)
    }
}

struct StudyLifeTimerLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: TimerActivityAttributes.self) { context in
            LockScreenView(title: context.attributes.title, state: context.state, isStale: context.isStale)
                .activityBackgroundTint(bg)
                .activitySystemActionForegroundColor(accent)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(context.attributes.title)
                            .font(.caption)
                            .foregroundStyle(fgMuted)
                            .lineLimit(1)
                        Text(phaseLabel(context.state))
                            .font(.headline)
                            .foregroundStyle(fg)
                    }
                }
                DynamicIslandExpandedRegion(.trailing) {
                    CountdownText(state: context.state)
                        .font(.title2.monospacedDigit())
                        .foregroundStyle(accent)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    HStack {
                        Text(roundLabel(context.state))
                            .font(.caption)
                            .foregroundStyle(fgMuted)
                        Spacer()
                        if #available(iOS 17.0, *) {
                            Button(intent: StudyLifeTimerToggleIntent()) {
                                Image(systemName: context.state.isPaused ? "play.fill" : "pause.fill")
                                    .font(.body)
                                    .foregroundStyle(accent)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            } compactLeading: {
                // Mini progress ring instead of a static icon - fills on its own.
                PhaseProgress(state: context.state)
                    .progressViewStyle(.circular)
                    .tint(accent)
                    .frame(width: 20, height: 20)
            } compactTrailing: {
                CountdownText(state: context.state)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(accent)
                    .frame(maxWidth: 52)
            } minimal: {
                Image(systemName: context.state.isBreak ? "cup.and.saucer.fill" : "timer")
                    .foregroundStyle(accent)
            }
        }
    }
}

/// Progress of the running phase: while the timer is running, ProgressView(timerInterval:)
/// fills on its own (like the countdown text); when paused it shows the frozen state.
private struct PhaseProgress: View {
    let state: TimerActivityAttributes.ContentState

    var body: some View {
        let total = max(1, state.phaseTotalSeconds)
        if state.isPaused {
            ProgressView(value: Double(total - max(0, min(total, state.secondsLeft))), total: Double(total))
        } else {
            let start = state.endsAt.addingTimeInterval(-Double(total))
            ProgressView(timerInterval: start...max(start, state.endsAt), countsDown: false)
                .labelsHidden()
        }
    }
}

private struct LockScreenView: View {
    let title: String
    let state: TimerActivityAttributes.ContentState
    var isStale: Bool = false

    var body: some View {
        VStack(spacing: 10) {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text("✦").foregroundStyle(accent)
                    Text(title)
                        .font(.caption)
                        .foregroundStyle(fgMuted)
                        .lineLimit(1)
                }
                Text(isStale ? "Phase vorbei — App öffnen" : phaseLabel(state))
                    .font(.headline)
                    .foregroundStyle(isStale ? fgMuted : fg)
                Text(roundLabel(state))
                    .font(.caption2)
                    .foregroundStyle(fgMuted)
            }
            Spacer()
            if isStale {
                Text("✓")
                    .font(.system(size: 40, weight: .light))
                    .foregroundStyle(fgMuted)
            } else {
                // Pause/resume directly on the card (iOS 17+; below that, display only).
                if #available(iOS 17.0, *) {
                    Button(intent: StudyLifeTimerToggleIntent()) {
                        Image(systemName: state.isPaused ? "play.fill" : "pause.fill")
                            .font(.title3)
                            .foregroundStyle(accent)
                            .frame(width: 40, height: 40)
                            .background(accent.opacity(0.18), in: Circle())
                    }
                    .buttonStyle(.plain)
                }
                CountdownText(state: state)
                    .font(.system(size: 40, weight: .light).monospacedDigit())
                    .foregroundStyle(state.isPaused ? fgMuted : accent)
            }
        }
        if !isStale {
            PhaseProgress(state: state)
                .progressViewStyle(.linear)
                .tint(state.isPaused ? fgMuted : accent)
        }
        }
        .padding(16)
    }
}

/// While the timer is running, Text(timerInterval:) counts down on its own without updates;
/// when paused it statically shows the frozen remainder.
private struct CountdownText: View {
    let state: TimerActivityAttributes.ContentState

    var body: some View {
        if state.isPaused {
            Text(staticRemaining)
        } else {
            Text(timerInterval: Date.now...max(Date.now, state.endsAt), countsDown: true)
                .multilineTextAlignment(.trailing)
        }
    }

    private var staticRemaining: String {
        let total = max(0, state.secondsLeft)
        return String(format: "%d:%02d", total / 60, total % 60)
    }
}

private func phaseLabel(_ state: TimerActivityAttributes.ContentState) -> String {
    if state.isPaused { return "Pausiert" }
    return state.isBreak ? "Pause ☕" : "Fokus"
}

private func roundLabel(_ state: TimerActivityAttributes.ContentState) -> String {
    "Runde \(state.round) von \(state.totalRounds)"
}
