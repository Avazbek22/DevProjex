"""Shared pieces of the agent benchmark: work folder, task files, run summaries and key metrics.

Nothing here talks to the network or to a model. Every path is either relative to this folder or
comes from an argument or the AGENT_BENCHMARK_WORK environment variable.
"""
import glob
import json
import os
import posixpath
import re
import shutil
import subprocess
import sys
from pathlib import Path, PurePosixPath, PureWindowsPath

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


def results_dir(work, series, agent='claude'):
    return Path(work) / 'results' / (series + '-codex' if agent == 'codex' else series)


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
    errors.extend(_validate_key(task.get('key'), where))
    version = task.get('keyVersion', 1)
    if type(version) is not int or version < 1:
        errors.append(f'{where}: keyVersion must be a positive integer')
        return errors
    previous = task.get('previousKeys', {})
    if not isinstance(previous, dict):
        return errors + [f'{where}: previousKeys must be an object']
    if version > 1 and '1' not in previous:
        errors.append(f'{where}: previousKeys must preserve version 1')
    for number, key in previous.items():
        if not isinstance(number, str) or not number.isdigit() or str(int(number)) != number or not 1 <= int(number) < version:
            errors.append(f'{where}: previous key version {number!r} must precede keyVersion')
        errors.extend(_validate_key(key, f'{where} key v{number}'))
    return errors


def _validate_key(key, where):
    errors = []
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
    keys = [key, *task.get('previousKeys', {}).values()]
    return list(dict.fromkeys(path for key in keys for name in KEY_LISTS for path in key[name]))


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


def eligible_run(summary):
    if summary.get('provenance', {}).get('status', 'passed') != 'passed':
        return False
    return summary.get('agent', 'claude') != 'codex' or (
        summary.get('eligibleForMetrics') is True and summary.get('isolation', {}).get('status') == 'passed'
        and not summary.get('isolation', {}).get('violations') and summary.get('usage', {}).get('costComplete') is True
        and not summary.get('error') and summary.get('exitCode') == 0)


def validate_series_agent(results, arms, task_ids, expected):
    for arm in arms:
        record_path = Path(results) / 'arms' / f'{arm}.json'
        if record_path.is_file():
            record = json.loads(record_path.read_text(encoding='utf-8'))
            if record.get('agent', 'claude') != expected:
                raise BenchmarkError(f'{arm} belongs to another agent; select the matching --agent series')
        for task_id in task_ids:
            for summary in load_runs(results, task_id, arm, include_invalid=True):
                if summary.get('agent', 'claude') != expected:
                    raise BenchmarkError(f'{summary["id"]} belongs to another agent; sessions cannot be mixed')


def load_runs(results, task, arm, include_invalid=False):
    """Summaries of one task and arm, ordered by repeat. Failed attempts are not summaries."""
    pattern = os.path.join(glob.escape(str(results)), 'runs', f'{glob.escape(task)}-{glob.escape(arm)}-r*', 'summary.json')
    runs = []
    for path in glob.glob(pattern):
        match = re.search(r'-r(\d+)$', os.path.basename(os.path.dirname(path)))
        if match:
            with open(path, encoding='utf-8') as handle:
                summary = json.load(handle)
                folder = Path(path).parent
                if (folder / 'stream.jsonl').is_file() and (folder / 'prompt.txt').is_file():
                    from provenance import inspect_session
                    manifest_path = Path(results).parent.parent / 'corpora' / f"{summary.get('repo', '')}.stand.json"
                    manifest = json.loads(manifest_path.read_text(encoding='utf-8')) if manifest_path.is_file() else None
                    summary['provenance'] = inspect_session(folder, manifest=manifest)
                    summary['_session_dir'] = str(folder)
                if include_invalid or eligible_run(summary):
                    runs.append((int(match.group(1)), summary))
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
# Relative-path suffix matching follows the historical scorer; absolute paths must belong to the corpus.

