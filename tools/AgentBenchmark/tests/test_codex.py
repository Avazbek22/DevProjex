"""Codex series boundaries and key metrics without any model invocation."""
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_DIR))

import benchlib
import judge
import report


class CodexSeriesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.work = Path(self.temporary.name)
        self.results = benchlib.results_dir(self.work, 'rich-fluentvalidation', 'codex')
        self.path = self.results / 'runs' / 'P1-server_b-r1' / 'summary.json'
        self.path.parent.mkdir(parents=True)
        self.summary = {'id': 'P1-server_b-r1', 'task': 'P1', 'server': 'server_b', 'repeat': 1,
                        'agent': 'codex', 'exitCode': 0, 'error': None, 'eligibleForMetrics': True,
                        'isolation': {'status': 'passed', 'violations': []}, 'toolCalls': [], 'answer': 'rich/text.py',
                        'usage': {'input': 5, 'cacheRead': 10, 'cacheCreation': 2, 'output': 1,
                                  'costUsd': 0.0001, 'costSource': 'calculated', 'costComplete': True}}
        self.save()
        arms = self.results / 'arms'
        arms.mkdir()
        (arms / 'server_b.json').write_text(json.dumps({'agent': 'codex', 'model': 'gpt-6-luna'}), encoding='utf-8')

    def tearDown(self):
        self.temporary.cleanup()

    def save(self):
        self.path.write_text(json.dumps(self.summary), encoding='utf-8')

    def test_codex_results_do_not_share_a_directory_with_claude(self):
        self.assertNotEqual(self.results, benchlib.results_dir(self.work, 'rich-fluentvalidation'))
        self.assertEqual(len(benchlib.load_runs(self.results, 'P1', 'server_b')), 1)
        self.assertEqual(benchlib.load_runs(benchlib.results_dir(self.work, 'rich-fluentvalidation'), 'P1', 'server_b'), [])

    def test_a_violating_session_is_preserved_but_excluded_from_metrics(self):
        self.summary['isolation'] = {'status': 'violated', 'violations': ['non-MCP item: command_execution']}
        self.save()
        self.assertEqual(benchlib.load_runs(self.results, 'P1', 'server_b'), [])
        self.assertEqual(len(benchlib.load_runs(self.results, 'P1', 'server_b', include_invalid=True)), 1)
        task = {'key': {'core': ['rich/text.py'], 'supporting': [], 'tests': [], 'unrelated': []}}
        with self.assertRaises(benchlib.BenchmarkError):
            benchlib.score_session(self.summary, task, None)
        self.assertIn('outside key metrics', report.excluded_sessions(self.results, [('server_b', 'B')], ['P1']))

    def test_codex_needs_positive_isolation_and_complete_usage(self):
        self.summary.pop('eligibleForMetrics')
        self.assertFalse(benchlib.eligible_run(self.summary))
        self.summary['eligibleForMetrics'] = True
        self.summary['usage']['costComplete'] = False
        self.assertFalse(benchlib.eligible_run(self.summary))

    def test_misplaced_sessions_cannot_be_scored_as_claude(self):
        with self.assertRaisesRegex(benchlib.BenchmarkError, 'another agent'):
            benchlib.validate_series_agent(self.results, ['server_b'], ['P1'], 'claude')
        benchlib.validate_series_agent(self.results, ['server_b'], ['P1'], 'codex')

    def test_report_names_the_agent_model_and_calculated_cost(self):
        text = report.run_configuration(self.results, [('server_b', 'B')])
        self.assertIn('codex', text)
        self.assertIn('gpt-6-luna', text)
        self.assertIn('calculated API estimate', text)

    def test_codex_runs_cannot_reach_the_claude_judge(self):
        with self.assertRaisesRegex(benchlib.BenchmarkError, 'judge is disabled'):
            judge.collect_pairs(self.results, {'P1': {}}, ['P1'], 'server_b', 'server_b')
        process = subprocess.run([sys.executable, str(TOOL_DIR / 'judge.py'), '--agent', 'codex',
                                  '--tasks-file', 'unused', '--ours', 'server_b', '--theirs', 'server_c', '--name', 'pair'],
                                 capture_output=True, text=True, timeout=30)
        self.assertEqual(process.returncode, 1)
        self.assertIn('judge is disabled', process.stderr)
        self.assertNotIn('Traceback', process.stderr)

    def test_configuration_is_valid_toml_with_one_server(self):
        try:
            import tomllib
        except ImportError:
            self.skipTest('stdlib TOML parsing requires Python 3.11')
        script = "import {codexConfiguration} from './tools/AgentBenchmark/codex.mjs';" + \
                 "console.log(codexConfiguration({mcpServers:{server_b:{command:'/bin/cli',args:['mcp','--root','/corpus/a b']}}},'/corpus/a b','/private/models.json'));"
        process = subprocess.run(['node', '--input-type=module', '-e', script], cwd=benchlib.REPOSITORY_ROOT,
                                 capture_output=True, text=True, encoding='utf-8', timeout=30, check=True)
        config = tomllib.loads(process.stdout)
        self.assertEqual(list(config['mcp_servers']), ['server_b'])
        self.assertEqual(config['mcp_servers']['server_b']['args'][-1], '/corpus/a b')
        self.assertFalse(config['features']['shell_tool'])
        self.assertEqual(config['web_search'], 'disabled')


if __name__ == '__main__':
    unittest.main()
