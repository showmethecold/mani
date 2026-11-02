"""Package a verified SUP runtime; emit candidate lock/props without selecting it.

Reuses the experimental PE-closure manifest and per-package source baseline.
The accepted binaries are copied verbatim, including their version metadata.
"""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

from restore_libmpv import require, sha256, validate_members, verify_files

ROOT = Path(__file__).resolve().parents[1]
BASELINE = 'mani-sup-msys2-ucrt64-20261101-r2'


def prepare(runtime, dependency_licenses, engine_licenses, output):
    require(not output.exists(), 'Use a new bundle output directory')
    manifest = json.loads((runtime / 'runtime-manifest.json').read_text(encoding='utf-8'))
    baseline = ROOT / 'build/experimental-sup/dependencies.lock.json'
    require(sha256(baseline) == manifest['dependencyLockSha256'], 'Build dependency baseline differs')
    require(manifest['variant'] == 'patched', 'Only the verified patched variant can be packaged')
    records = [(entry['file'], entry) for entry in manifest['files']]
    validate_members([name for name, _ in records])
    verify_files(runtime, records)
    source = {name: runtime / name for name, _ in records}
    for label, directory in [('dependencies', dependency_licenses), ('engine', engine_licenses)]:
        for path in sorted(directory.rglob('*')):
            if path.is_file():
                source[f'licenses/native/{label}/{path.relative_to(directory).as_posix()}'] = path
    require(any(name.startswith('licenses/native/dependencies/') for name in source), 'Dependency licenses missing')
    require(any(name.startswith('licenses/native/engine/') for name in source), 'Engine licenses missing')
    source['licenses/native/dependencies.lock.json'] = baseline
    source['licenses/native/ffmpeg-sup-keyframes-flush.patch'] = ROOT / 'build/experimental-sup/ffmpeg-sup-keyframes-flush.patch'
    source['licenses/native/Native-NOTICE.txt'] = ROOT / 'licenses/Native-NOTICE.txt'
    files = [dict(file=name, bytes=path.stat().st_size, sha256=sha256(path)) for name, path in sorted(source.items())]
    output.mkdir(parents=True)
    archive = output / f'{BASELINE}.zip'
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as package:
        for name, path in sorted(source.items()):
            info = zipfile.ZipInfo(name, (2026, 11, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            package.writestr(info, path.read_bytes())
    prefix = f'native/mpv/{BASELINE}'
    pin = json.loads((runtime / 'mpv-runtime.json').read_text(encoding='utf-8'))
    pin.pop('experimental', None)
    pin.update(baseline=BASELINE, files=files)
    lock = dict(schemaVersion=2, baseline=BASELINE, architecture='x86_64',
                dll=f'{prefix}/libmpv-2.dll',
                source=manifest['sourceIdentity'], dependencyLockSha256=sha256(baseline),
                buildRecipeSha256=manifest['buildRecipeSha256'],
                archives=dict(dev=dict(name=archive.name, url=None, bytes=archive.stat().st_size, sha256=sha256(archive))),
                files=[dict(path=f'{prefix}/{entry["file"]}', bytes=entry['bytes'], sha256=entry['sha256']) for entry in files])
    project = ET.Element('Project')
    props = ET.SubElement(project, 'PropertyGroup')
    ET.SubElement(props, 'ManiMpvDll').text = f'$(MSBuildThisFileDirectory)..\\{prefix.replace("/", chr(92))}\\libmpv-2.dll'
    ET.SubElement(props, 'ManiMpvSha256').text = pin['sha256'].upper()
    group = ET.SubElement(project, 'ItemGroup')
    for entry in files:
        item = ET.SubElement(group, 'ManiMpvFile', Include=f'$(MSBuildThisFileDirectory)..\\{prefix}/{entry["file"]}')
        ET.SubElement(item, 'RelativePath').text = entry['file']
        ET.SubElement(item, 'Link').text = entry['file'].replace('/', '\\')
        ET.SubElement(item, 'TargetPath').text = entry['file'].replace('/', '\\')
        ET.SubElement(item, 'Sha256').text = entry['sha256'].upper()
    ET.indent(project, space='  ')
    ET.ElementTree(project).write(output / 'libmpv.props', encoding='utf-8', xml_declaration=False)
    for name, content in [('libmpv.lock.json', lock), ('mpv-runtime.json', pin)]:
        (output / name).write_text(json.dumps(content, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(archive=str(archive), sha256=sha256(archive), bytes=archive.stat().st_size, files=len(files))))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('runtime', 'dependency-licenses', 'engine-licenses', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    prepare(args.runtime, args.dependency_licenses, args.engine_licenses, args.output)
