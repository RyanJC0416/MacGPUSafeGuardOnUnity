# Update Troubleshooting

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