_PATH_WITH_FOLDER = re.compile(r'[A-Za-z0-9_.-]+(?:[\\/][A-Za-z0-9_.-]+)+\.[A-Za-z0-9]+')
_ABSOLUTE_PATH = re.compile(
    r'(?<![A-Za-z0-9_./\\:}>*-])(?:/?[A-Za-z]:[\\/]|[\\/]{2}|/)(?:[A-Za-z0-9_.-]+[\\/])*[A-Za-z0-9_.-]+\.[A-Za-z0-9]+')
_DELIMITED_PATH = re.compile(r'(?P<quote>[`\"\'])(?P<quoted>[^\r\n]*?)(?P=quote)|\]\((?P<link>[^\r\n)]*)\)')
_BARE_FILE = re.compile(
    r'(?<![A-Za-z0-9_./\\-])([A-Za-z0-9_-]+\.(?:go|py|ts|tsx|js|json|cs|java|rs|toml|md))(?![A-Za-z0-9_])')


def _absolute_answer_path(path):
    return path.startswith('/') or bool(re.match(r'^[A-Za-z]:/', path))


def _normalize_answer_path(path, root=None):
    path = path.replace('\\', '/')
    if re.match(r'^/[A-Za-z]:/', path):
        path = path[1:]
    path = posixpath.normpath(path)
    if _absolute_answer_path(path) and root is not None:
        root_path = str(root).replace('\\', '/')
        if re.match(r'^/[A-Za-z]:/', root_path):
            root_path = root_path[1:]
        root_path = posixpath.normpath(root_path)
        windows = bool(re.match(r'^(?:[A-Za-z]:/|//)', path))
        root_windows = bool(re.match(r'^(?:[A-Za-z]:/|//)', root_path))
        if windows == root_windows:
            path_type = PureWindowsPath if windows else PurePosixPath
            try:
                return path_type(path).relative_to(path_type(root_path)).as_posix()
            except ValueError:
                pass
    return path


def paths_in(text, root=None):
    found = set()

    def collect_absolute(match):
        candidate = match.groupdict().get('quoted') or match.groupdict().get('link') or match.group(0)
        candidate = re.sub(r'(\.[A-Za-z0-9]+)(?::\d+(?::\d+)?|#L\d+(?:-L?\d+)?)$', r'\1', candidate)
        normalized = _normalize_answer_path(candidate)
        if not _absolute_answer_path(normalized) or not re.search(r'\.[A-Za-z0-9]+$', candidate):
            return match.group(0)
        found.add(_normalize_answer_path(candidate, root))
        # Do not extract a second, relative suffix or a bare filename from an absolute path.
        return ' ' * len(match.group(0))

    remaining = _DELIMITED_PATH.sub(collect_absolute, text or '')
    remaining = _ABSOLUTE_PATH.sub(collect_absolute, remaining)
    found |= {_normalize_answer_path(path) for path in _PATH_WITH_FOLDER.findall(remaining)}
    found |= set(_BARE_FILE.findall(remaining))
    return found


def mentions(key_path, found):
    return any(not _absolute_answer_path(path) and not path.startswith('../') and (path == key_path
               or ('/' in path and (path.endswith('/' + key_path) or path.endswith(key_path)))
               or ('/' in key_path and '/' in path and key_path.endswith(path)))
               for path in found)


def invented_paths(found, root):
    """Paths with a folder that do not exist in the clone."""
    return sorted(path for path in found
                  if '/' in path and not path.startswith(('http', 'node:'))
                  and (_absolute_answer_path(path) or path.startswith('../') or not (Path(root) / path).exists()))


def task_key(task, version):
    current = task.get('keyVersion', 1)
    if version == 'current':
        version = current
    if type(version) is not int or version < 1:
        raise BenchmarkError(f"task {task['id']}: invalid key version {version!r}")
    if version == current:
        return task['key']
    key = task.get('previousKeys', {}).get(str(version))
    if key is None:
        raise BenchmarkError(f"task {task['id']}: key version {version} is not available")
    return key


def parse_key_version(value):
    if value == 'current':
        return value
    number = int(value)
    if number < 1:
        raise ValueError('key version must be positive or current')
    return number


def format_key_versions(versions):
    return ', '.join(f"{task}=" + '/'.join(f'v{number}' for number in sorted(numbers))
                     for task, numbers in sorted(versions.items())) or 'none'


