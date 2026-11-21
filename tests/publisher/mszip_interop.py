#!/usr/bin/env python3
"""Produce Linux MSZIP fixtures and validate them with the Windows native API.

The fixture producer calls test-only entry points into the two production
encoders. No encoder is implemented here. A fresh, caller-supplied run ID is
required on both machines so a complete artifact from an older run cannot
accidentally be accepted.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET
import zlib
from dataclasses import dataclass


PROTOCOL = "pinget-portable-mszip-v1"
REPO = Path(__file__).resolve().parents[2]
BLOCK = 32 * 1024
LIMIT = 64 * 1024 * 1024
SIGNATURE = b"\x0a\x51\xe5\xc0"
MOTIF = b"Pinget MSZIP portable interop block sequence 0123456789\n"
PRODUCERS = ("rust", "dotnet")


@dataclass(frozen=True)
class Case:
    name: str
    size: int
    pattern: str


CASES = (
    Case("empty", 0, "empty"),
    Case("single_byte", 1, "single"),
    Case("block_32k_before", BLOCK - 1, "random"),
    Case("block_32k_at", BLOCK, "random"),
    Case("block_32k_after", BLOCK + 1, "random"),
    Case("block_64k_before", 2 * BLOCK - 1, "random"),
    Case("block_64k_at", 2 * BLOCK, "random"),
    Case("block_64k_after", 2 * BLOCK + 1, "random"),
    Case("multiblock_compressible", 3 * BLOCK + 137, "repeated"),
    Case("multiblock_incompressible", 3 * BLOCK + 137, "random"),
    Case("limit_64m_before", LIMIT - 1, "repeated"),
    Case("limit_64m_at", LIMIT, "repeated"),
)

RUST_TESTS = {
    "wave21_tests::mszip_interop_tests::portable_encoder_encodes_fixture_matrix_when_requested",
    "wave21_tests::mszip_interop_tests::portable_encoder_rejects_input_over_64_mib",
}
DOTNET_TESTS = {
    "Devolutions.Pinget.Publisher.Tests.PortableMsZipInteropTests.PortableEncoderEncodesFixtureMatrixWhenRequested",
    "Devolutions.Pinget.Publisher.Tests.PortableMsZipInteropTests.PortableEncoderRejectsInputOver64MiB",
}


class VerificationError(Exception):
    """A fixture, test report, or validation control did not meet the contract."""


class NativeDecodeError(VerificationError):
    """Decompress itself rejected a stream (not a Python-side comparison)."""


class NativeEmptyReferenceUnavailable(VerificationError):
    """The native compressor cannot provide an empty positive control."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise VerificationError(message)


def checked_run_id(run_id: str) -> None:
    require(
        re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,80}", run_id, flags=re.ASCII) is not None,
        "run ID must be 1-81 ASCII letters, digits, dot, underscore or hyphen",
    )


