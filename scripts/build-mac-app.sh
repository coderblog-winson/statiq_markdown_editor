#!/bin/bash
# =====================================================
#  build-mac-app.sh — package the editor as a Mac .app
#
#  Produces dist/Statiq Markdown Editor.app, a double-clickable
#  bundle containing:
#    - Electron host (MacOS/Electron + Frameworks/)
#    - Our main.js + package.json (Resources/app/)
#    - The self-contained .NET server (Resources/server/)
#    - Bundled wwwroot/, appsettings.json, etc.
#
#  Prerequisites:
#    - .NET 10 SDK (or .NET 9+)
#    - Node 18+ (for `electron` from node_modules)
#
#  First-time setup:
#    npm install   # installs electron + electron-builder
#
#  Build:
#    bash scripts/build-mac-app.sh
#
#  Or, to just publish the .NET binary (without .app):
#    dotnet publish Editor.csproj -c Release -r osx-arm64 \
#        --self-contained -p:PublishSingleFile=true \
#        -p:IncludeNativeLibrariesForSelfExtract=true \
#        -o ./dist/server
# =====================================================
set -euo pipefail

cd "$(dirname "$0")/.."
PROJECT_ROOT="$(pwd)"

APP_NAME="Statiq Markdown Editor"
APP_BUNDLE="$PROJECT_ROOT/dist/$APP_NAME.app"
ELECTRON_APP="$PROJECT_ROOT/node_modules/electron/dist/Electron.app"
DOTNET_PUBLISH_DIR="$PROJECT_ROOT/dist/server"
DOTNET_EXE_NAME="StatiqMarkdownEditor"

if [ ! -d "$ELECTRON_APP" ]; then
    echo "❌ Electron not found at $ELECTRON_APP"
    echo "   Run 'npm install' first."
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "❌ dotnet not found in PATH"
    echo "   Add /usr/local/share/dotnet to PATH and try again."
    exit 1
fi

ARCH="$(uname -m)"
if [ "$ARCH" != "arm64" ] && [ "$ARCH" != "x86_64" ]; then
    echo "❌ unsupported architecture: $ARCH (only arm64/x86_64)"
    exit 1
fi
RID="osx-$([ "$ARCH" = "arm64" ] && echo arm64 || echo x64)"

echo "==> 1/4 Publishing self-contained .NET binary ($RID)"
mkdir -p dist
# Wipe dist/ entirely (NOT just the .app bundle). Any leftover themes/
# sites/*.cshtml in dist/server/ from a previous build will otherwise
# be picked up by MSBuild as Razor Pages and fail to compile (they use
# Statiq's IDocument API, not ASP.NET's). Always build fresh.
rm -rf "$PROJECT_ROOT/dist"
# NOTE: do NOT use PublishSingleFile=true. Statiq.Razor's compiler needs
# CodeBase access on its loaded assemblies, which is unavailable when the
# runtime is packed into a single-file bundle. So we publish as a folder
# of DLLs (still self-contained — .NET runtime is bundled, just unpacked).
dotnet publish "$PROJECT_ROOT/Editor.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained \
    -o "$DOTNET_PUBLISH_DIR" \
    > /tmp/sme-publish.log 2>&1
if [ ! -f "$DOTNET_PUBLISH_DIR/$DOTNET_EXE_NAME" ]; then
    echo "❌ dotnet publish failed — see /tmp/sme-publish.log"
    tail -20 /tmp/sme-publish.log
    exit 1
fi
echo "    → $(du -sh "$DOTNET_PUBLISH_DIR" | cut -f1) self-contained folder"

echo "==> 2/4 Building .app bundle structure"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources/app"
mkdir -p "$APP_BUNDLE/Contents/Resources/server"

# Copy Electron binary + frameworks (Frameworks/ contains the helper apps)
cp -R "$ELECTRON_APP/Contents/MacOS/." "$APP_BUNDLE/Contents/MacOS/"
cp -R "$ELECTRON_APP/Contents/Frameworks" "$APP_BUNDLE/Contents/"

# Copy our main.js + package.json (the renderer/host entry point)
cp "$PROJECT_ROOT/package.json" "$APP_BUNDLE/Contents/Resources/app/"
mkdir -p "$APP_BUNDLE/Contents/Resources/app/electron"
cp "$PROJECT_ROOT/electron/main.js" "$APP_BUNDLE/Contents/Resources/app/electron/"

# Copy the entire .NET publish output (exe + wwwroot + appsettings + .pdb + ...)
cp -R "$DOTNET_PUBLISH_DIR/." "$APP_BUNDLE/Contents/Resources/server/"
chmod +x "$APP_BUNDLE/Contents/Resources/server/$DOTNET_EXE_NAME"

echo "==> 3/4 Writing Info.plist + PkgInfo"
cat > "$APP_BUNDLE/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Statiq Markdown Editor</string>
    <key>CFBundleDisplayName</key>
    <string>Statiq Markdown Editor</string>
    <key>CFBundleExecutable</key>
    <string>Electron</string>
    <key>CFBundleIdentifier</key>
    <string>com.winsonet.statiq-markdown-editor</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.13.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
</dict>
</plist>
PLIST
echo "APPL????" > "$APP_BUNDLE/Contents/PkgInfo"

echo "==> 4/4 Done"
echo ""
echo "📦 Built: $APP_BUNDLE"
echo "   Size: $(du -sh "$APP_BUNDLE" | cut -f1)"
echo ""
echo "Try it:"
echo "   open \"$APP_BUNDLE\""
echo ""
echo "Optional: install to /Applications so it shows up in Spotlight:"
echo "   cp -R \"$APP_BUNDLE\" /Applications/"
echo ""
echo "Note: the app is unsigned. First launch will show Gatekeeper"
echo "'cannot be opened' — right-click the .app → Open → Open to bypass."
