"""Shared pieces of the agent benchmark: work folder, task files, run summaries and key metrics.

Nothing here talks to the network or to a model. Every path is either relative to this folder or
comes from an argument or the AGENT_BENCHMARK_WORK environment variable.
"""
import glob
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent
REPOSITORY_ROOT = TOOL_DIR.parent.parent
DEFAULT_MODEL = 'claude-haiku-4-5-20251001'
WORK_ENV = 'AGENT_BENCHMARK_WORK'
ARM_NAME = re.compile(r'^server_[a-z0-9]+$')
KEY_LISTS = ('core', 'supporting', 'tests', 'acceptable', 'unrelated')
FIXTURE_KINDS = ('uncommitted-edits', 'commit')


class BenchmarkError(Exception):
    """A precondition failed; the message tells the user what to fix."""


# ---------------------------------------------------------------- work folder

def work_root(argument=None):
    """Resolves the work folder: argument, then AGENT_BENCHMARK_WORK, then a folder in the home directory.

    A folder inside the repository is refused unless it is the ignored tools/AgentBenchmark/work,
    so clones, hosts and results never end up in a commit.
    """
    raw = argument or os.environ.get(WORK_ENV) or str(Path.home() / '.devprojex-agent-benchmark')
    root = Path(raw).expanduser().resolve()
    ignored = (TOOL_DIR / 'work').resolve()
    if _is_within(root, REPOSITORY_ROOT) and not _is_within(root, ignored):
        raise BenchmarkError(f'The work folder {root} is inside the repository. Use a folder outside it '
                             f'or {ignored}, which is ignored by git.')
    return root


def _is_within(path, parent):
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def corpus_dir(work, repo):
    return Path(work) / 'corpora' / repo


def stand_manifest_path(work, repo):
    # Kept beside the clone, never inside it, so the agent cannot see it.
    return Path(work) / 'corpora' / f'{repo}.stand.json'


def results_dir(work, series):
    return Path(work) / 'results' / series


def remove_tree(path):
    """Deletes a folder, clearing the read-only flag git puts on pack files (Windows refuses otherwise)."""
    def make_writable(function, target, _):
        os.chmod(target, 0o700)
        function(target)

    if Path(path).exists():
        handler = {'onexc': make_writable} if sys.version_info >= (3, 12) else {'onerror': make_writable}
        shutil.rmtree(path, **handler)


# ---------------------------------------------------------------- task files

def load_tasks(path):
    with open(path, encoding='utf-8') as handle:
        data = json.load(handle)
    errors = validate_tasks(data)
    if errors:
        raise BenchmarkError(f'{path} is not a valid task file:\n  ' + '\n  '.join(errors))
    return data


def validate_tasks(data):
    """Returns every structural problem of a task file; an empty list means it is usable."""
    errors = []
    if not isinstance(data, dict):
        return ['the file must hold one JSON object']
    if not isinstance(data.get('series'), str) or not re.fullmatch(r'[a-z0-9][a-z0-9-]*', data.get('series') or ''):
        errors.append('series must be a lowercase name such as "fourteen"')
    if not isinstance(data.get('autocrlf'), bool):
        errors.append('autocrlf must be true or false')
    repos = data.get('repos')
    if not isinstance(repos, dict) or not repos:
        return errors + ['repos must be a non-empty object']
    for name, repo in repos.items():
        errors.extend(_validate_repo(name, repo, repos))
    tasks = data.get('tasks')
    if not isinstance(tasks, list) or not tasks:
        return errors + ['tasks must be a non-empty list']
    seen = set()
    for task in tasks:
        errors.extend(_validate_task(task, repos, seen))
    used = {task.get('repo') for task in tasks if isinstance(task, dict)}
    errors.extend(f'repo {name} is not used by any task' for name in repos if name not in used)
    return errors


def _validate_repo(name, repo, repos):
    where = f'repo {name}'
    if not isinstance(repo, dict):
        return [f'{where} must be an object']
    errors = []
    if not repo.get('language'):
        errors.append(f'{where}: language is missing')
    fixture = repo.get('fixture')
    if fixture is None:
        if not str(repo.get('url', '')).startswith('https://'):
            errors.append(f'{where}: url must be an https clone URL')
        if not repo.get('tag'):
            errors.append(f'{where}: tag is missing')
        if not re.fullmatch(r'[0-9a-f]{40}', str(repo.get('commit', ''))):
            errors.append(f'{where}: commit must be a full 40-character SHA')
        if not isinstance(repo.get('files'), int) or repo['files'] <= 0:
            errors.append(f'{where}: files must be the positive tracked-file count of the clone')
        return errors
    base = fixture.get('base')
    if base not in repos or 'fixture' in repos.get(base, {}):
        errors.append(f'{where}: fixture base must name a pinned repo in the same file')
    if fixture.get('kind') not in FIXTURE_KINDS:
        errors.append(f'{where}: fixture kind must be one of {", ".join(FIXTURE_KINDS)}')
    edits = fixture.get('edits')
    if not isinstance(edits, list) or not edits:
        errors.append(f'{where}: fixture edits must be a non-empty list')
    else:
        for edit in edits:
            if not all(isinstance(edit.get(k), str) and edit.get(k) for k in ('path', 'old', 'new')):
                errors.append(f'{where}: every edit needs path, old and new')
    if fixture.get('kind') == 'commit':
        commit = fixture.get('commit') or {}
        missing = [k for k in ('message', 'author', 'email', 'date') if not commit.get(k)]
        if missing:
            errors.append(f'{where}: commit fixture needs {", ".join(missing)}')
    return errors


