"""Exercise an installed Flatpak through its real CLI, SDK client, PTY and X11 window."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
from pty_check import Terminal

APP = 'io.github.Avazbek22.DevProjex'
SOURCE = Path(__file__).resolve().parent


class Checks:
    def __init__(self, evidence):
        self.evidence = evidence
        evidence.mkdir(parents=True, exist_ok=True)
        self.fixture = Path(tempfile.mkdtemp(prefix='devprojex-flatpak-smoke.', dir=Path.home()))
        self.results = {}
        self.dotnet = os.environ.get('DOTNET', 'dotnet')
        self.client = SOURCE / 'SmokeClient/bin/Release/net10.0/SmokeClient.dll'
        self.client_command = [os.environ['DEVPROJEX_FLATPAK_CHECK_CLIENT']] if os.environ.get('DEVPROJEX_FLATPAK_CHECK_CLIENT') else [self.dotnet, str(self.client)]
        files = {
            'Main.kt': '// flatpak-smoke-comment-kotlin FlatpakSearchMarker\nclass Greeting { fun hello(): String { return "flatpak-smoke-body" } }\n',
            'selected.cs': '// flatpak-smoke-comment-csharp FlatpakSearchMarker\nclass Greeting { string Hello() { return "flatpak-csharp-body"; } }\n',
            'outside.cs': '// FlatpakSearchMarker OutsideMarker\nclass Outside {}\n',
            'config.toml': '# flatpak-smoke-comment-toml\ntitle = "Example"\n',
            'project.xml': '<!-- flatpak-smoke-comment-xml --><project><name>Example</name></project>\n',
            'config.yaml': '# flatpak-smoke-comment-yaml\nproject:\n  name: Example\n'
        }
        for name, content in files.items():
            (self.fixture / name).write_text(content)
        (evidence / 'fixture.txt').write_text(str(self.fixture))

    def run(self, command, name, success=True, timeout=90, cwd=None):
        result = subprocess.run(command, cwd=cwd or self.fixture, capture_output=True, text=True, timeout=timeout)
        (self.evidence / (name + '.stdout')).write_text(result.stdout)
        (self.evidence / (name + '.stderr')).write_text(result.stderr)
        (self.evidence / (name + '.command.json')).write_text(json.dumps(command))
        if success is not None and (result.returncode == 0) != success:
            raise AssertionError(f'{name}: exit {result.returncode}\n{result.stdout}\n{result.stderr}')
        return result

    def app(self, arguments, name, **options):
        return self.run(['flatpak', 'run', APP, *map(str, arguments)], name, **options)

    def diagnostics(self):
        result = self.app(['doctor', '--format', 'json'], 'doctor')
        report = json.loads(result.stdout)
        assert report['packageType'] == 'flatpak', report
        checks = {check['name']: check for check in report['checks']}
        assert checks['current-directory']['path'] == str(self.fixture), report
        assert any('flatpak run ' + APP + ' mcp' in check['detail'] for check in report['checks']), report
        self.state = Path(checks['state-root']['path']) / 'DevProjex'
        self.profiles = Path(checks['profile-store']['path'])
        assert str(Path.home() / '.var/app' / APP) in str(self.state), self.state
        version = self.run(['flatpak', 'run', '--command=/app/bin/git', APP, '--version'], 'bundled-git')
        assert version.stdout.strip() == 'git version 2.56.0', version.stdout

    def mcp(self):
        folder = self.evidence / 'mcp'
        self.run([*self.client_command, str(self.fixture), str(folder)], 'sdk-client')
        messages = [json.loads(line) for line in (folder / 'stdout.jsonl').read_text().splitlines()]
        assert len(messages) >= 4 and all(item['jsonrpc'] == '2.0' for item in messages), messages

    def compression(self):
        destination = self.fixture.parent / (self.fixture.name + '-compressed.json')
        result = self.app(['export', 'context', self.fixture, '--compress-code', '--strip-comments',
                           '--format', 'json', '--output', destination], 'compression')
        content = destination.read_text()
        (self.evidence / 'compressed.json').write_text(content)
        destination.unlink()
        for name in ('Main.kt', 'selected.cs', 'config.toml', 'project.xml', 'config.yaml'):
            assert name in content, name
        for forbidden in ('flatpak-smoke-body', 'flatpak-csharp-body', 'flatpak-smoke-comment-'):
            assert forbidden not in content, forbidden
        assert 'DPX-COMPRESSION-UNAVAILABLE' not in result.stderr, result.stderr
        self.run(['flatpak', 'run', '--command=sh', APP, '-c',
                  'for language in kotlin toml xml yaml; do test -s /app/lib/devprojex/grammars/libtree-sitter-$language.so || exit 1; done'], 'native-grammars')

    def live(self):
        terminal = Terminal(['flatpak', 'run', APP, 'tui', str(self.fixture), '--profile', 'standard',
                             '--screen', 'inline', '--no-mouse', '--language', 'en'], self.evidence / 'tui')
        client = None
        try:
            terminal.wait(lambda text: 'selected.cs' in text, 'loaded')
            terminal.command('select ** off')
            terminal.command('select selected.cs on')
            terminal.command('set activity on')
            def persisted_selection():
                try:
                    return json.loads(self.profiles.read_text())['profiles'][str(self.fixture)]['selectedPaths'] == ['selected.cs']
                except (OSError, KeyError, json.JSONDecodeError):
                    return False
            terminal.wait(lambda _: persisted_selection(), 'selection-persisted')
            folder = self.evidence / 'live'
            folder.mkdir(exist_ok=True)
            before = set((self.state / 'live-sessions').glob('*.json'))
            with (self.evidence / 'live-client.log').open('w') as output:
                client = subprocess.Popen([*self.client_command, str(self.fixture), str(folder), '--live', '--hold'],
                                          stdout=output, stderr=output)
                terminal.wait(lambda _: (folder / 'ready').exists(), 'client-ready', timeout=60)
                registrations = set((self.state / 'live-sessions').glob('*.json')) - before
                assert len(registrations) == 1, registrations
                record = next(iter(registrations))
                (self.evidence / 'live-registration.json').write_bytes(record.read_bytes())
                terminal.wait(lambda text: '[x] A selected.cs' in text, 'returned-activity-marker', timeout=20)
                (folder / 'release').touch()
                client.wait(timeout=20)
                assert client.returncode == 0, client.returncode
                terminal.wait(lambda _: not record.exists(), 'registration-cleaned')
            folder = self.evidence / 'stale'
            folder.mkdir(exist_ok=True)
            with (self.evidence / 'stale-client.log').open('w') as output:
                client = subprocess.Popen([*self.client_command, str(self.fixture), str(folder), '--live', '--hold'],
                                          stdout=output, stderr=output)
                terminal.wait(lambda _: (folder / 'ready').exists(), 'stale-client-ready', timeout=60)
                records = set((self.state / 'live-sessions').glob('*.json')) - before
                assert len(records) == 1, records
                record = next(iter(records))
                instance = self.run(['flatpak', 'ps', '--columns=instance,application'], 'instances').stdout
                instances = [line.split()[0] for line in instance.splitlines() if APP in line]
                mcp_instance = []
                for item in instances:
                    probe = self.run(['flatpak', 'enter', item, 'sh', '-c', 'cat /proc/[0-9]*/cmdline'],
                                     'instance-' + item, success=None)
                    if '\x00mcp\x00' in probe.stdout:
                        mcp_instance.append(item)
                assert len(mcp_instance) == 1, (mcp_instance, instances)
                self.run(['flatpak', 'kill', mcp_instance[0]], 'kill-own-mcp')
                (folder / 'release').touch()
                client.wait(timeout=20)
                terminal.wait(lambda _: not record.exists(), 'stale-heartbeat-expired', timeout=25)
            terminal.command('reveal selected.cs')
            terminal.pump(2)
            assert terminal.process.poll() is None, 'File-manager launch closed the terminal workspace'
            terminal.snapshot('after-file-manager')
        finally:
            if client is not None and client.poll() is None:
                client.terminate()
                client.wait(timeout=10)
            terminal.close()

    def paths(self):
        from gui_check import check_path_window
        outside = Path(tempfile.mkdtemp(prefix='devprojex-flatpak-outside.', dir='/var/tmp'))
        (outside / 'example.cs').write_text('class OutsideSandbox {}\n')
        changed = False
        try:
            result = self.app(['analyze', outside, '--language', 'en'], 'outside-denied', success=False)
            assert 'flatpak override --user' in result.stderr and '--filesystem=' + str(outside) in result.stderr, result.stderr
            mcp = self.app(['mcp', '--root', outside, '--language', 'en'], 'outside-mcp-denied', success=False)
            assert 'flatpak override --user' in mcp.stderr, mcp.stderr
            assert not mcp.stdout, mcp.stdout
            check_path_window(self, outside, loaded=False)
            terminal = Terminal(['flatpak', 'run', APP, 'tui', str(self.fixture), '--screen', 'inline',
                                 '--no-mouse', '--language', 'en'], self.evidence / 'outside-tui')
            try:
                terminal.wait(lambda text: 'selected.cs' in text, 'loaded')
                terminal.command('open ' + str(outside))
                terminal.wait(lambda text: 'flatpak override' in text, 'sandbox-path-explanation')
            finally:
                terminal.close()
            override = self.run(['flatpak', 'override', '--user', '--show', APP], 'existing-overrides')
            assert not override.stdout.strip(), 'Refusing to replace existing user overrides'
            self.run(['flatpak', 'override', '--user', '--filesystem=' + str(outside), APP], 'allow-outside')
            changed = True
            allowed = self.app(['tree', outside], 'outside-allowed')
            assert 'example.cs' in allowed.stdout, allowed.stdout
            check_path_window(self, outside, loaded=True)
        finally:
            if changed:
                self.run(['flatpak', 'override', '--user', '--reset', APP], 'reset-overrides')
            self.run(['flatpak', 'run', '--command=sh', APP, '-c', 'test ! -r "$1/example.cs"', 'sh', str(outside)],
                     'outside-content-denied-again')
            shutil.rmtree(outside)

    def git(self):
        result = self.app(['tree', 'https://github.com/octocat/Hello-World', '--exclude', 'empty-files', '--language', 'en'], 'public-clone', timeout=120)
        assert 'README' in result.stdout, result.stdout
        failed = self.app(['tree', 'https://github.com/octocat/devprojex-flatpak-missing-repository', '--language', 'en'],
                          'private-clone', success=False, timeout=60)
        assert 'already cloned' in failed.stderr.lower(), failed.stderr
        assert 'fatal:' not in failed.stderr.lower(), failed.stderr
        self.run(['flatpak', 'run', '--command=sh', APP, '-c',
                  'test ! -e "$HOME/.gitconfig" || test -r "$HOME/.gitconfig"'], 'host-gitconfig-readable')

    def gui(self):
        from gui_check import check_gui
        check_gui(self)

    def shim(self):
        assert len(self.client_command) == 1, 'Shim round-trip requires the self-contained check client from smoke.sh'
        home = self.fixture / 'shim-home'
        home.mkdir()
        launcher = home / '.local/bin/devprojex'
        location = self.run(['flatpak', 'info', '--show-location', APP], 'installed-location').stdout.strip()
        ordinary = str(Path(location) / 'files/lib/devprojex/DevProjex')
        self.run([*self.client_command, '--shim', str(home), ordinary], 'shim-ordinary-install')
        original = launcher.read_bytes()
        assert b'exec flatpak' not in original, original
        self.run([str(launcher), '--version'], 'shim-ordinary-version')
        inside = ['flatpak', 'run', '--command=' + self.client_command[0], APP, '--shim', str(home), '/app/lib/devprojex/DevProjex']
        self.run(inside, 'shim-flatpak-replace')
        sandbox = launcher.read_bytes()
        assert b'exec flatpak run ' + APP.encode() in sandbox, sandbox
        self.run([str(launcher), 'mcp', '--help'], 'shim-flatpak-help')
        self.run(inside, 'shim-flatpak-repeat')
        assert launcher.read_bytes() == sandbox, 'Repeated Flatpak setup changed the wrapper'
        self.run([*self.client_command, '--shim', str(home), ordinary], 'shim-ordinary-replace')
        assert launcher.read_bytes() == original, 'Returning to ordinary installation changed wrapper bytes'
        self.run([str(launcher), '--version'], 'shim-ordinary-version-again')

    def portal(self):
        version = self.run(['gdbus', 'call', '--session', '--dest', 'org.freedesktop.portal.Documents',
                            '--object-path', '/org/freedesktop/portal/documents', '--method',
                            'org.freedesktop.DBus.Properties.Get', 'org.freedesktop.portal.Documents', 'version'],
                           'portal-version', success=None, timeout=15)
        if version.returncode:
            (self.evidence / 'portal-unavailable.txt').write_text(version.stderr)
            return 'unavailable: see portal-version.stderr'
        exported = self.run(['flatpak', 'document-export', '--app=' + APP, '--allow-read', str(self.fixture)],
                            'portal-directory-export', success=None)
        if exported.returncode:
            exported = self.run(['flatpak', 'document-export', '--app=' + APP, '--allow-read', str(self.fixture / 'selected.cs')],
                                'portal-file-export')
        document_path = Path(exported.stdout.strip())
        document_id = document_path.parts[5]
        try:
            self.run(['gdbus', 'call', '--session', '--dest', 'org.freedesktop.portal.Documents',
                      '--object-path', '/org/freedesktop/portal/documents', '--method',
                      'org.freedesktop.portal.Documents.GetHostPaths', json.dumps([document_id])],
                     'portal-host-paths', success=None, timeout=15)
            if document_path.is_dir():
                self.app(['tree', document_path], 'portal-tree')
                command = self.app(['mcp', 'connect', document_path, '--client', 'claude-code', '--print', '--language', 'en'],
                                   'portal-host-command-refused')
                assert 'flatpak run' not in command.stdout, command.stdout
                assert 'portal' in command.stdout.lower(), command.stdout
        finally:
            self.run(['flatpak', 'document-unexport', '--doc-id', document_id], 'portal-unexport')
        return 'passed: real document export and safe command refusal; ' + version.stdout.strip()

    def close(self):
        (self.evidence / 'results.json').write_text(json.dumps(self.results, indent=2))
        shutil.rmtree(self.fixture)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--only', choices=['diagnostics', 'mcp', 'compression', 'live', 'paths', 'git', 'gui', 'portal', 'shim'])
    arguments = parser.parse_args()
    checks = Checks(arguments.evidence.resolve())
    try:
        for name in ['diagnostics', 'mcp', 'compression', 'live', 'paths', 'git', 'gui', 'portal', 'shim']:
            if name != 'diagnostics' and arguments.only and name != arguments.only:
                continue
            print('Checking ' + name, flush=True)
            try:
                checks.results[name] = getattr(checks, name)() or 'passed'
            except BaseException as failure:
                checks.results[name] = str(failure)
                raise
    finally:
        checks.close()
