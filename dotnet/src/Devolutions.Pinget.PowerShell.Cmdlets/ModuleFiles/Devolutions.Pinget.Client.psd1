@{
    RootModule = 'Devolutions.Pinget.Client.psm1'
    ModuleVersion = '0.2.0'
    CompatiblePSEditions = @('Desktop', 'Core')
    GUID = 'c6d1b5f2-5ccd-4771-9480-25caad7c58bd'
    Author = 'Devolutions'
    CompanyName = 'Devolutions'
    Copyright = '(c) Devolutions. All rights reserved.'
    Description = 'WinGet-compatible Pinget module for Windows PowerShell and PowerShell 7.'
    PowerShellVersion = '5.1'

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
                'PSEdition_Desktop'
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
