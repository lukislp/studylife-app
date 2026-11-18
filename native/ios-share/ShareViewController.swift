import UIKit
import UniformTypeIdentifiers

// "StudyAtrium" share extension (share sheet): accepts shared text and/or URLs, asks the user to
// confirm them, and only then appends them as a JSON array to the App Group inbox
// (SharedNoteIntake.DrainIosInbox reads it on the next app launch/foreground transition and turns
// it into notes). Same rule as on Android (Platforms/Android/SharedTextConfirmation.cs): nothing
// becomes a note before the user saw the beginning of the text and said yes. The confirmation is
// a plain alert (preview, Save / Cancel), deliberately no compose UI. A share extension isn't
// allowed to open the main app itself (openURL is locked down there), hence the inbox detour.
//
// Bounded like the app side (IntakeLimits and SharedText in src/StudyLife.App.Core): one shared text
// is cut after maxTextLength characters (UTF-16 units, as .NET counts them), the preview shows the
// first previewLength of them (SharedText.PreviewChars), and the inbox holds at most
// maxPendingEntries texts until the app took them; further shares are dropped.
final class ShareViewController: UIViewController {
    private let appGroupId = "group.app.studylife.mobile"
    private let inboxFileName = "shared-note-inbox.json"
    private let maxTextLength = 20_000
    private let maxPendingEntries = 20
    private let previewLength = 300
    private var didAsk = false

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        // The alert can only be presented once the view is on screen; ask exactly once.
        guard !didAsk else { return }
        didAsk = true
        collectSharedText { [weak self] text in
            guard let self else { return }
            guard let text = text?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else {
                self.finish()
                return
            }
            self.confirm(text)
        }
    }

    /// Shows the beginning of the text; only "Save" queues it, "Cancel" drops it.
    private func confirm(_ text: String) {
        let alert = UIAlertController(
            title: String(localized: "Save as note?"),
            message: preview(of: text),
            preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: String(localized: "Cancel"), style: .cancel) { [weak self] _ in
            self?.finish()
        })
        alert.addAction(UIAlertAction(title: String(localized: "Save"), style: .default) { [weak self] _ in
            self?.appendToInbox(text)
            self?.finish()
        })
        present(alert, animated: true)
    }

    private func finish() {
        extensionContext?.completeRequest(returningItems: nil)
    }

    /// The beginning of the text, with an ellipsis when it goes on (SharedText.Preview).
    private func preview(of text: String) -> String {
        text.utf16.count <= previewLength ? text : ShareViewController.capped(text, to: previewLength) + "…"
    }

    private func collectSharedText(completion: @escaping (String?) -> Void) {
        let providers = (extensionContext?.inputItems as? [NSExtensionItem])?
            .flatMap { $0.attachments ?? [] } ?? []
        var parts: [String] = []
        let group = DispatchGroup()
        for provider in providers {
            if provider.hasItemConformingToTypeIdentifier(UTType.plainText.identifier) {
                group.enter()
                provider.loadItem(forTypeIdentifier: UTType.plainText.identifier) { item, _ in
                    DispatchQueue.main.async {
                        if let value = item as? String { parts.append(value) }
                        group.leave()
                    }
                }
            } else if provider.hasItemConformingToTypeIdentifier(UTType.url.identifier) {
                group.enter()
                provider.loadItem(forTypeIdentifier: UTType.url.identifier) { item, _ in
                    DispatchQueue.main.async {
                        if let value = item as? URL { parts.append(value.absoluteString) }
                        group.leave()
                    }
                }
            }
        }
        let limit = maxTextLength
        group.notify(queue: .main) {
            completion(ShareViewController.capped(parts.joined(separator: "\n"), to: limit))
        }
    }

    private func appendToInbox(_ text: String) {
        guard let container = FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: appGroupId) else { return }
        let url = container.appendingPathComponent(inboxFileName)
        var entries: [String] = []
        // A file larger than a full inbox was not written by this extension: start over instead of reading it.
        let maxInboxBytes = maxPendingEntries * maxTextLength * 4 + 64 * 1024
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        if size <= maxInboxBytes,
           let data = try? Data(contentsOf: url),
           let existing = try? JSONDecoder().decode([String].self, from: data) {
            entries = existing
        }
        guard entries.count < maxPendingEntries else { return }
        entries.append(text)
        if let data = try? JSONEncoder().encode(entries) {
            try? data.write(to: url, options: .atomic)
        }
    }

    /// Cuts after `limit` UTF-16 units without splitting a character.
    private static func capped(_ text: String, to limit: Int) -> String {
        guard text.utf16.count > limit else { return text }
        var result = ""
        var used = 0
        for character in text {
            let length = character.utf16.count
            if used + length > limit { break }
            result.append(character)
            used += length
        }
        return result
    }
}
