"""Verify every archived source file before building the experimental engines."""
from pathlib import Path
import hashlib
import json
import zipfile

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / 'artifacts/m5/sup-engine-integration'
AB = ROOT / 'artifacts/m5/sup-engine-ab'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def verify(archive, source, expected_archive, overrides):
    assert sha(archive.read_bytes()) == expected_archive, archive
    count = 0
    with zipfile.ZipFile(archive) as zipped:
        for item in zipped.infolist():
            if item.is_dir():
                continue
            name = item.filename.split('/', 1)[1]
            expected = overrides.get(name, sha(zipped.read(item)))
            assert sha((source / name).read_bytes()) == expected, source / name
            count += 1
    return dict(archive=str(archive.relative_to(ROOT)), archiveSha256=expected_archive,
        source=str(source.relative_to(ROOT)), filesVerified=count,
        permittedDifferences=sorted(overrides))


def main():
    patch = json.loads((AB / 'patch-identity.json').read_text())
    result = []
    for variant in ('baseline', 'patched'):
        overrides = {x['file']: x['patchedSha256'] for x in patch['files']} if variant == 'patched' else {}
        result.append(verify(AB / 'downloads/ffmpeg.zip', AB / ('src/ffmpeg-baseline' if variant == 'baseline' else 'src/ffmpeg'),
            'fcfbb4eb4f39fe668f8c421c7faec154e8a7651dc67ce96f0e00c128d38ec0d2', overrides))
    result.append(verify(EXP / 'downloads/mpv.zip', EXP / 'src/mpv',
        '980c603587121256800a5851f3f13af8a57a4e8cdd906a03413b5ef189fd3433',
        {'MPV_VERSION': sha(b'0.41.0-1205-g01e2533f3-mani-experiment\n')}))
    (EXP / 'full-source-verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
