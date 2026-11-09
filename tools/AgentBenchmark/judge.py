"""Blind pairwise judge (v3): two arms' answers to the same task, read in both orders.

Usage:
  python judge.py --tasks-file tasks/fourteen.json --ours server_b --theirs server_c --name b-vs-c
                  [--tasks T1,T2] [--answers-only] [--model ID] [--work DIR]

Every pair costs two model calls (four without --answers-only). Product and tool names are scrubbed
so the judge cannot tell which server produced which answer. A verdict counts only when both
orders agree; otherwise it is recorded as order-dependent. The judge is a second opinion: the key
metrics of keyscore.py come first.
"""
import argparse
import json
import re
import shutil
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

import benchlib

SCRUB = [
    (r'grep_repomix_output', 'grep_tool'), (r'read_repomix_output', 'lines_tool'),
    (r'pack_codebase', 'pack_tool'), (r'attach_packed_output', 'attach_tool'),
    (r'pack_remote_repository', 'remote_tool'), (r'generate_skill', 'skill_tool'),
    (r'file_system_read_file', 'fsfile_tool'), (r'file_system_read_directory', 'fsdir_tool'),
    (r'search_project', 'search_tool'), (r'get_file', 'file_tool'), (r'get_tree', 'tree_tool'),
    (r'read_pack', 'page_tool'), (r'list_projects', 'roots_tool'), (r'related_files', 'related_tool'),
    (r'get_dependencies_and_usages', 'related_tool'),
    (r'pack_context', 'bundle_tool'), (r'\banalyze\b', 'stats_tool'),
    (r'outputId', 'result_id'), (r'pack_id', 'result_id'),
    (r'repomix', 'the server'), (r'devprojex', 'the server'), (r'DPX-(MCP|CLI)-', 'ERR-'),
    (r'server_[a-z0-9]+\b', 'the server'),
]

ANSWER_CRITERIA = ('correctness', 'completeness', 'trust', 'actionability', 'preference')
TOOL_CRITERIA = ('convenience', 'agent_trust', 'efficiency')

ANSWER_PROMPT = """You are a senior engineer grading two anonymous answers to the same question about a real repository. Each answer was produced by an agent using a different code-context tool.

Question:
{task}

Reference facts (the grader's key; the agents never saw it):
{key}

Answer A:
{a}

Answer B:
{b}

How to read the reference: "core", "supporting" and "tests" are what a complete answer needs; "acceptable" lists files that are reasonable to name but not required; "unrelated" lists files that do not belong to this question.

You cannot see the repository. A file, line number, symbol or detail that the reference neither confirms nor contradicts is NOT an error: do not penalise it and do not list it as suspicious. Count as wrong only: claims that contradict the reference facts, files listed under "unrelated", internal contradictions, and paths or APIs that cannot exist given the reference.

Grade substance only, against the reference and internal consistency:
- correctness: fewer wrong claims about files, mechanism, values.
- completeness: covers more of the required files, tests and facts.
- trust: which answer you would act on without re-checking. Penalise contradictions with the reference, files the reference calls unrelated, internal contradictions and overconfidence; never penalise a detail only because you cannot verify it.
- actionability: which a developer could act on faster (precise file:line anchors, clear grouping, no padding).
- preference: overall, which answer you would rather receive.
Also give each answer 1-5 scores for the four criteria, and list up to three concrete wrong claims per answer under the rules above (empty list if none).
Reply with exactly one JSON object and nothing else:
{{"correctness": "A"|"B"|"tie", "completeness": "A"|"B"|"tie", "trust": "A"|"B"|"tie", "actionability": "A"|"B"|"tie", "preference": "A"|"B"|"tie",
 "scores": {{"A": {{"correctness": n, "completeness": n, "trust": n, "actionability": n}}, "B": {{"correctness": n, "completeness": n, "trust": n, "actionability": n}}}},
 "suspicious": {{"A": ["..."], "B": ["..."]}}, "reason": "<one sentence>"}}"""

