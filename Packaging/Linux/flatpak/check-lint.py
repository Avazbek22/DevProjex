"""Keep every Flathub error visible and accept only the owner's pending home permission."""
import json
import subprocess
import sys

result = subprocess.run(['flatpak', 'run', '--command=flatpak-builder-lint', 'org.flatpak.Builder',
                         *sys.argv[1:]], capture_output=True, text=True, check=False)
sys.stdout.write(result.stdout)
sys.stderr.write(result.stderr)
report = json.loads(result.stdout) if result.stdout.strip() else {}
errors = set(report.get('errors', []))
if errors - {'finish-args-home-filesystem-access'} or report.get('warnings'):
    raise SystemExit(result.returncode or 1)
if result.returncode and not errors:
    raise SystemExit(result.returncode)
if errors:
    print('Home access still requires Flathub approval; no exception was applied.')
