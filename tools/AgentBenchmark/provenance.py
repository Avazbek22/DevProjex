"""Check the corpus behind successful MCP calls without altering recorded sessions."""
import argparse
import json
import posixpath
import re
from pathlib import Path
from urllib.parse import urlparse

import benchlib
from transcripts import recorded_calls, recorded_root


def relative_to_root(value, root):
    normalized = benchlib._normalize_answer_path(str(value), root)
    return normalized if not benchlib._absolute_answer_path(normalized) and normalized != '..' \
        and not normalized.startswith('../') else None


def payload(call):
    try:
        return json.loads(call['text'])
    except (ValueError, TypeError):
        return {}


def successful(call):
    data = payload(call)
    return not call['is_error'] and not (isinstance(data, dict) and data.get('errorMessage')) \
        and not call['text'].startswith('DPX-MCP-')


def check_provenance(calls, root, server, manifest=None):
    violations, outputs = [], set()
    manifest = manifest or {}
    name = posixpath.basename(root.replace('\\', '/').rstrip('/'))
    for call in calls:
        if not successful(call):
            continue
        args, tool = call['arguments'], call['tool']
        reason = None
        if call.get('server') != server:
            reason = 'foreign-server'
        project = args.get('project')
        index_alias = isinstance(project, str) and re.fullmatch(r'#0*[01]', project)
        if project is not None and project != name and not index_alias and relative_to_root(project, root) != '.':
            reason = 'foreign-project'
        if tool in ('get_tree', 'get_diff'):
            reported = re.search(r'(?m)^Root: ([^\r\n]+)', call['text'])
            if reported and relative_to_root(reported[1], root) != '.':
                reason = 'foreign-root'
        if tool == 'list_projects':
            body = re.sub(r'</?untrusted-data-[^>]+>', '', call['text'])
            start = body.find('{')
            try:
                projects = json.loads(body[start:].strip()).get('projects', [])
            except (ValueError, AttributeError):
                projects = []
            if any(relative_to_root(entry.get('path', ''), root) != '.' for entry in projects):
                reason = 'foreign-root'
        if tool == 'pack_remote_repository':
            remote = str(args.get('remote', ''))
            parsed = urlparse(remote if '://' in remote else 'https://github.com/' + remote)
            expected = manifest.get('url', '').removesuffix('.git').rstrip('/')
            repository = f'{parsed.scheme}://{parsed.netloc}{parsed.path}'.removesuffix('.git').rstrip('/')
            reference = args.get('ref') or args.get('branch') or parsed.fragment
            if not reference or not re.fullmatch(r'[0-9a-fA-F]{40}', reference):
                reason = 'unpinned-remote'
            elif repository != expected or reference.lower() != manifest.get('head', '').lower():
                reason = 'foreign-remote'
        if tool in ('pack_codebase', 'attach_packed_output'):
            directory = args.get('directory', '.') if tool == 'pack_codebase' else args.get('path', '')
            if relative_to_root(directory, root) is None:
                reason = 'outside-corpus'
        if tool in ('read_repomix_output', 'grep_repomix_output') and args.get('outputId') not in outputs:
            reason = 'unverified-output'
        data = payload(call)
        if tool in ('pack_codebase', 'pack_remote_repository', 'attach_packed_output') and not reason:
            nested = data.get('result', {}) if isinstance(data, dict) else {}
            if isinstance(nested, str):
                try:
                    nested = json.loads(nested)
                except ValueError:
                    nested = {}
            identifier = data.get('outputId') or nested.get('outputId') if isinstance(data, dict) else None
            if identifier:
                outputs.add(identifier)
        if reason:
            violations.append({'call': call['sequence'], 'tool': tool, 'reason': reason})
    return {'status': 'violated' if violations else 'passed', 'violations': violations}


def inspect_session(folder, root=None, server=None, manifest=None):
    folder = Path(folder)
    if server is None:
        summary = json.loads((folder / 'summary.json').read_text(encoding='utf-8'))
        server = summary['server']
    root = root or recorded_root(folder)
    if not root:
        return {'status': 'unverified', 'violations': [{'reason': 'missing-corpus-root'}]}
    try:
        return check_provenance(recorded_calls(folder / 'stream.jsonl'), root, server, manifest)
    except (OSError, ValueError, KeyError):
        return {'status': 'unverified', 'violations': [{'reason': 'invalid-or-missing-transcript'}]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--session', required=True)
    parser.add_argument('--root')
    parser.add_argument('--manifest')
    parser.add_argument('--server')
    args = parser.parse_args()
    manifest = json.loads(Path(args.manifest).read_text(encoding='utf-8')) if args.manifest else None
    print(json.dumps(inspect_session(args.session, args.root, server=args.server, manifest=manifest)))


if __name__ == '__main__':
    main()
