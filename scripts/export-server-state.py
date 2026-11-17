#!/usr/bin/env python3
"""Export the local MovieDiscovery database, posters and private configuration."""

import argparse
import csv
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
from datetime import datetime, timezone


ROOT = Path(__file__).resolve().parents[1]


def inspect_container(name):
    return json.loads(subprocess.check_output(["docker", "inspect", name]))[0]


def private_directory(path):
    path.mkdir(parents=True, exist_ok=False, mode=0o700)
    if os.name == "nt":
        identity = subprocess.check_output(
            ["whoami", "/user", "/fo", "csv", "/nh"], text=True
        )
        sid = next(csv.reader(io.StringIO(identity.strip())))[1]
        subprocess.run(
            ["icacls", str(path), "/inheritance:r", "/grant:r",
             f"*{sid}:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F"],
            check=True, stdout=subprocess.DEVNULL,
        )


def export_command(path, command):
    with path.open("xb") as target:
        subprocess.run(command, stdout=target, check=True)


def save_private_config(output, env_file=None):
    def permissions(member):
        member.uid = member.gid = 0
        member.uname = member.gname = "root"
        member.mode = 0o700 if member.isdir() else 0o600
        return member

    key_mounts = [mount for mount in inspect_container("moviediscovery-api")["Mounts"]
                  if mount["Destination"] == "/data/keys" and mount["Type"] == "bind"]
    if len(key_mounts) != 1:
        raise RuntimeError("Expected the API's persistent DataProtection bind mount")
    with tarfile.open(output / "config-private.tar.gz", "w:gz") as archive:
        archive.add(env_file or ROOT / ".env", arcname=".env", filter=permissions)
        archive.add(Path(key_mounts[0]["Source"]), arcname="data/keys", filter=permissions)


def save_database_counts(output):
    sql = r"""SELECT format('SELECT %L, count(*) FROM %I.%I;', table_name, table_schema, table_name)
FROM information_schema.tables
WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name
\gexec
"""
    result = subprocess.check_output([
        "docker", "exec", "-i", "moviediscovery-db", "sh", "-c",
        'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -F "|" -v ON_ERROR_STOP=1',
    ], input=sql.encode())
    counts = {}
    for row in result.decode().splitlines():
        table, count = row.split("|")
        counts[table] = int(count)
    (output / "database-counts.json").write_text(
        json.dumps(counts, indent=2), encoding="utf-8",
    )


def write_manifest(output, final_snapshot):
    entries = []
    for path in sorted(output.iterdir()):
        if not path.is_file() or path.name in ("manifest.json", "SHA256SUMS"):
            continue
        with path.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        entries.append({"file": path.name, "bytes": path.stat().st_size, "sha256": digest})
    (output / "SHA256SUMS").write_text(
        "".join(f"{entry['sha256']}  {entry['file']}\n" for entry in entries),
        encoding="utf-8",
    )
    (output / "manifest.json").write_text(json.dumps({
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "finalSnapshot": final_snapshot,
        "entries": entries,
    }, indent=2), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New private backup directory")
    parser.add_argument("--env-file", type=Path, default=ROOT / ".env",
                        help="Actual runtime env file to include in the private backup")
    parser.add_argument("--final", action="store_true", help="Require stopped source writers")
    args = parser.parse_args()

    database = inspect_container("moviediscovery-db")
    posters = inspect_container("moviediscovery-minio")
    if not database["State"]["Running"]:
        raise RuntimeError("Source PostgreSQL must be running for pg_dump")
    if args.final:
        for name in ("moviediscovery-api", "moviediscovery-web", "moviediscovery-bot",
                     "moviediscovery-tunnel", "moviediscovery-minio"):
            if inspect_container(name)["State"]["Running"]:
                raise RuntimeError(f"Stop source writer before final export: {name}")
    elif not posters["State"]["Running"]:
        raise RuntimeError("Use --final to export a stopped MinIO")

    output = args.output.resolve()
    private_directory(output)
    export_command(output / "moviediscovery.dump", [
        "docker", "exec", "moviediscovery-db", "sh", "-c",
        'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc',
    ])
    export_command(output / "globals.sql", [
        "docker", "exec", "moviediscovery-db", "sh", "-c",
        'exec pg_dumpall -U "$POSTGRES_USER" --globals-only',
    ])
    poster_command = (
        ["docker", "run", "--rm", "--name", "movie-transfer-export-minio",
         "--label", "codex.purpose=movie-transfer-export",
         "--network", "none", "--volumes-from", "moviediscovery-minio:ro",
         "alpine:latest", "tar", "-C", "/data", "-cf", "-", "."]
        if args.final else
        ["docker", "exec", "moviediscovery-minio", "tar", "-C", "/data", "-cf", "-", "."]
    )
    export_command(output / "minio.tar", poster_command)
    save_private_config(output, args.env_file)
    save_database_counts(output)
    write_manifest(output, args.final)
    print(f"Export completed in {output}")


if __name__ == "__main__":
    main()
