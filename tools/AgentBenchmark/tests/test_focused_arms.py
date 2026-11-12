"""Server configurations and built-in file-tool sessions without model calls."""
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import attribution
import benchlib
import judge
import mechanisms
import replay
import report
from provenance import check_builtin, inspect_session
from transcripts import first_request_context, recorded_calls


class FocusedArmTests(unittest.TestCase):
    def test_first_assistant_usage_is_shared_by_summary_fallback_and_report(self):
        with tempfile.TemporaryDirectory() as scratch:
            folder = Path(scratch)
            stream = folder / 'stream.jsonl'
            stream.write_text('\n'.join(json.dumps(e) for e in [
                {'type': 'system'},
                {'type': 'assistant', 'message': {'usage': {'input_tokens': 2,
                    'cache_read_input_tokens': 3, 'cache_creation_input_tokens': 4}, 'content': []}},
                {'type': 'assistant', 'message': {'usage': {'input_tokens': 100}, 'content': []}}]), encoding='utf-8')
            self.assertEqual(9, first_request_context(stream))
            summary = {'id': 'T1-server_x-r1', 'task': 'T1', 'server': 'server_x', 'kind': 'builtin',
                '_session_dir': scratch, 'answer': '', 'toolCalls': [], 'usage': {'input': 1,
                'cacheRead': 0, 'cacheCreation': 0, 'costUsd': 0}, 'repo': 'fixture'}
            task = {'id': 'T1', 'key': {'core': [], 'supporting': [], 'tests': [], 'acceptable': [], 'unrelated': []}}
            with contextlib.redirect_stderr(io.StringIO()) as notices:
                old = benchlib.score_session(summary, task, None)
            self.assertEqual([9], old['first_request_context_tokens'])
            self.assertIn('skipped', notices.getvalue())
            summary['usage']['firstRequestContextTokens'] = 11
            fresh = benchlib.score_session(summary, task, None)
            self.assertEqual([11], fresh['first_request_context_tokens'])
            total = benchlib.empty_score()
            benchlib.add_scores(total, old)
            benchlib.add_scores(total, fresh)
            self.assertIn('| First request | 10.0 |', report.key_metrics_table({'server_x': total}, [('server_x', 'files')]))
            summary['agent'] = 'codex'
            summary.update(eligibleForMetrics=True, isolation={'status': 'passed'}, exitCode=0)
            summary['usage']['costComplete'] = True
            self.assertEqual([], benchlib.score_session(summary, task, None)['first_request_context_tokens'])
            stream.write_text('{"type":"assistant","message":{}}\n{"type":"assistant","message":{"usage":{"input_tokens":5}}}', encoding='utf-8')
            self.assertIsNone(first_request_context(stream))

    def test_builtin_calls_keep_counts_but_never_enter_mcp_attribution(self):
        with tempfile.TemporaryDirectory() as scratch:
            stream = Path(scratch) / 'stream.jsonl'
            stream.write_text('\n'.join(json.dumps(e) for e in [
                {'type': 'assistant', 'message': {'content': [{'type': 'tool_use', 'id': '1',
                    'name': 'Read', 'input': {'file_path': '/clone/file.ts'}}]}},
                {'type': 'user', 'message': {'content': [{'type': 'tool_result', 'tool_use_id': '1',
                    'content': 'export class Example {}'}]}}]), encoding='utf-8')
            self.assertEqual([], recorded_calls(stream))
            calls = recorded_calls(stream, include_builtin=True)
            self.assertEqual('Read', calls[0]['tool'])
            self.assertFalse(calls[0]['is_error'])
            self.assertEqual('passed', check_builtin(calls, '/clone', ['Read'])['status'])
            self.assertEqual(dict.fromkeys(mechanisms.COUNTERS, 0), mechanisms.count(recorded_calls(stream)))

    def test_builtin_isolation_checks_even_failed_calls_and_all_path_fields(self):
        for root, inside, outside in [('/clone', '/clone/a.ts', '/other/a.ts'),
                                      ('C:/clone', 'c:/clone/a.ts', 'D:/clone/a.ts')]:
            for key in ('file_path', 'path', 'pattern'):
                call = {'sequence': 1, 'tool': 'Read', 'arguments': {key: inside}, 'is_error': True}
                self.assertEqual('passed', check_builtin([call], root, ['Read'])['status'])
                call['arguments'][key] = outside
                self.assertEqual('violated', check_builtin([call], root, ['Read'])['status'])
            self.assertEqual('violated', check_builtin([{'sequence': 1, 'tool': 'Bash', 'arguments': {}}], root, ['Read'])['status'])
            self.assertEqual('violated', check_builtin([{'sequence': 1, 'tool': 'Grep', 'arguments': {'path': '../outside'}}], root, ['Grep'])['status'])
        self.assertFalse(benchlib.eligible_run({'eligibleForMetrics': False}))

    def test_inspection_excludes_builtin_escape_and_command_line_tools_skip_it(self):
        with tempfile.TemporaryDirectory() as scratch:
            folder = Path(scratch)
            (folder / 'summary.json').write_text(json.dumps({'kind': 'builtin', 'server': 'server_x', 'builtin': ['Read']}))
            (folder / 'prompt.txt').write_text('local checkout at this absolute path:\n/clone\n')
            (folder / 'stream.jsonl').write_text(json.dumps({'type': 'assistant', 'message': {'content': [
                {'type': 'tool_use', 'id': '1', 'name': 'Read', 'input': {'file_path': '/outside'}}]}}))
            self.assertEqual('violated', inspect_session(folder)['status'])
            with patch.object(sys, 'argv', ['replay.py', '--session', scratch, '--host', 'absent', '--corpus', '/clone', '--output', scratch]), patch.object(replay, 'StdioClient') as client, contextlib.redirect_stdout(io.StringIO()) as output:
                replay.main()
            client.assert_not_called()
            self.assertEqual(1, len(output.getvalue().splitlines()))
            with patch.object(sys, 'argv', ['attribution.py', '--session', scratch, '--tasks-file', 'absent']), contextlib.redirect_stdout(io.StringIO()) as output:
                attribution.main()
            self.assertEqual(1, len(output.getvalue().splitlines()))

    def test_replay_arguments_are_independent_and_recorded(self):
        with tempfile.TemporaryDirectory() as scratch:
            with patch.object(sys, 'argv', ['replay.py', '--session', scratch, '--host', 'first',
                    '--other-host', 'second', '--host-arg=--tool-set', '--host-arg=full',
                    '--other-host-arg=--live', '--corpus', '/clone', '--output', scratch]), \
                    patch.object(replay, 'replay_session', return_value=[]) as run, \
                    patch.object(replay, 'recorded_root', return_value='/clone'), contextlib.redirect_stdout(io.StringIO()):
                replay.main()
            self.assertEqual(['--tool-set', 'full'], run.call_args_list[0].kwargs['server_args'])
            self.assertEqual(['--live'], run.call_args_list[1].kwargs['server_args'])
            manifest = json.loads((Path(scratch) / 'manifest.json').read_text())
            self.assertEqual(['--tool-set', 'full'], manifest['hostArgs'])
            self.assertEqual(['--live'], manifest['otherHostArgs'])

    def test_scrubbing_uses_capitalized_whole_tool_names(self):
        self.assertEqual('search_tool tree_tool grep glob Grepper', judge.scrub('Grep Glob grep glob Grepper'))
    def test_builtin_pair_automatically_uses_answers_only_after_collecting_sessions(self):
        summary = {'kind': 'builtin', 'answer': '## Answer\nRead found it.', 'toolCalls': [],
                   'usage': {'totalContextTokens': 3}}
        split = judge.split_answer(summary)
        self.assertEqual('builtin', split['kind'])
        with tempfile.TemporaryDirectory() as scratch:
            with patch.object(sys, 'argv', ['judge.py', '--tasks-file', 'tasks.json', '--ours', 'server_x',
                    '--theirs', 'server_y', '--name', 'pair', '--work', scratch]), \
                    patch.object(benchlib, 'load_tasks', return_value={'series': 'fixture', 'tasks': [{'id': 'T1', 'text': 'Find'}]}), \
                    patch.object(judge, 'collect_pairs', return_value=[({'id': 'T1', 'text': 'Find'}, 1, split, split)]), \
                    patch.object(judge, 'make_asker', return_value=object()), \
                    patch.object(judge, 'judge_pair', return_value=([{}, {}], 0)) as pair, \
                    patch.object(judge, 'combine', return_value={'task': 'T1', 'repeat': 1, 'correctness': 'tie', 'trust': 'tie', 'preference': 'tie'}), \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, judge.main())
            self.assertTrue(pair.call_args.args[4])
            result = json.loads((Path(scratch) / 'results/fixture/judge/pair.json').read_text())
            self.assertTrue(result['answersOnly'])
            self.assertTrue(result['builtinAnswersOnly'])

    def test_builtin_key_metrics_and_task_table_keep_calls_errors_and_cost(self):
        summary = {'kind': 'builtin', 'server': 'server_x', 'task': 'T1', 'answer': 'src/a.ts tests/a.test.ts',
                   'toolCalls': [{'name': 'Read', 'isError': False}, {'name': 'Grep', 'isError': True}],
                   'usage': {'input': 2, 'cacheRead': 3, 'cacheCreation': 4, 'costUsd': 0.01},
                   'toolResponseChars': 50, 'durationMs': 1000}
        task = {'id': 'T1', 'repo': 'fixture', 'kind': 'change', 'key': {'core': ['src/a.ts'],
                'supporting': [], 'tests': ['tests/a.test.ts'], 'acceptable': [], 'unrelated': []}}
        score = benchlib.score_session(summary, task, None)
        self.assertEqual((1, 1, 2, 1, 9, 0.01), tuple(score[k] for k in
                         ['files', 'tests', 'calls', 'tool_errors', 'context', 'cost']))
        with patch.object(benchlib, 'load_runs', return_value=[summary]):
            table = report.per_task_table(Path('.'), {'tasks': [task], 'repos': {'fixture': {'language': 'TypeScript'}}},
                                          [('server_x', 'files')], ['T1'], {'fixture': None})
        self.assertIn('| 1/1 | 1/1 | 2 | 9 |', table)
