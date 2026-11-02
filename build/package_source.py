"""Capture the actual project inputs, including uncommitted, nonignored files.

Native corresponding-source materials are separate; this archive describes Mani.
Never includes Git metadata, ignored user data, keys, or dependency caches.
"""
import argparse
import json
from pathlib import Path
import subprocess
import zipfile

from restore_libmpv import sha256, require, validate_members

ROOT = Path(__file__).resolve().parents[1]


def capture(output):
    def git(*args):
        return subprocess.check_output(['git', '-C', str(ROOT), *args]).decode('utf-8')
    names = sorted(set(git('ls-files', '-z', '--cached', '--others', '--exclude-standard').split('\0')) - {''})
    names = [name for name in names if (ROOT / name).is_file()]
    validate_members(names)
    require(not any(Path(name).suffix.lower() in ('.pfx', '.p12', '.key') for name in names), 'Private signing material must not be tracked')
    records = [dict(file=name, sha256=sha256(ROOT / name), bytes=(ROOT / name).stat().st_size) for name in names]
    listing = output / 'source-files.json'
    require(not listing.exists(), 'Existing source evidence preserved')
    listing.write_text(json.dumps(records, indent=2) + '\n', encoding='utf-8')
    archive = output / 'Mani-source.zip'
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as package:
        for name in names:
            package.write(ROOT / name, name)
    info = dict(commit=git('rev-parse', 'HEAD').strip(), dirty=bool(git('status', '--porcelain').strip()),
                inventorySha256=sha256(listing), archive=archive.name, archiveSha256=sha256(archive),
                fileCount=len(records), scope='Mani project inputs; native corresponding source is separate')
    (output / 'source-info.json').write_text(json.dumps(info, indent=2) + '\n', encoding='utf-8')
    print(f'Captured {len(records)} project inputs; dirty={info["dirty"]}.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    capture(parser.parse_args().output)
