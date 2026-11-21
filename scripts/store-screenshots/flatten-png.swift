// Removes the alpha channel from store screenshots (README "App Store screenshots"): App Store Connect rejects
// PNGs with alpha, and simctl writes RGBA. Each image is drawn onto an opaque background in an RGB context
// without alpha and written back in place as PNG. The captures are opaque already, so the background only
// matters for pixels that are not.
//
// Usage: swift flatten-png.swift <dark|light> <file.png>...
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let arguments = Array(CommandLine.arguments.dropFirst())
guard let appearance = arguments.first, ["dark", "light"].contains(appearance), arguments.count > 1 else {
    FileHandle.standardError.write("usage: flatten-png.swift <dark|light> <file.png>...\n".data(using: .utf8)!)
    exit(2)
}
let background: CGFloat = appearance == "light" ? 1 : 0
let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!
var failed = 0

func flatten(_ path: String) -> Bool {
    let url = URL(fileURLWithPath: path) as CFURL
    guard let source = CGImageSourceCreateWithURL(url, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return false }
    let space = image.colorSpace.flatMap { $0.model == .rgb ? $0 : nil } ?? sRGB
    guard let context = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                                  bytesPerRow: 0, space: space,
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return false }
    let rect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
    context.setFillColor(CGColor(colorSpace: space, components: [background, background, background, 1]) ?? CGColor(gray: background, alpha: 1))
    context.fill(rect)
    context.draw(image, in: rect)
    guard let flat = context.makeImage(),
          let destination = CGImageDestinationCreateWithURL(url, UTType.png.identifier as CFString, 1, nil) else { return false }
    CGImageDestinationAddImage(destination, flat, nil)
    return CGImageDestinationFinalize(destination)
}

for path in arguments.dropFirst() where !flatten(path) {
    FileHandle.standardError.write("could not flatten \(path)\n".data(using: .utf8)!)
    failed += 1
}
exit(failed == 0 ? 0 : 1)
