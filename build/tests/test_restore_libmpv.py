"""Integrity and failure recovery checks for the locked native restore step."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import urllib.error

spec = importlib.util.spec_from_file_location('restore_libmpv', Path(__file__).resolve().parents[1] / 'restore_libmpv.py')
restore_libmpv = importlib.util.module_from_spec(spec)
spec.loader.exec_module(restore_libmpv)


class NativeRestoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='mani-restore-tests-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / 'config').mkdir()
        self.payload = b'locked synthetic native payload'
        self.archive = self.root / 'fixture.7z'
        self.archive.write_bytes(b'synthetic archive')
        digest = hashlib.sha256(self.payload).hexdigest()
        self.url = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/fixed/fixture.7z'
        self.lock = {
            'dll': 'native/fixed/dev/libmpv-2.dll',
            'files': [{'path': 'native/fixed/dev/libmpv-2.dll', 'bytes': len(self.payload), 'sha256': digest}],
            'archives': {'dev': {'name': 'fixture.7z', 'url': self.url, 'bytes': self.archive.stat().st_size,
                                 'sha256': restore_libmpv.sha256(self.archive)}}
        }
        (self.root / 'config/libmpv.lock.json').write_text(json.dumps(self.lock), encoding='utf-8')
        (self.root / 'config/mpv-runtime.json').write_text(json.dumps({'sha256': digest}), encoding='utf-8')
        (self.root / 'config/libmpv.props').write_text(
            '<Project><PropertyGroup><ManiMpvDll>$(MSBuildThisFileDirectory)..\\native\\fixed\\dev\\libmpv-2.dll</ManiMpvDll>'
            f'<ManiMpvSha256>{digest}</ManiMpvSha256></PropertyGroup></Project>', encoding='utf-8')
        self.dll = self.root / self.lock['dll']

    def extract_fixture(self, archive, destination):
        (destination / 'libmpv-2.dll').write_bytes(self.payload)

    def test_restore_is_idempotent_and_does_not_rewrite_locks(self):
        before = {path.name: path.read_bytes() for path in (self.root / 'config').iterdir()}
        with patch.object(restore_libmpv, 'extract', side_effect=self.extract_fixture) as extract:
            restore_libmpv.restore(self.root, self.archive)
            restore_libmpv.restore(self.root, self.archive)
            self.assertEqual(1, extract.call_count)
        self.assertEqual(self.payload, self.dll.read_bytes())
        self.assertEqual(before, {path.name: path.read_bytes() for path in (self.root / 'config').iterdir()})

    def test_corrupt_existing_payload_is_not_overwritten(self):
        self.dll.parent.mkdir(parents=True)
        self.dll.write_bytes(b'local unexpected content')
        with self.assertRaisesRegex(ValueError, 'refusing to replace'):
            restore_libmpv.restore(self.root, self.archive)
        self.assertEqual(b'local unexpected content', self.dll.read_bytes())

    def test_invalid_archive_never_reaches_extraction(self):
        self.archive.write_bytes(b'tampered')
        with patch.object(restore_libmpv, 'extract') as extract:
            with self.assertRaisesRegex(ValueError, 'archive hash or size mismatch'):
                restore_libmpv.restore(self.root, self.archive)
            extract.assert_not_called()
        self.assertFalse(self.dll.parent.exists())

    def test_extraction_failure_leaves_no_partial_install(self):
        def corrupt_extract(archive, destination):
            (destination / 'libmpv-2.dll').write_bytes(b'wrong extracted payload')
        with patch.object(restore_libmpv, 'extract', side_effect=corrupt_extract):
            with self.assertRaisesRegex(ValueError, 'Locked native file differs'):
                restore_libmpv.restore(self.root, self.archive)
        self.assertFalse(self.dll.parent.exists())
        self.assertEqual([], list(self.dll.parent.parent.iterdir()))

    def test_download_uses_locked_url_and_checks_content(self):
        with patch.object(restore_libmpv.urllib.request, 'urlopen', return_value=io.BytesIO(self.archive.read_bytes())) as download:
            with patch.object(restore_libmpv, 'extract', side_effect=self.extract_fixture):
                restore_libmpv.restore(self.root)
        self.assertEqual(self.url, download.call_args.args[0].full_url)
        self.assertEqual(self.payload, self.dll.read_bytes())

    def test_bad_download_is_removed_without_installing(self):
        with patch.object(restore_libmpv.urllib.request, 'urlopen', return_value=io.BytesIO(b'tampered download')):
            with self.assertRaisesRegex(ValueError, 'Downloaded native archive hash or size mismatch'):
                restore_libmpv.restore(self.root)
        self.assertEqual([], list((self.root / 'artifacts/downloads').iterdir()))
        self.assertFalse(self.dll.parent.exists())

    def test_disagreeing_hash_configurations_stop_restore(self):
        (self.root / 'config/mpv-runtime.json').write_text(json.dumps({'sha256': '0' * 64}), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'hash configurations disagree'):
            restore_libmpv.restore(self.root, self.archive)
        self.assertFalse(self.dll.parent.exists())

    def test_archive_paths_cannot_escape_destination(self):
        for name in ['../escape', 'nested/../../escape', '/absolute', 'C:\\escape', 'nested/file:stream', '\\\\server\\share']:
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, 'Unsafe archive member'):
                restore_libmpv.validate_members([name])
        restore_libmpv.validate_members(['include/mpv/client.h', 'libmpv-2.dll'])

    def enable_bundle(self):
        self.lock['schemaVersion'] = 2
        self.lock['archives']['dev']['url'] = None
        self.lock['files'].append(dict(path='native/fixed/dev/avcodec.dll', bytes=3,
                                       sha256=hashlib.sha256(b'abc').hexdigest()))
        (self.root / 'config/libmpv.lock.json').write_text(json.dumps(self.lock))
        runtime = dict(sha256=self.lock['files'][0]['sha256'], files=[
            dict(file=Path(entry['path']).name, bytes=entry['bytes'], sha256=entry['sha256']) for entry in self.lock['files']])
        (self.root / 'config/mpv-runtime.json').write_text(json.dumps(runtime))
        props_path = self.root / 'config/libmpv.props'
        items = ''.join(f'<ManiMpvFile Include="$(MSBuildThisFileDirectory)../{entry["path"]}">'
                        f'<RelativePath>{Path(entry["path"]).name}</RelativePath><Sha256>{entry["sha256"]}</Sha256></ManiMpvFile>'
                        for entry in self.lock['files'])
        props_path.write_text(props_path.read_text().replace('</Project>', f'<ItemGroup>{items}</ItemGroup></Project>'))

    def extract_bundle(self, archive, destination):
        self.extract_fixture(archive, destination)
        (destination / 'avcodec.dll').write_bytes(b'abc')

    def test_complete_bundle_restores_offline_and_detects_corrupt_dependency(self):
        self.enable_bundle()
        with patch.object(restore_libmpv, 'extract', side_effect=self.extract_bundle):
            restore_libmpv.restore(self.root, self.archive)
        (self.dll.parent / 'avcodec.dll').write_bytes(b'bad')
        with self.assertRaisesRegex(ValueError, 'Locked native file differs: avcodec.dll'):
            restore_libmpv.restore(self.root, self.archive)
        self.assertEqual(b'bad', (self.dll.parent / 'avcodec.dll').read_bytes())

    def test_partial_bundle_is_not_installed(self):
        self.enable_bundle()
        with patch.object(restore_libmpv, 'extract', side_effect=self.extract_fixture):
            with self.assertRaisesRegex(ValueError, 'Missing locked native file: avcodec.dll'):
                restore_libmpv.restore(self.root, self.archive)
        self.assertFalse(self.dll.parent.exists())

    def test_unlisted_dependency_is_rejected(self):
        self.enable_bundle()
        def extra(archive, destination):
            self.extract_bundle(archive, destination)
            (destination / 'injected.dll').write_bytes(b'unlisted')
        with patch.object(restore_libmpv, 'extract', side_effect=extra):
            with self.assertRaisesRegex(ValueError, 'Unexpected files'):
                restore_libmpv.restore(self.root, self.archive)
        self.assertFalse(self.dll.parent.exists())

    def test_runtime_inventory_disagreement_is_rejected_before_extract(self):
        self.enable_bundle()
        path = self.root / 'config/mpv-runtime.json'
        runtime = json.loads(path.read_text())
        runtime['files'][1]['sha256'] = '0' * 64
        path.write_text(json.dumps(runtime))
        with patch.object(restore_libmpv, 'extract') as extract:
            with self.assertRaisesRegex(ValueError, 'runtime inventories disagree'):
                restore_libmpv.restore(self.root, self.archive)
            extract.assert_not_called()

    def test_unpublished_bundle_requires_explicit_location(self):
        self.enable_bundle()
        with patch.object(restore_libmpv.urllib.request, 'urlopen') as download:
            with self.assertRaisesRegex(ValueError, 'has not been published'):
                restore_libmpv.restore(self.root)
            download.assert_not_called()

    def test_explicit_mirror_cannot_bypass_pinned_hash(self):
        self.enable_bundle()
        with patch.object(restore_libmpv.urllib.request, 'urlopen', return_value=io.BytesIO(b'wrong')):
            with self.assertRaisesRegex(ValueError, 'Downloaded native archive hash or size mismatch'):
                restore_libmpv.restore(self.root, archive_url='https://example.invalid/locked.zip')
        self.assertFalse(self.dll.parent.exists())

    def release_asset(self):
        asset = dict(self.lock['archives']['dev'], githubRelease={'repository': 'showmethecold/mani', 'tag': 'fixed-native'})
        metadata = dict(assets=[dict(name=asset['name'], id=42, size=asset['bytes'], state='uploaded',
                                     digest='sha256:' + asset['sha256'])])
        return asset, metadata

    def test_private_asset_redirect_never_forwards_token(self):
        asset, metadata = self.release_asset()
        opener = Mock()
        opener.open.side_effect = [io.BytesIO(json.dumps(metadata).encode()),
            urllib.error.HTTPError('https://api.github.com/asset', 302, 'Found',
                                   {'Location': 'https://storage.example.invalid/locked'}, None)]
        with patch.object(restore_libmpv.urllib.request, 'build_opener', return_value=opener), \
             patch.object(restore_libmpv.urllib.request, 'urlopen', return_value=io.BytesIO(b'archive')) as storage:
            with restore_libmpv.github_download(asset, 'test-token') as response:
                self.assertEqual(b'archive', response.read())
        self.assertEqual('Bearer test-token', opener.open.call_args_list[0].args[0].get_header('Authorization'))
        self.assertEqual('application/octet-stream', opener.open.call_args_list[1].args[0].get_header('Accept'))
        self.assertIsNone(storage.call_args.args[0].get_header('Authorization'))

    def test_release_metadata_must_match_locked_digest(self):
        asset, metadata = self.release_asset()
        metadata['assets'][0]['digest'] = 'sha256:' + '0' * 64
        opener = Mock()
        opener.open.return_value = io.BytesIO(json.dumps(metadata).encode())
        with patch.object(restore_libmpv.urllib.request, 'build_opener', return_value=opener):
            with self.assertRaisesRegex(ValueError, 'asset digest differs'):
                restore_libmpv.github_download(asset, 'test-token')
        self.assertEqual(1, opener.open.call_count)

    def test_release_redirect_cannot_downgrade_to_http(self):
        asset, metadata = self.release_asset()
        opener = Mock()
        opener.open.side_effect = [io.BytesIO(json.dumps(metadata).encode()),
            urllib.error.HTTPError('https://api.github.com/asset', 302, 'Found',
                                   {'Location': 'http://storage.example.invalid/locked'}, None)]
        with patch.object(restore_libmpv.urllib.request, 'build_opener', return_value=opener), \
             patch.object(restore_libmpv.urllib.request, 'urlopen') as storage:
            with self.assertRaisesRegex(ValueError, 'redirect requires HTTPS'):
                restore_libmpv.github_download(asset, 'test-token')
        storage.assert_not_called()

    def test_direct_asset_response_is_supported(self):
        asset, metadata = self.release_asset()
        opener = Mock()
        opener.open.side_effect = [io.BytesIO(json.dumps(metadata).encode()), io.BytesIO(b'direct archive')]
        with patch.object(restore_libmpv.urllib.request, 'build_opener', return_value=opener):
            with restore_libmpv.github_download(asset, None) as response:
                self.assertEqual(b'direct archive', response.read())
        self.assertIsNone(opener.open.call_args_list[0].args[0].get_header('Authorization'))


if __name__ == '__main__':
    unittest.main()