def payload(case: Case) -> bytes:
    """Regenerate the original independently of either encoded artifact."""
    if case.pattern == "empty":
        require(case.size == 0, "empty case has a nonzero size")
        return b""
    if case.pattern == "single":
        require(case.size == 1, "single-byte case has an incorrect size")
        return b"\xa7"
    if case.pattern == "repeated":
        return (MOTIF * ((case.size + len(MOTIF) - 1) // len(MOTIF)))[: case.size]
    require(case.pattern == "random", f"unknown payload pattern for {case.name}")
    result = bytearray()
    prefix = b"pinget-mszip-v1/" + case.name.encode("ascii") + b"/"
    for counter in range((case.size + 31) // 32):
        result.extend(hashlib.sha256(prefix + counter.to_bytes(8, "little")).digest())
    del result[case.size :]
    return bytes(result)


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_json(path: Path, value: object) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, sort_keys=True, indent=2)
        stream.write("\n")


def read_json(path: Path) -> object:
    def unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
        result: dict[str, object] = {}
        for key, value in pairs:
            require(key not in result, f"duplicate JSON key {key!r} in {path}")
            result[key] = value
        return result

    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise VerificationError(f"cannot read JSON {path}: {error}") from error


def check_entries(directory: Path, expected: set[str]) -> None:
    require(not directory.is_symlink() and directory.is_dir(), f"missing or invalid directory: {directory}")
    entries = list(directory.iterdir())
    for entry in entries:
        require(not entry.is_symlink(), f"fixture symlink is not allowed: {entry}")
    actual = {entry.name for entry in entries}
    require(
        actual == expected,
        f"fixture entries differ in {directory}; missing={sorted(expected - actual)}, extra={sorted(actual - expected)}",
    )


def read_file(path: Path) -> bytes:
    require(not path.is_symlink() and path.is_file(), f"missing or invalid fixture file: {path}")
    return path.read_bytes()


def initialize_fixture(root: Path, run_id: str, cases: tuple[Case, ...] = CASES) -> None:
    checked_run_id(run_id)
    root.mkdir(parents=True, exist_ok=False)
    inputs = root / "inputs"
    inputs.mkdir()
    records = []
    for case in cases:
        original = payload(case)
        with (inputs / f"{case.name}.bin").open("xb") as stream:
            stream.write(original)
        records.append({"name": case.name, "size": case.size, "pattern": case.pattern, "sha256": digest(original)})
    write_json(root / "run.json", {"protocol": PROTOCOL, "run_id": run_id, "cases": records})


def make_producer_manifest(root: Path, run_id: str, producer: str, cases: tuple[Case, ...] = CASES) -> None:
    directory = root / producer
    check_entries(directory, {f"{case.name}.mszip" for case in cases})
    records = []
    for case in cases:
        encoded = read_file(directory / f"{case.name}.mszip")
        records.append({"name": case.name, "size": len(encoded), "sha256": digest(encoded)})
    write_json(directory / "manifest.json", {"protocol": PROTOCOL, "run_id": run_id, "producer": producer, "cases": records})


def completion_record(root: Path, run_id: str) -> dict[str, str]:
    return {
        "protocol": PROTOCOL,
        "run_id": run_id,
        "run_sha256": digest(read_file(root / "run.json")),
        "rust_sha256": digest(read_file(root / "rust" / "manifest.json")),
        "dotnet_sha256": digest(read_file(root / "dotnet" / "manifest.json")),
    }


def check_frame(data: bytes, expected_size: int, inflate: bool = False) -> bytes:
    """Check the emitted block framing; zlib is used only for the Linux smoke test."""
    require(len(data) >= 24, "truncated MSZIP header")
    signature, reserved, declared, block_size = struct.unpack_from("<4s4sQQ", data)
    require(signature == SIGNATURE, "invalid MSZIP signature")
    require(reserved == b"\0" * 4, "invalid MSZIP reserved bytes")
    require(declared == expected_size, f"MSZIP declared length {declared} != {expected_size}")
    require(block_size == BLOCK, f"invalid MSZIP block size {block_size}")
    offset = 24
    decoded_blocks: list[bytes] = []
    for block_start in range(0, expected_size, BLOCK):
        expected_block_size = min(BLOCK, expected_size - block_start)
        require(offset + 4 <= len(data), "truncated MSZIP block length")
        framed_size = struct.unpack_from("<I", data, offset)[0]
        offset += 4
        require(framed_size >= 2 and framed_size <= len(data) - offset, "invalid or truncated MSZIP block")
        require(data[offset : offset + 2] == b"CK", "invalid MSZIP block marker")
        if inflate:
            decoder = zlib.decompressobj(-15)
            try:
                block = decoder.decompress(data[offset + 2 : offset + framed_size], expected_block_size + 1)
            except zlib.error as error:
                raise VerificationError(f"invalid raw DEFLATE block: {error}") from error
            require(
                decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail,
                "incomplete or trailing raw DEFLATE data",
            )
            require(len(block) == expected_block_size, "incorrect uncompressed MSZIP block length")
            decoded_blocks.append(block)
        offset += framed_size
    require(offset == len(data), "unexpected trailing MSZIP data or block")
    return b"".join(decoded_blocks)


def portable_smoke_decode(data: bytes, expected_size: int) -> bytes:
    return check_frame(data, expected_size, inflate=True)


def validate_tree(
    root: Path,
    run_id: str,
    decoder,
    *,
    cases: tuple[Case, ...] = CASES,
    complete: bool = True,
    frame_check=check_frame,
    on_empty=None,
    verbose: bool = True,
) -> None:
    """Validate inventory and bytes; only callers, not the CLI, can inject test fakes."""
    checked_run_id(run_id)
    root_entries = {"run.json", "inputs", *PRODUCERS}
    if complete:
        root_entries.add("complete.json")
    check_entries(root, root_entries)
    check_entries(root / "inputs", {f"{case.name}.bin" for case in cases})
    for producer in PRODUCERS:
        check_entries(root / producer, {"manifest.json", *(f"{case.name}.mszip" for case in cases)})
    if complete:
        require(read_json(root / "complete.json") == completion_record(root, run_id), "stale or corrupt completion marker")

    run = read_json(root / "run.json")
    require(isinstance(run, dict) and set(run) == {"protocol", "run_id", "cases"}, "invalid run manifest")
    require(run["protocol"] == PROTOCOL and run["run_id"] == run_id, "stale run manifest or run ID")
    records = run["cases"]
    require(isinstance(records, list) and len(records) == len(cases), "missing or extra run cases")

    manifests = {}
    for producer in PRODUCERS:
        manifest = read_json(root / producer / "manifest.json")
        require(
            isinstance(manifest, dict) and set(manifest) == {"protocol", "run_id", "producer", "cases"},
            f"invalid {producer} manifest",
        )
        require(
            manifest["protocol"] == PROTOCOL and manifest["run_id"] == run_id and manifest["producer"] == producer,
            f"stale {producer} manifest",
        )
        require(isinstance(manifest["cases"], list) and len(manifest["cases"]) == len(cases), f"missing or extra {producer} cases")
        manifests[producer] = manifest["cases"]

    failures = []
    for index, case in enumerate(cases):
        expected = payload(case)
        original = read_file(root / "inputs" / f"{case.name}.bin")
        require(original == expected, f"original payload differs from independently regenerated {case.name}")
        record = records[index]
        require(
            isinstance(record, dict)
            and set(record) == {"name", "size", "pattern", "sha256"}
            and type(record["size"]) is int
            and record == {"name": case.name, "size": case.size, "pattern": case.pattern, "sha256": digest(expected)},
            f"stale or corrupt input record for {case.name}",
        )
        for producer in PRODUCERS:
            encoded = read_file(root / producer / f"{case.name}.mszip")
            encoded_record = manifests[producer][index]
            require(
                isinstance(encoded_record, dict)
                and set(encoded_record) == {"name", "size", "sha256"}
                and type(encoded_record["size"]) is int
                and encoded_record == {"name": case.name, "size": len(encoded), "sha256": digest(encoded)},
                f"stale or corrupt {producer} fixture for {case.name}",
            )
            try:
                frame_check(encoded, case.size)
                decoded = decoder(encoded, case.size)
                require(decoded == expected, f"decoded bytes differ for {producer}/{case.name}")
                if case.name == "empty" and on_empty is not None:
                    on_empty(encoded)
            except VerificationError as error:
                failure = f"invalid or undecodable {producer}/{case.name}: {error}"
                failures.append(failure)
                if verbose:
                    print(f"FAILED {failure}", file=sys.stderr, flush=True)
                continue
            if verbose:
                print(f"verified {producer}/{case.name}", flush=True)
    if failures:
        raise VerificationError(f"{len(failures)} fixture(s) failed: " + "; ".join(failures))


class WindowsNativeMsZip:
    """Direct Windows Compression API calls with explicit output capacities."""

    def __init__(self) -> None:
        require(sys.platform == "win32", "Windows native validation requires Windows")
        self.api = ctypes.WinDLL("cabinet.dll", use_last_error=True)
        handle_out = ctypes.POINTER(ctypes.c_void_p)
        size_out = ctypes.POINTER(ctypes.c_size_t)
        for name in ("CreateCompressor", "CreateDecompressor"):
            function = getattr(self.api, name)
            function.argtypes = [ctypes.c_uint32, ctypes.c_void_p, handle_out]
            function.restype = ctypes.c_int
        for name in ("Compress", "Decompress"):
            function = getattr(self.api, name)
            function.argtypes = [
                ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                ctypes.c_void_p, ctypes.c_size_t, size_out,
            ]
            function.restype = ctypes.c_int
        for name in ("CloseCompressor", "CloseDecompressor"):
            function = getattr(self.api, name)
            function.argtypes = [ctypes.c_void_p]
            function.restype = ctypes.c_int

    def decompress(self, data: bytes, expected_size: int) -> bytes:
        handle = ctypes.c_void_p()
        if not self.api.CreateDecompressor(2, None, ctypes.byref(handle)):
            raise VerificationError(f"CreateDecompressor failed: Win32 error {ctypes.get_last_error()}")
        try:
            source = ctypes.create_string_buffer(data, max(len(data), 1))
            # A real, non-null one-byte output buffer avoids ambiguous zero-size queries.
            capacity = max(expected_size, 1)
            destination = ctypes.create_string_buffer(capacity)
            written = ctypes.c_size_t()
            if not self.api.Decompress(handle, source, len(data), destination, capacity, ctypes.byref(written)):
                raise NativeDecodeError(f"Decompress failed: Win32 error {ctypes.get_last_error()}")
            require(written.value == expected_size, f"native decoded length {written.value} != {expected_size}")
            return ctypes.string_at(destination, written.value)
        finally:
            self.api.CloseDecompressor(handle)

    def compress_empty_reference(self) -> bytes:
        handle = ctypes.c_void_p()
        if not self.api.CreateCompressor(2, None, ctypes.byref(handle)):
            raise NativeEmptyReferenceUnavailable(
                f"empty control CreateCompressor failed: Win32 error {ctypes.get_last_error()}"
            )
        try:
            source = ctypes.create_string_buffer(1)
            destination = ctypes.create_string_buffer(4096)
            written = ctypes.c_size_t()
            if not self.api.Compress(handle, source, 0, destination, len(destination), ctypes.byref(written)):
                raise NativeEmptyReferenceUnavailable(
                    "native empty compression control failed: "
                    f"Win32 error {ctypes.get_last_error()}"
                )
            require(written.value <= len(destination), "native empty compression exceeded its output buffer")
            return ctypes.string_at(destination, written.value)
        finally:
            self.api.CloseCompressor(handle)


def assert_native_rejects_malformed_empty(native, valid: bytes, reference: bytes | None = None) -> None:
    require(len(valid) >= 24, "empty control frame is truncated")
    damaged_signature = bytes([valid[0] ^ 0xFF]) + valid[1:]
    for name, damaged in (("truncated_header", valid[:23]), ("invalid_signature", damaged_signature)):
        require(damaged != reference, f"malformed empty control {name} matches the native reference")
        try:
            native.decompress(damaged, 0)
        except NativeDecodeError:
            continue
        raise VerificationError(f"native decoder accepted malformed empty control: {name}")


def validate_native_empty_reference(native) -> bytes:
    reference = native.compress_empty_reference()
    require(native.decompress(reference, 0) == b"", "native empty reference did not decode to zero bytes")
    return reference


def probe_native_zero_input(native) -> str:
    """Report, rather than assume, whether the API accepts no compressed bytes."""
    try:
        result = native.decompress(b"", 0)
    except NativeDecodeError as error:
        return f"rejected ({error})"
    require(result == b"", "native zero-input probe returned nonempty data")
    return "accepted"


def decode_windows_fixture(native, data: bytes, expected_size: int, reference: bytes | None) -> bytes:
    try:
        return native.decompress(data, expected_size)
    except NativeDecodeError as error:
        if expected_size != 0:
            raise
        if reference is None:
            raise VerificationError(
                "native rejected the portable empty stream, but no native empty reference was available; "
                f"cannot distinguish unsupported empty decoding from a malformed stream: {error}"
            ) from error
        raise VerificationError(
            f"native rejected the portable empty stream after decoding its own empty reference: {error}"
        ) from error


def check_rust_results(output: str) -> None:
    found = re.findall(r"^test ([A-Za-z0-9_:]+) \.\.\. ok$", output, flags=re.MULTILINE)
    require(len(found) == len(RUST_TESTS) and set(found) == RUST_TESTS, f"unexpected Rust test discovery/results: {found}")
    require(
        re.search(r"^test result: ok\. 2 passed; 0 failed; 0 ignored;", output, flags=re.MULTILINE) is not None,
        "Rust test summary did not report two passing, non-ignored tests",
    )


def check_dotnet_report(path: Path) -> None:
    try:
        document = ET.parse(path)
    except (OSError, ET.ParseError) as error:
        raise VerificationError(f"missing or invalid .NET TRX report {path}: {error}") from error
    counters = document.find(".//{*}ResultSummary/{*}Counters")
    require(counters is not None, "TRX report has no counters")
    for name, expected in (("total", 2), ("executed", 2), ("passed", 2), ("failed", 0), ("notExecuted", 0)):
        require(counters.get(name) == str(expected), f"unexpected TRX {name} counter: {counters.get(name)}")
    results = document.findall(".//{*}UnitTestResult")
    names = [result.get("testName") for result in results]
    require(len(results) == 2 and set(names) == DOTNET_TESTS, f"unexpected .NET test discovery: {names}")
    require(all(result.get("outcome") == "Passed" for result in results), "TRX contains a non-passing test")


def run_command(command: list[str], env: dict[str, str]) -> str:
    print("+ " + " ".join(command), flush=True)
    result = subprocess.run(command, cwd=REPO, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    print(result.stdout, end="", flush=True)
    require(result.returncode == 0, f"command exited {result.returncode}: {command[0]}")
    return result.stdout


def produce(root: Path, reports: Path, run_id: str, *, dotnet_no_restore: bool = False) -> None:
    require(sys.platform.startswith("linux"), "both portable fixture producers must run on Linux")
    checked_run_id(run_id)
    root = root.resolve()
    reports = reports.resolve()
    require(not root.exists(), f"fixture output already exists; choose a fresh path: {root}")
    require(not reports.exists(), f"report output already exists; choose a fresh path: {reports}")
    require(not root.is_relative_to(reports) and not reports.is_relative_to(root), "fixture and report directories must be separate")
    reports.mkdir(parents=True)
    initialize_fixture(root, run_id)
    env = os.environ.copy()
    env.update({
        "PINGET_MSZIP_FIXTURE_ROOT": str(root),
        "PINGET_MSZIP_RUN_ID": run_id,
        "CARGO_TARGET_DIR": str(REPO / "rust" / "target"),
        "CARGO_PROFILE_DEV_DEBUG": "0",
        "CARGO_PROFILE_TEST_DEBUG": "0",
        "CARGO_INCREMENTAL": "0",
    })
    rust_output = run_command([
        "cargo", "test", "--manifest-path", "rust/Cargo.toml",
        "-p", "pinget-publisher", "--lib", "wave21_tests::mszip_interop_tests",
        "--", "--test-threads=1",
    ], env)
    check_rust_results(rust_output)
    dotnet_command = [
        "dotnet", "test", "dotnet/src/Devolutions.Pinget.Publisher.Tests/Devolutions.Pinget.Publisher.Tests.csproj",
        "-c", "Release", "--filter", "FullyQualifiedName~PortableMsZipInteropTests",
        "--logger", "trx;LogFileName=mszip-portable.trx", "--results-directory", str(reports),
    ]
    if dotnet_no_restore:
        dotnet_command.append("--no-restore")
    run_command(dotnet_command, env)
    check_dotnet_report(reports / "mszip-portable.trx")
    for producer in PRODUCERS:
        make_producer_manifest(root, run_id, producer)
    validate_tree(root, run_id, portable_smoke_decode, complete=False)
    write_json(root / "complete.json", completion_record(root, run_id))
    print(f"Linux fixture production and portable smoke checks passed: {root}")
    print("Windows native validation has NOT run.")


def validate_windows(root: Path, run_id: str) -> None:
    native = WindowsNativeMsZip()
    try:
        reference = validate_native_empty_reference(native)
    except NativeEmptyReferenceUnavailable as error:
        reference = None
        print(f"Native empty reference unavailable ({error}); portable empty decoding is still required.")
    if reference is not None:
        print(f"Native empty reference decoded to zero bytes (compressed length {len(reference)}).")
    print(f"Native zero-compressed-byte probe: {probe_native_zero_input(native)}.")
    validate_tree(
        root,
        run_id,
        lambda data, size: decode_windows_fixture(native, data, size, reference),
        on_empty=lambda data: assert_native_rejects_malformed_empty(native, data, reference),
    )
    print("All portable fixtures passed the Windows native MSZIP decoder.")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="action", required=True)
    producer = subparsers.add_parser("produce", help="run both real portable encoders on Linux")
    producer.add_argument("--output", type=Path, required=True)
    producer.add_argument("--reports", type=Path, required=True)
    producer.add_argument("--run-id", required=True)
    producer.add_argument("--dotnet-no-restore", action="store_true", help="use already restored .NET test dependencies")
    validator = subparsers.add_parser("validate-windows", help="decode every transferred fixture with cabinet.dll")
    validator.add_argument("--input", type=Path, required=True)
    validator.add_argument("--run-id", required=True)
    args = parser.parse_args(argv)
    try:
        if args.action == "produce":
            produce(args.output, args.reports, args.run_id, dotnet_no_restore=args.dotnet_no_restore)
        else:
            validate_windows(args.input, args.run_id)
    except (VerificationError, OSError) as error:
        print(f"MSZIP interoperability validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
