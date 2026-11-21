"""Tests for the fixture protocol and the Windows empty-stream call contract."""

import ctypes
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest import mock
import zlib

import mszip_interop as interop


SMALL_CASES = (
    interop.Case("empty", 0, "empty"),
    interop.Case("single_byte", 1, "single"),
    interop.Case("block_32k_after", interop.BLOCK + 1, "random"),
)
EMPTY_FRAME = interop.SIGNATURE + bytes(4) + struct.pack("<QQ", 0, interop.BLOCK)
# A fixed, final, uncompressed DEFLATE block containing one byte.
SINGLE_FRAME = (
    interop.SIGNATURE
    + bytes(4)
    + struct.pack("<QQI", 1, interop.BLOCK, 8)
    + b"CK\x01\x01\x00\xfe\xff\xa7"
)


def no_frame_check(_encoded, _size):
    pass


def replace_json(path, value):
    path.write_text(json.dumps(value), encoding="utf-8")


class FixtureProtocolTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "fixtures"
        interop.initialize_fixture(self.root, "run-42", SMALL_CASES)
        for producer in interop.PRODUCERS:
            directory = self.root / producer
            directory.mkdir()
            for case in SMALL_CASES:
                # Identity data is only a fake for protocol tests; real fixtures
                # are produced exclusively by the production encoders.
                (directory / f"{case.name}.mszip").write_bytes(interop.payload(case))
            interop.make_producer_manifest(self.root, "run-42", producer, SMALL_CASES)
        interop.write_json(self.root / "complete.json", interop.completion_record(self.root, "run-42"))

    def validate(self, run_id="run-42", decoder=lambda data, _size: data):
        interop.validate_tree(
            self.root,
            run_id,
            decoder,
            cases=SMALL_CASES,
            frame_check=no_frame_check,
            verbose=False,
        )

    def reseal(self):
        replace_json(self.root / "complete.json", interop.completion_record(self.root, "run-42"))

    def test_exact_set_compares_each_producer_and_case(self):
        calls = []

        def decoder(data, size):
            calls.append((len(data), size))
            return data

        self.validate(decoder=decoder)
        self.assertEqual([(0, 0), (0, 0), (1, 1), (1, 1), (32769, 32769), (32769, 32769)], calls)

    def test_stale_external_run_id_is_rejected(self):
        with self.assertRaisesRegex(interop.VerificationError, "stale or corrupt completion marker"):
            self.validate(run_id="run-previous")

    def test_stale_producer_manifest_is_rejected_even_with_new_seal(self):
        path = self.root / "rust" / "manifest.json"
        manifest = interop.read_json(path)
        manifest["run_id"] = "run-previous"
        replace_json(path, manifest)
        self.reseal()
        with self.assertRaisesRegex(interop.VerificationError, "stale rust manifest"):
            self.validate()

    def test_missing_completion_marker_is_rejected(self):
        (self.root / "complete.json").unlink()
        with self.assertRaisesRegex(interop.VerificationError, "missing=.*complete.json"):
            self.validate()

    def test_missing_producer_fixture_is_rejected(self):
        (self.root / "dotnet" / "single_byte.mszip").unlink()
        with self.assertRaisesRegex(interop.VerificationError, "missing=.*single_byte.mszip"):
            self.validate()

    def test_missing_entire_producer_set_is_rejected(self):
        (self.root / "rust").rename(Path(self.temporary.name) / "removed-rust")
        with self.assertRaisesRegex(interop.VerificationError, "missing=.*rust"):
            self.validate()

    def test_extra_case_or_file_is_rejected(self):
        (self.root / "rust" / "old_case.mszip").write_bytes(b"old")
        with self.assertRaisesRegex(interop.VerificationError, "extra=.*old_case.mszip"):
            self.validate()

    def test_extra_root_directory_is_rejected(self):
        (self.root / "old_producer").mkdir()
        with self.assertRaisesRegex(interop.VerificationError, "extra=.*old_producer"):
            self.validate()

    def test_corrupted_encoded_bytes_fail_manifest_integrity(self):
        (self.root / "rust" / "single_byte.mszip").write_bytes(b"\x00")
        with self.assertRaisesRegex(interop.VerificationError, "corrupt rust fixture for single_byte"):
            self.validate()

    def test_wrong_decoded_bytes_fail_even_if_hash_and_seal_are_recomputed(self):
        directory = self.root / "rust"
        (directory / "single_byte.mszip").write_bytes(b"\x00")
        (directory / "manifest.json").unlink()
        interop.make_producer_manifest(self.root, "run-42", "rust", SMALL_CASES)
        self.reseal()
        with self.assertRaisesRegex(interop.VerificationError, "decoded bytes differ for rust/single_byte"):
            self.validate()

    def test_decoder_failures_are_reported_after_all_producers_and_cases(self):
        sizes = []

        def decoder(data, size):
            sizes.append(size)
            if size == 0:
                raise interop.NativeDecodeError("empty stream rejected")
            return data

        with self.assertRaisesRegex(interop.VerificationError, r"2 fixture\(s\) failed") as error:
            self.validate(decoder=decoder)
        self.assertIn("rust/empty", str(error.exception))
        self.assertIn("dotnet/empty", str(error.exception))
        self.assertEqual([0, 0, 1, 1, 32769, 32769], sizes)

    def test_corrupted_original_fails_even_if_manifest_hash_is_recomputed(self):
        (self.root / "inputs" / "single_byte.bin").write_bytes(b"\x00")
        path = self.root / "run.json"
        manifest = interop.read_json(path)
        manifest["cases"][1]["sha256"] = interop.digest(b"\x00")
        replace_json(path, manifest)
        self.reseal()
        with self.assertRaisesRegex(interop.VerificationError, "original payload differs.*single_byte"):
            self.validate()

    def test_reordered_manifest_cases_are_rejected(self):
        path = self.root / "dotnet" / "manifest.json"
        manifest = interop.read_json(path)
        manifest["cases"].reverse()
        replace_json(path, manifest)
        self.reseal()
        with self.assertRaisesRegex(interop.VerificationError, "corrupt dotnet fixture for empty"):
            self.validate()

    def test_extra_manifest_case_is_rejected_even_with_new_seal(self):
        path = self.root / "rust" / "manifest.json"
        manifest = interop.read_json(path)
        manifest["cases"].append(manifest["cases"][0])
        replace_json(path, manifest)
        self.reseal()
        with self.assertRaisesRegex(interop.VerificationError, "missing or extra rust cases"):
            self.validate()

    def test_duplicate_json_keys_are_rejected(self):
        path = self.root / "run.json"
        path.write_text('{"protocol":"one","protocol":"two"}', encoding="utf-8")
        self.reseal()
        with self.assertRaisesRegex(interop.VerificationError, "duplicate JSON key"):
            self.validate()

    def test_existing_output_directory_is_never_overwritten(self):
        with self.assertRaisesRegex(interop.VerificationError, "output already exists"):
            interop.produce(self.root, Path(self.temporary.name) / "new-reports", "run-43")
        self.validate()

    def test_failed_producer_never_writes_completion_marker(self):
        root = Path(self.temporary.name) / "failed-run"
        reports = Path(self.temporary.name) / "failed-reports"
        initialize = interop.initialize_fixture
        with mock.patch.object(
            interop, "initialize_fixture", side_effect=lambda path, run_id: initialize(path, run_id, SMALL_CASES)
        ), mock.patch.object(interop, "run_command", side_effect=interop.VerificationError("test runner failed")):
            with self.assertRaisesRegex(interop.VerificationError, "test runner failed"):
                interop.produce(root, reports, "run-43")
        self.assertFalse((root / "complete.json").exists())
        self.assertEqual(interop.payload(SMALL_CASES[1]), (root / "inputs" / "single_byte.bin").read_bytes())


