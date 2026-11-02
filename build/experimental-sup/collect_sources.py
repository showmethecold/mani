"""Acquire matching MSYS2 source packages for the verified runtime package map.

Reads PKGINFO from hash-verified cached binary packages; never executes PKGBUILD.
Source archives are retained with hashes and version metadata for release review.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import re
from pathlib import Path
import shutil
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    packages = {p['name']: p for p in json.loads((ROOT / 'build/experimental-sup/dependencies.lock.json').read_text())['packages']}
    mapping = json.loads((ROOT / 'artifacts/m5/sup-engine-integration/runtime-package-map.json').read_text())['patched']
    selected = {r['package'] for r in mapping if 'package' in r}
    providers = {}
    for name, package in packages.items():
        desc = ROOT / 'artifacts/m5/sup-engine-ab/tools/msys64/var/lib/pacman/local' / (name + '-' + package['version']) / 'desc'
        parts = desc.read_text(encoding='utf-8').split('%PROVIDES%\n')
        if len(parts) == 2:
            for value in parts[1].split('\n\n')[0].splitlines():
                providers[re.split(r'[<=>]', value)[0]] = name
    queue = list(selected)
    while queue:
        name = queue.pop()
        for dependency in packages[name]['depends']:
            dependency = re.split(r'[<=>]', dependency)[0]
            dependency = dependency if dependency in packages else providers.get(dependency, dependency)
            if dependency not in packages:
                raise ValueError(f'Unresolved package dependency: {name} -> {dependency}')
            if dependency not in selected:
                selected.add(dependency)
                queue.append(dependency)
    requests = {}
    for name in sorted(selected):
        package = packages[name]
        archive = ROOT / package['archive']
        if sha(archive) != package['sha256']:
            raise ValueError(f'Cached binary differs: {name}')
        text = subprocess.check_output(['tar.exe', '-xOf', str(archive), '.PKGINFO']).decode('utf-8')
        metadata = dict(line.split(' = ', 1) for line in text.splitlines() if ' = ' in line)
        if metadata['pkgver'] != package['version']:
            raise ValueError(f'Binary package version differs: {name}')
        version = metadata['pkgver'].split(':')[-1]
        source = f'{metadata["pkgbase"]}-{version}.src.tar.zst'
        requests.setdefault(source, []).append(dict(package=name, version=package['version'], license=metadata.get('license')))

    def fetch(item):
        name, binaries = item
        url = 'https://repo.msys2.org/mingw/sources/' + name
        destination = args.output / name
        result = dict(file=name, url=url, binaries=binaries)
        try:
            if not destination.exists():
                temporary = destination.with_suffix('.partial')
                try:
                    with urllib.request.urlopen(url, timeout=60) as response, temporary.open('wb') as out:
                        shutil.copyfileobj(response, out)
                    temporary.rename(destination)
                finally:
                    temporary.unlink(missing_ok=True)
            members = subprocess.check_output(['tar.exe', '-tf', str(destination)]).decode('utf-8').splitlines()
            if not any(path.endswith('/PKGBUILD') for path in members):
                raise ValueError('Source package has no build recipe')
            result.update(sha256=sha(destination), bytes=destination.stat().st_size, entries=len(members), acquired=True)
        except Exception as error:
            result.update(acquired=False, error=str(error))
        print(f'{name}: {result["acquired"]}', flush=True)
        return result

    with ThreadPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(fetch, sorted(requests.items())))
    (args.output / 'source-packages.json').write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    print(f'{sum(r["acquired"] for r in results)}/{len(results)} source archives acquired; release source audit remains separate.')


if __name__ == '__main__':
    main()
