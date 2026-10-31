#!/usr/bin/env python3
"""Compare two production snapshots by state identity and logical WinGet data."""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from pathlib import Path
from typing import Any

import yaml

from build_v2_fixture import decompress_mszip


def canonical(value: Any) -> Any:
    if isinstance(value, dict):
        return {key: canonical(value[key]) for key in sorted(value)}
    if isinstance(value, list):
        return [canonical(item) for item in value]
    return value


def snapshot_metadata(snapshot: Path) -> dict[str, Any]:
    return json.loads((snapshot / "snapshot.json").read_text(encoding="utf-8"))


def read_index(snapshot: Path) -> dict[str, Any]:
    database_path = snapshot / "Public" / "index.db"
    with sqlite3.connect(f"file:{database_path.as_posix()}?mode=ro", uri=True) as db:
        packages = {}
        for package_id, name, moniker, version, arp_min, arp_max in db.execute(
            "SELECT id,name,moniker,latest_version,arp_min_version,arp_max_version FROM packages"
        ):
            packages[package_id] = {
                "name": name,
                "moniker": moniker,
                "latestVersion": version,
                "arpMinimum": arp_min,
                "arpMaximum": arp_max,
                "tags": mapped_values(db, package_id, "tags2", "tag", "tags2_map", "tag"),
                "commands": mapped_values(db, package_id, "commands2", "command", "commands2_map", "command"),
                "pfns": correlation_values(db, package_id, "pfns2", "pfn"),
                "productCodes": correlation_values(db, package_id, "productcodes2", "productcode"),
                "upgradeCodes": correlation_values(db, package_id, "upgradecodes2", "upgradecode"),
                "normalizedNames": correlation_values(db, package_id, "norm_names2", "norm_name"),
                "normalizedPublishers": correlation_values(db, package_id, "norm_publishers2", "norm_publisher"),
            }
    return packages


def mapped_values(
    db: sqlite3.Connection,
    package_id: str,
    value_table: str,
    value_column: str,
    map_table: str,
    map_column: str,
) -> list[str]:
    query = (
        f"SELECT valuesTable.{value_column} FROM {value_table} AS valuesTable "
        f"JOIN {map_table} AS valueMap ON valueMap.{map_column}=valuesTable.rowid "
        "JOIN packages AS package ON package.rowid=valueMap.package "
        f"WHERE package.id=? ORDER BY valuesTable.{value_column}"
    )
    return [row[0] for row in db.execute(query, (package_id,))]


def correlation_values(
    db: sqlite3.Connection, package_id: str, table: str, column: str
) -> list[str]:
    query = (
        f"SELECT value.{column} FROM {table} AS value "
        "JOIN packages AS package ON package.rowid=value.package "
        f"WHERE package.id=? ORDER BY value.{column}"
    )
    return [row[0] for row in db.execute(query, (package_id,))]


def read_version_data(snapshot: Path) -> dict[str, Any]:
    database_path = snapshot / "Public" / "index.db"
    feed = snapshot / "feed"
    with sqlite3.connect(f"file:{database_path.as_posix()}?mode=ro", uri=True) as db:
        rows = db.execute("SELECT id,hash FROM packages").fetchall()
    result: dict[str, Any] = {}
    for package_id, package_hash in rows:
        sidecar = (
            feed
            / "packages"
            / package_id
            / package_hash.hex()[:8]
            / "versionData.mszyml"
        )
        document = yaml.safe_load(
            decompress_mszip(sidecar.read_bytes()).decode("utf-8")
        )
        if not isinstance(document, dict) or not isinstance(document.get("vD"), list):
            raise AssertionError(f"invalid version sidecar for package {package_id}")
        result[package_id] = canonical(document)
    return result


def read_compressed_sidecars(snapshot: Path) -> dict[str, tuple[str, bytes]]:
    database_path = snapshot / "Public" / "index.db"
    feed = snapshot / "feed"
    with sqlite3.connect(f"file:{database_path.as_posix()}?mode=ro", uri=True) as db:
        rows = db.execute("SELECT id,hash FROM packages").fetchall()
    result: dict[str, tuple[str, bytes]] = {}
    for package_id, package_hash in rows:
        digest = package_hash.hex()
        sidecar = (
            feed
            / "packages"
            / package_id
            / digest[:8]
            / "versionData.mszyml"
        )
        result[package_id] = (digest, sidecar.read_bytes())
    return result


