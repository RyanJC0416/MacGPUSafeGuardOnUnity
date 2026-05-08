// Generates shield-with-"MAC" icons for GpuSafeGuard.
// Usage: swift generate_icons.swift <output-dir>
//   produces: <output-dir>/AppIcon.iconset/* (and AppIcon.icns next to it via build.sh)
//             <output-dir>/menubar_on{,@2x}.png
//             <output-dir>/menubar_off{,@2x}.png
//             <output-dir>/menubar_kill{,@2x}.png

import AppKit
import CoreGraphics
import Foundation

func shieldPath(in size: CGFloat) -> NSBezierPath {
    let path = NSBezierPath()
    let p: (CGFloat, CGFloat) -> NSPoint = { x, y in
        NSPoint(x: x * size, y: y * size)
    }
    // start at top-center
    path.move(to: p(0.50, 0.93))
    // top-left rounded corner → straight down
    path.curve(to: p(0.10, 0.78), controlPoint1: p(0.32, 0.93), controlPoint2: p(0.10, 0.88))
    path.line(to: p(0.10, 0.45))
    // bottom point (left side curving in)
    path.curve(to: p(0.50, 0.05), controlPoint1: p(0.10, 0.22), controlPoint2: p(0.28, 0.05))
    // up the right (mirror of left)
    path.curve(to: p(0.90, 0.45), controlPoint1: p(0.72, 0.05), controlPoint2: p(0.90, 0.22))
    path.line(to: p(0.90, 0.78))
    // top-right rounded corner back to top-center
    path.curve(to: p(0.50, 0.93), controlPoint1: p(0.90, 0.88), controlPoint2: p(0.68, 0.93))
    path.close()
    return path
}

func renderIcon(size: CGFloat, fillColor: NSColor, withRoundedBackground: Bool, outlineOnly: Bool = false) -> Data? {
    let pixels = Int(size)
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: pixels,
        pixelsHigh: pixels,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 32
    ) else { return nil }

    NSGraphicsContext.saveGraphicsState()
    guard let ctx = NSGraphicsContext(bitmapImageRep: bitmap) else {
        NSGraphicsContext.restoreGraphicsState()
        return nil
    }
    NSGraphicsContext.current = ctx

    // Transparent canvas
    NSColor.clear.setFill()
    NSRect(x: 0, y: 0, width: size, height: size).fill()

    if withRoundedBackground {
        // squircle background for app icon
        let inset: CGFloat = size * 0.06
        let rect = NSRect(x: inset, y: inset, width: size - inset * 2, height: size - inset * 2)
        let radius = size * 0.20
        let bg = NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius)
        // soft gradient: light grey to slightly darker
        let gradient = NSGradient(starting: NSColor(white: 0.97, alpha: 1.0),
                                  ending: NSColor(white: 0.88, alpha: 1.0))
        gradient?.draw(in: bg, angle: -90)
        // 1pt border
        NSColor(white: 0.0, alpha: 0.1).setStroke()
        bg.lineWidth = max(1, size * 0.005)
        bg.stroke()
    }

    // shield path: scaled to ~80% of canvas, centered
    let shieldFrac: CGFloat = withRoundedBackground ? 0.74 : 0.96
    let shieldSize = size * shieldFrac
    let xOffset = (size - shieldSize) / 2
    let yOffset = (size - shieldSize) / 2
    let path = shieldPath(in: shieldSize)
    var transform = AffineTransform.identity
    transform.translate(x: xOffset, y: yOffset)
    path.transform(using: transform)

    if !outlineOnly {
        fillColor.setFill()
        path.fill()
    }
    // black outline
    NSColor.black.setStroke()
    path.lineWidth = max(1, size * (outlineOnly ? 0.025 : 0.015))
    path.stroke()

    // "MAC" text inside the shield
    let textColor = NSColor.black
    let fontSize = shieldSize * 0.30
    let weight: NSFont.Weight = size <= 32 ? .black : .heavy
    let font = NSFont.systemFont(ofSize: fontSize, weight: weight)
    let attrs: [NSAttributedString.Key: Any] = [
        .font: font,
        .foregroundColor: textColor,
        .kern: -fontSize * 0.04,
    ]
    let attr = NSAttributedString(string: "MAC", attributes: attrs)
    let textSize = attr.size()
    let textRect = NSRect(
        x: (size - textSize.width) / 2,
        y: yOffset + shieldSize * 0.45 - textSize.height / 2,
        width: textSize.width,
        height: textSize.height
    )
    attr.draw(in: textRect)

    NSGraphicsContext.restoreGraphicsState()

    return bitmap.representation(using: .png, properties: [:])
}

guard CommandLine.arguments.count >= 2 else {
    fputs("usage: generate_icons.swift <output-dir>\n", stderr)
    exit(2)
}
let outDir = URL(fileURLWithPath: CommandLine.arguments[1])
try? FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)

// === App icon ===
let iconsetDir = outDir.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: iconsetDir)
try? FileManager.default.createDirectory(at: iconsetDir, withIntermediateDirectories: true)

let appSizes: [(name: String, size: CGFloat)] = [
    ("icon_16x16.png", 16),
    ("icon_16x16@2x.png", 32),
    ("icon_32x32.png", 32),
    ("icon_32x32@2x.png", 64),
    ("icon_128x128.png", 128),
    ("icon_128x128@2x.png", 256),
    ("icon_256x256.png", 256),
    ("icon_256x256@2x.png", 512),
    ("icon_512x512.png", 512),
    ("icon_512x512@2x.png", 1024),
]
for (name, sz) in appSizes {
    if let data = renderIcon(size: sz, fillColor: NSColor.black, withRoundedBackground: true, outlineOnly: true) {
        try? data.write(to: iconsetDir.appendingPathComponent(name))
    } else {
        fputs("failed to render \(name)\n", stderr)
    }
}

print("icons written to \(outDir.path)")
