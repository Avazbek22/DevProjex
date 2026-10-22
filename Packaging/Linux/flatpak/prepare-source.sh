#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
git archive --format=tar.gz --output=Packaging/Linux/flatpak/devprojex-source.tar.gz HEAD \
  Kernel Application Infrastructure Assets Apps Packaging/Linux \
  Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
  LICENSE THIRD-PARTY-NOTICES.md \
  ':(exclude)Infrastructure/Grammars/vendored/win-x64' \
  ':(exclude)Infrastructure/Grammars/vendored/win-arm64' \
  ':(exclude)Infrastructure/Grammars/vendored/linux-x64' \
  ':(exclude)Infrastructure/Grammars/vendored/linux-arm64' \
  ':(exclude)Infrastructure/Grammars/vendored/osx-x64' \
  ':(exclude)Infrastructure/Grammars/vendored/osx-arm64'
python3 - <<'PY'
import hashlib, json
from pathlib import Path
folder = Path('Packaging/Linux/flatpak')
archive = folder / 'devprojex-source.tar.gz'
with archive.open('rb') as stream:
    checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
(folder / 'app-source.json').write_text(json.dumps([{
    'type': 'archive', 'path': archive.name, 'sha256': checksum, 'strip-components': 0,
}], indent=2) + '\n')
PY
