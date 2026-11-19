import io
import json
import runpy
import stat
import sys
import tempfile
import unittest
import zipfile
from hashlib import sha256
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch


HERE = Path(__file__).resolve().parent
if sys.platform == 'win32':
    # These tests invoke the deployment methods directly. The root CLI and its lock run on Linux.
    with patch.dict(sys.modules, {'fcntl': Mock()}):
        UPDATER = runpy.run_path(str(HERE / 'movie-discovery-update.py'))
else:
    UPDATER = runpy.run_path(str(HERE / 'movie-discovery-update.py'))
RESOLVER = runpy.run_path(str(HERE / 'resolve-release.py'))
REVISION = 'a' * 40
RELEASE = {
    'version': 1, 'revision': REVISION, 'topology': 'b' * 64,
    'images': {name: 'sha256:' + 'c' * 64 for name in ('api', 'web', 'bot')},
    'migrations': ['20260101000000_Initial', '20260201000000_Second'],
}


class TrustedPathTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(Path.cwd().anchor)
        self.mount = self.root / 'srv' / 'app-storage'
        self.state = self.mount / 'update'
        self.override = self.state / 'active.json'

    def check_path(self, path, metadata=None, **kwargs):
        metadata = metadata or {}
        directories = set(path.parents) | {self.state}

        def lstat(component):
            default_mode = stat.S_IFDIR | 0o755 if component in directories else stat.S_IFREG | 0o600
            mode, owner = metadata.get(component, (default_mode, 0))
            return SimpleNamespace(st_mode=mode, st_uid=owner)

        with patch.object(Path, 'lstat', lstat):
            UPDATER['trusted_path'](path, **kwargs)

    def test_root_owned_file_under_root_owned_mount_is_trusted(self):
        self.check_path(self.override, private=True)

    def test_private_root_owned_leaf_does_not_trust_runner_owned_ancestor(self):
        with self.assertRaisesRegex(RuntimeError, 'Unsafe path component'):
            self.check_path(self.state, {self.state: (stat.S_IFDIR | 0o700, 0),
                                         self.mount: (stat.S_IFDIR | 0o755, 1001)}, directory=True, private=True)

    def test_group_writable_root_owned_ancestor_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, 'Unsafe path component'):
            self.check_path(self.override, {self.mount: (stat.S_IFDIR | 0o775, 0)})

    def test_symlink_ancestor_and_leaf_are_rejected(self):
        for component in (self.mount, self.override):
            with self.subTest(component=component), self.assertRaisesRegex(RuntimeError, 'Unsafe path component'):
                self.check_path(self.override, {component: (stat.S_IFLNK | 0o755, 0)})

    def test_private_file_with_group_read_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, 'Unsafe path component'):
            self.check_path(self.override, {self.override: (stat.S_IFREG | 0o640, 0)}, private=True)

    def test_relative_and_traversal_paths_are_rejected(self):
        for path in (Path('override.json'), self.state / '..' / 'override.json'):
            with self.subTest(path=path), self.assertRaisesRegex(RuntimeError, 'normalized absolute path'):
                self.check_path(path)


class UpdaterTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        base = Path(self.directory.name)
        updater = UPDATER['Updater'].__new__(UPDATER['Updater'])
        updater.state = base
        updater.backups = base
        updater.active = base / 'active.json'
        updater.pending = base / 'pending.json'
        updater.failed = base / 'failed.json'
        updater.database = 'moviediscovery'
        updater.config = {'mounts': {'db': '/', 'backup': '/'}}
        updater.services = {'db': {'volumes': [{'source': base, 'target': '/var/lib/postgresql/data'}]}}
        updater.sql = Mock(return_value='1000')
        updater.database_oid = Mock(return_value='92000')
        updater.topology = Mock(return_value=RELEASE['topology'])
        updater.current_containers = Mock(return_value={'api': 'old-api', 'web': 'old-web'})
        updater.history = Mock(side_effect=[RELEASE['migrations'][:1], RELEASE['migrations']])
        self.updater = updater
        for name in ('storage', 'verify_main', 'readiness', 'labels', 'compose', 'allow_connections',
                     'writers_stopped', 'backup', 'require_capacity', 'check_template_metadata',
                     'clone_database', 'drop_candidate_database', 'probe_release', 'start', 'publish_database',
                     'recovery_database', 'discard_obsolete_image_refs', 'discard_obsolete_database_copies',
                     'discard_obsolete_archives'):
            setattr(updater, name, Mock(return_value={} if name == 'recovery_database' else None))

        def write(path, data):
            path.write_text(json.dumps(data))

        runner_globals = updater.deploy.__globals__
        replacement = patch.dict(runner_globals, {'atomic': write, 'command': Mock()})
        replacement.start()
        self.addCleanup(replacement.stop)

    def test_new_migrations_pass_private_probe_before_publish(self):
        checks = Mock()
        for name in ('allow_connections', 'writers_stopped', 'backup', 'clone_database',
                     'probe_release', 'publish_database', 'start'):
            checks.attach_mock(getattr(self.updater, name), name)
        self.updater.deploy(RELEASE)
        self.assertEqual([call[0] for call in checks.mock_calls],
                         ['allow_connections', 'writers_stopped', 'clone_database', 'backup', 'probe_release',
                          'allow_connections', 'writers_stopped', 'publish_database', 'start', 'start'])
        candidate = self.updater.probe_release.call_args.args[3]
        self.assertRegex(candidate, r'^md_probe_')
        self.updater.backup.assert_called_once_with(self.updater.backups / candidate.replace('md_probe_', 'update-').replace('_', '-'),
                                                   candidate, 1000)
        self.updater.require_capacity.assert_any_call('db', self.updater.state, 3000)
        self.assertTrue(self.updater.active.exists())
        self.assertFalse(self.updater.pending.exists())

    def test_migration_probe_failure_reopens_original_without_renaming(self):
        self.updater.probe_release.side_effect = RuntimeError('Failed candidate')
        with self.assertRaisesRegex(RuntimeError, 'Failed candidate'):
            self.updater.deploy(RELEASE)
        self.updater.publish_database.assert_not_called()
        self.updater.allow_connections.assert_called_with(self.updater.database, True)
        self.updater.drop_candidate_database.assert_called_once()
        self.assertEqual(self.updater.start.call_args.args[1:], ('api', 'web'))
        self.assertFalse(self.updater.pending.exists())
        self.assertTrue(self.updater.failed.exists())

    def test_publish_failure_preserves_database_and_recovery_journal(self):
        self.updater.start.side_effect = RuntimeError('Failed publish')
        with self.assertRaisesRegex(RuntimeError, 'Failed publish'):
            self.updater.deploy(RELEASE)
        self.updater.publish_database.assert_called_once()
        self.updater.drop_candidate_database.assert_not_called()
        self.assertEqual(json.loads(self.updater.pending.read_text())['phase'], 'publish')

    def test_writer_detection_prevents_backup_and_migration(self):
        self.updater.writers_stopped.side_effect = RuntimeError('Active writers')
        with self.assertRaisesRegex(RuntimeError, 'Active writers'):
            self.updater.deploy(RELEASE)
        self.updater.backup.assert_not_called()
        self.updater.probe_release.assert_not_called()
        self.updater.publish_database.assert_not_called()
        self.updater.allow_connections.assert_called_with(self.updater.database, True)

    def test_same_schema_release_uses_original_without_database_swap(self):
        self.updater.history.side_effect = [RELEASE['migrations'], RELEASE['migrations']]
        self.updater.deploy(RELEASE)
        self.updater.clone_database.assert_not_called()
        self.updater.allow_connections.assert_not_called()
        self.updater.publish_database.assert_not_called()
        self.assertEqual(self.updater.probe_release.call_args.args[3], self.updater.database)

    def test_before_publish_rechecks_the_current_main(self):
        self.updater.verify_main.side_effect = [None, None, RuntimeError('Superseded')]
        with self.assertRaisesRegex(RuntimeError, 'Superseded'):
            self.updater.deploy(RELEASE)
        self.updater.allow_connections.assert_called_with(self.updater.database, True)
        self.updater.publish_database.assert_not_called()


class ResolverTests(unittest.TestCase):
    def resolve(self, event='push', digest_matches=True, repository='Xydik1otto/movie-discovery', conclusion='success'):
        run = {'id': 1, 'event': event, 'head_branch': 'main', 'head_sha': REVISION,
               'head_repository': {'full_name': repository}, 'conclusion': conclusion,
               'path': '.github/workflows/build-server-release.yml'}
        data = io.BytesIO()
        with zipfile.ZipFile(data, 'w') as archive:
            archive.writestr('server-release.json', json.dumps(RELEASE))
        digest = sha256(data.getvalue()).hexdigest() if digest_matches else 'f' * 64
        github_json = Mock(side_effect=[{'workflow_runs': [run]}, {'artifacts': [
            {'id': 2, 'name': 'server-release', 'expired': False, 'digest': 'sha256:' + digest},
        ]}])
        mocks = {'main_revision': Mock(return_value=REVISION), 'github_json': github_json,
                 'github': Mock(return_value=data.getvalue())}
        resolve = RESOLVER['resolve']
        with patch.dict(resolve.__globals__, mocks):
            return resolve('test-token')

    def test_accepts_verified_main_push(self):
        self.assertEqual(self.resolve(), RELEASE)

    def test_ignores_pull_request_result(self):
        self.assertIsNone(self.resolve(event='pull_request'))

    def test_refuses_modified_archive(self):
        with self.assertRaisesRegex(ValueError, 'checksum'):
            self.resolve(digest_matches=False)

    def test_ignores_build_from_a_fork(self):
        self.assertIsNone(self.resolve(repository='example/movie-discovery'))

    def test_ignores_failed_main_build(self):
        self.assertIsNone(self.resolve(conclusion='failure'))
