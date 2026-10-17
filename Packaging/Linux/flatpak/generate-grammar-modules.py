"""Derive native SDK compiler modules from the shared grammar source lock."""
import json
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
    modules.append({
        'name': name,
        'buildsystem': 'simple',
        'build-commands': [shlex.join(flags), f'install -Dm644 {output} /app/lib/devprojex/grammars/{output}'],
        'sources': [{'type': 'archive', 'url': grammar['source']['archiveUrl'],
                     'sha256': grammar['source']['archiveSha256']}],
    })
(folder / 'vendored-grammars.json').write_text(json.dumps({
    'name': 'vendored-grammars', 'buildsystem': 'simple', 'build-commands': [], 'modules': modules,
}, indent=2) + '\n')
