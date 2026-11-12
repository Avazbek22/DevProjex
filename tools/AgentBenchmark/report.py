"""Markdown report of one series: key metrics, session costs, per-task numbers, judge verdicts, tool use.

Usage:
  python report.py --tasks-file tasks/fourteen.json --arms server_b=v5.2.2,server_c=Repomix
                   [--tasks T1,T2] [--skip-invented] [--work DIR] > report.md

--arms gives the column order and labels; without it every arm with sessions is shown under its
own name. Judge tables are read from every file in <results>/judge whose two arms are both shown.
"""
import argparse
import json
import re
import statistics
import sys
from pathlib import Path

import benchlib
from keyscore import corpus_roots
from judge import ANSWER_CRITERIA, TOOL_CRITERIA


def parse_arms(value, results):
    if not value:
        return [(arm, arm) for arm in benchlib.arms_with_runs(results)]
    arms = []
    for item in value.split(','):
        name, _, label = item.partition('=')
        arms.append((name, label or name))
    return arms


def self_trust(summary):
    match = re.search(r'Trust score[^0-9]*([1-5])', summary.get('answer') or '', re.I)
    return int(match.group(1)) if match else None


def table(header, rows):
    lines = ['| ' + ' | '.join(header) + ' |', '|' + '---|' * len(header)]
    lines += ['| ' + ' | '.join(str(cell) for cell in row) + ' |' for row in rows]
    return '\n'.join(lines)


def key_metrics_table(scores, arms, cost_precision=2):
    rows = [
        ('Key versions', lambda s: benchlib.format_key_versions(s['key_versions'])),
        ('Sessions', lambda s: s['sessions']),
        ('Required files', lambda s: f"{s['files']}/{s['files_needed']}"),
        ('Required tests', lambda s: f"{s['tests']}/{s['tests_needed']}"),
        ('Known-wrong files', lambda s: s['known_wrong']),
        ('Invented paths', lambda s: s['invented']),
        ('Misses: never shown / listed / content shown', lambda s:
         f"{s['missed_never_shown']} / {s['missed_listed']} / {s['missed_content_shown']}"),
        ('Sessions with attribution', lambda s: s['attribution_sessions']),
        ('Tool calls', lambda s: s['calls']),
        ('Tool errors', lambda s: s['tool_errors']),
        ('Context tokens', lambda s: f"{s['context']:,}"),
        ('Tool output, chars', lambda s: f"{s['tool_output_chars']:,}"),
        ('Cost', lambda s: f"${s['cost']:.{cost_precision}f}"),
        ('Time, s (sum)', lambda s: f"{s['duration_ms'] / 1000:.0f}"),
    ]
    from mechanisms import COUNTERS
    rows.extend((name, lambda score, key=name: score[key]) for name in COUNTERS)
    return table(['Metric'] + [label for _, label in arms],
                 [[name] + [render(scores[arm]) for arm, _ in arms] for name, render in rows])


def per_task_table(results, tasks_data, arms, task_ids, roots, key_version=None):
    by_id = {task['id']: task for task in tasks_data['tasks']}
    labels = ' / '.join(label for _, label in arms)
    rows = []
    for task_id in task_ids:
        scores = [benchlib.score_arm(results, tasks_data, arm, [task_id], roots, key_version) for arm, _ in arms]
        task = by_id[task_id]
        language = tasks_data['repos'][task['repo']]['language']
        rows.append([f"{task_id} {task['kind']}", f"{task['repo']} ({language})",
                     ' / '.join(f"{s['files']}/{s['files_needed']}" for s in scores),
                     ' / '.join(f"{s['tests']}/{s['tests_needed']}" for s in scores),
                     ' / '.join(str(s['calls']) for s in scores),
                     ' / '.join(f"{s['context']:,}" for s in scores),
                     ' / '.join(f"{s['missed_never_shown']},{s['missed_listed']},{s['missed_content_shown']}" for s in scores)] + [
                     ' / '.join(','.join(str(s[f'after_{status}_{action}'])
                                         for action in ('range', 'whole', 'search', 'none')) for s in scores)
                     for status in ('structure', 'partial', 'not_returned')])
    return table(['Task', 'Repo', f'Files {labels}', 'Tests', 'Calls', 'Context', 'Misses never,listed,content',
                  'After structure: range,whole,search,none', 'After partial: range,whole,search,none',
                  'After not-returned: range,whole,search,none'], rows)


def judge_section(path, labels):
    document = json.loads(Path(path).read_text(encoding='utf-8'))
    rows = document['results']
    ours, theirs = labels[document['ours']], labels[document['theirs']]
    criteria = ANSWER_CRITERIA + (() if document.get('answersOnly') else TOOL_CRITERIA)
    lines = [f"### Judge {Path(path).stem}: {ours} vs {theirs} ({len(rows)} pairs, both orders, "
             f"${document['costUsd']:.2f})", '']
    counts = []
    for criterion in criteria:
        tally = {v: sum(1 for row in rows if row.get(criterion) == v) for v in ('ours', 'theirs', 'tie', 'disagree')}
        counts.append([criterion, tally['ours'], tally['theirs'], tally['tie'], tally['disagree']])
    lines.append(table(['Criterion', ours, theirs, 'Tie', 'Order-dependent'], counts))
    for side, label in (('ours', ours), ('theirs', theirs)):
        values = {k: [s[k] for row in rows for s in row['scores'][side] if isinstance(s.get(k), (int, float))]
                  for k in ('correctness', 'completeness', 'trust', 'actionability')}
        means = ', '.join(f'{k} {statistics.mean(v):.2f}' for k, v in values.items() if v)
        lines.append(f'\nScores 1-5 for {label}: {means or "none"}')
        flagged = [f"{row['task']}: {claim}" for row in rows for claim in row['suspicious'][side]]
        lines.append(f'Claims the judge flagged for {label} ({len(flagged)}): ' + ' | '.join(flagged[:10]))
    return '\n'.join(lines)


