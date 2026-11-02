"""Restore the exact published Beta inputs for a new bootstrapper; never re-sign the MSIX."""
import argparse
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile

from restore_libmpv import require, sha256, validate_members

ROOT = Path(__file__).resolve().parents[1]

def restore(output, archive=None):
    config = json.loads((ROOT / 'config/beta-0.1.json').read_text(encoding='utf-8'))
    require(not output.exists(), 'Use a new Beta input directory')
    output.mkdir(parents=True)
    if archive is None:
        archive = output / 'published-beta.zip'
        request = urllib.request.Request(config['archiveUrl'], headers={'User-Agent':'Mani-Setup-Build'})
        with urllib.request.urlopen(request, timeout=60) as response, archive.open('xb') as target:
            shutil.copyfileobj(response, target)
    require(archive.stat().st_size == config['archiveBytes'] and sha256(archive) == config['archiveSha256'], 'Published Beta archive integrity mismatch')
    with zipfile.ZipFile(archive) as package:
        validate_members(package.namelist())
        require(len(package.namelist()) == len(set(package.namelist())), 'Duplicate Beta archive members')
        for entry in config['files']:
            require(Path(entry['file']).name == entry['file'], 'Unexpected Beta input name')
            target = output / entry['file']
            with package.open(entry['file']) as source, target.open('xb') as stream:
                shutil.copyfileobj(source, stream)
            require(target.stat().st_size == entry['bytes'] and sha256(target) == entry['sha256'], 'Beta input integrity mismatch: ' + entry['file'])
    print('Restored the pinned MSIX and public certificate. No installation or trust changes.')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--archive', type=Path)
    args = parser.parse_args()
    restore(args.output.resolve(), args.archive.resolve() if args.archive else None)
