"""Exercise the generator against isolated locks and patch artifacts."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


class GrammarModuleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.folder = self.root / 'Packaging/Linux/flatpak'
        self.folder.mkdir(parents=True)
        shutil.copyfile(Path(__file__).with_name('generate-grammar-modules.py'), self.folder / 'generate-grammar-modules.py')
        self.lock_path = self.root / 'Infrastructure/Grammars/vendored/vendored-grammars.lock.json'
        self.lock_path.parent.mkdir(parents=True)
        self.lock = {'grammars': [{
            'name': 'tree-sitter-example',
            'source': {'archiveUrl': 'https://example.test/zip/abcdef', 'archiveSha256': 'a' * 64},
            'toolchain': {'compilerFlags': ['-shared', '-O2']},
            'build': {'includeDirectories': ['src', 'space dir'], 'sourceFiles': ['src/parser.c', 'src/scanner.c']},
            'sourcePatches': [{'path': 'src/parser.c', 'oldText': 'old token', 'newText': 'new token'}],
        }]}

    def generate(self):
        self.lock_path.write_text(json.dumps(self.lock), encoding='utf-8')
        return subprocess.run([sys.executable, str(self.folder / 'generate-grammar-modules.py')],
                              capture_output=True, text=True, timeout=10)

    def test_extensionless_zip_and_compiler_arguments_follow_the_lock(self):
        result = self.generate()
        self.assertEqual(result.returncode, 0, result.stderr)
        module = json.loads((self.folder / 'vendored-grammars.json').read_text())['modules'][0]
        self.assertEqual(module['sources'][0], {
            'type': 'archive', 'archive-type': 'zip', 'url': 'https://example.test/zip/abcdef', 'sha256': 'a' * 64})
        import shlex
        self.assertEqual(shlex.split(module['build-commands'][-2]),
                         ['cc', '-fPIC', '-shared', '-O2', '-Isrc', '-Ispace dir', 'src/parser.c', 'src/scanner.c', '-o', 'libtree-sitter-example.so'])
        source = self.root / 'src/parser.c'
        source.parent.mkdir()
        source.write_text('old token', encoding='utf-8')
        command = shlex.split(module['build-commands'][0])
        subprocess.run([sys.executable, *command[1:]], cwd=self.root, check=True, timeout=10)
        self.assertEqual(source.read_text(), 'new token')
        duplicate = subprocess.run([sys.executable, *command[1:]], cwd=self.root, capture_output=True, timeout=10)
        self.assertNotEqual(duplicate.returncode, 0)

    def test_patch_artifact_checksum_is_verified_and_emitted(self):
        patch = self.root / 'fix.patch'
        patch.write_bytes(b'patch contents\r\n')
        checksum = hashlib.sha256(b'patch contents\n').hexdigest()
        self.lock['grammars'][0]['sourcePatchArtifacts'] = [{
            'artifactPath': 'fix.patch', 'sha256': checksum, 'targetPath': 'src/parser.c', 'resultSha256': 'b' * 64}]
        result = self.generate()
        self.assertEqual(result.returncode, 0, result.stderr)
        module = json.loads((self.folder / 'vendored-grammars.json').read_text())['modules'][0]
        self.assertEqual(module['sources'][1]['contents'], 'patch contents\n')
        self.assertIn('patch -p1 < fix.patch', module['build-commands'])
        self.assertIn('b' * 64, '\n'.join(module['build-commands']))
        patch.write_text('modified', encoding='utf-8')
        result = self.generate()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('checksum mismatch', result.stderr)

    def test_unknown_archive_format_is_rejected(self):
        self.lock['grammars'][0]['source']['archiveUrl'] = 'https://example.test/archive.tar'
        result = self.generate()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Unrecognized locked archive format', result.stderr)


if __name__ == '__main__':
    unittest.main()
