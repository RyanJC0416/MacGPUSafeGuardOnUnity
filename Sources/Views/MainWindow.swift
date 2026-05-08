import SwiftUI
import AppKit

enum PathPickerKind {
    case file, directory
}

struct PathRow: View {
    let label: String
    @Binding var path: String
    let kind: PathPickerKind

    var body: some View {
        HStack {
            Text(label)
                .frame(width: 110, alignment: .leading)
            TextField("", text: $path)
                .font(.system(.body, design: .monospaced))
                .textFieldStyle(.roundedBorder)
            Button("Browse…") {
                let panel = NSOpenPanel()
                panel.canChooseFiles = (kind == .file)
                panel.canChooseDirectories = (kind == .directory)
                panel.allowsMultipleSelection = false
                panel.resolvesAliases = true
                if !path.isEmpty {
                    let base = URL(fileURLWithPath: path)
                    panel.directoryURL = (kind == .directory) ? base : base.deletingLastPathComponent()
                }
                if panel.runModal() == .OK, let url = panel.url {
                    path = url.path
                }
            }
        }
    }
}

struct MainWindow: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("GpuSafeGuard")
                    .font(.title2)
                    .bold()
                Spacer()
                UpdateBadge()
                Button("Settings") {
                    openWindow(id: "settings")
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)

            Divider()

            VStack(spacing: 16) {
                WatchdogTab()
                KillToolsTab()
            }
            .padding(16)
        }
        .frame(minWidth: 740, minHeight: 700)
        .onAppear {
            state.refreshWatchdog()
        }
    }

    @ViewBuilder
    private func UpdateBadge() -> some View {
        switch state.updateStatus {
        case .checking:
            HStack(spacing: 4) {
                ProgressView().controlSize(.small)
                Text("Checking…").font(.caption)
            }
        case .available(let version, _):
            HStack(spacing: 4) {
                Image(systemName: "arrow.down.circle.fill")
                    .foregroundColor(.blue)
                Text("v\(version) available")
                    .font(.caption)
                    .foregroundColor(.blue)
                Button("Update") {
                    if case .available(let v, let url) = state.updateStatus {
                        state.downloadAndInstallUpdate(version: v, url: url)
                    }
                }
                .controlSize(.small)
                .buttonStyle(.borderedProminent)
            }
        case .downloading:
            HStack(spacing: 4) {
                ProgressView().controlSize(.small)
                Text("Downloading…").font(.caption)
            }
        case .installing:
            HStack(spacing: 4) {
                ProgressView().controlSize(.small)
                Text("Installing…").font(.caption)
            }
        case .upToDate:
            HStack(spacing: 4) {
                Image(systemName: "checkmark.circle.fill")
                    .foregroundColor(.green)
                Text("v\(Updater.currentVersion())")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
        case .error(let msg):
            HStack(spacing: 4) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundColor(.orange)
                Text(msg)
                    .font(.caption)
                    .foregroundColor(.orange)
                    .lineLimit(1)
            }
        default:
            EmptyView()
        }
    }
}
