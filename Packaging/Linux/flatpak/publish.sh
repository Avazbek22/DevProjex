#!/bin/bash
set -euo pipefail
case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64) rid=linux-arm64 ;;
  *) printf 'Unsupported architecture\n' >&2; exit 1 ;;
esac
mkdir -p "Infrastructure/Grammars/vendored/$rid"
for language in kotlin toml xml yaml; do
  install -m644 "/app/lib/devprojex/grammars/libtree-sitter-$language.so" \
    "Infrastructure/Grammars/vendored/$rid/libtree-sitter-$language.so"
done
dotnet restore Apps/Avalonia/DevProjex.Avalonia.csproj --source nuget-sources \
  -p:NuGetPackageRoot="${NUGET_PACKAGES%/}/" \
  -r "$rid" -p:RuntimeIdentifiers="$rid" -p:SelfContained=true \
  -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DevProjexGrammarDelivery=Content
dotnet publish Apps/Avalonia/DevProjex.Avalonia.csproj --no-restore -c Release -m:1 \
  -p:NuGetPackageRoot="${NUGET_PACKAGES%/}/" \
  -r "$rid" --self-contained true -p:RuntimeIdentifiers="$rid" \
  -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DevProjexGrammarDelivery=Content \
  -p:EnableProjectLoadTiming=false -p:PublishTrimmed=false -p:DebugType=None \
  -p:DebugSymbols=false -p:UseSharedCompilation=false -o /app/lib/devprojex
install -Dm755 Packaging/Linux/flatpak/devprojex.sh /app/bin/devprojex
