"""Stage verified Actions artifacts on the existing Beta release; never replace assets.

Reuse Mani's locked-input hashing and safe archive checks. This is only the GitHub
delivery boundary; publication of stable download names remains a separate step.
"""
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile

from restore_libmpv import NoRedirect, require, sha256, validate_members
from verify_desktop_release import FILES as DESKTOP_FILES, verify as verify_desktop

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = 'showmethecold/mani'
PREFIX = '/repos/' + REPOSITORY
STAGED_PREFIX = 'pending-0.1.1.0-'


def verify_artifact(artifact, run, mode, pin, head):
    require(mode in ('archive', 'setup', 'desktop'), 'Unexpected stage mode')
    expected_path = '.github/workflows/' + {'archive':'beta-package.yml', 'setup':'beta-setup.yml', 'desktop':'desktop-installer.yml'}[mode]
    expected_head = pin['applicationCommit'] if mode == 'archive' else head
    require(run['conclusion'] == 'success' and run['head_sha'] == expected_head and run['path'] == expected_path, 'Unexpected source workflow or commit')
    require(run['head_branch'] == 'main' and run['event'] == 'workflow_dispatch', 'Expected a maintained manual build')
    require(artifact['workflow_run']['id'] == run['id'] and not artifact['expired'], 'Artifact origin mismatch')
    prefix = {'archive':'mani-beta-x64-', 'setup':'mani-beta-0.1-setup-x64-', 'desktop':'mani-desktop-x64-'}[mode]
    require(artifact['name'].startswith(prefix), 'Unexpected artifact kind')
    require(re.fullmatch(r'sha256:[0-9a-f]{64}', artifact['digest']) is not None, 'Missing artifact digest')
    if mode == 'archive':
        require(artifact['size_in_bytes'] == pin['archiveBytes'] and artifact['digest'] == 'sha256:' + pin['archiveSha256'], 'Pinned artifact mismatch')


def upload_name(existing, name, size, digest, prefix=STAGED_PREFIX):
    if existing is None:
        return name
    if existing['state'] == 'uploaded' and existing['size'] == size and existing['digest'] == 'sha256:' + digest:
        return None
    return prefix + name


