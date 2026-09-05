# Linux Packaging

This folder contains the shared Linux metadata used by AppImage and prepared for
future Flatpak and Snap packaging.

## Files

- `io.github.Avazbek22.DevProjex.desktop` is the canonical desktop entry.
- `io.github.Avazbek22.DevProjex.metainfo.xml` is the canonical AppStream metadata.
- PNG icons are in `Assets/AppIcon/Linux/png/`.

The 27 MB `Assets/AppIcon/Linux/appicon-master.svg` source asset is not included
in AppImages.

## Release asset names

AppImages use the filename pattern required by the AppImage catalog:

- `DevProjex-<version>-x86_64.AppImage`
- `DevProjex-<version>-aarch64.AppImage`

The existing portable archives keep their established names:

- `DevProjex.v<version>.linux-x64.tar.gz`
- `DevProjex.v<version>.linux-arm64.tar.gz`

## Manual installation

Publish and install the command as `devprojex`:

```bash
dotnet publish Apps/Avalonia/DevProjex.Avalonia.csproj \
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

sudo install -Dm755 ./publish/linux-x64/DevProjex /usr/local/bin/devprojex
```

For a user-only installation, place the binary at `~/.local/bin/devprojex`.
Install the app-id desktop entry, metadata, and PNG icons with the same app-id:

```bash
install -Dm644 Packaging/Linux/io.github.Avazbek22.DevProjex.desktop \
  ~/.local/share/applications/io.github.Avazbek22.DevProjex.desktop
install -Dm644 Packaging/Linux/io.github.Avazbek22.DevProjex.metainfo.xml \
  ~/.local/share/metainfo/io.github.Avazbek22.DevProjex.metainfo.xml
for size in 128 256 512; do
  install -Dm644 "Assets/AppIcon/Linux/png/${size}.png" \
    "${HOME}/.local/share/icons/hicolor/${size}x${size}/apps/io.github.Avazbek22.DevProjex.png"
done
```

The desktop entry invokes `devprojex open %f`, so a graphical launcher opens the
selected folder in DevProjex Desktop. Running `devprojex` in an interactive
terminal opens DevProjex Terminal. The window icon remains embedded in the
Avalonia application independently of the desktop entry.

## Local AppImage build

The authoritative build is `.github/workflows/package-appimage.yml`. On an
x86_64 Ubuntu 24.04 or newer host, the equivalent local build is:

