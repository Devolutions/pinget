#!/usr/bin/env python3
"""Independently validate a Pinget build snapshot with sqlite3 and native MSZIP."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sqlite3
import sys
from pathlib import Path

import yaml

from build_v2_fixture import decompress_mszip


EXPECTED_TABLES = {
    "metadata",
    "packages",
    "tags2",
    "tags2_map",
    "commands2",
    "commands2_map",
    "pfns2",
    "productcodes2",
    "norm_names2",
    "norm_publishers2",
    "upgradecodes2",
}


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def contained_file(root: Path, relative: str) -> Path:
    root = root.resolve(strict=True)
    path = (root / Path(relative.replace("/", "\\"))).resolve(strict=True)
    if not path.is_relative_to(root) or not path.is_file():
        raise AssertionError(f"artifact path is not a contained file: {relative}")
    return path


def version_records(text: str) -> list[dict[str, object]]:
    document = yaml.safe_load(text)
    if not isinstance(document, dict) or document.get("sV") != "1.0":
        raise AssertionError("version sidecar has an invalid document or schema marker")
    records = document.get("vD")
    if not isinstance(records, list) or not records:
        raise AssertionError("version sidecar has no vD records")
    for record in records:
        if not isinstance(record, dict):
            raise AssertionError("version sidecar contains a non-object vD record")
        if not all(isinstance(record.get(key), str) for key in ("v", "rP", "s256H")):
            raise AssertionError("version sidecar record lacks string v, rP, or s256H fields")
        if not re.fullmatch(r"[0-9a-f]{64}", record["s256H"]):
            raise AssertionError("version sidecar contains an invalid lowercase SHA-256")
    return records


def validate(snapshot: Path, expected_package: str | None) -> dict[str, object]:
    snapshot = snapshot.resolve(strict=True)
    metadata_path = snapshot / "snapshot.json"
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if metadata.get("schemaVersion") != 1:
        raise AssertionError("snapshot schemaVersion is not 1")
    if metadata.get("artifactFormatVersion") != 2:
        raise AssertionError("snapshot artifactFormatVersion is not 2")
    if not metadata.get("buildId"):
        raise AssertionError("snapshot buildId is missing")

    feed = snapshot / "feed"
    artifact_inventory = metadata.get("artifacts")
    if not isinstance(artifact_inventory, dict):
        raise AssertionError("snapshot artifacts must be a JSON object")
    for relative, expected_hash in artifact_inventory.items():
        path = contained_file(feed, relative)
        if digest(path) != expected_hash:
            raise AssertionError(f"artifact hash mismatch: {relative}")

    index = snapshot / "Public" / "index.db"
    with sqlite3.connect(f"file:{index.as_posix()}?mode=ro", uri=True) as db:
        integrity = db.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            raise AssertionError(f"SQLite integrity_check returned {integrity!r}")
        tables = {
            row[0]
            for row in db.execute(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
            )
        }
        if tables != EXPECTED_TABLES:
            raise AssertionError(f"unexpected WinGet v2 table set: {sorted(tables)}")
        metadata_rows = dict(db.execute("SELECT name,value FROM metadata"))
        if (metadata_rows.get("majorVersion"), metadata_rows.get("minorVersion")) != ("2", "0"):
            raise AssertionError("WinGet schema markers are not 2.0")
        packages = db.execute(
            "SELECT id,name,latest_version,hash,arp_min_version,arp_max_version "
            "FROM packages ORDER BY id COLLATE NOCASE"
        ).fetchall()
        if not packages:
            raise AssertionError("the production snapshot contains no packages")
        if expected_package is not None and expected_package not in {row[0] for row in packages}:
            raise AssertionError(f"expected package {expected_package!r} is absent")

        decoded_versions: dict[str, list[str]] = {}
        for package_id, _name, latest_version, package_hash, _, _ in packages:
            if package_hash is None or len(package_hash) != 32:
                raise AssertionError(f"package {package_id} has no 32-byte sidecar hash")
            full_hash = package_hash.hex()
            sidecar_relative = (
                f"packages/{package_id}/{full_hash[:8]}/versionData.mszyml"
            )
            sidecar_path = contained_file(feed, sidecar_relative)
            if digest(sidecar_path) != full_hash:
                raise AssertionError(f"package {package_id} sidecar hash/path is invalid")
            raw_version_data = decompress_mszip(sidecar_path.read_bytes())
            version_text = raw_version_data.decode("utf-8")
            if not version_text.startswith('sV: "1.0"\n'):
                raise AssertionError(f"package {package_id} has an unexpected sidecar header")
            records = version_records(version_text)
            record_versions = [record["v"] for record in records]
            if len(record_versions) != len(set(record_versions)):
                raise AssertionError(f"package {package_id} has duplicate version records")
            if record_versions[0] != latest_version:
                raise AssertionError(
                    f"package {package_id} sidecar does not place latest version first"
                )
            for record in records:
                version = record["v"]
                manifest_relative = record["rP"]
                manifest_hash = record["s256H"]
                manifest_path = contained_file(feed, manifest_relative)
                if digest(manifest_path) != manifest_hash:
                    raise AssertionError(
                        f"package {package_id} manifest hash mismatch for {version}"
                    )
            if latest_version not in {record["v"] for record in records}:
                raise AssertionError(f"latest version missing from sidecar for {package_id}")
            arp_minimum, arp_maximum = next(
                row[4:6] for row in packages if row[0] == package_id
            )
            sidecar_minima = {
                record.get("aMiV")
                for record in records
                if isinstance(record.get("aMiV"), str)
            }
            sidecar_maxima = {
                record.get("aMaV")
                for record in records
                if isinstance(record.get("aMaV"), str)
            }
            if arp_minimum is not None and arp_minimum not in sidecar_minima:
                raise AssertionError(f"minimum ARP version missing from sidecar for {package_id}")
            if arp_maximum is not None and arp_maximum not in sidecar_maxima:
                raise AssertionError(f"maximum ARP version missing from sidecar for {package_id}")
            if package_id == "Pinget.Test.Matrix":
                expected_arp = {
                    "1.9.0": ("1.9.0", "1.9.0"),
                    "1.10.0": ("1.10.0", "1.10.0"),
                }
                actual_arp = {
                    record["v"]: (record.get("aMiV"), record.get("aMaV"))
                    for record in records
                }
                if actual_arp != expected_arp or (arp_minimum, arp_maximum) != (
                    "1.9.0",
                    "1.10.0",
                ):
                    raise AssertionError(
                        f"per-version ARP metadata or package bounds are incorrect for {package_id}"
                    )
            decoded_versions[package_id] = record_versions

    return {
        "snapshot": str(snapshot),
        "buildId": metadata["buildId"],
        "packages": {row[0]: row[2] for row in packages},
        "versions": decoded_versions,
        "artifacts": len(artifact_inventory),
        "sqlite": "ok",
        "mszip": "native Windows Compression API",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot", type=Path, required=True)
    parser.add_argument("--expect-package")
    args = parser.parse_args()
    try:
        print(json.dumps(validate(args.snapshot, args.expect_package), sort_keys=True))
    except Exception as error:
        print(f"snapshot validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
