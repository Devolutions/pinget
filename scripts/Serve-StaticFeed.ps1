[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Root,

    [Parameter(Mandatory = $true)]
    [string] $CertificatePath,

    [string] $CertificatePasswordEnvironmentVariable,

    [string] $BindAddress = '127.0.0.1',

    [int] $Port = 443
)

$ErrorActionPreference = 'Stop'

$rootPath = [System.IO.Path]::GetFullPath($Root)
if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
    throw "Static feed root directory does not exist: $rootPath"
}
$rootPrefix = $rootPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

$certFullPath = [System.IO.Path]::GetFullPath($CertificatePath)
if (-not (Test-Path -LiteralPath $certFullPath -PathType Leaf)) {
    throw "TLS server certificate PFX does not exist: $certFullPath"
}

$password = [string]::Empty
if (-not [string]::IsNullOrWhiteSpace($CertificatePasswordEnvironmentVariable)) {
    $envValue = [Environment]::GetEnvironmentVariable($CertificatePasswordEnvironmentVariable)
    if ($null -eq $envValue) {
        throw "TLS certificate password environment variable '$CertificatePasswordEnvironmentVariable' is not set."
    }
    $password = $envValue
}

$ipAddress = [System.Net.IPAddress]::Parse($BindAddress)
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certFullPath,
    $password,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable `
        -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
)

function Get-FeedContentType {
    param([string] $Path)
    switch ([System.IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        '.msix' { return 'application/msix' }
        '.yaml' { return 'application/yaml' }
        '.yml'  { return 'application/yaml' }
        default { return 'application/octet-stream' }
    }
}

function Write-SimpleHttpResponse {
    param(
        [System.Net.Security.SslStream] $TlsStream,
        [string] $Status,
        [bool] $CloseConnection
    )
    $connHeader = if ($CloseConnection) { 'close' } else { 'keep-alive' }
    $headerText = "HTTP/1.1 $Status`r`nContent-Length: 0`r`nConnection: $connHeader`r`n`r`n"
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($headerText)
    $TlsStream.Write($bytes, 0, $bytes.Length)
    $TlsStream.Flush()
}

function Invoke-SingleHttpRequest {
    param(
        [System.Net.Security.SslStream] $TlsStream,
        [string] $RootPrefix
    )
    $headerBuffer = [System.IO.MemoryStream]::new()
    try {
        $state = 0
        while ($headerBuffer.Length -lt 65536) {
            try {
                $byte = $TlsStream.ReadByte()
            }
            catch {
                return $false
            }
            if ($byte -lt 0) {
                return $false
            }
            $headerBuffer.WriteByte([byte]$byte)
            if (($state -eq 0 -and $byte -eq 13) -or ($state -eq 2 -and $byte -eq 13)) { $state++ }
            elseif (($state -eq 1 -and $byte -eq 10) -or ($state -eq 3 -and $byte -eq 10)) { $state++ }
            else { $state = if ($byte -eq 13) { 1 } else { 0 } }
            if ($state -eq 4) { break }
        }
        if ($state -ne 4) {
            return $false
        }
        $requestText = [System.Text.Encoding]::ASCII.GetString($headerBuffer.ToArray())
    }
    finally {
        $headerBuffer.Dispose()
    }

    $lines = $requestText -split "`r`n" | Where-Object { $_.Length -gt 0 }
    if ($lines.Count -eq 0) {
        return $false
    }
    $requestParts = $lines[0] -split ' ', 3
    if ($requestParts.Count -lt 2) {
        Write-SimpleHttpResponse -TlsStream $TlsStream -Status '400 Bad Request' -CloseConnection $true
        return $false
    }

    $method = $requestParts[0].ToUpperInvariant()
    $rawTarget = $requestParts[1]
    $headers = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    for ($i = 1; $i -lt $lines.Count; $i++) {
        $colon = $lines[$i].IndexOf(':')
        if ($colon -gt 0) {
            $headers[$lines[$i].Substring(0, $colon).Trim()] = $lines[$i].Substring($colon + 1).Trim()
        }
    }

    $connValue = $null
    $closeConnection = $headers.TryGetValue('Connection', [ref]$connValue) -and
        ($connValue.IndexOf('close', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)

    if ($method -notin @('GET', 'HEAD')) {
        Write-SimpleHttpResponse -TlsStream $TlsStream -Status '405 Method Not Allowed' -CloseConnection $closeConnection
        return (-not $closeConnection)
    }

    $targetPath = if ($rawTarget.StartsWith('/')) { $rawTarget } else { '/' + $rawTarget }
    $parsedUri = $null
    if (-not [System.Uri]::TryCreate("https://localhost$targetPath", [System.UriKind]::Absolute, [ref]$parsedUri)) {
        Write-SimpleHttpResponse -TlsStream $TlsStream -Status '400 Bad Request' -CloseConnection $true
        return $false
    }

    $unescaped = [System.Uri]::UnescapeDataString($parsedUri.AbsolutePath).TrimStart('/')
    if ([string]::IsNullOrWhiteSpace($unescaped)) {
        $unescaped = 'source2.msix'
    }

    foreach ($segment in ($unescaped -split '/')) {
        if ([string]::IsNullOrEmpty($segment) -or $segment.StartsWith('.') -or $segment.Contains('\')) {
            Write-SimpleHttpResponse -TlsStream $TlsStream -Status '404 Not Found' -CloseConnection $closeConnection
            return (-not $closeConnection)
        }
    }

    $fullPath = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($RootPrefix, $unescaped.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    )
    if (-not $fullPath.StartsWith($RootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.File]::Exists($fullPath)) {
        Write-SimpleHttpResponse -TlsStream $TlsStream -Status '404 Not Found' -CloseConnection $closeConnection
        return (-not $closeConnection)
    }

    $fileInfo = [System.IO.FileInfo]::new($fullPath)
    $totalLength = $fileInfo.Length
    $lastModifiedRfc = $fileInfo.LastWriteTimeUtc.ToString('R', [System.Globalization.CultureInfo]::InvariantCulture)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashStream = [System.IO.File]::Open($fullPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        try {
            $digest = $sha.ComputeHash($hashStream)
        }
        finally {
            $hashStream.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
    $etagBuilder = [System.Text.StringBuilder]::new(34).Append('"')
    for ($i = 0; $i -lt 16; $i++) {
        [void]$etagBuilder.Append($digest[$i].ToString('x2', [System.Globalization.CultureInfo]::InvariantCulture))
    }
    $etag = $etagBuilder.Append('"').ToString()

    $cacheControl = if ($unescaped.Equals('source2.msix', [System.StringComparison]::OrdinalIgnoreCase)) {
        'no-cache'
    }
    else {
        'public, max-age=31536000, immutable'
    }
    $connHeader = if ($closeConnection) { 'close' } else { 'keep-alive' }

    $ifNoneMatch = $null
    if ($headers.TryGetValue('If-None-Match', [ref]$ifNoneMatch) -and
        [string]::Equals($ifNoneMatch.Trim(), $etag, [System.StringComparison]::Ordinal)) {
        $notModifiedHeader = [System.Text.Encoding]::ASCII.GetBytes(
            "HTTP/1.1 304 Not Modified`r`n" +
            "ETag: $etag`r`n" +
            "Last-Modified: $lastModifiedRfc`r`n" +
            "Cache-Control: $cacheControl`r`n" +
            "Connection: $connHeader`r`n`r`n"
        )
        $TlsStream.Write($notModifiedHeader, 0, $notModifiedHeader.Length)
        $TlsStream.Flush()
        return (-not $closeConnection)
    }

    [long]$start = 0
    [long]$end = $totalLength - 1
    $isPartial = $false
    $rangeHeader = $null
    if ($totalLength -gt 0 -and
        $headers.TryGetValue('Range', [ref]$rangeHeader) -and
        $rangeHeader.StartsWith('bytes=', [System.StringComparison]::OrdinalIgnoreCase)) {
        $spec = $rangeHeader.Substring(6).Trim()
        $dash = $spec.IndexOf('-')
        if ($dash -ge 0 -and $spec.IndexOf(',') -lt 0) {
            $left = $spec.Substring(0, $dash).Trim()
            $right = $spec.Substring($dash + 1).Trim()
            [long]$parsedStart = 0
            if ($left.Length -gt 0 -and [long]::TryParse($left, [ref]$parsedStart)) {
                [long]$parsedEnd = $totalLength - 1
                if ($right.Length -gt 0) {
                    [long]$rawEnd = 0
                    if ([long]::TryParse($right, [ref]$rawEnd)) {
                        $parsedEnd = [Math]::Min($rawEnd, $totalLength - 1)
                    }
                }
                if ($parsedStart -ge 0 -and $parsedStart -le $parsedEnd -and $parsedStart -lt $totalLength) {
                    $start = $parsedStart
                    $end = $parsedEnd
                    $isPartial = $true
                }
            }
        }
    }

    [long]$sendLength = if ($totalLength -eq 0) { 0 } else { $end - $start + 1 }
    $statusLine = if ($isPartial) { 'HTTP/1.1 206 Partial Content' } else { 'HTTP/1.1 200 OK' }
    $contentType = Get-FeedContentType -Path $fullPath
    $responseHeader = "$statusLine`r`n" +
        "Content-Type: $contentType`r`n" +
        "Content-Length: $sendLength`r`n" +
        "Accept-Ranges: bytes`r`n" +
        "ETag: $etag`r`n" +
        "Last-Modified: $lastModifiedRfc`r`n" +
        "Cache-Control: $cacheControl`r`n"
    if ($isPartial) {
        $responseHeader += "Content-Range: bytes $start-$end/$totalLength`r`n"
    }
    $responseHeader += "Connection: $connHeader`r`n`r`n"

    $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($responseHeader)
    $TlsStream.Write($headerBytes, 0, $headerBytes.Length)

    if ($method -eq 'GET' -and $sendLength -gt 0) {
        $fileStream = [System.IO.File]::Open($fullPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        try {
            [void]$fileStream.Seek($start, [System.IO.SeekOrigin]::Begin)
            $buffer = [byte[]]::new(65536)
            [long]$remaining = $sendLength
            while ($remaining -gt 0) {
                $toRead = [int][Math]::Min([long]$buffer.Length, $remaining)
                $read = $fileStream.Read($buffer, 0, $toRead)
                if ($read -le 0) { break }
                $TlsStream.Write($buffer, 0, $read)
                $remaining -= $read
            }
        }
        finally {
            $fileStream.Dispose()
        }
    }
    $TlsStream.Flush()
    return (-not $closeConnection)
}

$sslProtocols = if ($PSVersionTable.PSVersion.Major -ge 7) {
    [System.Security.Authentication.SslProtocols]::None
}
else {
    [System.Security.Authentication.SslProtocols]::Tls12
}

$listener = [System.Net.Sockets.TcpListener]::new($ipAddress, $Port)
$listener.Start()
Write-Output "Serving $rootPrefix over HTTPS on $($ipAddress):$Port"

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        $tls = $null
        try {
            $client.ReceiveTimeout = 5000
            $client.SendTimeout = 30000
            $tls = [System.Net.Security.SslStream]::new($client.GetStream(), $false)
            $tls.AuthenticateAsServer($certificate, $false, $sslProtocols, $false)
            while (Invoke-SingleHttpRequest -TlsStream $tls -RootPrefix $rootPrefix) {
            }
        }
        catch {
            Write-Verbose "Transient client error: $($_.Exception.Message)"
        }
        finally {
            if ($null -ne $tls) { $tls.Dispose() }
            $client.Dispose()
        }
    }
}
finally {
    $listener.Stop()
    $certificate.Dispose()
}
