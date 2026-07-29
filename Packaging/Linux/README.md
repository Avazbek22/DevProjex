# Linux Packaging

This folder contains resources for packaging DevProjex on Linux.

## Files

- `devprojex.desktop` - Desktop entry file for application launchers
- Icons are located in `Assets/AppIcon/Linux/`

## Manual Installation

### 1. Build the application

```bash
dotnet publish Apps/Avalonia/DevProjex.Avalonia/DevProjex.Avalonia.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    /p:PublishReadyToRun=true \
    /p:PublishTrimmed=false \
    /p:DebugType=None \
    /p:DebugSymbols=false \
    -o ./publish/linux-x64
```

This is the same single-file recipe used by release validation. It must produce
exactly one primary file named `DevProjex`. Native libraries can be extracted by
the .NET single-file host at startup; set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a
private writable directory in environments without a usable home directory.

### 2. Install the executable

```bash
# System-wide
sudo cp ./publish/linux-x64/DevProjex /usr/local/bin/devprojex
sudo chmod +x /usr/local/bin/devprojex

# Or user-only
mkdir -p ~/.local/bin
cp ./publish/linux-x64/DevProjex ~/.local/bin/devprojex
chmod +x ~/.local/bin/devprojex
```

### 3. Install the desktop entry

```bash
# System-wide
sudo cp Packaging/Linux/devprojex.desktop /usr/share/applications/

# Or user-only
mkdir -p ~/.local/share/applications
cp Packaging/Linux/devprojex.desktop ~/.local/share/applications/
```

### 4. Install icons

```bash
# System-wide installation
sudo mkdir -p /usr/share/icons/hicolor/{128x128,256x256,512x512,scalable}/apps

sudo cp Assets/AppIcon/Linux/png/128.png /usr/share/icons/hicolor/128x128/apps/devprojex.png
sudo cp Assets/AppIcon/Linux/png/256.png /usr/share/icons/hicolor/256x256/apps/devprojex.png
sudo cp Assets/AppIcon/Linux/png/512.png /usr/share/icons/hicolor/512x512/apps/devprojex.png
sudo cp Assets/AppIcon/Linux/appicon-master.svg /usr/share/icons/hicolor/scalable/apps/devprojex.svg

# Update icon cache
sudo gtk-update-icon-cache /usr/share/icons/hicolor
```

## Window Icon

The application window icon is set automatically via Avalonia's `Window.Icon` property using the embedded PNG resource. This works independently of the desktop entry and hicolor icons.

The desktop entry invokes `devprojex open`, so graphical launchers always open DevProjex Desktop. Running `devprojex` from an interactive terminal opens DevProjex Terminal instead.

## GitHub Release Asset Naming

Portable Linux single-file artifacts are published as:

- `DevProjex.v<version>.linux-x64.portable`
- `DevProjex.v<version>.linux-arm64.portable`
