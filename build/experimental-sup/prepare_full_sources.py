"""Prepare the fixed full-engine inputs using the existing archive verifier."""
import hashlib
import json
from pathlib import Path
import zipfile

import prepare_sources
import verify_full_sources

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / 'artifacts/m5/sup-engine-integration'


def main():
    prepare_sources.main()
    identity = json.loads((ROOT / 'build/experimental-sup/dependencies.lock.json').read_text())['sources']
    archive = EXP / 'downloads/mpv.zip'
    if hashlib.sha256(archive.read_bytes()).hexdigest() != identity['mpvArchiveSha256']:
        raise ValueError('Fixed mpv archive identity differs')
    version = (identity['mpvMetadataOverride']['experimental'] + '\n').encode()
    destination = EXP / 'src/mpv'
    with zipfile.ZipFile(archive) as zipped:
        existed = prepare_sources.prepare(zipped, destination, {'MPV_VERSION': hashlib.sha256(version).hexdigest()})
    if not existed:
        (destination / 'MPV_VERSION').write_bytes(version)
    target = EXP / 'source-identity.json'
    if target.exists() and json.loads(target.read_text()) != identity:
        raise ValueError('Existing source identity differs; preserved')
    if not target.exists():
        target.write_text(json.dumps(identity, indent=2) + '\n', encoding='utf-8')
    verify_full_sources.main()


if __name__ == '__main__':
    main()
