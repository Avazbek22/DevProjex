"""Versioned task keys and historical scoring; no model calls."""
import copy
import json
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

TOOL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_DIR))
import benchlib
import report


class KeyVersionTests(unittest.TestCase):
    def setUp(self):
        self.tasks = benchlib.load_tasks(TOOL_DIR / 'tasks/fourteen.json')
        self.task = next(task for task in self.tasks['tasks'] if task['id'] == 'D1')
        self.summary = {'id': 'D1-server_z-r1', 'task': 'D1', 'server': 'server_z', 'repeat': 1,
                        'answer': 'src/http-exception.ts src/request.ts src/middleware/jwt/index.test.ts',
                        'toolCalls': [], 'usage': {'input': 1, 'output': 0, 'cacheRead': 0,
                                                'cacheCreation': 0, 'costUsd': 0}}

    def test_unversioned_sessions_keep_the_exact_original_key(self):
        original = json.dumps(self.task['previousKeys']['1'], sort_keys=True, separators=(',', ':')).encode()
        self.assertEqual(hashlib.sha256(original).hexdigest(), '5ade6607dbab9106105481571b466b3ea1b2d267da9bfce662770ac412858658')
        score = benchlib.score_session(self.summary, self.task, None)
        self.assertEqual((score['files'], score['files_needed'], score['tests'], score['tests_needed']), (1, 5, 1, 7))
        self.assertEqual(score['key_versions'], {'D1': [1]})
        self.assertEqual(score, benchlib.score_session(self.summary, self.task, None, 1))
        self.assertIn('keys D1=v1', benchlib.format_score(score))

    def test_new_key_marks_supplied_response_cases_as_acceptable(self):
        self.summary['keyVersion'] = 2
        score = benchlib.score_session(self.summary, self.task, None)
        self.assertEqual((score['files'], score['files_needed'], score['tests'], score['tests_needed']), (2, 8, 0, 7))
        self.assertEqual(score['known_wrong'], 0)
        unrelated = {**self.summary, 'answer': 'src/adapter/aws-lambda/handler.test.ts'}
        self.assertEqual(benchlib.score_session(unrelated, self.task, None)['known_wrong'], 1)
        self.assertEqual(score['key_versions'], {'D1': [2]})
        self.assertIn('keys D1=v2', benchlib.format_score(score))
        self.assertIn('D1=v2', report.key_metrics_table({'server_z': score}, [('server_z', 'current')]))

    def test_explicit_version_overrides_the_session_without_mutating_it(self):
        original = copy.deepcopy(self.summary)
        score = benchlib.score_session(self.summary, self.task, None, 2)
        self.assertEqual(score['key_versions'], {'D1': [2]})
        self.assertEqual(self.summary, original)
        self.assertEqual(benchlib.score_session({**self.summary, 'keyVersion': 2}, self.task, None, 1)['tests'], 1)

    def test_aggregates_disclose_mixed_versions_instead_of_hiding_them(self):
        total = benchlib.empty_score()
        for version in (2, 1, 2):
            benchlib.add_scores(total, benchlib.score_session(self.summary, self.task, None, version))
        self.assertEqual(total['key_versions'], {'D1': [1, 2]})
        self.assertIn('D1=v1/v2', benchlib.format_score(total))

    def test_unknown_or_invalid_versions_fail_explicitly(self):
        for version in (0, -1, 3, True, '2'):
            with self.subTest(version=version), self.assertRaises(benchlib.BenchmarkError):
                benchlib.score_session(self.summary, self.task, None, version)
        broken = copy.deepcopy(self.tasks)
        task = next(task for task in broken['tasks'] if task['id'] == 'D1')
        task['previousKeys'] = {}
        self.assertTrue(any('preserve version 1' in error for error in benchlib.validate_tasks(broken)))

    def test_command_outputs_and_reports_identify_both_versions(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runs = root / 'results/fourteen/runs/D1-server_z-r1'
            runs.mkdir(parents=True)
            (runs / 'summary.json').write_text(json.dumps(self.summary), encoding='utf-8')
            for version, count in ((1, 5), (2, 8)):
                common = ['--tasks-file', str(TOOL_DIR / 'tasks/fourteen.json'), '--tasks', 'D1',
                          '--work', str(root), '--skip-invented', '--key-version', str(version)]
                result = subprocess.run([sys.executable, str(TOOL_DIR / 'keyscore.py'), *common, '--json'],
                                        capture_output=True, text=True, timeout=20)
                self.assertEqual(result.returncode, 0, result.stderr)
                score = json.loads(result.stdout)['server_z']['all']
                self.assertEqual(score['key_versions'], {'D1': [version]})
                self.assertEqual(score['files_needed'], count)
                plain = subprocess.run([sys.executable, str(TOOL_DIR / 'keyscore.py'), *common],
                                       capture_output=True, text=True, encoding='utf-8', timeout=20)
                self.assertEqual(plain.returncode, 0, plain.stderr)
                self.assertIn(f'keys D1=v{version}', plain.stdout)
                rendered = subprocess.run([sys.executable, str(TOOL_DIR / 'report.py'), *common],
                                          capture_output=True, text=True, encoding='utf-8', timeout=20)
                self.assertEqual(rendered.returncode, 0, rendered.stderr)
                self.assertIn(f'D1=v{version}', rendered.stdout)

    def test_alternative_templates_do_not_require_several_mutually_optional_examples(self):
        rich = benchlib.load_tasks(TOOL_DIR / 'tasks/rich-fluentvalidation.json')
        for task_id, tasks in (('T6', self.tasks), ('C2', rich)):
            task = next(task for task in tasks['tasks'] if task['id'] == task_id)
            key = benchlib.task_key(task, 2)
            summary = {**self.summary, 'task': task_id, 'answer': '\n'.join(key['core'] + key['supporting'])}
            latest = benchlib.score_session(summary, task, None, 2)
            previous = benchlib.score_session(summary, task, None, 1)
            self.assertEqual(latest['files'], latest['files_needed'])
            self.assertEqual(latest['tests_needed'], 0)
            self.assertGreater(previous['files_needed'], latest['files_needed'])
            self.assertIn('template', key['notes'])
            self.assertEqual(benchlib.score_session(summary, task, None), previous)

    def test_positional_json_callers_are_required_in_the_new_signature_key(self):
        task = next(task for task in self.tasks['tasks'] if task['id'] == 'T5')
        summary = {**self.summary, 'answer': 'src/validator/validator.test.ts src/client/client.test.ts'}
        before = benchlib.score_session(summary, task, None, 1)
        after = benchlib.score_session(summary, task, None, 2)
        self.assertEqual((before['known_wrong'], before['tests']), (2, 0))
        self.assertEqual((after['known_wrong'], after['tests']), (0, 2))

    def test_enrichment_callers_are_distinct_from_indirect_regression_tests(self):
        task = next(task for task in self.tasks['tasks'] if task['id'] == 'T8')
        summary = {**self.summary, 'answer': 'test/Serilog.Tests/Core/LoggerTests.cs'}
        self.assertEqual(benchlib.score_session(summary, task, None, 1)['tests'], 1)
        self.assertEqual(benchlib.score_session(summary, task, None, 2)['tests'], 0)
        self.assertIn('test/Serilog.Tests/Core/LoggerTests.cs', task['key']['acceptable'])

    def test_matching_description_correction_keeps_historical_key_and_path_counts(self):
        task = next(task for task in self.tasks['tasks'] if task['id'] == 'T7')
        before, after = benchlib.task_key(task, 1), benchlib.task_key(task, 2)
        for name in benchlib.KEY_LISTS:
            self.assertEqual(before[name], after[name])
        self.assertIn('shortens', before['notes'])
        self.assertIn('does not repeatedly shorten', after['notes'])

    def test_current_override_resolves_each_task_and_records_numeric_versions(self):
        for task in self.tasks['tasks']:
            current = benchlib.score_session(self.summary, task, None, 'current')
            expected = benchlib.score_session(self.summary, task, None, task.get('keyVersion', 1))
            self.assertEqual(current, expected)
        self.assertEqual(benchlib.parse_key_version('current'), 'current')
        self.assertEqual(benchlib.parse_key_version('2'), 2)
        for value in ('0', '-1', 'latest'):
            with self.assertRaises(ValueError):
                benchlib.parse_key_version(value)


if __name__ == '__main__':
    unittest.main()
