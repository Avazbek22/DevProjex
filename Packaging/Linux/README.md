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
    /p:PublishTrimmed=false \
    -o ./publish/linux-x64
```

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

## Future: DEB/RPM Packages

For proper distribution packages (`.deb`, `.rpm`, Flatpak, Snap), additional configuration files will be needed. The icon assets and desktop entry in this folder serve as the foundation for those packages.

## GitHub Release Asset Naming

Portable Linux single-file artifacts are published as:

- `DevProjex.v<version>.linux-x64.portable`
- `DevProjex.v<version>.linux-arm64.portable`
