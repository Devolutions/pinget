#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RustCli,
    [Parameter(Mandatory)][string] $DotnetAssembly,
    [Parameter(Mandatory)][string] $PsignTool,
    [Parameter(Mandatory)][string] $Report,
    [string] $WorkRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-GateProcess {
    param([string[]] $Command, [System.Collections.IDictionary] $Environment = @{}, [int] $Timeout = 180)
    $start = [System.Diagnostics.ProcessStartInfo]::new($Command[0])
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    for ($index = 1; $index -lt $Command.Count; $index++) { $start.ArgumentList.Add($Command[$index]) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = [string]$Environment[$key] }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not start $($Command[0])" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Timeout * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Command timed out after ${Timeout}s: $Command"
        }
        return @{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Invoke-GateCommand {
    param([string[]] $Command, [System.Collections.IDictionary] $Environment = @{}, [int] $Expected = 0, [int] $Timeout = 180)
    $result = Invoke-GateProcess $Command $Environment $Timeout
    if ($result.ExitCode -ne $Expected) {
        throw "Command exited $($result.ExitCode), expected ${Expected}: $Command`n$($result.Output)`n$($result.Error)"
    }
    return $result.Output
}

function Assert-Gate {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Write-GateJson {
    param([string] $Path, $Value)
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path))
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 50) + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Read-GateJson {
    param([string] $Path)
    return [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable
}

function Get-PinnedBytes {
    param([string] $Url, [string] $Token, [string] $Digest)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
    $request.Headers.Accept.ParseAdd('application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json')
    if ($Token) { $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token) }
    try {
        $response = $script:upstream.SendAsync($request).GetAwaiter().GetResult()
        try {
            [void]$response.EnsureSuccessStatusCode()
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            if ($Digest -and [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -ne $Digest) {
                throw "Digest mismatch for $Url"
            }
            return ,$bytes
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Get-PinnedJson {
    param([string] $Url, [string] $Token, [string] $Digest)
    return [System.Text.Encoding]::UTF8.GetString((Get-PinnedBytes $Url $Token $Digest)) | ConvertFrom-Json -AsHashtable
}

function Get-CuratedFormulas {
    param([string] $Work, [string] $Tag)
    $packages = [ordered]@{
        oniguruma = @('6.9.10', '1a3d69a0e476e31caff1bfc669bbddcb1d6a71b6161dd90a3ef9acf04ba6f8a9')
        jq = @('1.8.1', '93f902ae812c900a0181e9e107b9cc29c6ee1cfc365e83458419946f6d6e4b64')
    }
    $formulas = [ordered]@{}
    foreach ($name in $packages.Keys) {
        $version, $indexDigest = $packages[$name]
        $scope = [uri]::EscapeDataString("repository:homebrew/core/${name}:pull")
        $token = (Get-PinnedJson "https://ghcr.io/token?service=ghcr.io&scope=$scope").token
        $base = "https://ghcr.io/v2/homebrew/core/$name"
        $index = Get-PinnedJson "$base/manifests/sha256:$indexDigest" $token $indexDigest
        $upstreamTag = if ($Tag -eq 'aarch64_linux') { 'arm64_linux' } else { $Tag }
        $descriptors = @($index.manifests | Where-Object { $_.annotations['org.opencontainers.image.ref.name'] -eq "$version.$upstreamTag" })
        Assert-Gate ($descriptors.Count -eq 1) "Expected exactly one pinned $name/$upstreamTag descriptor"
        $descriptor = $descriptors[0]
        $manifestDigest = $descriptor.digest.Substring(7)
        $manifest = Get-PinnedJson "$base/manifests/$($descriptor.digest)" $token $manifestDigest
        $annotations = $descriptor.annotations
        $bottleDigest = $annotations['sh.brew.bottle.digest']
        Assert-Gate (@($manifest.layers | Where-Object { $_.digest -eq "sha256:$bottleDigest" }).Count -gt 0) 'Pinned bottle digest is absent from its authenticated OCI manifest'
        [System.IO.File]::WriteAllBytes((Join-Path $Work "$name.tar.gz"), (Get-PinnedBytes "$base/blobs/sha256:$bottleDigest" $token $bottleDigest))
        $tab = $annotations['sh.brew.tab'] | ConvertFrom-Json -AsHashtable
        $dependencies = @($tab.runtime_dependencies)
        Assert-Gate (@($dependencies | Where-Object { -not $packages.Contains($_.full_name) }).Count -eq 0) 'The pinned dependency closure has changed'
        $selected = @{ sha256 = $bottleDigest; url = "$base/blobs/sha256:$bottleDigest"; cellar = 'any' }
        if ($Tag.EndsWith('_linux')) {
            $selected.minimum_glibc = $annotations['sh.brew.bottle.glibc.version']
            $selected.loader = 'glibc'
        }
        $formulas[$name] = @{
            name = $name; tap = 'homebrew/core'; full_name = $name
            versions = @{ stable = $version }; revision = 0; version_scheme = 0
            keg_only = $false; aliases = @(); oldnames = @()
            desc = "Pinned real $name bottle acceptance fixture"
            homepage = $(if ($name -eq 'jq') { 'https://jqlang.github.io/jq/' } else { 'https://github.com/kkos/oniguruma' })
            license = $annotations['sh.brew.license']
            dependencies = @($dependencies | ForEach-Object { $_.full_name })
            pinget_runtime_dependencies = $dependencies
            bottle = @{ stable = @{ rebuild = 0; files = @{ $Tag = $selected } } }
            pinget_provenance = @{
                kind = 'curated OCI adaptation, not an upstream formula API snapshot'
                oci_index_sha256 = $indexDigest; oci_manifest_sha256 = $manifestDigest; oci_bottle_tag = $upstreamTag
                cellar_policy = 'operator-classified any; native private-prefix execution is the acceptance gate'
                catalog_rebuild = 'operator-selected zero; subsequent one is a synthetic transition with unchanged bytes'
            }
        }
    }
    return $formulas
}

function New-GateCertificates {
    param([string] $Work, [string] $OpenSsl)
    $config = Join-Path $Work 'certificates.cnf'
    # Isolate OpenSSL/LibreSSL defaults; Apple rejects duplicate certificate extensions.
    [System.IO.File]::WriteAllText($config, @'
[req]
distinguished_name = dn
prompt = no
[dn]
[root]
basicConstraints = critical,CA:TRUE
keyUsage = critical,keyCertSign,cRLSign
subjectKeyIdentifier = hash
[signer]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = codeSigning
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid
[server]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature,keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = DNS:localhost
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid
'@)
    $environment = @{ OPENSSL_CONF = $config }
    $root = Join-Path $Work 'root.pem'
    $null = Invoke-GateCommand @($OpenSsl, 'req', '-config', $config, '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256',
        '-days', '1', '-subj', '/CN=Pinget Homebrew Disposable Root', '-extensions', 'root',
        '-keyout', (Join-Path $Work 'root.key'), '-out', $root) $environment
    $serial = 2
    foreach ($name in @('signer', 'server')) {
        $subject = if ($name -eq 'signer') { "/$script:publisher" } else { '/CN=localhost' }
        $null = Invoke-GateCommand @($OpenSsl, 'req', '-config', $config, '-new', '-newkey', 'rsa:2048', '-nodes', '-sha256',
            '-subj', $subject, '-keyout', (Join-Path $Work "$name.key"), '-out', (Join-Path $Work "$name.csr")) $environment
        $null = Invoke-GateCommand @($OpenSsl, 'x509', '-req', '-sha256', '-in', (Join-Path $Work "$name.csr"), '-CA', $root,
            '-CAkey', (Join-Path $Work 'root.key'), '-set_serial', "$serial", '-days', '1',
            '-extfile', $config, '-extensions', $name, '-out', (Join-Path $Work "$name.pem")) $environment
        $serial++
    }
    foreach ($name in @('root', 'signer', 'server')) {
        $text = Invoke-GateCommand @($OpenSsl, 'x509', '-in', (Join-Path $Work "$name.pem"), '-noout', '-text') $environment
        $extensions = @($text -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_.StartsWith('X509v3 ') })
        Assert-Gate ($extensions.Count -eq @($extensions | Sort-Object -Unique).Count) "$name certificate has duplicate extensions"
    }
    if ($IsMacOS) {
        $null = Invoke-GateCommand @('security', 'verify-cert', '-c', (Join-Path $Work 'server.pem'), '-r', $root, '-p', 'ssl', '-s', 'localhost', '-L', '-l')
    }
    $null = Invoke-GateCommand @($OpenSsl, 'pkcs12', '-export', '-inkey', (Join-Path $Work 'signer.key'),
        '-in', (Join-Path $Work 'signer.pem'), '-certfile', $root, '-passout', 'pass:', '-out', (Join-Path $Work 'signer.pfx')) $environment
}

function Assert-PrivateDependencies {
    param([string] $App, [System.Collections.IDictionary] $Environment)
    $executable = Join-Path $App 'homebrew/bin/jq'
    if ($IsLinux) { $lines = (Invoke-GateCommand @('ldd', $executable) $Environment) -split '\r?\n' }
    else {
        $files = @($executable) + @(Get-ChildItem -LiteralPath (Join-Path $App 'homebrew/Cellar/jq/1.8.1/lib') -Filter '*.dylib' | ForEach-Object { $_.FullName })
        $lines = @($files | ForEach-Object { ((Invoke-GateCommand @('otool', '-L', $_) $Environment) -split '\r?\n') | Select-Object -Skip 1 })
    }
    $libraries = @($lines | Where-Object { $_.Contains('libonig.') -or $_.Contains('libjq.') })
    $prefix = (Join-Path $App 'homebrew') + [System.IO.Path]::DirectorySeparatorChar
    Assert-Gate (@($libraries | Where-Object { $_.Contains('libonig.') }).Count -gt 0 -and
        @($libraries | Where-Object { -not $_.Contains($prefix) }).Count -eq 0) "jq did not resolve its bottle libraries inside the private prefix: $libraries"
}

function Invoke-Interrupted {
    param([string[]] $Command, [System.Collections.IDictionary] $Environment, [string] $Point)
    $crashEnvironment = @{} + $Environment
    $crashEnvironment.PINGET_HOMEBREW_TEST_INTERRUPT = $Point
    $null = Invoke-GateCommand $Command $crashEnvironment -Expected 86 -Timeout 600
    Assert-Gate ([System.IO.File]::Exists((Join-Path $Environment.PINGET_APPROOT 'homebrew/transaction.json'))) "Interruption at $Point left no recovery journal"
}

function Assert-Recovered {
    param([string] $App)
    Assert-Gate (-not (Test-Path -LiteralPath (Join-Path $App 'homebrew/transaction.json')) -and
        @(Get-ChildItem -LiteralPath (Join-Path $App 'homebrew/.staging') -Force).Count -eq 0) 'Recovery left a journal or staging data behind'
}

function Test-GateSymlink {
    param([string] $Path)
    return (Test-Path -LiteralPath $Path) -and $null -ne (Get-Item -LiteralPath $Path -Force).LinkTarget
}

function New-GateHost {
    param([string] $Root, [string] $CertificatePath, [string] $KeyPath)
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($CertificatePath, $KeyPath)
    try {
        if ($IsWindows) {
            # Schannel cannot use the ephemeral key created by the PEM loader.
            # UserKeySet without PersistKeySet deletes the temporary key on disposal.
            $imported = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx),
                [string]::Empty, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
            $certificate.Dispose()
            $certificate = $imported
        }
        return [HomebrewGateHost]::new($Root, $certificate)
    }
    catch {
        $certificate.Dispose()
        throw
    }
}

function Publish-GateGeneration {
    param([string] $Runtime, [int] $Rebuild)
    $cli = $script:clis[$Runtime]
    $generation = Join-Path $script:work "$Runtime-$Rebuild"
    foreach ($name in $script:formulas.Keys) {
        $formula = $script:formulas[$name] | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable
        if ($name -eq 'jq') { $formula.bottle.stable.rebuild = $Rebuild }
        $source = Join-Path $script:work "$name-$Rebuild.json"
        Write-GateJson $source $formula
        $null = Invoke-GateCommand ($cli + @('repository', 'import-homebrew', '--formula-json', $source,
            '--bottle-archive', (Join-Path $script:work "$name.tar.gz"),
            '--manifest-output', (Join-Path $generation 'input/manifests'), '--bottle-mirror', (Join-Path $generation 'input/bottles'),
            '--tag', $script:tag, '--target-os', $script:targetOs, '--architecture', $script:arch,
            '--base-url', "$script:base/$Runtime/bottles")) $script:environment
    }
    $state = Join-Path $generation 'state'
    $msix = Join-Path $generation 'brew.msix'
    $null = Invoke-GateCommand ($cli + @('repository', 'build', '--input', (Join-Path $generation 'input'), '--state', $state)) $script:environment
    $null = Invoke-GateCommand ($cli + @('repository', 'package', '--state', $state, '--name', 'Pinget.HomebrewSource',
        '--publisher', $script:publisher, '--version', "1.0.0.$Rebuild", '--msix', $msix)) $script:environment
    $null = Invoke-GateCommand @($script:psign, 'sign', '--mode', 'portable', '--pfx', (Join-Path $script:work 'signer.pfx'), '--password', '', $msix) $script:environment
    $null = Invoke-GateCommand ($cli + @('repository', 'publish', '--state', $state, '--destination', (Join-Path $script:public $Runtime),
        '--msix', $msix, '--psign-tool', $script:psign, '--trusted-ca', (Join-Path $script:work 'root.pem'))) $script:environment
    $script:result.checks.Add("$Runtime signed publishing generation $Rebuild")
    Remove-Item -LiteralPath $generation -Recurse -Force
}

# A loopback-only TLS host keeps acceptance independent of external server tools and host trust stores.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
public sealed class HomebrewGateHost : IDisposable
{
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    readonly X509Certificate2 certificate;
    readonly string root;
    readonly Task loop;
    readonly List<Task> requests = new List<Task>();
    volatile bool stopping;
    volatile string lastTlsFailure;
    public int Port { get; }
    public HomebrewGateHost(string root, X509Certificate2 serverCertificate)
    {
        this.root = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        certificate = serverCertificate;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(async () => {
            while (!stopping) {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (SocketException) when (stopping) { break; }
                catch (ObjectDisposedException) when (stopping) { break; }
                lock (requests) { requests.Add(Task.Run(() => Serve(client))); }
            }
        });
    }
    void Serve(TcpClient client)
    {
        using (client)
        using (var tls = new SslStream(client.GetStream(), false))
        {
            try {
                client.ReceiveTimeout = client.SendTimeout = 30000;
                tls.ReadTimeout = tls.WriteTimeout = 30000;
                tls.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
                using var reader = new StreamReader(tls, Encoding.ASCII, false, 4096, true);
                string line = reader.ReadLine();
                if (line == null || line.Length > 8192) return;
                string[] parts = line.Split(' ');
                string header;
                int headers = 0;
                while (!string.IsNullOrEmpty(header = reader.ReadLine())) {
                    if (++headers > 100 || header.Length > 8192) return;
                }
                string status = "404 Not Found";
                string path = null;
                if (parts.Length >= 2 && (parts[0] == "GET" || parts[0] == "HEAD")) {
                    string relative = Uri.UnescapeDataString(new Uri("http://localhost" + parts[1]).AbsolutePath.TrimStart('/'));
                    if (!relative.Contains("\\") && !Path.IsPathRooted(relative)) {
                        string candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (candidate.StartsWith(root, StringComparison.Ordinal) && File.Exists(candidate)) {
                            path = candidate;
                            status = "200 OK";
                        }
                    }
                }
                long length = path == null ? 0 : new FileInfo(path).Length;
                byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: " + length +
                    "\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                tls.Write(bytes, 0, bytes.Length);
                if (path != null && parts[0] == "GET") {
                    using var file = File.OpenRead(path);
                    file.CopyTo(tls);
                }
                tls.Flush();
            }
            // Clients deliberately reject the test host before applying source-scoped trust.
            catch (AuthenticationException error) { lastTlsFailure = error.ToString(); }
            catch (IOException error) { Console.Error.WriteLine("Loopback client disconnected: " + error.Message); }
        }
    }
    public void CheckReady(string rootPem, string path)
    {
        using var anchor = X509Certificate2.CreateFromPem(File.ReadAllText(rootPem));
        using var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) => {
            if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(anchor);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try {
            using var response = client.GetAsync("https://localhost:" + Port + "/" + path).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException error) {
            throw new InvalidOperationException("Loopback HTTPS readiness failed: " + error +
                "\nServer TLS failure: " + (lastTlsFailure ?? "none recorded"), error);
        }
    }
    public void Dispose()
    {
        stopping = true;
        listener.Stop();
        try {
            loop.GetAwaiter().GetResult();
            Task[] pending;
            lock (requests) { pending = requests.ToArray(); }
            Task.WhenAll(pending).GetAwaiter().GetResult();
        }
        finally { certificate.Dispose(); }
    }
}
'@

if ($MyInvocation.InvocationName -eq '.') { return }

$hosted = $env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_ENVIRONMENT -eq 'github-hosted'
Assert-Gate ($IsLinux -or $hosted) 'This certificate-trust gate is restricted to disposable GitHub-hosted runners outside Linux'
Assert-Gate ($hosted -or $WorkRoot) 'Local Linux acceptance requires an explicit -WorkRoot'
$temporaryRoot = if ($hosted) { $env:RUNNER_TEMP } else { $WorkRoot }
Assert-Gate ([System.IO.Path]::IsPathFullyQualified($temporaryRoot)) 'Temporary parent must be absolute'
$openSslCommand = Get-Command openssl -ErrorAction SilentlyContinue
$openSsl = if ($openSslCommand) { $openSslCommand.Source } elseif ($IsWindows) { Join-Path $env:ProgramFiles 'Git/usr/bin/openssl.exe' } else { $null }
Assert-Gate ($openSsl -and (Test-Path -LiteralPath $openSsl -PathType Leaf)) 'OpenSSL is required'
$script:publisher = 'CN=Pinget Homebrew Acceptance'
$script:arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' } else { 'x64' }
$script:targetOs = if ($IsMacOS) { 'macOS' } else { 'Linux' }
$script:tag = if ($IsMacOS) { if ($script:arch -eq 'arm64') { 'arm64_sonoma' } else { 'sonoma' } }
    else { if ($script:arch -eq 'arm64') { 'aarch64_linux' } else { 'x86_64_linux' } }
if ($IsWindows) { $script:arch = 'x64'; $script:tag = 'x86_64_linux' }
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script:clis = [ordered]@{ rust = @((Resolve-Path -LiteralPath $RustCli).Path); dotnet = @('dotnet', (Resolve-Path -LiteralPath $DotnetAssembly).Path) }
$script:psign = (Resolve-Path -LiteralPath $PsignTool).Path
$reportPath = [System.IO.Path]::GetFullPath($Report)
$script:result = @{
    platform = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = $script:arch; success = $false; checks = [System.Collections.Generic.List[string]]::new()
}
Write-GateJson $reportPath $script:result
$script:work = Join-Path $temporaryRoot ("pinget-homebrew-" + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($script:work)
$script:public = Join-Path $script:work 'public'
[void][System.IO.Directory]::CreateDirectory($script:public)
$server = $null
$script:upstream = [System.Net.Http.HttpClient]::new()
$script:upstream.Timeout = [timespan]::FromSeconds(60)
try {
    New-GateCertificates $script:work $openSsl
    $server = New-GateHost $script:public (Join-Path $script:work 'server.pem') (Join-Path $script:work 'server.key')
    [System.IO.File]::WriteAllText((Join-Path $script:public 'ready'), 'ready')
    $server.CheckReady((Join-Path $script:work 'root.pem'), 'ready')
    $script:base = "https://localhost:$($server.Port)"
    $script:environment = @{ PINGET_SOURCE_MODE = 'private'; PATH = (Split-Path $script:psign) + [System.IO.Path]::PathSeparator + $env:PATH }
    $script:formulas = Get-CuratedFormulas $script:work $script:tag
    $script:result.inputs = @{}
    foreach ($name in $script:formulas.Keys) {
        $inputMetadata = @{} + $script:formulas[$name].pinget_provenance
        $inputMetadata.bottle_sha256 = $script:formulas[$name].bottle.stable.files[$script:tag].sha256
        $script:result.inputs[$name] = $inputMetadata
    }
    $catalogInput = $null
    $catalogRoots = @()
    if (-not $IsWindows) {
        $catalogRoot = Join-Path $script:work 'catalog'
        $catalogInput = Join-Path $catalogRoot 'input'
        $catalogReport = Join-Path $catalogRoot 'import-report.json'
        try {
            $null = Invoke-GateCommand @((Join-Path $PSHOME 'pwsh'), '-NoLogo', '-NoProfile', '-File', (Join-Path $repo 'scripts/Import-HomebrewCatalog.ps1'),
                '-FormulaList', (Join-Path $repo 'tests/homebrew/acceptance-corpus.txt'), '-MinimumImported', '20', '-Tag', $script:tag,
                '-Runtime', 'rust', '-RustCli', $script:clis.rust[0],
                '-ManifestOutput', (Join-Path $catalogInput 'manifests'), '-BottleMirror', (Join-Path $catalogInput 'bottles'),
                '-BaseUrl', "$script:base/catalog/bottles", '-Report', $catalogReport) $script:environment -Timeout 3600
        }
        finally {
            if ([System.IO.File]::Exists($catalogReport)) { $script:result.catalogImport = Read-GateJson $catalogReport }
        }
        $catalogResult = Read-GateJson $catalogReport
        $catalogRoots = @($catalogResult.importedRoots | Select-Object -First 20)
        Assert-Gate ($catalogRoots.Count -ge 20) 'The live Homebrew catalog acceptance imported fewer than 20 formula roots'
        $fixedCellarRoots = @($catalogResult.importedMetadata | Where-Object {
            $_.formula -in $catalogResult.importedRoots -and $_.relocation -notin @('any', 'any_skip_relocation')
        } | ForEach-Object { $_.formula })
        if ($fixedCellarRoots.Count -gt 0 -and $fixedCellarRoots[0] -notin $catalogRoots) { $catalogRoots[-1] = $fixedCellarRoots[0] }
        $catalogBottles = Join-Path $script:public 'catalog/bottles'
        [void][System.IO.Directory]::CreateDirectory($catalogBottles)
        foreach ($bottle in Get-ChildItem -LiteralPath (Join-Path $catalogInput 'bottles') -File) {
            $null = New-Item -ItemType HardLink -Path (Join-Path $catalogBottles $bottle.Name) -Target $bottle.FullName
        }
        $script:result.catalog = @{
            target = $script:tag; importedRoots = $catalogRoots; fixedCellarRoots = $fixedCellarRoots
            importedMetadata = $catalogResult.importedMetadata; skipped = $catalogResult.skipped
        }
    }
    foreach ($runtime in $script:clis.Keys) { Publish-GateGeneration $runtime 0 }
    $clients = [System.Collections.Generic.List[object]]::new()
    if ($IsWindows) { $script:result.checks.Add('Windows Rust/.NET publishing and signature verification (native clients disabled)') }
    else {
        foreach ($consumer in $script:clis.Keys) {
            $cli = $script:clis[$consumer]
            $oppositeName = if ($consumer -eq 'rust') { 'dotnet' } else { 'rust' }
            $opposite = $script:clis[$oppositeName]
            foreach ($sourcePublisher in $script:clis.Keys) {
                $app = Join-Path $script:work "$consumer-from-$sourcePublisher"
                $clientEnvironment = @{} + $script:environment
                $clientEnvironment.PINGET_APPROOT = $app
                $null = Invoke-GateCommand ($cli + @('source', 'add', '--name', 'homebrew', '--arg', "$script:base/$sourcePublisher",
                    '--type', 'Pinget.PreIndexed.Homebrew', '--publisher', $script:publisher, '--trusted-ca', (Join-Path $script:work 'root.pem'))) $clientEnvironment
                $null = Invoke-GateCommand ($cli + @('source', 'update', 'homebrew')) $clientEnvironment
                Assert-Gate ((Invoke-GateCommand ($cli + @('search', 'jq', '--source', 'homebrew')) $clientEnvironment).Contains('Pinget.Homebrew.jq')) 'Signed source jq search failed'
                Invoke-Interrupted ($cli + @('install', 'jq', '--source', 'homebrew', '--silent', '--accept-package-agreements')) $clientEnvironment 'jq:install-link'
                Assert-Gate (Test-GateSymlink (Join-Path $app 'homebrew/bin/jq')) 'Interrupted install left no jq link'
                Assert-Gate ((Invoke-GateCommand ($opposite + @('list')) $clientEnvironment).Contains('oniguruma')) 'Interrupted install lost its completed dependency'
                Assert-Recovered $app
                Assert-Gate ([System.IO.File]::Exists((Join-Path $app 'homebrew/receipts/oniguruma.json'))) 'Recovery lost dependency receipt'
                Assert-Gate (-not (Test-Path -LiteralPath (Join-Path $app 'homebrew/receipts/jq.json'))) 'Recovery kept jq receipt'
                Assert-Gate (-not (Test-Path -LiteralPath (Join-Path $app 'homebrew/bin/jq')) -and
                    -not (Test-Path -LiteralPath (Join-Path $app 'homebrew/Cellar/jq'))) 'Recovery kept partial jq keg or link'
                $script:result.checks.Add("$consumer/${sourcePublisher}: interrupted install kept the dependency and $oppositeName rolled back jq")
                $null = Invoke-GateCommand ($cli + @('install', 'jq', '--source', 'homebrew', '--silent', '--accept-package-agreements')) $clientEnvironment
                Assert-Gate ((Invoke-GateCommand @((Join-Path $app 'homebrew/bin/jq'), '-n', '"abc" | test("^a")') $clientEnvironment).Trim() -eq 'true') 'Installed jq execution failed'
                Assert-PrivateDependencies $app $clientEnvironment
                Assert-Gate ((Invoke-GateCommand ($cli + @('list')) $clientEnvironment).Contains('oniguruma')) 'Dependency list failed'
                $clients.Add(@{ Consumer = $consumer; Publisher = $sourcePublisher; App = $app; Environment = $clientEnvironment })
                $script:result.checks.Add("$consumer consumed $sourcePublisher signed static source")
            }
            $app = Join-Path $script:work "$consumer-hostname-mismatch"
            $clientEnvironment = @{} + $script:environment
            $clientEnvironment.PINGET_APPROOT = $app
            $null = Invoke-GateCommand ($cli + @('source', 'add', '--name', 'wrong-host', '--arg', "https://127.0.0.1:$($server.Port)",
                '--type', 'Pinget.PreIndexed.Homebrew', '--publisher', $script:publisher, '--trusted-ca', (Join-Path $script:work 'root.pem'))) $clientEnvironment
            $failure = Invoke-GateProcess ($cli + @('source', 'update', 'wrong-host')) $clientEnvironment -Timeout 60
            Assert-Gate ($failure.ExitCode -ne 0 -and @(Get-ChildItem -LiteralPath $app -Recurse -Filter 'brew.msix').Count -eq 0) 'Client accepted a hostname mismatch'
            $script:result.checks.Add("$consumer rejected a trusted CA with a hostname mismatch")
        }
    }
    foreach ($runtime in $script:clis.Keys) { Publish-GateGeneration $runtime 1 }
    foreach ($client in $clients) {
        $consumer, $sourcePublisher, $app, $clientEnvironment = $client.Consumer, $client.Publisher, $client.App, $client.Environment
        $cli = $script:clis[$consumer]
        $oppositeName = if ($consumer -eq 'rust') { 'dotnet' } else { 'rust' }
        $opposite = $script:clis[$oppositeName]
        $null = Invoke-GateCommand ($cli + @('source', 'update', 'homebrew')) $clientEnvironment
        $receiptPath = Join-Path $app 'homebrew/receipts/jq.json'
        $previous = (Read-GateJson $receiptPath).Package.PackageVersion
        Invoke-Interrupted ($cli + @('upgrade', 'jq', '--source', 'homebrew', '--silent')) $clientEnvironment 'jq:install-receipt'
        $null = Invoke-GateCommand ($opposite + @('list')) $clientEnvironment
        Assert-Recovered $app
        Assert-Gate ((Read-GateJson $receiptPath).Package.PackageVersion -eq $previous) 'Interrupted upgrade did not restore the previous version'
        Assert-Gate ((Invoke-GateCommand @((Join-Path $app 'homebrew/bin/jq'), '-n', '"abc" | test("^a")') $clientEnvironment).Trim() -eq 'true') 'Previous jq stopped working after interrupted upgrade'
        Assert-PrivateDependencies $app $clientEnvironment
        $script:result.checks.Add("$consumer/${sourcePublisher}: interrupted upgrade restored the working keg via $oppositeName")
        $null = Invoke-GateCommand ($cli + @('upgrade', 'jq', '--source', 'homebrew', '--silent')) $clientEnvironment
        $receipt = Read-GateJson $receiptPath
        Assert-Gate ($receipt.Package.PackageVersion -eq '1.8.1.bottle.1' -and $receipt.Package.KegVersion -eq '1.8.1') 'Rebuild-only upgrade receipt is incorrect'
        $executable = Join-Path $app 'homebrew/Cellar/jq/1.8.1/bin/jq'
        [System.IO.File]::SetUnixFileMode($executable, ([System.IO.File]::GetUnixFileMode($executable) -band (-bnot 73)))
        $null = Invoke-GateCommand ($opposite + @('repair', 'jq', '--source', 'homebrew', '--silent')) $clientEnvironment
        Assert-Gate ((Invoke-GateCommand @((Join-Path $app 'homebrew/bin/jq'), '-n', '"abc" | test("^a")') $clientEnvironment).Trim() -eq 'true') 'Cross-runtime repair failed'
        Assert-PrivateDependencies $app $clientEnvironment
        $refusal = Invoke-GateProcess ($opposite + @('uninstall', 'oniguruma', '--silent')) $clientEnvironment -Timeout 60
        Assert-Gate ($refusal.ExitCode -eq 1 -and $refusal.Error.Contains('depends on')) 'Dependency-protected uninstall did not refuse'
        foreach ($name in @('jq', 'oniguruma')) { $null = Invoke-GateCommand ($opposite + @('uninstall', $name, '--silent')) $clientEnvironment }
        Assert-Gate (@(Get-ChildItem -LiteralPath (Join-Path $app 'homebrew/receipts') -Filter '*.json').Count -eq 0 -and
            @(Get-ChildItem -LiteralPath (Join-Path $app 'homebrew/bin') -Force).Count -eq 0 -and
            @(Get-ChildItem -LiteralPath (Join-Path $app 'homebrew/opt') -Force).Count -eq 0) 'Uninstall left receipts or command/opt links'
        $script:result.checks.Add("$consumer/${sourcePublisher}: synthetic rebuild upgrade, cross-runtime repair/uninstall")
    }
    foreach ($client in $clients) {
        $consumer, $sourcePublisher, $app, $clientEnvironment = $client.Consumer, $client.Publisher, $client.App, $client.Environment
        $package = Join-Path $script:public "$sourcePublisher/brew.msix"
        $original = [System.IO.File]::ReadAllBytes($package)
        $cache = Join-Path $app 'sources/homebrew'
        $before = @{}
        foreach ($name in @('brew.msix', 'index.db')) { $before[$name] = [System.IO.File]::ReadAllBytes((Join-Path $cache $name)) }
        try {
            $archive = [System.IO.Compression.ZipFile]::Open($package, [System.IO.Compression.ZipArchiveMode]::Update)
            try {
                $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.db') })
                Assert-Gate ($entries.Count -gt 0) 'Tamper fixture has no database'
                foreach ($entry in $entries) {
                    $stream = $entry.Open()
                    try { [void]$stream.Seek(0, [System.IO.SeekOrigin]::End); $stream.WriteByte(0) }
                    finally { $stream.Dispose() }
                }
            }
            finally { $archive.Dispose() }
            $failure = Invoke-GateProcess ($script:clis[$consumer] + @('source', 'update', 'homebrew')) $clientEnvironment -Timeout 60
            Assert-Gate ($failure.ExitCode -ne 0) 'Tampered source update succeeded'
            foreach ($name in $before.Keys) {
                Assert-Gate ([Convert]::ToBase64String($before[$name]) -ceq [Convert]::ToBase64String([System.IO.File]::ReadAllBytes((Join-Path $cache $name)))) "Tamper rejection changed cached $name"
            }
            Assert-Gate ((Invoke-GateCommand ($script:clis[$consumer] + @('search', 'jq', '--source', 'homebrew')) $clientEnvironment).Contains('Pinget.Homebrew.jq')) 'Tamper rejection lost active search cache'
        }
        finally { [System.IO.File]::WriteAllBytes($package, $original) }
        $script:result.checks.Add("$consumer/${sourcePublisher}: tamper rejection preserves active cache")
    }
    foreach ($sourcePublisher in $script:clis.Keys) {
        if (-not $catalogInput) { continue }
        $cli = $script:clis[$sourcePublisher]
        $state = Join-Path $script:work "catalog-$sourcePublisher-state"
        $msix = Join-Path $script:work "catalog-$sourcePublisher/brew.msix"
        [void][System.IO.Directory]::CreateDirectory((Split-Path $msix))
        $null = Invoke-GateCommand ($cli + @('repository', 'build', '--input', $catalogInput, '--state', $state)) $script:environment
        $null = Invoke-GateCommand ($cli + @('repository', 'package', '--state', $state, '--name', 'Pinget.HomebrewSource',
            '--publisher', $script:publisher, '--version', '1.0.0.0', '--msix', $msix)) $script:environment
        $null = Invoke-GateCommand @($script:psign, 'sign', '--mode', 'portable', '--pfx', (Join-Path $script:work 'signer.pfx'), '--password', '', $msix) $script:environment
        $null = Invoke-GateCommand ($cli + @('repository', 'publish', '--state', $state, '--destination', (Join-Path $script:public "catalog-$sourcePublisher"),
            '--msix', $msix, '--psign-tool', $script:psign, '--trusted-ca', (Join-Path $script:work 'root.pem'))) $script:environment
        $script:result.checks.Add("$sourcePublisher signed live catalog source")
    }
    for ($index = 0; $index -lt $catalogRoots.Count; $index++) {
        $formula = $catalogRoots[$index]
        $client = $clients[$index % $clients.Count]
        $cli = $script:clis[$client.Consumer]
        $app = Join-Path $script:work "catalog-client-$index"
        $clientEnvironment = @{} + $script:environment
        $clientEnvironment.PINGET_APPROOT = $app
        $null = Invoke-GateCommand ($cli + @('source', 'add', '--name', 'catalog', '--arg', "$script:base/catalog-$($client.Publisher)",
            '--type', 'Pinget.PreIndexed.Homebrew', '--publisher', $script:publisher, '--trusted-ca', (Join-Path $script:work 'root.pem'))) $clientEnvironment
        $null = Invoke-GateCommand ($cli + @('source', 'update', 'catalog')) $clientEnvironment
        $null = Invoke-GateCommand ($cli + @('install', $formula, '--source', 'catalog', '--silent', '--accept-package-agreements')) $clientEnvironment -Timeout 600
        $receipt = $null
        foreach ($path in Get-ChildItem -LiteralPath (Join-Path $app 'homebrew/receipts') -Filter '*.json') {
            $candidate = Read-GateJson $path.FullName
            if ($candidate['Package']['Formula'] -eq $formula) { $receipt = $candidate; break }
        }
        Assert-Gate ($null -ne $receipt) "Catalog package $formula installed without a Homebrew receipt"
        Assert-Gate (Test-GateSymlink (Join-Path $app "homebrew/opt/$formula")) "Catalog package $formula did not create its private opt link"
        $script:result.checks.Add("$($client.Consumer)/$($client.Publisher): installed real catalog bottle $formula $($receipt.Package.KegVersion)")
    }
    $script:result.success = $true
}
finally {
    try { if ($server) { $server.Dispose() } }
    finally {
        $script:upstream.Dispose()
        Write-GateJson $reportPath $script:result
        Remove-Item -LiteralPath $script:work -Recurse -Force
    }
}
