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
                Button("Settings") {
                    openWindow(id: "settings")
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)

            Divider()

            ScrollView {
                VStack(spacing: 16) {
                    WatchdogTab()
                    KillToolsTab()
                }
                .padding(16)
            }
        }
        .frame(minWidth: 740, minHeight: 580)
        .onAppear {
            state.refreshWatchdog()
        }
    }
}
