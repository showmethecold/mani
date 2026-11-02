"""Map every collected non-OS runtime file to a pinned package or source build."""
from pathlib import Path
import hashlib
import json
import tarfile

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / 'artifacts/m5/sup-engine-integration'
MSYS = ROOT / 'artifacts/m5/sup-engine-ab/tools/msys64'


def main():
    lock = json.loads((ROOT / 'build/experimental-sup/dependencies.lock.json').read_text())
    owners = {}
    for package in lock['packages']:
        files = MSYS / 'var/lib/pacman/local' / (package['name']+'-'+package['version']) / 'files'
        for line in files.read_text(encoding='utf-8').splitlines():
            if line.startswith('ucrt64/bin/'):
                owners[line.casefold()] = package
    variants = {}
    verified_packages = {}
    for variant in ('baseline', 'patched'):
        manifest = json.loads((EXP / ('runtime-'+variant) / 'runtime-manifest.json').read_text())
        records = []
        for item in manifest['files']:
            record = dict(file=item['file'], sha256=item['sha256'])
            if item['builtFromSource']:
                record['source'] = 'fixed mpv source' if item['file'] in ('libmpv-2.dll','mpv.exe') else 'fixed FFmpeg source'
                record['variant'] = variant
            else:
                relative = (ROOT / item['source']).relative_to(MSYS).as_posix().casefold()
                package = owners[relative]
                record.update(package=package['name'], version=package['version'], packageSha256=package['sha256'])
                if relative not in verified_packages:
                    with tarfile.open(ROOT / package['archive'], 'r:*') as archive:
                        member = next(m for m in archive if m.name.casefold() == relative)
                        verified_packages[relative] = hashlib.sha256(archive.extractfile(member).read()).hexdigest()
                assert verified_packages[relative] == item['sha256'], item['file']
                record['matchesLockedPackageContents'] = True
            records.append(record)
        variants[variant] = records
    (EXP / 'runtime-package-map.json').write_text(json.dumps(variants, indent=2), encoding='utf-8')
    print('Mapped every runtime file in both variants to a locked source/package')


if __name__ == '__main__':
    main()
