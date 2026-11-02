"""Exercise the missing-icon regression against both publish trees and MSIX files."""
import hashlib
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

BUILD = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BUILD))
try:
    spec = importlib.util.spec_from_file_location('verify_package_assets', BUILD / 'verify_package_assets.py')
    assets = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(assets)
finally:
    sys.path.pop(0)


class PackageAssetTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='mani-assets-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        names = ['StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png',
                 'Wide310x150Logo.png', 'SplashScreen.png', 'Square44x44Logo.targetsize-48_altform-unplated.png']
        self.files = {'Assets/' + name: ('icon fixture: ' + name).encode() for name in names}
        self.files['Assets/AppIcon.ico'] = b'window icon fixture'
        self.inventory = {'pngs': [{'name': n, 'sha256': hashlib.sha256(self.files['Assets/' + n]).hexdigest()} for n in names],
                          'ico': {'file': 'AppIcon.ico', 'sha256': hashlib.sha256(self.files['Assets/AppIcon.ico']).hexdigest()}}
        self.files['AppxManifest.xml'] = b'''<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
<Properties><Logo>Assets\\StoreLogo.png</Logo></Properties><Applications><Application><uap:VisualElements Square44x44Logo="Assets\\Square44x44Logo.png" Square150x150Logo="Assets\\Square150x150Logo.png"><uap:DefaultTile Wide310x150Logo="Assets\\Wide310x150Logo.png"/><uap:SplashScreen Image="Assets\\SplashScreen.png"/></uap:VisualElements></Application></Applications></Package>'''

    def package(self):
        path = self.root / 'fixture.msix'
        with zipfile.ZipFile(path, 'w') as archive:
            for name, data in self.files.items():
                archive.writestr(name, data)
        return path

    def test_complete_publish_tree(self):
        for name, data in self.files.items():
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        result = assets.verify_layout(self.root, self.inventory)
        self.assertTrue(result['passed'])
        self.assertEqual(6, result['pngs'])

    def test_complete_msix(self):
        self.assertTrue(assets.verify_package(self.package(), self.inventory)['passed'])

    def test_manifest_and_ico_alone_cannot_pass(self):
        self.files = {n: d for n, d in self.files.items() if not n.endswith('.png')}
        with self.assertRaisesRegex(ValueError, 'Missing published branding asset'):
            assets.verify_package(self.package(), self.inventory)

    def test_missing_taskbar_variant_is_rejected(self):
        del self.files['Assets/Square44x44Logo.targetsize-48_altform-unplated.png']
        with self.assertRaisesRegex(ValueError, 'targetsize-48_altform-unplated'):
            assets.verify_package(self.package(), self.inventory)

    def test_wrong_image_bytes_are_rejected(self):
        self.files['Assets/Square44x44Logo.png'] = b'stale template icon'
        with self.assertRaisesRegex(ValueError, 'branding asset differs'):
            assets.verify_package(self.package(), self.inventory)

    def test_manifest_must_reference_the_expected_assets(self):
        self.files['AppxManifest.xml'] = self.files['AppxManifest.xml'].replace(b'Assets\\StoreLogo.png', b'Assets\\WrongLogo.png')
        with self.assertRaisesRegex(ValueError, 'Unexpected branding resource'):
            assets.verify_package(self.package(), self.inventory)

    def test_unsafe_manifest_path_is_rejected(self):
        self.files['AppxManifest.xml'] = self.files['AppxManifest.xml'].replace(b'Assets\\StoreLogo.png', b'..\\StoreLogo.png')
        with self.assertRaisesRegex(ValueError, 'Unsafe'):
            assets.verify_package(self.package(), self.inventory)
