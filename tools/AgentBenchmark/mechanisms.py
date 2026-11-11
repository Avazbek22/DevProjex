"""Counts observed delivery mechanisms independently of the benchmark client."""
import re

COUNTERS = ('structure_large', 'structure_no_room', 'structure_with_text', 'structure_only',
            'read_as_ranges', 'read_as_combined', 'read_as_folders', 'symbols_as_text')


def count(calls):
    result = dict.fromkeys(COUNTERS, 0)
    for call in calls:
        text = call['text'].replace('\r\n', '\n').replace('\r', '\n')
        trusted = re.sub(r'<untrusted-data-([a-f0-9]{24})>.*?</untrusted-data-\1>', '', text, flags=re.S)
        if call['is_error']:
            continue
        for name, phrase in (
                ('read_as_ranges', r'\[Read as\] start_line and end_line were applied to each of the \d+ files\.'),
                ('read_as_combined', r'\[Read as\] path and requests were combined; \d+ file\(s\) from path are read whole\.'),
                ('read_as_folders', r'\[Read as\] \d+ folder\(s\) were replaced by the \d+ file\(s\) directly inside them as seeds\.'),
                ('symbols_as_text', re.escape('[Symbols] symbols=true takes declaration names; this pattern is a regular expression, so it was searched as text.'))):
            result[name] += len(re.findall(r'(?m)^' + phrase + '$', trusted))
        if call['tool'] != 'get_file':
            continue
        reasons = dict(re.findall(r'(?m)^(\d+\.\d+) — structure — ([^\n]+)$', text))
        for section in re.finditer(r'(?m)^File: [^\n]+\nRequests: ([^\n]+)\nStatus: structure\nLines: ([^\n]+)', text):
            identifiers = [item.strip() for item in section[1].split(',')]
            reason = next((reasons[item] for item in identifiers if item in reasons), '')
            result['structure_large'] += reason.startswith('large file, ')
            result['structure_no_room'] += reason.startswith('no room left in this response, ')
            result['structure_only' if section[2].startswith('none of ') else 'structure_with_text'] += 1
    return result
