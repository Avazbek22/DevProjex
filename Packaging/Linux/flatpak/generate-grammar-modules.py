"""Derive native SDK compiler modules from the shared grammar source lock."""
import json
import hashlib
from pathlib import Path
import shlex

folder = Path(__file__).resolve().parent
root = folder.parents[2]
lock = json.loads((root / 'Infrastructure/Grammars/vendored/vendored-grammars.lock.json').read_text())
modules = []
for grammar in lock['grammars']:
    name = grammar['name']
    output = f'lib{name}.so'
    build = grammar['build']
    flags = ['cc', '-fPIC', *grammar['toolchain']['compilerFlags']]
    flags += [f'-I{directory}' for directory in build.get('includeDirectories', ['src'])]
    flags += build['sourceFiles'] + ['-o', output]
    archive_url = grammar['source']['archiveUrl']
    if '/zip/' not in archive_url:
        raise ValueError(f'Unrecognized locked archive format: {archive_url}')
    sources = [{'type': 'archive', 'archive-type': 'zip', 'url': archive_url,
                'sha256': grammar['source']['archiveSha256']}]
    commands = []
    for patch in grammar.get('sourcePatches', []):
        program = ('from pathlib import Path; '
                   f'p=Path({patch["path"]!r}); text=p.read_text(); old={patch["oldText"]!r}; '
                   f'assert text.count(old)==1; p.write_text(text.replace(old, {patch["newText"]!r}))')
        commands.append('python3 -c ' + shlex.quote(program))
    for patch in grammar.get('sourcePatchArtifacts', []):
        artifact = root / patch['artifactPath']
        content = artifact.read_bytes().replace(b'\r\n', b'\n')
        if hashlib.sha256(content).hexdigest() != patch['sha256']:
            raise ValueError(f'Locked source patch checksum mismatch: {artifact}')
        sources.append({'type': 'inline', 'dest-filename': artifact.name, 'contents': content.decode('utf-8')})
        commands.append('patch -p1 < ' + shlex.quote(artifact.name))
        program = ('from pathlib import Path; import hashlib; '
                   f'assert hashlib.sha256(Path({patch["targetPath"]!r}).read_bytes()).hexdigest()=={patch["resultSha256"]!r}')
        commands.append('python3 -c ' + shlex.quote(program))
    commands += [shlex.join(flags), f'install -Dm644 {output} /app/lib/devprojex/grammars/{output}']
    modules.append({
        'name': name,
        'buildsystem': 'simple',
        'build-commands': commands,
        'sources': sources,
    })
(folder / 'vendored-grammars.json').write_text(json.dumps({
    'name': 'vendored-grammars', 'buildsystem': 'simple', 'build-commands': [], 'modules': modules,
}, indent=2) + '\n')
