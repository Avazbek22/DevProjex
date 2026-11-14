"""Client-neutral MCP calls from recorded JSONL. No model or server is started here."""
import json
import re
from pathlib import Path


def text_content(value):
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        return '\n'.join(text_content(part) for part in value)
    if isinstance(value, dict):
        if value.get('type') == 'text':
            return value.get('text', '')
        if 'content' in value:
            return text_content(value['content'])
        return json.dumps(value, ensure_ascii=False)
    return ''


def recorded_calls(path, include_builtin=False):
    """Keep invocation order, join Claude results by ID, and admit completed Codex items once."""
    calls, pending, completed, codex_calls = [], {}, set(), {}
    with Path(path).open(encoding='utf-8') as source:
        for number, line in enumerate(source, 1):
            if not line.strip() or not line.lstrip().startswith('{'):
                continue
            try:
                event = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f'invalid transcript JSON at line {number}') from error
            item = event.get('item', {})
            if event.get('type') in ('item.started', 'item.updated', 'item.completed') and item.get('type') == 'mcp_tool_call':
                identifier = item['id']
                if identifier not in codex_calls:
                    call = dict(sequence=len(calls) + 1, id=identifier, server=item.get('server'), tool=item['tool'],
                                arguments=item.get('arguments', {}), text='', result=None, is_error=True)
                    calls.append(call)
                    codex_calls[identifier] = call
                if event['type'] == 'item.completed' and identifier not in completed:
                    completed.add(identifier)
                    codex_calls[identifier].update(
                        text=text_content(item.get('result')), result=item.get('result'), completionOrder=number,
                        is_error=bool(item.get('error') or item.get('status') == 'failed'
                                      or (item.get('result') or {}).get('isError')))
            if event.get('type') == 'assistant':
                for block in event.get('message', {}).get('content', []):
                    if block.get('type') != 'tool_use' or not (include_builtin or block.get('name', '').startswith('mcp__')):
                        continue
                    if block['name'].startswith('mcp__'):
                        _, server, tool = block['name'].split('__', 2)
                    else:
                        server, tool = None, block['name']
                    call = dict(sequence=len(calls) + 1, id=block['id'], server=server, tool=tool,
                                arguments=block.get('input', {}), text='', result=None, is_error=False)
                    calls.append(call)
                    pending[block['id']] = call
            if event.get('type') == 'user':
                for block in event.get('message', {}).get('content', []):
                    if block.get('type') == 'tool_result' and block.get('tool_use_id') in pending:
                        call = pending.pop(block['tool_use_id'])
                        call.update(text=text_content(block.get('content')), result=block.get('content'),
                                    is_error=bool(block.get('is_error')), completionOrder=number)
    for call in pending.values():
        call['is_error'] = True
    return calls


def recorded_root(folder):
    prompt = Path(folder) / 'prompt.txt'
    if not prompt.is_file():
        return None
    match = re.search(r'local checkout at this absolute path:\s*\n([^\r\n]+)',
                      prompt.read_text(encoding='utf-8'))
    return match.group(1).strip() if match else None


def builtin_session(folder):
    path = Path(folder) / 'summary.json'
    return path.is_file() and json.loads(path.read_text(encoding='utf-8')).get('kind') == 'builtin'


def first_request_context(path):
    with Path(path).open(encoding='utf-8') as source:
        for line in source:
            try:
                event = json.loads(line)
            except ValueError:
                continue
            if event.get('type') == 'assistant':
                usage = event.get('message', {}).get('usage')
                if usage is None:
                    return None
                return sum(usage.get(key, 0) for key in
                           ('input_tokens', 'cache_read_input_tokens', 'cache_creation_input_tokens'))
    return None