def _validate_task(task, repos, seen):
    if not isinstance(task, dict):
        return ['every task must be an object']
    tid = task.get('id')
    where = f'task {tid}'
    errors = []
    if not isinstance(tid, str) or not re.fullmatch(r'[A-Z][A-Z0-9]*', tid):
        errors.append(f'{where}: id must be short uppercase, such as T1')
    if tid in seen:
        errors.append(f'{where}: duplicate id')
    seen.add(tid)
    if task.get('repo') not in repos:
        errors.append(f'{where}: repo {task.get("repo")} is not declared')
    for field in ('kind', 'text'):
        if not isinstance(task.get(field), str) or not task[field].strip():
            errors.append(f'{where}: {field} is missing')
    key = task.get('key')
    if not isinstance(key, dict):
        return errors + [f'{where}: key is missing']
    for name in KEY_LISTS:
        values = key.get(name)
        if not isinstance(values, list):
            errors.append(f'{where}: key.{name} must be a list')
            continue
        for path in values:
            if not _is_clean_relative_path(path):
                errors.append(f'{where}: key.{name} has a bad path {path!r}')
    if not key.get('core'):
        errors.append(f'{where}: key.core must name at least one file')
    if not isinstance(key.get('notes'), str) or not key['notes'].strip():
        errors.append(f'{where}: key.notes is missing')
    wanted = set(key.get('core') or []) | set(key.get('supporting') or []) | set(key.get('tests') or [])
    clash = wanted & (set(key.get('unrelated') or []) | set(key.get('acceptable') or []))
    if clash:
        errors.append(f'{where}: {sorted(clash)} are both required and acceptable or unrelated')
    return errors


def _is_clean_relative_path(path):
    return (isinstance(path, str) and path and '\\' not in path and not path.startswith('/')
            and ':' not in path and '..' not in path.split('/'))


def key_paths(task):
    key = task['key']
    return [path for name in KEY_LISTS for path in key[name]]


def missing_key_paths(tasks_data, repo, root):
    """Key paths of the tasks on one repo that do not exist in its clone."""
    return [(task['id'], path) for task in tasks_data['tasks'] if task['repo'] == repo
            for path in key_paths(task) if not (Path(root) / path).is_file()]


# ---------------------------------------------------------------- corpora

def count_worktree_files(root):
    """Every tracked-looking entry outside .git: files and symbolic links alike, as git tracks both."""
    total = 0
    for folder, dirs, files in os.walk(root):
        if '.git' in dirs:
            dirs.remove('.git')
        linked = [name for name in dirs if os.path.islink(os.path.join(folder, name))]
        total += len(files) + len(linked)
    return total


def git_head(root):
    """HEAD through git log, which reads the index of a stale fixture without refreshing it."""
    result = subprocess.run(['git', '-C', str(root), 'log', '-1', '--format=%H'],
                            capture_output=True, text=True, check=False)
    return result.stdout.strip() if result.returncode == 0 else None


def verify_corpus(work, repo):
    """Checks a prepared clone against its stand manifest; returns a list of problems."""
    manifest_path = stand_manifest_path(work, repo)
    root = corpus_dir(work, repo)
    if not manifest_path.is_file():
        return [f'{repo}: not prepared ({manifest_path} is missing); run prepare_corpora.py']
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    problems = []
    head = git_head(root)
    if head != manifest['head']:
        problems.append(f'{repo}: git log -1 gives {head or "nothing"}, expected {manifest["head"]}')
    files = count_worktree_files(root) if root.is_dir() else 0
    if files != manifest['files']:
        problems.append(f'{repo}: {files} files on disk, expected {manifest["files"]}; the clone was changed or emptied')
    return problems


# ---------------------------------------------------------------- run summaries

def run_dir_name(task, arm, repeat):
    return f'{task}-{arm}-r{repeat}'