TOOL_PROMPT = """Two AI agents solved the same repository question, each with a different anonymous code-context tool. You see each agent's own review of its tool and the measured cost of its run.

Question:
{task}

Tool A run: {ma}
Agent A's review of tool A:
{ea}

Tool B run: {mb}
Agent B's review of tool B:
{eb}

Judge the tools, not the answers:
- convenience: which tool was easier and less frustrating for its agent to use.
- agent_trust: which tool's results its agent trusted more, from what the agent reports.
- efficiency: which run got the job done with less reading and fewer calls.
Name the single biggest pain point of each tool in a short phrase.
Reply with exactly one JSON object and nothing else:
{{"convenience": "A"|"B"|"tie", "agent_trust": "A"|"B"|"tie", "efficiency": "A"|"B"|"tie", "pain": {{"A": "...", "B": "..."}}, "reason": "<one sentence>"}}"""


def scrub(text):
    for pattern, replacement in SCRUB:
        text = re.sub(pattern, replacement, text, flags=re.I)
    return text


def split_answer(summary):
    """The graded answer and the agent's tool review, both scrubbed, plus the run's measured cost."""
    answer = summary.get('answer') or ''
    cut = answer.find('## Experience')
    return {
        'answer': scrub((answer[:cut] if cut >= 0 else answer).strip()),
        'experience': scrub(answer[cut:].strip()) if cut >= 0 else '(none)',
        'calls': len(summary['toolCalls']),
        'errors': sum(1 for call in summary['toolCalls'] if call.get('isError')),
        # Judge v3 has always shown the new-token count here; kept so verdicts stay comparable.
        'context': summary['usage']['totalContextTokens'],
        'response_chars': summary.get('toolResponseChars', 0),
    }


def metrics_line(run):
    return (f"{run['calls']} tool calls, {run['errors']} tool errors, {run['context']} context tokens, "
            f"{run['response_chars']} characters of tool output")


def settle(first, second, criterion):
    """Maps the two readings back to arms; a verdict stands only when both orders agree."""
    as_first = {'A': 'ours', 'B': 'theirs', 'tie': 'tie'}
    as_second = {'A': 'theirs', 'B': 'ours', 'tie': 'tie'}
    a = as_first.get(first.get(criterion), '?')
    b = as_second.get(second.get(criterion), '?')
    return (a if a == b else 'disagree'), (a, b)


def combine(task_id, repeat, first, second, tool_first, tool_second):
    row = {'task': task_id, 'repeat': repeat}
    for criterion in ANSWER_CRITERIA:
        row[criterion], row[criterion + '_orders'] = settle(first, second, criterion)
    for criterion in TOOL_CRITERIA:
        row[criterion], row[criterion + '_orders'] = settle(tool_first, tool_second, criterion)

    def side(verdict, field, label):
        return ((verdict.get(field) or {}).get(label)) or ({} if field == 'scores' else [])

    row['scores'] = {'ours': [side(first, 'scores', 'A'), side(second, 'scores', 'B')],
                     'theirs': [side(first, 'scores', 'B'), side(second, 'scores', 'A')]}
    row['suspicious'] = {'ours': side(first, 'suspicious', 'A') + side(second, 'suspicious', 'B'),
                         'theirs': side(first, 'suspicious', 'B') + side(second, 'suspicious', 'A')}
    row['pain'] = {'ours': [(tool_first.get('pain') or {}).get('A'), (tool_second.get('pain') or {}).get('B')],
                   'theirs': [(tool_first.get('pain') or {}).get('B'), (tool_second.get('pain') or {}).get('A')]}
    row['reasons'] = [first.get('reason'), second.get('reason'), tool_first.get('reason'), tool_second.get('reason')]
    return row


def parse_verdict(text):
    match = re.search(r'\{[\s\S]*\}', text or '')
    if not match:
        return {}
    try:
        return json.loads(match.group(0))
    except json.JSONDecodeError:
        return {}


def make_asker(model):
    claude = shutil.which('claude') or shutil.which('claude.cmd')
    if not claude:
        raise benchlib.BenchmarkError('the claude CLI is not on PATH')

    def ask(prompt):
        result = subprocess.run(
            [claude, '-p', '--model', model, '--tools', '', '--strict-mcp-config', '--no-session-persistence',
             '--output-format', 'json', '--max-turns', '1'],
            input=prompt.encode('utf-8'), capture_output=True, timeout=600, check=False)
        try:
            reply = json.loads(result.stdout.decode('utf-8', 'replace'))
        except json.JSONDecodeError:
            return {}, 0.0
        return parse_verdict(reply.get('result', '')), reply.get('total_cost_usd', 0.0)

    return ask


