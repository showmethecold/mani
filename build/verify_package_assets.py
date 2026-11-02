"""Verify the actual published/MSIX branding files, not just their PRI declarations."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

from restore_libmpv import require, validate_members

ROOT = Path(__file__).resolve().parents[1]


def verify(read, inventory):
    expected = {'Assets/' + item['name']: item['sha256'] for item in inventory['pngs']}
    expected['Assets/' + inventory['ico']['file']] = inventory['ico']['sha256']
    validate_members(expected)
    manifest = ET.fromstring(read('AppxManifest.xml'))
    logo = manifest.find('./{*}Properties/{*}Logo')
    visual = manifest.find('./{*}Applications/{*}Application/{*}VisualElements')
    require(logo is not None and visual is not None, 'Missing app branding declarations')
    tile = visual.find('./{*}DefaultTile')
    splash = visual.find('./{*}SplashScreen')
    require(tile is not None and splash is not None, 'Missing tile or splash branding declarations')
    paths = [logo.text, visual.get('Square44x44Logo'), visual.get('Square150x150Logo'),
             tile.get('Wide310x150Logo'), splash.get('Image')]
    for path in paths:
        require(bool(path), 'Missing branding resource path')
        validate_members([path])
        require(path.replace('\\', '/') in expected, 'Unexpected branding resource: ' + path)
    for name, digest in expected.items():
        try:
            data = read(name)
        except (FileNotFoundError, KeyError):
            raise ValueError('Missing published branding asset: ' + name) from None
        require(hashlib.sha256(data).hexdigest() == digest, 'Published branding asset differs: ' + name)
    return {'passed': True, 'manifestLogos': len(paths), 'pngs': len(inventory['pngs']), 'ico': inventory['ico']['file']}


def verify_layout(layout, inventory):
    return verify(lambda name: (layout / name).read_bytes(), inventory)


def verify_package(package, inventory):
    with zipfile.ZipFile(package) as archive:
        validate_members(archive.namelist())
        require(len(archive.namelist()) == len(set(archive.namelist())), 'Duplicate MSIX entries')
        return verify(archive.read, inventory)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument('--layout', type=Path)
    target.add_argument('--package', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    inventory = json.loads((ROOT / 'assets/branding/windows-assets.json').read_text(encoding='utf-8'))
    result = verify_layout(args.layout, inventory) if args.layout else verify_package(args.package, inventory)
    if args.report:
        args.report.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result))
