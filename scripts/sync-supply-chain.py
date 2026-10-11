#!/usr/bin/env python3
"""Bring config/supply-chain.json in line with the pins a dependency update moved.

Dependabot rewrites the workflow, Dockerfile, NuGet and tool declarations but not the
inventory. Each moved entry is refilled from the same official source the online check
reads, then the offline check runs on the result.
"""

import importlib.util
import json
import re
import sys
from pathlib import Path
from urllib.parse import quote
from urllib.request import Request, urlopen
from xml.etree import ElementTree

SPEC = importlib.util.spec_from_file_location("supply_chain_checker", Path(__file__).with_name("check-supply-chain.py"))
checker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(checker)

ACTION_PIN = re.compile(
    rf"^\s+- uses: (?P<name>{checker.SAFE_RELATIVE_PATH})@(?P<sha>[0-9a-f]{{40}}) # (?P<tag>{checker.SAFE_NAME})$"
)
OWN_NAMESPACE = "institut-du-numerique-responsable"
INVENTORY = Path("config") / "supply-chain.json"


def pin(found: dict, name: str, value: tuple, where: str) -> None:
    if found.setdefault(name, value) != value:
        raise ValueError(f"{name} is pinned to two different values, see {where}")


def declared_pins(root: Path) -> dict:
    """Every pin the repository declares, keyed by inventory name."""
    found: dict = {}
    for path in sorted((root / ".github" / "workflows").glob("*.yml")):
        for line in path.read_text(encoding="utf-8").splitlines():
            if match := ACTION_PIN.fullmatch(line):
                repository = "/".join(match.group("name").split("/")[:2])
                pin(found, repository, (match.group("tag"), match.group("sha")), path.name)
    dockerfile = root / "Dockerfile"
    for line in dockerfile.read_text(encoding="utf-8").splitlines():
        if (match := checker.FROM_LINE.match(line)) and "@" in match.group(1):
            reference, digest = match.group(1).split("@", 1)
            image, tag = reference.rsplit(":", 1)
            pin(found, image, (tag, digest), dockerfile.name)
    central = ElementTree.parse(root / "Directory.Packages.props").getroot()
    for element in central.iter("PackageVersion"):
        pin(found, element.get("Include"), (element.get("Version"), None), "Directory.Packages.props")
    tools = json.loads((root / ".config" / "dotnet-tools.json").read_text(encoding="utf-8"))
    for name, tool in tools["tools"].items():
        pin(found, name, (tool["version"], None), "dotnet-tools.json")
    return found


def lock_hashes(root: Path) -> dict:
    """Direct dependency content hashes, keyed by (casefolded name, version)."""
    hashes = {}
    for path in sorted(root.glob("*/packages.lock.json")):
        for entries in json.loads(path.read_text(encoding="utf-8"))["dependencies"].values():
            for name, record in entries.items():
                if record.get("type") == "Direct":
                    hashes[(name.casefold(), record["resolved"])] = record["contentHash"]
    return hashes


def github_json(path: str) -> dict:
    return checker.fetch_json(f"https://api.github.com/repos/{path}")[0]


def github_release(repository: str, tag: str) -> dict:
    release = github_json(f"{repository}/releases/tags/{quote(tag)}")
    if release.get("draft") is not False or release.get("prerelease") is not False:
        raise ValueError(f"{repository} {tag} is not a stable published release")
    return release


def sync_action(item: dict, tag: str, sha: str) -> None:
    commit = github_json(f"{item['name']}/commits/{quote(tag)}")
    if commit.get("sha", "").lower() != sha.lower():
        raise ValueError(f"{item['name']}: {tag} does not resolve to {sha}")
    item.update(
        version=tag.lstrip("v"),
        digest_or_sha=sha,
        released_at=github_release(item["name"], tag)["published_at"],
        source=f"https://github.com/{item['name']}/releases/tag/{tag}",
    )


def registry_digest(source: str, repository: str) -> str | None:
    headers = checker.request_headers(
        source,
        "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json",
    )
    if source.startswith("https://ghcr.io/"):
        token_url = f"https://ghcr.io/token?scope=repository:{repository}:pull&service=ghcr.io"
        headers["Authorization"] = f"Bearer {checker.fetch_json(token_url)[0]['token']}"
    with urlopen(Request(source, headers=headers), timeout=20, context=checker.trusted_context()) as response:  # nosec B310: fixed official registries
        return response.headers.get("Docker-Content-Digest")