def read_merged_manifests(snapshot: Path) -> dict[str, Any]:
    feed = snapshot / "feed"
    with sqlite3.connect(f"file:{(snapshot / 'Public' / 'index.db').as_posix()}?mode=ro", uri=True) as db:
        hashes = [
            (package_id, package_hash.hex())
            for package_id, package_hash in db.execute("SELECT id,hash FROM packages")
        ]
    result: dict[str, Any] = {}
    for package_id, package_hash in hashes:
        sidecar = feed / "packages" / package_id / package_hash[:8] / "versionData.mszyml"
        version_data = decompress_mszip(sidecar.read_bytes()).decode("utf-8")
        records: set[str] = set()
        for document in yaml.safe_load_all(version_data):
            if not isinstance(document, dict):
                continue
            records.update(
                item["rP"]
                for item in document.get("vD", [])
                if isinstance(item, dict) and isinstance(item.get("rP"), str)
            )
        for relative in records:
            manifest_path = feed / Path(relative.replace("/", "\\"))
            for manifest in yaml.safe_load_all(manifest_path.read_text(encoding="utf-8")):
                if not isinstance(manifest, dict):
                    continue
                key = "|".join(
                    (
                        str(manifest.get("PackageIdentifier", "")),
                        str(manifest.get("PackageVersion", "")),
                        str(manifest.get("PackageLocale", "")),
                    )
                )
                if key in result:
                    raise AssertionError(f"duplicate merged manifest identity: {key}")
                result[key] = canonical(manifest)
    return result


def read_merged_manifest_bytes(snapshot: Path) -> dict[str, bytes]:
    result: dict[str, bytes] = {}
    for path in (snapshot / "feed" / "manifests").rglob("*.yaml"):
        raw = path.read_bytes()
        document = yaml.safe_load(raw)
        if not isinstance(document, dict):
            raise AssertionError(f"merged manifest is not a mapping: {path}")
        key = "|".join(
            (
                str(document.get("PackageIdentifier", "")),
                str(document.get("PackageVersion", "")),
                str(document.get("PackageLocale", "")),
            )
        )
        if key in result:
            raise AssertionError(f"duplicate merged manifest identity: {key}")
        result[key] = raw
    return result


def compare(left: Path, right: Path) -> dict[str, Any]:
    left = left.resolve(strict=True)
    right = right.resolve(strict=True)
    left_meta = snapshot_metadata(left)
    right_meta = snapshot_metadata(right)
    if left_meta.get("artifactFormatVersion") != 2 or right_meta.get("artifactFormatVersion") != 2:
        raise AssertionError("both snapshots must use artifactFormatVersion 2")
    if left_meta.get("buildId") != right_meta.get("buildId"):
        raise AssertionError("Rust and .NET build identifiers differ for identical inputs")
    if left_meta.get("inventory") != right_meta.get("inventory"):
        raise AssertionError("canonical raw input inventories differ")
    left_index = read_index(left)
    right_index = read_index(right)
    if canonical(left_index) != canonical(right_index):
        raise AssertionError("WinGet logical database rows or correlation mappings differ")
    left_sidecars = read_compressed_sidecars(left)
    right_sidecars = read_compressed_sidecars(right)
    if left_sidecars != right_sidecars:
        differing = sorted(
            package_id
            for package_id in left_sidecars.keys() | right_sidecars.keys()
            if left_sidecars.get(package_id) != right_sidecars.get(package_id)
        )
        raise AssertionError(
            "compressed version sidecar bytes or package hash differ: "
            + ", ".join(differing)
        )
    left_version_data = read_version_data(left)
    right_version_data = read_version_data(right)
    if canonical(left_version_data) != canonical(right_version_data):
        raise AssertionError("WinGet version sidecar data differs between implementations")
    left_manifests = read_merged_manifests(left)
    right_manifests = read_merged_manifests(right)
    if canonical(left_manifests) != canonical(right_manifests):
        raise AssertionError("canonical merged manifest data differs between implementations")
    left_manifest_bytes = read_merged_manifest_bytes(left)
    right_manifest_bytes = read_merged_manifest_bytes(right)
    if left_manifest_bytes != right_manifest_bytes:
        differing = sorted(
            key
            for key in left_manifest_bytes.keys() | right_manifest_bytes.keys()
            if left_manifest_bytes.get(key) != right_manifest_bytes.get(key)
        )
        raise AssertionError("raw merged manifest bytes differ: " + ", ".join(differing))
    return {
        "buildId": left_meta["buildId"],
        "packages": len(left_index),
        "manifests": len(left_manifests),
        "logicalIndex": "equal",
        "compressedSidecarBytes": "equal",
        "versionData": "equal",
        "canonicalManifests": "equal",
        "rawMergedManifestBytes": "equal",
        "rawInputInventory": "equal",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rust-snapshot", type=Path, required=True)
    parser.add_argument("--dotnet-snapshot", type=Path, required=True)
    args = parser.parse_args()
    try:
        print(
            json.dumps(
                compare(args.rust_snapshot, args.dotnet_snapshot),
                sort_keys=True,
            )
        )
    except Exception as error:
        print(f"cross-language snapshot comparison failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
