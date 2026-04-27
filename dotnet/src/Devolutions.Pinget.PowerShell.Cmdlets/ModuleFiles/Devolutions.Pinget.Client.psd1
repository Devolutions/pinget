@{
    RootModule = 'Devolutions.Pinget.PowerShell.Cmdlets.dll'
    ModuleVersion = '0.1.0'
    CompatiblePSEditions = @('Core')
    GUID = 'c6d1b5f2-5ccd-4771-9480-25caad7c58bd'
    Author = 'Devolutions'
    CompanyName = 'Devolutions'
    Copyright = '(c) Devolutions. All rights reserved.'
    Description = 'PowerShell 7 winget-compatible pinget module.'
    PowerShellVersion = '7.4.0'

    FunctionsToExport = @()

    CmdletsToExport = @(
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
    )

    AliasesToExport = @()

    FormatsToProcess = @('Format.ps1xml')

    PrivateData = @{
        PSData = @{
            Tags = @(
                'PSEdition_Core'
                'WindowsPackageManager'
                'WinGet'
                'Pinget'
                'PortableWinget'
                'PackageManagement'
            )
            LicenseUri = 'https://github.com/Devolutions/pinget/blob/master/LICENSE'
            ProjectUri = 'https://github.com/Devolutions/pinget'
            ReleaseNotes = 'See the repository changelog and release notes for details.'
        }
    }
}
