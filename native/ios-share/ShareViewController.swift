import UIKit
import UniformTypeIdentifiers

// "StudyLife" share extension (share sheet): accepts shared text and/or URLs,
// appends them as a JSON array to the App Group inbox (SharedNoteIntake.DrainIosInbox reads
// it on the next app launch/foreground transition and turns it into notes) and closes
// itself right away - deliberately without its own compose UI. A share extension isn't
// allowed to open the main app itself (openURL is locked down there), hence the inbox detour.
final class ShareViewController: UIViewController {
    private let appGroupId = "group.app.studylife.mobile"
    private let inboxFileName = "shared-note-inbox.json"

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
        group.notify(queue: .main) {
            completion(parts.joined(separator: "\n"))
        }
    }

    private func appendToInbox(_ text: String) {
        guard let container = FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: appGroupId) else { return }
        let url = container.appendingPathComponent(inboxFileName)
        var entries: [String] = []
        if let data = try? Data(contentsOf: url),
           let existing = try? JSONDecoder().decode([String].self, from: data) {
            entries = existing
        }
        entries.append(text)
        if let data = try? JSONEncoder().encode(entries) {
            try? data.write(to: url, options: .atomic)
        }
    }
}
