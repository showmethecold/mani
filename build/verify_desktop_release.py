"""Verify desktop release provenance before staging downloads or publishing links."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import zipfile

from restore_libmpv import require, sha256, validate_members

ROOT = Path(__file__).resolve().parents[1]
PIN = json.loads((ROOT / 'config/beta-desktop.json').read_text(encoding='utf-8'))
FILES = {PIN['installer'], 'setup-info.json', 'installed-files.json', 'Mani-Setup-source.zip',
         'setup-source-info.json', 'setup-source-files.json', 'INSTALL.txt', 'INSTALL.zh-CN.txt', 'SETUP-SHA256SUMS.txt'}


def verify_info(info, public=True):
    require(info.get('format') == 'desktop' and info.get('version') == PIN['version'] and
            info.get('installer') == PIN['installer'], 'Unexpected desktop installer identity')
    require(info.get('isolated') is False and info.get('configuration') == 'Release', 'Expected an ordinary Release build')
    require(re.fullmatch('[0-9a-f]{64}', info.get('sha256', '')) is not None, 'Missing installer digest')
    native = json.loads((ROOT / 'config/mpv-runtime.json').read_text(encoding='utf-8'))
    require(info.get('nativeSha256') == native['sha256'], 'Native baseline differs')
    if public:
        require(info.get('signed') is True and info.get('timestamped') is True and
                info.get('signatureStatus') in ('Valid', 'NotTrusted', 'UnknownError') and
                info.get('signerThumbprint') == PIN['signerThumbprint'], 'Expected the timestamped Beta signer')
        require(info.get('sourceDirty') is False and re.fullmatch('[0-9a-f]{40}', info.get('sourceCommit', '')) is not None,
                'Expected a clean source commit')
    return info


def verify(folder, commit=None):
    folder = Path(folder)
    read = lambda name: json.loads((folder / name).read_text(encoding='utf-8-sig'))
    info = verify_info(read('setup-info.json'))
    require(commit is None or info['sourceCommit'] == commit, 'Installer source differs from the requested commit')
    require(sha256(folder / info['installer']) == info['sha256'], 'Installer digest mismatch')
    source = read('setup-source-info.json')
    require(source['commit'] == info['sourceCommit'] and source['dirty'] is False and source['archive'] == 'Mani-Setup-source.zip', 'Corresponding source mismatch')
    require(sha256(folder / source['archive']) == source['archiveSha256'] and
            sha256(folder / 'setup-source-files.json') == source['inventorySha256'], 'Source archive or inventory changed')
    listing = read('setup-source-files.json')
    require(len(listing) == source['fileCount'], 'Source file count differs')
    with zipfile.ZipFile(folder / source['archive']) as archive:
        names = archive.namelist()
        validate_members(names)
        require(len(names) == len(set(names)) and set(names) == {x['file'] for x in listing}, 'Source archive members differ')
        for entry in listing:
            data = archive.read(entry['file'])
            require(len(data) == entry['bytes'] and hashlib.sha256(data).hexdigest() == entry['sha256'], 'Source file digest differs')
    checksums = {}
    for line in (folder / 'SETUP-SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
        digest, name = line.split('  ', 1)
        require(name in FILES and name not in checksums and sha256(folder / name) == digest, 'Release checksum mismatch')
        checksums[name] = digest
    require(set(checksums) == FILES - {'SETUP-SHA256SUMS.txt'}, 'Incomplete release checksum coverage')
    installed = read('installed-files.json')
    require(len(installed) == info['files'], 'Installed file inventory count differs')
    validate_members([x['path'].replace('\\', '/') for x in installed])
    require(not any('Mani.IsolatedFileActivationTests' in x['path'] or 'baseline-media.json' in x['path'] for x in installed), 'Test data in release inventory')
    return {'passed': True, 'sourceCommit': info['sourceCommit'], 'version': info['version'], 'sha256': info['sha256'], 'releaseFiles': len(FILES)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--commit')
    args = parser.parse_args()
    print(json.dumps(verify(args.directory, args.commit)))
