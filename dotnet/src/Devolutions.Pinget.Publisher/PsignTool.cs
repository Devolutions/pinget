using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace Pinget.Publisher;

/// <summary>
/// Signs and verifies MSIX packages with the cross-platform <c>psign-tool</c> from
/// https://github.com/Devolutions/psign (dotnet tool <c>Devolutions.Psign.Tool</c>).
/// </summary>
/// <remarks>
/// Every invocation passes its arguments through a response file in a private temporary
/// directory so the PFX password never appears on a process command line, and so
/// <c>.cmd</c> tool shims never re-parse arguments.
/// </remarks>
internal static class PsignTool
{
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    private const string TrustedCaHint = "Pass --trusted-ca with the signing root certificate when it is not trusted by default.";

    /// <summary>Locates <c>psign-tool</c> from an explicit path, <c>PATH</c>, or the .NET global tool directory.</summary>
    public static string? Find(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var path = Path.GetFullPath(explicitPath);
            return File.Exists(path) ? path : null;
        }
        string[] names = OperatingSystem.IsWindows() ? ["psign-tool.exe", "psign-tool.cmd"] : ["psign-tool"];
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => directory.Trim('"'))
            .Where(directory => directory.Length > 0)
            .ToList();
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (!string.IsNullOrEmpty(home))
            directories.Add(Path.Combine(home, ".dotnet", "tools"));
        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                string candidate;
                try { candidate = Path.Combine(directory, name); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>Resolves <c>psign-tool</c> or explains how to install it.</summary>
    public static string Require(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && !File.Exists(Path.GetFullPath(explicitPath)))
            throw new PublisherException($"psign-tool does not exist at {Path.GetFullPath(explicitPath)}");
        return Find(explicitPath) ?? throw new PublisherException(
            "psign-tool was not found; install it with `dotnet tool install -g Devolutions.Psign.Tool` or pass --psign-tool");
    }

    /// <summary>Signs <paramref name="package"/> in place with a PFX, using portable Authenticode signing.</summary>
    public static void Sign(string tool, string package, string pfx, string password, string? timestampUrl)
    {
        if (password.Contains('\r') || password.Contains('\n'))
            throw new PublisherException("the PFX password must not contain line breaks");
        var arguments = new List<string>
        {
            "--mode", "portable", "sign",
            "--pfx", PathArgument(pfx),
            // `--password=<value>` keeps a leading `@` from being read as a response-file include.
            $"--password={password}",
            "--digest", "sha256",
        };
        if (timestampUrl is not null)
            arguments.AddRange(["--timestamp-url", timestampUrl, "--timestamp-digest", "sha256"]);
        arguments.Add(PathArgument(package));
        _ = Run(tool, "MSIX signing", arguments, password);
    }

    /// <summary>
    /// Verifies the package digests, its signer against <paramref name="publisher"/>, and its certificate chain.
    /// </summary>
    /// <remarks>
    /// Without trusted CAs, Windows uses the operating-system trust store (<c>signtool verify /pa</c>
    /// semantics) and other platforms use psign's Microsoft AuthRoot anchors.
    /// </remarks>
    public static void Verify(string tool, string package, string publisher, IReadOnlyList<string> trustedCertificateAuthorities)
    {
        var packageArgument = PathArgument(package);
        _ = Run(tool, "MSIX digest verification", ["portable", "verify-msix", packageArgument]);
        VerifySigner(tool, package, publisher);
        var hint = trustedCertificateAuthorities.Count == 0 ? TrustedCaHint : null;
        if (trustedCertificateAuthorities.Count == 0 && OperatingSystem.IsWindows())
        {
            _ = Run(
                tool,
                "MSIX signature trust verification",
                ["--mode", "windows", "verify", "--policy", "pa", packageArgument],
                failureHint: hint);
        }
        else
        {
            var arguments = new List<string> { "portable", "trust-verify-msix", packageArgument };
            foreach (var anchor in trustedCertificateAuthorities)
            {
                arguments.Add("--trusted-ca");
                arguments.Add(PathArgument(anchor));
            }
            _ = Run(tool, "MSIX signature trust verification", arguments, failureHint: hint);
        }
    }

    private static void VerifySigner(string tool, string package, string publisher)
    {
        byte[] signature;
        try
        {
            using var archive = ZipFile.OpenRead(package);
            var entry = archive.GetEntry("AppxSignature.p7x")
                ?? throw new PublisherException("source MSIX is not signed");
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            signature = buffer.ToArray();
        }
        catch (PublisherException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new PublisherException($"could not read source MSIX: {ex.Message}", ex);
        }

        using var workspace = Workspace.Create();
        var signaturePath = workspace.Write("AppxSignature.p7x", signature);
        var output = Run(tool, "MSIX signature inspection", ["portable", "inspect-pkcs7", PathArgument(signaturePath)]);
        JsonNode? report;
        try { report = JsonNode.Parse(output); }
        catch (System.Text.Json.JsonException) { report = null; }
        if (report is null)
            throw new PublisherException("psign-tool returned an unreadable signature report");
        string? encoded = null;
        try { encoded = report["signers"]?[0]?["signer_certificate_der_base64"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or FormatException) { }
        if (encoded is null)
            throw new PublisherException("source MSIX signature has no signer certificate");
        byte[] certificate;
        try { certificate = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new PublisherException("source MSIX signer certificate is not valid base64"); }
        CheckSigningCertificate(certificate, publisher);
    }

    /// <summary>Requires the signer subject to match the package publisher, the Code Signing EKU and current validity.</summary>
    public static void CheckSigningCertificate(byte[] der, string publisher)
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
            throw new PublisherException("signing certificate could not be parsed", ex);
        }
        using (certificate)
        {
            // X509Certificate2.Subject is the Windows format that MSIX Identity/@Publisher must match.
            var subject = certificate.Subject;
            if (!string.Equals(subject, publisher, StringComparison.OrdinalIgnoreCase))
                throw new PublisherException(
                    $"signing certificate subject does not match --publisher (expected \"{publisher}\", found \"{subject}\")");
            var now = DateTime.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                throw new PublisherException("signing certificate is not currently valid");
            var codeSigning = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CodeSigningOid));
            if (!codeSigning)
                throw new PublisherException("signing certificate must include the Code Signing enhanced key usage");
        }
    }

    internal static string PathArgument(string path)
    {
        if (path.Contains('\r') || path.Contains('\n'))
            throw new PublisherException($"path contains a line break: \"{path}\"");
        return path;
    }

    /// <summary>Formats arguments as a psign-tool response file: one quoted argument per line.</summary>
    internal static string ResponseFile(IEnumerable<string> arguments)
    {
        var response = new StringBuilder();
        foreach (var argument in arguments)
            // Doubled quotes encode a literal quote.
            response.Append('"').Append(argument.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"\n");
        return response.ToString();
    }

    /// <summary>Runs <c>psign-tool @&lt;response file&gt;</c> and returns standard output.</summary>
    private static string Run(
        string tool,
        string operation,
        IReadOnlyList<string> arguments,
        string? secret = null,
        string? failureHint = null)
    {
        int exitCode;
        string stdout;
        string stderr;
        using (var workspace = Workspace.Create())
        {
            var responsePath = workspace.Write("arguments.rsp", new UTF8Encoding(false).GetBytes(ResponseFile(arguments)));
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            SetCommand(startInfo, tool, "@" + PathArgument(responsePath));
            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                throw new PublisherException($"could not start {tool}: {ex.Message}", ex);
            }
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
            exitCode = process.ExitCode;
        }
        if (exitCode == 0)
            return stdout;
        var message = stderr.Trim();
        if (message.Length == 0)
            message = stdout.Trim();
        if (!string.IsNullOrEmpty(secret))
            message = message.Replace(secret, "<redacted>", StringComparison.Ordinal);
        if (message.Length > 4000)
            message = message[..4000].Trim();
        var error = $"{operation} with psign-tool failed (exit code {exitCode}): {message}";
        if (failureHint is not null)
            error += "\n" + failureHint;
        throw new PublisherException(error);
    }

    /// <summary>Sets the process to run <paramref name="tool"/> with one argument.</summary>
    /// <remarks>
    /// Windows starts <c>.cmd</c> and <c>.bat</c> files through <c>cmd.exe</c>, which re-parses the
    /// command line and does not understand the quoting <see cref="ProcessStartInfo.ArgumentList"/>
    /// produces. Run such shims explicitly with <c>/s /c</c> so the outer quotes are stripped as a
    /// pair, disable AutoRun and delayed expansion, and reject the characters <c>cmd.exe</c> still
    /// interprets inside quotes.
    /// </remarks>
    internal static void SetCommand(ProcessStartInfo startInfo, string tool, string argument)
    {
        var extension = Path.GetExtension(tool);
        if (!OperatingSystem.IsWindows()
            || !(extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = tool;
            startInfo.ArgumentList.Add(argument);
            return;
        }
        foreach (var value in new[] { tool, argument })
        {
            if (value.Contains('"') || value.Contains('%'))
                throw new PublisherException($"cannot pass \"{value}\" to a batch-file psign-tool; use psign-tool.exe or a path without '%' or '\"'");
        }
        startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        startInfo.Arguments = $"/d /v:off /s /c \"\"{tool}\" \"{argument}\"\"";
    }

    /// <summary>A private temporary directory removed on dispose.</summary>
    private sealed class Workspace : IDisposable
    {
        private Workspace(string path) => Path = path;

        public string Path { get; }

        public static Workspace Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pinget-psign-{Environment.ProcessId}-{Guid.NewGuid():N}");
            if (Directory.Exists(path) || File.Exists(path))
                throw new PublisherException($"temporary directory already exists: {path}");
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(path);
            else
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new Workspace(path);
        }

        public string Write(string name, byte[] contents)
        {
            var path = System.IO.Path.Combine(Path, name);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(path, options);
            stream.Write(contents);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