```bash
sudo apt-get update
sudo apt-get install -y appstream desktop-file-utils file jq libfile-mimeinfo-perl xvfb
appstreamcli --version # 0.16.4 or newer

VERSION=5.1
RID=linux-x64
APPIMAGE_ARCH=x86_64
APP_ID=io.github.Avazbek22.DevProjex
BUILD_ROOT="artifacts/appimage-local/${APPIMAGE_ARCH}"
APP_DIR="${BUILD_ROOT}/DevProjex.AppDir"

dotnet publish Apps/Avalonia/DevProjex.Avalonia.csproj \
  -c Release -r "${RID}" --self-contained true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  /p:PublishReadyToRun=true \
  /p:PublishTrimmed=false \
  /p:DebugType=None \
  /p:DebugSymbols=false \
  "/p:DevProjexVersion=${VERSION}" \
  -o "${BUILD_ROOT}/publish"

mkdir -p "${APP_DIR}/usr/bin" \
  "${APP_DIR}/usr/share/applications" \
  "${APP_DIR}/usr/share/metainfo"
install -m755 "${BUILD_ROOT}/publish/DevProjex" "${APP_DIR}/usr/bin/DevProjex"
ln -s DevProjex "${APP_DIR}/usr/bin/devprojex"
ln -s usr/bin/DevProjex "${APP_DIR}/AppRun"
install -m644 "Packaging/Linux/${APP_ID}.desktop" "${APP_DIR}/${APP_ID}.desktop"
install -m644 "Packaging/Linux/${APP_ID}.desktop" \
  "${APP_DIR}/usr/share/applications/${APP_ID}.desktop"
install -m644 "Packaging/Linux/${APP_ID}.metainfo.xml" \
  "${APP_DIR}/usr/share/metainfo/${APP_ID}.metainfo.xml"
ln -s "${APP_ID}.metainfo.xml" \
  "${APP_DIR}/usr/share/metainfo/${APP_ID}.appdata.xml"
for size in 128 256 512; do
  icon_dir="${APP_DIR}/usr/share/icons/hicolor/${size}x${size}/apps"
  mkdir -p "${icon_dir}"
  install -m644 "Assets/AppIcon/Linux/png/${size}.png" "${icon_dir}/${APP_ID}.png"
done
install -m644 Assets/AppIcon/Linux/png/512.png "${APP_DIR}/${APP_ID}.png"
ln -s "${APP_ID}.png" "${APP_DIR}/.DirIcon"

desktop-file-validate "Packaging/Linux/${APP_ID}.desktop"
appstreamcli validate --strict --explain "Packaging/Linux/${APP_ID}.metainfo.xml"

curl -fL https://raw.githubusercontent.com/AppImage/AppImages/19e30b276ffedf4d3b4b56bc6320f463625a74f8/appdir-lint.sh \
  -o "${BUILD_ROOT}/appdir-lint.sh"
curl -fL https://raw.githubusercontent.com/AppImage/AppImages/19e30b276ffedf4d3b4b56bc6320f463625a74f8/excludelist \
  -o "${BUILD_ROOT}/excludelist"
bash "${BUILD_ROOT}/appdir-lint.sh" "${APP_DIR}"

curl -fL https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage \
  -o "${BUILD_ROOT}/appimagetool.AppImage"
echo "a6d71e2b6cd66f8e8d16c37ad164658985e0cf5fcaa950c90a482890cb9d13e0  ${BUILD_ROOT}/appimagetool.AppImage" \
  | sha256sum --check --strict
chmod +x "${BUILD_ROOT}/appimagetool.AppImage"
ARCH="${APPIMAGE_ARCH}" VERSION="${VERSION}" APPIMAGE_EXTRACT_AND_RUN=1 \
  "${BUILD_ROOT}/appimagetool.AppImage" "${APP_DIR}" \
  "artifacts/DevProjex-${VERSION}-${APPIMAGE_ARCH}.AppImage"
```

The CI build itself remains on Ubuntu 22.04. Its distribution AppStream is too
old for strict validation and the `<developer>` element, so the workflow builds
AppStream 0.16.4 and libxmlb 0.3.14 from checksum-pinned upstream sources. To
build on Ubuntu 22.04 locally, run that workflow step first and then use the same
commands above.

For native aarch64, use `RID=linux-arm64`, `APPIMAGE_ARCH=aarch64`, the
`appimagetool-aarch64.AppImage` URL, and SHA-256
`1b00524ba8c6b678dc15ef88a5c25ec24def36cdfc7e3abb32ddcd068e8007fe`.
Do not cross-build the catalog artifact: the CI matrix uses native
`ubuntu-22.04-arm`.

The workflow additionally extracts the finished image, compares the packaged
`usr/bin/DevProjex` SHA-256 with the published single file, and runs the CLI and
Xvfb smoke checks.

## Submit to AppImageHub

After a GitHub release containing both AppImages is published, the repository
owner can submit DevProjex to the catalog:

1. Fork and clone `AppImage/appimage.github.io`.
2. Create `data/DevProjex` containing exactly this one line:

   ```text
   https://github.com/Avazbek22/DevProjex
   ```

3. Commit that file on a topic branch in the fork.
4. Open a pull request to the `master` branch of
   `AppImage/appimage.github.io` and wait for its AppImage checks.

Do not submit the catalog entry before the release assets exist: the catalog
discovers the AppImage from GitHub Releases and validates the embedded desktop
and AppStream metadata.
