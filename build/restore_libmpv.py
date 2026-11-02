"""Restore the committed libmpv build without changing its lock or running it.

Hash and archive-member checks reuse Mani's local prepare_libmpv.py acquisition
logic. CI consumes the existing lock; it does not select or probe a new build.
"""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path, PureWindowsPath
import shutil
import subprocess
import tempfile
import urllib.request
import urllib.error
import urllib.parse
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def github_download(asset, token):
    """Use contents:read for private assets; never forward it to storage redirects."""
    release = asset['githubRelease']
    repository = release['repository']
    require(re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository), 'Invalid native release repository')
    prefix = 'https://api.github.com/repos/' + repository
    headers = {'Accept': 'application/vnd.github+json', 'User-Agent': 'Mani-libmpv-restore',
               'X-GitHub-Api-Version': '2022-11-28'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    opener = urllib.request.build_opener(NoRedirect())
    url = prefix + '/releases/tags/' + urllib.parse.quote(release['tag'], safe='')
    with opener.open(urllib.request.Request(url, headers=headers), timeout=60) as response:
        metadata = json.load(response)
    matches = [entry for entry in metadata.get('assets', []) if entry.get('name') == asset['name']]
    require(len(matches) == 1, 'Locked native release asset is missing or ambiguous')
    entry = matches[0]
    require(entry.get('size') == asset['bytes'] and entry.get('state') == 'uploaded', 'Native release asset size/state differs')
    require(type(entry.get('id')) is int and entry['id'] > 0, 'Invalid native release asset id')
    if entry.get('digest'):
        require(entry['digest'].lower() == 'sha256:' + asset['sha256'].lower(), 'Native release asset digest differs')
    headers['Accept'] = 'application/octet-stream'
    request = urllib.request.Request(prefix + '/releases/assets/' + str(entry['id']), headers=headers)
    try:
        return opener.open(request, timeout=60)
    except urllib.error.HTTPError as error:
        if error.code not in (301, 302, 303, 307, 308):
            raise
        location = error.headers.get('Location', '')
        require(urllib.parse.urlsplit(location).scheme == 'https', 'Native release redirect requires HTTPS')
        # A signed storage URL is sufficient; it must not receive Authorization.
        return urllib.request.urlopen(urllib.request.Request(location,
            headers={'User-Agent': 'Mani-libmpv-restore'}), timeout=60)


def require(ok, message):
    if not ok:
        raise ValueError(message)


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def validate_members(names):
    for name in names:
        path = PureWindowsPath(name)
        require(bool(name) and not path.drive and not path.root and '..' not in path.parts
                and ':' not in name, f'Unsafe archive member: {name}')


def extract(archive, destination):
    tar = shutil.which('tar.exe') or str(Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/tar.exe')
    require(Path(tar).is_file(), 'Windows tar.exe is required')
    result = subprocess.run([tar, '-tf', str(archive)], capture_output=True, encoding='utf-8', errors='replace', timeout=60, check=True)
    validate_members(result.stdout.splitlines())
    details = subprocess.run([tar, '-tvf', str(archive)], capture_output=True, encoding='utf-8', errors='replace', timeout=60, check=True)
    require(all(line[0] in '-d' for line in details.stdout.splitlines() if line), 'Archive links or special entries are not allowed')
    subprocess.run([tar, '-xf', str(archive), '-C', str(destination), '--no-same-owner', '--no-same-permissions'],
                   capture_output=True, timeout=120, check=True)


def verify_files(folder, files, exact=False):
    names = [relative.lower() for relative, _ in files]
    require(len(names) == len(set(names)), 'Duplicate native file inventory')
    for relative, record in files:
        path = folder / relative
        require(not path.is_symlink() and path.resolve().is_relative_to(folder.resolve()),
                f'Native file escapes its directory: {relative}')
        require(path.is_file(), f'Missing locked native file: {relative}')
        require(path.stat().st_size == record['bytes'] and sha256(path) == record['sha256'].lower(),
                f'Locked native file differs: {relative}; refusing to replace it')
    if exact:
        actual = {path.relative_to(folder).as_posix().lower() for path in folder.rglob('*') if path.is_file()}
        require(actual == set(names), 'Unexpected files in the locked native bundle')


def restore(root, archive_path=None, archive_url=None):
    root = root.resolve()
    lock = json.loads((root / 'config/libmpv.lock.json').read_text(encoding='utf-8'))
    runtime = json.loads((root / 'config/mpv-runtime.json').read_text(encoding='utf-8'))
    properties = ET.parse(root / 'config/libmpv.props').getroot()
    validate_members([lock['dll']])
    dll = root / lock['dll']
    destination = dll.parent
    require(destination.resolve().is_relative_to(root / 'native'), 'Locked destination must be inside native/')
    prefix = destination.relative_to(root).as_posix() + '/'
    files = [(entry['path'][len(prefix):], entry) for entry in lock['files'] if entry['path'].startswith(prefix)]
    require(files, 'The lock has no native file inventory')
    validate_members([relative for relative, _ in files])
    dll_record = next(entry for relative, entry in files if relative == dll.name)
    require(dll_record['sha256'].lower() == runtime['sha256'].lower() ==
            properties.findtext('./PropertyGroup/ManiMpvSha256').lower(), 'Native hash configurations disagree')
    configured_path = properties.findtext('./PropertyGroup/ManiMpvDll').replace('$(MSBuildThisFileDirectory)', str(root / 'config') + '/')
    require(Path(configured_path.replace('\\', '/')).resolve() == dll.resolve(), 'Native path configurations disagree')
    exact = lock.get('schemaVersion', 1) >= 2
    if exact:
        runtime_files = {entry['file']: (entry['bytes'], entry['sha256'].lower()) for entry in runtime['files']}
        expected = {relative: (entry['bytes'], entry['sha256'].lower()) for relative, entry in files}
        require(runtime_files == expected, 'Native runtime inventories disagree')
        props_files = {}
        for item in properties.findall('./ItemGroup/ManiMpvFile'):
            relative = item.findtext('RelativePath')
            include = item.get('Include').replace('$(MSBuildThisFileDirectory)', str(root / 'config') + '/')
            require(Path(include.replace('\\', '/')).resolve() == (destination / relative).resolve(),
                    'Native item path configurations disagree')
            props_files[relative] = item.findtext('Sha256').lower()
        require(props_files == {name: value[1] for name, value in expected.items()}, 'Native build inventories disagree')
    if destination.exists():
        verify_files(destination, files, exact)
        print('Pinned libmpv is already present and verified.')
        return

    asset = lock['archives']['dev']
    validate_members([asset['name']])
    require(Path(asset['name']).name == asset['name'], 'Invalid archive filename')
    url = asset.get('url') or archive_url
    if url:
        require(url.startswith('https://'), 'Native download requires HTTPS')
        if not exact:
            require(url.startswith('https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/'), 'Unexpected download origin')
    if archive_path:
        archive = Path(archive_path).resolve()
    else:
        cache = root / 'artifacts/downloads'
        cache.mkdir(parents=True, exist_ok=True)
        archive = cache / asset['name']
        if not archive.exists():
            require(url or asset.get('githubRelease'), 'Native archive has not been published. Supply --archive or MANI_NATIVE_ARCHIVE_URL; the locked SHA-256 remains mandatory.')
            partial = archive.with_name(archive.name + '.' + uuid.uuid4().hex + '.partial')
            try:
                if asset.get('githubRelease'):
                    response = github_download(asset, os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN'))
                else:
                    request = urllib.request.Request(url, headers={'User-Agent': 'Mani-libmpv-restore'})
                    response = urllib.request.urlopen(request, timeout=60)
                with response, partial.open('xb') as output:
                    shutil.copyfileobj(response, output)
                require(partial.stat().st_size == asset['bytes'] and sha256(partial) == asset['sha256'].lower(),
                        'Downloaded native archive hash or size mismatch')
                partial.replace(archive)
            finally:
                partial.unlink(missing_ok=True)
    require(archive.stat().st_size == asset['bytes'] and sha256(archive) == asset['sha256'].lower(),
            'Native archive hash or size mismatch')
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix='libmpv-', dir=destination.parent))
    try:
        extract(archive, staging)
        verify_files(staging, files, exact)
        staging.rename(destination)
    finally:
        if staging.exists():
            require(staging.resolve().is_relative_to(root / 'native'), 'Unexpected extraction cleanup path')
            shutil.rmtree(staging)
    print('Pinned libmpv restored; lock files were not changed.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', type=Path, help='Use an existing archive, with the same mandatory hash verification')
    parser.add_argument('--archive-url', default=os.environ.get('MANI_NATIVE_ARCHIVE_URL'),
                        help='HTTPS mirror for an unpublished locked archive; cannot override a published URL or its hash')
    args = parser.parse_args()
    restore(ROOT, args.archive, args.archive_url)
