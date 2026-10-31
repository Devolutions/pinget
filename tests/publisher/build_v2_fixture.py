#!/usr/bin/env python3
"""Build and self-check a locally authored WinGet schema 2.0 source fixture."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import sqlite3
import subprocess
import sys
import unittest
import uuid
import zipfile
from pathlib import Path
from xml.etree import ElementTree


ROOT = Path(__file__).resolve().parent
FIXTURES = ROOT / "fixtures"
SCHEMA = ROOT / "index-schema.sql"
PAYLOAD = b"Harmless Pinget contract fixture. Download only; never execute.\n"
MANIFEST_RELATIVE = "manifests/Pinget.Contract.Singleton.yaml"
SIDECAR_RELATIVE = (
    "packages/Pinget.Contract.Singleton/{hash_prefix}/versionData.mszyml"
)
HASH_RE = set("0123456789abcdef")
ARTIFACTS: Path | None = None


def sha256(data: bytes) -> bytes:
    return hashlib.sha256(data).digest()


def compress_mszip(data: bytes) -> bytes:
    """Use the same native Windows Compression API as WinGet's producer."""
    api = ctypes.WinDLL("cabinet.dll", use_last_error=True)
    handle = ctypes.c_void_p()
    api.CreateCompressor.argtypes = [
        ctypes.c_uint32,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_void_p),
    ]
    api.CreateCompressor.restype = ctypes.c_int
    api.Compress.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    ]
    api.Compress.restype = ctypes.c_int
    api.CloseCompressor.argtypes = [ctypes.c_void_p]
    api.CloseCompressor.restype = ctypes.c_int

    if not api.CreateCompressor(2, None, ctypes.byref(handle)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        source = ctypes.create_string_buffer(data)
        required = ctypes.c_size_t()
        api.Compress(
            handle, source, len(data), None, 0, ctypes.byref(required)
        )
        if ctypes.get_last_error() != 122:
            raise ctypes.WinError(ctypes.get_last_error())
        output = ctypes.create_string_buffer(required.value)
        actual = ctypes.c_size_t()
        if not api.Compress(
            handle,
            source,
            len(data),
            output,
            required.value,
            ctypes.byref(actual),
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        return output.raw[: actual.value]
    finally:
        api.CloseCompressor(handle)


def decompress_mszip(data: bytes) -> bytes:
    """Independently verify the stored stream using the native decompressor."""
    api = ctypes.WinDLL("cabinet.dll", use_last_error=True)
    handle = ctypes.c_void_p()
    api.CreateDecompressor.argtypes = [
        ctypes.c_uint32,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_void_p),
    ]
    api.CreateDecompressor.restype = ctypes.c_int
    api.Decompress.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    ]
    api.Decompress.restype = ctypes.c_int
    api.CloseDecompressor.argtypes = [ctypes.c_void_p]
    api.CloseDecompressor.restype = ctypes.c_int

    if not api.CreateDecompressor(2, None, ctypes.byref(handle)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        source = ctypes.create_string_buffer(data)
        required = ctypes.c_size_t()
        api.Decompress(
            handle, source, len(data), None, 0, ctypes.byref(required)
        )
        if ctypes.get_last_error() != 122:
            raise ctypes.WinError(ctypes.get_last_error())
        output = ctypes.create_string_buffer(required.value)
        actual = ctypes.c_size_t()
        if not api.Decompress(
            handle,
            source,
            len(data),
            output,
            required.value,
            ctypes.byref(actual),
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        return output.raw[: actual.value]
    finally:
        api.CloseDecompressor(handle)


def create_database(path: Path, compressed_sidecar: bytes) -> None:
    connection = sqlite3.connect(path)
    try:
        connection.executescript(SCHEMA.read_text(encoding="utf-8"))
        with connection:
            connection.executemany(
                "INSERT INTO metadata(name, value) VALUES(?, ?)",
                [
                    ("majorVersion", "2"),
                    ("minorVersion", "0"),
                    ("databaseIdentifier", "{" + str(uuid.uuid4()).upper() + "}"),
                    ("lastwritetime", "1793380000"),
                ],
            )
            connection.execute(
                """INSERT INTO packages(
                       rowid, id, name, moniker, latest_version,
                       arp_min_version, arp_max_version, hash
                   ) VALUES(1, ?, ?, ?, ?, NULL, NULL, ?)""",
                (
                    "Pinget.Contract.Singleton",
                    "Pinget Contract Fixture",
                    "pinget-contract",
                    "1.0.0",
                    sqlite3.Binary(sha256(compressed_sidecar)),
                ),
            )
            connection.execute(
                "INSERT INTO tags2(rowid, tag) VALUES(1, 'publisher-contract')"
            )
            connection.execute(
                "INSERT INTO tags2_map(tag, package) VALUES(1, 1)"
            )
    finally:
        connection.close()


def build(output: Path, makeappx: Path, publisher: str, installer_url: str) -> None:
    global ARTIFACTS
    ARTIFACTS = output.resolve()
    if ARTIFACTS.exists():
        raise FileExistsError(
            f"Refusing to overwrite fixture output directory: {ARTIFACTS}"
        )
    feed = ARTIFACTS / "feed"
    stage = ARTIFACTS / "package-staging"
    (feed / "manifests").mkdir(parents=True)
    (feed / "packages").mkdir()
    (stage / "Public").mkdir(parents=True)
    (stage / "Assets").mkdir()

    payload_path = feed / "HarmlessPayload.txt"
    payload_path.write_bytes(PAYLOAD)
    manifest_template = (FIXTURES / "singleton.yaml.in").read_text(
        encoding="utf-8"
    )
    manifest = manifest_template.replace("{{INSTALLER_URL}}", installer_url)
    manifest = manifest.replace(
        "{{INSTALLER_SHA256}}", hashlib.sha256(PAYLOAD).hexdigest()
    )
    manifest_bytes = manifest.encode("utf-8")
    (feed / MANIFEST_RELATIVE).write_bytes(manifest_bytes)

    version_data = (
        'sV: "1.0"\n'
        "vD:\n"
        '  - v: "1.0.0"\n'
        f"    rP: {MANIFEST_RELATIVE}\n"
        f"    s256H: {hashlib.sha256(manifest_bytes).hexdigest()}\n"
    ).encode("utf-8")
    compressed_sidecar = compress_mszip(version_data)
    sidecar_hash = hashlib.sha256(compressed_sidecar).hexdigest()
    sidecar_relative = SIDECAR_RELATIVE.format(hash_prefix=sidecar_hash[:8])
    sidecar_path = feed / Path(sidecar_relative)
    sidecar_path.parent.mkdir(parents=True)
    sidecar_path.write_bytes(compressed_sidecar)
    (ARTIFACTS / "versionData.yml").write_bytes(version_data)

    create_database(stage / "Public" / "index.db", compressed_sidecar)

    appx_template = (FIXTURES / "AppxManifest.xml.in").read_text(
        encoding="utf-8"
    )
    appx_manifest = appx_template.replace("{{PUBLISHER}}", publisher)
    (stage / "AppxManifest.xml").write_text(
        appx_manifest, encoding="utf-8", newline="\n"
    )
    # Locally-authored, one-pixel transparent PNG used only as required package assets.
    logo = bytes.fromhex(
        "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c489"
        "0000000b49444154789c636000020000050001a5f645400000000049454e44ae426082"
    )
    for name in ("StoreLogo.png", "Logo150.png", "Logo44.png"):
        (stage / "Assets" / name).write_bytes(logo)

    package_path = feed / "source2.msix"
    subprocess.run(
        [str(makeappx), "pack", "/d", str(stage), "/p", str(package_path), "/o"],
        check=True,
    )
    (ARTIFACTS / "fixture-metadata.txt").write_text(
        f"Package=source2.msix\n"
        f"Index=Public/index.db\n"
        f"Manifest={MANIFEST_RELATIVE}\n"
        f"Sidecar={sidecar_relative}\n"
        f"SidecarSHA256={sidecar_hash}\n"
        f"InstallerURL={installer_url}\n"
        f"InstallerSHA256={hashlib.sha256(PAYLOAD).hexdigest()}\n"
        f"Publisher={publisher}\n",
        encoding="utf-8",
    )


class V2FixtureTests(unittest.TestCase):
    def setUp(self) -> None:
        if ARTIFACTS is None:
            self.fail("fixture must be built before running contract checks")

    def test_sqlite_v2_schema_and_package_metadata(self) -> None:
        assert ARTIFACTS is not None
        path = ARTIFACTS / "package-staging" / "Public" / "index.db"
        with sqlite3.connect(path) as database:
            self.assertEqual("ok", database.execute("PRAGMA integrity_check").fetchone()[0])
            tables = {
                row[0]
                for row in database.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'"
                )
            }
            self.assertEqual(
                {
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
                },
                tables,
            )
            metadata = dict(database.execute("SELECT name, value FROM metadata"))
            self.assertEqual("2", metadata["majorVersion"])
            self.assertEqual("0", metadata["minorVersion"])
            self.assertRegex(metadata["databaseIdentifier"], r"^\{[0-9A-F-]{36}\}$")
            self.assertTrue(metadata["lastwritetime"].isdigit())
            package = database.execute(
                "SELECT id, name, latest_version, hash FROM packages WHERE rowid=1"
            ).fetchone()
            self.assertEqual(
                (
                    "Pinget.Contract.Singleton",
                    "Pinget Contract Fixture",
                    "1.0.0",
                ),
                package[:3],
            )
            self.assertEqual(32, len(package[3]))
            self.assertEqual(
                [(1, 1)],
                database.execute(
                    "SELECT tag, package FROM tags2_map"
                ).fetchall(),
            )

    def test_sidecar_hash_matches_native_mszip_stream(self) -> None:
        assert ARTIFACTS is not None
        metadata = dict(
            line.split("=", 1)
            for line in (ARTIFACTS / "fixture-metadata.txt")
            .read_text(encoding="utf-8")
            .splitlines()
        )
        package_hash = bytes.fromhex(metadata["SidecarSHA256"])
        expected_sidecar = (
            "packages/Pinget.Contract.Singleton/"
            f"{metadata['SidecarSHA256'][:8].lower()}/versionData.mszyml"
        )
        self.assertEqual(expected_sidecar, metadata["Sidecar"])
        sidecar = (
            ARTIFACTS
            / "feed"
            / Path(metadata["Sidecar"].replace("/", "\\"))
        ).read_bytes()
        self.assertEqual(package_hash, sha256(sidecar))
        self.assertEqual(
            (ARTIFACTS / "versionData.yml").read_bytes(),
            decompress_mszip(sidecar),
        )
        with sqlite3.connect(
            ARTIFACTS / "package-staging" / "Public" / "index.db"
        ) as database:
            index_hash = database.execute(
                "SELECT hash FROM packages WHERE id=?",
                ("Pinget.Contract.Singleton",),
            ).fetchone()[0]
        self.assertEqual(package_hash, index_hash)
        self.assertTrue(set(metadata["SidecarSHA256"]) <= HASH_RE)

    def test_msix_contains_the_v2_index_and_hosted_artifacts(self) -> None:
        assert ARTIFACTS is not None
        package_path = ARTIFACTS / "feed" / "source2.msix"
        with zipfile.ZipFile(package_path) as package:
            names = set(package.namelist())
            self.assertIn("Public/index.db", names)
            self.assertIn("AppxManifest.xml", names)
            packaged_index = package.read("Public/index.db")
            staged_index = (
                ARTIFACTS / "package-staging" / "Public" / "index.db"
            ).read_bytes()
            self.assertEqual(staged_index, packaged_index)
            package_xml = ElementTree.fromstring(
                package.read("AppxManifest.xml")
            )
            app_extension = next(
                node
                for node in package_xml.iter()
                if node.tag.endswith("AppExtension")
            )
            self.assertEqual("com.microsoft.winget.source", app_extension.attrib["Name"])
            self.assertEqual("Public", app_extension.attrib["PublicFolder"])
            metadata = dict(
                line.split("=", 1)
                for line in (ARTIFACTS / "fixture-metadata.txt")
                .read_text(encoding="utf-8")
                .splitlines()
            )
            self.assertNotIn(metadata["Manifest"], names)
            self.assertNotIn(metadata["Sidecar"], names)
            self.assertTrue((ARTIFACTS / "feed" / metadata["Manifest"]).is_file())
            self.assertTrue((ARTIFACTS / "feed" / metadata["Sidecar"]).is_file())
            self.assertEqual(
                hashlib.sha256(PAYLOAD).hexdigest(),
                metadata["InstallerSHA256"],
            )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--makeappx", type=Path, required=True)
    parser.add_argument(
        "--publisher", default="CN=Pinget Publisher Contract Test"
    )
    parser.add_argument(
        "--installer-url",
        default="https://127.0.0.1:8765/HarmlessPayload.txt",
    )
    args = parser.parse_args()
    build(args.output, args.makeappx, args.publisher, args.installer_url)
    result = unittest.TextTestRunner(verbosity=2).run(
        unittest.defaultTestLoader.loadTestsFromTestCase(V2FixtureTests)
    )
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
