[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Root,

    [Parameter(Mandatory = $true)]
    [string] $CertificatePath,

    [string] $CertificatePasswordEnvironmentVariable,

    [string] $BindAddress = '127.0.0.1',

    [int] $Port = 8443,

    [int] $MaxConcurrentClients = 64
)

$ErrorActionPreference = 'Stop'

if ($MaxConcurrentClients -lt 1) {
    throw 'MaxConcurrentClients must be at least 1.'
}

# Ensure CodeDom / Add-Type on Windows PowerShell 5.1 has a writable process TMP/TEMP directory.
$tempProbe = Join-Path ([System.IO.Path]::GetTempPath()) ("pinget-serve-probe-" + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.File]::WriteAllText($tempProbe, 'ok')
    Remove-Item -LiteralPath $tempProbe -Force -ErrorAction SilentlyContinue
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $fallbackTemp = Join-Path $env:LOCALAPPDATA 'Temp'
        New-Item -ItemType Directory -Path $fallbackTemp -Force | Out-Null
        [Environment]::SetEnvironmentVariable('TMP', $fallbackTemp, 'Process')
        [Environment]::SetEnvironmentVariable('TEMP', $fallbackTemp, 'Process')
        $env:TMP = $fallbackTemp
        $env:TEMP = $fallbackTemp
    }
}

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
$useOsDefaultTls = $PSVersionTable.PSVersion.Major -ge 7

# Windows Schannel (lsass.exe) requires a persisted key container (UserKeySet) rather than
# EphemeralKeySet for SslStream.AuthenticateAsServer; omit Exportable and delete the transient
# key container on shutdown so private key material does not outlive the server process.
$loaderType = 'System.Security.Cryptography.X509Certificates.X509CertificateLoader' -as [type]
$certificate = if ($null -ne $loaderType) {
    $loaderType::LoadPkcs12FromFile(
        $certFullPath,
        $password,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
    )
}
else {
    [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $certFullPath,
        $password,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
    )
}

$serverSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pinget.StaticFeedHost
{
    public static class TlsStaticServer
    {
        public static void Run(
            string rootPrefix,
            X509Certificate2 certificate,
            IPAddress bindAddress,
            int port,
            int maxConcurrentClients,
            bool useOsDefaultTls)
        {
            SslProtocols protocols = useOsDefaultTls ? SslProtocols.None : SslProtocols.Tls12;
            var concurrencyGate = new SemaphoreSlim(maxConcurrentClients, maxConcurrentClients);
            var listener = new TcpListener(bindAddress, port);
            listener.Start();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Serving {0} over HTTPS on {1}:{2}", rootPrefix, bindAddress, port));
            Console.Out.Flush();

            try
            {
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    concurrencyGate.Wait();
                    Task.Run(() =>
                    {
                        try
                        {
                            HandleClient(client, certificate, protocols, rootPrefix);
                        }
                        finally
                        {
                            concurrencyGate.Release();
                        }
                    });
                }
            }
            finally
            {
                listener.Stop();
                DeleteTransientPrivateKey(certificate);
                certificate.Dispose();
                concurrencyGate.Dispose();
            }
        }

        private static void DeleteTransientPrivateKey(X509Certificate2 certificate)
        {
            try
            {
                using (RSA rsa = certificate.GetRSAPrivateKey())
                {
                    var rsaCng = rsa as RSACng;
                    if (rsaCng != null && rsaCng.Key != null)
                    {
                        rsaCng.Key.Delete();
                        return;
                    }
                    var rsaCsp = rsa as RSACryptoServiceProvider;
                    if (rsaCsp != null)
                    {
                        rsaCsp.PersistKeyInCsp = false;
                        rsaCsp.Clear();
                        return;
                    }
                }
            }
            catch
            {
            }

            try
            {
                using (ECDsa ecdsa = certificate.GetECDsaPrivateKey())
                {
                    var ecdsaCng = ecdsa as ECDsaCng;
                    if (ecdsaCng != null && ecdsaCng.Key != null)
                    {
                        ecdsaCng.Key.Delete();
                    }
                }
            }
            catch
            {
            }
        }

        private static void HandleClient(TcpClient client, X509Certificate2 certificate, SslProtocols protocols, string rootPrefix)
        {
            using (client)
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 30000;
                using (var tls = new SslStream(client.GetStream(), false))
                {
                    try
                    {
                        tls.AuthenticateAsServer(certificate, false, protocols, false);
                        while (ServeOneRequest(tls, rootPrefix))
                        {
                        }
                    }
                    catch
                    {
                        // Ignore transient client disconnects and TLS probes.
                    }
                }
            }
        }

        private static bool ServeOneRequest(SslStream tls, string rootPrefix)
        {
            var headerBytes = new MemoryStream();
            int state = 0;
            while (headerBytes.Length < 65536)
            {
                int b;
                try
                {
                    b = tls.ReadByte();
                }
                catch
                {
                    return false;
                }
                if (b < 0)
                {
                    return false;
                }
                headerBytes.WriteByte((byte)b);
                if ((state == 0 && b == '\r') || (state == 2 && b == '\r')) state++;
                else if ((state == 1 && b == '\n') || (state == 3 && b == '\n')) state++;
                else state = (b == '\r') ? 1 : 0;
                if (state == 4) break;
            }
            if (state != 4)
            {
                return false;
            }

            string requestText = Encoding.ASCII.GetString(headerBytes.ToArray());
            string[] lines = requestText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return false;
            }

            string[] requestParts = lines[0].Split(new[] { ' ' }, 3);
            if (requestParts.Length < 2)
            {
                WriteSimpleResponse(tls, "400 Bad Request", true, null);
                return false;
            }

            string method = requestParts[0].ToUpperInvariant();
            string rawTarget = requestParts[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }

            string connValue;
            bool closeConnection = headers.TryGetValue("Connection", out connValue) &&
                connValue.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0;

            if (method != "GET" && method != "HEAD")
            {
                WriteSimpleResponse(tls, "405 Method Not Allowed", closeConnection, null);
                return !closeConnection;
            }

            string targetPath = rawTarget.StartsWith("/", StringComparison.Ordinal) ? rawTarget : "/" + rawTarget;
            Uri parsedUri;
            if (!Uri.TryCreate("https://localhost" + targetPath, UriKind.Absolute, out parsedUri))
            {
                WriteSimpleResponse(tls, "400 Bad Request", true, null);
                return false;
            }

            string unescaped = Uri.UnescapeDataString(parsedUri.AbsolutePath).TrimStart('/');
            if (string.IsNullOrWhiteSpace(unescaped))
            {
                unescaped = "source2.msix";
            }

            string[] segments = unescaped.Split('/');
            foreach (string seg in segments)
            {
                if (string.IsNullOrEmpty(seg) || seg.StartsWith(".", StringComparison.Ordinal) || seg.IndexOf('\\') >= 0)
                {
                    WriteSimpleResponse(tls, "404 Not Found", closeConnection, null);
                    return !closeConnection;
                }
            }

            string fullPath = Path.GetFullPath(Path.Combine(rootPrefix, unescaped.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                WriteSimpleResponse(tls, "404 Not Found", closeConnection, null);
                return !closeConnection;
            }

            var info = new FileInfo(fullPath);
            long totalLength = info.Length;
            DateTime lastModifiedUtc = info.LastWriteTimeUtc;
            string etag;
            using (var fsHash = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(fsHash);
                var sb = new StringBuilder(34).Append('"');
                for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                etag = sb.Append('"').ToString();
            }

            string cacheControl = unescaped.Equals("source2.msix", StringComparison.OrdinalIgnoreCase)
                ? "no-cache"
                : "public, max-age=31536000, immutable";
            string contentType = GetContentType(fullPath);

            string inm;
            if (headers.TryGetValue("If-None-Match", out inm) && string.Equals(inm.Trim(), etag, StringComparison.Ordinal))
            {
                string notModified = string.Format(
                    CultureInfo.InvariantCulture,
                    "HTTP/1.1 304 Not Modified\r\nETag: {0}\r\nLast-Modified: {1:R}\r\nCache-Control: {2}\r\nConnection: {3}\r\n\r\n",
                    etag,
                    lastModifiedUtc,
                    cacheControl,
                    closeConnection ? "close" : "keep-alive");
                byte[] nmBytes = Encoding.ASCII.GetBytes(notModified);
                tls.Write(nmBytes, 0, nmBytes.Length);
                tls.Flush();
                return !closeConnection;
            }

            long start = 0;
            long end = totalLength - 1;
            bool isPartial = false;
            string rangeHeader;
            if (headers.TryGetValue("Range", out rangeHeader) &&
                rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string spec = rangeHeader.Substring(6).Trim();
                // Multi-range requests fall back to a full 200 response; invalid single-range requests return 416.
                if (spec.IndexOf(',') < 0)
                {
                    if (!TryParseSingleByteRange(spec, totalLength, out start, out end))
                    {
                        WriteSimpleResponse(
                            tls,
                            "416 Range Not Satisfiable",
                            closeConnection,
                            string.Format(CultureInfo.InvariantCulture, "Content-Range: bytes */{0}\r\n", totalLength));
                        return !closeConnection;
                    }
                    isPartial = true;
                }
            }

            long sendLength = totalLength == 0 ? 0 : (end - start + 1);
            var responseBuilder = new StringBuilder();
            responseBuilder.Append(isPartial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            responseBuilder.AppendFormat(CultureInfo.InvariantCulture, "Content-Type: {0}\r\n", contentType);
            responseBuilder.AppendFormat(CultureInfo.InvariantCulture, "Content-Length: {0}\r\n", sendLength);
            responseBuilder.Append("Accept-Ranges: bytes\r\n");
            responseBuilder.AppendFormat(CultureInfo.InvariantCulture, "ETag: {0}\r\n", etag);
            responseBuilder.AppendFormat(CultureInfo.InvariantCulture, "Last-Modified: {0:R}\r\n", lastModifiedUtc);
            responseBuilder.AppendFormat(CultureInfo.InvariantCulture, "Cache-Control: {0}\r\n", cacheControl);
            if (isPartial)
            {
                responseBuilder.AppendFormat(CultureInfo.InvariantCulture, "Content-Range: bytes {0}-{1}/{2}\r\n", start, end, totalLength);
            }
            responseBuilder.AppendFormat(CultureInfo.InvariantCulture, "Connection: {0}\r\n\r\n", closeConnection ? "close" : "keep-alive");

            byte[] headerOut = Encoding.ASCII.GetBytes(responseBuilder.ToString());
            tls.Write(headerOut, 0, headerOut.Length);

            if (method == "GET" && sendLength > 0)
            {
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buffer = new byte[65536];
                    long remaining = sendLength;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = fs.Read(buffer, 0, toRead);
                        if (read <= 0) break;
                        tls.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }
            tls.Flush();
            return !closeConnection;
        }

        private static bool TryParseSingleByteRange(string spec, long totalLength, out long start, out long end)
        {
            start = 0;
            end = 0;
            if (totalLength <= 0)
            {
                return false;
            }
            int dash = spec.IndexOf('-');
            if (dash < 0)
            {
                return false;
            }
            string left = spec.Substring(0, dash).Trim();
            string right = spec.Substring(dash + 1).Trim();
            if (left.Length == 0)
            {
                long suffixLength;
                if (right.Length == 0 || !long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out suffixLength) || suffixLength <= 0)
                {
                    return false;
                }
                start = suffixLength >= totalLength ? 0 : totalLength - suffixLength;
                end = totalLength - 1;
                return true;
            }

            long parsedStart;
            if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out parsedStart) || parsedStart < 0 || parsedStart >= totalLength)
            {
                return false;
            }
            if (right.Length == 0)
            {
                start = parsedStart;
                end = totalLength - 1;
                return true;
            }

            long parsedEnd;
            if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out parsedEnd) || parsedEnd < parsedStart)
            {
                return false;
            }
            start = parsedStart;
            end = Math.Min(parsedEnd, totalLength - 1);
            return true;
        }

        private static void WriteSimpleResponse(SslStream tls, string status, bool closeConnection, string extraHeaders)
        {
            string resp = string.Format(
                CultureInfo.InvariantCulture,
                "HTTP/1.1 {0}\r\n{1}Content-Length: 0\r\nConnection: {2}\r\n\r\n",
                status,
                extraHeaders ?? string.Empty,
                closeConnection ? "close" : "keep-alive");
            byte[] bytes = Encoding.ASCII.GetBytes(resp);
            tls.Write(bytes, 0, bytes.Length);
            tls.Flush();
        }

        private static string GetContentType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".msix": return "application/msix";
                case ".yaml":
                case ".yml": return "application/yaml";
                default: return "application/octet-stream";
            }
        }
    }
}
'@

if (-not ('Pinget.StaticFeedHost.TlsStaticServer' -as [type])) {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        Add-Type -TypeDefinition $serverSource -ReferencedAssemblies @('System.dll', 'System.Core.dll')
    }
    else {
        Add-Type -TypeDefinition $serverSource
    }
}

[Pinget.StaticFeedHost.TlsStaticServer]::Run($rootPrefix, $certificate, $ipAddress, $Port, $MaxConcurrentClients, $useOsDefaultTls)
