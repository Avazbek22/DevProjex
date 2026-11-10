"""Conservative evidence of paths and source text actually returned to a benchmark client."""
import json
import re

import benchlib

LEVELS = ('never-shown', 'listed', 'content-shown')


def tree_paths(text):
    parents, found = [], set()
    for line in text.splitlines():
        match = re.match(r'^( *)- (.+?)(?:\s+\[.*)?$', line)
        if not match:
            continue
        indent = len(match[1])
        name = re.sub(r'\\([_*`\[\]])', r'\1', match[2].strip())
        while parents and parents[-1][0] >= indent:
            parents.pop()
        prefix = '/'.join(part for _, part in parents)
        path = (prefix + '/' if prefix else '') + name.rstrip('/')
        if name.endswith('/'):
            parents.append((indent, name.rstrip('/')))
        else:
            found.add(path)
    return found


def content_paths(call, root):
    if call['is_error']:
        return set()
    text = call['text'].replace('\r\n', '\n').replace('\r', '')
    found = set()
    if call['tool'] == 'get_file':
        sections = re.split(r'(?m)^File: (.+)\n', text)
        for index in range(1, len(sections), 2):
            body = sections[index + 1]
            if re.search(r'(?m)^Lines: \d+-\d+ of \d+\n(?!</untrusted-data|\[)', body):
                found |= benchlib.paths_in(sections[index], root)
    elif call['tool'] == 'search_project':
        current = set()
        for line in text.splitlines():
            if re.match(r'^\d+[:-]', line) and current:
                found |= current
            elif re.fullmatch(r'[^\s`]+\.[A-Za-z0-9]+', line):
                current = benchlib.paths_in(line, root)
            elif line.startswith('File: '):
                current = benchlib.paths_in(line[6:], root)
    elif call['tool'] == 'get_diff':
        current = set()
        for line in text.splitlines():
            if line.startswith('+++ b/'):
                current = benchlib.paths_in(line[6:], root)
            elif re.match(r'^@@ .*@@', line):
                found |= current
    elif call['tool'] in ('read_repomix_output', 'pack_context', 'read_pack'):
        try:
            data = json.loads(text)
            text = data.get('content', text)
        except (ValueError, AttributeError):
            pass
        for match in re.finditer(r'<file path="([^"]+)">\s*([^<].*?)(?:</file>|$)', text, re.S):
            found |= benchlib.paths_in(match[1], root)
        for match in re.finditer(r'(?m)^## (?:File: )?([^\n]+\.[A-Za-z0-9]+)\n+```[^\n]*\n([^`]+)', text):
            found |= benchlib.paths_in(match[1], root)
    return found


def attribute(calls, task, answer, root=None, key_version=None):
    version = key_version if key_version is not None else task.get('keyVersion', 1)
    key = benchlib.task_key(task, version)
    required = sorted(set(key['core']) | set(key['supporting']) | set(key['tests']))
    named = benchlib.paths_in(answer, root)
    entries = {path: {'path': path, 'kind': 'test' if path in key['tests'] else 'file',
                      'named': benchlib.mentions(path, named), 'exposure': LEVELS[0],
                      'firstShown': None, 'firstContent': None} for path in required}
    for call in calls:
        listed = benchlib.paths_in(call['text'], root)
        if call['tool'] == 'get_tree':
            listed |= tree_paths(call['text'])
        content = content_paths(call, root)
        for path, entry in entries.items():
            evidence = {'call': call['sequence'], 'tool': call['tool']}
            if benchlib.mentions(path, listed | content) and entry['firstShown'] is None:
                entry['firstShown'] = evidence
                entry['exposure'] = LEVELS[1]
            if benchlib.mentions(path, content) and entry['firstContent'] is None:
                entry['firstContent'] = evidence
                entry['exposure'] = LEVELS[2]
    return list(entries.values())
