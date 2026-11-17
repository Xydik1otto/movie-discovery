#!/usr/bin/env bash
set -euo pipefail

# Read-only survey. Supply candidate PostgreSQL, MinIO and backup directories.
printf 'Host and storage\n'
hostname
uname -m
df -hT
lsblk -o NAME,SIZE,FSTYPE,MOUNTPOINTS
free -h
printf '\nHost routes\n'
ip -brief address
ip route
printf '\nDocker storage\n'
docker info --format '{{.DockerRootDir}}'
docker system df
printf '\nExisting services\n'
docker ps --format '{{.Names}} {{.Ports}} {{.Status}}'
printf '\nDocker subnets\n'
while IFS= read -r network; do
    docker network inspect "$network" --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}'
done < <(docker network ls --format '{{.Name}}')
printf '\nCompose version\n'
docker compose version

root_source=$(findmnt -n -o SOURCE -T /)
for candidate in "$@"; do
    if [[ "$candidate" != /* ]]; then
        printf 'Expected an absolute path, received %s\n' "$candidate" >&2
        exit 1
    fi
    probe=$candidate
    while [[ ! -e "$probe" ]]; do probe=$(dirname -- "$probe"); done
    printf '\nCandidate %s\n' "$candidate"
    findmnt -T "$probe" -o SOURCE,TARGET,FSTYPE,OPTIONS
    df -hT -- "$probe"
    candidate_source=$(findmnt -n -o SOURCE -T "$probe")
    if [[ "$candidate_source" == "$root_source" ]]; then
        printf 'This path shares the system filesystem. Review its free capacity before use.\n'
    fi
done
