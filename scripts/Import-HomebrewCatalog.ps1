#Requires -Version 7.4
<#
.SYNOPSIS
Resolve and mirror a bounded Homebrew/core formula closure for Pinget.
#>
[CmdletBinding()]
param(
    [string[]] $Formula = @(),
    [string] $FormulaList,
    [string] $Tag,
    [ValidateSet('rust', 'dotnet')] [string] $Runtime,
    [string] $RustCli,
    [string] $DotnetAssembly,
    [string] $ManifestOutput,
    [string] $BottleMirror,
    [string] $BaseUrl,
    [string] $Report,
    [Nullable[int]] $MinimumImported
)

$script:ApiRoot = 'https://formulae.brew.sh/api/formula/'
$script:MaxFormulas = 256
$script:MaxFormulaBytes = 8MB
$script:MaxBottleBytes = 2GB
$script:MaxClosureBottleBytes = 2GB
$script:TargetTags = @('arm64_sonoma', 'sonoma', 'arm64_sequoia', 'sequoia', 'x86_64_linux', 'aarch64_linux')

function Get-Sha256([byte[]] $Data) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Data)).ToLowerInvariant()
}

function Get-CheckedRedirect([uri] $Source, [uri] $Target, [string[]] $AllowedHosts, [hashtable] $Headers) {
    if ($Target.Scheme -cne 'https' -or $Target.Host -cnotin $AllowedHosts -or $Target.UserInfo) {
        throw 'upstream redirected outside the allowed HTTPS host'
    }
    $redirectedHeaders = $Headers.Clone()
    if ($Target.Host -cne $Source.Host) { $redirectedHeaders.Remove('Authorization') }
    return @{ Url = $Target; Headers = $redirectedHeaders }
}

function Open-Https([string] $Url, [string[]] $AllowedHosts, [hashtable] $Headers = @{}) {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(180)
    try {
        $current = [uri] $Url
        $currentHeaders = $Headers.Clone()
        for ($redirects = 0; $redirects -le 10; $redirects++) {
            $null = Get-CheckedRedirect $current $current $AllowedHosts $currentHeaders
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $current)
            foreach ($key in $currentHeaders.Keys) {
                $null = $request.Headers.TryAddWithoutValidation($key, [string] $currentHeaders[$key])
            }
            try {
                $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            } finally { $request.Dispose() }
            if ([int] $response.StatusCode -in @(301, 302, 303, 307, 308)) {
                try {
                    if (-not $response.Headers.Location) { throw 'upstream redirect lacks a location' }
                    $target = [uri]::new($current, $response.Headers.Location)
                    $redirect = Get-CheckedRedirect $current $target $AllowedHosts $currentHeaders
                    $current = $redirect.Url
                    $currentHeaders = $redirect.Headers
                } finally { $response.Dispose() }
                continue
            }
            try {
                $null = $response.EnsureSuccessStatusCode()
                $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                return @{ Stream = $stream; Response = $response; Client = $client; Url = $current }
            } catch { $response.Dispose(); throw }
        }
        throw 'upstream exceeded the redirect limit'
    } catch { $client.Dispose(); throw }
}

function Close-Https($Connection) {
    $Connection.Stream.Dispose()
    if ($Connection.Response) { $Connection.Response.Dispose() }
    if ($Connection.Client) { $Connection.Client.Dispose() }
}

function Read-Https([string] $Url, [long] $Limit, [hashtable] $Headers = @{}, [string[]] $AllowedHosts = @()) {
    if (-not $AllowedHosts.Count) { $AllowedHosts = @(([uri] $Url).Host) }
    $connection = Open-Https $Url $AllowedHosts $Headers
    $output = [IO.MemoryStream]::new()
    try {
        $null = Get-CheckedRedirect ([uri] $Url) $connection.Url $AllowedHosts $Headers
        $buffer = [byte[]]::new(64KB)
        while (($count = $connection.Stream.Read($buffer, 0, [int] [Math]::Min($buffer.Length, $Limit + 1 - $output.Length))) -gt 0) {
            $output.Write($buffer, 0, $count)
            if ($output.Length -gt $Limit) { throw "upstream response exceeds the $Limit-byte limit" }
        }
        return ,$output.ToArray()
    } finally { $output.Dispose(); Close-Https $connection }
}