def judge_pair(ask, task, ours, theirs, answers_only):
    base = {'task': task['text'], 'key': json.dumps(task['key'], ensure_ascii=False, indent=1)}
    first, c1 = ask(ANSWER_PROMPT.format(a=ours['answer'], b=theirs['answer'], **base))
    second, c2 = ask(ANSWER_PROMPT.format(a=theirs['answer'], b=ours['answer'], **base))
    if answers_only:
        # Key corrections change answer grading only; the tool verdict reads the agents' reviews.
        return (first, second, {}, {}), c1 + c2
    tool_first, c3 = ask(TOOL_PROMPT.format(task=task['text'], ma=metrics_line(ours), ea=ours['experience'],
                                            mb=metrics_line(theirs), eb=theirs['experience']))
    tool_second, c4 = ask(TOOL_PROMPT.format(task=task['text'], ma=metrics_line(theirs), ea=theirs['experience'],
                                             mb=metrics_line(ours), eb=ours['experience']))
    return (first, second, tool_first, tool_second), c1 + c2 + c3 + c4


def collect_pairs(results, tasks, task_ids, ours, theirs):
    """Pairs of sessions with the same task and repeat number; unmatched sessions are skipped."""
    pairs = []
    for task_id in task_ids:
        mine = {s['repeat']: s for s in benchlib.load_runs(results, task_id, ours)}
        other = {s['repeat']: s for s in benchlib.load_runs(results, task_id, theirs)}
        for repeat in sorted(set(mine) & set(other)):
            pairs.append((tasks[task_id], repeat, split_answer(mine[repeat]), split_answer(other[repeat])))
    return pairs


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--tasks-file', required=True)
    parser.add_argument('--ours', required=True)
    parser.add_argument('--theirs', required=True)
    parser.add_argument('--name', required=True, help='output name, for example b-vs-c')
    parser.add_argument('--tasks', default='', help='task ids, default all')
    parser.add_argument('--answers-only', action='store_true', help='skip the tool-experience verdict')
    parser.add_argument('--model', default=benchlib.DEFAULT_MODEL)
    parser.add_argument('--parallel', type=int, default=3)
    parser.add_argument('--work')
    args = parser.parse_args()
    try:
        tasks_data = benchlib.load_tasks(args.tasks_file)
        results = benchlib.results_dir(benchlib.work_root(args.work), tasks_data['series'])
        out_path = results / 'judge' / f'{args.name}.json'
        if out_path.exists():
            raise benchlib.BenchmarkError(f'{out_path} exists; pick a new --name')
        tasks = {task['id']: task for task in tasks_data['tasks']}
        task_ids = [t for t in args.tasks.split(',') if t] or list(tasks)
        unknown = [t for t in task_ids if t not in tasks]
        if unknown:
            raise benchlib.BenchmarkError(f'unknown tasks {unknown}')
        pairs = collect_pairs(results, tasks, task_ids, args.ours, args.theirs)
        if not pairs:
            raise benchlib.BenchmarkError(f'no session pairs of {args.ours} and {args.theirs} in {results}')
        ask = make_asker(args.model)
    except benchlib.BenchmarkError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1

    def run_pair(pair):
        task, repeat, ours, theirs = pair
        verdicts, cost = judge_pair(ask, task, ours, theirs, args.answers_only)
        row = combine(task['id'], repeat, *verdicts)
        print(json.dumps({k: row[k] for k in ('task', 'repeat', 'correctness', 'trust', 'preference')}), flush=True)
        return row, cost

    with ThreadPoolExecutor(max_workers=max(1, args.parallel)) as pool:
        judged = list(pool.map(run_pair, pairs))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    document = {'ours': args.ours, 'theirs': args.theirs, 'model': args.model, 'answersOnly': args.answers_only,
                'results': [row for row, _ in judged], 'costUsd': sum(cost for _, cost in judged)}
    out_path.write_text(json.dumps(document, indent=1, ensure_ascii=False), encoding='utf-8')
    print(f'judge cost ${document["costUsd"]:.3f}, written to {out_path}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
