#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Import-HomebrewCatalog.ps1')

function Assert-Equal($Actual, $Expected) {
    $actualJson = ConvertTo-Json -InputObject $Actual -Depth 100 -Compress
    $expectedJson = ConvertTo-Json -InputObject $Expected -Depth 100 -Compress
    if ($actualJson -cne $expectedJson) { throw "Expected $expectedJson; got $actualJson" }
}
function Assert-True([bool] $Condition, [string] $Message = 'Assertion failed') {
    if (-not $Condition) { throw $Message }
}
function Assert-Throws([scriptblock] $Body, [string] $Pattern) {
    $caught = $null
    try { $null = & $Body } catch { $caught = $_ }
    if (-not $caught -or $caught.Exception.Message -notmatch $Pattern) { throw "Expected error matching '$Pattern'; got '$caught'" }
}
function With-Mocks([hashtable] $Mocks, [scriptblock] $Body) {
    $saved = @{}
    try {
        foreach ($name in $Mocks.Keys) {
            $saved[$name] = (Get-Item "Function:$name").ScriptBlock
            Set-Item "Function:script:$name" $Mocks[$name]
        }
        & $Body
    } finally {
        foreach ($name in $saved.Keys) { Set-Item "Function:script:$name" $saved[$name] }
    }
}
function New-TestFormula([string] $Name, [string[]] $Dependencies = @(), [string] $Target = 'x86_64_linux') {
    return @{
        name = $Name; tap = 'homebrew/core'; dependencies = $Dependencies
        versions = @{ stable = '1.2.3' }; revision = 0
        bottle = @{ stable = @{ rebuild = 1; files = @{ $Target = @{
            url = "https://ghcr.io/v2/homebrew/core/$Name/blobs/sha256:$('a' * 64)"
            sha256 = 'a' * 64; cellar = 'any'
        } } } }
    }
}
function New-TestOptions([string] $Directory, [string[]] $Roots = @('sample'), [string] $Target = 'x86_64_linux') {
    return @{
        Formula = $Roots; FormulaList = $null; Tag = $Target; Runtime = 'rust'; RustCli = 'pinget'
        DotnetAssembly = $null; ManifestOutput = Join-Path $Directory 'manifests'
        BottleMirror = Join-Path $Directory 'bottles'; BaseUrl = 'https://mirror.invalid/bottles'
        Report = Join-Path $Directory 'report.json'; MinimumImported = $null
    }
}
function Save-FixtureBottle($Value, $Target, $Directory) {
    $path = Join-Path $Directory "$($Value['name']).tar.gz"
    [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes('fixture'))
    return $path
}
function Get-Argument([string[]] $Command, [string] $Name) {
    return $Command[[Array]::IndexOf($Command, $Name) + 1]
}

