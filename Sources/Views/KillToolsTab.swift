import SwiftUI

struct KillToolsTab: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Process Kill")
                    .font(.headline)
                Spacer()
                Button("Refresh list") { state.refreshProcessList() }
            }

            HStack(spacing: 12) {
                Button("Kill Unity Editor") {
                    state.killUnityEditor()
                }
                .controlSize(.large)
                .tint(.red)

                Button("Kill Unity Hub") {
                    state.killUnityHub()
                }
                .controlSize(.large)
                .tint(.red)

                Spacer()
            }

            Text("Forcefully terminate running Unity processes.")
                .font(.caption)
                .foregroundColor(.secondary)

            if !state.processList.isEmpty {
                ScrollView {
                    Text(state.processList)
                        .font(.system(.caption, design: .monospaced))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                        .textSelection(.enabled)
                }
                .background(Color(NSColor.textBackgroundColor))
                .cornerRadius(6)
                .frame(height: 120)
            }

            Spacer()
        }
        .padding(12)
        .onAppear {
            state.refreshProcessList()
        }
    }
}
