"""Key metrics, run loading, corpus checks and judge bookkeeping on small fixtures. No network, no model."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_DIR))

import benchlib  # noqa: E402
import judge  # noqa: E402
import prepare_corpora  # noqa: E402

FIXTURES = Path(__file__).resolve().parent / 'fixtures'
RICH_TASKS = TOOL_DIR / 'tasks' / 'rich-fluentvalidation.json'


def make_corpus(root, paths):
    for path in paths:
        target = Path(root) / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text('x\n', encoding='utf-8')


class PathExtractionTests(unittest.TestCase):
    def test_absolute_windows_paths_are_relative_only_inside_the_corpus(self):
        root = 'C:/bench/corpora/rich'
        for path in ('C:/bench/corpora/rich/rich/text.py',
                     r'c:\bench\corpora\rich\rich\text.py',
                     '/C:/bench/corpora/rich/rich/text.py',
                     r'C:/bench\corpora/rich\rich/text.py'):
            with self.subTest(path=path):
                self.assertEqual(benchlib.paths_in(f'[source]({path})', root), {'rich/text.py'})

    def test_absolute_posix_paths_use_the_corpus_boundary(self):
        self.assertEqual(benchlib.paths_in('/bench/rich/rich/text.py', '/bench/rich'), {'rich/text.py'})
        self.assertFalse(benchlib.mentions('rich/text.py', benchlib.paths_in('/other/rich/text.py', '/bench/rich')))
        self.assertFalse(benchlib.mentions('rich/text.py', benchlib.paths_in('/bench/richer/rich/text.py', '/bench/rich')))

    def test_unc_paths_keep_the_share_boundary(self):
        root = r'\\host\share\rich'
        self.assertEqual(benchlib.paths_in(r'\\host\share\rich\rich\text.py', root), {'rich/text.py'})
        outside = benchlib.paths_in(r'\\host\other\rich\rich\text.py', root)
        self.assertFalse(benchlib.mentions('rich/text.py', outside))

    def test_outside_absolute_paths_do_not_match_keys_or_bare_names(self):
        for path in ('D:/bench/corpora/rich/rich/text.py', 'C:/other/rich/text.py',
                     'C:/bench/corpora/richer/rich/text.py', '/other/context.go'):
            with self.subTest(path=path):
                found = benchlib.paths_in(path, 'C:/bench/corpora/rich')
                self.assertFalse(benchlib.mentions('rich/text.py', found))
                self.assertFalse(benchlib.mentions('context.go', found))

    def test_parent_segments_cannot_escape_the_corpus(self):
        root = 'C:/bench/corpora/rich'
        for path in ('../rich/text.py', 'C:/bench/corpora/rich/../other/rich/text.py'):
            with self.subTest(path=path):
                self.assertFalse(benchlib.mentions('rich/text.py', benchlib.paths_in(path, root)))
        self.assertEqual(benchlib.paths_in('C:/bench/corpora/rich/sub/../rich/text.py', root), {'rich/text.py'})

    def test_absolute_path_with_spaces_in_markdown_is_not_split(self):
        root = 'C:/bench folder/corpora/rich'
        self.assertEqual(benchlib.paths_in(f'[source]({root}/rich/text.py)', root), {'rich/text.py'})

    def test_relative_backslashes_and_root_level_absolute_file(self):
        self.assertEqual(benchlib.paths_in(r'rich\text.py'), {'rich/text.py'})
        self.assertEqual(benchlib.paths_in('/bench/context.go', '/bench'), {'context.go'})

    def test_template_placeholders_do_not_turn_suffixes_into_absolute_paths(self):
        for path in ('src/middleware/{name}/index.ts', 'src/middleware/<name>/index.ts',
                     'src/middleware/**/index.ts'):
            with self.subTest(path=path):
                self.assertEqual(benchlib.paths_in(f'`{path}`'), set())

    def test_finds_paths_with_folders_and_bare_source_files(self):
        found = benchlib.paths_in('See `./rich/text.py` and test_cells.py, not https://x.io or notes.txt.')
        self.assertIn('rich/text.py', found)
        self.assertIn('test_cells.py', found)
        self.assertNotIn('notes.txt', found)

    def test_mentions_matches_suffixes_both_ways(self):
        self.assertTrue(benchlib.mentions('rich/text.py', {'src/rich/text.py'}))
        self.assertTrue(benchlib.mentions('src/FluentValidation/Syntax.cs', {'FluentValidation/Syntax.cs'}))
        self.assertTrue(benchlib.mentions('tests/test_cells.py', {'tests/test_cells.py'}))
        self.assertFalse(benchlib.mentions('rich/cells.py', {'tests/test_cells.py'}))

    def test_a_bare_file_name_does_not_satisfy_a_key_path_with_a_folder(self):
        self.assertFalse(benchlib.mentions('tests/test_cells.py', {'test_cells.py'}))


class ScoreTests(unittest.TestCase):
    def setUp(self):
        self.tasks = benchlib.load_tasks(RICH_TASKS)
        self.results = FIXTURES / 'results'
        self.corpus = Path(tempfile.mkdtemp())
        make_corpus(self.corpus, ['rich/cells.py', 'rich/rule.py', 'rich/segment.py', 'rich/text.py',
                                  'tests/test_cells.py', 'tests/test_rule.py', 'tests/test_segment.py',
                                  'tests/test_text.py'])

    def tearDown(self):
        shutil.rmtree(self.corpus, ignore_errors=True)

    def test_scores_a_real_session_against_its_key(self):
        score = benchlib.score_arm(self.results, self.tasks, 'server_z', ['P1'], {'rich': self.corpus})
        self.assertEqual(score['sessions'], 1)
        self.assertEqual((score['files'], score['files_needed']), (3, 4))  # rich/cells.py is not named
        self.assertEqual((score['tests'], score['tests_needed']), (1, 1))
        self.assertEqual(score['known_wrong'], 0)
        self.assertEqual(score['invented'], 1)  # tests/test_rule_in_table.py is missing from this corpus
        self.assertEqual(score['calls'], 3)
        self.assertEqual(score['tool_errors'], 1)
        self.assertEqual(score['context'], 41 + 88053 + 27351)
        self.assertAlmostEqual(score['cost'], 0.0787233)

    def test_counts_files_the_key_calls_unrelated(self):
        summary = {'answer': 'Change rich/panel.py and rich/text.py.', 'toolCalls': [],
                   'usage': {'input': 1, 'cacheRead': 2, 'cacheCreation': 3, 'costUsd': 0.5}}
        task = next(t for t in self.tasks['tasks'] if t['id'] == 'P1')
        score = benchlib.score_session(summary, task, None)
        self.assertEqual(score['known_wrong'], 1)
        self.assertEqual(score['invented'], 0)  # no corpus given: not counted
        self.assertEqual(score['context'], 6)

    def test_an_arm_without_sessions_scores_zero(self):
        score = benchlib.score_arm(self.results, self.tasks, 'server_q', ['P1', 'P2'], {'rich': self.corpus})
        self.assertEqual(score['sessions'], 0)

    def test_absolute_links_to_existing_files_are_not_invented(self):
        task = next(t for t in self.tasks['tasks'] if t['id'] == 'P1')
        summary = {'answer': f'[source]({self.corpus.as_posix()}/rich/text.py)', 'toolCalls': [],
                   'usage': {'input': 1, 'cacheRead': 0, 'cacheCreation': 0, 'costUsd': 0}}
        score = benchlib.score_session(summary, task, self.corpus)
        self.assertEqual((score['files'], score['invented']), (1, 0))


class RunLoadingTests(unittest.TestCase):
    def test_failed_attempts_are_not_sessions(self):
        runs = benchlib.load_runs(FIXTURES / 'results', 'P1', 'server_z')
        self.assertEqual([run['id'] for run in runs], ['P1-server_z-r1'])
        self.assertEqual(benchlib.arms_with_runs(FIXTURES / 'results'), ['server_z'])

    def test_answer_sections(self):
        summary = benchlib.load_runs(FIXTURES / 'results', 'P1', 'server_z')[0]
        self.assertIn('tests/test_cells.py', benchlib.answer_section(summary['answer'], 'Tests'))
        self.assertNotIn('## Confidence', benchlib.answer_section(summary['answer'], 'Tests'))


class WorkFolderTests(unittest.TestCase):
    def test_refuses_a_folder_inside_the_repository(self):
        with self.assertRaises(benchlib.BenchmarkError):
            benchlib.work_root(str(TOOL_DIR / 'results'))

    def test_accepts_the_ignored_work_folder_and_outside_folders(self):
        self.assertEqual(benchlib.work_root(str(TOOL_DIR / 'work' / 'a')), (TOOL_DIR / 'work' / 'a').resolve())
        outside = Path(tempfile.gettempdir()) / 'agent-benchmark-test'
        self.assertEqual(benchlib.work_root(str(outside)), outside.resolve())


@unittest.skipUnless(shutil.which('git'), 'git is required')
class CorpusCheckTests(unittest.TestCase):
    def setUp(self):
        self.work = Path(tempfile.mkdtemp())
        root = benchlib.corpus_dir(self.work, 'demo')
        make_corpus(root, ['a.py', 'src/b.py'])
        env = {**os.environ, 'GIT_AUTHOR_NAME': 't', 'GIT_AUTHOR_EMAIL': 't@example.com',
               'GIT_COMMITTER_NAME': 't', 'GIT_COMMITTER_EMAIL': 't@example.com'}
        for args in (['init', '-q'], ['add', '.'], ['-c', 'commit.gpgsign=false', 'commit', '-q', '-m', 'init']):
            subprocess.run(['git', '-C', str(root), *args], check=True, env=env, capture_output=True)
        self.root = root
        head = benchlib.git_head(root)
        benchlib.stand_manifest_path(self.work, 'demo').write_text(json.dumps({'head': head, 'files': 2}), encoding='utf-8')

    def tearDown(self):
        benchlib.remove_tree(self.work)

    def test_a_prepared_clone_passes(self):
        self.assertEqual(benchlib.verify_corpus(self.work, 'demo'), [])

    def test_an_emptied_clone_fails(self):
        (self.root / 'src' / 'b.py').unlink()
        problems = benchlib.verify_corpus(self.work, 'demo')
        self.assertEqual(len(problems), 1)
        self.assertIn('1 files on disk, expected 2', problems[0])

    def test_a_missing_clone_fails_on_head_and_files(self):
        benchlib.remove_tree(self.root)
        self.assertEqual(len(benchlib.verify_corpus(self.work, 'demo')), 2)

    def test_an_unprepared_repo_fails(self):
        self.assertIn('not prepared', benchlib.verify_corpus(self.work, 'other')[0])


class FixtureEditTests(unittest.TestCase):
    def test_edits_keep_crlf_line_endings_and_replace_the_first_occurrence(self):
        folder = Path(tempfile.mkdtemp())
        try:
            path = folder / 'a.py'
            path.write_bytes(b'x = 1\r\ny = 2\r\nx = 1\r\n')
            prepare_corpora.apply_edits(folder, [{'path': 'a.py', 'old': 'x = 1\ny = 2', 'new': 'x = 3\ny = 4'}])
            self.assertEqual(path.read_bytes(), b'x = 3\r\ny = 4\r\nx = 1\r\n')
        finally:
            shutil.rmtree(folder, ignore_errors=True)

    def test_a_missing_edit_target_is_an_error(self):
        folder = Path(tempfile.mkdtemp())
        try:
            (folder / 'a.py').write_text('x\n', encoding='utf-8')
            with self.assertRaises(benchlib.BenchmarkError):
                prepare_corpora.apply_edits(folder, [{'path': 'a.py', 'old': 'missing', 'new': 'y'}])
        finally:
            shutil.rmtree(folder, ignore_errors=True)


class JudgeBookkeepingTests(unittest.TestCase):
    def test_a_verdict_counts_only_when_both_orders_agree(self):
        self.assertEqual(judge.settle({'trust': 'A'}, {'trust': 'B'}, 'trust'), ('ours', ('ours', 'ours')))
        self.assertEqual(judge.settle({'trust': 'A'}, {'trust': 'A'}, 'trust'), ('disagree', ('ours', 'theirs')))
        self.assertEqual(judge.settle({'trust': 'tie'}, {'trust': 'tie'}, 'trust')[0], 'tie')
        self.assertEqual(judge.settle({}, {'trust': 'B'}, 'trust')[0], 'disagree')

    def test_combine_maps_scores_and_claims_back_to_arms(self):
        first = {'correctness': 'A', 'scores': {'A': {'trust': 5}, 'B': {'trust': 2}}, 'suspicious': {'A': [], 'B': ['x']}}
        second = {'correctness': 'B', 'scores': {'A': {'trust': 1}, 'B': {'trust': 4}}, 'suspicious': {'A': ['y'], 'B': []}}
        row = judge.combine('P1', 1, first, second, {}, {})
        self.assertEqual(row['correctness'], 'ours')
        self.assertEqual(row['scores']['ours'], [{'trust': 5}, {'trust': 4}])
        self.assertEqual(row['suspicious']['theirs'], ['x', 'y'])
        self.assertEqual(row['convenience'], '?')  # answers-only run: no tool verdict

    def test_scrub_hides_products_tools_and_arm_names(self):
        text = judge.scrub('server_z used search_project; Repomix grep_repomix_output; DevProjex DPX-MCP-12; server_b2')
        for leak in ('server_z', 'search_project', 'repomix', 'devprojex', 'DPX-', 'server_b2'):
            self.assertNotIn(leak.lower(), text.lower())

    def test_split_answer_separates_the_tool_review(self):
        summary = benchlib.load_runs(FIXTURES / 'results', 'P1', 'server_z')[0]
        part = judge.split_answer(summary)
        self.assertNotIn('## Experience', part['answer'])
        self.assertTrue(part['experience'].startswith('## Experience'))
        self.assertEqual(part['calls'], 3)
        self.assertEqual(part['errors'], 1)

    def test_parse_verdict_tolerates_prose_and_bad_json(self):
        self.assertEqual(judge.parse_verdict('Here: {"trust": "A"} done'), {'trust': 'A'})
        self.assertEqual(judge.parse_verdict('no json'), {})
        self.assertEqual(judge.parse_verdict('{broken'), {})


if __name__ == '__main__':
    unittest.main()