class FixtureDefinitionTests(unittest.TestCase):
    def test_inventory_covers_exact_boundaries_and_both_multiblock_patterns(self):
        self.assertEqual(
            [(case.name, case.size, case.pattern) for case in interop.CASES],
            [
                ("empty", 0, "empty"),
                ("single_byte", 1, "single"),
                ("block_32k_before", 32767, "random"),
                ("block_32k_at", 32768, "random"),
                ("block_32k_after", 32769, "random"),
                ("block_64k_before", 65535, "random"),
                ("block_64k_at", 65536, "random"),
                ("block_64k_after", 65537, "random"),
                ("multiblock_compressible", 98441, "repeated"),
                ("multiblock_incompressible", 98441, "random"),
                ("limit_64m_before", 67108863, "repeated"),
                ("limit_64m_at", 67108864, "repeated"),
            ],
        )
        self.assertEqual(b"\xa7", interop.payload(interop.CASES[1]))
        repeated = interop.payload(interop.CASES[8])
        random = interop.payload(interop.CASES[9])
        self.assertNotEqual(repeated[:64], repeated[interop.BLOCK : interop.BLOCK + 64])
        self.assertNotEqual(random[:64], random[interop.BLOCK : interop.BLOCK + 64])
        self.assertEqual(random, interop.payload(interop.CASES[9]))
        self.assertLess(len(zlib.compress(repeated)), len(repeated) // 10)
        self.assertGreater(len(zlib.compress(random)), len(random) * 98 // 100)

    def test_run_id_rejects_empty_unsafe_and_overlong_values(self):
        for name, invalid in (
            ("empty", ""),
            ("path", "../old"),
            ("space", "run with spaces"),
            ("too_long", "x" * 82),
            ("null", "\x00"),
        ):
            with self.subTest(case=name):
                with self.assertRaises(interop.VerificationError):
                    interop.checked_run_id(invalid)
        interop.checked_run_id("ci-123.2_attempt")

    def test_frame_parser_accepts_exact_empty_and_single_byte_streams(self):
        self.assertEqual(b"", interop.portable_smoke_decode(EMPTY_FRAME, 0))
        self.assertEqual(b"\xa7", interop.portable_smoke_decode(SINGLE_FRAME, 1))

    def test_frame_parser_rejects_corruption_and_trailing_data(self):
        corruptions = {
            "truncated_header": EMPTY_FRAME[:23],
            "bad_signature": b"\x00" + EMPTY_FRAME[1:],
            "bad_reserved": EMPTY_FRAME[:4] + b"\x01" + EMPTY_FRAME[5:],
            "wrong_length": SINGLE_FRAME,
            "wrong_block_size": EMPTY_FRAME[:16] + struct.pack("<Q", 32767),
            "unexpected_trailing": EMPTY_FRAME + b"\x00",
        }
        for name, frame in corruptions.items():
            with self.subTest(case=name):
                with self.assertRaises(interop.VerificationError):
                    interop.portable_smoke_decode(frame, 0)
        for frame in (
            SINGLE_FRAME[:-1],
            SINGLE_FRAME[:28] + b"XX" + SINGLE_FRAME[30:],
            SINGLE_FRAME + b"\x00",
            SINGLE_FRAME[:30] + b"\xff" + SINGLE_FRAME[31:],
        ):
            with self.assertRaises(interop.VerificationError):
                interop.portable_smoke_decode(frame, 1)


class NativeEmptyControlTests(unittest.TestCase):
    def test_native_validator_refuses_non_windows_hosts(self):
        with mock.patch.object(interop.sys, "platform", "linux"):
            with self.assertRaisesRegex(interop.VerificationError, "requires Windows"):
                interop.WindowsNativeMsZip()

    def test_windows_empty_decode_uses_non_null_one_byte_output(self):
        class FakeApi:
            def __init__(self):
                self.calls = []

            def CreateDecompressor(self, algorithm, options, handle):
                self.calls.append(("create", algorithm, options))
                ctypes.cast(handle, ctypes.POINTER(ctypes.c_void_p))[0] = ctypes.c_void_p(11)
                return 1

            def Decompress(self, handle, source, source_size, destination, capacity, written):
                self.calls.append(("decompress", source_size, capacity, ctypes.addressof(destination) != 0))
                ctypes.cast(written, ctypes.POINTER(ctypes.c_size_t))[0] = 0
                return 1

            def CloseDecompressor(self, handle):
                self.calls.append(("close", handle.value))
                return 1

        native = object.__new__(interop.WindowsNativeMsZip)
        native.api = FakeApi()
        self.assertEqual(b"", native.decompress(EMPTY_FRAME, 0))
        self.assertEqual(
            [("create", 2, None), ("decompress", len(EMPTY_FRAME), 1, True), ("close", 11)],
            native.api.calls,
        )

    def test_native_empty_success_with_nonzero_written_length_is_rejected(self):
        class FakeApi:
            closed = False

            def CreateDecompressor(self, _algorithm, _options, handle):
                ctypes.cast(handle, ctypes.POINTER(ctypes.c_void_p))[0] = ctypes.c_void_p(12)
                return 1

            def Decompress(self, _handle, _source, _source_size, _destination, _capacity, written):
                ctypes.cast(written, ctypes.POINTER(ctypes.c_size_t))[0] = 1
                return 1

            def CloseDecompressor(self, _handle):
                self.closed = True
                return 1

        native = object.__new__(interop.WindowsNativeMsZip)
        native.api = FakeApi()
        with self.assertRaisesRegex(interop.VerificationError, "native decoded length 1 != 0"):
            native.decompress(EMPTY_FRAME, 0)
        self.assertTrue(native.api.closed)

    def test_native_decode_error_reports_win32_failure_and_closes_handle(self):
        class FakeApi:
            closed = False

            def CreateDecompressor(self, _algorithm, _options, handle):
                ctypes.cast(handle, ctypes.POINTER(ctypes.c_void_p))[0] = ctypes.c_void_p(13)
                return 1

            def Decompress(self, _handle, _source, _source_size, _destination, _capacity, _written):
                return 0

            def CloseDecompressor(self, _handle):
                self.closed = True
                return 1

        native = object.__new__(interop.WindowsNativeMsZip)
        native.api = FakeApi()
        with mock.patch.object(interop.ctypes, "get_last_error", return_value=13, create=True):
            with self.assertRaisesRegex(interop.NativeDecodeError, "Win32 error 13"):
                native.decompress(EMPTY_FRAME, 0)
        self.assertTrue(native.api.closed)

    def test_native_empty_reference_compression_uses_explicit_buffer(self):
        class FakeApi:
            def __init__(self):
                self.calls = []

            def CreateCompressor(self, algorithm, options, handle):
                self.calls.append(("create", algorithm, options))
                ctypes.cast(handle, ctypes.POINTER(ctypes.c_void_p))[0] = ctypes.c_void_p(14)
                return 1

            def Compress(self, _handle, source, source_size, destination, capacity, written):
                self.calls.append(("compress", source_size, capacity, ctypes.addressof(source) != 0))
                ctypes.memmove(destination, EMPTY_FRAME, len(EMPTY_FRAME))
                ctypes.cast(written, ctypes.POINTER(ctypes.c_size_t))[0] = len(EMPTY_FRAME)
                return 1

            def CloseCompressor(self, handle):
                self.calls.append(("close", handle.value))
                return 1

        native = object.__new__(interop.WindowsNativeMsZip)
        native.api = FakeApi()
        self.assertEqual(EMPTY_FRAME, native.compress_empty_reference())
        self.assertEqual(
            [("create", 2, None), ("compress", 0, 4096, True), ("close", 14)],
            native.api.calls,
        )

    def test_native_positive_control_checks_roundtrip_before_fixture_validation(self):
        class FakeNative:
            def compress_reference(self, data):
                self.asserted_input = data
                return b"native"

            def decompress(self, data, size):
                if data == b"native" and size == 1:
                    return b"\xa7"
                raise interop.NativeDecodeError("wrong control")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for producer in interop.PRODUCERS:
                (root / producer).mkdir()
                (root / producer / "single_byte.mszip").write_bytes(SINGLE_FRAME)
            native = FakeNative()
            with mock.patch.object(interop.ctypes, "FormatError", return_value="bad compression", create=True), \
                    mock.patch("builtins.print"):
                interop.check_native_positive_control(native, root)
            self.assertEqual(b"\xa7", native.asserted_input)

            with mock.patch.object(native, "decompress", return_value=b"\x00"):
                with self.assertRaisesRegex(interop.VerificationError, "native nonempty control failed"):
                    interop.check_native_positive_control(native, root)

    def test_native_empty_positive_and_corrupted_controls_are_all_exercised(self):
        class FakeNative:
            def __init__(self):
                self.calls = []

            def compress_empty_reference(self):
                return EMPTY_FRAME

            def decompress(self, data, size):
                self.calls.append((data, size))
                if data == EMPTY_FRAME:
                    return b""
                raise interop.NativeDecodeError("invalid native stream")

        native = FakeNative()
        reference = interop.validate_native_empty_reference(native)
        self.assertIn("rejected", interop.probe_native_zero_input(native))
        interop.assert_native_rejects_malformed_empty(native, EMPTY_FRAME, reference)
        self.assertEqual(4, len(native.calls))
        self.assertEqual([0, 0, 0, 0], [size for _, size in native.calls])
        self.assertEqual([EMPTY_FRAME, b"", EMPTY_FRAME[:23]], [item[0] for item in native.calls[:3]])
        self.assertNotEqual(EMPTY_FRAME[0], native.calls[3][0][0])

    def test_zero_byte_native_reference_is_not_mislabeled_as_malformed(self):
        class ZeroByteReference:
            def __init__(self):
                self.calls = []

            def compress_empty_reference(self):
                return b""

            def decompress(self, data, size):
                self.calls.append((data, size))
                if data == b"":
                    return b""
                raise interop.NativeDecodeError("invalid native stream")

        native = ZeroByteReference()
        reference = interop.validate_native_empty_reference(native)
        self.assertEqual(b"", reference)
        self.assertEqual("accepted", interop.probe_native_zero_input(native))
        interop.assert_native_rejects_malformed_empty(native, EMPTY_FRAME, reference)
        self.assertEqual(4, len(native.calls))
        self.assertEqual([b"", b"", EMPTY_FRAME[:23]], [item[0] for item in native.calls[:3]])
        self.assertNotEqual(EMPTY_FRAME[0], native.calls[3][0][0])

    def test_native_decoder_accepting_malformed_empty_is_a_failure(self):
        class AcceptsAnything:
            def decompress(self, _data, _size):
                return b""

        with self.assertRaisesRegex(interop.VerificationError, "accepted malformed empty control: truncated_header"):
            interop.assert_native_rejects_malformed_empty(AcceptsAnything(), EMPTY_FRAME)

    def test_native_zero_input_probe_rejects_a_nonempty_result(self):
        class WrongZeroResult:
            def decompress(self, _data, _size):
                return b"x"

        with self.assertRaisesRegex(interop.VerificationError, "zero-input probe returned nonempty"):
            interop.probe_native_zero_input(WrongZeroResult())

    def test_native_empty_reference_failure_is_not_treated_as_compatibility(self):
        class UnsupportedControl:
            def compress_empty_reference(self):
                raise interop.NativeEmptyReferenceUnavailable("native empty compression control failed")

        with self.assertRaisesRegex(interop.NativeEmptyReferenceUnavailable, "native empty compression control failed"):
            interop.validate_native_empty_reference(UnsupportedControl())

    def test_portable_empty_rejection_distinguishes_reference_availability(self):
        class RejectsEmpty:
            def decompress(self, _data, _size):
                raise interop.NativeDecodeError("Win32 error 13")

        with self.assertRaisesRegex(interop.VerificationError, "cannot distinguish unsupported empty decoding"):
            interop.decode_windows_fixture(RejectsEmpty(), EMPTY_FRAME, 0, None)
        with self.assertRaisesRegex(interop.VerificationError, "after decoding its own empty reference"):
            interop.decode_windows_fixture(RejectsEmpty(), EMPTY_FRAME, 0, EMPTY_FRAME)

    def test_unavailable_native_reference_still_requires_portable_empty_and_corruption_checks(self):
        class NoNativeCompressor:
            def __init__(self):
                self.calls = []

            def compress_empty_reference(self):
                raise interop.NativeEmptyReferenceUnavailable("native compressor cannot encode empty input")

            def decompress(self, data, size):
                self.calls.append((data, size))
                if data == EMPTY_FRAME:
                    return b""
                raise interop.NativeDecodeError("invalid native stream")

        native = NoNativeCompressor()

        def fake_validate(_root, _run_id, decoder, *, on_empty):
            self.assertEqual(b"", decoder(EMPTY_FRAME, 0))
            on_empty(EMPTY_FRAME)

        with mock.patch.object(interop, "WindowsNativeMsZip", return_value=native), mock.patch.object(
            interop, "check_native_positive_control"
        ) as positive_control, mock.patch.object(interop, "validate_tree", side_effect=fake_validate) as validator, \
                mock.patch("builtins.print"):
            interop.validate_windows(Path("unused"), "run-42")
        positive_control.assert_called_once_with(native, Path("unused"))
        validator.assert_called_once()
        self.assertEqual([b"", EMPTY_FRAME, EMPTY_FRAME[:23]], [call[0] for call in native.calls[:3]])
        self.assertNotEqual(EMPTY_FRAME[0], native.calls[3][0][0])


class RunnerEvidenceTests(unittest.TestCase):
    def test_rust_results_require_both_named_passing_tests(self):
        output = "running 2 tests\n" + "".join(f"test {name} ... ok\n" for name in sorted(interop.RUST_TESTS))
        output += "test result: ok. 2 passed; 0 failed; 0 ignored; 4 measured; 0 filtered out\n"
        interop.check_rust_results(output)
        for invalid in ("test result: ok. 0 passed; 0 failed; 0 ignored;", output.replace("2 passed", "1 passed")):
            with self.assertRaises(interop.VerificationError):
                interop.check_rust_results(invalid)

    def test_trx_requires_exact_discovery_counters_and_passing_outcomes(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "test.trx"
            results = "".join(
                f'<UnitTestResult testName="{name}" outcome="Passed" />' for name in sorted(interop.DOTNET_TESTS)
            )
            text = (
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                f"<Results>{results}</Results>"
                '<ResultSummary><Counters total="2" executed="2" passed="2" failed="0" notExecuted="0" />'
                "</ResultSummary></TestRun>"
            )
            report.write_text(text, encoding="utf-8")
            interop.check_dotnet_report(report)
            for invalid in (
                text.replace('passed="2"', 'passed="0"'),
                text.replace('outcome="Passed"', 'outcome="Failed"', 1),
                text.replace(results, ""),
            ):
                report.write_text(invalid, encoding="utf-8")
                with self.assertRaises(interop.VerificationError):
                    interop.check_dotnet_report(report)


if __name__ == "__main__":
    unittest.main()
