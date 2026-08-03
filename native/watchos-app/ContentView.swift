import SwiftUI

private let wAccent = Color(red: 204 / 255, green: 120 / 255, blue: 92 / 255)

struct ContentView: View {
    @EnvironmentObject private var session: WatchSessionDelegate
    // Always-On (wrist lowered): dim/declutter instead of the full interactive layout -
    // Text(timerInterval:) already renders correctly on its own in this state, the rest of
    // the chrome (buttons, secondary stats) doesn't need to stay visible or vibrant.
    @Environment(\.isLuminanceReduced) private var isLuminanceReduced

    private var baked: WatchSnapshot? {
        session.snapshot.map { bakedWatchSnapshot($0, at: .now) }
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    if let snapshot = baked {
                        if isLuminanceReduced {
                            alwaysOnContent(snapshot)
                        } else {
                            content(snapshot)
                        }
                    } else {
                        VStack(spacing: 4) {
                            Text("✦").font(.title2).foregroundStyle(wAccent)
                            Text("Öffne StudyLife\nauf dem iPhone")
                                .font(.caption2)
                                .multilineTextAlignment(.center)
                        }
                    }
                }
                .padding(.horizontal, 4)
            }
        }
        // Triggered by a genuine session-complete signal (sessionCompletedAt), never by a
        // plain pause/stop - see StudyLifeWatchApp.swift's didReceiveApplicationContext.
        .sheet(isPresented: $session.showRatingPrompt) {
            RatingPromptView()
        }
    }

    /// Minimal always-on variant: just the primary stat, no buttons/colors that would look
    /// odd dimmed and static for extended periods.
    @ViewBuilder
    private func alwaysOnContent(_ snapshot: WatchSnapshot) -> some View {
        if let ends = snapshot.activeTimerEnd {
            VStack(alignment: .leading, spacing: 2) {
                Text(snapshot.timerIsBreak == true ? "Pause" : "Fokus")
                    .font(.caption).foregroundStyle(.secondary)
                Text(timerInterval: Date.now...ends, countsDown: true)
                    .font(.system(size: 26, weight: .light).monospacedDigit())
                    .minimumScaleFactor(0.5)
            }
        } else {
            VStack(alignment: .leading, spacing: 2) {
                Text("Heute").font(.caption).foregroundStyle(.secondary)
                Text(formatWatchMinutes(snapshot.todayMinutes))
                    .font(.system(size: 26, weight: .light).monospacedDigit())
            }
        }
    }

    @ViewBuilder
    private func content(_ snapshot: WatchSnapshot) -> some View {
        if let ends = snapshot.activeTimerEnd {
            VStack(alignment: .leading, spacing: 2) {
                Text(snapshot.timerIsBreak == true ? "✦ Pause" : "✦ Fokus läuft")
                    .font(.caption).foregroundStyle(.secondary)
                Text(timerInterval: Date.now...ends, countsDown: true)
                    .font(.system(size: 26, weight: .light).monospacedDigit())
                    .foregroundStyle(wAccent)
                    .minimumScaleFactor(0.5)
            }
        } else {
            VStack(alignment: .leading, spacing: 2) {
                Text("✦ Heute").font(.caption).foregroundStyle(.secondary)
                Text(formatWatchMinutes(snapshot.todayMinutes))
                    .font(.system(size: 26, weight: .light).monospacedDigit())
                    .foregroundStyle(snapshot.todayMinutes > 0 ? wAccent : .secondary)
            }
        }

        HStack(spacing: 4) {
            Image(systemName: "flame.fill")
                .foregroundStyle(snapshot.streakDays > 0 ? .orange : .secondary)
            Text(snapshot.streakDays == 1 ? "1 Tag Serie" : "\(snapshot.streakDays) Tage Serie")
        }
        .font(.caption)

        // Relays to the phone's TimerService (WatchTimerCoordinator.cs) - never runs a timer
        // on the Watch itself, see docs/plan. The snapshot only ever tells us "running" or
        // "not currently known to be running" (HomeWidgetSnapshot omits paused state
        // entirely) - "Start" is therefore a best-effort tap that silently no-ops on the
        // phone if no mode is loaded there, same guard TimerService.Start() already has.
        HStack(spacing: 8) {
            Button {
                sendTimerCommand(snapshot.activeTimerEnd != nil ? .pause : .start)
            } label: {
                Label(snapshot.activeTimerEnd != nil ? "Pause" : "Start",
                      systemImage: snapshot.activeTimerEnd != nil ? "pause.fill" : "play.fill")
            }
            .tint(wAccent)
            // Double Tap (Series 9+/Ultra 2+): pinch thumb-to-index without touching the
            // screen triggers whichever control carries .primaryAction - makes this button
            // the Double Tap target regardless of scroll position.
            .handGestureShortcut(.primaryAction)

            if let modes = snapshot.modes, !modes.isEmpty {
                NavigationLink(destination: ModePickerView(modes: modes)) {
                    Image(systemName: "list.bullet")
                }
                .frame(width: 36)
            }
        }

        HStack(spacing: 14) {
            if let goal = snapshot.weeklyGoalMinutes, goal > 0 {
                Gauge(value: Double(min(snapshot.weekMinutes, goal)), in: 0...Double(goal)) {
                    EmptyView()
                } currentValueLabel: {
                    Text("\(Int((Double(snapshot.weekMinutes) / Double(goal)) * 100))%")
                        .font(.system(size: 11))
                }
                .gaugeStyle(.accessoryCircularCapacity)
                .tint(wAccent)
                .frame(width: 34, height: 34)
                .padding(.trailing, 2)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text("Woche").font(.caption2).foregroundStyle(.secondary)
                Text(formatWatchMinutes(snapshot.weekMinutes)).font(.caption2)
            }
        }
        .padding(.vertical, 2)

        if let title = snapshot.currentTitle, let ends = snapshot.currentEndsAt {
            Divider()
            Text("▶ \(title)").font(.caption2.weight(.semibold)).lineLimit(1)
            Text("bis \(clockLabel(ends))").font(.caption2).foregroundStyle(.secondary)
        } else if let title = snapshot.nextTitle {
            Divider()
            Text("→ \(title)").font(.caption2).lineLimit(1)
        }

        if let recent = snapshot.recentSessions, !recent.isEmpty {
            Divider()
            Text("Zuletzt").font(.caption2).foregroundStyle(.secondary)
            ForEach(recent) { entry in
                HStack {
                    Text(entry.title).font(.caption2).lineLimit(1)
                    Spacer(minLength: 4)
                    Text(formatWatchMinutes(entry.minutes)).font(.caption2).foregroundStyle(.secondary)
                }
            }
        }

        // "Auswertung": scrolled to directly, not behind a button - weekly plan, 7-day chart,
        // ECTS progress, top courses, all-time total.
        Divider()
        StatsSection(snapshot: snapshot)
    }

    private func clockLabel(_ epoch: Double) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm"
        return formatter.string(from: Date(timeIntervalSince1970: epoch))
    }
}
