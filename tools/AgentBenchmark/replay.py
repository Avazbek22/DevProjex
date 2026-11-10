"""Replay recorded MCP calls without a model and compare recorded or two live host responses."""
import argparse
import difflib
import json
import os
import queue
import re
import subprocess
import threading
import time
from collections import Counter
from pathlib import Path

import benchlib
from transcripts import recorded_calls, recorded_root, text_content

NORMALIZATIONS = (
    'CRLF and CR become LF',
    'exact configured corpus and state roots become placeholders (including JSON escaping)',
    'separators in those relocated paths become forward slashes',
    'spotlight envelope nonces become a fixed nonce',
    'stored-result identifiers are mapped in first-occurrence order',
)


def normalize_response(text, roots=(), identifiers=None):
    text = text.replace('\r\n', '\n').replace('\r', '\n')
    for root in sorted(filter(None, map(str, roots)), key=len, reverse=True):
        for spelling in sorted({root, root.replace('\\', '/'), root.replace('/', '\\')}, key=len, reverse=True):
            for value in (json.dumps(spelling)[1:-1], spelling):
                text = re.sub(re.escape(value), '<ROOT>', text, flags=re.I if re.match(r'^[A-Za-z]:', root) else 0)
    text = re.sub(r'<ROOT>(?:[\\/][^\s<>"\'`]+)*',
                  lambda match: re.sub(r'\\+', '/', match[0]), text)
    text = re.sub(r'(?<=untrusted-data-)[0-9a-f]{24}(?=>)', 'NONCE', text)
    identifiers = identifiers if identifiers is not None else {}

    def replace(match):
        value = match[2]
        identifiers.setdefault(value, f'ID{len(identifiers) + 1}')
        return match[1] + identifiers[value]

    return re.sub(r'(pack_id[=":\s]+)([a-zA-Z0-9_-]{16,64})', replace, text)


class StdioClient:
    def __init__(self, command, cwd, data_root, timeout=60):
        self.timeout, self.sequence, self.messages = timeout, 0, queue.Queue()
        self.process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL, text=True, encoding='utf-8',
                                        env={**os.environ, 'DEVPROJEX_INTERNAL_DATA_ROOT': str(data_root),
                                             'DEVPROJEX_LANGUAGE': 'en'},
                                        creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        threading.Thread(target=self._read, daemon=True).start()
        self.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                   'clientInfo': {'name': 'benchmark-replay', 'version': '1'}})
        self.send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})

    def _read(self):
        for line in self.process.stdout:
            try:
                self.messages.put(json.loads(line))
            except ValueError:
                continue
        self.messages.put(None)

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + '\n')
        self.process.stdin.flush()

    def request(self, method, params):
        self.sequence += 1
        identifier = self.sequence
        self.send({'jsonrpc': '2.0', 'id': identifier, 'method': method, 'params': params})
        deadline = time.monotonic() + self.timeout
        while True:
            message = self.messages.get(timeout=max(0.001, deadline - time.monotonic()))
            if message is None:
                raise RuntimeError(f'MCP process exited during {method}')
            if message.get('id') == identifier and 'method' not in message:
                return message
            if 'method' in message and 'id' in message:
                self.send({'jsonrpc': '2.0', 'id': message['id'], 'error':
                           {'code': -32601, 'message': 'Client method not supported'}})

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        self.process.stdout.close()


def rewrite_arguments(value, original, current, ids=None):
    if isinstance(value, dict):
        return {key: rewrite_arguments(item, original, current, ids) for key, item in value.items()}
    if isinstance(value, list):
        return [rewrite_arguments(item, original, current, ids) for item in value]
    if isinstance(value, str):
        if ids and value in ids:
            return ids[value]
        if benchlib._absolute_answer_path(value.replace('\\', '/')):
            relative = benchlib._normalize_answer_path(value, original)
            if not benchlib._absolute_answer_path(relative) and not relative.startswith('../'):
                return str(Path(current) / relative)
    return value


def replay_session(folder, command, corpus, output):
    original = recorded_root(folder)
    if not original:
        raise ValueError('The recorded prompt does not identify a corpus')
    output = Path(output)
    data_root = output / 'state'
    data_root.mkdir(parents=True)
    client, rows, ids = None, [], {}
    try:
        client = StdioClient([*command, 'mcp', '--root', str(corpus)], corpus, data_root)
        for call in recorded_calls(Path(folder) / 'stream.jsonl'):
            arguments = rewrite_arguments(call['arguments'], original, corpus, ids)
            started = time.perf_counter_ns()
            response = client.request('tools/call', {'name': call['tool'], 'arguments': arguments})
            elapsed = (time.perf_counter_ns() - started) / 1e6
            result = response.get('result', response.get('error', {}))
            rendered = text_content(result)
            old_ids = re.findall(r'pack_id[=":\s]+([a-zA-Z0-9_-]{16,64})', call['text'])
            new_ids = re.findall(r'pack_id[=":\s]+([a-zA-Z0-9_-]{16,64})', rendered)
            ids.update(zip(old_ids, new_ids))
            rows.append({'sequence': call['sequence'], 'tool': call['tool'], 'arguments': arguments,
                         'response': result, 'text': rendered, 'is_error': bool(result.get('isError') or 'error' in response),
                         'durationMs': elapsed})
    finally:
        if client:
            client.close()
    (output / 'responses.json').write_text(json.dumps(rows, indent=1, ensure_ascii=False), encoding='utf-8')
    return rows


def compare_calls(left, right, roots=()):
    if len(left) != len(right):
        raise ValueError('Replays have different call counts')
    differences, totals = [], Counter()
    left_ids, right_ids = {}, {}
    for before, after in zip(left, right):
        if (before['sequence'], before['tool']) != (after['sequence'], after['tool']):
            raise ValueError('Replays have different call order')
        a = normalize_response(before['text'], roots, left_ids)
        b = normalize_response(after['text'], roots, right_ids)
        if a != b or before.get('is_error', False) != after.get('is_error', False):
            totals[before['tool']] += 1
            differences.append({'call': before['sequence'], 'tool': before['tool'],
                                'diff': ''.join(difflib.unified_diff(a.splitlines(True), b.splitlines(True),
                                                                  fromfile='before', tofile='after'))})
    return {'calls': len(left), 'changed': len(differences), 'byTool': dict(totals), 'differences': differences}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--session', required=True)
    parser.add_argument('--corpus', required=True)
    parser.add_argument('--host', required=True)
    parser.add_argument('--other-host')
    parser.add_argument('--recorded', action='store_true', help='compare the host to the original recorded text')
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    output = Path(args.output)
    left = replay_session(args.session, [args.host], args.corpus, output / 'first')
    if args.recorded:
        right, left = left, recorded_calls(Path(args.session) / 'stream.jsonl')
    else:
        right = replay_session(args.session, [args.other_host or args.host], args.corpus, output / 'second')
    comparison = compare_calls(left, right, [recorded_root(args.session), args.corpus,
                                           output / 'first/state', output / 'second/state'])
    (output / 'comparison.json').write_text(json.dumps(comparison, indent=1), encoding='utf-8')
    print(json.dumps({key: value for key, value in comparison.items() if key != 'differences'}))


if __name__ == '__main__':
    main()
