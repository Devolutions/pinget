param(
    [string] $WorkRoot = 'C:\WinApp\work\PublisherContract'
)

$ErrorActionPreference = 'Stop'
$bundlePath = Join-Path $WorkRoot 'bootstrap\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle'
$certificatePath = Join-Path $WorkRoot 'publisher-contract.cer'

$signerCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePath
)
$trustedPeople = [System.Security.Cryptography.X509Certificates.X509Store]::new(
    [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
)
try {
    $trustedPeople.Open(
        [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
    )
    $alreadyTrusted = $trustedPeople.Certificates |
        Where-Object { $_.Thumbprint -eq $signerCertificate.Thumbprint }
    if ($null -eq $alreadyTrusted) {
        $trustedPeople.Add($signerCertificate)
    }
}
finally {
    $trustedPeople.Close()
    $signerCertificate.Dispose()
}
Write-Output 'Trusted ephemeral fixture signer in the Sandbox user Trusted People store.'

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$serverRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=127.0.0.1',
    $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
)
$serverRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
        $false, $false, 0, $true
    )
)
$serverRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
        $true
    )
)
$serverEku = [System.Security.Cryptography.OidCollection]::new()
[void]$serverEku.Add(
    [System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1')
)
$serverRequest.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
        $serverEku, $false
    )
)
$san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
$san.AddIpAddress([System.Net.IPAddress]::Parse('127.0.0.1'))
$serverRequest.CertificateExtensions.Add($san.Build())
$tlsCertificate = $serverRequest.CreateSelfSigned(
    [DateTimeOffset]::UtcNow.AddMinutes(-5),
    [DateTimeOffset]::UtcNow.AddDays(2)
)
$tlsPath = Join-Path $WorkRoot 'tls-server.pfx'
[System.IO.File]::WriteAllBytes(
    $tlsPath,
    $tlsCertificate.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx
    )
)
[System.IO.File]::WriteAllBytes(
    (Join-Path $WorkRoot 'tls-server.cer'),
    $tlsCertificate.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert
    )
)
$tlsTrustedPeople = [System.Security.Cryptography.X509Certificates.X509Store]::new(
    [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
)
try {
    $tlsTrustedPeople.Open(
        [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
    )
    $tlsTrustedPeople.Add($tlsCertificate)
}
finally {
    $tlsTrustedPeople.Close()
    $tlsCertificate.Dispose()
    $rsa.Dispose()
}
Write-Output 'Created and trusted the loopback TLS certificate in Sandbox only.'

$dependencyDirectory = Join-Path $WorkRoot 'bootstrap\selected-x64'
if (-not (Test-Path -LiteralPath $dependencyDirectory)) {
    $dependencyDirectory = Join-Path $WorkRoot 'bootstrap'
}
$dependencies = Get-ChildItem -LiteralPath $dependencyDirectory `
    -Filter '*.appx' | ForEach-Object FullName
$appInstaller = Get-AppxPackage -Name Microsoft.DesktopAppInstaller
if ($null -eq $appInstaller -or $appInstaller.Version.ToString() -ne '1.29.380.0') {
    Add-AppxPackage -Path $bundlePath -DependencyPath $dependencies
    $appInstaller = Get-AppxPackage -Name Microsoft.DesktopAppInstaller
}
if ($null -eq $appInstaller -or $appInstaller.Version.ToString() -ne '1.29.380.0') {
    $actualVersion = if ($null -eq $appInstaller) {
        '<missing>'
    }
    else {
        $appInstaller.Version.ToString()
    }
    throw "Expected official WinGet 1.29.380.0, found $actualVersion"
}
Write-Output "Installed official WinGet $($appInstaller.Version)"
