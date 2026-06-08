#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

APP="GpuSafeGuard.app"
NAME="GpuSafeGuard"
BUNDLE_ID="local.tools.gpusafeguard"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

echo "Generating icons…"
ICON_TMP="$(mktemp -d /tmp/gsg-icons.XXXXXX)"
swift tools/generate_icons.swift "$ICON_TMP" >/dev/null
iconutil -c icns -o "$APP/Contents/Resources/AppIcon.icns" "$ICON_TMP/AppIcon.iconset"
rm -rf "$ICON_TMP"

echo "Compiling…"
SWIFT_SOURCES=()
while IFS= read -r -d '' f; do
  SWIFT_SOURCES+=("$f")
done < <(find Sources -name '*.swift' -print0)

swiftc -O -target arm64-apple-macos15 -parse-as-library \
  -framework SwiftUI -framework AppKit -framework Foundation \
  -o "$APP/Contents/MacOS/$NAME" "${SWIFT_SOURCES[@]}"

echo "Copying Resources…"
cp Resources/watchdog.sh.tmpl "$APP/Contents/Resources/"
cp kill-unity.sh "$APP/Contents/Resources/"
chmod +x "$APP/Contents/Resources/kill-unity.sh"

# Copy Unity injection templates (runtime GPU safe — flat templates/)
if [ -d "Resources/templates" ]; then
  mkdir -p "$APP/Contents/Resources/templates"
  cp -R Resources/templates/* "$APP/Contents/Resources/templates/"
fi

# SceneGuard core + diagnostics (separate apply channels — preserve Assets/ tree)
for bundle in scene-guard scene-guard-tools; do
  if [ -d "Resources/$bundle" ]; then
    mkdir -p "$APP/Contents/Resources/$bundle"
    cp -R "Resources/$bundle/"* "$APP/Contents/Resources/$bundle/"
  fi
done

echo "Writing Info.plist…"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>$NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleName</key><string>$NAME</string>
    <key>CFBundleDisplayName</key><string>GpuSafeGuard</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>CFBundleShortVersionString</key><string>1.8.2</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>15.0</string>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
    <key>NSRemovableVolumesUsageDescription</key><string>GpuSafeGuard 在扫描 Unity 项目和快照目录时可能遍历到挂载卷,需要授权读取以正常清理快照。</string>
    <key>NSDesktopFolderUsageDescription</key><string>GpuSafeGuard 可能需要读取桌面上的 Unity 项目文件。</string>
    <key>NSDocumentsFolderUsageDescription</key><string>GpuSafeGuard 可能需要读取文档目录中的 Unity 项目文件。</string>
    <key>NSDownloadsFolderUsageDescription</key><string>GpuSafeGuard 可能需要读取下载目录中的更新包。</string>
</dict>
</plist>
EOF

echo "Writing entitlements…"
cat > "$APP/Contents/Resources/entitlements.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.security.files.user-selected.read-write</key><true/>
    <key>com.apple.security.files.bookmarks.app-scope</key><true/>
    <key>com.apple.security.network.client</key><true/>
</dict>
</plist>
EOF

echo "Ad-hoc signing…"
codesign --force --sign - --entitlements "$APP/Contents/Resources/entitlements.plist" "$APP" 2>/dev/null || \
  codesign --force --sign - "$APP" 2>/dev/null || true

echo "Built: $APP ($(du -sh "$APP" | cut -f1))"
