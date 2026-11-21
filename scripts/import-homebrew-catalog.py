#!/usr/bin/env python3
"""Resolve and mirror a bounded Homebrew/core formula closure for Pinget."""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

API_ROOT = "https://formulae.brew.sh/api/formula/"
MAX_FORMULAS = 256
MAX_FORMULA_BYTES = 8 * 1024 * 1024
MAX_BOTTLE_BYTES = 2 * 1024 * 1024 * 1024
MAX_CLOSURE_BOTTLE_BYTES = 2 * 1024 * 1024 * 1024


class ImportError(Exception):
    pass


class CheckedRedirectHandler(urllib.request.HTTPRedirectHandler):
    def __init__(self, allowed_hosts):
        super().__init__()
        self.allowed_hosts = allowed_hosts

    def redirect_request(self, request, response, code, message, headers, new_url):
        target = urllib.parse.urlsplit(new_url)
        if target.scheme != "https" or target.hostname not in self.allowed_hosts or target.username or target.password:
            raise ImportError("upstream redirected outside the allowed HTTPS host")
        redirected = super().redirect_request(request, response, code, message, headers, new_url)
        if redirected is not None and target.hostname != urllib.parse.urlsplit(request.full_url).hostname:
            redirected.remove_header("Authorization")
            redirected.unredirected_hdrs.pop("Authorization", None)
            redirected.unredirected_hdrs.pop("authorization", None)
        return redirected


def open_https(request, allowed_hosts):
    opener = urllib.request.build_opener(CheckedRedirectHandler(allowed_hosts))
    return opener.open(request, timeout=180)


def read_https(url, limit, headers=None, allowed_hosts=None):
    request = urllib.request.Request(url, headers=headers or {})
    allowed_hosts = allowed_hosts or {urllib.parse.urlsplit(url).hostname}
    with open_https(request, allowed_hosts) as response:
        final = urllib.parse.urlsplit(response.geturl())
        if final.scheme != "https" or (allowed_hosts and final.hostname not in allowed_hosts):
            raise ImportError("upstream redirected outside the allowed HTTPS host")
        data = response.read(limit + 1)
    if len(data) > limit:
        raise ImportError(f"upstream response exceeds the {limit}-byte limit")
    return data


def api_formula(name):
    if not re.fullmatch(r"[A-Za-z0-9@+._-]{1,128}", name):
        raise ImportError(f"unsupported formula name: {name}")
    url = API_ROOT + urllib.parse.quote(name, safe="@+._-") + ".json"
    try:
        data = read_https(url, MAX_FORMULA_BYTES, allowed_hosts={"formulae.brew.sh"})
    except urllib.error.HTTPError as error:
        if error.code == 404:
            raise ImportError(f"formula not found in Homebrew/core: {name}") from error
        raise ImportError(f"Homebrew formula API request failed ({error.code}): {name}") from error
    except urllib.error.URLError as error:
        raise ImportError(f"Homebrew formula API request failed: {name}") from error
    try:
        formula = json.loads(data)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ImportError(f"invalid formula API JSON for {name}") from error
    if not isinstance(formula, dict) or formula.get("name") != name or formula.get("tap") != "homebrew/core":
        raise ImportError(f"formula API identity is not the requested homebrew/core formula: {name}")
    return formula, hashlib.sha256(data).hexdigest()


def bottle_for(formula, tag):
    upstream_tag = "arm64_linux" if tag == "aarch64_linux" else tag
    try:
        return formula["bottle"]["stable"]["files"][upstream_tag]
    except (KeyError, TypeError):
        raise ImportError(f"{formula.get('name')}: no stable bottle for target {tag}") from None


