"""The committed task files are complete and the validator rejects broken ones. No network, no model."""
import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_DIR))

import benchlib  # noqa: E402

TASK_FILES = sorted((TOOL_DIR / 'tasks').glob('*.json'))


def load(path):
    return json.loads(path.read_text(encoding='utf-8'))


class CommittedTaskFileTests(unittest.TestCase):
    def test_both_series_are_present(self):
        series = {load(path)['series']: len(load(path)['tasks']) for path in TASK_FILES}
        self.assertEqual(series, {'fourteen': 14, 'rich-fluentvalidation': 4})

    def test_every_task_file_validates(self):
        for path in TASK_FILES:
            with self.subTest(path=path.name):
                self.assertEqual(benchlib.validate_tasks(load(path)), [])

    def test_every_task_has_a_repo_and_a_full_key(self):
        for path in TASK_FILES:
            data = load(path)
            for task in data['tasks']:
                with self.subTest(task=task['id']):
                    self.assertIn(task['repo'], data['repos'])
                    self.assertEqual(set(task['key']), set(benchlib.KEY_LISTS) | {'notes'})
                    self.assertTrue(task['key']['core'])

    def test_series_names_are_unique(self):
        names = [load(path)['series'] for path in TASK_FILES]
        self.assertEqual(len(names), len(set(names)))

    def test_special_stands_are_described_as_data(self):
        repos = load(TOOL_DIR / 'tasks' / 'fourteen.json')['repos']
        self.assertEqual(repos['httpx-changes']['fixture']['kind'], 'uncommitted-edits')
        self.assertEqual(repos['hono-commit']['fixture']['kind'], 'commit')
        self.assertRegex(repos['hono-commit']['fixture']['commit']['expectHead'], r'^[0-9a-f]{40}$')

    def test_no_machine_paths_in_task_files(self):
        for path in TASK_FILES:
            text = path.read_text(encoding='utf-8')
            for marker in ('C:/', 'C:\\', '/Users/', '/home/'):
                self.assertNotIn(marker, text, f'{path.name} contains {marker}')
            # Clone locations come from the work folder, never from the task file.
            self.assertEqual([name for name, repo in load(path)['repos'].items() if 'path' in repo], [])


class ValidatorTests(unittest.TestCase):
    def setUp(self):
        self.data = load(TOOL_DIR / 'tasks' / 'fourteen.json')

    def broken(self, change):
        data = copy.deepcopy(self.data)
        change(data)
        return benchlib.validate_tasks(data)

    def test_rejects_an_undeclared_repo(self):
        errors = self.broken(lambda d: d['tasks'][0].update(repo='nowhere'))
        self.assertTrue(any('not declared' in e for e in errors))

    def test_rejects_a_missing_key_list(self):
        errors = self.broken(lambda d: d['tasks'][0]['key'].pop('unrelated'))
        self.assertTrue(any('key.unrelated' in e for e in errors))

    def test_rejects_an_empty_core(self):
        errors = self.broken(lambda d: d['tasks'][0]['key'].update(core=[]))
        self.assertTrue(any('key.core' in e for e in errors))

    def test_rejects_absolute_or_parent_paths_in_keys(self):
        errors = self.broken(lambda d: d['tasks'][0]['key'].update(tests=['../x.py', 'C:/x.py']))
        self.assertEqual(sum('bad path' in e for e in errors), 2)

    def test_rejects_a_file_that_is_both_required_and_unrelated(self):
        errors = self.broken(lambda d: d['tasks'][0]['key'].update(unrelated=[d['tasks'][0]['key']['core'][0]]))
        self.assertTrue(any('both required' in e for e in errors))

    def test_rejects_duplicate_ids(self):
        errors = self.broken(lambda d: d['tasks'][1].update(id=d['tasks'][0]['id']))
        self.assertTrue(any('duplicate id' in e for e in errors))

    def test_rejects_a_short_commit_and_a_missing_file_count(self):
        def change(d):
            d['repos']['httpx'].update(commit='26d48e0')
            d['repos']['httpx'].pop('files')
        errors = self.broken(change)
        self.assertTrue(any('40-character' in e for e in errors))
        self.assertTrue(any('files must be' in e for e in errors))

    def test_rejects_a_fixture_on_an_unknown_base(self):
        errors = self.broken(lambda d: d['repos']['hono-commit']['fixture'].update(base='missing'))
        self.assertTrue(any('fixture base' in e for e in errors))

    def test_rejects_a_commit_fixture_without_author(self):
        errors = self.broken(lambda d: d['repos']['hono-commit']['fixture']['commit'].pop('author'))
        self.assertTrue(any('commit fixture needs author' in e for e in errors))

    def test_rejects_an_unused_repo(self):
        errors = self.broken(lambda d: d['repos'].update(extra=dict(d['repos']['gin'])))
        self.assertTrue(any('not used' in e for e in errors))

    def test_key_paths_are_checked_against_a_clone(self):
        folder = Path(tempfile.mkdtemp())
        try:
            data = load(TOOL_DIR / 'tasks' / 'rich-fluentvalidation.json')
            missing = benchlib.missing_key_paths(data, 'rich', folder)
            expected = sum(len(benchlib.key_paths(t)) for t in data['tasks'] if t['repo'] == 'rich')
            self.assertEqual(len(missing), expected)
        finally:
            benchlib.remove_tree(folder)


if __name__ == '__main__':
    unittest.main()
