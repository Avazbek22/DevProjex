"""Builds DevProjex hosts at given commits and installs a pinned Repomix version.

Usage:
  python build_hosts.py --host v522=a194709a [--host master=<sha> ...] [--repomix 1.18.1] [--source REPO] [--work DIR]

Each host is checked out from a local clone of --source (default: the repository holding this tool)
into a temporary folder, built in Release with one MSBuild node, and copied to <work>/hosts/<name>/
with host.json recording the full commit. A host name that already exists for another commit is an
error unless --force is given: a reused name once made two builds look like one.
The commit must exist in --source; fetch it there first if it does not.
"""
import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

import benchlib

PROJECT = 'Apps/TerminalHost/DevProjex.TerminalHost.csproj'
EXECUTABLE_NAMES = ('devprojex.exe', 'devprojex')


def run(args, cwd=None, log=None):
    result = subprocess.run(args, cwd=cwd, capture_output=True, text=True, check=False)
    if log is not None:
        Path(log).write_text(result.stdout + result.stderr, encoding='utf-8')
    if result.returncode != 0:
        detail = f'see {log}' if log else (result.stderr or result.stdout).strip()[-2000:]
        raise benchlib.BenchmarkError(f'{" ".join(args[:3])} failed: {detail}')
    return result.stdout.strip()


def parse_host(value):
    name, separator, commit = value.partition('=')
    if not separator or not name or not commit or not name.replace('-', '').replace('.', '').isalnum():
        raise benchlib.BenchmarkError(f'--host {value!r} must look like NAME=COMMIT, for example v522=a194709a')
    return name, commit


def find_output(checkout):
    """The folder of the built executable under bin/Release; exactly one is expected."""
    found = [path for name in EXECUTABLE_NAMES
             for path in (Path(checkout) / 'Apps' / 'TerminalHost' / 'bin' / 'Release').rglob(name)
             if path.is_file()]
    if len(found) != 1:
        raise benchlib.BenchmarkError(f'expected one built executable, found {[str(p) for p in found]}')
    return found[0]


def build_host(work, source, name, commit, force):
    target = work / 'hosts' / name
    record_path = target / 'host.json'
    full = run(['git', '-C', str(source), 'rev-parse', '--verify', f'{commit}^{{commit}}'])
    if record_path.is_file():
        recorded = json.loads(record_path.read_text(encoding='utf-8'))
        if recorded.get('commit') == full and (target / recorded['executable']).is_file():
            print(f'{name}: ok (already built at {full[:12]})')
            return
        if not force:
            raise benchlib.BenchmarkError(f'host {name} already holds {recorded.get("commit")}; pick a new name or pass --force')
    (work / 'hosts').mkdir(parents=True, exist_ok=True)
    log = work / 'hosts' / f'build-{name}.log'
    with tempfile.TemporaryDirectory(prefix=f'devprojex-{name}-') as scratch:
        checkout = Path(scratch) / 'src'
        run(['git', '-c', 'core.longpaths=true', 'clone', '-q', '--no-checkout', str(source), str(checkout)])
        run(['git', '-C', str(checkout), '-c', 'core.longpaths=true', '-c', 'advice.detachedHead=false',
             'checkout', '-q', '--detach', full])
        # One node and no parallel project builds: benchmark machines run one heavy command at a time.
        run(['dotnet', 'build', PROJECT, '-c', 'Release', '-m:1', '-p:BuildInParallel=false', '-v', 'q', '-nologo'],
            cwd=checkout, log=log)
        executable = find_output(checkout)
        benchlib.remove_tree(target)
        shutil.copytree(executable.parent, target)
        benchlib.remove_tree(checkout)
    record = {'name': name, 'commit': full, 'executable': executable.name,
              'builtAt': datetime.now(timezone.utc).isoformat(timespec='seconds')}
    record_path.write_text(json.dumps(record, indent=1) + '\n', encoding='utf-8')
    print(f'{name}: built {full[:12]}')


def install_repomix(work, version):
    target = work / 'repomix' / version
    script = target / 'node_modules' / 'repomix' / 'bin' / 'repomix.cjs'
    if script.is_file():
        print(f'repomix {version}: ok (already installed)')
        return
    target.mkdir(parents=True, exist_ok=True)
    npm = shutil.which('npm') or shutil.which('npm.cmd')
    if not npm:
        raise benchlib.BenchmarkError('npm is not on PATH')
    run([npm, 'install', '--prefix', str(target), '--no-save', '--ignore-scripts', f'repomix@{version}'])
    installed = run(['node', str(script), '--version'])
    if version not in installed:
        raise benchlib.BenchmarkError(f'repomix reports {installed}, expected {version}')
    print(f'repomix {version}: installed')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--host', action='append', default=[], help='NAME=COMMIT, repeatable')
    parser.add_argument('--repomix', help='exact Repomix version, for example 1.18.1')
    parser.add_argument('--source', default=str(benchlib.REPOSITORY_ROOT), help='local DevProjex repository')
    parser.add_argument('--work')
    parser.add_argument('--force', action='store_true', help='rebuild a host name that holds another commit')
    args = parser.parse_args()
    if not args.host and not args.repomix:
        parser.error('nothing to do: pass --host and/or --repomix')
    try:
        work = benchlib.work_root(args.work)
        for value in args.host:
            build_host(work, Path(args.source).resolve(), *parse_host(value), args.force)
        if args.repomix:
            install_repomix(work, args.repomix)
    except benchlib.BenchmarkError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
