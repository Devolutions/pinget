[CmdletBinding()]
param(
    [string]$ModulePath,
    [string]$SourceArgument
)

BeforeAll {
    $script:previousLocalAppData = $env:LOCALAPPDATA
    $script:previousPingetAppRoot = $env:PINGET_APPROOT
    $script:testRoot = Join-Path $env:TEMP "PingetPwshTests-$([guid]::NewGuid())"
    $script:appRoot = Join-Path $script:testRoot 'app-root'
    $script:sourceName = 'PingetParitySource'
    $script:downloadDirectory = Join-Path $script:testRoot 'downloads'
    $script:supportsPackageOperationTests = $true
    $script:skipPackageOperationReason = 'The package source did not return the expected test package.'

    New-Item -ItemType Directory -Force -Path $script:testRoot, $script:appRoot, $script:downloadDirectory | Out-Null
    $env:LOCALAPPDATA = $script:testRoot
    $env:PINGET_APPROOT = $script:appRoot

    Import-Module $ModulePath -Force

    function Add-PingetParitySource {
        try
        {
            Get-PingetSource -Name $script:sourceName -ErrorAction Stop | Out-Null
        }
        catch
        {
            Add-PingetSource -Name $script:sourceName -Argument $SourceArgument -Type 'Microsoft.Rest' -TrustLevel Trusted -Explicit -Priority 42
        }
    }
}

AfterAll {
    try
    {
        Remove-PingetSource -Name $script:sourceName -ErrorAction SilentlyContinue
    }
    catch
    {
    }

    Remove-Module Devolutions.Pinget.Client -Force -ErrorAction SilentlyContinue
    $env:LOCALAPPDATA = $script:previousLocalAppData
    $env:PINGET_APPROOT = $script:previousPingetAppRoot

    if (Test-Path $script:testRoot)
    {
        Remove-Item -Path $script:testRoot -Recurse -Force
    }
}

Describe 'Pinget module exports' {
    It 'exports the renamed client cmdlets' {
        $commands = Get-Command -Module Devolutions.Pinget.Client | Select-Object -ExpandProperty Name

        @(
            'Get-PingetVersion'
            'Find-PingetPackage'
            'Get-PingetPackage'
            'Get-PingetSource'
            'Install-PingetPackage'
            'Uninstall-PingetPackage'
            'Update-PingetPackage'
            'Repair-PingetPackage'
            'Get-PingetUserSetting'
            'Set-PingetUserSetting'
            'Test-PingetUserSetting'
            'Get-PingetSetting'
            'Enable-PingetSetting'
            'Disable-PingetSetting'
            'Assert-PingetPackageManager'
            'Repair-PingetPackageManager'
            'Add-PingetSource'
            'Remove-PingetSource'
            'Reset-PingetSource'
            'Export-PingetPackage'
        ) | ForEach-Object {
            $commands | Should -Contain $_
        }
    }

    It 'does not export aliases' {
        $aliases = (Get-Module Devolutions.Pinget.Client).ExportedAliases.Keys

        $aliases | Should -BeNullOrEmpty
    }
}

Describe 'Pinget command metadata' {
    It 'keeps Add-PingetSource compatibility parameters' {
        $command = Get-Command Add-PingetSource -Module Devolutions.Pinget.Client
        $command.Parameters.Keys | Should -Contain 'TrustLevel'
        $command.Parameters.Keys | Should -Contain 'Explicit'
        $command.Parameters.Keys | Should -Contain 'Priority'
    }

    It 'keeps export installer selection parameters' {
        $command = Get-Command Export-PingetPackage -Module Devolutions.Pinget.Client
        $command.Parameters.Keys | Should -Contain 'AllowHashMismatch'
        $command.Parameters.Keys | Should -Contain 'Platform'
        $command.Parameters.Keys | Should -Contain 'TargetOSVersion'
    }

    It 'keeps install compatibility headers as a hashtable' {
        $command = Get-Command Install-PingetPackage -Module Devolutions.Pinget.Client
        $command.Parameters['Header'].ParameterType.FullName | Should -Be 'System.Collections.Hashtable'
    }

    It 'keeps Reset-PingetSource parameter sets' {
        $command = Get-Command Reset-PingetSource -Module Devolutions.Pinget.Client

        $command.DefaultParameterSet | Should -Be 'DefaultSet'
        ($command.ParameterSets | Select-Object -ExpandProperty Name) | Should -Contain 'DefaultSet'
        ($command.ParameterSets | Select-Object -ExpandProperty Name) | Should -Contain 'OptionalSet'
    }

    It 'declares upstream-like output types for key commands' {
        (Get-Command Get-PingetSource -Module Devolutions.Pinget.Client).OutputType.Type.FullName | Should -Contain 'Devolutions.Pinget.PowerShell.Engine.PSObjects.PSSourceResult'
        (Get-Command Find-PingetPackage -Module Devolutions.Pinget.Client).OutputType.Type.FullName | Should -Contain 'Devolutions.Pinget.PowerShell.Engine.PSObjects.PSFoundCatalogPackage'
        (Get-Command Export-PingetPackage -Module Devolutions.Pinget.Client).OutputType.Type.FullName | Should -Contain 'Devolutions.Pinget.PowerShell.Engine.PSObjects.PSDownloadResult'
    }
}

