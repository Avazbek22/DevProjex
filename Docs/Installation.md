# Installation

Choose the artifact that matches your operating system and CPU architecture from the
GitHub release page.

## Windows

- Download and run `DevProjex.v<version>.win-x64.exe` or
  `DevProjex.v<version>.win-arm64.exe`.
- Install with WinGet: `winget install OlimoffDev.DevProjex`.
- Install the packaged version from Microsoft Store when it is available in your
  region.

## Linux

### AppImage

Download `DevProjex-<version>-x86_64.AppImage` or
`DevProjex-<version>-aarch64.AppImage` from the GitHub release page, then run:

```bash
chmod +x DevProjex-<version>-<architecture>.AppImage
./DevProjex-<version>-<architecture>.AppImage
```

The AppImage runtime is static and does not require `libfuse2`. If the system
disables FUSE mounts, use the extraction fallback instead:

```bash
./DevProjex-<version>-<architecture>.AppImage --appimage-extract-and-run
```

Desktop-menu integration through AppImageLauncher or appimaged is optional.

### tar.gz archive

The archive preserves the executable permission. Replace `<version>` and
`<architecture>` (`x64` or `arm64`) before running:

```bash
curl -fL "https://github.com/Avazbek22/DevProjex/releases/download/v<version>/DevProjex.v<version>.linux-<architecture>.tar.gz" | tar xz
./DevProjex
```

Move `DevProjex` to a directory on `PATH` if you want a system-wide command.

## macOS

DevProjex requires macOS 14 or newer. Replace `<version>` and `<architecture>` (`x64`
or `arm64`) before running:

```bash
curl -fL "https://github.com/Avazbek22/DevProjex/releases/download/v<version>/DevProjex.v<version>.osx-<architecture>.app.tar.gz" | tar xz
sudo mv DevProjex.app /Applications/
```

The current bundle is unsigned and not notarized. On macOS 15, try to launch the
app once, then open **System Settings -> Privacy & Security**, scroll to the
security notice for DevProjex, choose **Open Anyway**, and confirm. On macOS 14,
**right-click -> Open** remains an alternative. `curl` does not normally attach
the quarantine attribute that browsers add.