def main():
    require(os.environ.get('GITHUB_REPOSITORY') == REPOSITORY and os.environ.get('GITHUB_REF') == 'refs/heads/main', 'Only the maintained repository branch can stage a release')
    mode = os.environ['MANI_STAGE_MODE']
    require(mode in ('archive', 'setup', 'desktop'), 'Unexpected stage mode')
    artifact_id = os.environ['MANI_STAGE_ARTIFACT']
    require(artifact_id.isdecimal(), 'Expected numeric artifact ID')
    head = os.environ['GITHUB_SHA']
    headers = {'Authorization': 'Bearer ' + os.environ['GITHUB_TOKEN'], 'Accept': 'application/vnd.github+json',
               'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'Mani-release-stage'}

    def request(path):
        require(path.startswith(PREFIX + '/'), 'Unexpected API repository')
        with urllib.request.urlopen(urllib.request.Request('https://api.github.com' + path, headers=headers), timeout=60) as response:
            return json.load(response)

    pin = json.loads((ROOT / 'config/beta-0.1.json').read_text(encoding='utf-8'))
    artifact = request(PREFIX + '/actions/artifacts/' + artifact_id)
    run = request(PREFIX + '/actions/runs/' + str(artifact['workflow_run']['id']))
    verify_artifact(artifact, run, mode, pin, head)
    require(request(PREFIX + '/git/ref/heads/main')['object']['sha'] == head, 'Main changed during staging')
    release = request(PREFIX + '/releases/tags/beta-0.1')
    require(release['prerelease'] and not release['draft'] and not release.get('immutable'), 'Expected the existing editable Beta prerelease')
    upload = urllib.parse.urlsplit(release['upload_url'].split('{', 1)[0])
    require(upload.scheme == 'https' and upload.netloc == 'uploads.github.com' and
            upload.path == PREFIX + '/releases/' + str(release['id']) + '/assets', 'Unexpected upload destination')

    with tempfile.TemporaryDirectory(prefix='mani-release-stage-') as temporary:
        folder = Path(temporary)
        archive_path = folder / 'artifact.zip'
        download = urllib.request.Request('https://api.github.com' + PREFIX + '/actions/artifacts/' + artifact_id + '/zip', headers=headers)
        try:
            response = urllib.request.build_opener(NoRedirect()).open(download, timeout=60)
        except urllib.error.HTTPError as error:
            require(error.code in (301, 302, 303, 307, 308), 'Artifact download failed')
            location = error.headers['Location']
            require(urllib.parse.urlparse(location).scheme == 'https', 'Insecure artifact redirect')
            # The storage request never carries the GitHub credential.
            response = urllib.request.urlopen(location, timeout=60)
        with response, archive_path.open('xb') as output:
            shutil.copyfileobj(response, output, 1024 * 1024)
        require(archive_path.stat().st_size == artifact['size_in_bytes'] and 'sha256:' + sha256(archive_path) == artifact['digest'], 'Artifact content mismatch')
        with zipfile.ZipFile(archive_path) as archive:
            names = archive.namelist()
            validate_members(names)
            require(len(names) == len(set(names)) and all(Path(n).suffix.lower() not in ('.pfx', '.p12', '.key') for n in names), 'Unsafe artifact contents')
            if mode == 'archive':
                source = json.loads(archive.read('source-info.json').decode('utf-8-sig'))
                require(source['commit'] == pin['applicationCommit'] and not source['dirty'], 'Application source mismatch')
                for entry in pin['files']:
                    data = archive.read(entry['file'])
                    require(len(data) == entry['bytes'] and hashlib.sha256(data).hexdigest() == entry['sha256'], 'Pinned MSIX input mismatch')
                url = urllib.parse.urlsplit(pin['archiveUrl'])
                require(url.scheme == 'https' and url.netloc == 'github.com' and url.path.startswith('/' + REPOSITORY + '/releases/download/beta-0.1/'), 'Unexpected archive destination')
                files = [(Path(url.path).name, archive_path)]
            elif mode == 'desktop':
                require(set(names) == DESKTOP_FILES, 'Unexpected desktop artifact files')
                archive.extractall(folder / 'desktop')
                setup = folder / 'desktop'
                verify_desktop(setup, head)
                files = [(name, setup / name) for name in sorted(DESKTOP_FILES)]
            else:
                expected = {'Mani-Beta-0.1-Windows-x64-Setup.exe', 'setup-info.json', pin['files'][0]['file'], pin['files'][1]['file'],
                            'Mani-Setup-source.zip', 'setup-source-info.json', 'setup-source-files.json', 'INSTALL.txt', 'INSTALL.zh-CN.txt', 'SETUP-SHA256SUMS.txt', 'payload-verification.json'}
                require(set(names) == expected, 'Unexpected setup artifact files')
                archive.extractall(folder / 'setup')
                setup = folder / 'setup'
                info = json.loads((setup / 'setup-info.json').read_text(encoding='utf-8-sig'))
                source = json.loads((setup / 'setup-source-info.json').read_text(encoding='utf-8-sig'))
                require(source['archive'] == 'Mani-Setup-source.zip' and source['commit'] == head and not source['dirty'] and sha256(setup / source['archive']) == source['archiveSha256'], 'Setup source mismatch')
                require(info['configuration'] == 'Release' and info['signatureStatus'] in ('Valid', 'UnknownError', 'NotTrusted'), 'Expected signed Release setup')
                require(info['setupFile'] == 'Mani-Beta-0.1-Windows-x64-Setup.exe' and info['setupVersion'] == '0.1.1.0' and info['packageVersion'] == pin['packageVersion'] and sha256(setup / info['setupFile']) == info['setupSha256'], 'Setup version or digest mismatch')
                for entry in pin['files']:
                    require((setup / entry['file']).stat().st_size == entry['bytes'] and sha256(setup / entry['file']) == entry['sha256'], 'Setup input differs from pin')
                for line in (setup / 'SETUP-SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
                    digest, name = line.split('  ', 1)
                    require(name in expected and sha256(setup / name) == digest, 'Setup checksum mismatch')
                files = [(name, setup / name) for name in sorted(expected - {'payload-verification.json'})]
        for name, path in files:
            size, digest = path.stat().st_size, sha256(path)
            existing = next((a for a in release['assets'] if a['name'] == name), None)
            selected = upload_name(existing, name, size, digest,
                                   'pending-desktop-' if mode == 'desktop' else STAGED_PREFIX)
            if selected is None:
                print('Already verified: ' + name, flush=True)
                continue
            if mode == 'archive':
                require(selected == name, 'An unexpected archive already occupies this name')
            staged = next((a for a in release['assets'] if a['name'] == selected), None)
            if staged:
                require(upload_name(staged, selected, size, digest) is None, 'Unexpected staging asset exists')
                continue
            connection = http.client.HTTPSConnection(upload.hostname, timeout=240, blocksize=1024 * 1024)
            with path.open('rb') as content:
                connection.request('POST', upload.path + '?' + urllib.parse.urlencode({'name': selected}), body=content,
                                   headers=dict(headers, **{'Content-Type': 'application/octet-stream', 'Content-Length': str(size)}))
                response = connection.getresponse()
                require(response.status == 201, 'Release upload failed: HTTP ' + str(response.status))
                result = json.load(response)
            connection.close()
            require(upload_name(result, selected, size, digest) is None, 'Uploaded bytes differ')
            print('Uploaded and verified: ' + selected, flush=True)


if __name__ == '__main__':
    main()
