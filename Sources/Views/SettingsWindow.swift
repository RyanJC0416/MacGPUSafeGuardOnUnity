import SwiftUI
import AppKit

struct SettingsWindow: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        VStack(spacing: 16) {
            Text("Settings")
                .font(.title2)
                .bold()

            ScrollView {
                VStack(spacing: 16) {
                    // Base Config
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Base Config")
                            .font(.headline)
                        PathRow(label: "P4 binary", path: $state.p4Binary, kind: .file)
                        PathRow(label: "Unity project", path: $state.unityProjectPath, kind: .directory)
                    }
                    .padding(12)
                    .background(Color(NSColor.controlBackgroundColor))
                    .cornerRadius(8)

                    // Inject Unity Script
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Inject Unity Script")
                            .font(.headline)

                        PathRow(label: "Unity editor", path: $state.unityEditorBinary, kind: .file)

                        HStack(alignment: .top) {
                            Text("P4 env:")
                                .bold()
                                .frame(width: 110, alignment: .leading)
                            if let err = state.p4Error {
                                Text(err)
                                    .foregroundColor(.red)
                                    .font(.system(.caption, design: .monospaced))
                                    .textSelection(.enabled)
                            } else {
                                Text("user=\(env(state.p4Env.user))  client=\(env(state.p4Env.client))  server=\(env(state.p4Env.serverAddress))")
                                    .font(.system(.caption, design: .monospaced))
                                    .textSelection(.enabled)
                            }
                            Spacer()
                            Button("Refresh") { state.refreshP4() }
                        }

                        HStack {
                            Text("Default CL:")
                                .bold()
                                .frame(width: 110, alignment: .leading)
                            Picker("", selection: $state.defaultChangelist) {
                                Text("(none)").tag("")
                                ForEach(state.changelists) { cl in
                                    Text(cl.label).tag(cl.id)
                                }
                                if !state.defaultChangelist.isEmpty
                                    && !state.changelists.contains(where: { $0.id == state.defaultChangelist }) {
                                    Text("\(state.defaultChangelist) (manual)").tag(state.defaultChangelist)
                                }
                            }
                            .frame(maxWidth: 480)
                            .labelsHidden()
                            Spacer()
                        }

                        Divider()

                        HStack {
                            Text("Templates").bold()
                            Spacer()
                            Button("Capture / Re-capture from project") {
                                state.captureTemplates()
                            }
                        }

                        VStack(alignment: .leading, spacing: 4) {
                            ForEach(state.injectorTargets) { t in
                                HStack(spacing: 8) {
                                    Image(systemName: iconForStatus(t.status))
                                        .foregroundColor(colorForStatus(t.status))
                                        .frame(width: 18)
                                    Text(t.basename)
                                        .font(.system(.body, design: .monospaced))
                                    Spacer()
                                    Text(t.status.rawValue)
                                        .font(.system(.caption, design: .monospaced))
                                        .foregroundColor(colorForStatus(t.status))
                                }
                            }
                        }

                        HStack {
                            Button("Apply unity inner safe") { state.applyInnerSafe() }
                                .buttonStyle(.borderedProminent)
                                .disabled(!canApply())
                            Button("Re-check") { state.refreshInjector() }
                            if !canApply() {
                                Text(applyDisabledReason())
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                            }
                        }

                        if !state.lastApplyResults.isEmpty {
                            Divider()
                            Text("Result").font(.headline)
                            ScrollView {
                                VStack(alignment: .leading, spacing: 4) {
                                    ForEach(state.lastApplyResults) { r in
                                        HStack(alignment: .top, spacing: 8) {
                                            Image(systemName: r.ok ? "checkmark.circle.fill" : "xmark.circle.fill")
                                                .foregroundColor(r.ok ? .green : .red)
                                                .frame(width: 18)
                                            VStack(alignment: .leading, spacing: 2) {
                                                Text(r.basename).font(.system(.body, design: .monospaced))
                                                Text(r.action)
                                                    .font(.system(.caption, design: .monospaced))
                                                    .foregroundColor(.secondary)
                                                    .textSelection(.enabled)
                                            }
                                            Spacer()
                                        }
                                    }
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(8)
                            }
                            .background(Color(NSColor.textBackgroundColor))
                            .cornerRadius(6)
                            .frame(minHeight: 120, maxHeight: 220)
                        }
                    }
                    .padding(12)
                    .background(Color(NSColor.controlBackgroundColor))
                    .cornerRadius(8)
                }
                .padding(16)
            }
        }
        .frame(minWidth: 700, minHeight: 500)
        .onAppear {
            if state.p4Env.user.isEmpty { state.refreshP4() }
            state.refreshInjector()
        }
    }

    private func env(_ s: String) -> String { s.isEmpty ? "?" : s }

    private func iconForStatus(_ s: FileStatus) -> String {
        switch s {
        case .inSync: return "checkmark.circle.fill"
        case .drift: return "exclamationmark.triangle.fill"
        case .missing: return "xmark.circle.fill"
        case .templateMissing: return "questionmark.circle.fill"
        }
    }

    private func colorForStatus(_ s: FileStatus) -> Color {
        switch s {
        case .inSync: return .green
        case .drift: return .orange
        case .missing: return .red
        case .templateMissing: return .gray
        }
    }

    private func canApply() -> Bool {
        guard !state.unityProjectPath.isEmpty else { return false }
        guard !state.defaultChangelist.isEmpty else { return false }
        let templatesOK = state.injectorTargets.allSatisfy { $0.status != .templateMissing }
        let needsAction = state.injectorTargets.contains { $0.status == .drift || $0.status == .missing }
        return templatesOK && needsAction
    }

    private func applyDisabledReason() -> String {
        if state.unityProjectPath.isEmpty { return "set Unity project path first" }
        if state.defaultChangelist.isEmpty { return "select a default CL first" }
        if state.injectorTargets.contains(where: { $0.status == .templateMissing }) {
            return "press Capture first"
        }
        if !state.injectorTargets.contains(where: { $0.status == .drift || $0.status == .missing }) {
            return "all in sync — nothing to apply"
        }
        return ""
    }
}
