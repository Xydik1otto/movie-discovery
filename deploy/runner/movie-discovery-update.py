#!/usr/bin/python3 -I
import fcntl
import hashlib
import json
import os
import re
import runpy
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import urllib.request
import uuid
from pathlib import Path


CONFIG = Path('/etc/movie-discovery/update.json')
TOKEN = Path('/etc/movie-discovery/github-actions-token')
RESOLVER = Path('/usr/local/lib/movie-discovery/resolve-release.py')
REPOSITORY = 'https://github.com/Xydik1otto/movie-discovery.git'
REGISTRY = 'ghcr.io/xydik1otto/movie-discovery-'
SERVICES = ('api', 'web', 'bot')
TOPOLOGY = ('docker-compose.funnel.yml', 'docker-compose.server.yml', 'docker-compose.yml', 'minio/Dockerfile')
GIB = 1024 ** 3
REVISION = r'[0-9a-f]{40}'
IDENTIFIER = r'[a-z][a-z0-9_]{0,40}'


def command(*args, capture=False, stdin=None, stdout=None, env=None, timeout=120):
    args = tuple(map(str, args))
    try:
        result = subprocess.run(args, stdin=stdin, stdout=subprocess.PIPE if capture else stdout,
                                env=env, check=True, timeout=timeout)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError(f'{args[0]} command failed or timed out') from exc
    return result.stdout.decode().strip() if capture else None


