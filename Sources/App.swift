import SwiftUI
import AppKit
import Combine

@main
@MainActor
struct GpuSafeGuardApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var state = AppState()

    init() {
        delegate.setupStatusItem(with: state)
    }

    var body: some Scene {
        WindowGroup(id: "main") {
            MainWindow()
                .environmentObject(state)
                .frame(minWidth: 740, minHeight: 580)
        }
        .defaultSize(width: 780, height: 720)
        .defaultPosition(.center)

        WindowGroup(id: "settings") {
            SettingsWindow()
                .environmentObject(state)
        }
        .defaultSize(width: 720, height: 580)
        .defaultPosition(.center)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    var state: AppState?
    private var statusItem: NSStatusItem?
    private var cancellables = Set<AnyCancellable>()

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.activate(ignoringOtherApps: true)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
            for win in NSApp.windows where win.canBecomeKey {
                Self.recoverIfOffscreen(win)
            }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag {
            NSApp.activate(ignoringOtherApps: true)
            for win in NSApp.windows where win.canBecomeKey {
                Self.recoverIfOffscreen(win)
                win.makeKeyAndOrderFront(nil)
            }
        }
        return true
    }

    func setupStatusItem(with state: AppState) {
        self.state = state

        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let btn = item.button {
            btn.image = MenuBarIconRenderer.image(for: state.iconState)
            btn.imagePosition = .imageOnly
            btn.imageScaling = .scaleProportionallyDown
            btn.toolTip = "GpuSafeGuard"
        }
        item.menu = buildMenu()
        self.statusItem = item

        state.objectWillChange
            .receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.updateMenu() }
            .store(in: &cancellables)
    }

    private func buildMenu() -> NSMenu {
        let menu = NSMenu()

        let show = NSMenuItem(title: "Show Window", action: #selector(showWindow), keyEquivalent: "")
        show.target = self
        menu.addItem(show)

        let killInfo = NSMenuItem(title: "Last freeze kill: --", action: nil, keyEquivalent: "")
        killInfo.isEnabled = false
        menu.addItem(killInfo)

        menu.addItem(NSMenuItem.separator())

        let toggle = NSMenuItem(title: "Watchdog: OFF", action: #selector(toggleWatchdog), keyEquivalent: "")
        toggle.target = self
        menu.addItem(toggle)

        let refresh = NSMenuItem(title: "Refresh status", action: #selector(refreshWatchdog), keyEquivalent: "")
        refresh.target = self
        menu.addItem(refresh)

        menu.addItem(NSMenuItem.separator())

        let killEditor = NSMenuItem(title: "Kill Unity Editor", action: #selector(killEditor), keyEquivalent: "")
        killEditor.target = self
        menu.addItem(killEditor)

        let killHub = NSMenuItem(title: "Kill Unity Hub", action: #selector(killHub), keyEquivalent: "")
        killHub.target = self
        menu.addItem(killHub)

        menu.addItem(NSMenuItem.separator())

        let checkUpdate = NSMenuItem(title: "Check for Updates…", action: #selector(checkForUpdatesMenu), keyEquivalent: "")
        checkUpdate.target = self
        menu.addItem(checkUpdate)

        menu.addItem(NSMenuItem.separator())

        let quit = NSMenuItem(title: "Quit GpuSafeGuard", action: #selector(quitApp), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)

        return menu
    }

    private func updateMenu() {
        guard let menu = statusItem?.menu, let state = state else { return }
        if let btn = statusItem?.button {
            btn.image = MenuBarIconRenderer.image(for: state.iconState)
        }
        if let killInfo = menu.item(at: 1) {
            if let evt = state.lastKillEvent {
                killInfo.title = "Last freeze kill: \(Self.shortFmt.string(from: evt.date))"
            } else {
                killInfo.title = "Last freeze kill: --"
            }
        }
        if let toggle = menu.item(at: 3) {
            toggle.title = state.watchdogStatus.running ? "Watchdog: ON" : "Watchdog: OFF"
            toggle.state = state.watchdogStatus.running ? .on : .off
        }
        if let updateItem = menu.item(withTitle: "Check for Updates…") {
            switch state.updateStatus {
            case .checking:
                updateItem.title = "Checking for Updates…"
                updateItem.isEnabled = false
            case .available(let version, _):
                updateItem.title = "Update to v\(version)"
                updateItem.isEnabled = true
            case .downloading, .installing:
                updateItem.title = "Updating…"
                updateItem.isEnabled = false
            default:
                updateItem.title = "Check for Updates…"
                updateItem.isEnabled = true
            }
        }
    }

    @objc private func showWindow() {
        NSApp.activate(ignoringOtherApps: true)
        for win in NSApp.windows where win.canBecomeKey {
            win.makeKeyAndOrderFront(nil)
            Self.recoverIfOffscreen(win)
        }
    }

    @objc private func toggleWatchdog() {
        if state?.watchdogStatus.running == true {
            state?.deactivateWatchdog()
        } else {
            state?.activateWatchdog()
        }
    }

    @objc private func refreshWatchdog() {
        state?.refreshWatchdog()
    }

    @objc private func killEditor() {
        state?.killUnityEditor()
    }

    @objc private func killHub() {
        state?.killUnityHub()
    }

    @objc private func checkForUpdatesMenu() {
        state?.checkForUpdates()
    }

    @objc private func quitApp() {
        NSApplication.shared.terminate(nil)
    }

    func applicationWillTerminate(_ notification: Notification) {
        if state?.watchdogStatus.running == true {
            _ = Watchdog.stop()
        }
    }

    private static let shortFmt: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "MM-dd HH:mm"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    private static func recoverIfOffscreen(_ window: NSWindow) {
        let frame = window.frame
        let onAnyScreen = NSScreen.screens.contains { $0.visibleFrame.intersects(frame) }
        guard !onAnyScreen, let main = NSScreen.main else { return }
        let v = main.visibleFrame
        let newOrigin = NSPoint(
            x: v.midX - frame.width / 2,
            y: v.midY - frame.height / 2
        )
        window.setFrameOrigin(newOrigin)
    }
}

enum MenuBarIconRenderer {
    static func image(for state: WatchdogIconState) -> NSImage {
        let s: CGFloat = 18
        let img = NSImage(size: NSSize(width: s, height: s))
        img.isTemplate = (state != .recentKill)
        img.lockFocus()

        let ctx = NSGraphicsContext.current!.cgContext
        let color: NSColor = state == .recentKill ? .systemRed : .controlTextColor
        ctx.setStrokeColor(color.cgColor)
        ctx.setLineWidth(1.5)

        let path = CGMutablePath()
        path.move(to: CGPoint(x: s * 0.50, y: s * 0.93))
        path.addCurve(to: CGPoint(x: s * 0.10, y: s * 0.78), control1: CGPoint(x: s * 0.32, y: s * 0.93), control2: CGPoint(x: s * 0.10, y: s * 0.88))
        path.addLine(to: CGPoint(x: s * 0.10, y: s * 0.45))
        path.addCurve(to: CGPoint(x: s * 0.50, y: s * 0.05), control1: CGPoint(x: s * 0.10, y: s * 0.22), control2: CGPoint(x: s * 0.28, y: s * 0.05))
        path.addCurve(to: CGPoint(x: s * 0.90, y: s * 0.45), control1: CGPoint(x: s * 0.72, y: s * 0.05), control2: CGPoint(x: s * 0.90, y: s * 0.22))
        path.addLine(to: CGPoint(x: s * 0.90, y: s * 0.78))
        path.addCurve(to: CGPoint(x: s * 0.50, y: s * 0.93), control1: CGPoint(x: s * 0.90, y: s * 0.88), control2: CGPoint(x: s * 0.68, y: s * 0.93))
        path.closeSubpath()

        ctx.addPath(path)
        ctx.strokePath()

        let str = "MAC" as NSString
        let font = NSFont.systemFont(ofSize: 5.5, weight: .heavy)
        let attrs: [NSAttributedString.Key: Any] = [
            .font: font,
            .foregroundColor: color,
            .kern: -0.3,
        ]
        let textSize = str.size(withAttributes: attrs)
        let textRect = NSRect(
            x: (s - textSize.width) / 2,
            y: (s - textSize.height) / 2 - 0.5,
            width: textSize.width,
            height: textSize.height
        )
        str.draw(in: textRect, withAttributes: attrs)

        img.unlockFocus()
        return img
    }
}
