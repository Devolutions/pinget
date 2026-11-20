using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Devolutions.Pinget.Core;

internal static class HomebrewPreIndexedSource
{
    private const string ExpectedIdentityName = "Pinget.HomebrewSource";
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumIndexBytes = 512L * 1024 * 1024;
    private const long MaximumSignatureBytes = 16L * 1024 * 1024;

    public static string Update(HttpClient client, SourceRecord source, string? appRoot = null, string? psignToolPath = null)
    {
        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException("Homebrew static sources are only supported on native macOS and Linux clients.");
        if (string.IsNullOrWhiteSpace(source.Publisher))
            throw new InvalidOperationException("Homebrew source has no expected publisher configured.");

        var stateDir = SourceStoreManager.SourceStateDir(source, appRoot);
        Directory.CreateDirectory(stateDir);
        var url = $"{Repository.ValidateHomebrewSourceBaseUrl(source.Arg)}/brew.msix";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            throw new InvalidOperationException("Homebrew source MSIX exceeds the 2 GiB size limit.");

        var nonce = Guid.NewGuid().ToString("N");
        // Psign chooses its MSIX verifier from the file extension.
        var stagedPackage = Path.Combine(stateDir, $"brew.{nonce}.tmp.msix");
        var stagedIndex = Path.Combine(stateDir, $"index.{nonce}.tmp");
        try
        {
            using (var input = response.Content.ReadAsStream())
            using (var output = new FileStream(stagedPackage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyBounded(input, output, MaximumPackageBytes, "Homebrew source MSIX");
                output.Flush(flushToDisk: true);
            }

            var index = VerifyMsix(stagedPackage, source, psignToolPath);
            using (var output = new FileStream(stagedIndex, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(index);
                output.Flush(flushToDisk: true);
            }

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = stagedIndex,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                }.ToString()))
            {
                connection.Open();
                _ = PreIndexedSource.SearchV2(connection, new PackageQuery(), SearchSemantics.Many);
            }

            var indexPath = PreIndexedSource.IndexPath(source, appRoot);
            File.Move(stagedIndex, indexPath, overwrite: true);
            PreIndexedSource.RemoveSqliteSidecars(indexPath);
            File.Move(stagedPackage, Path.Combine(stateDir, "brew.msix"), overwrite: true);

            source.SourceVersion = response.Headers.TryGetValues("x-ms-meta-sourceversion", out var versions)
                ? versions.FirstOrDefault()
                : null;
            if (response.Headers.ETag is not null)
                source.ETag = response.Headers.ETag.ToString();
            if (response.Content.Headers.LastModified is DateTimeOffset lastModified)
                source.LastModified = lastModified.ToString("R");
            source.LastUpdate = DateTime.UtcNow;
            return "downloaded and verified brew.msix";
        }
        finally
        {
            TryDelete(stagedPackage);
            TryDelete(stagedIndex);
        }
    }

    private static byte[] VerifyMsix(string path, SourceRecord source, string? psignToolPath)
    {
        byte[] manifestBytes;
        byte[] signatureBytes;
        using (var archive = ZipFile.OpenRead(path))
        {
            manifestBytes = ReadEntry(archive, "AppxManifest.xml", 1024 * 1024);
            signatureBytes = ReadEntry(archive, "AppxSignature.p7x", MaximumSignatureBytes);
        }

        var identity = ReadIdentity(manifestBytes);
        if (!string.Equals(identity.Name, ExpectedIdentityName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Homebrew source MSIX identity mismatch (expected {ExpectedIdentityName}, found {identity.Name}).");
        if (!string.Equals(identity.Publisher, source.Publisher, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Homebrew source MSIX publisher does not match the configured publisher.");

        var tool = FindPsignTool(psignToolPath)
            ?? throw new InvalidOperationException("psign-tool is required to verify Homebrew sources; install Devolutions.Psign.Tool.");
        RunPsign(tool, "MSIX digest verification", ["portable", "verify-msix", path]);
        var trustArguments = new List<string> { "portable", "trust-verify-msix", path };
        foreach (var anchor in source.TrustedCa)
        {
            trustArguments.Add("--trusted-ca");
            trustArguments.Add(anchor);
        }
        RunPsign(tool, "MSIX signature trust verification", trustArguments);

        var workspace = Path.Combine(Path.GetTempPath(), $"pinget-homebrew-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var signaturePath = Path.Combine(workspace, "AppxSignature.p7x");
            File.WriteAllBytes(signaturePath, signatureBytes);
            var reportText = RunPsign(tool, "MSIX signature inspection", ["portable", "inspect-pkcs7", signaturePath]);
            JsonNode? report;
            try { report = JsonNode.Parse(reportText); }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException("psign-tool returned an unreadable signature report.", ex);
            }
            string? encoded;
            try { encoded = report?["signers"]?[0]?["signer_certificate_der_base64"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or FormatException)
            {
                encoded = null;
            }
            if (encoded is null)
                throw new InvalidOperationException("Homebrew source signature has no signer certificate.");
            byte[] der;
            try { der = Convert.FromBase64String(encoded); }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Homebrew source signer certificate is not valid base64.", ex);
            }
            VerifySigner(der, source.Publisher!);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        using var verifiedArchive = ZipFile.OpenRead(path);
        return ReadEntry(verifiedArchive, "Public/index.db", MaximumIndexBytes);
    }

    private static (string Name, string Publisher) ReadIdentity(byte[] manifestBytes)
    {
        using var stream = new MemoryStream(manifestBytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        var identity = document.Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Identity")
            ?? throw new InvalidOperationException("Homebrew source AppxManifest.xml has no Identity element.");
        var name = (string?)identity.Attribute("Name")
            ?? throw new InvalidOperationException("Homebrew source MSIX identity has no Name.");
        var publisher = (string?)identity.Attribute("Publisher")
            ?? throw new InvalidOperationException("Homebrew source MSIX identity has no Publisher.");
        return (name, publisher);
    }

    private static void VerifySigner(byte[] der, string publisher)
    {
        X509Certificate2 certificate;
        try
        {
#pragma warning disable SYSLIB0057
            certificate = new X509Certificate2(der);
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Homebrew source signer certificate could not be parsed.", ex);
        }
        using (certificate)
        {
            if (!string.Equals(certificate.Subject, publisher, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Homebrew source signer does not match the configured publisher.");
            var codeSigning = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3"));
            if (!codeSigning)
                throw new InvalidOperationException("Homebrew source signer lacks the Code Signing enhanced key usage.");
        }
    }

    private static byte[] ReadEntry(ZipArchive archive, string name, long limit)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidOperationException($"Homebrew source MSIX is missing {name}.");
        if (entry.Length > limit)
            throw new InvalidOperationException($"Homebrew source MSIX member {name} exceeds the {limit}-byte limit.");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        CopyBounded(input, output, limit, $"Homebrew source MSIX member {name}");
        return output.ToArray();
    }

    private static void CopyBounded(Stream input, Stream output, long limit, string description)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new InvalidOperationException($"{description} exceeds the {limit}-byte limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static string? FindPsignTool(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"psign-tool does not exist at {fullPath}.");
            return fullPath;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "psign-tool");
            if (File.Exists(candidate))
                return candidate;
        }
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var candidate = Path.Combine(home, ".dotnet", "tools", "psign-tool");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string RunPsign(string tool, string operation, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start psign-tool at {tool}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var stdout = output.GetAwaiter().GetResult();
        var stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{operation} with psign-tool failed (exit code {process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
        return stdout;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
