#!/usr/bin/env python3
import hashlib
import json
import re
import sys
from pathlib import Path


def fingerprint(paths):
    entries = ''.join(f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.as_posix()}\n' for path in sorted(paths))
    return hashlib.sha256(entries.encode()).hexdigest()


def main():
    revision, directory = sys.argv[1:]
    if not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise ValueError('Invalid source revision')

    directory = Path(directory)
    images = {}
    for service in ('api', 'web', 'bot'):
        digest = (directory / f'{service}.digest').read_text().strip()
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
            raise ValueError(f'Invalid image digest for {service}')
        images[service] = digest

    migrations = sorted(
        path.stem
        for path in Path('backend/src/MovieDiscovery.Infrastructure/Migrations').glob('*.cs')
        if re.fullmatch(r'[0-9]{14}_[A-Za-z0-9_]+', path.stem)
    )
    if not migrations:
        raise ValueError('No Entity Framework migrations found')

    topology_files = tuple(map(Path, (
        'docker-compose.yml', 'docker-compose.server.yml', 'docker-compose.funnel.yml', 'minio/Dockerfile',
    )))
    manifest = {
        'version': 1,
        'revision': revision,
        'images': images,
        'topology': fingerprint(topology_files),
        'migrations': migrations,
    }
    (directory / 'server-release.json').write_text(json.dumps(manifest, separators=(',', ':')) + '\n')


if __name__ == '__main__':
    main()
