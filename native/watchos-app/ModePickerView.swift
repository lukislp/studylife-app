import SwiftUI

// Tapping a mode sends command 3 (loadModeAndStart) to the phone - WatchTimerCoordinator.cs
// resolves the id against the same built-in + custom mode list Focus.razor uses, then calls
// TimerService.LoadMode(mode) + Start() (same as picking the mode on the phone itself).
struct ModePickerView: View {
    let modes: [WatchTimerModeInfo]
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        List(modes) { mode in
            Button {
                sendTimerCommand(.loadModeAndStart, modeId: mode.id)
                dismiss()
            } label: {
                Text("\(mode.emoji) \(mode.name)")
            }
        }
        .navigationTitle("Modus")
    }
}
