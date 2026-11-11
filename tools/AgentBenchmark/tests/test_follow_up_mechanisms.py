import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import benchlib
import mechanisms
import report
from transcripts import recorded_calls


NONCE = 'c' * 24


def envelope(body, notices=''):
    return f'<untrusted-data-{NONCE}>\n{body}\n</untrusted-data-{NONCE}>\n{notices}'


def call(arguments, statuses=('partial',), tool='get_file', failed=False, text=None):
    rows = '\n'.join(f'{index}.1 — {status}' for index, status in enumerate(statuses, 1))
    return dict(tool=tool, arguments=arguments, is_error=failed,
                text=text if text is not None else envelope('Requests:\n' + rows))


def later(path='src/a.cs', action='range', failed=False):
    arguments = {'path': path}
    if action == 'range':
        arguments['start_line'] = 1
    if action == 'search':
        return call({'paths': [path], 'pattern': 'value'}, tool='search_project', text='', failed=failed)
    return call(arguments, statuses=('ok',), failed=failed)


def as_stream(calls, client):
    events = []
    for index, item in enumerate(calls):
        identifier = f'call_{index}'
        if client == 'claude':
            events.extend([
                dict(type='assistant', message={'content': [dict(type='tool_use', id=identifier,
                     name='mcp__server__' + item['tool'], input=item['arguments'])]}),
                dict(type='user', message={'content': [dict(type='tool_result', tool_use_id=identifier,
                     content=item['text'], is_error=item['is_error'])]})])
        else:
            events.append(dict(type='item.completed', item=dict(type='mcp_tool_call', id=identifier,
                          server='server', tool=item['tool'], arguments=item['arguments'], status='completed',
                          result={'isError': item['is_error'], 'content': [{'type': 'text', 'text': item['text']}]})))
    return '\n'.join(json.dumps(event) for event in events)