def sync_container(item: dict, tag: str, digest: str) -> None:
    registry, repository = item["name"].split("/", 1)
    source = f"https://{registry}/v2/{repository}/manifests/{tag}"
    if (registry_digest(source, repository) or "").lower() != digest.lower():
        raise ValueError(f"{item['name']}: {tag} does not resolve to {digest}")
    if registry == "mcr.microsoft.com":
        release = checker.dotnet_container_release({**item, "version": tag}, checker.fetch_json(checker.DOTNET_RELEASES)[0])
        if release is None:
            raise ValueError(f"{item['name']}: .NET metadata does not list {tag}")
        released_at = release["release-date"]
    elif registry == "ghcr.io" and repository.startswith(f"{OWN_NAMESPACE}/"):
        project = repository.split("/", 1)[1]
        released_at = github_release(f"Institut-du-Numerique-Responsable/{project}", f"v{tag}")["published_at"][:10]
    else:
        raise ValueError(f"{item['name']}: no official release date source")
    item.update(version=tag, digest_or_sha=digest, released_at=released_at, source=source)


def sync_nuget(item: dict, version: str, lock: dict) -> None:
    source = f"https://api.nuget.org/v3/registration5-gz-semver2/{item['name'].lower()}/{version.lower()}.json"
    registration = checker.fetch_json(source)[0]
    catalog = checker.fetch_json(registration["catalogEntry"])[0]
    if registration.get("listed") is not True or catalog.get("version") != version or catalog.get("packageHashAlgorithm") != "SHA512":
        raise ValueError(f"{item['name']}: {version} is not a listed SHA512 NuGet release")
    item.update(
        version=version,
        digest_or_sha=f"sha512-base64:{catalog['packageHash']}",
        released_at=catalog["published"],
        source=source,
    )
    if "lock_content_hash" in item:
        content_hash = lock.get((item["name"].casefold(), version))
        if content_hash is None:
            raise ValueError(f"{item['name']}: no direct lock entry for {version}")
        item["lock_content_hash"] = content_hash


def sync(root: Path) -> list[str]:
    """Refill every inventory entry whose declared pin moved, return what changed."""
    path = root / INVENTORY
    payload = json.loads(path.read_text(encoding="utf-8"))
    declared = declared_pins(root)
    lock = lock_hashes(root)
    changes = []
    for item in payload["inventory"]:
        if item["name"] not in declared or item.get("nuget_role"):
            continue
        version, digest = declared[item["name"]]
        before = (item["version"], item["digest_or_sha"], item.get("lock_content_hash"))
        if item["kind"] == "github-action" and digest.lower() != item["digest_or_sha"].lower():
            sync_action(item, version, digest)
        elif item["kind"] == "container" and (version, digest.lower()) != (item["version"], item["digest_or_sha"].lower()):
            sync_container(item, version, digest)
        elif item["kind"] in ("nuget", "dotnet-tool") and (
            version != item["version"] or lock.get((item["name"].casefold(), version), item.get("lock_content_hash")) != item.get("lock_content_hash")
        ):
            sync_nuget(item, version, lock)
        if (item["version"], item["digest_or_sha"], item.get("lock_content_hash")) != before:
            moved = f"{before[0]} -> {item['version']}" if before[0] != item["version"] else f"{item['version']} rebuilt"
            changes.append(f"{item['name']}: {moved}, {item['digest_or_sha'][:19]}")
    if changes:
        path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return changes


def main() -> int:
    root = Path.cwd()
    try:
        changes = sync(root)
    except (OSError, KeyError, ValueError) as error:
        print(f"unable to sync the inventory: {error}", file=sys.stderr)
        return 1
    for change in changes:
        print(change)
    inventory = json.loads((root / INVENTORY).read_text(encoding="utf-8"))["inventory"]
    errors = checker.validate_inventory(inventory) + checker.validate_declarations(root, inventory)
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
