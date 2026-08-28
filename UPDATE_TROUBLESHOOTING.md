# Update Troubleshooting

## Problem: Check failed: Unzip failed (cannot find or open GpuSafeGuard_x.y.z.zip)

### Root Cause
`Updater.download` used `curl -sL` and only looked at stderr. When GitHub timed out, curl exited non-zero **without writing the zip** and `-s` hid the error. The updater then ran `unzip` on a missing file.

Seen with v1.8.4 → v1.8.5: `GpuSafeGuard_1.8.5/` existed (empty extract dir) but `GpuSafeGuard_1.8.5.zip` did not. GitHub asset download count stayed 0.

### What We Fixed (v1.8.6+)
- Check curl / unzip **exit codes**
- Retry download up to 3 times (`curl -fL --retry`)
- Refuse to unzip unless the zip exists and is large enough
- Confirm `GpuSafeGuard.app` is inside the archive

### Workaround on 1.8.4 / 1.8.5
Download `GpuSafeGuard.app.zip` from GitHub Releases and replace `/Applications/GpuSafeGuard.app`. Then Check for Updates again.

---

## Problem: Update Failed with "Read-only file system"

### Root Cause
macOS **App Translocation** security feature moves downloaded apps to a read-only temporary location (`/private/var/folders/.../AppTranslocation/...`), preventing in-place updates.

### Symptoms
- Clicking "Update" closes the app but doesn't install the new version
- Install log shows: `mv: ... Read-only file system`
- App path contains `/AppTranslocation/` or `/private/var/folders/.../T/`

### Solution
**Move the app to `/Applications/` before updating:**

1. Quit GpuSafeGuard
2. Open Finder and navigate to where you downloaded `GpuSafeGuard.app`
3. Drag `GpuSafeGuard.app` to `/Applications/`
4. Launch GpuSafeGuard from `/Applications/`
5. Now updates will work normally

### What We Fixed (v1.4.1+)
- ✅ **Detection**: App now detects when it's translocated
- ✅ **UI Warning**: Shows orange banner in Settings when updates are blocked
- ✅ **Friendly Error**: Explains the issue instead of silent failure
- ✅ **Disabled Buttons**: "Update" button is disabled when translocated

### Technical Details
The update script (`Updater.swift:install()`) now checks:
```swift
if appPath.contains("/AppTranslocation/") || 
   (appPath.contains("/private/var/folders/") && appPath.contains("/T/")) {
    return "Cannot update: App is running from a quarantined location..."
}
```

### References
- [App Translocation on macOS](https://developer.apple.com/library/archive/technotes/tn2206/_index.html)
- Install log: `~/Library/Application Support/MacGPUSafeGuard/updates/install.log`