def empty_score():
    from mechanisms import COUNTERS
    return dict.fromkeys(COUNTERS, 0) | dict(sessions=0, files=0, files_needed=0, tests=0, tests_needed=0, known_wrong=0, invented=0,
                calls=0, tool_errors=0, context=0, cost=0.0, duration_ms=0, tool_output_chars=0, key_versions={},
                attribution_sessions=0, missed_never_shown=0, missed_listed=0, missed_content_shown=0,
                named_never_shown=0, named_listed=0, named_content_shown=0)


def score_session(summary, task, corpus_root, key_version=None):
    """Key metrics of one session. corpus_root=None skips the invented-path count."""
    if not eligible_run(summary):
        raise BenchmarkError('A failed, isolation-violating or corpus-violating session cannot enter key metrics')
    version = key_version if key_version is not None else summary.get('keyVersion', 1)
    if version == 'current':
        version = task.get('keyVersion', 1)
    key = task_key(task, version)
    from transcripts import recorded_root
    folder = summary.get('_session_dir')
    original_root = recorded_root(folder) if folder else None
    found = paths_in(summary.get('answer') or '', original_root or corpus_root)
    needed = set(key['core']) | set(key['supporting'])
    tests = set(key['tests'])
    calls = summary['toolCalls']
    score = empty_score()
    score.update(
        sessions=1, key_versions={task['id']: [version]},
        files=sum(mentions(path, found) for path in needed), files_needed=len(needed),
        tests=sum(mentions(path, found) for path in tests), tests_needed=len(tests),
        known_wrong=sum(mentions(path, found) for path in key['unrelated']),
        invented=len(invented_paths(found, corpus_root)) if corpus_root is not None else 0,
        calls=len(calls) if isinstance(calls, list) else int(calls),
        tool_errors=sum(1 for call in calls if call.get('isError')) if isinstance(calls, list) else 0,
        context=context_tokens(summary['usage']), cost=summary['usage']['costUsd'],
        duration_ms=summary.get('durationMs') or 0, tool_output_chars=summary.get('toolResponseChars', 0),
    )
    if folder:
        from attribution import attribute
        from transcripts import recorded_calls
        calls = recorded_calls(Path(folder) / 'stream.jsonl')
        entries = attribute(calls, task,
                            summary.get('answer') or '', original_root or corpus_root, version)
        from mechanisms import count
        score.update(count(calls))
        score['attribution_sessions'] = 1
        for entry in entries:
            category = ('named_' if entry['named'] else 'missed_') + entry['exposure'].replace('-', '_')
            score[category] += 1
    return score


def add_scores(total, score):
    for name, value in score.items():
        if name == 'key_versions':
            for task, versions in value.items():
                total[name][task] = sorted(set(total[name].get(task, [])) | set(versions))
        else:
            total[name] += value
    return total


def score_arm(results, tasks_data, arm, task_ids, corpus_roots, key_version=None):
    """Sum of key metrics over all sessions of one arm. corpus_roots maps repo -> clone or None."""
    by_id = {task['id']: task for task in tasks_data['tasks']}
    total = empty_score()
    for task_id in task_ids:
        task = by_id[task_id]
        for summary in load_runs(results, task_id, arm):
            add_scores(total, score_session(summary, task, corpus_roots.get(task['repo']), key_version))
    return total


def format_score(score):
    return (f"keys {format_key_versions(score['key_versions'])} · files {score['files']}/{score['files_needed']} · tests {score['tests']}/{score['tests_needed']} · "
            f"known-wrong {score['known_wrong']} · invented {score['invented']} · calls {score['calls']} · "
            f"ctx {score['context'] / 1e6:.2f}M · ${score['cost']:.2f} · "
            f"misses never/listed/content {score['missed_never_shown']}/{score['missed_listed']}/{score['missed_content_shown']} "
            f"({score['attribution_sessions']} transcripts) · structure large/no-room {score['structure_large']}/{score['structure_no_room']} · "
            f"read-as {score['read_as_ranges'] + score['read_as_combined'] + score['read_as_folders']} · symbols-as-text {score['symbols_as_text']} · tool-errors {score['tool_errors']}")
