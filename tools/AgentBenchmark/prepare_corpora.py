"""Clones the benchmark repositories at their pinned tags, builds the two special stands and verifies them.

Usage:
  python prepare_corpora.py --tasks-file tasks/fourteen.json [--tasks-file ...] [--work DIR] [--force]

Each clone goes to <work>/corpora/<repo>. After cloning, the script checks the commit, the tracked
file count, the file count on disk and that every path named in a task key exists. A passing clone
gets <work>/corpora/<repo>.stand.json, which the runner checks again before every session.
A clone that already passes is left alone; --force clones it again.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

import benchlib


# Long paths: some corpora exceed 260 characters under a deep work folder on Windows.
GIT_DEFAULTS = ('-c', 'core.longpaths=true', '-c', 'advice.detachedHead=false')


def git(*args, cwd=None, env=None):
    result = subprocess.run(['git', *GIT_DEFAULTS, *args], cwd=cwd, env=env, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise benchlib.BenchmarkError(f'git {" ".join(args)} failed: {result.stderr.strip()}')
    return result.stdout.strip()


def clone_pinned(repo, target, autocrlf):
    """Shallow clone of the tag with a fixed line-ending setting, so every machine checks out the same bytes."""
    setting = f'core.autocrlf={"true" if autocrlf else "false"}'
    benchlib.remove_tree(target)
    git('-c', setting, 'clone', '-q', '--depth', '1', '--branch', repo['tag'], '--config', setting,
        '--config', 'core.longpaths=true', repo['url'], str(target))
    head = git('-C', str(target), 'rev-parse', 'HEAD')
    if head != repo['commit']:
        raise benchlib.BenchmarkError(f'{target.name}: tag {repo["tag"]} resolves to {head}, expected {repo["commit"]}')


def clone_local(source, target, url, autocrlf):
    setting = f'core.autocrlf={"true" if autocrlf else "false"}'
    benchlib.remove_tree(target)
    git('-c', setting, 'clone', '-q', '--config', setting, '--config', 'core.longpaths=true', str(source), str(target))
    git('-C', str(target), 'remote', 'set-url', 'origin', url)


def apply_edits(root, edits):
    """Replaces exact text, written with '\\n' in the task file, in the file's own line endings."""
    for edit in edits:
        path = Path(root) / edit['path']
        data = path.read_bytes()
        newline = '\r\n' if b'\r\n' in data else '\n'
        old = edit['old'].replace('\n', newline).encode('utf-8')
        new = edit['new'].replace('\n', newline).encode('utf-8')
        if old not in data:
            raise benchlib.BenchmarkError(f'{edit["path"]}: the text to replace is not in the file')
        path.write_bytes(data.replace(old, new, 1))


def build_uncommitted_edits(base_root, target, base, fixture, autocrlf):
    # Copying a fresh clone gives every file a new timestamp, so the copied index is stale and git
    # has to recheck content: the condition a change review on a CRLF checkout runs into. Do not run
    # git status in the copy; it would refresh the index and remove the condition.
    with tempfile.TemporaryDirectory(dir=target.parent, prefix=f'.{target.name}-') as scratch:
        fresh = Path(scratch) / 'clone'
        clone_local(base_root, fresh, base['url'], autocrlf)
        benchlib.remove_tree(target)
        shutil.copytree(fresh, target, copy_function=shutil.copy)
        benchlib.remove_tree(fresh)
    apply_edits(target, fixture['edits'])


def build_commit(base_root, target, base, fixture, autocrlf):
    clone_local(base_root, target, base['url'], autocrlf)
    apply_edits(target, fixture['edits'])
    commit = fixture['commit']
    env = {**os.environ,
           'GIT_AUTHOR_NAME': commit['author'], 'GIT_AUTHOR_EMAIL': commit['email'], 'GIT_AUTHOR_DATE': commit['date'],
           'GIT_COMMITTER_NAME': commit['author'], 'GIT_COMMITTER_EMAIL': commit['email'],
           'GIT_COMMITTER_DATE': commit['date']}
    git('-C', str(target), '-c', 'commit.gpgsign=false', 'commit', '-q', '-a', '-m', commit['message'], env=env)
    parent = git('-C', str(target), 'rev-parse', 'HEAD~1')
    if parent != base['commit']:
        raise benchlib.BenchmarkError(f'{target.name}: the local commit sits on {parent}, expected {base["commit"]}')
    expected = commit.get('expectHead')
    head = git('-C', str(target), 'rev-parse', 'HEAD')
    if expected and head != expected:
        raise benchlib.BenchmarkError(f'{target.name}: the local commit is {head}, expected {expected}')


