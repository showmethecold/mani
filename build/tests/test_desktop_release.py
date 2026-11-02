"""Prevent test builds, old native runtimes or unverified signatures reaching Pages."""
import importlib.util
import json
from pathlib import Path
import sys
import unittest

BUILD = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BUILD))
try:
    spec = importlib.util.spec_from_file_location('verify_desktop_release', BUILD / 'verify_desktop_release.py')
    release = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(release)
finally:
    sys.path.pop(0)


class DesktopReleaseTests(unittest.TestCase):
    def setUp(self):
        self.info = dict(format='desktop', version=release.PIN['version'], installer=release.PIN['installer'],
                         isolated=False, configuration='Release', sha256='a'*64,
                         nativeSha256=json.loads((BUILD.parent/'config/mpv-runtime.json').read_text())['sha256'],
                         signed=True, timestamped=True, signatureStatus='NotTrusted',
                         signerThumbprint=release.PIN['signerThumbprint'], sourceDirty=False, sourceCommit='b'*40)

    def test_beta_private_ca_can_be_untrusted_on_verifying_host(self):
        self.assertEqual(self.info, release.verify_info(self.info))

    def test_rejects_unsigned_isolated_wrong_signer_or_modified_source(self):
        for key, value in [('signed', False), ('isolated', True), ('timestamped', False),
                           ('signatureStatus', 'HashMismatch'), ('signerThumbprint', 'unknown'),
                           ('sourceDirty', True), ('sourceCommit', 'invalid')]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                release.verify_info(dict(self.info, **{key:value}))

    def test_rejects_stale_msix_or_runtime_and_missing_digest(self):
        for key, value in [('format', 'msix'), ('version','0.5.7.0'), ('nativeSha256','0'*64),
                           ('sha256', ''), ('configuration','Debug'), ('installer','other.exe')]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                release.verify_info(dict(self.info, **{key:value}))

    def test_preview_can_use_unsigned_ordinary_build_but_not_isolated_build(self):
        release.verify_info(dict(self.info, signed=False, sourceDirty=True), public=False)
        with self.assertRaises(ValueError): release.verify_info(dict(self.info, isolated=True), public=False)


if __name__ == '__main__': unittest.main()
