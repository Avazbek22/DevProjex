# Installation

Choose the artifact that matches your operating system and CPU architecture from the
GitHub release page.

## Windows

- Download and run `DevProjex.v<version>.win-x64.exe` or
  `DevProjex.v<version>.win-arm64.exe`.
- Install with WinGet: `winget install Avazbek22.DevProjex`.
- Install the packaged version from Microsoft Store when it is available in your
  region.

## Linux

The archive preserves the executable permission. Replace `<version>` and
`<architecture>` (`x64` or `arm64`) before running:

```bash
curl -L "https://github.com/Avazbek22/DevProjex/releases/download/v<version>/DevProjex.v<version>.linux-<architecture>.tar.gz" | tar xz
./DevProjex
```

Move `DevProjex` to a directory on `PATH` if you want a system-wide command.

## macOS

DevProjex requires macOS 14 or newer. Replace `<version>` and `<architecture>` (`x64`
or `arm64`) before running:

```bash
curl -L "https://github.com/Avazbek22/DevProjex/releases/download/v<version>/DevProjex.v<version>.osx-<architecture>.app.tar.gz" | tar xz
mv DevProjex.app /Applications/
```

The current bundle is unsigned and not notarized. For a browser download, start it
the first time with **right-click -> Open** and confirm the prompt. `curl` does not
normally attach the macOS quarantine attribute that browsers add, but the same
right-click flow remains the safest first-launch fallback.
