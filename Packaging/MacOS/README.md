# macOS Packaging

This folder contains information for packaging DevProjex on macOS.

## Icon Assets

Icon PNGs are located in `Assets/AppIcon/MacOS/AppIconSet/`:
- 16.png, 32.png, 64.png, 128.png, 256.png, 512.png, 1024.png

## Generating app.icns

The `.icns` file is required for macOS application bundles. It must be generated on macOS using `iconutil`.

### Option 1: Using the provided script (macOS only)

```bash
./Scripts/generate-app-icns.sh
```

This will create `Assets/AppIcon/MacOS/app.icns`.

### Option 2: Manual generation (macOS only)

```bash
# Create iconset directory with proper naming
mkdir -p Assets/AppIcon/MacOS/app.iconset

cp Assets/AppIcon/MacOS/AppIconSet/16.png   Assets/AppIcon/MacOS/app.iconset/icon_16x16.png
cp Assets/AppIcon/MacOS/AppIconSet/32.png   Assets/AppIcon/MacOS/app.iconset/icon_16x16@2x.png
cp Assets/AppIcon/MacOS/AppIconSet/32.png   Assets/AppIcon/MacOS/app.iconset/icon_32x32.png
cp Assets/AppIcon/MacOS/AppIconSet/64.png   Assets/AppIcon/MacOS/app.iconset/icon_32x32@2x.png
cp Assets/AppIcon/MacOS/AppIconSet/128.png  Assets/AppIcon/MacOS/app.iconset/icon_128x128.png
cp Assets/AppIcon/MacOS/AppIconSet/256.png  Assets/AppIcon/MacOS/app.iconset/icon_128x128@2x.png
cp Assets/AppIcon/MacOS/AppIconSet/256.png  Assets/AppIcon/MacOS/app.iconset/icon_256x256.png
cp Assets/AppIcon/MacOS/AppIconSet/512.png  Assets/AppIcon/MacOS/app.iconset/icon_256x256@2x.png
cp Assets/AppIcon/MacOS/AppIconSet/512.png  Assets/AppIcon/MacOS/app.iconset/icon_512x512.png
cp Assets/AppIcon/MacOS/AppIconSet/1024.png Assets/AppIcon/MacOS/app.iconset/icon_512x512@2x.png

# Generate icns
iconutil -c icns Assets/AppIcon/MacOS/app.iconset -o Assets/AppIcon/MacOS/app.icns

# Cleanup
rm -rf Assets/AppIcon/MacOS/app.iconset
```

### Option 3: Cross-platform using png2icns (Node.js)

```bash
npm install -g png2icns
png2icns Assets/AppIcon/MacOS/app.icns \
    Assets/AppIcon/MacOS/AppIconSet/16.png \
    Assets/AppIcon/MacOS/AppIconSet/32.png \
    Assets/AppIcon/MacOS/AppIconSet/128.png \
    Assets/AppIcon/MacOS/AppIconSet/256.png \
    Assets/AppIcon/MacOS/AppIconSet/512.png \
    Assets/AppIcon/MacOS/AppIconSet/1024.png
```

## Creating an App Bundle

For distribution on macOS, you need to create an `.app` bundle:

```bash
# Build for macOS
dotnet publish Apps/Avalonia/DevProjex.Avalonia/DevProjex.Avalonia.csproj \
    -c Release \
    -r osx-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    /p:PublishTrimmed=false \
    -o ./publish/macos

# Create app bundle structure
mkdir -p "DevProjex.app/Contents/MacOS"
mkdir -p "DevProjex.app/Contents/Resources"

# Copy executable
cp ./publish/macos/* "DevProjex.app/Contents/MacOS/"

# Copy icon
cp Assets/AppIcon/MacOS/app.icns "DevProjex.app/Contents/Resources/app.icns"

# Create Info.plist (example - customize as needed)
cat > "DevProjex.app/Contents/Info.plist" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>DevProjex</string>
    <key>CFBundleDisplayName</key>
    <string>DevProjex</string>
    <key>CFBundleIdentifier</key>
    <string>com.devprojex.app</string>
    <key>CFBundleVersion</key>
    <string>4.6.0</string>
    <key>CFBundleShortVersionString</key>
    <string>4.6.0</string>
    <key>CFBundleExecutable</key>
    <string>DevProjex</string>
    <key>CFBundleIconFile</key>
    <string>app.icns</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF
```

## Code Signing and Notarization

For distribution outside the Mac App Store, the app must be signed and notarized:

```bash
# Sign the app (requires Apple Developer certificate)
codesign --deep --force --verify --verbose \
    --sign "Developer ID Application: Your Name (TEAMID)" \
    "DevProjex.app"

# Create ZIP for notarization
ditto -c -k --keepParent "DevProjex.app" "DevProjex.zip"

# Submit for notarization
xcrun notarytool submit "DevProjex.zip" \
    --apple-id "your@email.com" \
    --team-id "TEAMID" \
    --password "app-specific-password" \
    --wait

# Staple the notarization ticket
xcrun stapler staple "DevProjex.app"
```