$tests = [ordered]@{
    resolves_dependencies_before_roots_and_fetches_each_once = {
        $catalogFormulas = @{
            jq = @{ name = 'jq'; dependencies = @('oniguruma') }
            oniguruma = @{ name = 'oniguruma'; dependencies = @() }
        }
        $fetched = [Collections.Generic.List[string]]::new()
        $result = Resolve-Closure @('jq', 'oniguruma') {
            param($Name)
            $fetched.Add($Name)
            @{ Formula = $catalogFormulas[$Name]; Digest = "hash-$Name" }
        }
        Assert-Equal $result.Ordered @('oniguruma', 'jq')
        Assert-Equal $fetched.ToArray() @('jq', 'oniguruma')
        Assert-Equal @($result.Formulas.Keys | Sort-Object) @('jq', 'oniguruma')
        Assert-Equal $result.Hashes['jq'] 'hash-jq'
        Assert-Equal $result.Failures.Count 0
    }
    marks_dependency_cycles_as_unavailable = {
        $catalogFormulas = @{
            first = @{ name = 'first'; dependencies = @('second') }
            second = @{ name = 'second'; dependencies = @('first') }
        }
        $result = Resolve-Closure @('first') { param($Name) @{ Formula = $catalogFormulas[$Name]; Digest = 'hash' } }
        Assert-Equal @($result.Formulas.Keys | Sort-Object) @('first', 'second')
        Assert-Equal $result.Ordered @()
        Assert-True ($result.Failures['first'] -match 'cycle')
    }
    rejects_non_core_dependency_names = {
        Assert-Throws { Get-DependencyNames @{ name = 'sample'; dependencies = @('owner/tap/formula') } } 'homebrew/core'
    }
    rejects_missing_target_bottle = {
        Assert-Throws { Get-Bottle @{ name = 'sample'; bottle = @{} } 'x86_64_linux' } 'no stable bottle'
    }
    linux_arm_uses_upstream_tag_and_normalizes_relocation = {
        $value = @{ name = 'sample'; bottle = @{ stable = @{ files = @{ arm64_linux = @{ cellar = ':any_skip_relocation' } } } } }
        $calls = [Collections.Generic.List[string]]::new()
        With-Mocks @{ 'Get-LinuxBottleAbi' = {
            param($Value, $Target, $Selected)
            $calls.Add($Target)
            @{ Minimum = '2.35'; Digest = 'index-hash' }
        } } {
            $result = Get-AdaptedBottle $value 'aarch64_linux'
            Assert-Equal $result.Selected['cellar'] 'any_skip_relocation'
            Assert-Equal $result.Selected['minimum_glibc'] '2.35'
            Assert-Equal $result.Selected['loader'] 'glibc'
            Assert-Equal $result.Selected.Count 3
            Assert-Equal $result.Provenance['oci_index_sha256'] 'index-hash'
            Assert-Equal $value.bottle.stable.files.arm64_linux.cellar ':any_skip_relocation'
            Assert-Equal $calls.ToArray() @('aarch64_linux')
        }
    }
    macos_normalizes_only_known_symbolic_cellar_values = {
        $value = @{ name = 'sample'; bottle = @{ stable = @{ files = @{ sonoma = @{ cellar = ':any' } } } } }
        $result = Get-AdaptedBottle $value 'sonoma'
        Assert-Equal $result.Selected['cellar'] 'any'
        Assert-Equal $result.Provenance.Count 0
        $value.bottle.stable.files.sonoma.cellar = ':unknown'
        Assert-Equal (Get-AdaptedBottle $value 'sonoma').Selected['cellar'] ':unknown'
    }
    sequoia_targets_are_forwarded_with_matching_architecture = {
        foreach ($target in @('sequoia', 'arm64_sequoia')) {
            $value = New-TestFormula 'sample' @() $target
            $value.versions.stable = '1.0'; $value.bottle.stable.rebuild = 0
            $value.bottle.stable.files[$target].cellar = ':any'
            $options = New-TestOptions $testRoot @('sample') $target
            $commands = [Collections.Generic.List[object]]::new()
            With-Mocks @{
                'Get-ApiFormula' = { param($Name) @{ Formula = $value; Digest = 'api-hash' } }
                'Save-Bottle' = ${function:Save-FixtureBottle}
                'Invoke-ImporterProcess' = { param($Command) $commands.Add($Command) }
            } {
                $result = Invoke-CatalogImport $options
                $command = $commands[0]
                Assert-Equal (Get-Argument $command '--tag') $target
                Assert-Equal (Get-Argument $command '--target-os') 'macOS'
                Assert-Equal (Get-Argument $command '--architecture') $(if ($target -eq 'sequoia') { 'x64' } else { 'arm64' })
                Assert-Equal $result.importedRoots @('sample')
            }
        }
    }
    linux_abi_requires_exact_oci_tag_and_bottle_digest = {
        $digest = 'a' * 64
        $value = @{ name = 'sample'; versions = @{ stable = '1.2' }; revision = 3; bottle = @{ stable = @{ rebuild = 2 } } }
        $index = @{ manifests = @(@{ annotations = @{
            'org.opencontainers.image.ref.name' = '1.2_3.arm64_linux.2'
            'sh.brew.bottle.digest' = $digest; 'sh.brew.bottle.glibc.version' = '2.35'
        } }) }
        $data = [Text.Encoding]::UTF8.GetBytes(($index | ConvertTo-Json -Depth 10 -Compress))
        $urls = [Collections.Generic.List[string]]::new()
        With-Mocks @{
            'Get-BottleToken' = { param($Name) 'token' }
            'Read-Https' = { param($Url, $Limit, $Headers, $AllowedHosts) $urls.Add($Url); return ,$data }
        } {
            $abi = Get-LinuxBottleAbi $value 'aarch64_linux' @{ sha256 = $digest }
            Assert-Equal $abi.Minimum '2.35'
            Assert-Equal $abi.Digest (Get-Sha256 $data)
            Assert-True $urls[0].EndsWith('/manifests/1.2_3-2')
            Assert-Throws { Get-LinuxBottleAbi $value 'x86_64_linux' @{ sha256 = $digest } } 'does not uniquely match'
            Assert-Throws { Get-LinuxBottleAbi $value 'aarch64_linux' @{ sha256 = 'b' * 64 } } 'does not uniquely match'
            $index.manifests[0].annotations.Remove('sh.brew.bottle.glibc.version')
            $data = [Text.Encoding]::UTF8.GetBytes(($index | ConvertTo-Json -Depth 10 -Compress))
            Assert-Throws { Get-LinuxBottleAbi $value 'aarch64_linux' @{ sha256 = $digest } } 'lacks a valid minimum glibc'
        }
    }
    strips_registry_authorization_before_allowed_cdn_redirect = {
        $headers = @{ Authorization = 'Bearer private-token' }
        $redirect = Get-CheckedRedirect 'https://ghcr.io/v2/blob' 'https://pkg-containers.githubusercontent.com/blob' `
            @('ghcr.io', 'pkg-containers.githubusercontent.com') $headers
        Assert-True (-not $redirect.Headers.ContainsKey('Authorization'))
        Assert-Throws {
            Get-CheckedRedirect 'https://ghcr.io/v2/blob' 'http://example.invalid/blob' @('ghcr.io') $headers
        } 'outside the allowed HTTPS host'
    }
    downloads_only_the_declared_sha256_blob = {
        $payload = [Text.Encoding]::UTF8.GetBytes('pinned bottle')
        $digest = Get-Sha256 $payload
        $value = New-TestFormula 'sample'
        $value.bottle.stable.files.x86_64_linux.sha256 = $digest
        $value.bottle.stable.files.x86_64_linux.url = "https://ghcr.io/v2/homebrew/core/sample/blobs/sha256:$digest"
        With-Mocks @{
            'Get-BottleToken' = { param($Name) 'ephemeral-test-token' }
            'Open-Https' = {
                param($Url, $AllowedHosts, $Headers)
                @{ Stream = [IO.MemoryStream]::new($payload); Url = [uri] "https://ghcr.io/v2/homebrew/core/sample/blobs/sha256:$digest" }
            }
        } {
            $path = Save-Bottle $value 'x86_64_linux' $testRoot
            Assert-Equal ([Convert]::ToHexString([IO.File]::ReadAllBytes($path))) ([Convert]::ToHexString($payload))
            [IO.File]::Delete($path)
            $wrong = ConvertFrom-Json ($value | ConvertTo-Json -Depth 20) -AsHashtable
            $wrong.bottle.stable.files.x86_64_linux.sha256 = 'b' * 64
            $wrong.bottle.stable.files.x86_64_linux.url = "https://ghcr.io/v2/homebrew/core/sample/blobs/sha256:$('b' * 64)"
            Assert-Throws { Save-Bottle $wrong 'x86_64_linux' $testRoot } 'SHA-256'
            Assert-True (-not [IO.File]::Exists((Join-Path $testRoot 'sample.tar.gz')))
        }
    }
    imports_supported_closure_with_verified_dependency_pins = {
        $catalogFormulas = @{ jq = New-TestFormula 'jq' @('oniguruma'); oniguruma = New-TestFormula 'oniguruma' }
        $catalogFormulas.oniguruma.revision = 2
        $options = New-TestOptions $testRoot @('jq')
        $imports = [Collections.Generic.List[object]]::new()
        $stage = Join-Path $testRoot 'stage'
        $null = [IO.Directory]::CreateDirectory($stage)
        $stageInput = $stage
        if (-not $IsWindows) {
            $stageInput = Join-Path $testRoot 'stage-link'
            $null = New-Item -ItemType SymbolicLink -Path $stageInput -Target $stage
        }
        With-Mocks @{
            'Get-ApiFormula' = { param($Name) @{ Formula = $catalogFormulas[$Name]; Digest = "api-$Name" } }
            'Save-Bottle' = ${function:Save-FixtureBottle}
            'Get-LinuxBottleAbi' = { param($Value, $Target, $Selected) @{ Minimum = '2.35'; Digest = 'index-hash' } }
            'New-CatalogStage' = { $stageInput }
            'Invoke-ImporterProcess' = {
                param($Command)
                $formulaPath = Get-Argument $Command '--formula-json'
                Assert-Equal ([IO.Path]::GetDirectoryName($formulaPath)) ([IO.Path]::GetFullPath($stage))
                $imports.Add((ConvertFrom-Json ([IO.File]::ReadAllText($formulaPath)) -AsHashtable))
            }
        } {
            $result = Invoke-CatalogImport $options
            Assert-Equal $result.imported @('oniguruma', 'jq')
            Assert-Equal $result.importedRoots @('jq')
            Assert-Equal $imports[0]['pinget_runtime_dependencies'] @()
            $pins = $imports[1]['pinget_runtime_dependencies']
            Assert-Equal $pins.Count 1
            Assert-Equal $pins[0]['full_name'] 'oniguruma'
            Assert-Equal $pins[0]['pkg_version'] '1.2.3_2'
            Assert-Equal $pins[0]['bottle_rebuild'] 1
            Assert-Equal $imports[1]['bottle']['stable']['files']['x86_64_linux']['minimum_glibc'] '2.35'
            Assert-Equal $result.importedMetadata[1]['formulaApiSha256'] 'api-jq'
            Assert-Equal $result.importedMetadata[1]['oci_index_sha256'] 'index-hash'
        }
        if (-not $IsWindows) { [IO.Directory]::Delete($stageInput, $false); [IO.Directory]::Delete($stage, $false) }
    }
    native_process_errors_are_checked_explicitly = {
        $pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
        Invoke-ImporterProcess @($pwsh, '-NoLogo', '-NoProfile', '-Command', 'exit 0')
        Assert-Throws {
            Invoke-ImporterProcess @($pwsh, '-NoLogo', '-NoProfile', '-Command', '[Console]::Error.WriteLine("native-fixture-error"); exit 23')
        } 'native-fixture-error'
        Assert-Equal (Get-RuntimeCommand @{ Runtime = 'rust'; RustCli = 'pinget' }) @([IO.Path]::GetFullPath('pinget'))
        Assert-Equal (Get-RuntimeCommand @{ Runtime = 'dotnet'; DotnetAssembly = 'pinget.dll' }) @('dotnet', [IO.Path]::GetFullPath('pinget.dll'))
        Assert-Throws { Get-RuntimeCommand @{ Runtime = 'rust' } } 'RustCli'
        Assert-Throws { Get-RuntimeCommand @{ Runtime = 'dotnet' } } 'DotnetAssembly'
    }
    formula_list_default_minimum_and_report_are_preserved = {
        Assert-Throws { Invoke-CatalogImport @{} } 'provide 1..256 unique'
        $options = New-TestOptions $testRoot @('sample') 'sonoma'
        $options.FormulaList = Join-Path $testRoot 'formulas.txt'
        [IO.File]::WriteAllText($options.FormulaList, "# comment`n sample `n`nmissing`n")
        $fixtureFormula = New-TestFormula 'sample' @() 'sonoma'
        With-Mocks @{
            'Get-ApiFormula' = {
                param($Name)
                if ($Name -eq 'missing') { throw 'formula not found' }
                @{ Formula = $fixtureFormula; Digest = 'api-hash' }
            }
            'Save-Bottle' = ${function:Save-FixtureBottle}
            'Invoke-ImporterProcess' = { param($Command) }
        } {
            Assert-Throws { Invoke-CatalogImport $options } 'imported 1 of 2 requested roots; minimum is 2'
            $written = ConvertFrom-Json ([IO.File]::ReadAllText($options.Report)) -AsHashtable
            Assert-Equal $written.schemaVersion 1
            Assert-Equal $written.requested @('sample', 'missing')
            Assert-Equal $written.importedRoots @('sample')
            Assert-Equal $written.skipped[0].formula 'missing'
            Assert-Equal $written.importedMetadata[0].packageVersion '1.2.3.bottle.1'
            $options.MinimumImported = 1
            Assert-Equal (Invoke-CatalogImport $options).importedRoots @('sample')
            $options.MinimumImported = 0
            Assert-Throws { Invoke-CatalogImport $options } 'MinimumImported must be between 1 and 2'
        }
        [IO.File]::Delete($options.FormulaList)
        Assert-Equal (Get-DependencyNames @{ name = 'sample' }) @()
        Assert-Throws { Get-DependencyNames @{ name = 'sample'; dependencies = 'scalar' } } 'must be a list'
    }
    bounded_reads_and_secure_redirect_targets = {
        $payload = [Text.Encoding]::UTF8.GetBytes('oversize')
        With-Mocks @{ 'Open-Https' = {
            param($Url, $AllowedHosts, $Headers)
            @{ Stream = [IO.MemoryStream]::new($payload); Url = [uri] $Url }
        } } {
            Assert-Throws { Read-Https 'https://ghcr.io/token' 3 -AllowedHosts @('ghcr.io') } '3-byte limit'
            Assert-Equal ([Convert]::ToHexString((Read-Https 'https://ghcr.io/token' 8 -AllowedHosts @('ghcr.io')))) ([Convert]::ToHexString($payload))
        }
        foreach ($target in @('http://ghcr.io/blob', 'https://evil.invalid/blob', 'https://user:password@ghcr.io/blob')) {
            Assert-Throws { Get-CheckedRedirect 'https://ghcr.io/blob' $target @('ghcr.io') @{} } 'outside the allowed HTTPS host'
        }
        Assert-Equal (Get-CheckedRedirect 'https://ghcr.io/blob' 'https://ghcr.io/other' @('ghcr.io') @{ Authorization = 'token' }).Headers.Authorization 'token'
    }
    closure_and_bottle_download_limits = {
        $originalCount = $script:MaxFormulas
        $originalBottle = $script:MaxBottleBytes
        $originalTotal = $script:MaxClosureBottleBytes
        try {
            $script:MaxFormulas = 1
            $closure = Resolve-Closure @('sample') { param($Name) @{ Formula = @{ name = $Name; dependencies = @('other') }; Digest = 'hash' } }
            Assert-True ($closure.Failures['other'] -match 'exceeds 1 formulas')
            Assert-Throws { Invoke-CatalogImport (New-TestOptions $testRoot @('one', 'two')) } 'provide 1..1 unique'
            $script:MaxFormulas = $originalCount
            $depthClosure = Resolve-Closure @('node0') {
                param($Name)
                $number = [int] $Name.Substring(4)
                @{ Formula = @{ name = $Name; dependencies = @("node$($number + 1)") }; Digest = 'hash' }
            }
            Assert-Equal $depthClosure.Failures['node65'] 'dependency graph exceeds the 64-level limit'
            $script:MaxBottleBytes = 3
            $payload = [Text.Encoding]::UTF8.GetBytes('pinned bottle')
            $digest = Get-Sha256 $payload
            $fixtureFormula = New-TestFormula 'sample' @() 'sonoma'
            $fixtureFormula.bottle.stable.files.sonoma.sha256 = $digest
            $fixtureFormula.bottle.stable.files.sonoma.url = "https://ghcr.io/v2/homebrew/core/sample/blobs/sha256:$digest"
            With-Mocks @{
                'Get-BottleToken' = { param($Name) 'token' }
                'Open-Https' = { param($Url, $AllowedHosts, $Headers) @{ Stream = [IO.MemoryStream]::new($payload); Url = [uri] $Url } }
            } {
                Assert-Throws { Save-Bottle $fixtureFormula 'sonoma' $testRoot } '2 GiB download limit'
                Assert-True (-not [IO.File]::Exists((Join-Path $testRoot 'sample.tar.gz')))
            }
            $script:MaxClosureBottleBytes = 3
            $options = New-TestOptions $testRoot @('sample') 'sonoma'
            With-Mocks @{
                'Get-ApiFormula' = { param($Name) @{ Formula = $fixtureFormula; Digest = 'api-hash' } }
                'Save-Bottle' = ${function:Save-FixtureBottle}
                'Invoke-ImporterProcess' = { param($Command) throw 'must not invoke importer' }
            } {
                Assert-Throws { Invoke-CatalogImport $options } 'imported 0 of 1'
                $written = ConvertFrom-Json ([IO.File]::ReadAllText($options.Report)) -AsHashtable
                Assert-Equal $written.skipped[0].reason 'dependency closure exceeds the 2 GiB total bottle-download limit'
            }
        } finally {
            $script:MaxFormulas = $originalCount
            $script:MaxBottleBytes = $originalBottle
            $script:MaxClosureBottleBytes = $originalTotal
        }
    }
}

$testRoot = Join-Path (Get-Location).ProviderPath ('.pinget-catalog-tests-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($testRoot)
$passed = 0
$failed = 0
try {
    foreach ($name in $tests.Keys) {
        try {
            & $tests[$name]
            $passed++
            Write-Host "PASS $name"
        } catch {
            $failed++
            [Console]::Error.WriteLine("FAIL ${name}: $_`n$($_.ScriptStackTrace)")
        }
    }
} finally {
    # Fixtures live in a unique directory. Remove only known fixture files and empty directories.
    foreach ($file in @('report.json', 'sample.tar.gz', 'formulas.txt')) {
        $path = Join-Path $testRoot $file
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    foreach ($directory in @('stage-link', 'stage')) {
        $path = Join-Path $testRoot $directory
        if ([IO.Directory]::Exists($path)) {
            if (-not (Get-Item -LiteralPath $path).LinkType) {
                foreach ($file in @('jq.json', 'jq.tar.gz', 'oniguruma.json', 'oniguruma.tar.gz')) {
                    $fixture = Join-Path $path $file
                    if ([IO.File]::Exists($fixture)) { [IO.File]::Delete($fixture) }
                }
            }
            [IO.Directory]::Delete($path, $false)
        }
    }
    [IO.Directory]::Delete($testRoot, $false)
}
Write-Host "Catalog import tests: $passed passed, $failed failed ($($tests.Count) total)"
if ($failed) { exit 1 }