function ConvertFrom-ApiJson([byte[]] $Data) {
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    return ConvertFrom-Json -InputObject $utf8.GetString($Data) -AsHashtable -ErrorAction Stop
}

function Get-ApiFormula([string] $Name) {
    if ($Name -cnotmatch '^[A-Za-z0-9@+._-]{1,128}$') { throw "unsupported formula name: $Name" }
    $url = $script:ApiRoot + [uri]::EscapeDataString($Name) + '.json'
    try { $data = Read-Https $url $script:MaxFormulaBytes -AllowedHosts @('formulae.brew.sh') }
    catch {
        if ($_.Exception.ToString() -match '\b404\b') { throw "formula not found in Homebrew/core: $Name" }
        throw "Homebrew formula API request failed: $Name ($($_.Exception.Message))"
    }
    try { $value = ConvertFrom-ApiJson $data }
    catch { throw "invalid formula API JSON for $Name" }
    if ($value -isnot [System.Collections.IDictionary] -or $value['name'] -cne $Name -or $value['tap'] -cne 'homebrew/core') {
        throw "formula API identity is not the requested homebrew/core formula: $Name"
    }
    return @{ Formula = $value; Digest = Get-Sha256 $data }
}

function Get-Bottle($Formula, [string] $Tag) {
    $upstreamTag = if ($Tag -ceq 'aarch64_linux') { 'arm64_linux' } else { $Tag }
    $bottle = $Formula['bottle']
    $stable = if ($bottle -is [System.Collections.IDictionary]) { $bottle['stable'] }
    $files = if ($stable -is [System.Collections.IDictionary]) { $stable['files'] }
    if ($files -isnot [System.Collections.IDictionary] -or -not $files.Contains($upstreamTag)) {
        throw "$($Formula['name']): no stable bottle for target $Tag"
    }
    return $files[$upstreamTag]
}

function Get-BottleToken([string] $Name) {
    $scope = 'service=ghcr.io&scope=' + [uri]::EscapeDataString("repository:homebrew/core/${Name}:pull")
    try {
        $data = Read-Https "https://ghcr.io/token?$scope" 1MB -AllowedHosts @('ghcr.io')
        $value = ConvertFrom-ApiJson $data
        $token = if ($value -is [System.Collections.IDictionary]) { $value['token'] }
    } catch { throw "could not acquire a public GHCR pull token for $Name" }
    if ($token -isnot [string] -or -not $token -or $token.Length -gt 16384) { throw "GHCR returned an invalid pull token for $Name" }
    return $token
}

