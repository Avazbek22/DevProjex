"""Counts observed delivery mechanisms independently of the benchmark client."""
import json
import re

import benchlib

COUNTERS = ('structure_large', 'structure_no_room', 'structure_with_text', 'structure_only',
            'read_as_ranges', 'read_as_combined', 'read_as_folders', 'symbols_as_text')
FOLLOW_UP_COUNTERS = tuple(f'after_{status}_{action}'
                           for status in ('structure', 'partial', 'not_returned')
                           for action in ('range', 'whole', 'search', 'none'))
COUNTERS += FOLLOW_UP_COUNTERS


def count(calls, root=None):
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
        body = _response_body(text)
        reasons = {row[1]: row[3] or '' for row in _status_rows(body)}
        for _, identifiers, status, lines in _batch_sections(body):
            if status != 'structure':
                continue
            reason = next((reasons[item] for item in identifiers if item in reasons), '')
            result['structure_large'] += reason.startswith('large file, ')
            result['structure_no_room'] += reason.startswith('no room left in this response, ')
            result['structure_only' if lines.startswith('none of ') else 'structure_with_text'] += 1
    result.update(follow_ups(calls, root))
    return result


_ENVELOPE = re.compile(r'<untrusted-data-([a-f0-9]{24})>(.*?)</untrusted-data-\1>', re.S)
_ROW = re.compile(r'(\d+\.\d+) — (ok|structure|partial|not-returned|unavailable)(?: — ([^\n]*))?')
_SELECTORS = ('start_line', 'end_line', 'start_column', 'symbol', 'ranges')
_LARGE = ('[Large file] only the start and the structure are shown; read any part with '
          'start_line and end_line, or the whole file with start_line=1 and end_line set to its last line.')


def _response_body(text):
    block = _ENVELOPE.search(text)
    return block[2].strip('\n') if block else text


def _status_rows(body):
    if not body.startswith('Requests:\n'):
        return []
    rows = []
    for line in body.split('\n')[1:]:
        row = _ROW.fullmatch(line)
        if row is None:
            break
        rows.append(row)
    return rows


def _batch_sections(body):
    rows = {row[1]: row[2] for row in _status_rows(body)}
    lines = body.split('\n')
    position = len(rows) + 1
    while position < len(lines):
        header = re.match(r'File: ([^\n]+)\nRequests: ([^\n]+)\nStatus: (ok|structure|partial)\nLines: (none of \d+|\d+-\d+ of \d+)$',
                          '\n'.join(lines[position:position + 4]))
        if header is None:
            position += 1
            continue
        identifiers = [item.strip() for item in header[2].split(',')]
        if any(identifier not in rows for identifier in identifiers):
            position += 1
            continue
        yield header[1], identifiers, header[3], header[4]
        bounds = re.match(r'(\d+)-(\d+) of \d+', header[4])
        position += 4 + (max(0, int(bounds[2]) - int(bounds[1]) + 1) if bounds else 0)


def _path(value, root):
    if not isinstance(value, str):
        return None
    value = value.strip().strip('`')
    link = re.fullmatch(r'\[[^\n]*\]\(([^\n]*)\)', value)
    if link:
        value = link[1]
    value = re.sub(r'\\([_*`\[\]])', r'\1', value)
    value = benchlib._normalize_answer_path(value, root)
    if value in ('', '.') or value.startswith('../') or benchlib._absolute_answer_path(value):
        return None
    windows = root is not None and bool(re.match(r'^(?:/?[A-Za-z]:[\\/]|[\\/]{2})', str(root)))
    return value.casefold() if windows else value


def _requests(arguments, root):
    if not isinstance(arguments, dict):
        return []
    requests = arguments.get('requests')
    if isinstance(requests, str):
        try:
            requests = json.loads(requests)
        except ValueError:
            requests = []
    items = []
    if isinstance(requests, list):
        for index, request in enumerate(requests, 1):
            if isinstance(request, dict) and (path := _path(request.get('path'), root)):
                items.append((path, not any(request.get(key) is not None for key in _SELECTORS), f'{index}.1'))
    seen = {item[0] for item in items}
    for name in ('path', 'paths'):
        values = arguments.get(name, [])
        for value in values if isinstance(values, list) else [values]:
            path = _path(value, root)
            if path is not None and path not in seen:
                seen.add(path)
                whole = requests is not None or not any(arguments.get(key) is not None for key in _SELECTORS)
                items.append((path, whole, f'{len(items) + 1}.1'))
    return items


def _delivery_events(call, root):
    if call['tool'] != 'get_file' or call['is_error']:
        return []
    requests = _requests(call.get('arguments', {}), root)
    whole = {identifier: path for path, is_whole, identifier in requests if is_whole}
    if not whole:
        return []
    text = call['text'].replace('\r\n', '\n').replace('\r', '\n')
    blocks = list(_ENVELOPE.finditer(text))
    body = _response_body(text)
    if body.startswith('Requests:\n'):
        events = {}
        delivered = {identifier: path for name, identifiers, _, _ in _batch_sections(body)
                     if (path := _path(name, root)) is not None for identifier in identifiers}
        for row in _status_rows(body):
            if row[1] in whole and row[2] in ('structure', 'partial', 'not-returned'):
                events.setdefault(delivered.get(row[1], whole[row[1]]), row[2].replace('-', '_'))
        return list(events.items())
    if len(requests) != 1 or len(whole) != 1:
        return []
    header = re.match(r'File: ([^\n]+)\nLines: (\d+)-(\d+) of (\d+)\n', body)
    if header is None or (path := _path(header[1], root)) is None:
        return []
    trusted = _ENVELOPE.sub('', text) if blocks else ''
    if _LARGE in trusted.splitlines():
        return [(path, 'structure')]
    start, end, total = map(int, header.groups()[1:])
    notice = rf'\[Showing lines {start}-{end} of {total}; continue with start_line=\d+(?: start_column=\d+)?\.\]'
    if re.search(r'(?m)^' + notice + '$', trusted):
        return [(path, 'partial')]
    return []


def _named_files(call, root):
    if call['tool'] == 'get_file':
        named = {}
        for path, whole, _ in _requests(call.get('arguments', {}), root):
            action = 'whole' if whole else 'range'
            if path not in named or action == 'range':
                named[path] = action
        return named
    if call['tool'] != 'search_project':
        return {}
    pending, named = [call.get('arguments', {})], {}
    while pending:
        value = pending.pop()
        if isinstance(value, dict):
            pending.extend(value.values())
        elif isinstance(value, list):
            pending.extend(value)
        elif isinstance(value, str):
            if (path := _path(value, root)) is not None:
                named[path] = 'search'
            for candidate in benchlib.paths_in(value, root):
                if (path := _path(candidate, root)) is not None:
                    named[path] = 'search'
    return named


def follow_ups(calls, root=None):
    result = dict.fromkeys(FOLLOW_UP_COUNTERS, 0)
    pending = {}
    for call in calls:
        for path, action in _named_files(call, root).items():
            for status in pending.pop(path, []):
                result[f'after_{status}_{action}'] += 1
        for path, status in _delivery_events(call, root):
            pending.setdefault(path, []).append(status)
    for statuses in pending.values():
        for status in statuses:
            result[f'after_{status}_none'] += 1
    return result
