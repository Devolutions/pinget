#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $Repository = 'microsoft/winget-pkgs',

    [string] $SubtreePath = 'manifests/d/Devolutions',

    [string] $PullRequestAuthor = 'devolutionsbot',

    [switch] $SkipOpenPullRequests,

    [switch] $SkipConflictingPullRequests,

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
$parentDir = Split-Path -Parent $outputFullPath
if ([string]::IsNullOrWhiteSpace($parentDir)) {
    throw "OutputDirectory cannot be a drive root: '$outputFullPath'."
}
New-Item -ItemType Directory -Path $parentDir -Force | Out-Null

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
    if ((Test-Path -LiteralPath $cloneRoot) -and
        -not (Test-Path -LiteralPath (Join-Path $cloneRoot '.git') -PathType Container)) {
        if (-not (Test-Path -LiteralPath $cloneRoot -PathType Container) -or
            @(Get-ChildItem -LiteralPath $cloneRoot -Force).Count -gt 0) {
            throw "Refusing to overwrite existing non-git SparseCheckoutDirectory: '$cloneRoot'."
        }
    }
}

# Stage on the same volume as OutputDirectory so final directory replacement is an atomic rename swap.
$stagingRoot = Join-Path $parentDir (".pinget-manifest-stage-" + [Guid]::NewGuid().ToString('N'))
$patchTempDir = Join-Path $userTempRoot ("pinget-pr-patches-" + [Guid]::NewGuid().ToString('N'))

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
        throw "Manifest path escaped root: '$RelativePath'."
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
        & git -C $cloneRoot clean -fd --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git clean failed.' }
    }
    else {
        & git clone --filter=blob:none --no-checkout --depth 1 --sparse $repoUrl $cloneRoot
        if ($LASTEXITCODE -ne 0) { throw "git clone failed for $repoUrl." }
        & git -C $cloneRoot sparse-checkout set $normalizedSubtree
        if ($LASTEXITCODE -ne 0) { throw 'git sparse-checkout set failed.' }
        & git -C $cloneRoot checkout --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git checkout failed.' }
    }

    $clonePrefix = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($cloneRoot)) + [System.IO.Path]::DirectorySeparatorChar
    $sourceSubtree = Join-Path $cloneRoot ($normalizedSubtree.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $sourceSubtree -PathType Container)) {
        throw "Subtree '$normalizedSubtree' was not found in $Repository."
    }

    $appliedPrs = [System.Collections.Generic.List[object]]::new()
    $skippedPrs = [System.Collections.Generic.List[object]]::new()
    if (-not $SkipOpenPullRequests -and -not [string]::IsNullOrWhiteSpace($PullRequestAuthor)) {
        New-Item -ItemType Directory -Path $patchTempDir -Force | Out-Null
        $prJson = & gh pr list --repo $Repository --author $PullRequestAuthor --state open --limit 1000 --json number,title,updatedAt
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to list open pull requests in $Repository for author '$PullRequestAuthor'."
        }
        $parsedPrs = @($prJson | ConvertFrom-Json)
        $openPrs = @($parsedPrs | Sort-Object { [DateTimeOffset]::Parse([string]$_.updatedAt) }, number)
        $subtreeSlashPrefix = "$normalizedSubtree/"

        foreach ($pr in $openPrs) {
            $filesJson = & gh api "repos/$Repository/pulls/$($pr.number)/files?per_page=100" --paginate --slurp
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to read files for PR #$($pr.number) in $Repository."
            }
            $pages = @($filesJson | ConvertFrom-Json)
            $files = [System.Collections.Generic.List[object]]::new()
            foreach ($page in $pages) {
                foreach ($entry in @($page)) {
                    if ($null -ne $entry) {
                        $files.Add($entry)
                    }
                }
            }

            $matchingCount = 0
            $renameOutPaths = [System.Collections.Generic.List[string]]::new()
            $renameInEntries = [System.Collections.Generic.List[object]]::new()
            $hasInSubtreeDiff = $false

            foreach ($file in $files) {
                $filename = [string]$file.filename
                $prevFilename = if ($null -ne $file.PSObject.Properties['previous_filename'] -and
                    -not [string]::IsNullOrWhiteSpace([string]$file.previous_filename)) {
                    [string]$file.previous_filename
                }
                else {
                    $null
                }

                $newInSubtree = $filename.StartsWith($subtreeSlashPrefix, [System.StringComparison]::OrdinalIgnoreCase)
                $oldInSubtree = ($null -ne $prevFilename) -and $prevFilename.StartsWith($subtreeSlashPrefix, [System.StringComparison]::OrdinalIgnoreCase)

                if (-not $newInSubtree -and -not $oldInSubtree) {
                    continue
                }
                $matchingCount++

                if ($file.status -eq 'renamed' -and $oldInSubtree -and -not $newInSubtree) {
                    $renameOutPaths.Add($prevFilename)
                }
                elseif ($file.status -eq 'renamed' -and $newInSubtree -and -not $oldInSubtree) {
                    $renameInEntries.Add($file)
                }
                else {
                    $hasInSubtreeDiff = $true
                }
            }

            if ($matchingCount -eq 0) {
                continue
            }

            $savedTree = (& git -C $cloneRoot write-tree).Trim()
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($savedTree)) {
                throw "Failed to snapshot git index tree before applying PR #$($pr.number)."
            }

            $applyError = $null
            try {
                foreach ($oldRepoRel in $renameOutPaths) {
                    $null = Assert-SafeRelativeDestination -RootPrefix $clonePrefix -BasePath $cloneRoot -RelativePath $oldRepoRel
                    & git -C $cloneRoot rm --force --quiet --ignore-unmatch -- $oldRepoRel
                    if ($LASTEXITCODE -ne 0) {
                        throw "git rm failed for renamed-out path '$oldRepoRel'."
                    }
                }

                foreach ($entry in $renameInEntries) {
                    $newRepoRel = [string]$entry.filename
                    $destFile = Assert-SafeRelativeDestination -RootPrefix $clonePrefix -BasePath $cloneRoot -RelativePath $newRepoRel
                    New-Item -ItemType Directory -Path (Split-Path -Parent $destFile) -Force | Out-Null
                    Invoke-WebRequest -Uri ([string]$entry.raw_url) -OutFile $destFile
                    & git -C $cloneRoot add -- $newRepoRel
                    if ($LASTEXITCODE -ne 0) {
                        throw "git add failed for renamed-in path '$newRepoRel'."
                    }
                }

                if ($hasInSubtreeDiff) {
                    $patchPath = Join-Path $patchTempDir "pr-$($pr.number).patch"
                    $previousOutputEncoding = [Console]::OutputEncoding
                    try {
                        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
                        $patchLines = & gh api "repos/$Repository/pulls/$($pr.number)" -H 'Accept: application/vnd.github.v3.diff'
                        if ($LASTEXITCODE -ne 0) {
                            throw "Failed to fetch diff for PR #$($pr.number)."
                        }
                    }
                    finally {
                        [Console]::OutputEncoding = $previousOutputEncoding
                    }
                    $patchText = ($patchLines -join "`n") + "`n"
                    [System.IO.File]::WriteAllText($patchPath, $patchText, [System.Text.UTF8Encoding]::new($false))

                    $applyOutput = & git -C $cloneRoot apply --index --3way --allow-empty --whitespace=nowarn --include="$normalizedSubtree/*" $patchPath 2>&1
                    if ($LASTEXITCODE -ne 0) {
                        throw ("git apply --3way failed: " + (($applyOutput | ForEach-Object { $_.ToString() }) -join ' '))
                    }
                    $unmerged = @(& git -C $cloneRoot diff --name-only --diff-filter=U)
                    if ($unmerged.Count -gt 0) {
                        throw ("3-way merge conflict on: " + ($unmerged -join ', '))
                    }
                }
            }
            catch {
                $applyError = $_.Exception.Message
            }

            if ($null -ne $applyError) {
                & git -C $cloneRoot read-tree --reset -u $savedTree
                & git -C $cloneRoot clean -fd --quiet
                if ($SkipConflictingPullRequests) {
                    Write-Warning "Skipping conflicting PR #$($pr.number) ($($pr.title)): $applyError"
                    $skippedPrs.Add([pscustomobject]@{
                        Number = $pr.number
                        Title = $pr.title
                        Reason = $applyError
                    })
                    continue
                }
                throw "Failed to apply open PR #$($pr.number) ($($pr.title)): $applyError"
            }

            $appliedPrs.Add([pscustomobject]@{
                Number = $pr.number
                Title = $pr.title
                UpdatedAt = $pr.updatedAt
                Files = $matchingCount
            })
        }
    }

    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    if (Test-Path -LiteralPath $sourceSubtree -PathType Container) {
        Get-ChildItem -LiteralPath $sourceSubtree -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $stagingRoot -Recurse -Force
        }
    }

    $backupPath = $null
    if (Test-Path -LiteralPath $outputFullPath) {
        $backupPath = Join-Path $parentDir (".pinget-manifest-backup-" + [Guid]::NewGuid().ToString('N'))
        Move-Item -LiteralPath $outputFullPath -Destination $backupPath
    }
    try {
        Move-Item -LiteralPath $stagingRoot -Destination $outputFullPath
    }
    catch {
        if ($null -ne $backupPath -and (Test-Path -LiteralPath $backupPath) -and -not (Test-Path -LiteralPath $outputFullPath)) {
            Move-Item -LiteralPath $backupPath -Destination $outputFullPath -ErrorAction SilentlyContinue
        }
        throw
    }
    if ($null -ne $backupPath -and (Test-Path -LiteralPath $backupPath)) {
        Remove-Item -LiteralPath $backupPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    $yamlCount = @(Get-ChildItem -LiteralPath $outputFullPath -Recurse -File |
        Where-Object { $_.Extension -in @('.yaml', '.yml') }).Count

    [pscustomobject]@{
        OutputDirectory = $outputFullPath
        Repository = $Repository
        SubtreePath = $normalizedSubtree
        YamlFileCount = $yamlCount
        AppliedPullRequests = $appliedPrs.ToArray()
        SkippedPullRequests = $skippedPrs.ToArray()
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $patchTempDir) {
        Remove-Item -LiteralPath $patchTempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($cleanupClone -and (Test-Path -LiteralPath $cloneRoot)) {
        Remove-Item -LiteralPath $cloneRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
