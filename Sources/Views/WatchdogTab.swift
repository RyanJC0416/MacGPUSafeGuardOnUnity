import SwiftUI
import AppKit

struct WatchdogTab: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Toggle(
                    state.watchdogStatus.running ? "Watchdog: ON" : "Watchdog: OFF",
                    isOn: Binding(
                        get: { state.watchdogStatus.running },
                        set: { isOn in
                            if isOn {
                                state.activateWatchdog()
                            } else {
                                state.deactivateWatchdog()
                            }
                        }
                    )
                )
                .toggleStyle(.switch)
                .tint(.green)
                Spacer()
                Button("Refresh") { state.refreshWatchdog() }
            }

            if state.pathsDriftedFromRunningWatchdog {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundColor(.orange)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Paths changed since watchdog started")
                            .font(.caption).bold()
                        Text("The running watchdog still uses the old project / Unity binary. Deactivate then Activate to apply the new paths.")
                            .font(.caption2)
                            .foregroundColor(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer()
                }
                .padding(8)
                .background(Color.orange.opacity(0.15))
                .cornerRadius(6)
            }

            HStack(alignment: .top) {
                Text("Status:").bold().frame(width: 60, alignment: .leading)
                Text(state.watchdogStatus.raw)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                Spacer()
            }

            if let evt = state.lastKillEvent {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "bolt.shield.fill")
                        .foregroundColor(Self.killEventColor(for: evt.date))
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Last freeze kill: \(Self.absoluteFormatter.string(from: evt.date))   \(Self.relativeAge(of: evt.date))")
                            .font(.caption).bold()
                        Text(evt.reason)
                            .font(.caption2)
                            .foregroundColor(.secondary)
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer()
                }
                .padding(8)
                .background(Self.killEventColor(for: evt.date).opacity(0.12))
                .cornerRadius(6)
            }

            if !state.watchdogLastError.isEmpty {
                Text(state.watchdogLastError)
                    .foregroundColor(.red)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
            }

            HStack {
                Text("Recent log").font(.headline)
                Spacer()
                Button("Clear") { state.watchdogLogTail = "" }
                    .controlSize(.small)
            }
            ScrollViewReader { proxy in
                ScrollView {
                    Text(state.watchdogLogTail.isEmpty ? "(empty)" : state.watchdogLogTail)
                        .id("logBottom")
                        .font(.system(.caption, design: .monospaced))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                        .textSelection(.enabled)
                }
                .onChange(of: state.watchdogLogTail) {
                    withAnimation {
                        proxy.scrollTo("logBottom", anchor: .bottom)
                    }
                }
                .onAppear {
                    proxy.scrollTo("logBottom", anchor: .bottom)
                }
            }
            .background(Color(NSColor.textBackgroundColor))
            .cornerRadius(6)
            .overlay(
                RoundedRectangle(cornerRadius: 6)
                    .stroke(Color.gray.opacity(0.3), lineWidth: 1)
            )
            .frame(height: 180)
        }
        .padding(12)
    }

    private static let absoluteFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    private static func relativeAge(of date: Date) -> String {
        let secs = Int(Date().timeIntervalSince(date))
        if secs < 60 { return "(\(secs)s ago)" }
        if secs < 3600 { return "(\(secs / 60)m ago)" }
        if secs < 86400 { return "(\(secs / 3600)h ago)" }
        return "(\(secs / 86400)d ago)"
    }

    private static func killEventColor(for date: Date) -> Color {
        Date().timeIntervalSince(date) < 60 ? .red : .orange
    }
}
