"""Real pinned bottles and disposable signed-source acceptance on hosted runners."""

import argparse
import functools
import hashlib
import http.server
import io
import json
import os
import platform
import shutil
import ssl
import subprocess
import tempfile
import threading
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path


PACKAGES = {
    "oniguruma": ("6.9.10", "1a3d69a0e476e31caff1bfc669bbddcb1d6a71b6161dd90a3ef9acf04ba6f8a9"),
    "jq": ("1.8.1", "93f902ae812c900a0181e9e107b9cc29c6ee1cfc365e83458419946f6d6e4b64"),
}
PUBLISHER = "CN=Pinget Homebrew Acceptance"


def run(command, env=None, expected=0):
    result = subprocess.run(
        [str(value) for value in command], env=env, capture_output=True, text=True, timeout=180
    )
    if result.returncode != expected:
        raise RuntimeError(
            f"Command exited {result.returncode}, expected {expected}: {command}\n"
            f"{result.stdout}\n{result.stderr}"
        )
    return result.stdout


def fetch(url, token=None, digest=None):
    headers = {"Accept": "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60) as response:
        data = response.read()
    if digest and hashlib.sha256(data).hexdigest() != digest:
        raise RuntimeError(f"Digest mismatch for {url}")
    return data


def curate(work, tag):
    formulas = {}
    for name, (version, index_digest) in PACKAGES.items():
        scope = urllib.parse.urlencode({"service": "ghcr.io", "scope": f"repository:homebrew/core/{name}:pull"})
        token = json.loads(fetch(f"https://ghcr.io/token?{scope}"))["token"]
        base = f"https://ghcr.io/v2/homebrew/core/{name}"
        index = json.loads(fetch(f"{base}/manifests/sha256:{index_digest}", token, index_digest))
        upstream_tag = "arm64_linux" if tag == "aarch64_linux" else tag
        descriptors = [
            value for value in index["manifests"]
            if value["annotations"]["org.opencontainers.image.ref.name"] == f"{version}.{upstream_tag}"
        ]
        if len(descriptors) != 1:
            raise RuntimeError(f"Expected exactly one pinned {name}/{upstream_tag} descriptor")
        descriptor = descriptors[0]
        manifest_digest = descriptor["digest"].removeprefix("sha256:")
        manifest = json.loads(fetch(f"{base}/manifests/{descriptor['digest']}", token, manifest_digest))
        annotations = descriptor["annotations"]
        bottle_digest = annotations["sh.brew.bottle.digest"]
        if not any(value["digest"] == f"sha256:{bottle_digest}" for value in manifest["layers"]):
            raise RuntimeError("Pinned bottle digest is absent from its authenticated OCI manifest")
        bottle = fetch(f"{base}/blobs/sha256:{bottle_digest}", token, bottle_digest)
        (work / f"{name}.tar.gz").write_bytes(bottle)
        tab = json.loads(annotations["sh.brew.tab"])
        dependencies = tab["runtime_dependencies"]
        if any(value["full_name"] not in PACKAGES for value in dependencies):
            raise RuntimeError("The pinned dependency closure has changed")
        selected = {
            "sha256": bottle_digest, "url": f"{base}/blobs/sha256:{bottle_digest}", "cellar": "any"
        }
        if tag.endswith("_linux"):
            selected.update(minimum_glibc=annotations["sh.brew.bottle.glibc.version"], loader="glibc")
        formulas[name] = {
            "name": name, "tap": "homebrew/core", "full_name": name,
            "versions": {"stable": version}, "revision": 0, "version_scheme": 0,
            "keg_only": False, "aliases": [], "oldnames": [],
            "desc": f"Pinned real {name} bottle acceptance fixture",
            "homepage": "https://jqlang.github.io/jq/" if name == "jq" else "https://github.com/kkos/oniguruma",
            "license": annotations["sh.brew.license"],
            "dependencies": [value["full_name"] for value in dependencies],
            "pinget_runtime_dependencies": dependencies,
            "bottle": {"stable": {"rebuild": 0, "files": {tag: selected}}},
            "pinget_provenance": {
                "kind": "curated OCI adaptation, not an upstream formula API snapshot",
                "oci_index_sha256": index_digest, "oci_manifest_sha256": manifest_digest,
                "oci_bottle_tag": upstream_tag,
                "cellar_policy": "operator-classified any; native private-prefix execution is the acceptance gate",
                "catalog_rebuild": "operator-selected zero; subsequent one is a synthetic transition with unchanged bytes",
            },
        }
    return formulas


def certificates(work, openssl):
    root = work / "root.pem"
    run([openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
         "-subj", "/CN=Pinget Homebrew Disposable Root", "-keyout", work / "root.key",
         "-out", root, "-addext", "basicConstraints=critical,CA:TRUE",
         "-addext", "keyUsage=critical,keyCertSign,cRLSign"])
    for name, subject, extensions in [
        ("signer", f"/{PUBLISHER}", "extendedKeyUsage=codeSigning\n"),
        ("server", "/CN=localhost",
         "extendedKeyUsage=serverAuth\nsubjectAltName=DNS:localhost,IP:127.0.0.1\n"),
    ]:
        (work / f"{name}.ext").write_text(
            "basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\n" + extensions
        )
        run([openssl, "req", "-new", "-newkey", "rsa:2048", "-nodes", "-subj", subject,
             "-keyout", work / f"{name}.key", "-out", work / f"{name}.csr"])
        run([openssl, "x509", "-req", "-in", work / f"{name}.csr", "-CA", root,
             "-CAkey", work / "root.key", "-CAcreateserial", "-days", "1",
             "-extfile", work / f"{name}.ext", "-out", work / f"{name}.pem"])
    run([openssl, "pkcs12", "-export", "-inkey", work / "signer.key",
         "-in", work / "signer.pem", "-certfile", root, "-passout", "pass:",
         "-out", work / "signer.pfx"])
    run([openssl, "x509", "-in", root, "-outform", "DER", "-out", work / "root.cer"])
    return run([openssl, "x509", "-in", root, "-noout", "-fingerprint", "-sha1"]).strip().split("=")[1].replace(":", "")


def install_tls_trust(work):
    root = work / "root.pem"
    if platform.system() == "Darwin":
        keychain = work / "acceptance.keychain-db"
        password = "pinget-temporary-keychain"
        previous = subprocess.run(
            ["security", "list-keychains", "-d", "user"], capture_output=True, text=True, timeout=30, check=True
        ).stdout
        keychains = [line.strip().strip('"') for line in previous.splitlines() if line.strip()]
        try:
            run(["security", "create-keychain", "-p", password, keychain])
            run(["security", "set-keychain-settings", "-lut", "21600", keychain])
            run(["security", "unlock-keychain", "-p", password, keychain])
            run(["security", "list-keychains", "-d", "user", "-s", keychain, *keychains])
            run(["security", "add-trusted-cert", "-r", "trustRoot", "-k", keychain, root])
        except Exception:
            if keychain.exists():
                run(["security", "list-keychains", "-d", "user", "-s", *keychains])
                subprocess.run(["security", "delete-keychain", keychain], capture_output=True, timeout=30)
            raise
        return ("macOS", keychain, keychains)
    if platform.system() == "Windows":
        script = work / "add-tls-root.ps1"
        script.write_text(
            "$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($args[0])\n"
            "$store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')\n"
            "$store.Open('ReadWrite')\n"
            "try { $store.Add($cert) } finally { $store.Close(); $cert.Dispose() }\n"
        )
        run(["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, work / "root.cer"])
        return ("Windows", None, None)
    return ("Linux", None, None)


def remove_tls_trust(state, work, fingerprint):
    system, keychain, old_keychains = state
    if system == "macOS":
        if keychain.exists():
            run(["security", "list-keychains", "-d", "user", "-s", *old_keychains])
            run(["security", "delete-keychain", keychain])
    elif system == "Windows":
        script = work / "remove-tls-root.ps1"
        script.write_text(
            "$store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')\n"
            "$store.Open('ReadWrite')\n"
            "try { foreach ($cert in @($store.Certificates | Where-Object Thumbprint -eq $args[0])) "
            "{ $store.Remove($cert); $cert.Dispose() } } finally { $store.Close() }\n"
        )
        run(["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, fingerprint])


def private_dependencies(app, env):
    executable = app / "homebrew/bin/jq"
    if platform.system() == "Linux":
        lines = run(["ldd", executable], env).splitlines()
    else:
        files = [executable, *list((app / "homebrew/Cellar/jq/1.8.1/lib").glob("*.dylib"))]
        lines = [line for file in files for line in run(["otool", "-L", file], env).splitlines()[1:]]
    libraries = [line for line in lines if "libonig." in line or "libjq." in line]
    prefix = str(app / "homebrew") + os.sep
    if not any("libonig." in line for line in libraries) or any(prefix not in line for line in libraries):
        raise RuntimeError(f"jq did not resolve its bottle libraries inside the private prefix: {libraries}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rust-cli", type=Path, required=True)
    parser.add_argument("--dotnet-assembly", type=Path, required=True)
    parser.add_argument("--psign-tool", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, help="Absolute private temporary parent for a local Linux run")
    args = parser.parse_args()
    hosted = os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
    if platform.system() != "Linux" and not hosted:
        raise RuntimeError("This certificate-trust gate is restricted to disposable GitHub-hosted runners")
    if not hosted and not args.work_root:
        raise RuntimeError("Local Linux acceptance requires an explicit --work-root")
    runner_temp = args.work_root if not hosted else Path(os.environ["RUNNER_TEMP"])
    if not runner_temp.is_absolute():
        raise RuntimeError("RUNNER_TEMP must be absolute")
    openssl = shutil.which("openssl")
    if not openssl and platform.system() == "Windows":
        openssl = str(Path(os.environ["ProgramFiles"]) / "Git/usr/bin/openssl.exe")
    if not openssl:
        raise RuntimeError("OpenSSL is required")
    arch = "arm64" if platform.machine().lower() in ("arm64", "aarch64") else "x64"
    target_os = "macOS" if platform.system() == "Darwin" else "Linux"
    tag = ("arm64_sonoma" if arch == "arm64" else "sonoma") if target_os == "macOS" else (
        "aarch64_linux" if arch == "arm64" else "x86_64_linux"
    )
    # Windows exercises cross-platform publishing and source verification, not installation.
    if platform.system() == "Windows":
        arch, target_os, tag = "x64", "Linux", "x86_64_linux"
    clis = {"rust": [args.rust_cli.resolve()], "dotnet": ["dotnet", args.dotnet_assembly.resolve()]}
    psign = args.psign_tool.resolve()
    report = {"platform": platform.platform(), "architecture": arch, "success": False, "checks": []}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n")
    with tempfile.TemporaryDirectory(prefix="pinget-homebrew-", dir=runner_temp) as temporary:
        work = Path(temporary)
        public = work / "public"
        public.mkdir()
        fingerprint = certificates(work, openssl)
        succeeded = False
        server = None
        server_thread = None
        trust_state = None
        try:
            trust_state = install_tls_trust(work)
            server = http.server.ThreadingHTTPServer(
                ("127.0.0.1", 0), functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(public))
            )
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(work / "server.pem", work / "server.key")
            server.socket = context.wrap_socket(server.socket, server_side=True)
            server_thread = threading.Thread(target=server.serve_forever, daemon=True)
            server_thread.start()
            base = f"https://localhost:{server.server_port}"
            env = os.environ.copy()
            env.update(PINGET_SOURCE_MODE="private", SSL_CERT_FILE=str(work / "root.pem"))
            env["PATH"] = str(psign.parent) + os.pathsep + env["PATH"]
            formulas = curate(work, tag)
            report["inputs"] = {
                name: dict(value["pinget_provenance"], bottle_sha256=value["bottle"]["stable"]["files"][tag]["sha256"])
                for name, value in formulas.items()
            }

            def publish(runtime, rebuild):
                cli = clis[runtime]
                generation = work / f"{runtime}-{rebuild}"
                for name, original in formulas.items():
                    formula = json.loads(json.dumps(original))
                    if name == "jq":
                        formula["bottle"]["stable"]["rebuild"] = rebuild
                    source = work / f"{name}-{rebuild}.json"
                    source.write_text(json.dumps(formula))
                    run(cli + ["repository", "import-homebrew", "--formula-json", source,
                               "--bottle-archive", work / f"{name}.tar.gz",
                               "--manifest-output", generation / "input/manifests",
                               "--bottle-mirror", generation / "input/bottles",
                               "--tag", tag, "--target-os", target_os, "--architecture", arch,
                               "--base-url", f"{base}/{runtime}/bottles"], env)
                state, msix = generation / "state", generation / "brew.msix"
                run(cli + ["repository", "build", "--input", generation / "input", "--state", state], env)
                run(cli + ["repository", "package", "--state", state, "--name", "Pinget.HomebrewSource",
                           "--publisher", PUBLISHER, "--version", f"1.0.0.{rebuild}", "--msix", msix], env)
                run([psign, "sign", "--mode", "portable", "--pfx", work / "signer.pfx", "--password", "", msix], env)
                run(cli + ["repository", "publish", "--state", state, "--destination", public / runtime,
                           "--msix", msix, "--psign-tool", psign, "--trusted-ca", work / "root.pem"], env)
                report["checks"].append(f"{runtime} signed publishing generation {rebuild}")

            for runtime in clis:
                publish(runtime, 0)
            clients = []
            for consumer, cli in clis.items():
                for publisher in clis:
                    app = work / f"{consumer}-from-{publisher}"
                    client_env = dict(env, PINGET_APPROOT=str(app))
                    run(cli + ["source", "add", "--name", "homebrew", "--arg", f"{base}/{publisher}",
                               "--type", "Pinget.PreIndexed.Homebrew", "--publisher", PUBLISHER,
                               "--trusted-ca", work / "root.pem"], client_env)
                    run(cli + ["source", "update", "homebrew"], client_env)
                    assert "Pinget.Homebrew.jq" in run(cli + ["search", "jq", "--source", "homebrew"], client_env)
                    if platform.system() != "Windows":
                        run(cli + ["install", "jq", "--source", "homebrew", "--silent",
                                   "--accept-package-agreements"], client_env)
                        assert run([app / "homebrew/bin/jq", "-n", '"abc" | test("^a")'], client_env).strip() == "true"
                        private_dependencies(app, client_env)
                        assert "oniguruma" in run(cli + ["list"], client_env)
                    clients.append((consumer, publisher, app, client_env))
                    report["checks"].append(f"{consumer} consumed {publisher} signed static source")
            for runtime in clis:
                publish(runtime, 1)
            for consumer, publisher, app, client_env in clients:
                cli = clis[consumer]
                run(cli + ["source", "update", "homebrew"], client_env)
                if platform.system() == "Windows":
                    continue
                run(cli + ["upgrade", "jq", "--source", "homebrew", "--silent"], client_env)
                receipt = json.loads((app / "homebrew/receipts/jq.json").read_text())
                assert receipt["Package"]["PackageVersion"] == "1.8.1.bottle.1"
                assert receipt["Package"]["KegVersion"] == "1.8.1"
                executable = app / "homebrew/Cellar/jq/1.8.1/bin/jq"
                executable.chmod(executable.stat().st_mode & ~0o111)
                opposite = clis["dotnet" if consumer == "rust" else "rust"]
                run(opposite + ["repair", "jq", "--source", "homebrew", "--silent"], client_env)
                assert run([app / "homebrew/bin/jq", "-n", '"abc" | test("^a")'], client_env).strip() == "true"
                private_dependencies(app, client_env)
                refusal = subprocess.run(
                    opposite + ["uninstall", "oniguruma", "--silent"], env=client_env,
                    capture_output=True, text=True, timeout=60
                )
                assert refusal.returncode == 1 and "depends on" in refusal.stderr
                for name in ["jq", "oniguruma"]:
                    run(opposite + ["uninstall", name, "--silent"], client_env)
                assert not list((app / "homebrew/receipts").glob("*.json"))
                assert not list((app / "homebrew/bin").iterdir())
                assert not list((app / "homebrew/opt").iterdir())
                report["checks"].append(f"{consumer}/{publisher}: synthetic rebuild upgrade, cross-runtime repair/uninstall")
            for consumer, publisher, app, client_env in clients:
                package = public / publisher / "brew.msix"
                original = package.read_bytes()
                cache = app / "sources/homebrew"
                before = {name: (cache / name).read_bytes() for name in ["brew.msix", "index.db"]}
                try:
                    with zipfile.ZipFile(io.BytesIO(original)) as src, zipfile.ZipFile(package, "w") as dst:
                        for info in src.infolist():
                            data = src.read(info.filename)
                            dst.writestr(info, data + b"\0" if info.filename.endswith(".db") else data)
                    failure = subprocess.run(
                        clis[consumer] + ["source", "update", "homebrew"], env=client_env,
                        capture_output=True, text=True, timeout=60
                    )
                    assert failure.returncode != 0
                    assert before == {name: (cache / name).read_bytes() for name in before}
                    assert "Pinget.Homebrew.jq" in run(clis[consumer] + ["search", "jq", "--source", "homebrew"], client_env)
                finally:
                    package.write_bytes(original)
                report["checks"].append(f"{consumer}/{publisher}: tamper rejection preserves active cache")
            succeeded = True
        finally:
            if server and server_thread:
                server.shutdown()
                server_thread.join(timeout=10)
                server.server_close()
            if trust_state:
                remove_tls_trust(trust_state, work, fingerprint)
            report["success"] = succeeded
            args.report.write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