def atomic(path, data):
    file = tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=path.parent,
                                       prefix=f'.{path.name}.', delete=False)
    pending = Path(file.name)
    try:
        with file:
            file.write(json.dumps(data) + '\n')
            file.flush()
            os.fsync(file.fileno())
        pending.replace(path)
    finally:
        pending.unlink(missing_ok=True)
    fd = os.open(path.parent, os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def trusted_path(value, *, directory=False, private=False):
    path = Path(value)
    if not path.is_absolute() or '..' in path.parts:
        raise RuntimeError(f'Expected a normalized absolute path, got {path}')
    for component in (*reversed(path.parents), path):
        info = component.lstat()
        is_directory = component != path or directory
        expected = stat.S_ISDIR if is_directory else stat.S_ISREG
        forbidden = 0o077 if component == path and private else 0o022
        if not expected(info.st_mode) or info.st_uid != 0 or info.st_mode & forbidden:
            raise RuntimeError(f'Unsafe path component {component}; root must exclusively control the path')


def validate_manifest(manifest):
    migrations = manifest['migrations']
    images = manifest['images']
    if (
        manifest['version'] != 1 or not re.fullmatch(REVISION, manifest['revision'])
        or not re.fullmatch(r'[0-9a-f]{64}', manifest['topology'])
        or set(images) != set(SERVICES)
        or not all(re.fullmatch(r'sha256:[0-9a-f]{64}', image) for image in images.values())
        or not migrations or migrations != sorted(set(migrations))
        or not all(re.fullmatch(r'[0-9]{14}_[A-Za-z0-9_]+', migration) for migration in migrations)
    ):
        raise ValueError('Invalid release manifest')


def require_storage(path, mount, minimum_gib, extra=0):
    path = Path(path).resolve(strict=True)
    mount = Path(mount).resolve(strict=True)
    actual_mount = command('findmnt', '-n', '-o', 'TARGET', '--target', path, capture=True)
    if str(mount) != actual_mount or not path.is_relative_to(mount):
        raise RuntimeError(f'Unexpected filesystem for {path}; expected {mount}')
    if shutil.disk_usage(path).free < minimum_gib * GIB + extra:
        raise RuntimeError(f'Free space on {mount} is below the configured limit for {path}')


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


class Updater:
    def __init__(self):
        config = json.loads(CONFIG.read_text())
        self.config = config
        self.state = Path(config['state_dir'])
        self.backups = Path(config['backup_dir'])
        self.worktree = Path(config['compose_dir'])
        trusted_path(self.state, directory=True, private=True)
        trusted_path(self.backups, directory=True, private=True)
        trusted_path(self.worktree, directory=True)
        trusted_path(config['env_file'], private=True)
        for name in TOPOLOGY:
            trusted_path(self.worktree / name)
        profile = config['profile']
        if profile not in ('server', 'funnel'):
            raise ValueError('Unknown production Compose profile')
        self.base = ('docker', 'compose', '--env-file', config['env_file'], '-f',
                     self.worktree / f'docker-compose.{profile}.yml')
        self.production = json.loads(command(*self.base, 'config', '--format', 'json', capture=True))
        self.services = self.production['services']
        if self.production['name'] != 'moviediscovery':
            raise RuntimeError('Unexpected Compose project name')
        self.database = self.services['db']['environment']['POSTGRES_DB']
        self.db_user = self.services['db']['environment']['POSTGRES_USER']
        if not re.fullmatch(IDENTIFIER, self.database) or self.database in ('postgres', 'template0', 'template1'):
            raise RuntimeError('The production database requires a simple dedicated database name')
        self.active = self.state / 'active.json'
        self.pending = self.state / 'pending.json'
        self.failed = self.state / 'failed.json'
        self.timeout = int(config.get('ready_timeout_seconds', 600))
        self.health_path = '/api/health/ready'

    def compose(self, *args, override=None, **kwargs):
        base = self.base + (('-f', override) if override else ())
        return command(*base, *args, **kwargs)

    def db(self, *args, **kwargs):
        return self.compose('exec', '-T', 'db', *args, **kwargs)

    def sql(self, database, statement, **kwargs):
        return self.db('psql', '-XAtq', '-v', 'ON_ERROR_STOP=1', '-U', self.db_user,
                       '-d', database, '-c', statement, **kwargs)

    def history(self, database=None):
        statement = 'SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId";'
        return self.sql(database or self.database, statement, capture=True).splitlines()

    def writers_stopped(self, name):
        self.ident(name)
        count = self.sql('postgres', f"""
            SELECT (SELECT count(*) FROM pg_stat_activity
                    WHERE datname = '{name}' AND backend_type <> 'autovacuum worker') +
                   (SELECT count(*) FROM pg_prepared_xacts WHERE database = '{name}');
            """, capture=True)
        if count != '0':
            raise RuntimeError('The production database still has client sessions or prepared transactions')

    def allow_connections(self, name, allowed):
        value = 'true' if allowed else 'false'
        self.sql('postgres', f'ALTER DATABASE {self.ident(name)} ALLOW_CONNECTIONS {value};')

    def ident(self, value):
        if not re.fullmatch(IDENTIFIER, value):
            raise RuntimeError('Unexpected PostgreSQL database identifier')
        return '"' + value + '"'

    def topology(self):
        entries = ''.join(
            f'{hashlib.sha256((self.worktree / name).read_bytes()).hexdigest()}  {name}\n'
            for name in TOPOLOGY
        )
        return hashlib.sha256(entries.encode()).hexdigest()

    def storage(self):
        disk = self.config['mounts']
        reserve = self.config['reserve_gib']
        allow_root = self.config.get('allow_root_storage') is True
        if not allow_root and any(mount == '/' for mount in disk.values()):
            raise RuntimeError('One or more deployment mounts share the root filesystem without permission')
        for service, target, key in (('db', '/var/lib/postgresql/data', 'db'), ('minio', '/data', 'minio'),
                                     ('api', '/data/keys', 'api'), ('api', '/data/catalog-cache', 'catalog_cache')):
            binds = [volume for volume in self.services[service]['volumes']
                     if volume['type'] == 'bind' and volume['target'] == target]
            if len(binds) != 1:
                raise RuntimeError(f'Missing configured bind storage for {service} at {target}')
            require_storage(binds[0]['source'], disk[key], reserve[key])
        require_storage(self.state, disk['state'], reserve['state'])
        require_storage(self.backups, disk['backup'], reserve['backup'])
        require_storage('/', '/', reserve['root'])
        info = json.loads(command('docker', 'info', '--format', '{{json .}}', capture=True))
        require_storage(info['DockerRootDir'], disk['docker'], reserve['docker'])
        if dict(info.get('DriverStatus') or []).get('driver-type') == 'io.containerd.snapshotter.v1':
            require_storage(self.config['containerd_dir'], disk['containerd'], reserve['docker'])

    def require_capacity(self, key, path, extra):
        mounts = self.config['mounts']
        reserves = self.config['reserve_gib']
        mount = mounts[key]
        colocated = [reserves.get(name, reserves['docker']) for name, target in mounts.items() if target == mount]
        if mount == '/':
            colocated.append(reserves['root'])
        require_storage(path, mount, max(colocated), extra=extra)

    def port(self, service, target):
        bindings = [port for port in self.services[service]['ports']
                    if port['target'] == target and port.get('host_ip') == '127.0.0.1']
        if len(bindings) != 1:
            raise RuntimeError(f'Expected a single loopback binding for {service}')
        return int(bindings[0]['published'])

    def readiness(self, service, attempts=None):
        port = self.port(service, 5200 if service == 'api' else 3000)
        deadline = time.monotonic() + (attempts if attempts is not None else self.timeout)
        while True:
            try:
                with urllib.request.urlopen(f'http://127.0.0.1:{port}{self.health_path}', timeout=4) as result:
                    if json.load(result).get('db') is True:
                        return
            except (OSError, ValueError):
                pass
            if time.monotonic() >= deadline:
                raise RuntimeError(f'{service} did not pass readiness')
            time.sleep(3)

    def current_containers(self):
        running = {}
        for service in ('db', 'minio', *SERVICES):
            container = self.compose('ps', '-q', '--status', 'running', service, capture=True)
            if container:
                info = json.loads(command('docker', 'inspect', '--format', '{{json .}}', container, capture=True))
                live = {mount['Destination']: mount['Source'] for mount in info['Mounts'] if mount['Type'] == 'bind'}
                for mount in self.services[service].get('volumes', ()):
                    source = live.get(mount['target'])
                    if mount['type'] == 'bind' and (source is None or Path(source).resolve() != Path(mount['source']).resolve()):
                        raise RuntimeError(f'Live storage for {service} differs from the installed Compose configuration')
                running[service] = info['Image']
            elif service != 'bot':
                raise RuntimeError(f'The production {service} container is not running')
        return running

    def image_override(self, path, images):
        atomic(path, {'services': {service: {'image': image} for service, image in images.items()}})

    def labels(self, image, revision):
        label = command('docker', 'image', 'inspect', '--format',
                        '{{index .Config.Labels "org.opencontainers.image.revision"}}', image, capture=True)
        if label != revision:
            raise RuntimeError('Image source revision differs from the release')

    def verify_main(self, revision):
        ref = command('git', 'ls-remote', '--exit-code', REPOSITORY, 'refs/heads/main', capture=True)
        if ref.split() != [revision, 'refs/heads/main']:
            raise RuntimeError('The GitHub main branch has moved past this release')

    def start(self, override, *services):
        self.compose('up', '-d', '--no-deps', '--no-build', '--pull', 'never', *services, override=override)

    def backup(self, directory, database, size):
        self.require_capacity('backup', self.backups, size)
        directory.mkdir(mode=0o700)
        archive = directory / 'database.dump'
        with archive.open('xb') as output:
            self.db('pg_dump', '-U', self.db_user, '-Fc', database, stdout=output, timeout=1800)
            output.flush()
            os.fsync(output.fileno())
        with archive.open('rb') as source:
            self.db('pg_restore', '--list', stdin=source, stdout=subprocess.DEVNULL, timeout=120)
        with (directory / 'globals.sql').open('xb') as output:
            self.db('pg_dumpall', '-U', self.db_user, '--globals-only', stdout=output)
            output.flush()
            os.fsync(output.fileno())
        atomic(directory / 'checksums.json', {
            'database.dump': sha256(archive), 'globals.sql': sha256(directory / 'globals.sql'),
        })
        atomic(self.state / f'archive-{directory.name}.json', {'name': directory.name})
        print(f'Validated pre-release PostgreSQL archive at {directory}', flush=True)

    def recovery_database(self, name, backup):
        self.ident(name)
        return {'name': name, 'backup': str(backup), 'oid': self.database_oid(name)}

    def database_oid(self, name):
        self.ident(name)
        return self.sql('postgres', f"SELECT oid FROM pg_database WHERE datname = '{name}';", capture=True)

    def archive_intact(self, directory):
        checksums = json.loads((directory / 'checksums.json').read_text())
        return all(sha256(directory / file) == checksums[file] for file in ('database.dump', 'globals.sql'))

    def discard_obsolete_database_copies(self):
        keep = max(1, int(self.config.get('keep_database_copies', 1)))
        known = sorted(self.state.glob('recovery-db-*.json'), key=lambda path: path.stat().st_mtime_ns, reverse=True)
        for path in known[keep:]:
            record = json.loads(path.read_text())
            name = record['name']
            backup = Path(record['backup'])
            if (not re.fullmatch(r'md_pre_[0-9a-f]{12}_[0-9a-f]{10}', name)
                    or name == self.database or backup.parent != self.backups
                    or not re.fullmatch(r'update-[0-9a-f]{12}-[0-9a-f]{10}', backup.name)):
                continue
            oid = self.database_oid(name)
            if not oid:
                path.unlink()
                continue
            if oid != record['oid']:
                continue
            if not self.archive_intact(backup):
                raise RuntimeError('An archived PostgreSQL backup no longer matches its checksums')
            self.sql('postgres', f'DROP DATABASE {self.ident(name)};')
            path.unlink()
            print(f'Removed verified obsolete deployment copy {name}.', flush=True)

    def discard_obsolete_archives(self, current):
        keep = max(2, int(self.config.get('keep_release_archives', 3)))
        database_backups = {Path(json.loads(path.read_text())['backup'])
                            for path in self.state.glob('recovery-db-*.json')}
        known = sorted(self.state.glob('archive-update-*.json'), key=lambda path: path.stat().st_mtime_ns,
                       reverse=True)
        for record in known[keep:]:
            name = json.loads(record.read_text())['name']
            if not re.fullmatch(r'update-[0-9a-f]{12}-[0-9a-f]{10}', name) or record.name != f'archive-{name}.json':
                continue
            directory = self.backups / name
            if directory == current or directory in database_backups:
                continue
            if not directory.exists():
                record.unlink()
                continue
            expected = {'checksums.json', 'database.dump', 'globals.sql'}
            if (directory.is_symlink() or {path.name for path in directory.iterdir()} != expected
                    or any(path.is_symlink() for path in directory.iterdir()) or not self.archive_intact(directory)):
                raise RuntimeError(f'Unexpected contents in retained deployment archive {directory}')
            for name in expected:
                (directory / name).unlink()
            directory.rmdir()
            record.unlink()

    def discard_obsolete_image_refs(self, retain):
        def images(path):
            return {service['image'] for service in json.loads(path.read_text())['services'].values()}

        kept = set().union(*(images(path) for path in retain))
        for path in sorted(self.state.glob('release-*.json')) + sorted(self.state.glob('rollback-*.json')):
            if path in retain:
                continue
            unresolved = False
            for image in sorted(images(path) - kept):
                ref = re.fullmatch(r'ghcr\.io/xydik1otto/movie-discovery-(api|web|bot)@sha256:[0-9a-f]{64}'
                                   r'|moviediscovery-update-(api|web|bot):previous-[0-9a-f]{12}-[0-9a-f]{10}', image)
                if not ref:
                    unresolved = True
                    continue
                exists = subprocess.run(('docker', 'image', 'inspect', image), stdout=subprocess.DEVNULL,
                                        stderr=subprocess.DEVNULL, timeout=15)
                if exists.returncode == 0:
                    result = subprocess.run(('docker', 'image', 'rm', image), stdout=subprocess.DEVNULL,
                                            stderr=subprocess.DEVNULL, timeout=60)
                    unresolved |= result.returncode != 0
            if not unresolved:
                path.unlink()

    def check_template_metadata(self):
        # Template databases do not copy per-database role settings or database-level ACLs.
        metadata = self.sql(self.database, '''
            SELECT (pg_get_userbyid(datdba) <> current_user OR datacl IS NOT NULL OR datconnlimit <> -1
                    OR datistemplate OR NOT datallowconn OR dattablespace <> (
                        SELECT oid FROM pg_tablespace WHERE spcname = 'pg_default'
                    ) OR EXISTS (
                        SELECT 1 FROM pg_db_role_setting WHERE setdatabase = pg_database.oid
                    ) OR EXISTS (
                        SELECT 1 FROM pg_class WHERE reltablespace <> 0 AND relisshared = false
                    ) OR EXISTS (
                        SELECT 1 FROM pg_subscription WHERE subdbid = pg_database.oid
                    ) OR shobj_description(oid, 'pg_database') IS NOT NULL)::int
            FROM pg_database WHERE datname = current_database();
            ''', capture=True)
        if metadata != '0':
            raise RuntimeError('The database has custom metadata or subscriptions that automated cloning cannot preserve')

    def clone_database(self, name):
        self.sql('postgres', f'CREATE DATABASE {self.ident(name)} WITH TEMPLATE {self.ident(self.database)};',
                 timeout=1800)

    def publish_database(self, journal):
        clone = self.ident(journal['candidate_database'])
        previous = self.ident(journal['previous_database'])
        original = self.ident(self.database)
        self.sql('postgres', f'BEGIN; ALTER DATABASE {original} RENAME TO {previous}; '
                             f'ALTER DATABASE {clone} RENAME TO {original}; '
                             f'ALTER DATABASE {original} ALLOW_CONNECTIONS true; COMMIT;', timeout=120)

    def drop_candidate_database(self, name):
        self.sql('postgres', f'DROP DATABASE IF EXISTS {self.ident(name)};')

    def discard_candidate(self, name):
        matches = command('docker', 'ps', '-aq', '--filter', f'name=^/{name}$',
                          '--filter', f'label=io.movie-discovery.update-probe={name}', capture=True, timeout=15)
        if matches:
            command('docker', 'stop', '--time', '10', matches, timeout=30)
            command('docker', 'rm', matches, timeout=30)

    def probe_release(self, override, candidate_api, candidate_web, candidate_database):
        probe = ('node', '-e', "fetch('http://127.0.0.1:3000/api/health/ready')"
                 ".then(r=>r.json()).then(body=>{if(body.db!==true) process.exitCode=1})"
                 ".catch(()=>process.exitCode=1)")
        try:
            connection_key = 'ConnectionStrings__Default'
            source = self.services['api']['environment'][connection_key]
            prefix = f'Host=db;Port=5432;Database={self.database};'
            if not source.startswith(prefix):
                raise RuntimeError('Database connection string differs from the installed Compose topology')
            connection = f'Host=db;Port=5432;Database={candidate_database};' + source[len(prefix):]
            self.compose('run', '-d', '--no-deps', '--pull', 'never', '--name', candidate_api,
                         '--label', f'io.movie-discovery.update-probe={candidate_api}',
                         '--env', 'MovieDiscovery__DeploymentProbe=true', '--env', connection_key, 'api',
                         env={**os.environ, connection_key: connection}, override=override)
            self.compose('run', '-d', '--no-deps', '--pull', 'never', '--name', candidate_web,
                         '--label', f'io.movie-discovery.update-probe={candidate_web}',
                         '--env', f'API_SERVER_URL=http://{candidate_api}:5200', 'web', override=override)
            deadline = time.monotonic() + self.timeout
            while time.monotonic() < deadline:
                try:
                    command('docker', 'exec', candidate_web, *probe, timeout=8)
                    return
                except RuntimeError:
                    time.sleep(3)
            raise RuntimeError('Candidate web proxy did not pass readiness')
        finally:
            try:
                self.discard_candidate(candidate_web)
            finally:
                self.discard_candidate(candidate_api)

    def deploy(self, manifest):
        revision = manifest['revision']
        if self.pending.exists():
            raise RuntimeError(f'An interrupted release requires recovery, see {self.pending}')
        if self.active.exists() and json.loads(self.active.read_text())['revision'] == revision:
            print('The release is already active.')
            return
        if self.failed.exists() and json.loads(self.failed.read_text())['revision'] == revision:
            raise RuntimeError('The same release previously failed; inspect the server record before retrying')
        if self.topology() != manifest['topology']:
            raise RuntimeError('Production Compose or MinIO build configuration has changed; validate and install topology first')
        self.storage()
        self.verify_main(revision)
        previous = self.current_containers()
        self.readiness('web', attempts=12)
        history = self.history()
        if history != manifest['migrations'][:len(history)]:
            raise RuntimeError('The release is incompatible with the current Entity Framework migration history')
        schema_changes = history != manifest['migrations']
        if schema_changes:
            self.check_template_metadata()
        app = tuple(service for service in SERVICES if service in previous)
        prefix = f'{revision[:12]}-{uuid.uuid4().hex[:10]}'
        target = self.state / f'release-{prefix}.json'
        rollback = self.state / f'rollback-{prefix}.json'
        backup = self.backups / f'update-{prefix}'
        new_images = {service: f'{REGISTRY}{service}@{manifest["images"][service]}' for service in app}
        old_images = {service: f'moviediscovery-update-{service}:previous-{prefix}' for service in app}
        self.image_override(target, new_images)
        self.image_override(rollback, old_images)
        for service in app:
            command('docker', 'pull', new_images[service], timeout=900)
            self.labels(new_images[service], revision)
            command('docker', 'tag', previous[service], old_images[service])
        self.storage()
        self.verify_main(revision)
        size = int(self.sql(self.database, 'SELECT pg_database_size(current_database());', capture=True))
        self.require_capacity('backup', self.backups, size)
        if schema_changes:
            db_path = next(volume['source'] for volume in self.services['db']['volumes']
                           if volume['target'] == '/var/lib/postgresql/data')
            same_mount = self.config['mounts']['db'] == self.config['mounts']['backup']
            self.require_capacity('db', db_path, (3 if same_mount else 2) * size)
        journal = {'revision': revision, 'phase': 'freeze', 'rollback': str(rollback), 'target': str(target),
                   'services': app, 'backup': str(backup), 'probe_api': f'moviediscovery-update-api-{prefix}',
                   'probe_web': f'moviediscovery-update-web-{prefix}',
                   'candidate_database': f'md_probe_{prefix.replace("-", "_")}' if schema_changes else None,
                   'previous_database': f'md_pre_{prefix.replace("-", "_")}' if schema_changes else None,
                   'original_oid': self.database_oid(self.database)}
        database = journal['candidate_database'] or self.database
        atomic(self.pending, journal)
        try:
            self.compose('stop', *reversed(app))
            if schema_changes:
                self.allow_connections(self.database, False)
                self.writers_stopped(self.database)
                self.clone_database(database)
                journal['candidate_oid'] = self.database_oid(database)
            self.backup(backup, database, size)
            journal['phase'] = 'probe'
            atomic(self.pending, journal)
            self.probe_release(target, journal['probe_api'], journal['probe_web'], database)
            if self.history(database) != manifest['migrations']:
                raise RuntimeError('Post-deployment migration history differs from the published history')
            if schema_changes:
                self.allow_connections(database, False)
                self.writers_stopped(database)
            self.verify_main(revision)
            # The moment the candidate gets the production name, other clients can write to it.
            journal['phase'] = 'publish'
            atomic(self.pending, journal)
            if schema_changes:
                self.publish_database(journal)
                recovery = self.recovery_database(journal['previous_database'], backup)
                atomic(self.state / f'recovery-db-{prefix}.json', recovery)
            self.start(target, 'api')
            self.readiness('api')
            self.start(target, 'web')
            self.readiness('web', attempts=60)
            if 'bot' in app:
                self.start(target, 'bot')
                self.current_containers()['bot']
            atomic(self.active, {'revision': revision, 'override': str(target), 'rollback': str(rollback), 'backup': str(backup),
                                 'previous_database': journal['previous_database']})
        except Exception:
            atomic(self.failed, journal)
            if journal['phase'] != 'publish':
                if schema_changes:
                    if self.database_oid(self.database) != journal['original_oid']:
                        raise RuntimeError('The original database name points to an unexpected OID; inspect the release journal')
                    self.allow_connections(self.database, True)
                self.start(rollback, *app)
                self.readiness('web', attempts=60)
                if schema_changes:
                    self.drop_candidate_database(database)
                self.pending.unlink()
                print('Reinstated the previous application release.', flush=True)
            raise
        self.pending.unlink()
        print(f'Activated {revision}.', flush=True)
        try:
            self.discard_obsolete_image_refs({target, rollback})
            self.discard_obsolete_database_copies()
            self.discard_obsolete_archives(backup)
        except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
            print(f'Release activated. Storage housekeeping needs attention: {exc}', file=sys.stderr)


def main():
    if sys.argv[1:] != ['--deploy'] or os.geteuid() != 0:
        raise RuntimeError('Run the installed updater as root with --deploy')
    os.umask(0o077)
    os.chdir('/')
    os.environ.clear()
    os.environ.update(PATH='/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin', HOME='/root',
                      LC_ALL='C.UTF-8', GIT_CONFIG_GLOBAL='/dev/null', GIT_CONFIG_SYSTEM='/dev/null')
    trusted_path(CONFIG, private=True)
    trusted_path(TOKEN, private=True)
    trusted_path(RESOLVER)
    # Lock before resolving the immutable upstream artifact and before opening local state files.
    lock_path = CONFIG.with_name('deploy.lock')
    lock_fd = os.open(lock_path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
    with os.fdopen(lock_fd, 'a') as lock:
        trusted_path(lock_path, private=True)
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        token = TOKEN.read_text().strip()
        if not token:
            raise RuntimeError('The configured GitHub Actions token file is empty')
        resolver = runpy.run_path(str(RESOLVER))
        manifest = resolver['resolve'](token)
        if manifest is None:
            return
        validate_manifest(manifest)
        updater = Updater()
        require_storage(updater.state, updater.config['mounts']['state'], updater.config['reserve_gib']['state'])
        updater.deploy(manifest)


if __name__ == '__main__':
    try:
        main()
    except (KeyError, ValueError, TypeError, OSError, RuntimeError) as exc:
        sys.exit(f'Movie Discovery update stopped: {exc}')
