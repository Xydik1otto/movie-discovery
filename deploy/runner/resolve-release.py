#!/usr/bin/env python3
import hashlib
import io
import json
import re
import urllib.parse
import urllib.request
import zipfile


REPOSITORY = 'Xydik1otto/movie-discovery'
API = f'https://api.github.com/repos/{REPOSITORY}'
WORKFLOW = 'build-server-release.yml'
MAX_ARCHIVE_SIZE = 128 * 1024


def github(token, path, limit=2 * 1024 * 1024):
    request = urllib.request.Request(API + path, headers={
        'Accept': 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        'User-Agent': 'movie-discovery-release-resolver',
    })
    request.add_unredirected_header('Authorization', 'Bearer ' + token)
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read(limit + 1)
    if len(data) > limit:
        raise ValueError('GitHub API response exceeded the expected size')
    return data


def github_json(token, path):
    return json.loads(github(token, path))


def main_revision(token):
    revision = github_json(token, '/git/ref/heads/main')['object']['sha']
    if not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise ValueError('Invalid main revision')
    return revision


def resolve(token):
    revision = main_revision(token)
    query = urllib.parse.urlencode({'event': 'push', 'branch': 'main', 'head_sha': revision, 'per_page': 5})
    runs = github_json(token, f'/actions/workflows/{WORKFLOW}/runs?{query}')['workflow_runs']
    runs = [run for run in runs if (
        run['event'] == 'push' and run['head_branch'] == 'main' and run['head_sha'] == revision
        and run['head_repository']['full_name'].casefold() == REPOSITORY.casefold()
        and run['path'] == f'.github/workflows/{WORKFLOW}'
    )]
    if not runs or runs[0]['conclusion'] != 'success':
        print('The current main revision has no successful production image build yet.')
        return None

    run_id = int(runs[0]['id'])
    artifacts = github_json(token, f'/actions/runs/{run_id}/artifacts?name=server-release')['artifacts']
    artifacts = [item for item in artifacts if item['name'] == 'server-release' and not item['expired']]
    if len(artifacts) != 1:
        raise ValueError('Expected one current server-release artifact')

    artifact = artifacts[0]
    archive = github(token, f'/actions/artifacts/{int(artifact["id"])}/zip', MAX_ARCHIVE_SIZE)
    digest = 'sha256:' + hashlib.sha256(archive).hexdigest()
    if digest != artifact['digest']:
        raise ValueError('GitHub artifact checksum verification failed')

    with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
        files = zipped.infolist()
        if len(files) != 1 or files[0].filename != 'server-release.json' or files[0].file_size > 16384:
            raise ValueError('Unexpected release artifact layout')
        manifest = json.loads(zipped.read(files[0]))
    if manifest['revision'] != revision or main_revision(token) != revision:
        print('The release was superseded while the artifact was being checked.')
        return None
    return manifest
