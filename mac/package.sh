#!/bin/bash
# Package the published app into a double-clickable macOS .app bundle.
#
#   ./package.sh
#
# Produces dist/AltsTools.app — self-contained (bundles the .NET runtime),
# with the native injector + payload.dylib, an icon, and ad-hoc code signing.
# The injector is signed with the debugger entitlement so task_for_pid works
# (still requires SIP disabled + admin auth at inject time, like Phase 1).
set -e
cd "$(dirname "$0")"

APP="dist/AltsTools.app"
PUB="publish"
ENT="../mac-inject-poc/injector.entitlements"

echo "[*] cleaning dist"
rm -rf dist
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "[*] copying published payload"
cp -R "$PUB"/* "$APP/Contents/MacOS/"

echo "[*] writing Info.plist"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>ALTs Tools</string>
  <key>CFBundleDisplayName</key><string>ALTs Tools</string>
  <key>CFBundleIdentifier</key><string>com.altstools.mac</string>
  <key>CFBundleVersion</key><string>1.0.2</string>
  <key>CFBundleShortVersionString</key><string>1.0.2</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>AltsTools</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
PLIST

# Build an .icns from the existing .ico if iconutil/sips are available.
if [ -f "Assets/coolpig.ico" ]; then
  echo "[*] building app icon"
  TMPICON=$(mktemp -d)
  if sips -s format png "Assets/coolpig.ico" --out "$TMPICON/icon.png" >/dev/null 2>&1; then
    mkdir -p "$TMPICON/AppIcon.iconset"
    for sz in 16 32 64 128 256 512; do
      sips -z $sz $sz "$TMPICON/icon.png" --out "$TMPICON/AppIcon.iconset/icon_${sz}x${sz}.png" >/dev/null 2>&1 || true
    done
    iconutil -c icns "$TMPICON/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMPICON"
fi

echo "[*] code signing"
# IMPORTANT ORDER: sign the whole bundle FIRST (--deep ad-hoc), THEN sign the
# injector with its debugger entitlement LAST. Doing --deep after the injector
# re-signs (and wipes the entitlement off) the injector, breaking task_for_pid.
codesign -s - --force --deep "$APP" 2>/dev/null || true
if [ -f "$APP/Contents/MacOS/injector" ] && [ -f "$ENT" ]; then
  codesign -s - --force --entitlements "$ENT" "$APP/Contents/MacOS/injector"
fi

echo "[+] done -> $APP"
echo "    Run: open $APP   (or double-click it in Finder)"
