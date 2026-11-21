import importlib.util
import json
import hashlib
import tempfile
import unittest
from pathlib import Path
from subprocess import CompletedProcess
from types import SimpleNamespace
from unittest.mock import patch
from urllib.request import Request


MODULE_PATH = Path(__file__).resolve().parents[2] / "scripts" / "import-homebrew-catalog.py"
SPEC = importlib.util.spec_from_file_location("homebrew_catalog_import", MODULE_PATH)
catalog = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(catalog)


class CatalogClosureTests(unittest.TestCase):
    def test_resolves_dependencies_before_roots_and_fetches_each_once(self):
        formulas = {
            "jq": {"name": "jq", "dependencies": ["oniguruma"]},
            "oniguruma": {"name": "oniguruma", "dependencies": []},
        }
        fetched = []

        def fetch(name):
            fetched.append(name)
            return formulas[name], f"hash-{name}"

        resolved, hashes, order, failures = catalog.resolve_closure(["jq", "oniguruma"], fetch)

        self.assertEqual(order, ["oniguruma", "jq"])
        self.assertEqual(fetched, ["jq", "oniguruma"])
        self.assertEqual(set(resolved), {"jq", "oniguruma"})
        self.assertEqual(hashes["jq"], "hash-jq")
        self.assertEqual(failures, {})

    def test_marks_dependency_cycles_as_unavailable(self):
        formulas = {
            "first": {"name": "first", "dependencies": ["second"]},
            "second": {"name": "second", "dependencies": ["first"]},
        }

        resolved, _, order, failures = catalog.resolve_closure(["first"], lambda name: (formulas[name], "hash"))
        self.assertEqual(set(resolved), {"first", "second"})
        self.assertEqual(order, [])
        self.assertIn("cycle", failures["first"])

    def test_rejects_non_core_dependency_names(self):
        with self.assertRaisesRegex(catalog.ImportError, "homebrew/core"):
            catalog.dependency_names({"name": "sample", "dependencies": ["owner/tap/formula"]})

    def test_rejects_missing_target_bottle(self):
        with self.assertRaisesRegex(catalog.ImportError, "no stable bottle"):
            catalog.bottle_for({"name": "sample", "bottle": {}}, "x86_64_linux")

    def test_strips_registry_authorization_before_allowed_cdn_redirect(self):
        request = Request("https://ghcr.io/v2/blob", headers={"Authorization": "Bearer private-token"})
        redirected = catalog.CheckedRedirectHandler(
            {"ghcr.io", "pkg-containers.githubusercontent.com"}
        ).redirect_request(
            request, None, 302, "Found", {}, "https://pkg-containers.githubusercontent.com/blob"
        )
        self.assertNotIn("Authorization", redirected.headers)
        with self.assertRaisesRegex(catalog.ImportError, "outside the allowed HTTPS host"):
            catalog.CheckedRedirectHandler({"ghcr.io"}).redirect_request(
                request, None, 302, "Found", {}, "http://example.invalid/blob"
            )

    def test_downloads_only_the_declared_sha256_blob(self):
        payload = b"pinned bottle"
        digest = hashlib.sha256(payload).hexdigest()

        class Response:
            def __init__(self):
                self.remaining = payload

            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            @staticmethod
            def geturl():
                return "https://ghcr.io/v2/homebrew/core/sample/blobs/sha256:" + digest

            def read(self, size):
                result, self.remaining = self.remaining[:size], self.remaining[size:]
                return result

        formula = {
            "name": "sample",
            "bottle": {
                "stable": {
                    "files": {
                        "x86_64_linux": {
                            "url": "https://ghcr.io/v2/homebrew/core/sample/blobs/sha256:" + digest,
                            "sha256": digest,
                        }
                    }
                }
            },
        }
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(catalog, "bottle_token", return_value="ephemeral-test-token"), \
             patch.object(catalog, "open_https", side_effect=lambda *_args: Response()):
            path = catalog.download_bottle(formula, "x86_64_linux", Path(temporary))
            self.assertEqual(path.read_bytes(), payload)
            path.unlink()

            wrong = json.loads(json.dumps(formula))
            wrong_hash = "b" * 64
            wrong["bottle"]["stable"]["files"]["x86_64_linux"]["sha256"] = wrong_hash
            wrong["bottle"]["stable"]["files"]["x86_64_linux"]["url"] = (
                "https://ghcr.io/v2/homebrew/core/sample/blobs/sha256:" + wrong_hash
            )
            with patch.object(catalog, "open_https", side_effect=lambda *_args: Response()):
                with self.assertRaisesRegex(catalog.ImportError, "SHA-256"):
                    catalog.download_bottle(wrong, "x86_64_linux", Path(temporary))
            self.assertFalse((Path(temporary) / "sample.tar.gz").exists())

    def test_imports_supported_closure_with_verified_dependency_pins(self):
        digest = "a" * 64

        def formula(name, dependencies):
            return {
                "name": name,
                "tap": "homebrew/core",
                "dependencies": dependencies,
                "versions": {"stable": "1.2.3"},
                "revision": 0,
                "bottle": {
                    "stable": {
                        "rebuild": 1,
                        "files": {
                            "x86_64_linux": {
                                "url": f"https://ghcr.io/v2/homebrew/core/{name}/blobs/sha256:{digest}",
                                "sha256": digest,
                                "cellar": "any",
                            }
                        },
                    }
                },
            }

        formulas = {"jq": formula("jq", ["oniguruma"]), "oniguruma": formula("oniguruma", [])}
        args = SimpleNamespace(
            formula=["jq"],
            formula_list=None,
            tag="x86_64_linux",
            runtime="rust",
            rust_cli="pinget",
            dotnet_assembly=None,
            manifest_output="out/manifests",
            bottle_mirror="out/bottles",
            base_url="https://mirror.invalid/bottles",
            report="out/catalog-import.json",
            minimum_imported=None,
        )

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args.manifest_output = str(root / "manifests")
            args.bottle_mirror = str(root / "bottles")
            args.report = str(root / "catalog-import.json")
            imports = []

            def download(value, tag, directory):
                path = directory / f"{value['name']}.tar.gz"
                path.write_bytes(b"fixture")
                return path

            def run(command, **_kwargs):
                formula_path = Path(command[command.index("--formula-json") + 1])
                imported = json.loads(formula_path.read_text(encoding="utf-8"))
                imports.append(imported)
                return CompletedProcess(command, 0, "", "")

            with patch.object(catalog, "api_formula", side_effect=lambda name: (formulas[name], f"api-{name}")), \
                 patch.object(catalog, "download_bottle", side_effect=download), \
                 patch.object(catalog.subprocess, "run", side_effect=run):
                report = catalog.import_catalog(args)

            self.assertEqual(report["imported"], ["oniguruma", "jq"])
            self.assertEqual(report["importedRoots"], ["jq"])
            self.assertEqual(imports[0]["pinget_runtime_dependencies"], [])
            self.assertEqual(imports[1]["pinget_runtime_dependencies"], [
                {"full_name": "oniguruma", "pkg_version": "1.2.3", "bottle_rebuild": 1},
            ])
            self.assertEqual(report["importedMetadata"][1]["formulaApiSha256"], "api-jq")


if __name__ == "__main__":
    unittest.main()
