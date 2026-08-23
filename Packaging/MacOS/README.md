# macOS Packaging

This folder contains information for packaging DevProjex on macOS.

## Icon Assets

Icon PNGs are located in `Assets/AppIcon/MacOS/AppIconSet/`:
- 16.png, 32.png, 64.png, 128.png, 256.png, 512.png, 1024.png

## Official Release Artifact

`Scripts/release-all.ps1` builds the official unsigned macOS artifacts:

```powershell
./Scripts/release-all.ps1 -Version 5.1 -GitHubArtifactsOnly
```

The resulting files are named `DevProjex.v<version>.osx-<architecture>.app.tar.gz`.
Each archive contains one Finder-ready `DevProjex.app` bundle:

```text
DevProjex.app/
└── Contents/
    ├── Info.plist
    ├── MacOS/
    │   └── DevProjex
    └── Resources/
        └── app.icns
```

The release script generates `app.icns` deterministically from the committed PNGs,
fills `Packaging/MacOS/Info.plist.template` with the release version, preserves Unix
permissions in a deterministic ustar archive, and verifies the result with both its
own reader and `tar -tvf`. No macOS tools, Node.js packages, or network downloads are
used during bundle assembly.

The raw RID publish directory must still contain exactly one file named `DevProjex`.
It is an intermediate validation boundary and is not uploaded as the macOS release
artifact. DevProjex targets .NET 10 and therefore requires macOS 14 or newer.

## Reference Manual Bundle Recipe

The following recipe documents the bundle layout for maintainers. Normal releases
must use `release-all.ps1` so the icon, metadata, permissions, and validation remain
reproducible.

```bash
# Build for macOS
dotnet publish Apps/Avalonia/DevProjex.Avalonia.csproj \
    -c Release \
    -r osx-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    /p:PublishReadyToRun=true \
    /p:PublishTrimmed=false \
    /p:DebugType=None \
    /p:DebugSymbols=false \
    -o ./publish/osx-x64

# Create app bundle structure
mkdir -p "DevProjex.app/Contents/MacOS"
mkdir -p "DevProjex.app/Contents/Resources"

# Copy executable
cp ./publish/osx-x64/DevProjex "DevProjex.app/Contents/MacOS/DevProjex"

# Generate or copy a valid app.icns into the bundle
./Scripts/generate-app-icns.sh
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
    <string>YOUR_RELEASE_VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>YOUR_RELEASE_VERSION</string>
    <key>CFBundleExecutable</key>
    <string>DevProjex</string>
    <key>CFBundleIconFile</key>
    <string>app.icns</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF
```

This manual output is unsigned and not notarized. Native libraries can be extracted
by the .NET single-file host at startup; set
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a private writable directory if the process
has no usable home directory.

## Optional Terminal Alias

For terminal automation, use **Help -> Terminal command** in the app. DevProjex creates a user-level wrapper named `devprojex` in `~/.local/bin` and can repair it if the app bundle moves.

Manual equivalent:

```bash
mkdir -p ~/.local/bin
cat > ~/.local/bin/devprojex <<'EOF'
#!/bin/sh
# DevProjex terminal command wrapper
# target: /Applications/DevProjex.app/Contents/MacOS/DevProjex
export DEVPROJEX_TERMINAL_HOST=1
exec '/Applications/DevProjex.app/Contents/MacOS/DevProjex' "$@"
EOF
chmod +x ~/.local/bin/devprojex
```

Make sure `~/.local/bin` is in `PATH`. The app bundle itself does not modify shell profiles or global environment variables.

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
