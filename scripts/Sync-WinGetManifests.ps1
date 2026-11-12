[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $Repository = 'microsoft/winget-pkgs',

    [string] $SubtreePath = 'manifests/d/Devolutions',

    [string] $PullRequestAuthor = 'devolutionsbot',

    [switch] $SkipOpenPullRequests,

    [string] $SparseCheckoutDirectory
)

$ErrorActionPreference = 'Stop'

$normalizedSubtree = $SubtreePath.Replace('\', '/').Trim('/')
if ([string]::IsNullOrWhiteSpace($normalizedSubtree) -or
    $normalizedSubtree.Split('/') -contains '..' -or
    $normalizedSubtree.Split('/') -contains '.') {
    throw "Invalid SubtreePath: '$SubtreePath'."
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$outputPrefix = [System.IO.Path]::TrimEndingDirectorySeparator($outputFullPath) + [System.IO.Path]::DirectorySeparatorChar

$userTempRoot = if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Temp'
    New-Item -ItemType Directory -Path $candidate -Force | Out-Null
    $candidate
}
else {
    [System.IO.Path]::GetTempPath()
}

$cleanupClone = $false
if ([string]::IsNullOrWhiteSpace($SparseCheckoutDirectory)) {
    $cloneRoot = Join-Path $userTempRoot ("pinget-winget-sync-" + [Guid]::NewGuid().ToString('N'))
    $cleanupClone = $true
}
else {
    $cloneRoot = [System.IO.Path]::GetFullPath($SparseCheckoutDirectory)
}

$stagingRoot = Join-Path $userTempRoot ("pinget-manifest-stage-" + [Guid]::NewGuid().ToString('N'))

function Assert-SafeRelativeDestination {
    param([string] $RootPrefix, [string] $BasePath, [string] $RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw 'Empty manifest relative path.'
    }
    $segments = $RelativePath.Replace('\', '/').Split('/')
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
            throw "Unsafe manifest relative path: '$RelativePath'."
        }
    }
    $combined = [System.IO.Path]::GetFullPath((Join-Path $BasePath ($RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
    if (-not $combined.StartsWith($RootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest path escaped staging root: '$RelativePath'."
    }
    return $combined
}

try {
    $repoUrl = "https://github.com/$Repository.git"
    if (Test-Path -LiteralPath (Join-Path $cloneRoot '.git') -PathType Container) {
        & git -C $cloneRoot sparse-checkout set $normalizedSubtree
        if ($LASTEXITCODE -ne 0) { throw 'git sparse-checkout set failed.' }
        & git -C $cloneRoot fetch --depth 1 --filter=blob:none origin HEAD
        if ($LASTEXITCODE -ne 0) { throw 'git fetch failed.' }
        & git -C $cloneRoot checkout --force --quiet FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { throw 'git checkout FETCH_HEAD failed.' }
    }
    else {
        if (Test-Path -LiteralPath $cloneRoot) {
            Remove-Item -LiteralPath $cloneRoot -Recurse -Force
        }
        & git clone --filter=blob:none --no-checkout --depth 1 --sparse $repoUrl $cloneRoot
        if ($LASTEXITCODE -ne 0) { throw "git clone failed for $repoUrl." }
        & git -C $cloneRoot sparse-checkout set $normalizedSubtree
        if ($LASTEXITCODE -ne 0) { throw 'git sparse-checkout set failed.' }
        & git -C $cloneRoot checkout --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git checkout failed.' }
    }

    $sourceSubtree = Join-Path $cloneRoot ($normalizedSubtree.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $sourceSubtree -PathType Container)) {
        throw "Subtree '$normalizedSubtree' was not found in $Repository."
    }

    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $stagingPrefix = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($stagingRoot)) + [System.IO.Path]::DirectorySeparatorChar
    Copy-Item -Path (Join-Path $sourceSubtree '*') -Destination $stagingRoot -Recurse -Force

    $appliedPrs = [System.Collections.Generic.List[object]]::new()
    if (-not $SkipOpenPullRequests -and -not [string]::IsNullOrWhiteSpace($PullRequestAuthor)) {
        $prJson = & gh pr list --repo $Repository --author $PullRequestAuthor --state open --limit 100 --json number,title,createdAt
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to list open pull requests in $Repository for author '$PullRequestAuthor'."
        }
        $openPrs = @($prJson | ConvertFrom-Json | Sort-Object number)
        $subtreeSlashPrefix = "$normalizedSubtree/"
        foreach ($pr in $openPrs) {
            $filesJson = & gh api "repos/$Repository/pulls/$($pr.number)/files" --paginate
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to read files for PR #$($pr.number) in $Repository."
            }
            $files = @($filesJson | ConvertFrom-Json)
            $matchingCount = 0
            foreach ($file in $files) {
                $filename = [string]$file.filename
                if (-not $filename.StartsWith($subtreeSlashPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
                $relative = $filename.Substring($subtreeSlashPrefix.Length)
                $targetPath = Assert-SafeRelativeDestination -RootPrefix $stagingPrefix -BasePath $stagingRoot -RelativePath $relative
                if ($file.status -eq 'removed') {
                    if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                        Remove-Item -LiteralPath $targetPath -Force
                        $matchingCount++
                    }
                    continue
                }
                if (-not ($relative.EndsWith('.yaml', [System.StringComparison]::OrdinalIgnoreCase) -or
                          $relative.EndsWith('.yml', [System.StringComparison]::OrdinalIgnoreCase))) {
                    continue
                }
                New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
                Invoke-WebRequest -Uri ([string]$file.raw_url) -OutFile $targetPath -UseBasicParsing
                $matchingCount++
            }
            if ($matchingCount -gt 0) {
                $appliedPrs.Add([pscustomobject]@{
                    Number = $pr.number
                    Title = $pr.title
                    Files = $matchingCount
                })
            }
        }
    }

    if (Test-Path -LiteralPath $outputFullPath) {
        Remove-Item -LiteralPath $outputFullPath -Recurse -Force
    }
    $parentDir = Split-Path -Parent $outputFullPath
    if (-not [string]::IsNullOrWhiteSpace($parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }
    Move-Item -LiteralPath $stagingRoot -Destination $outputFullPath

    $yamlCount = @(Get-ChildItem -LiteralPath $outputFullPath -Recurse -File |
        Where-Object { $_.Extension -in @('.yaml', '.yml') }).Count

    [pscustomobject]@{
        OutputDirectory = $outputFullPath
        Repository = $Repository
        SubtreePath = $normalizedSubtree
        YamlFileCount = $yamlCount
        AppliedPullRequests = $appliedPrs.ToArray()
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($cleanupClone -and (Test-Path -LiteralPath $cloneRoot)) {
        Remove-Item -LiteralPath $cloneRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