def load_runs(results, task, arm):
    """Summaries of one task and arm, ordered by repeat. Failed attempts are not summaries."""
    pattern = os.path.join(glob.escape(str(results)), 'runs', f'{glob.escape(task)}-{glob.escape(arm)}-r*', 'summary.json')
    runs = []
    for path in glob.glob(pattern):
        match = re.search(r'-r(\d+)$', os.path.basename(os.path.dirname(path)))
        if match:
            with open(path, encoding='utf-8') as handle:
                runs.append((int(match.group(1)), json.load(handle)))
    return [summary for _, summary in sorted(runs, key=lambda item: item[0])]


def arms_with_runs(results):
    names = set()
    for path in glob.glob(os.path.join(glob.escape(str(results)), 'runs', '*', 'summary.json')):
        with open(path, encoding='utf-8') as handle:
            names.add(json.load(handle)['server'])
    return sorted(names)


def answer_section(answer, name):
    match = re.search(r'##\s*' + re.escape(name) + r'\s*\n([\s\S]*?)(?=\n##\s|$)', answer or '', re.I)
    return match.group(1) if match else ''


def context_tokens(usage):
    """Every input token the model processed, cached input included (the 'context tokens' of the log)."""
    return usage['input'] + usage['cacheRead'] + usage['cacheCreation']


# ---------------------------------------------------------------- key metrics
# Path extraction and matching are kept identical to the scorer used since 2026-11-08, so numbers
# measured with this tool are comparable with Docs/Benchmarks.

_PATH_WITH_FOLDER = re.compile(r'[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+\.[A-Za-z0-9]+')
_BARE_FILE = re.compile(
    r'(?<![A-Za-z0-9_./-])([A-Za-z0-9_-]+\.(?:go|py|ts|tsx|js|json|cs|java|rs|toml|md))(?![A-Za-z0-9_])')


def paths_in(text):
    found = {path.lstrip('./') for path in _PATH_WITH_FOLDER.findall(text or '')}
    found |= set(_BARE_FILE.findall(text or ''))
    return found


def mentions(key_path, found):
    return any(path == key_path
               or ('/' in path and (path.endswith('/' + key_path) or path.endswith(key_path)))
               or ('/' in key_path and '/' in path and key_path.endswith(path))
               for path in found)


def invented_paths(found, root):
    """Paths with a folder that do not exist in the clone."""
    return sorted(path for path in found
                  if '/' in path and not path.startswith(('http', 'node:')) and not (Path(root) / path).exists())


def empty_score():
    return dict(sessions=0, files=0, files_needed=0, tests=0, tests_needed=0, known_wrong=0, invented=0,
                calls=0, tool_errors=0, context=0, cost=0.0, duration_ms=0, tool_output_chars=0)


def score_session(summary, task, corpus_root):
    """Key metrics of one session. corpus_root=None skips the invented-path count."""
    key = task['key']
    found = paths_in(summary.get('answer') or '')
    needed = set(key['core']) | set(key['supporting'])
    tests = set(key['tests'])
    calls = summary['toolCalls']
    score = empty_score()
    score.update(
        sessions=1,
        files=sum(mentions(path, found) for path in needed), files_needed=len(needed),
        tests=sum(mentions(path, found) for path in tests), tests_needed=len(tests),
        known_wrong=sum(mentions(path, found) for path in key['unrelated']),
        invented=len(invented_paths(found, corpus_root)) if corpus_root is not None else 0,
        calls=len(calls) if isinstance(calls, list) else int(calls),
        tool_errors=sum(1 for call in calls if call.get('isError')) if isinstance(calls, list) else 0,
        context=context_tokens(summary['usage']), cost=summary['usage']['costUsd'],
        duration_ms=summary.get('durationMs') or 0, tool_output_chars=summary.get('toolResponseChars', 0),
    )
    return score


def add_scores(total, score):
    for name, value in score.items():
        total[name] += value
    return total


def score_arm(results, tasks_data, arm, task_ids, corpus_roots):
    """Sum of key metrics over all sessions of one arm. corpus_roots maps repo -> clone or None."""
    by_id = {task['id']: task for task in tasks_data['tasks']}
    total = empty_score()
    for task_id in task_ids:
        task = by_id[task_id]
        for summary in load_runs(results, task_id, arm):
            add_scores(total, score_session(summary, task, corpus_roots.get(task['repo'])))
    return total


def format_score(score):
    return (f"files {score['files']}/{score['files_needed']} · tests {score['tests']}/{score['tests_needed']} · "
            f"known-wrong {score['known_wrong']} · invented {score['invented']} · calls {score['calls']} · "
            f"ctx {score['context'] / 1e6:.2f}M · ${score['cost']:.2f}")
