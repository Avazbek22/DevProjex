"""Key metrics per arm: required files and tests named, known-wrong files, invented paths, calls, context, cost.

Usage:
  python keyscore.py --tasks-file tasks/fourteen.json [--arms server_b,server_c] [--tasks T1,T2]
                     [--by-task] [--json] [--skip-invented] [--work DIR]

These are the primary numbers of a series. Invented paths are checked against the prepared clones,
which are verified first: an emptied clone would turn every path into an invented one.
"""
import argparse
import json
import sys

import benchlib


def corpus_roots(work, tasks_data, task_ids, skip_invented):
    repos = sorted({task['repo'] for task in tasks_data['tasks'] if task['id'] in task_ids})
    if skip_invented:
        return {repo: None for repo in repos}
    problems = [problem for repo in repos for problem in benchlib.verify_corpus(work, repo)]
    if problems:
        raise benchlib.BenchmarkError('\n  '.join(problems + ['or pass --skip-invented']))
    return {repo: benchlib.corpus_dir(work, repo) for repo in repos}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--tasks-file', required=True)
    parser.add_argument('--arms', default='', help='default: every arm with sessions')
    parser.add_argument('--tasks', default='', help='default: every task')
    parser.add_argument('--by-task', action='store_true')
    parser.add_argument('--json', action='store_true')
    parser.add_argument('--key-version', type=int, help='override recorded key versions; absent metadata uses version 1')
    parser.add_argument('--skip-invented', action='store_true', help='do not count invented paths')
    parser.add_argument('--work')
    parser.add_argument('--agent', choices=('claude', 'codex'), default='claude')
    args = parser.parse_args()
    try:
        tasks_data = benchlib.load_tasks(args.tasks_file)
        work = benchlib.work_root(args.work)
        results = benchlib.results_dir(work, tasks_data['series'], args.agent)
        arms = [a for a in args.arms.split(',') if a] or benchlib.arms_with_runs(results)
        task_ids = [t for t in args.tasks.split(',') if t] or [task['id'] for task in tasks_data['tasks']]
        benchlib.validate_series_agent(results, arms, task_ids, args.agent)
        roots = corpus_roots(work, tasks_data, task_ids, args.skip_invented)
    except benchlib.BenchmarkError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1
    if not arms:
        print(f'no sessions in {results}', file=sys.stderr)
        return 1
    try:
        for task in tasks_data['tasks']:
            if task['id'] in task_ids and args.key_version is not None:
                benchlib.task_key(task, args.key_version)
        table = {arm: {'all': benchlib.score_arm(results, tasks_data, arm, task_ids, roots, args.key_version)} for arm in arms}
    except benchlib.BenchmarkError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1
    if args.by_task:
        for arm in arms:
            for task_id in task_ids:
                table[arm][task_id] = benchlib.score_arm(results, tasks_data, arm, [task_id], roots, args.key_version)
    if args.json:
        print(json.dumps(table, indent=1))
        return 0
    width = max(len(arm) for arm in arms)
    for arm in arms:
        print(f"{arm:{width}}  {table[arm]['all']['sessions']:3} sessions  {benchlib.format_score(table[arm]['all'])}")
        if args.by_task:
            for task_id in task_ids:
                print(f"{'':{width}}  {task_id:>4}  {benchlib.format_score(table[arm][task_id])}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
