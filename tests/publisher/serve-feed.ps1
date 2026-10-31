param(
    [Parameter(Mandatory = $true)]
    [string] $Root,
    [Parameter(Mandatory = $true)]
    [string] $CertificatePath,
    [int] $Port = 8765,
    [string] $HarmlessPayloadPath
)

$ErrorActionPreference = 'Stop'
$rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
$listener = [System.Net.Sockets.TcpListener]::new(
    [System.Net.IPAddress]::Loopback,
    $Port
)
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $CertificatePath,
    [string]::Empty,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable `
        -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
)
$listener.Start()
Write-Output "Serving $rootPath over TLS on 127.0.0.1:$Port"

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        $tls = $null
        try {
            $tls = [System.Net.Security.SslStream]::new(
                $client.GetStream(),
                $false
            )
            $tls.AuthenticateAsServer(
                $certificate,
                $false,
                [System.Security.Authentication.SslProtocols]::Tls12,
                $false
            )
            $requestBuffer = [System.IO.MemoryStream]::new()
            try {
                $previous = 0
                $current = 0
                while (($current = $tls.ReadByte()) -ge 0) {
                    $requestBuffer.WriteByte([byte]$current)
                    if ($previous -eq 13 -and $current -eq 10) {
                        $bytes = $requestBuffer.ToArray()
                        $length = $bytes.Length
                        if ($length -ge 4 -and
                            $bytes[$length - 4] -eq 13 -and
                            $bytes[$length - 3] -eq 10 -and
                            $bytes[$length - 2] -eq 13 -and
                            $bytes[$length - 1] -eq 10) {
                            break
                        }
                    }
                    $previous = $current
                }
                $requestText = [System.Text.Encoding]::ASCII.GetString(
                    $requestBuffer.ToArray()
                )
            }
            finally {
                $requestBuffer.Dispose()
            }

            $requestLine = ($requestText -split "`r`n", 2)[0]
            $parts = $requestLine -split ' ', 3
            if ($parts.Length -lt 2 -or $parts[0] -notin @('GET', 'HEAD')) {
                $status = '405 Method Not Allowed'
                $body = [byte[]]::new(0)
                $contentLength = 0
            }
            else {
                $relative = [System.Uri]::UnescapeDataString(
                    ([System.Uri]::new("http://127.0.0.1:$Port$($parts[1])")).AbsolutePath.TrimStart('/')
                ).Replace('/', '\')
                if ([string]::IsNullOrWhiteSpace($relative)) {
                    $relative = 'source2.msix'
                }
                $isHarmlessPayload = $relative.Equals(
                    'HarmlessPayload.txt',
                    [System.StringComparison]::OrdinalIgnoreCase
                ) -and -not [string]::IsNullOrWhiteSpace($HarmlessPayloadPath)
                if ($isHarmlessPayload) {
                    $filePath = [System.IO.Path]::GetFullPath($HarmlessPayloadPath)
                    $allowedPath = $true
                }
                else {
                    $filePath = [System.IO.Path]::GetFullPath(
                        [System.IO.Path]::Combine($rootPath, $relative)
                    )
                    $allowedPath = $filePath.StartsWith(
                        $rootPath,
                        [System.StringComparison]::OrdinalIgnoreCase
                    )
                }
                if (-not $allowedPath -or -not [System.IO.File]::Exists($filePath)) {
                    $status = '404 Not Found'
                    $body = [byte[]]::new(0)
                    $contentLength = 0
                }
                else {
                    $status = '200 OK'
                    $body = [System.IO.File]::ReadAllBytes($filePath)
                    $contentLength = $body.Length
                    if ($parts[0] -eq 'HEAD') {
                        $body = [byte[]]::new(0)
                    }
                }
            }

            $header = [System.Text.Encoding]::ASCII.GetBytes(
                "HTTP/1.1 $status`r`n" +
                "Content-Length: $contentLength`r`n" +
                "Content-Type: application/octet-stream`r`n" +
                "Connection: close`r`n`r`n"
            )
            $tls.Write($header, 0, $header.Length)
            if ($body.Length -gt 0) {
                $tls.Write($body, 0, $body.Length)
            }
            $tls.Flush()
        }
        finally {
            if ($null -ne $tls) {
                $tls.Dispose()
            }
            $client.Dispose()
        }
    }
}
finally {
    $listener.Stop()
    $certificate.Dispose()
}