def expected_files(tasks_data, name):
    repo = tasks_data['repos'][name]
    return repo['files'] if 'fixture' not in repo else tasks_data['repos'][repo['fixture']['base']]['files']


def verify_fresh(tasks_data, name, root):
    """Checks a clone just built; returns the manifest fields."""
    files = expected_files(tasks_data, name)
    # git ls-files reads the index without refreshing it, so it is safe on the stale fixture.
    tracked = len([line for line in git('-C', str(root), 'ls-files').splitlines() if line])
    on_disk = benchlib.count_worktree_files(root)
    if tracked != files or on_disk != files:
        raise benchlib.BenchmarkError(f'{name}: {tracked} tracked and {on_disk} files on disk, expected {files}')
    missing = benchlib.missing_key_paths(tasks_data, name, root)
    if missing:
        raise benchlib.BenchmarkError(f'{name}: key paths missing from the clone: {missing}')
    return {'head': benchlib.git_head(root), 'files': files}


def write_manifest(work, tasks_data, name, fields):
    repo = tasks_data['repos'][name]
    pinned = repo if 'fixture' not in repo else tasks_data['repos'][repo['fixture']['base']]
    manifest = {'repo': name, 'url': pinned['url'], 'tag': pinned['tag'], 'commit': pinned['commit'],
                'fixture': repo.get('fixture', {}).get('kind'), 'autocrlf': tasks_data['autocrlf'],
                'preparedAt': datetime.now(timezone.utc).isoformat(timespec='seconds'), **fields}
    benchlib.stand_manifest_path(work, name).write_text(json.dumps(manifest, indent=1) + '\n', encoding='utf-8')


def is_prepared(work, tasks_data, name):
    root = benchlib.corpus_dir(work, name)
    return (not benchlib.verify_corpus(work, name)) and not benchlib.missing_key_paths(tasks_data, name, root)


def prepare(work, tasks_data, force):
    repos = tasks_data['repos']
    autocrlf = tasks_data['autocrlf']
    ordered = [n for n in repos if 'fixture' not in repos[n]] + [n for n in repos if 'fixture' in repos[n]]
    for name in ordered:
        if not force and is_prepared(work, tasks_data, name):
            print(f'{name}: ok (already prepared)')
            continue
        benchlib.stand_manifest_path(work, name).unlink(missing_ok=True)
        target = benchlib.corpus_dir(work, name)
        repo = repos[name]
        fixture = repo.get('fixture')
        if fixture is None:
            clone_pinned(repo, target, autocrlf)
        else:
            base_name = fixture['base']
            builder = build_uncommitted_edits if fixture['kind'] == 'uncommitted-edits' else build_commit
            builder(benchlib.corpus_dir(work, base_name), target, repos[base_name], fixture, autocrlf)
        fields = verify_fresh(tasks_data, name, target)
        write_manifest(work, tasks_data, name, fields)
        print(f'{name}: prepared at {fields["head"][:12]}, {fields["files"]} files')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--tasks-file', action='append', required=True)
    parser.add_argument('--work')
    parser.add_argument('--force', action='store_true', help='clone again even when the clone passes its checks')
    args = parser.parse_args()
    try:
        work = benchlib.work_root(args.work)
        (work / 'corpora').mkdir(parents=True, exist_ok=True)
        for tasks_file in args.tasks_file:
            prepare(work, benchlib.load_tasks(tasks_file), args.force)
    except benchlib.BenchmarkError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