def tool_usage(results, arms, task_ids):
    lines = []
    for arm, label in arms:
        counts, errors = {}, []
        for task_id in task_ids:
            for summary in benchlib.load_runs(results, task_id, arm):
                for call in summary['toolCalls']:
                    name = call['name'].split('__')[-1]
                    counts[name] = counts.get(name, 0) + 1
                    if call.get('isError'):
                        errors.append(f"{task_id} r{summary['repeat']} {name} {json.dumps(call.get('input') or {})[:120]}")
        ordered = sorted(counts.items(), key=lambda item: -item[1])
        lines.append(f'- {label}: ' + ', '.join(f'{name} {count}' for name, count in ordered))
        lines += [f'  - error: {line}' for line in errors]
    return '\n'.join(lines)


def run_configuration(results, arms):
    rows = []
    for arm, label in arms:
        record_path = results / 'arms' / f'{arm}.json'
        record = json.loads(record_path.read_text(encoding='utf-8')) if record_path.is_file() else {}
        summaries = [json.loads(path.read_text(encoding='utf-8'))
                     for path in (results / 'runs').glob(f'*-{arm}-r*/summary.json')]
        models = sorted({s.get('usage', {}).get('model') for s in summaries if s.get('usage', {}).get('model')})
        rows.append([label, record.get('agent', 'claude'), record.get('model') or ', '.join(models) or 'unrecorded',
                     'calculated API estimate' if record.get('agent') == 'codex' else 'client reported'])
    return table(['Arm', 'Agent', 'Model', 'Cost'], rows)


def excluded_sessions(results, arms, task_ids):
    rows = []
    for arm, label in arms:
        for task_id in task_ids:
            for summary in benchlib.load_runs(results, task_id, arm, include_invalid=True):
                if not benchlib.eligible_run(summary):
                    rows.append([label, task_id, summary['repeat'], summary.get('error') or
                                 json.dumps(summary.get('provenance') or summary.get('isolation'), ensure_ascii=False),
                                 f"${summary['usage']['costUsd']:.6f}", summary['usage'].get('costComplete', False)])
    return ('\n## Excluded sessions (outside key metrics)\n\n' +
            table(['Arm', 'Task', 'Repeat', 'Reason', 'Calculated cost', 'Complete usage'], rows)) if rows else ''


def build_report(work, tasks_data, arms, task_ids, skip_invented, agent='claude', key_version=None):
    if key_version is not None:
        for task in tasks_data['tasks']:
            if task['id'] in task_ids:
                benchlib.task_key(task, key_version)
    results = benchlib.results_dir(work, tasks_data['series'], agent)
    benchlib.validate_series_agent(results, [arm for arm, _ in arms], task_ids, agent)
    roots = corpus_roots(work, tasks_data, task_ids, skip_invented)
    scores = {arm: benchlib.score_arm(results, tasks_data, arm, task_ids, roots, key_version) for arm, _ in arms}
    trusts = {arm: [self_trust(s) for t in task_ids for s in benchlib.load_runs(results, t, arm)] for arm, _ in arms}
    parts = [
        f"# Series {results.name}: {', '.join(label for _, label in arms)}", '',
        f"{len(task_ids)} tasks. Context tokens are every input token the model processed, cached input included.",
        '', '## Run configuration', '', run_configuration(results, arms),
        '', '## Key metrics', '', key_metrics_table(scores, arms, 6 if agent == 'codex' else 2), '',
        'Agent self-trust scores: ' + ' / '.join(' '.join(str(t) for t in trusts[arm] if t) or '-' for arm, _ in arms),
        '', '## Per task', '', per_task_table(results, tasks_data, arms, task_ids, roots, key_version),
    ]
    labels = dict(arms)
    judge_files = sorted((results / 'judge').glob('*.json')) if (results / 'judge').is_dir() else []
    shown = [path for path in judge_files
             if {json.loads(path.read_text(encoding='utf-8')).get(k) for k in ('ours', 'theirs')} <= set(labels)]
    if shown:
        parts += ['', '## Blind judge (second opinion)', '']
        parts += ['\n\n'.join(judge_section(path, labels) for path in shown)]
    parts += ['', '## Tool calls by tool', '', tool_usage(results, arms, task_ids)]
    parts += [excluded_sessions(results, arms, task_ids)]
    return '\n'.join(parts) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--tasks-file', required=True)
    parser.add_argument('--arms', default='', help='server_b=label,server_c=label')
    parser.add_argument('--tasks', default='')
    parser.add_argument('--key-version', type=benchlib.parse_key_version,
                        help='override recorded key versions with a number or current; absent metadata uses version 1')
    parser.add_argument('--skip-invented', action='store_true')
    parser.add_argument('--work')
    parser.add_argument('--agent', choices=('claude', 'codex'), default='claude')
    args = parser.parse_args()
    try:
        tasks_data = benchlib.load_tasks(args.tasks_file)
        work = benchlib.work_root(args.work)
        arms = parse_arms(args.arms, benchlib.results_dir(work, tasks_data['series'], args.agent))
        if not arms:
            raise benchlib.BenchmarkError('no sessions to report')
        task_ids = [t for t in args.tasks.split(',') if t] or [task['id'] for task in tasks_data['tasks']]
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stdout.write(build_report(work, tasks_data, arms, task_ids, args.skip_invented, args.agent, args.key_version))
    except benchlib.BenchmarkError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
