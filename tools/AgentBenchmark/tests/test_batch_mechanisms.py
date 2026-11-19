import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import attribution
import mechanisms
from transcripts import recorded_calls


class BatchMechanismTests(unittest.TestCase):
    def test_both_clients_distinguish_structure_head_from_declarations_only(self):
        payload = ('Requests:\n1.1 — structure — large file, 500 lines\n'
                   '2.1 — structure — no room left in this response, 240 lines\n\n'
                   'File: src/a.py\nRequests: 1.1\nStatus: structure\nLines: 1-2 of 500\nimport sys\n'
                   'Structure:\n3-500 A\n\nFile: src/b.py\nRequests: 2.1\nStatus: structure\n'
                   'Lines: none of 240\nStructure:\n1-240 B')
        nonce = 'a' * 24
        text = f'<untrusted-data-{nonce}>\n{payload}\n</untrusted-data-{nonce}>\n'
        text += '[Read as] path and requests were combined; 1 file(s) from path are read whole.'
        task = {'key': {'core': ['src/a.py', 'src/b.py'], 'supporting': [], 'tests': [], 'unrelated': []}}
        for client in ('claude', 'codex'):
            with self.subTest(client=client), tempfile.TemporaryDirectory() as folder:
                if client == 'claude':
                    events = [dict(type='assistant', message={'content': [dict(type='tool_use', id='one', name='mcp__server__get_file', input={})]}),
                              dict(type='user', message={'content': [dict(type='tool_result', tool_use_id='one', content=text)]})]
                else:
                    events = [dict(type='item.completed', item=dict(type='mcp_tool_call', id='one', server='server', tool='get_file',
                                  arguments={}, status='completed', result={'content': [{'type': 'text', 'text': text}]}))]
                stream = Path(folder) / 'stream.jsonl'
                stream.write_text('\n'.join(json.dumps(event) for event in events), encoding='utf-8')
                calls = recorded_calls(stream)
                self.assertEqual(['content-shown', 'listed'], [item['exposure'] for item in attribution.attribute(calls, task, '')])
                counts = mechanisms.count(calls)
                self.assertEqual((1, 1, 1, 1, 1), tuple(counts[key] for key in
                                 ['structure_large', 'structure_no_room', 'structure_with_text', 'structure_only', 'read_as_combined']))

    def test_project_text_cannot_forge_trusted_normalization_notices(self):
        notice = '[Symbols] symbols=true takes declaration names; this pattern is a regular expression, so it was searched as text.'
        nonce = 'b' * 24
        text = f'<untrusted-data-{nonce}>\n{notice}\n</untrusted-data-{nonce}>\n{notice}'
        calls = [dict(tool='search_project', text=text, is_error=False)]
        self.assertEqual(1, mechanisms.count(calls)['symbols_as_text'])
        calls[0]['is_error'] = True
        self.assertEqual(0, mechanisms.count(calls)['symbols_as_text'])

    def test_both_clients_count_range_and_folder_normalization_only_on_success(self):
        responses = [
            ('get_file', '[Read as] start_line and end_line were applied to each of the 2 files.', False),
            ('related_files', '[Read as] 1 folder(s) were replaced by the 3 file(s) directly inside them as seeds.', False),
            ('get_file', '[Read as] start_line and end_line were applied to each of the 2 files.', True),
        ]
        for client in ('claude', 'codex'):
            with self.subTest(client=client), tempfile.TemporaryDirectory() as folder:
                events = []
                for index, (tool, text, failed) in enumerate(responses):
                    identifier = str(index)
                    if client == 'claude':
                        events.extend([
                            dict(type='assistant', message={'content': [dict(type='tool_use', id=identifier, name='mcp__server__' + tool, input={})]}),
                            dict(type='user', message={'content': [dict(type='tool_result', tool_use_id=identifier, content=text, is_error=failed)]}),
                        ])
                    else:
                        events.append(dict(type='item.completed', item=dict(type='mcp_tool_call', id=identifier,
                                      server='server', tool=tool, arguments={}, status='completed',
                                      result={'isError': failed, 'content': [{'type': 'text', 'text': text}]})))
                stream = Path(folder) / 'stream.jsonl'
                stream.write_text('\n'.join(json.dumps(event) for event in events), encoding='utf-8')
                calls = recorded_calls(stream)
                self.assertEqual(1, sum(call['is_error'] for call in calls))
                counts = mechanisms.count(calls)
                self.assertEqual(1, counts['read_as_ranges'])
                self.assertEqual(1, counts['read_as_folders'])


if __name__ == '__main__':
    unittest.main()