Describe 'Pinget source compatibility' {
    BeforeAll {
        Add-PingetParitySource
    }

    It 'round-trips source metadata' {
        $source = Get-PingetSource -Name $script:sourceName

        $source.Name | Should -Be $script:sourceName
        $source.Argument | Should -Be $SourceArgument
        $source.Type | Should -Be 'Microsoft.Rest'
        $source.TrustLevel | Should -Be 'Trusted'
        $source.Explicit | Should -BeTrue
        $source.Priority | Should -Be 42
    }

    It 'throws for a missing named source' {
        { Get-PingetSource -Name 'Pinget.Missing.Source' } | Should -Throw
    }
}

Describe 'Pinget package object parity' {
    BeforeAll {
        Add-PingetParitySource
        $script:foundPackage = Find-PingetPackage -Id 'WinMerge.WinMerge' -Source $script:sourceName | Select-Object -First 1
        if ($null -eq $script:foundPackage)
        {
            $script:supportsPackageOperationTests = $false
        }
    }

    It 'exposes upstream-like package members' {
        if (-not $script:supportsPackageOperationTests)
        {
            Set-ItResult -Skipped -Because $script:skipPackageOperationReason
            return
        }

        $script:foundPackage | Should -Not -BeNullOrEmpty
        ($script:foundPackage | Get-Member -Name 'AvailableVersions').MemberType | Should -Be 'Property'
        ($script:foundPackage | Get-Member -Name 'CheckInstalledStatus').MemberType | Should -Be 'Method'
        ($script:foundPackage | Get-Member -Name 'GetPackageVersionInfo').MemberType | Should -Be 'Method'
    }

    It 'returns package version info objects' {
        if (-not $script:supportsPackageOperationTests)
        {
            Set-ItResult -Skipped -Because $script:skipPackageOperationReason
            return
        }

        $script:foundPackage.AvailableVersions.Count | Should -BeGreaterThan 0

        $version = $script:foundPackage.AvailableVersions[0]
        $packageVersionInfo = $script:foundPackage.GetPackageVersionInfo($version)

        $packageVersionInfo.Id | Should -Be $script:foundPackage.Id
        $packageVersionInfo.DisplayName | Should -Be $script:foundPackage.Name
        $packageVersionInfo.CompareToVersion($version).ToString() | Should -Be 'Equal'
    }
}

Describe 'Pinget format data and result objects' {
    BeforeAll {
        Add-PingetParitySource
    }

    It 'registers format data for package version info' {
        $typeData = Get-FormatData -TypeName 'Devolutions.Pinget.PowerShell.Engine.PSObjects.PSPackageVersionInfo'
        $typeData | Should -Not -BeNullOrEmpty
    }

    It 'returns download results with common operation members' {
        if (-not $script:supportsPackageOperationTests)
        {
            Set-ItResult -Skipped -Because $script:skipPackageOperationReason
            return
        }

        $warnings = @()
        $result = Export-PingetPackage `
            -Id 'WinMerge.WinMerge' `
            -Source $script:sourceName `
            -DownloadDirectory $script:downloadDirectory `
            -AllowHashMismatch `
            -SkipMicrosoftStoreLicense `
            -Platform Desktop `
            -TargetOSVersion ([System.Environment]::OSVersion.Version.ToString()) `
            -WarningVariable warnings

        $result.Status | Should -Be 'Ok'
        $result.CorrelationData | Should -Not -BeNullOrEmpty
        $result.ExtendedErrorCode | Should -Not -BeNullOrEmpty
        $result.Succeeded() | Should -BeTrue
        (Test-Path $result.DownloadedInstallerPath) | Should -BeTrue
        $warnings | Should -BeNullOrEmpty
    }
}
