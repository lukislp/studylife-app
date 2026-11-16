import UIKit
import UniformTypeIdentifiers

// "StudyLife" share extension (share sheet): accepts shared text and/or URLs,
// appends them as a JSON array to the App Group inbox (SharedNoteIntake.DrainIosInbox reads
// it on the next app launch/foreground transition and turns it into notes) and closes
// itself right away - deliberately without its own compose UI. A share extension isn't
// allowed to open the main app itself (openURL is locked down there), hence the inbox detour.
//
// Bounded like the app side (IntakeLimits in src/StudyLife.App.Core): one shared text is cut after
// maxTextLength characters (UTF-16 units, as .NET counts them) and the inbox holds at most
// maxPendingEntries texts until the app took them; further shares are dropped.
final class ShareViewController: UIViewController {
    private let appGroupId = "group.app.studylife.mobile"
    private let inboxFileName = "shared-note-inbox.json"
    private let maxTextLength = 20_000
    private let maxPendingEntries = 20

    override func viewDidLoad() {
        super.viewDidLoad()
        collectSharedText { [weak self] text in
            if let text, !text.isEmpty { self?.appendToInbox(text) }
            self?.extensionContext?.completeRequest(returningItems: nil)
        }
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