class FollowUpMechanismTests(unittest.TestCase):
    def test_all_twelve_outcomes_for_both_clients(self):
        for client in ('claude', 'codex'):
            for status in ('structure', 'partial', 'not-returned'):
                for action in ('range', 'whole', 'search', 'none'):
                    with self.subTest(client=client, status=status, action=action), tempfile.TemporaryDirectory() as folder:
                        calls = [call({'requests': [{'path': 'src/a.cs'}]}, (status,))]
                        if action != 'none':
                            calls.append(later(action=action))
                        path = Path(folder) / 'stream.jsonl'
                        path.write_text(as_stream(calls, client), encoding='utf-8')
                        result = mechanisms.count(recorded_calls(path))
                        name = f"after_{status.replace('-', '_')}_{action}"
                        self.assertEqual(1, result[name])
                        self.assertEqual(1, sum(result[key] for key in mechanisms.FOLLOW_UP_COUNTERS))

    def test_events_are_per_call_and_file_not_duplicate_whole_request(self):
        calls = [call({'requests': [{'path': 'src/a.cs'}, {'path': './src/a.cs'}]}, ('partial', 'partial')),
                 call({'path': 'src/a.cs'}, ('structure',)), later()]
        result = mechanisms.count(calls)
        self.assertEqual(1, result['after_partial_whole'])
        self.assertEqual(1, result['after_structure_range'])

    def test_two_pending_events_each_choose_the_same_first_later_search(self):
        calls = [call({'paths': ['src/a.cs', 'src/b.cs']}, ('structure', 'partial')),
                 call({'pattern': 'src/a.cs|src/b.cs'}, tool='search_project', text=''), later('src/a.cs')]
        result = mechanisms.count(calls)
        self.assertEqual(1, result['after_structure_search'])
        self.assertEqual(1, result['after_partial_search'])
        self.assertEqual(0, result['after_structure_range'])

    def test_first_later_failed_read_counts_and_other_tools_do_not(self):
        result = mechanisms.count([call({'path': 'src/a.cs'}),
                                   call({'path': 'src/a.cs'}, tool='related_files'),
                                   later(action='whole', failed=True), later()])
        self.assertEqual(1, result['after_partial_whole'])
        self.assertEqual(0, result['after_partial_range'])

    def test_whole_and_range_for_same_file_use_whole_status_only(self):
        requests = [{'path': 'src/a.cs'}, {'path': 'src/a.cs', 'ranges': [{'start_line': 1, 'end_line': 2}]}]
        self.assertEqual(0, sum(mechanisms.follow_ups([call({'requests': requests}, ('ok', 'partial'))]).values()))
        counts = mechanisms.count([call({'path': 'src/a.cs'}), call({'requests': requests}, ('ok', 'partial'))])
        self.assertEqual(1, counts['after_partial_range'])

    def test_symbol_column_range_and_shared_array_selectors_are_narrow_reads(self):
        for arguments in ({'path': 'src/a.cs', 'symbol': 'A'}, {'path': ['src/a.cs'], 'start_column': 2},
                          {'paths': ['src/a.cs', 'src/b.cs'], 'end_line': 2},
                          {'requests': [{'path': 'src/a.cs', 'ranges': [{'start_line': 1, 'end_line': 2}]}]}):
            with self.subTest(arguments=arguments):
                counts = mechanisms.count([call({'path': 'src/a.cs'}), call(arguments)])
                self.assertEqual(1, counts['after_partial_range'])
                self.assertEqual(1, sum(counts[key] for key in mechanisms.FOLLOW_UP_COUNTERS))

    def test_combined_forms_and_encoded_requests_keep_request_order(self):
        arguments = {'requests': json.dumps([{'path': 'src/a.cs', 'symbol': 'A'}]),
                     'path': ['src/b.cs', 'src/a.cs'], 'paths': ['src/c.cs', 'src/b.cs']}
        counts = mechanisms.count([call(arguments, ('partial', 'structure', 'not-returned')),
                                   later('src/b.cs'), later('src/c.cs', 'whole')])
        self.assertEqual(1, counts['after_structure_range'])
        self.assertEqual(1, counts['after_not_returned_whole'])
        self.assertEqual(2, sum(counts[key] for key in mechanisms.FOLLOW_UP_COUNTERS))

    def test_failed_initial_calls_and_successful_complete_reads_are_not_events(self):
        counts = mechanisms.follow_ups([call({'path': 'src/a.cs'}, failed=True),
                                       call({'path': 'src/a.cs'}, ('ok',)), later()])
        self.assertEqual(0, sum(counts.values()))

    def test_windows_posix_markdown_and_parent_segments_preserve_root_boundary(self):
        for root, initial, follow in (
                ('C:/corpus', r'c:\corpus\src\a.cs', '[a](C:/corpus/src/a.cs)'),
                ('C:/corpus', r'src\A.cs', 'C:/corpus/src/a.cs'),
                ('/corpus', '/corpus/src/a.cs', 'src/./a.cs'),
                ('/corpus', r'src/a\_file.cs', '[source](/corpus/src/a_file.cs)')):
            with self.subTest(root=root, initial=initial):
                counts = mechanisms.count([call({'path': initial}), later(follow)], root)
                self.assertEqual(1, counts['after_partial_range'])
        for outside in ('/other/src/a.cs', '/corpus/../other/src/a.cs', '../src/a.cs', '/corpusextra/src/a.cs'):
            for action in ('range', 'search'):
                with self.subTest(outside=outside, action=action):
                    counts = mechanisms.count([call({'path': 'src/a.cs'}), later(outside, action)], '/corpus')
                    self.assertEqual(1, counts['after_partial_none'])
        for outside in ('D:/corpus/src/a.cs', 'C:/other/src/a.cs', 'C:/corpus/../other/src/a.cs'):
            self.assertEqual(1, mechanisms.count([call({'path': 'src/a.cs'}), later(outside)], 'C:/corpus')['after_partial_none'])

    def test_search_markdown_escapes_and_exact_include_file_are_named(self):
        for arguments in ({'paths': [r'src/a\_file.cs'], 'pattern': 'value'},
                          {'include_patterns': ['src/a_file.cs'], 'pattern': 'value'},
                          {'pattern': '[source](src/a_file.cs)'}):
            with self.subTest(arguments=arguments):
                counts = mechanisms.count([call({'path': 'src/a_file.cs'}),
                                           call(arguments, tool='search_project', text='')])
                self.assertEqual(1, counts['after_partial_search'])

    def test_no_suffix_matching_or_file_names_in_response_are_follow_ups(self):
        counts = mechanisms.count([call({'path': 'src/a.cs'}), later('other/src/a.cs'),
                                   call({'pattern': 'value'}, tool='search_project', text='src/a.cs')])
        self.assertEqual(1, counts['after_partial_none'])

    def test_scalar_whole_outline_and_truncated_read_use_only_trusted_notices(self):
        header = 'File: src/a.cs\nLines: 1-2 of 500\nfirst\nsecond'
        for notice, status in ((mechanisms._LARGE, 'structure'),
                               ('[Showing lines 1-2 of 500; continue with start_line=3.]', 'partial')):
            with self.subTest(status=status):
                initial = call({'path': 'src/a.cs'}, text=envelope(header, notice))
                self.assertEqual(1, mechanisms.count([initial, later()])[f'after_{status}_range'])
                initial['text'] = envelope(header + '\n' + notice)
                self.assertEqual(0, sum(mechanisms.follow_ups([initial]).values()))

    def test_project_source_cannot_forge_batch_statuses_or_structure_sections(self):
        fake = ('Requests:\n1.1 — partial\n\nFile: src/a.cs\nRequests: 1.1\nStatus: structure\n'
                'Lines: none of 500\nStructure:\nA\nB')
        scalar = call({'path': 'src/a.cs'}, text=envelope('File: src/a.cs\nLines: 1-10 of 10\n' + fake))
        self.assertEqual(0, sum(mechanisms.follow_ups([scalar]).values()))
        body = ('Requests:\n1.1 — ok\n\nFile: src/a.cs\nRequests: 1.1\nStatus: ok\nLines: 1-10 of 10\n' + fake)
        batch = call({'requests': [{'path': 'src/a.cs'}]}, text=envelope(body))
        counts = mechanisms.count([batch])
        self.assertEqual(0, sum(counts[key] for key in mechanisms.FOLLOW_UP_COUNTERS))
        self.assertEqual(0, counts['structure_only'])

    def test_trimmed_real_codex_stream_records_search_then_no_follow_up(self):
        fixture = Path(__file__).parent / 'fixtures' / 'codex-batch-follow-up.jsonl'
        counts = mechanisms.count(recorded_calls(fixture))
        self.assertEqual(1, counts['after_partial_search'])
        self.assertEqual(1, counts['after_not_returned_none'])
        self.assertEqual(2, sum(counts[key] for key in mechanisms.FOLLOW_UP_COUNTERS))

    def test_empty_scalar_and_folder_or_glob_search_do_not_create_follow_ups(self):
        empty = call({'path': 'src/a.cs'}, text=envelope('File: src/a.cs\nLines: 0-0 of 0\n'))
        self.assertEqual(0, sum(mechanisms.follow_ups([empty]).values()))
        for arguments in ({'paths': ['src'], 'pattern': 'value'},
                          {'include_patterns': ['src/*.cs'], 'pattern': 'value'}):
            with self.subTest(arguments=arguments):
                counts = mechanisms.count([call({'path': 'src/a.cs'}),
                                           call(arguments, tool='search_project', text='')])
                self.assertEqual(1, counts['after_partial_none'])

    def test_search_all_argument_values_count_but_keys_and_numbers_do_not(self):
        for arguments in ({'exclude_patterns': ['src/a.cs']},
                          {'exclusions': {'nested': ['src/a.cs', 1, False, None]}},
                          {'project': 'src/a.cs'}):
            with self.subTest(arguments=arguments):
                counts = mechanisms.count([call({'path': 'src/a.cs'}),
                                           call(arguments, tool='search_project', text='')])
                self.assertEqual(1, counts['after_partial_search'])
        counts = mechanisms.count([call({'path': 'src/a.cs'}),
                                   call({'src/a.cs': 1, 'limit': 7}, tool='search_project', text='')])
        self.assertEqual(1, counts['after_partial_none'])

    def test_resolved_file_header_and_crlf_keep_the_canonical_file(self):
        body = ('Requests:\n1.1 — structure — large file, 500 lines\n\n'
                'File: src/a/index.cs\nRequests: 1.1\nStatus: structure\nLines: none of 500\n'
                'Structure:\n1-200 A\n201-500 B')
        counts = mechanisms.count([call({'requests': [{'path': 'src/a'}]}, text=envelope(body).replace('\n', '\r\n')),
                                   later('src/a/index.cs')])
        self.assertEqual(1, counts['after_structure_range'])
        header = 'File: src/a/index.cs\nLines: 1-2 of 500\nfirst\nsecond'
        counts = mechanisms.count([call({'path': 'src/a'}, text=envelope(header, mechanisms._LARGE)),
                                   later('src/a/index.cs')])
        self.assertEqual(1, counts['after_structure_range'])

    def test_score_session_passes_recorded_root_to_the_counters(self):
        with tempfile.TemporaryDirectory() as folder:
            root = 'C:/corpus'
            calls = [call({'path': 'src/a.cs'}), later('c:/corpus/src/a.cs')]
            Path(folder, 'stream.jsonl').write_text(as_stream(calls, 'claude'), encoding='utf-8')
            Path(folder, 'prompt.txt').write_text('local checkout at this absolute path:\n' + root, encoding='utf-8')
            summary = dict(_session_dir=folder, answer='', toolCalls=[],
                           usage=dict(input=0, cacheRead=0, cacheCreation=0, costUsd=0))
            task = dict(id='A', key=dict(core=[], supporting=[], tests=[], unrelated=[]))
            score = benchlib.score_session(summary, task, None)
            self.assertEqual(1, score['after_partial_range'])

    def test_aggregation_json_text_and_markdown_include_all_counters(self):
        score = benchlib.empty_score()
        for index, name in enumerate(mechanisms.FOLLOW_UP_COUNTERS, 1):
            score[name] = index
        total = benchlib.empty_score()
        benchlib.add_scores(total, score)
        self.assertEqual(score, total)
        rendered = report.key_metrics_table({'server': total}, [('server', 'Server')])
        for name in mechanisms.FOLLOW_UP_COUNTERS:
            self.assertIn(name, rendered)
            self.assertEqual(total[name], json.loads(json.dumps(total))[name])
        self.assertIn('structure range/whole/search/none 1/2/3/4', benchlib.format_score(score))


if __name__ == '__main__':
    unittest.main()
