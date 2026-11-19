#!/usr/bin/env python3
"""Check the resolved Tailscale/VPS configuration without printing its secrets."""

import argparse
import ipaddress
import json
from pathlib import Path
import shutil
import subprocess
from urllib.parse import urlsplit


def placeholder(value):
    value = (value or "").strip()
    return not value or "REPLACE" in value.upper() or value.lower().startswith("changeme")


def check_config(config, check_storage):
    errors = []
    warnings = []
    services = config["services"]
    if set(services) != {"db", "minio", "api", "web", "bot"}:
        errors.append("Use docker-compose.funnel.yml with the five application services")
        return errors, warnings

    api = services["api"]["environment"]
    public_url = api.get("FRONT_PUBLIC_URL", "")
    try:
        address = urlsplit(public_url)
        valid_address = (
            address.scheme == "https" and address.hostname
            and "." in address.hostname and address.hostname != "localhost"
            and address.port in (None, 443)
            and not address.username and not address.password
            and address.path in ("", "/") and not address.query and not address.fragment
        )
    except ValueError:
        valid_address = False
    if placeholder(public_url) or not valid_address:
        errors.append("Set FRONT_PUBLIC_URL to the actual public HTTPS frontend origin")
    if services["bot"]["environment"].get("FRONT_PUBLIC_URL") != public_url:
        errors.append("API and bot public URLs must match")

    credentials = {
        "ADMIN_KEY": api.get("ADMIN_KEY"),
        "BOT_API_KEY": api.get("BOT_API_KEY"),
        "POSTGRES_PASSWORD": services["db"]["environment"].get("POSTGRES_PASSWORD"),
        "MINIO_MINIO_PASSWORD": services["minio"]["environment"].get("MINIO_ROOT_PASSWORD"),
    }
    for name, value in credentials.items():
        if placeholder(value):
            errors.append(f"Replace the empty or template {name}")
    if services["bot"]["environment"].get("BOT_API_KEY") != api.get("BOT_API_KEY"):
        errors.append("API and bot authentication keys must match")

    for name, service in services.items():
        for port in service.get("ports", []):
            if port.get("host_ip") not in ("127.0.0.1", "::1"):
                errors.append(f"Bind {name}'s host ports to loopback")
    if services["bot"].get("volumes"):
        errors.append("The public bot profile must run without host volume access")

    for service, target in (("db", "/var/lib/postgresql/data"), ("minio", "/data"),
                            ("api", "/data/keys"), ("api", "/data/catalog-cache")):
        mounts = [mount for mount in services[service].get("volumes", [])
                  if mount.get("target") == target]
        if len(mounts) != 1 or mounts[0].get("type") != "bind":
            errors.append(f"Configure persistent bind storage for {service} at {target}")
            continue
        mount = mounts[0]
        source = mount.get("source", "")
        if placeholder(source) or not source.startswith("/"):
            errors.append(f"Set an absolute Linux data path for {service} at {target}")
        if mount.get("bind", {}).get("create_host_path") is not False:
            errors.append(f"Disable automatic creation of {service}'s storage path at {target}")
        if check_storage and not Path(source).is_dir():
            errors.append(f"Create and verify the chosen storage directory for {service} at {target}")
        elif check_storage:
            free = shutil.disk_usage(source).free
            if free < 20 * 1024 ** 3:
                warnings.append(f"{service} storage has {free / 1024 ** 3:.1f} GiB free; review growth and restore headroom")

    for cidr in api.get("MD_TRUSTED_NETWORKS", "").split(","):
        try:
            ipaddress.ip_network(cidr, strict=False)
        except ValueError:
            errors.append("MD_TRUSTED_NETWORKS contains an invalid network")

    if api.get("MAIL_ENABLED", "").lower() == "true":
        for name in ("SMTP_HOST", "MAIL_FROM"):
            if placeholder(api.get(name)):
                errors.append(f"Configure {name} for email delivery")
        if api.get("SMTP_USER") and placeholder(api.get("SMTP_USER")):
            errors.append("Configure SMTP_USER for the selected SMTP account")
        if api.get("SMTP_USER") and placeholder(api.get("SMTP_PASSWORD")):
            errors.append("Configure SMTP_PASSWORD for the selected SMTP account")
        warnings.append("Verify actual confirmation and password-reset delivery before opening registration")
    else:
        warnings.append("Email delivery is disabled; password-reset messages will not reach users")
    return errors, warnings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--env-file", default="deploy/funnel.env")
    parser.add_argument("--skip-storage-check", action="store_true",
                        help="Validate configuration off-server, without inspecting mounted data paths")
    args = parser.parse_args()
    result = subprocess.run([
        "docker", "compose", "--env-file", args.env_file, "-f", "docker-compose.funnel.yml",
        "config", "--format", "json",
    ], capture_output=True, text=True)
    if result.returncode:
        print("ERROR: Compose could not resolve the configuration. Check the env file and required variables.")
        raise SystemExit(1)
    errors, warnings = check_config(json.loads(result.stdout), not args.skip_storage_check)
    for message in warnings:
        print(f"REVIEW: {message}")
    for message in errors:
        print(f"ERROR: {message}")
    if errors:
        raise SystemExit(1)
    print("Configuration checks passed. Public routing, mail delivery and backups still need live verification.")


if __name__ == "__main__":
    main()
