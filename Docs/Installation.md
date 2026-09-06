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

## Headless package channels (from v5.2)

The v5.2 release adds two package channels for the existing CLI, Terminal Workspace,
and MCP server. These packages do not contain Avalonia or a desktop application.
Package commands that only talk to an already running desktop (`devprojex ui ...`)
still use the existing IPC contract. `devprojex open` cannot start Desktop from a
headless package; install one of the desktop distributions above when that workflow
is needed.

The package names must be reserved and published separately from the repository
changes. Before the first v5.2 package publication, use a matching binary from the
GitHub release page.

### Run through Node

Requires Node.js 20 or later:

```shell
npx -y devprojex tree .
npx -y devprojex analyze . --compress-code
npx -y devprojex mcp --root .
```

The npm launcher has no dependencies, lifecycle scripts, downloads, telemetry, or
version checks. Its optional dependency selects one binary for `win32-x64`,
`win32-arm64`, `linux-x64`, `linux-arm64`, `darwin-x64`, or `darwin-arm64`.
Linux packages require glibc; Alpine Linux and other musl systems are not supported
in v5.2. An install made with `--ignore-scripts` works normally. An install made
with `--omit=optional` intentionally fails at launch with exit code `1` and lists
the supported targets plus the `dnx` and GitHub-release alternatives.

Projects that commit multi-platform npm lockfiles may need the package manager's
supported-architecture setting. In particular, regenerating a lockfile with an
existing `node_modules` directory can prune optional packages for other platforms;
regenerate it from a clean install when the lockfile must cover more than one RID.

### Run through the .NET SDK

Requires .NET SDK 10.0.100 or later (`dnx` is the short form of
`dotnet tool execute`):

```shell
dnx devprojex tree .
dnx devprojex analyze . --compress-code
dnx devprojex mcp --root .
```

The top-level `devprojex` NuGet package points to one of six self-contained RID
tool packages, so the target machine does not need a separate .NET runtime. The
same RID matrix as npm is supported. No `any` fallback exists: other operating
systems, CPU architectures, and musl Linux should use an applicable GitHub release
or build from source. A persistent installation is also available:

```shell
dotnet tool install --global devprojex
```

## Headless binary

Release automation prepares one headless archive for each supported RID. Choose
`DevProjex-headless.v<version>.<rid>.zip` on Windows or
`DevProjex-headless.v<version>.<rid>.tar.gz` on Linux and macOS. For example:

```powershell
Expand-Archive DevProjex-headless.v<version>.win-x64.zip
./devprojex.exe --version
```

```bash
tar xzf DevProjex-headless.v<version>.linux-x64.tar.gz
./devprojex --version
```

The archive contains the same self-contained host used by the matching npm
platform package. It provides the CLI, Terminal Workspace, and MCP server without
Avalonia. It cannot launch the GUI with `open`; `ui ...` remains available only
to control an already running Desktop instance.

## Docker

The release workflow is configured to publish a glibc-based, multi-architecture
image for amd64 and arm64 to `ghcr.io/avazbek22/devprojex`. The image contains no
desktop application and runs as a non-root user. Mount project input read-only by
default:

```bash
docker run --rm -i --read-only --tmpfs /tmp \
  -v "$PWD:/project:ro" \
  ghcr.io/avazbek22/devprojex mcp --root /project

docker run --rm --read-only --tmpfs /tmp \
  -v "$PWD:/project:ro" \
  ghcr.io/avazbek22/devprojex analyze /project --findings --fail-on-findings
```

Use an explicit version tag for reproducible automation, for example
`ghcr.io/avazbek22/devprojex:5.2`. The untagged examples use Docker's `latest`
tag only after a release workflow has published it. Alpine and other musl hosts
are not supported.
