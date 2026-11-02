"""Reject unrelated, failed or altered artifacts and never overwrite release assets."""
import importlib.util
from pathlib import Path
import sys
import unittest

BUILD = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BUILD))
try:
    spec = importlib.util.spec_from_file_location('stage_beta_assets', BUILD / 'stage_beta_assets.py')
    stage = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(stage)
finally:
    sys.path.pop(0)


class StageTests(unittest.TestCase):
    def setUp(self):
        self.pin = {'applicationCommit': 'application', 'archiveBytes': 100, 'archiveSha256': 'a' * 64}
        self.run = {'id': 42, 'conclusion': 'success', 'head_sha': 'application', 'path': '.github/workflows/beta-package.yml', 'head_branch': 'main', 'event': 'workflow_dispatch'}
        self.artifact = {'name': 'mani-beta-x64-3-1', 'workflow_run': {'id': 42}, 'expired': False, 'digest': 'sha256:' + 'a' * 64, 'size_in_bytes': 100}

    def test_archive_uses_actual_application_commit(self):
        stage.verify_artifact(self.artifact, self.run, 'archive', self.pin, 'later-installer-commit')

    def test_wrong_or_failed_origin_is_rejected(self):
        for key, value in [('conclusion', 'failure'), ('head_sha', 'other'), ('path', '.github/workflows/other.yml'), ('head_branch', 'other'), ('event', 'pull_request')]:
            with self.subTest(key=key):
                run = dict(self.run, **{key: value})
                with self.assertRaises(ValueError): stage.verify_artifact(self.artifact, run, 'archive', self.pin, 'later')

    def test_changed_or_expired_artifact_is_rejected(self):
        for key, value in [('expired', True), ('workflow_run', {'id': 99}), ('digest', 'sha256:' + 'b' * 64), ('size_in_bytes', 101), ('name', 'unrelated')]:
            with self.subTest(key=key):
                artifact = dict(self.artifact, **{key: value})
                with self.assertRaises(ValueError): stage.verify_artifact(artifact, self.run, 'archive', self.pin, 'later')

    def test_setup_must_match_current_commit(self):
        run = dict(self.run, path='.github/workflows/beta-setup.yml', head_sha='current')
        artifact = dict(self.artifact, name='mani-beta-0.1-setup-x64-2-1')
        stage.verify_artifact(artifact, run, 'setup', self.pin, 'current')
        with self.assertRaises(ValueError): stage.verify_artifact(artifact, run, 'setup', self.pin, 'newer')

    def test_new_equal_and_changed_assets(self):
        asset = {'state': 'uploaded', 'size': 100, 'digest': 'sha256:' + 'a' * 64}
        self.assertEqual('Setup.exe', stage.upload_name(None, 'Setup.exe', 100, 'a' * 64))
        self.assertIsNone(stage.upload_name(asset, 'Setup.exe', 100, 'a' * 64))
        self.assertEqual('pending-0.1.1.0-Setup.exe', stage.upload_name(asset, 'Setup.exe', 101, 'b' * 64))

    def test_desktop_uses_its_own_successful_workflow_and_current_commit(self):
        run = dict(self.run, path='.github/workflows/desktop-installer.yml', head_sha='current')
        artifact = dict(self.artifact, name='mani-desktop-x64-2-1')
        stage.verify_artifact(artifact, run, 'desktop', self.pin, 'current')
        for altered in (dict(run, head_sha='old'), dict(run, conclusion='failure'),
                        dict(run, path='.github/workflows/beta-setup.yml')):
            with self.assertRaises(ValueError): stage.verify_artifact(artifact, altered, 'desktop', self.pin, 'current')


if __name__ == '__main__': unittest.main()
