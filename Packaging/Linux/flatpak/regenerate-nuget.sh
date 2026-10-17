#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
arch="${1:-$(flatpak --default-arch)}"
case "$arch" in
  x86_64) rid=linux-x64 ;;
  aarch64) rid=linux-arm64 ;;
  *) printf 'Unsupported architecture\n' >&2; exit 1 ;;
esac
flatpak install --user --noninteractive flathub \
  org.freedesktop.Sdk//26.08 org.freedesktop.Sdk.Extension.dotnet10//26.08
generator_dir="$(mktemp -d)"
trap 'rm -f -- "$generator_dir/flatpak-dotnet-generator.py"; rmdir -- "$generator_dir"' EXIT
curl --fail --location --silent --show-error \
  https://raw.githubusercontent.com/flatpak/flatpak-builder-tools/74697c75b630d7330e77250fc13cb5ea688d9479/dotnet/flatpak-dotnet-generator.py \
  --output "$generator_dir/flatpak-dotnet-generator.py"
python3 "$generator_dir/flatpak-dotnet-generator.py" \
  Packaging/Linux/flatpak/nuget-sources.json Apps/Avalonia/DevProjex.Avalonia.csproj \
  --runtime "$rid" --only-arches "$arch" --freedesktop 26.08 --dotnet 10 \
  --dotnet-args -p:RuntimeIdentifiers="$rid" -p:SelfContained=true \
  -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DevProjexGrammarDelivery=Content
python3 - "$rid" <<'PY'
import json, sys
from pathlib import Path
sources = json.loads(Path('Packaging/Linux/flatpak/nuget-sources.json').read_text())
names = [item['dest-filename'] for item in sources]
if not sources or not any(f'microsoft.netcore.app.runtime.{sys.argv[1]}.' in name for name in names):
    raise SystemExit('NuGet generation did not include the self-contained runtime pack')
if not any('microsoft.netcore.app.crossgen2.' in name for name in names):
    raise SystemExit('NuGet generation did not include the ReadyToRun compiler')
PY
