"""Conservative evidence of paths and source text actually returned to a benchmark client."""
import json
import re
import argparse
from pathlib import Path
from xml.etree import ElementTree

import benchlib

LEVELS = ('never-shown', 'listed', 'content-shown')


def tree_paths(text):
    parents, found = [], set()
    block = re.search(r'<untrusted-data-[^>]+>(.*?)</untrusted-data-[^>]+>', text, re.S)
    body = block[1].strip() if block else text.strip()
    if body.startswith('{'):
        try:
            pending = [('', json.loads(body)['tree'])]
            while pending:
                prefix, children = pending.pop()
                if isinstance(children, dict):
                    pending.extend(((prefix + '/' if prefix else '') + name, child) for name, child in children.items())
                elif isinstance(children, list):
                    for child in children:
                        if isinstance(child, str):
                            found.add((prefix + '/' if prefix else '') + child)
                        else:
                            pending.append((prefix, child))
            return found
        except (ValueError, KeyError):
            pass
    if body.startswith('<'):
        try:
            pending = [('', ElementTree.fromstring(body))]
            while pending:
                prefix, node = pending.pop()
                if node.tag == 'f':
                    found.add((prefix + '/' if prefix else '') + (node.text or ''))
                else:
                    if node.tag == 'd':
                        prefix = (prefix + '/' if prefix else '') + node.get('n', '')
                    pending.extend((prefix, child) for child in node)
            return found
        except ElementTree.ParseError:
            pass
    for line in text.splitlines():
        match = re.match(r'^( *)- (.+?)(?:\s+\[.*)?$', line)
        branch = re.match(r'^([│ ]*)[├└]── (.+)$', line)
        if branch:
            match = branch
        if not match:
            continue
        indent = len(match[1])
        name = re.sub(r'\\([_*`\[\]])', r'\1', match[2].strip())
        while parents and parents[-1][0] >= indent:
            parents.pop()
        prefix = '/'.join(part for _, part in parents)
        path = (prefix + '/' if prefix else '') + name.rstrip('/')
        if name.endswith('/') or branch:
            parents.append((indent, name.rstrip('/')))
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
    elif call['tool'] in ('read_repomix_output', 'grep_repomix_output', 'pack_context', 'read_pack'):
        try:
            data = json.loads(text)
            if call['tool'] == 'grep_repomix_output':
                text = '\n'.join(re.sub(r'^\d+[:-]', '', line) for line in data.get('formattedOutput', []))
                text = text.replace('\n--\n', '\n</file>\n')
            else:
                text = data.get('content', text)
        except (ValueError, AttributeError):
            pass
        for match in re.finditer(r'<file path="([^"]+)">\s*([^<].*?)(?:</file>|$)', text, re.S):
            found |= benchlib.paths_in(match[1], root)
        for match in re.finditer(r'(?m)^## (?:File: )?([^\n]+\.[A-Za-z0-9]+)\n+```[^\n]*\n([^`]+)', text):
            found |= benchlib.paths_in(match[1], root)
    if call['tool'] == 'read_pack':
        found |= content_paths({**call, 'tool': 'search_project'}, root)
    return found


def attribute(calls, task, answer, root=None, key_version=None):
    version = key_version if key_version is not None else task.get('keyVersion', 1)
    key = benchlib.task_key(task, version)
    required = sorted(set(key['core']) | set(key['supporting']) | set(key['tests']))
    named = benchlib.paths_in(answer, root)
    entries = {path: {'path': path, 'kind': 'test' if path in key['tests'] else 'file',
                      'named': benchlib.mentions(path, named), 'exposure': LEVELS[0],
                      'firstShown': None, 'firstContent': None} for path in required}
    for call in sorted(calls, key=lambda item: item.get('completionOrder', item['sequence'])):
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--session', required=True)
    parser.add_argument('--tasks-file', required=True)
    parser.add_argument('--key-version', type=benchlib.parse_key_version)
    parser.add_argument('--output')
    args = parser.parse_args()
    from provenance import inspect_session
    from transcripts import recorded_calls, recorded_root
    folder = Path(args.session)
    summary = json.loads((folder / 'summary.json').read_text(encoding='utf-8'))
    tasks = benchlib.load_tasks(args.tasks_file)
    task = next(task for task in tasks['tasks'] if task['id'] == summary['task'])
    version = args.key_version if args.key_version is not None else summary.get('keyVersion', 1)
    if version == 'current':
        version = task.get('keyVersion', 1)
    output = {'id': summary['id'], 'keyVersion': version, 'provenance': inspect_session(folder),
              'paths': attribute(recorded_calls(folder / 'stream.jsonl'), task, summary.get('answer') or '',
                                 recorded_root(folder), version)}
    rendered = json.dumps(output, indent=1, ensure_ascii=False) + '\n'
    if args.output:
        Path(args.output).write_text(rendered, encoding='utf-8')
    else:
        print(rendered, end='')


if __name__ == '__main__':
    main()
