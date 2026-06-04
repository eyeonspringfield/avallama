#!/usr/bin/env bash
set -euo pipefail

log_ts() { date -u +"%Y-%m-%dT%H:%M:%SZ"; }
log()    { printf '%s [INFO] %s\n' "$(log_ts)" "$*"; }

log "Starting macOS package script"

VERSION="${1:-0.0.0}"
log "Using version: $VERSION"

PROJECT="avallama/avallama.csproj"
NATIVE_SRC="native/macos/FullScreenCheck.m"
DYLIB_OUTPUT="./libFullScreenCheck.dylib"

log "Checking for native source code"
# checks if the native macOS source code exists
if [ ! -f "$NATIVE_SRC" ]; then
    echo "ERROR: Native source file not found at $NATIVE_SRC"
    exit 1
fi

log "Compiling native macOS source code"
# compiles native macOS source code to create a universal dylib binary
clang -dynamiclib -framework Cocoa -arch x86_64 -arch arm64 -o "$DYLIB_OUTPUT" "$NATIVE_SRC"

log "Running dotnet publish for osx-x64"
dotnet publish "$PROJECT" -v n -c Release -r osx-x64 --self-contained true -o mac-dist-x64
log "Running dotnet publish for osx-arm64"
dotnet publish "$PROJECT" -v n -c Release -r osx-arm64 --self-contained true -o mac-dist-arm64

create_app_structure() {
  local arch_dir="$1"
  local output_app="$2"

  mkdir -p "$output_app/Contents/"{_CodeSignature,MacOS,Resources}
  cp -a "$arch_dir"/. "$output_app/Contents/MacOS/"
  cp avallama/Assets/Avallama.icns "$output_app/Contents/Resources"

  log "[create_app_structure()] Adding universal dylib to .app for ${arch_dir}"
  # includes the compiled universal dylib in the .app
  cp "$DYLIB_OUTPUT" "$output_app/Contents/MacOS/"
  chmod +x "$output_app/Contents/MacOS/libFullScreenCheck.dylib"

  log "[create_app_structure()] Creating Info.plist for ${arch_dir}"
  cat > "$output_app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>CFBundleIconFile</key>
    <string>Avallama.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.4foureyes.avallama</string>
    <key>CFBundleName</key>
    <string>Avallama</string>
    <key>CFBundleDisplayName</key>
    <string>Avallama</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.12</string>
    <key>CFBundleExecutable</key>
    <string>avallama</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>NSHighResolutionCapable</key>
    <true/>
  </dict>
</plist>
EOF
}

create_installer_dmg() {
    local app_name="Avallama.app"
    local arch="$1"
    local dmg_name="avallama_${VERSION}_osx_${arch}.dmg"
    local vol_name="Avallama Installer"

    # delete previous dmg file
    rm -f "$dmg_name"

    # volname: set volume name (displayed in the Finder sidebar and window title)
    # window-pos: set position the folder window
    # window-size: set size of the folder window
    # icon-size: set window icons size (up to 128)
    # background: set folder background image (provide png, gif, jpg)
    # icon: set position of the file's icon
    # app-drop-link: creates a drop link to Applications and positions it
    # hide-extension: hides the '.app' extension for a cleaner look
    # no-internet-enable: disable automatic mount&copy

    create-dmg \
      --volname "$vol_name" \
      --window-pos 200 120 \
      --window-size 660 400 \
      --icon-size 100 \
      --icon "$app_name" 180 170 \
      --hide-extension "$app_name" \
      --app-drop-link 480 170 \
      --no-internet-enable \
      "$dmg_name" \
      "$app_name"
}

# x64
rm -rf Avallama.app
log "Creating .app structure for x64"
create_app_structure "mac-dist-x64" "Avallama.app"
# zip -r "avallama_${VERSION}_osx_x64.zip" Avallama.app
log "Creating DMG installer for x64"
create_installer_dmg "x64"

# arm64
rm -rf Avallama.app
log "Creating .app structure for arm64"
create_app_structure "mac-dist-arm64" "Avallama.app"
# zip -r "avallama_${VERSION}_osx_arm64.zip" Avallama.app
log "Creating DMG installer for arm64"
create_installer_dmg "arm64"

# delete leftover files
log "Cleaning up temporary files"
rm -rf Avallama.app
rm -rf mac-dist-*
rm -rf $DYLIB_OUTPUT

log "Done: Created avallama_${VERSION}_osx_x64.dmg and avallama_${VERSION}_osx_arm64.dmg"