function Get-LinuxBottleAbi($Formula, [string] $Tag, $Selected) {
    $name = $Formula['name']
    try {
        $version = $Formula['versions']['stable']
        $revision = $Formula['revision']
        $rebuild = $Formula['bottle']['stable']['rebuild']
    } catch { throw "${name}: invalid version, revision or rebuild for OCI metadata" }
    $digest = $Selected['sha256']
    if ($version -isnot [string] -or $version -cnotmatch '^[A-Za-z0-9.+_-]+$' -or
        ($revision -isnot [int] -and $revision -isnot [long]) -or $revision -lt 0 -or
        ($rebuild -isnot [int] -and $rebuild -isnot [long]) -or $rebuild -lt 0 -or
        $digest -isnot [string] -or $digest -cnotmatch '^[0-9a-fA-F]{64}$') {
        throw "${name}: invalid version, revision or rebuild for OCI metadata"
    }
    $pkgVersion = $version + $(if ($revision) { "_$revision" })
    $indexTag = $pkgVersion + $(if ($rebuild) { "-$rebuild" })
    $upstreamTag = if ($Tag -ceq 'aarch64_linux') { 'arm64_linux' } else { $Tag }
    $imageRef = "$pkgVersion.$upstreamTag" + $(if ($rebuild) { ".$rebuild" })
    $token = Get-BottleToken $name
    try {
        $data = Read-Https "https://ghcr.io/v2/homebrew/core/$name/manifests/$indexTag" $script:MaxFormulaBytes `
            -Headers @{ Authorization = "Bearer $token"; Accept = 'application/vnd.oci.image.index.v1+json' } -AllowedHosts @('ghcr.io')
        $index = ConvertFrom-ApiJson $data
        if ($index -isnot [System.Collections.IDictionary] -or $index['manifests'] -isnot [array]) { throw 'invalid index' }
        $matches = @(
            foreach ($item in $index['manifests']) {
                if ($item -isnot [System.Collections.IDictionary] -or $item['annotations'] -isnot [System.Collections.IDictionary]) { throw 'invalid annotations' }
                $annotations = $item['annotations']
                $bottleDigest = $annotations['sh.brew.bottle.digest']
                if ($annotations['org.opencontainers.image.ref.name'] -ceq $imageRef -and
                    $bottleDigest -is [string] -and $bottleDigest.ToLowerInvariant() -ceq $digest.ToLowerInvariant()) { $annotations }
            }
        )
    } catch { throw "${name}: invalid or unavailable OCI index metadata" }
    if ($matches.Count -ne 1) { throw "${name}: OCI index does not uniquely match the selected bottle" }
    $minimum = $matches[0]['sh.brew.bottle.glibc.version']
    if ($minimum -isnot [string] -or $minimum -cnotmatch '^[0-9]+(?:\.[0-9]+)+$') { throw "${name}: OCI metadata lacks a valid minimum glibc version" }
    return @{ Minimum = $minimum; Digest = Get-Sha256 $data }
}

function Get-AdaptedBottle($Formula, [string] $Tag) {
    $source = Get-Bottle $Formula $Tag
    if ($source -isnot [System.Collections.IDictionary]) { throw "$($Formula['name']): invalid selected bottle" }
    $selected = @{}; foreach ($key in $source.Keys) { $selected[$key] = $source[$key] }
    if ($selected['cellar'] -cin @(':any', ':any_skip_relocation')) { $selected['cellar'] = $selected['cellar'].Substring(1) }
    $provenance = @{}
    if ($Tag.EndsWith('_linux')) {
        $abi = Get-LinuxBottleAbi $Formula $Tag $selected
        $selected['minimum_glibc'] = $abi.Minimum
        $selected['loader'] = 'glibc'
        $provenance['oci_index_sha256'] = $abi.Digest
    }
    return @{ Selected = $selected; Provenance = $provenance }
}

function Save-Bottle($Formula, [string] $Tag, [string] $Directory) {
    $selected = Get-Bottle $Formula $Tag
    $name = $Formula['name']
    $url = $selected['url']; $digest = $selected['sha256']
    $parsed = $null
    $validUri = [uri]::TryCreate([string] $url, [UriKind]::Absolute, [ref] $parsed)
    $prefix = "/v2/homebrew/core/$name/blobs/sha256:"
    if (-not $validUri -or $parsed.Scheme -cne 'https' -or $parsed.Host -cne 'ghcr.io' -or $parsed.UserInfo -or
        $parsed.Query -or $parsed.Fragment -or -not $parsed.AbsolutePath.StartsWith($prefix, [StringComparison]::Ordinal) -or
        $digest -isnot [string] -or $digest -cnotmatch '^[0-9a-fA-F]{64}$' -or
        $parsed.AbsolutePath.Split(':')[-1].ToLowerInvariant() -cne $digest.ToLowerInvariant()) {
        throw "${name}: selected bottle must be the matching public GHCR sha256 blob"
    }
    $token = Get-BottleToken $name
    $path = Join-Path $Directory "$name.tar.gz"
    $connection = $null; $output = $null
    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $total = 0L
    try {
        $connection = Open-Https $url @('ghcr.io', 'pkg-containers.githubusercontent.com') @{ Authorization = "Bearer $token" }
        $null = Get-CheckedRedirect $parsed $connection.Url @('ghcr.io', 'pkg-containers.githubusercontent.com') @{}
        $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        $buffer = [byte[]]::new(1MB)
        while (($count = $connection.Stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $total += $count
            if ($total -gt $script:MaxBottleBytes) { throw "${name}: bottle exceeds the 2 GiB download limit" }
            $hasher.AppendData($buffer, 0, $count)
            $output.Write($buffer, 0, $count)
        }
        $actual = [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
        if ($total -eq 0 -or $actual -cne $digest.ToLowerInvariant()) { throw "${name}: bottle SHA-256 does not match the formula API" }
    } catch {
        if ($output) { $output.Dispose(); $output = $null }
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
        throw "${name}: bottle download failed: $($_.Exception.Message)"
    } finally {
        if ($output) { $output.Dispose() }
        if ($connection) { Close-Https $connection }
        $hasher.Dispose()
    }
    return $path
}

function Get-DependencyNames($Formula) {
    $dependencies = @()
    if ($Formula.Contains('dependencies')) { $dependencies = $Formula['dependencies'] }
    if ($dependencies -isnot [array] -or @($dependencies | Where-Object { $_ -isnot [string] }).Count) {
        throw "$($Formula['name']): dependencies must be a list of formula names"
    }
    foreach ($dependency in $dependencies) {
        if ($dependency -cnotmatch '^[A-Za-z0-9@+._-]{1,128}$') { throw "$($Formula['name']): only homebrew/core runtime dependencies are supported" }
    }
    return ,$dependencies
}

function Resolve-Closure([string[]] $Roots, [scriptblock] $FetchFormula = { param($Name) Get-ApiFormula $Name }) {
    $formulas = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    $hashes = [hashtable]::new([StringComparer]::Ordinal)
    $visiting = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $ordered = [Collections.Generic.List[string]]::new()
    $failures = [hashtable]::new([StringComparer]::Ordinal)
    function Visit-Formula([string] $Name, [int] $Depth = 0) {
        if ($visiting.Contains($Name)) { $failures[$Name] = "dependency cycle contains $Name"; return }
        if ($formulas.Contains($Name) -or $failures.ContainsKey($Name)) { return }
        if ($Depth -gt 64) { $failures[$Name] = 'dependency graph exceeds the 64-level limit'; return }
        if ($formulas.Count + $visiting.Count -ge $script:MaxFormulas) { $failures[$Name] = "dependency closure exceeds $script:MaxFormulas formulas"; return }
        $null = $visiting.Add($Name)
        try {
            $result = & $FetchFormula $Name
            $dependencies = Get-DependencyNames $result.Formula
        } catch {
            $null = $visiting.Remove($Name); $failures[$Name] = $_.Exception.Message; return
        }
        $formulas[$Name] = $result.Formula; $hashes[$Name] = $result.Digest
        foreach ($dependency in $dependencies) { Visit-Formula $dependency ($Depth + 1) }
        $null = $visiting.Remove($Name)
        $unavailable = @($dependencies | Where-Object { -not $formulas.Contains($_) -or $failures.ContainsKey($_) })
        if ($unavailable.Count) {
            if (-not $failures.ContainsKey($Name)) { $failures[$Name] = "unavailable dependency: $($unavailable -join ', ')" }
        } else { $ordered.Add($Name) }
    }
    foreach ($root in $Roots) { Visit-Formula $root }
    return @{ Formulas = $formulas; Hashes = $hashes; Ordered = $ordered.ToArray(); Failures = $failures }
}

function Get-RuntimeCommand($Options) {
    if ($Options.Runtime -ceq 'rust') {
        if (-not $Options.RustCli) { throw '-RustCli is required for -Runtime rust' }
        return ,@([IO.Path]::GetFullPath($Options.RustCli))
    }
    if (-not $Options.DotnetAssembly) { throw '-DotnetAssembly is required for -Runtime dotnet' }
    return ,@('dotnet', [IO.Path]::GetFullPath($Options.DotnetAssembly))
}

function Invoke-ImporterProcess([string[]] $Command) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Command[0]
    foreach ($argument in $Command[1..($Command.Length - 1)]) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(300000)) {
            $process.Kill($true); $process.WaitForExit()
            throw 'offline importer timed out after 300 seconds'
        }
        $out = $stdout.GetAwaiter().GetResult(); $err = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            $detail = if ($err) { $err } elseif ($out) { $out } else { 'offline importer failed' }
            $detail = $detail.Trim()
            throw $detail.Substring([Math]::Max(0, $detail.Length - 2048))
        }
    } finally { $process.Dispose() }
}

function New-CatalogStage {
    $path = Join-Path (Get-Location).ProviderPath ('.pinget-homebrew-catalog-' + [guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($path)
    return $path
}

function Get-CanonicalDirectory([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.LinkType) { return $item.ResolveLinkTarget($true).FullName }
    return $item.FullName
}

function Invoke-CatalogImport($Options) {
    $roots = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inputs = @()
    if ($null -ne $Options.Formula) { $inputs = @($Options.Formula) }
    if ($Options.FormulaList) {
        $inputs += @([IO.File]::ReadAllLines([IO.Path]::GetFullPath($Options.FormulaList), [Text.Encoding]::UTF8) |
            Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('#') } | ForEach-Object { $_.Trim() })
    }
    foreach ($name in $inputs) { if ($seen.Add($name)) { $roots.Add($name) } }
    if (-not $roots.Count -or $roots.Count -gt $script:MaxFormulas) { throw "provide 1..$script:MaxFormulas unique -Formula values or a formula list" }
    if ($Options.Tag -cnotin $script:TargetTags) { throw "unsupported native target tag: $($Options.Tag)" }
    if ($Options.Tag.EndsWith('sonoma') -or $Options.Tag.EndsWith('sequoia')) {
        $targetOs = 'macOS'; $architecture = if ($Options.Tag.StartsWith('arm64')) { 'arm64' } else { 'x64' }
    } else {
        $targetOs = 'Linux'; $architecture = if ($Options.Tag.StartsWith('aarch64')) { 'arm64' } else { 'x64' }
    }
    $closure = Resolve-Closure $roots.ToArray()
    $result = [ordered]@{
        schemaVersion = 1; target = $Options.Tag; requested = $roots.ToArray()
        imported = @(); importedRoots = @(); importedMetadata = @()
        skipped = @($closure.Failures.Keys | Sort-Object -CaseSensitive | ForEach-Object { @{ formula = $_; reason = $closure.Failures[$_] } })
    }
    $cli = Get-RuntimeCommand $Options
    $downloadedBytes = 0L
    $stageInput = New-CatalogStage
    $stage = Get-CanonicalDirectory $stageInput
    $stageFiles = [Collections.Generic.List[string]]::new()
    try {
        foreach ($name in $closure.Ordered) {
            $value = $closure.Formulas[$name]
            $dependencies = Get-DependencyNames $value
            $unresolved = @($dependencies | Where-Object { $_ -cnotin $result.imported })
            if ($unresolved.Count) {
                $result.skipped += @{ formula = $name; reason = "unavailable dependency: $($unresolved -join ', ')" }
                continue
            }
            try {
                $adaptation = Get-AdaptedBottle $value $Options.Tag
                $selected = $adaptation.Selected; $provenance = $adaptation.Provenance
                $bottlePath = Join-Path $stage "$name.tar.gz"; $stageFiles.Add($bottlePath)
                $bottlePath = Save-Bottle $value $Options.Tag $stage
                $downloadedBytes += (Get-Item -LiteralPath $bottlePath).Length
                if ($downloadedBytes -gt $script:MaxClosureBottleBytes) {
                    [IO.File]::Delete($bottlePath)
                    $result.skipped += @{ formula = $name; reason = 'dependency closure exceeds the 2 GiB total bottle-download limit' }
                    break
                }
                $metadata = $value['bottle']['stable']; $version = $value['versions']['stable']; $rebuild = $metadata['rebuild']
                if ($version -isnot [string] -or ($rebuild -isnot [int] -and $rebuild -isnot [long] -and $rebuild -isnot [bool]) -or $rebuild -lt 0) {
                    throw "${name}: invalid stable version/rebuild metadata"
                }
                $adapted = ConvertFrom-Json ($value | ConvertTo-Json -Depth 100 -Compress) -AsHashtable
                $adapted['bottle']['stable']['files'] = @{ $Options.Tag = $selected }
                $adapted['pinget_runtime_dependencies'] = @(
                    foreach ($dependency in $dependencies) {
                        $dep = $closure.Formulas[$dependency]
                        @{ full_name = $dependency
                           pkg_version = $dep['versions']['stable'] + $(if ($dep['revision']) { "_$($dep['revision'])" })
                           bottle_rebuild = $dep['bottle']['stable']['rebuild'] }
                    }
                )
                $adapted['pinget_import_provenance'] = @{ formula_api_sha256 = $closure.Hashes[$name]; bottle_sha256 = $selected['sha256'].ToLowerInvariant() }
                foreach ($key in $provenance.Keys) { $adapted['pinget_import_provenance'][$key] = $provenance[$key] }
                $formulaPath = Join-Path $stage "$name.json"; $stageFiles.Add($formulaPath)
                [IO.File]::WriteAllText($formulaPath, ($adapted | ConvertTo-Json -Depth 100 -Compress) + "`n", [Text.UTF8Encoding]::new($false))
                $command = $cli + @(
                    'repository', 'import-homebrew', '--formula-json', $formulaPath, '--bottle-archive', $bottlePath,
                    '--manifest-output', [IO.Path]::GetFullPath($Options.ManifestOutput),
                    '--bottle-mirror', [IO.Path]::GetFullPath($Options.BottleMirror),
                    '--tag', $Options.Tag, '--target-os', $targetOs, '--architecture', $architecture, '--base-url', $Options.BaseUrl
                )
                Invoke-ImporterProcess $command
                $result.imported += $name
                if ($seen.Contains($name)) { $result.importedRoots += $name }
                $record = @{
                    formula = $name; formulaApiSha256 = $closure.Hashes[$name]; bottleSha256 = $selected['sha256'].ToLowerInvariant()
                    relocation = $selected['cellar']
                    packageVersion = "$version" + $(if ($value['revision']) { "_$($value['revision'])" }) + ".bottle.$rebuild"
                }
                foreach ($key in $provenance.Keys) { $record[$key] = $provenance[$key] }
                $result.importedMetadata += $record
                Write-Host "Imported $name $version bottle rebuild $rebuild"
            } catch {
                $result.skipped += @{ formula = $name; reason = $_.Exception.Message }
                [Console]::Error.WriteLine("Skipped ${name}: $($_.Exception.Message)")
            }
        }
    } finally {
        foreach ($file in $stageFiles) { if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) } }
        # Delete only our now-empty staging directory, never recursively remove a shared directory.
        if ([IO.Directory]::Exists($stageInput) -and -not (Get-Item -LiteralPath $stageInput -Force).LinkType) { [IO.Directory]::Delete($stageInput, $false) }
    }
    $reportPath = [IO.Path]::GetFullPath($Options.Report)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath))
    [IO.File]::WriteAllText($reportPath, ($result | ConvertTo-Json -Depth 100) + "`n", [Text.UTF8Encoding]::new($false))
    $minimum = if ($null -ne $Options.MinimumImported) { $Options.MinimumImported } else { $roots.Count }
    if ($minimum -lt 1 -or $minimum -gt $roots.Count) { throw "-MinimumImported must be between 1 and $($roots.Count)" }
    if ($result.importedRoots.Count -lt $minimum) {
        $missing = @($roots | Where-Object { $_ -cnotin $result.importedRoots } | Sort-Object -CaseSensitive)
        throw "imported $($result.importedRoots.Count) of $($roots.Count) requested roots; minimum is $minimum. Missing: $($missing -join ', '); see $reportPath"
    }
    return $result
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        foreach ($required in @('Tag', 'Runtime', 'ManifestOutput', 'BottleMirror', 'BaseUrl', 'Report')) {
            if (-not $PSBoundParameters[$required]) { throw "-$required is required" }
        }
        $options = @{}; foreach ($key in $PSBoundParameters.Keys) { $options[$key] = $PSBoundParameters[$key] }
        $null = Invoke-CatalogImport $options
        exit 0
    } catch {
        [Console]::Error.WriteLine("Homebrew catalog import failed: $($_.Exception.Message)")
        exit 1
    }
}
