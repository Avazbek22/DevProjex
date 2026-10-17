#!/bin/bash
set -euo pipefail
app=io.github.Avazbek22.DevProjex
fixture="$(mktemp -d "$HOME/devprojex-flatpak-smoke.XXXXXX")"
trap 'rm -f -- "$fixture"/*; rmdir -- "$fixture"' EXIT
printf '// flatpak-smoke-comment-kotlin\nclass Greeting { fun hello(): String { return "flatpak-smoke-body" } }\n' > "$fixture/Main.kt"
printf '# flatpak-smoke-comment-toml\ntitle = "Example"\n' > "$fixture/config.toml"
printf '<!-- flatpak-smoke-comment-xml --><project><name>Example</name></project>\n' > "$fixture/project.xml"
printf '# flatpak-smoke-comment-yaml\nproject:\n  name: Example\n' > "$fixture/config.yaml"
cd "$fixture"
flatpak run "$app" doctor --json > doctor.json
python3 - <<'PY'
import json
from pathlib import Path
report = json.loads(Path('doctor.json').read_text())
assert report['packageType'] == 'flatpak', report
assert next(check['path'] for check in report['checks'] if check['name'] == 'current-directory') == str(Path.cwd())
assert any('flatpak run io.github.Avazbek22.DevProjex mcp' in check['detail'] for check in report['checks'])
PY
flatpak run "$app" export context . --compress-code --strip-comments --format json --output "$fixture/context.json" 2> compression.stderr
test -s "$fixture/context.json"
python3 - <<'PY'
from pathlib import Path
content = Path('context.json').read_text()
assert 'flatpak-smoke-body' not in content, content
assert 'flatpak-smoke-comment-' not in content, content
assert 'DPX-COMPRESSION-UNAVAILABLE' not in Path('compression.stderr').read_text()
PY
flatpak run --command=sh "$app" -c 'test -x /app/bin/git; for language in kotlin toml xml yaml; do test -f /app/lib/devprojex/grammars/libtree-sitter-$language.so || exit 1; done'