def linux_bottle_abi(formula, tag, selected):
    name = formula["name"]
    stable = formula["bottle"]["stable"]
    version = formula["versions"]["stable"]
    revision = formula["revision"]
    rebuild = stable["rebuild"]
    digest = selected.get("sha256")
    if (not isinstance(version, str) or not re.fullmatch(r"[A-Za-z0-9.+_-]+", version)
            or type(revision) is not int or revision < 0 or type(rebuild) is not int or rebuild < 0
            or not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest)):
        raise ImportError(f"{name}: invalid version, revision or rebuild for OCI metadata")
    pkg_version = version + (f"_{revision}" if revision else "")
    index_tag = pkg_version + (f"-{rebuild}" if rebuild else "")
    upstream_tag = "arm64_linux" if tag == "aarch64_linux" else tag
    image_ref = f"{pkg_version}.{upstream_tag}" + (f".{rebuild}" if rebuild else "")
    token = bottle_token(name)
    url = f"https://ghcr.io/v2/homebrew/core/{name}/manifests/{index_tag}"
    try:
        data = read_https(
            url, MAX_FORMULA_BYTES,
            headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.oci.image.index.v1+json"},
            allowed_hosts={"ghcr.io"},
        )
        index = json.loads(data)
        matches = [
            item["annotations"] for item in index["manifests"]
            if item["annotations"].get("org.opencontainers.image.ref.name") == image_ref
            and item["annotations"].get("sh.brew.bottle.digest", "").lower() == digest.lower()
        ]
    except (urllib.error.URLError, KeyError, TypeError, AttributeError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ImportError(f"{name}: invalid or unavailable OCI index metadata") from error
    if len(matches) != 1:
        raise ImportError(f"{name}: OCI index does not uniquely match the selected bottle")
    minimum = matches[0].get("sh.brew.bottle.glibc.version")
    if not isinstance(minimum, str) or not re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", minimum):
        raise ImportError(f"{name}: OCI metadata lacks a valid minimum glibc version")
    return minimum, hashlib.sha256(data).hexdigest()


def adapted_bottle(formula, tag):
    selected = dict(bottle_for(formula, tag))
    if selected.get("cellar") in (":any", ":any_skip_relocation"):
        selected["cellar"] = selected["cellar"][1:]
    provenance = {}
    if tag.endswith("_linux"):
        minimum, index_digest = linux_bottle_abi(formula, tag, selected)
        selected.update(minimum_glibc=minimum, loader="glibc")
        provenance["oci_index_sha256"] = index_digest
    return selected, provenance


def bottle_token(name):
    scope = urllib.parse.urlencode({"service": "ghcr.io", "scope": f"repository:homebrew/core/{name}:pull"})
    try:
        data = read_https(f"https://ghcr.io/token?{scope}", 1024 * 1024, allowed_hosts={"ghcr.io"})
        token = json.loads(data)["token"]
    except (urllib.error.URLError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise ImportError(f"could not acquire a public GHCR pull token for {name}") from error
    if not isinstance(token, str) or not token or len(token) > 16384:
        raise ImportError(f"GHCR returned an invalid pull token for {name}")
    return token


def download_bottle(formula, tag, directory):
    selected = bottle_for(formula, tag)
    url = selected.get("url")
    digest = selected.get("sha256")
    name = formula["name"]
    parsed = urllib.parse.urlsplit(url or "")
    expected_prefix = f"/v2/homebrew/core/{name}/blobs/sha256:"
    if (
        parsed.scheme != "https" or parsed.hostname != "ghcr.io" or parsed.username or parsed.password
        or parsed.query or parsed.fragment or not parsed.path.startswith(expected_prefix)
        or not re.fullmatch(r"[0-9a-fA-F]{64}", digest or "")
        or parsed.path.rsplit(":", 1)[-1].lower() != digest.lower()
    ):
        raise ImportError(f"{name}: selected bottle must be the matching public GHCR sha256 blob")
    token = bottle_token(name)
    headers = {"Authorization": f"Bearer {token}"}
    request = urllib.request.Request(url, headers=headers)
    path = directory / f"{name}.tar.gz"
    hasher = hashlib.sha256()
    total = 0
    try:
        with open_https(request, {"ghcr.io", "pkg-containers.githubusercontent.com"}) as response, path.open("xb") as output:
            final = urllib.parse.urlsplit(response.geturl())
            if final.scheme != "https" or final.hostname not in {"ghcr.io", "pkg-containers.githubusercontent.com"}:
                raise ImportError(f"{name}: bottle download redirected outside GHCR")
            while True:
                chunk = response.read(1024 * 1024)
                if not chunk:
                    break
                total += len(chunk)
                if total > MAX_BOTTLE_BYTES:
                    raise ImportError(f"{name}: bottle exceeds the 2 GiB download limit")
                hasher.update(chunk)
                output.write(chunk)
    except (ImportError, urllib.error.URLError, OSError) as error:
        path.unlink(missing_ok=True)
        if isinstance(error, ImportError):
            raise
        raise ImportError(f"{name}: bottle download failed") from error
    if total == 0 or hasher.hexdigest() != digest.lower():
        path.unlink(missing_ok=True)
        raise ImportError(f"{name}: bottle SHA-256 does not match the formula API")
    return path


def dependency_names(formula):
    dependencies = formula.get("dependencies", [])
    if not isinstance(dependencies, list) or any(not isinstance(value, str) for value in dependencies):
        raise ImportError(f"{formula['name']}: dependencies must be a list of formula names")
    if any("/" in value or not re.fullmatch(r"[A-Za-z0-9@+._-]{1,128}", value) for value in dependencies):
        raise ImportError(f"{formula['name']}: only homebrew/core runtime dependencies are supported")
    return dependencies


def resolve_closure(roots, fetch_formula):
    formulas = {}
    hashes = {}
    visiting = set()
    ordered = []
    failures = {}

    def visit(name, depth=0):
        if name in visiting:
            failures[name] = f"dependency cycle contains {name}"
            return
        if name in formulas:
            return
        if name in failures:
            return
        if depth > 64:
            failures[name] = "dependency graph exceeds the 64-level limit"
            return
        if len(formulas) + len(visiting) >= MAX_FORMULAS:
            failures[name] = f"dependency closure exceeds {MAX_FORMULAS} formulas"
            return
        visiting.add(name)
        try:
            formula, digest = fetch_formula(name)
            dependencies = dependency_names(formula)
        except ImportError as error:
            visiting.remove(name)
            failures[name] = str(error)
            return
        formulas[name], hashes[name] = formula, digest
        for dependency in dependencies:
            visit(dependency, depth + 1)
        visiting.remove(name)
        unavailable = [dependency for dependency in dependencies if dependency not in formulas or dependency in failures]
        if unavailable:
            failures.setdefault(name, f"unavailable dependency: {', '.join(unavailable)}")
        else:
            ordered.append(name)

    for root in roots:
        visit(root)
    return formulas, hashes, ordered, failures


def runtime_command(args):
    if args.runtime == "rust":
        if not args.rust_cli:
            raise ImportError("--rust-cli is required for --runtime rust")
        return [str(Path(args.rust_cli).resolve())]
    if not args.dotnet_assembly:
        raise ImportError("--dotnet-assembly is required for --runtime dotnet")
    return ["dotnet", str(Path(args.dotnet_assembly).resolve())]


def import_catalog(args):
    roots = list(args.formula)
    if args.formula_list:
        roots.extend(
            line.strip() for line in Path(args.formula_list).read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.lstrip().startswith("#")
        )
    roots = list(dict.fromkeys(roots))
    if not roots or len(roots) > MAX_FORMULAS:
        raise ImportError(f"provide 1..{MAX_FORMULAS} unique --formula values or a formula list")
    if args.tag not in {"arm64_sonoma", "sonoma", "x86_64_linux", "aarch64_linux"}:
        raise ImportError(f"unsupported native target tag: {args.tag}")
    if args.tag.endswith("sonoma"):
        target_os, architecture = "macOS", "arm64" if args.tag.startswith("arm64") else "x64"
    else:
        target_os, architecture = "Linux", "arm64" if args.tag.startswith("aarch64") else "x64"

    formulas, api_hashes, ordered, failures = resolve_closure(roots, api_formula)
    report = {
        "schemaVersion": 1, "target": args.tag, "requested": roots,
        "imported": [], "importedRoots": [], "importedMetadata": [],
        "skipped": [{"formula": name, "reason": reason} for name, reason in sorted(failures.items())],
    }
    cli = runtime_command(args)
    downloaded_bytes = 0
    with tempfile.TemporaryDirectory(prefix="pinget-homebrew-catalog-") as temp:
        stage = Path(temp).resolve()
        for name in ordered:
            formula = formulas[name]
            unresolved = [dep for dep in dependency_names(formula) if dep not in report["imported"]]
            if unresolved:
                report["skipped"].append({"formula": name, "reason": f"unavailable dependency: {', '.join(unresolved)}"})
                continue
            try:
                selected, provenance = adapted_bottle(formula, args.tag)
                bottle_path = download_bottle(formula, args.tag, stage)
                downloaded_bytes += bottle_path.stat().st_size
                if downloaded_bytes > MAX_CLOSURE_BOTTLE_BYTES:
                    bottle_path.unlink(missing_ok=True)
                    report["skipped"].append({
                        "formula": name,
                        "reason": "dependency closure exceeds the 2 GiB total bottle-download limit",
                    })
                    break
                metadata = formula.get("bottle", {}).get("stable", {})
                version = formula.get("versions", {}).get("stable")
                rebuild = metadata.get("rebuild")
                if not isinstance(version, str) or not isinstance(rebuild, int) or rebuild < 0:
                    raise ImportError(f"{name}: invalid stable version/rebuild metadata")
                adapted = dict(formula)
                adapted["bottle"] = dict(formula["bottle"])
                adapted["bottle"]["stable"] = dict(metadata)
                adapted["bottle"]["stable"]["files"] = {args.tag: selected}
                adapted["pinget_runtime_dependencies"] = [
                    {
                        "full_name": dependency,
                        "pkg_version": formulas[dependency]["versions"]["stable"] + (
                            f"_{formulas[dependency]['revision']}" if formulas[dependency]["revision"] else ""
                        ),
                        "bottle_rebuild": formulas[dependency]["bottle"]["stable"]["rebuild"],
                    }
                    for dependency in dependency_names(formula)
                ]
                adapted["pinget_import_provenance"] = {
                    "formula_api_sha256": api_hashes[name],
                    "bottle_sha256": selected["sha256"].lower(),
                    **provenance,
                }
                formula_path = stage / f"{name}.json"
                formula_path.write_text(json.dumps(adapted, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
                command = cli + [
                    "repository", "import-homebrew",
                    "--formula-json", str(formula_path),
                    "--bottle-archive", str(bottle_path),
                    "--manifest-output", str(Path(args.manifest_output).resolve()),
                    "--bottle-mirror", str(Path(args.bottle_mirror).resolve()),
                    "--tag", args.tag, "--target-os", target_os, "--architecture", architecture,
                    "--base-url", args.base_url,
                ]
                try:
                    subprocess.run(command, check=True, timeout=300, capture_output=True, text=True)
                except subprocess.CalledProcessError as error:
                    detail = (error.stderr or error.stdout or "offline importer failed").strip()
                    raise ImportError(detail[-2048:]) from error
                report["imported"].append(name)
                if name in roots:
                    report["importedRoots"].append(name)
                report["importedMetadata"].append({
                    "formula": name,
                    "formulaApiSha256": api_hashes[name],
                    "bottleSha256": selected["sha256"].lower(),
                    "relocation": selected["cellar"],
                    **provenance,
                    "packageVersion": f"{version}{'_' + str(formula.get('revision', 0)) if formula.get('revision', 0) else ''}.bottle.{rebuild}",
                })
                print(f"Imported {name} {version} bottle rebuild {rebuild}")
            except (ImportError, OSError, subprocess.SubprocessError, KeyError, TypeError) as error:
                report["skipped"].append({"formula": name, "reason": str(error)})
                print(f"Skipped {name}: {error}", file=sys.stderr)

    report_path = Path(args.report).resolve()
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    missing_roots = sorted(set(roots) - set(report["importedRoots"]))
    minimum = args.minimum_imported if args.minimum_imported is not None else len(roots)
    if not 1 <= minimum <= len(roots):
        raise ImportError(f"--minimum-imported must be between 1 and {len(roots)}")
    if len(report["importedRoots"]) < minimum:
        raise ImportError(f"requested formulas were not all imported: {', '.join(missing_roots)}; see {report_path}")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--formula", action="append", default=[], help="homebrew/core formula to include; repeatable")
    parser.add_argument("--formula-list", help="UTF-8 file with one formula name per line")
    parser.add_argument("--tag", required=True, choices=["arm64_sonoma", "sonoma", "x86_64_linux", "aarch64_linux"])
    parser.add_argument("--manifest-output", required=True)
    parser.add_argument("--bottle-mirror", required=True)
    parser.add_argument("--base-url", required=True, help="Public HTTPS bottle mirror URL")
    parser.add_argument("--report", required=True, help="Import outcome report path")
    parser.add_argument("--minimum-imported", type=int, help="Allow skipped roots if at least this many are imported")
    parser.add_argument("--runtime", required=True, choices=["rust", "dotnet"])
    parser.add_argument("--rust-cli")
    parser.add_argument("--dotnet-assembly")
    args = parser.parse_args()
    try:
        import_catalog(args)
    except (ImportError, OSError, urllib.error.URLError) as error:
        print(f"Homebrew catalog import failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
