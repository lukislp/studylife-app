import SwiftUI

// Lightweight watch-only equivalent of the iPhone's text reflection prompt (typing on a
// watch is impractical) - a tap sends the rating to the phone (sendSessionRating), which
// stores it as a short Note (WatchTimerCoordinator.cs), reusing the existing Notes
// infrastructure rather than a new schema/endpoint.
struct RatingPromptView: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 12) {
            Text("Wie lief die Session?").font(.headline).multilineTextAlignment(.center)
            HStack(spacing: 16) {
                ratingButton("👎", rating: 0)
                ratingButton("😐", rating: 1)
                ratingButton("👍", rating: 2)
            }
        }
        .padding()
    }

    private func ratingButton(_ emoji: String, rating: Int) -> some View {
        Button {
            sendSessionRating(rating)
            dismiss()
        } label: {
            Text(emoji).font(.title2)
        }
        .buttonStyle(.plain)
    }
}
