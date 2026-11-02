"""Reject altered distribution inputs before they can become installer resources."""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import warnings
import zipfile

BUILD = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BUILD))
try:
    spec = importlib.util.spec_from_file_location('restore_beta_payload', BUILD / 'restore_beta_payload.py')
    restore_beta = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(restore_beta)
finally:
    sys.path.pop(0)


class BetaPayloadTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='mani-beta-restore-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / 'config').mkdir()
        self.archive = self.root / 'published.zip'
        self.output = self.root / 'restored'
        self.payloads = {'Mani.msix': b'signed-package-fixture', 'Mani.cer': b'public-certificate-fixture'}
        self.config = {'archiveUrl': 'https://example.invalid/published.zip', 'files': [
            {'file': name, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)}
            for name, data in self.payloads.items()]}
        self.write_archive(list(self.payloads.items()) + [('unselected.txt', b'not an installer resource')])

    def write_archive(self, members):
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', UserWarning)
            with zipfile.ZipFile(self.archive, 'w') as package:
                for name, data in members:
                    package.writestr(name, data)
        self.config.update(archiveBytes=self.archive.stat().st_size,
                           archiveSha256=hashlib.sha256(self.archive.read_bytes()).hexdigest())
        (self.root / 'config/beta-0.1.json').write_text(json.dumps(self.config), encoding='utf-8')

    def restore(self):
        with patch.object(restore_beta, 'ROOT', self.root), contextlib.redirect_stdout(io.StringIO()):
            restore_beta.restore(self.output, self.archive)

    def test_only_pinned_files_are_extracted_without_modifying_inputs(self):
        original = self.archive.read_bytes()
        self.restore()
        self.assertEqual(self.payloads, {p.name: p.read_bytes() for p in self.output.iterdir()})
        self.assertEqual(original, self.archive.read_bytes())

    def test_existing_directory_is_preserved(self):
        self.output.mkdir()
        marker = self.output / 'existing.txt'
        marker.write_text('keep', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'new Beta input directory'):
            self.restore()
        self.assertEqual('keep', marker.read_text(encoding='utf-8'))

    def test_altered_archive_is_rejected_before_extraction(self):
        self.archive.write_bytes(self.archive.read_bytes() + b'tampered')
        with self.assertRaisesRegex(ValueError, 'archive integrity mismatch'):
            self.restore()
        self.assertFalse(any(self.output.iterdir()))

    def test_inner_file_must_also_match_its_pin(self):
        self.write_archive([('Mani.msix', b'altered'), ('Mani.cer', self.payloads['Mani.cer'])])
        with self.assertRaisesRegex(ValueError, 'input integrity mismatch'):
            self.restore()
        self.assertFalse((self.output / 'Mani.cer').exists())

    def test_duplicate_members_are_rejected_before_extraction(self):
        self.write_archive(list(self.payloads.items()) + [('Mani.msix', b'duplicate')])
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            self.restore()
        self.assertFalse(any(self.output.iterdir()))

    def test_traversal_is_rejected_even_for_unselected_members(self):
        self.write_archive(list(self.payloads.items()) + [('../escaped.txt', b'bad')])
        with self.assertRaises(ValueError):
            self.restore()
        self.assertFalse((self.root / 'escaped.txt').exists())
        self.assertFalse(any(self.output.iterdir()))

    def test_missing_pinned_member_fails(self):
        self.write_archive([('unselected.txt', b'no package')])
        with self.assertRaises(KeyError):
            self.restore()
        self.assertFalse(any(self.output.iterdir()))


if __name__ == '__main__':
    unittest.main()
