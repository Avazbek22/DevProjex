#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
dotnet="${DOTNET:-dotnet}"
evidence="${1:-$PWD/flatpak-smoke-evidence}"
client_directory="$(mktemp -d "$HOME/devprojex-flatpak-check-client.XXXXXX")"
trap 'rm -rf -- "$client_directory"' EXIT
case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64) rid=linux-arm64 ;;
  *) printf 'Unsupported check-client architecture\n' >&2; exit 1 ;;
esac
"$dotnet" publish Packaging/Linux/flatpak/SmokeClient/SmokeClient.csproj -c Release -m:1 --nologo \
  -r "$rid" --self-contained true -p:PublishSingleFile=false -o "$client_directory"
export DOTNET="$dotnet"
export DEVPROJEX_FLATPAK_CHECK_CLIENT="$client_directory/SmokeClient"
export DEVPROJEX_FLATPAK_TRANSPORT="$PWD/Packaging/Linux/flatpak/transport.py"
python3 Packaging/Linux/flatpak/smoke.py --evidence "$evidence"
