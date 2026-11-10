"""Evidence, source boundaries and replay normalization without model calls."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

TOOL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_DIR))
import attribution
import benchlib
import provenance
import replay
from transcripts import recorded_calls


def call(tool, text, arguments=None, error=False, sequence=1):
    return dict(sequence=sequence, tool=tool, text=text, arguments=arguments or {},
                is_error=error, server='server_b')


class AttributionTests(unittest.TestCase):
    def setUp(self):
        self.task = {'id': 'T1', 'key': {'core': ['src/a.py', 'src/b.py'], 'supporting': [],
                                      'tests': ['tests/test_a.py'], 'unrelated': []}}

    def test_listing_content_and_missing_keep_first_evidence_and_final_answer_independent(self):
        calls = [call('get_tree', '- src/\n  - a.py\n  - b.py', sequence=1),
                 call('get_file', 'File: src/a.py\nLines: 2-2 of 3\nreturn 1', sequence=2)]
        rows = attribution.attribute(calls, self.task, '[source](c:/corpus/src/a.py)', 'C:/corpus')
        a, b, test = rows
        self.assertEqual((a['exposure'], a['named']), ('content-shown', True))
        self.assertEqual(a['firstShown'], {'call': 1, 'tool': 'get_tree'})
        self.assertEqual(a['firstContent'], {'call': 2, 'tool': 'get_file'})
        self.assertEqual((b['exposure'], b['named']), ('listed', False))
        self.assertEqual(test['exposure'], 'never-shown')

    def test_rejected_and_budget_withheld_reads_are_not_content(self):
        for text, error in [('DPX-MCP-PATH-NOT-FOUND', True),
                            ('Requests:\n1.1 — not-returned', False),
                            ('File: src/a.py\nStatus: unavailable\n', False)]:
            row = attribution.attribute([call('get_file', text, {'path': 'src/a.py'}, error)], self.task, '')[0]
            self.assertNotEqual(row['exposure'], 'content-shown')

    def test_numbered_search_context_is_content_but_footer_references_are_only_listed(self):
        text = 'src/a.py\n3: def f():\nImports: src/b.py\nTests for this file: tests/test_a.py'
        rows = attribution.attribute([call('search_project', text)], self.task, '')
        self.assertEqual([row['exposure'] for row in rows], ['content-shown', 'listed', 'listed'])

    def test_markdown_escaped_tree_names_are_paths_not_windows_separators(self):
        self.assertEqual(attribution.tree_paths('- tests/\r\n  - test\\_a.py\r\n'), {'tests/test_a.py'})

    def test_absolute_outside_paths_do_not_satisfy_evidence(self):
        rows = attribution.attribute([call('get_file', 'File: /elsewhere/src/a.py\nLines: 1-1 of 1\nx')],
                                     self.task, '', '/corpus')
        self.assertEqual(rows[0]['exposure'], 'never-shown')

    def test_repomix_content_requires_an_observed_file_boundary(self):
        source = json.dumps({'content': '<file path="src/a.py">\ndef f():\n</file>'})
        rows = attribution.attribute([call('read_repomix_output', source)], self.task, '')
        self.assertEqual(rows[0]['exposure'], 'content-shown')
        rows = attribution.attribute([call('read_repomix_output', '{"content":"def f():"}')], self.task, '')
        self.assertEqual(rows[0]['exposure'], 'never-shown')

    def test_existing_real_client_fixtures_join_calls_without_counting_updates_twice(self):
        fixtures = TOOL_DIR / 'tests/fixtures'
        claude = recorded_calls(fixtures / 'stream.jsonl')
        codex = recorded_calls(fixtures / 'codex-stream.jsonl')
        self.assertEqual([c['tool'] for c in claude], ['get_tree', 'get_file'])
        self.assertEqual(claude[0]['text'], 'src/a.py\nsrc/b.py')
        self.assertTrue(claude[1]['is_error'])
        self.assertEqual(len(codex), 1)
        self.assertEqual(codex[0]['text'], 'MCP_PROBE_OK')


class ProvenanceTests(unittest.TestCase):
    def check(self, calls, root='C:/corpus'):
        return provenance.check_provenance(calls, root, 'server_b',
                                           {'url': 'https://github.com/example/project.git', 'head': 'a' * 40})

    def test_local_pack_inside_root_and_foreign_projects(self):
        for value in ('C:/corpus', 'c:\\corpus\\src', './src', 'sub/../src'):
            self.assertEqual(self.check([call('pack_codebase', '{}', {'directory': value})])['status'], 'passed')
        for value in ('../other', 'C:/corpuser', 'D:/corpus', '/other', 'C:/corpus/../other'):
            with self.subTest(value=value):
                self.assertEqual(self.check([call('pack_codebase', '{}', {'directory': value})])['status'], 'violated')
        self.assertEqual(self.check([call('get_tree', 'data', {'project': 'other'})])['status'], 'violated')
        self.assertEqual(self.check([call('get_tree', 'data', {'project': '#1'})])['status'], 'passed')

    def test_remote_requires_exact_repository_and_immutable_head_not_a_tag(self):
        for reference in (None, 'main', 'v1', 'b' * 40):
            args = {'remote': 'example/project', 'ref': reference}
            self.assertEqual(self.check([call('pack_remote_repository', '{}', args)])['status'], 'violated')
        args = {'remote': 'example/project', 'ref': 'a' * 40}
        self.assertEqual(self.check([call('pack_remote_repository', '{}', args)])['status'], 'passed')
        args['remote'] = 'example/other'
        self.assertEqual(self.check([call('pack_remote_repository', '{}', args)])['status'], 'violated')

    def test_failed_remote_does_not_taint_a_later_local_pack_but_unknown_output_does(self):
        calls = [call('pack_remote_repository', '{"errorMessage":"invalid URL"}', {'remote': 'other'}),
                 call('pack_codebase', '{"result":"{\\"outputId\\":\\"one\\"}"}'),
                 call('read_repomix_output', 'text', {'outputId': 'one'})]
        self.assertEqual(self.check(calls)['status'], 'passed')
        calls[-1]['arguments']['outputId'] = 'unknown'
        self.assertEqual(self.check(calls)['violations'][0]['reason'], 'unverified-output')

    def test_active_provenance_violations_exclude_both_clients(self):
        for agent in ('claude', 'codex'):
            summary = {'agent': agent, 'provenance': {'status': 'violated'}}
            self.assertFalse(benchlib.eligible_run(summary))

    def test_implicit_project_is_checked_against_reported_roots(self):
        for text in ('Root: C:/other\n- a.py',
                     '{"projects":[{"path":"C:/other"}]}'):
            tool = 'get_tree' if text.startswith('Root:') else 'list_projects'
            self.assertEqual(self.check([call(tool, text)])['violations'][0]['reason'], 'foreign-root')

    def test_loading_preserves_files_and_scores_attribution_with_the_recorded_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            results = Path(temporary)
            folder = results / 'runs/T1-server_b-r1'
            folder.mkdir(parents=True)
            summary = {'id': folder.name, 'task': 'T1', 'server': 'server_b', 'repeat': 1,
                       'answer': '[file](C:/corpus/src/a.py)', 'toolCalls': [],
                       'usage': {'input': 0, 'cacheRead': 0, 'cacheCreation': 0, 'costUsd': 0}}
            path = folder / 'summary.json'
            path.write_text(json.dumps(summary), encoding='utf-8')
            original = path.read_bytes()
            (folder / 'prompt.txt').write_text('local checkout at this absolute path:\nC:/corpus\n', encoding='utf-8')
            event = {'type': 'item.completed', 'item': {'id': '1', 'type': 'mcp_tool_call', 'server': 'server_b',
                     'tool': 'get_file', 'arguments': {'path': 'src/a.py'}, 'status': 'completed',
                     'result': {'content': [{'type': 'text', 'text': 'File: src/a.py\nLines: 1-1 of 1\nx'}]}}}
            (folder / 'stream.jsonl').write_text(json.dumps(event) + '\n', encoding='utf-8')
            loaded = benchlib.load_runs(results, 'T1', 'server_b')[0]
            task = {'id': 'T1', 'key': {'core': ['src/a.py'], 'supporting': [], 'tests': [], 'unrelated': []}}
            score = benchlib.score_session(loaded, task, None)
            self.assertEqual((score['files'], score['named_content_shown'], score['attribution_sessions']), (1, 1, 1))
            self.assertEqual(path.read_bytes(), original)
            event['item']['arguments']['project'] = 'other'
            (folder / 'stream.jsonl').write_text(json.dumps(event), encoding='utf-8')
            self.assertEqual(benchlib.load_runs(results, 'T1', 'server_b'), [])
            self.assertEqual(len(benchlib.load_runs(results, 'T1', 'server_b', include_invalid=True)), 1)


class ReplayTests(unittest.TestCase):
    def test_explicit_normalizations_preserve_source_values_and_semantics(self):
        first = '<untrusted-data-' + 'a' * 24 + '>\r\nC:\\corpus\\src\\a.py\r\npack_id=' + 'a' * 32
        second = '<untrusted-data-' + 'b' * 24 + '>\nD:/clone/src/a.py\npack_id=' + 'b' * 32
        roots = ('C:/corpus', 'D:/clone')
        self.assertEqual(replay.normalize_response(first, roots), replay.normalize_response(second, roots))
        text = 'const id = "' + 'a' * 32 + '";\nTime: 2026-01-01\n1.2 ms'
        self.assertEqual(replay.normalize_response(text), text)
        self.assertNotEqual(replay.normalize_response('valid lines 1-35'), replay.normalize_response('valid lines 1-36'))

    def test_arguments_relocate_only_absolute_paths_inside_the_old_corpus(self):
        args = {'project': 'c:\\old', 'paths': ['C:/old/src/a.py', 'C:/other/a.py', 'src/b.py']}
        mapped = replay.rewrite_arguments(args, 'C:/old', Path('/new'))
        self.assertEqual(Path(mapped['project']), Path('/new'))
        self.assertEqual(Path(mapped['paths'][0]), Path('/new/src/a.py'))
        self.assertEqual(mapped['paths'][1:], args['paths'][1:])

    def test_comparison_reports_full_diffs_and_rejects_misaligned_calls(self):
        before = [call('get_tree', 'one\n')]
        after = [call('get_tree', 'two\n')]
        result = replay.compare_calls(before, after)
        self.assertEqual(result['byTool'], {'get_tree': 1})
        self.assertIn('-one\n+two\n', result['differences'][0]['diff'])
        self.assertEqual(replay.compare_calls(before, before)['changed'], 0)
        with self.assertRaises(ValueError):
            replay.compare_calls(before, [])


if __name__ == '__main__':
    unittest.main()
