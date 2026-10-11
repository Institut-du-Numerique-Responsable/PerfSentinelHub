import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


REPOSITORY = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("supply_chain_sync", REPOSITORY / "scripts" / "sync-supply-chain.py")
sync = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(sync)

OLD_SHA = "a" * 40
NEW_SHA = "b" * 40
OLD_DIGEST = "sha256:" + "c" * 64
NEW_DIGEST = "sha256:" + "d" * 64


def write(root: Path, relative: str, text: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def repository(root: Path, *, action=(OLD_SHA, "v1.0.0"), image=("10.0.1-aot", OLD_DIGEST), package="1.0.0", lock_hash="old-lock") -> None:
    write(root, ".github/workflows/ci.yml", f"jobs:\n  a:\n    steps:\n      - uses: example/tool/sub@{action[0]} # {action[1]}\n")
    write(root, "Dockerfile", f"FROM mcr.microsoft.com/dotnet/sdk:{image[0]}@{image[1]} AS build\n")
    write(
        root,
        "Directory.Packages.props",
        f'<Project><ItemGroup><PackageVersion Include="Example.Package" Version="{package}" /></ItemGroup></Project>',
    )
    write(root, ".config/dotnet-tools.json", json.dumps({"version": 1, "isRoot": True, "tools": {}}))
    lock = {"version": 2, "dependencies": {"net10.0": {"Example.Package": {
        "type": "Direct", "requested": f"[{package}, )", "resolved": package, "contentHash": lock_hash}}}}
    write(root, "Tests/packages.lock.json", json.dumps(lock))
    inventory = [
        {"name": "example/tool", "kind": "github-action", "version": "1.0.0", "digest_or_sha": OLD_SHA,
         "released_at": "2026-01-01T00:00:00Z", "source": "https://github.com/example/tool/releases/tag/v1.0.0", "reason": "r"},
        {"name": "mcr.microsoft.com/dotnet/sdk", "kind": "container", "version": "10.0.1-aot", "digest_or_sha": OLD_DIGEST,
         "released_at": "2026-01-01", "source": "https://mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0.1-aot", "reason": "r"},
        {"name": "Example.Package", "kind": "nuget", "version": "1.0.0", "digest_or_sha": "sha512-base64:old",
         "lock_content_hash": "old-lock", "released_at": "2026-01-01T00:00:00Z",
         "source": "https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.0.0.json", "reason": "r"},
    ]
    write(root, "config/supply-chain.json", json.dumps({"schema_version": 1, "inventory": inventory}, indent=2) + "\n")


def official(url: str):
    responses = {
        "https://api.github.com/repos/example/tool/commits/v1.1.0": {"sha": NEW_SHA},
        "https://api.github.com/repos/example/tool/releases/tags/v1.1.0": {
            "draft": False, "prerelease": False, "published_at": "2026-02-02T02:02:02Z"},
        sync.checker.DOTNET_RELEASES: {"releases": [{"release-date": "2026-02-10", "sdk": {"version": "10.0.1"}}]},
        "https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.1.0.json": {
            "listed": True, "catalogEntry": "https://api.nuget.org/v3/catalog0/data/example.package.1.1.0.json"},
        "https://api.nuget.org/v3/catalog0/data/example.package.1.1.0.json": {
            "version": "1.1.0", "packageHashAlgorithm": "SHA512", "packageHash": "new", "published": "2026-03-03T03:03:03Z"},
    }
    return responses[url], {}


class SyncSupplyChainTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        patcher = patch.object(sync.checker, "fetch_json", side_effect=official)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.addCleanup(self.directory.cleanup)

    def inventory(self) -> dict:
        payload = json.loads((self.root / "config/supply-chain.json").read_text(encoding="utf-8"))
        return {item["name"]: item for item in payload["inventory"]}

    def test_leaves_an_inventory_in_sync_untouched(self):
        repository(self.root)
        before = (self.root / "config/supply-chain.json").read_text(encoding="utf-8")

        self.assertEqual([], sync.sync(self.root))
        self.assertEqual(before, (self.root / "config/supply-chain.json").read_text(encoding="utf-8"))

    def test_refills_a_moved_action_from_its_release(self):
        repository(self.root, action=(NEW_SHA, "v1.1.0"))

        sync.sync(self.root)

        self.assertEqual(
            {"version": "1.1.0", "digest_or_sha": NEW_SHA, "released_at": "2026-02-02T02:02:02Z",
             "source": "https://github.com/example/tool/releases/tag/v1.1.0"},
            {key: self.inventory()["example/tool"][key] for key in ("version", "digest_or_sha", "released_at", "source")},
        )

    def test_rejects_an_action_whose_tag_resolves_elsewhere(self):
        repository(self.root, action=("e" * 40, "v1.1.0"))

        with self.assertRaisesRegex(ValueError, "does not resolve"):
            sync.sync(self.root)

    def test_refills_a_moved_package_from_the_catalog_and_the_lock(self):
        repository(self.root, package="1.1.0", lock_hash="new-lock")

        sync.sync(self.root)

        item = self.inventory()["Example.Package"]
        self.assertEqual(("1.1.0", "sha512-base64:new", "new-lock", "2026-03-03T03:03:03Z"),
                         (item["version"], item["digest_or_sha"], item["lock_content_hash"], item["released_at"]))

    def test_refills_a_rebuilt_image_with_the_dotnet_release_date(self):
        repository(self.root, image=("10.0.1-aot", NEW_DIGEST))

        with patch.object(sync, "registry_digest", return_value=NEW_DIGEST):
            sync.sync(self.root)

        item = self.inventory()["mcr.microsoft.com/dotnet/sdk"]
        self.assertEqual((NEW_DIGEST, "2026-02-10"), (item["digest_or_sha"], item["released_at"]))

    def test_rejects_an_image_the_registry_does_not_serve(self):
        repository(self.root, image=("10.0.1-aot", NEW_DIGEST))

        with patch.object(sync, "registry_digest", return_value=OLD_DIGEST), self.assertRaisesRegex(ValueError, "does not resolve"):
            sync.sync(self.root)

    def test_rejects_one_action_pinned_twice(self):
        repository(self.root)
        write(self.root, ".github/workflows/other.yml", f"jobs:\n  b:\n    steps:\n      - uses: example/tool@{NEW_SHA} # v1.1.0\n")

        with self.assertRaisesRegex(ValueError, "two different values"):
            sync.sync(self.root)


if __name__ == "__main__":
    unittest.main()
