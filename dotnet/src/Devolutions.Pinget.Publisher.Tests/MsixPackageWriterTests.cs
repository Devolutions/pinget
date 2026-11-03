using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

public sealed class MsixPackageWriterTests
{
    private const int BlockSize = 64 * 1024;

    // Mirrors the `sample()` payload of the Rust `msix.rs` unit tests.
    private static (string Name, byte[] Data)[] Sample() =>
    [
        ("AppxManifest.xml", "<Package/>"u8.ToArray()),
        ("Public/index.db", Enumerable.Repeat((byte)7, BlockSize + 3).ToArray()),
        ("Assets/Logo.png", [1, 2, 3]),
    ];

    // Exercises content-type ordering, overrides, attribute escaping, empty parts,
    // multiple blocks and non-ASCII part names.
    private static (string Name, byte[] Data)[] EdgeCases()
    {
        var patterned = new byte[3 * BlockSize + 17];
        for (var index = 0; index < patterned.Length; index++)
            patterned[index] = unchecked((byte)((index * 31) ^ (index >> 7)));
        return
        [
            ("AppxManifest.xml", "<?xml version=\"1.0\"?><Package/>\n"u8.ToArray()),
            ("Public/index.db", patterned),
            ("Assets/A&B <x>.PNG", Enumerable.Repeat((byte)9, 10).ToArray()),
            ("LICENSE", "MIT"u8.ToArray()),
            ("docs/.hidden", []),
            ("docs/trailing.", "t"u8.ToArray()),
            ("data/Z.json", "{}"u8.ToArray()),
            ("data/caf\u00e9.Db", "x"u8.ToArray()),
        ];
    }

    [Theory]
    // Expected values come from the Rust publisher's msix::write_package for the same payloads;
    // CI compares Rust and .NET publisher output, so these must stay in lockstep.
    [InlineData("sample", 67221, "e2227f8cec6c28cda47d0bf148c9a9257d6663cf3eb4585db098cb6ced7cd3c6")]
    [InlineData("edge", 199763, "0f2625ced43f110cdd93f3d6d83495dba88b3e36d891ad11b4118d4a019e430e")]
    public void Write_IsByteIdenticalToRustWriter(string payload, int expectedLength, string expectedSha256)
    {
        var package = MsixPackageWriter.Write(payload == "sample" ? Sample() : EdgeCases());

        Assert.Equal(expectedLength, package.Length);
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());
    }

    [Fact]
    public void Write_ReadsBackWithBlockMapAndContentTypes()
    {
        var package = MsixPackageWriter.Write(Sample());

        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        Assert.Equal(
            ["AppxManifest.xml", "Public/index.db", "Assets/Logo.png", "AppxBlockMap.xml", "[Content_Types].xml"],
            archive.Entries.Select(entry => entry.FullName).ToArray());
        Assert.All(archive.Entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
        Assert.Equal(Enumerable.Repeat((byte)7, BlockSize + 3).ToArray(), ReadEntry(archive, "Public/index.db"));
        Assert.Equal([1, 2, 3], ReadEntry(archive, "Assets/Logo.png"));

        var blockMap = Encoding.UTF8.GetString(ReadEntry(archive, "AppxBlockMap.xml"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\r\n<BlockMap ", blockMap, StringComparison.Ordinal);
        Assert.Contains("<File Name=\"Public\\index.db\" Size=\"65539\" LfhSize=\"45\">", blockMap, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(blockMap, "<Block Hash="));
        var firstBlock = Convert.ToBase64String(SHA256.HashData(Enumerable.Repeat((byte)7, BlockSize).ToArray()));
        Assert.Contains($"<Block Hash=\"{firstBlock}\"/>", blockMap, StringComparison.Ordinal);

        var contentTypes = Encoding.UTF8.GetString(ReadEntry(archive, "[Content_Types].xml"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Types ", contentTypes, StringComparison.Ordinal);
        Assert.Contains("<Default Extension=\"db\" ContentType=\"application/octet-stream\"/>", contentTypes, StringComparison.Ordinal);
        Assert.Contains("<Default Extension=\"png\" ContentType=\"image/png\"/>", contentTypes, StringComparison.Ordinal);
        Assert.Contains("<Default Extension=\"xml\" ContentType=\"application/vnd.ms-appx.manifest+xml\"/>", contentTypes, StringComparison.Ordinal);
        Assert.EndsWith("<Override PartName=\"/AppxBlockMap.xml\" ContentType=\"application/vnd.ms-appx.blockmap+xml\"/></Types>", contentTypes, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_UsesAppxPackagingZipLayout()
    {
        var package = MsixPackageWriter.Write(Sample());

        var end = package.Length - 22;
        Assert.Equal(0x06054b50u, U32(package, end));
        Assert.All(package.AsSpan(end + 8, 12).ToArray(), value => Assert.Equal(0xff, value));
        Assert.Equal(0, U16(package, end + 20));
        var locator = end - 20;
        Assert.Equal(0x07064b50u, U32(package, locator));
        Assert.Equal(1u, U32(package, locator + 16));
        var zip64End = locator - 56;
        Assert.Equal(0x06064b50u, U32(package, zip64End));
        Assert.Equal((ulong)zip64End, U64(package, locator + 8));
        Assert.Equal(44ul, U64(package, zip64End + 4));
        Assert.Equal(45, U16(package, zip64End + 12));
        Assert.Equal(45, U16(package, zip64End + 14));
        Assert.Equal(5ul, U64(package, zip64End + 24));
        Assert.Equal(5ul, U64(package, zip64End + 32));
        var centralSize = checked((int)U64(package, zip64End + 40));
        var centralOffset = checked((int)U64(package, zip64End + 48));
        Assert.Equal(zip64End, centralOffset + centralSize);

        var entries = 0;
        for (var cursor = centralOffset; cursor < zip64End; entries++)
        {
            Assert.Equal(0x02014b50u, U32(package, cursor));
            Assert.Equal(45, U16(package, cursor + 4));
            Assert.Equal(20, U16(package, cursor + 6));
            Assert.Equal(0, U16(package, cursor + 8));
            Assert.Equal(0, U16(package, cursor + 10));
            Assert.Equal(0, U16(package, cursor + 12));
            Assert.Equal(0x0021, U16(package, cursor + 14));
            Assert.Equal(0u, U32(package, cursor + 38));
            var localOffset = checked((int)U32(package, cursor + 42));
            Assert.Equal(0x04034b50u, U32(package, localOffset));
            Assert.Equal(20, U16(package, localOffset + 4));
            Assert.Equal(0, U16(package, localOffset + 6));
            Assert.Equal(0, U16(package, localOffset + 8));
            Assert.Equal(0, U16(package, localOffset + 28));
            cursor += 46 + U16(package, cursor + 28);
        }
        Assert.Equal(5, entries);
        Assert.Equal(package, MsixPackageWriter.Write(Sample()));
    }

    [Theory]
    [InlineData("AppxBlockMap.xml")]
    [InlineData("[content_types].xml")]
    [InlineData("appxsignature.p7x")]
    [InlineData("AppxMetadata/CodeIntegrity.cat")]
    [InlineData("../x")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("")]
    public void Write_RejectsReservedAndUnsafeNames(string name)
    {
        var error = Assert.Throws<PublisherException>(() => MsixPackageWriter.Write([(name, [])]));
        Assert.Contains("invalid MSIX part name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RejectsCaseInsensitiveDuplicateNames()
    {
        var error = Assert.Throws<PublisherException>(() => MsixPackageWriter.Write([("A.xml", []), ("a.xml", [])]));
        Assert.Contains("duplicate MSIX part name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseFile_QuotesEveryArgumentOnItsOwnLine()
    {
        Assert.Equal(
            "\"--password=p@ss \"\"word\"\" %PATH%\"\n\"C:\\a b\\c.msix\"\n\"\"\n",
            PsignTool.ResponseFile(["--password=p@ss \"word\" %PATH%", "C:\\a b\\c.msix", ""]));
    }

    [Fact]
    public void Find_MissingExplicitTool_IsNotFound()
    {
        Assert.Null(PsignTool.Find(Path.Combine(Path.GetTempPath(), $"missing-psign-tool-{Guid.NewGuid():N}.exe")));
        var error = Assert.Throws<PublisherException>(() =>
            PsignTool.Require(Path.Combine(Path.GetTempPath(), $"missing-psign-tool-{Guid.NewGuid():N}.exe")));
        Assert.Contains("psign-tool does not exist at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetCommand_RunsBatchShimsThroughCmdWithPairedQuotes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var startInfo = new System.Diagnostics.ProcessStartInfo();
        PsignTool.SetCommand(startInfo, @"C:\Tools & More\psign-tool.cmd", @"@C:\Temp (x)\arguments.rsp");
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), startInfo.FileName);
        Assert.Equal(@"/d /v:off /s /c """"C:\Tools & More\psign-tool.cmd"" ""@C:\Temp (x)\arguments.rsp""""", startInfo.Arguments);
        Assert.Throws<PublisherException>(() =>
            PsignTool.SetCommand(new System.Diagnostics.ProcessStartInfo(), @"C:\Tools\psign-tool.cmd", @"@C:\%TMP%\arguments.rsp"));

        var exe = new System.Diagnostics.ProcessStartInfo();
        PsignTool.SetCommand(exe, @"C:\Tools\psign-tool.exe", @"@C:\%TMP%\arguments.rsp");
        Assert.Equal(@"C:\Tools\psign-tool.exe", exe.FileName);
        Assert.Equal([@"@C:\%TMP%\arguments.rsp"], exe.ArgumentList);
    }

    [Fact]
    public void Sign_PassesArgumentsThroughResponseFileAndRedactsPassword()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var temp = new TempDirectory();
        var captured = Path.Combine(temp.Path, "captured.rsp");
        var commandLine = Path.Combine(temp.Path, "command-line.txt");
        // A space and cmd.exe metacharacters in the shim path must survive the cmd.exe re-parse.
        var toolDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "fake psign & (tool)")).FullName;
        var tool = Path.Combine(toolDirectory, "fake-psign-tool.cmd");
        File.WriteAllText(
            tool,
            string.Join(
                "\r\n",
                [
                    "@echo off",
                    "setlocal DisableDelayedExpansion",
                    $"echo %*> \"{commandLine}\"",
                    "set \"RSP=%~1\"",
                    $"copy /y \"%RSP:~1%\" \"{captured}\" >nul",
                    "echo signing failed for secret-pa$$word 1>&2",
                    "exit /b 7",
                ]) + "\r\n",
            new UTF8Encoding(false));
        const string password = "secret-pa$$word";
        var package = Path.Combine(temp.Path, "source2.msix");
        var pfx = Path.Combine(temp.Path, "signer.pfx");

        var error = Assert.Throws<PublisherException>(() =>
            PsignTool.Sign(tool, package, pfx, password, "https://timestamp.example.test"));

        Assert.StartsWith("MSIX signing with psign-tool failed (exit code 7): ", error.Message, StringComparison.Ordinal);
        Assert.Contains("<redacted>", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, File.ReadAllText(commandLine), StringComparison.Ordinal);
        Assert.StartsWith("@", File.ReadAllText(commandLine).Trim().Trim('"'), StringComparison.Ordinal);
        Assert.Equal(
            PsignTool.ResponseFile(
            [
                "--mode", "portable", "sign", "--pfx", pfx, $"--password={password}", "--digest", "sha256",
                "--timestamp-url", "https://timestamp.example.test", "--timestamp-digest", "sha256", package,
            ]),
            File.ReadAllText(captured));
    }

    [Fact]
    public void Sign_RejectsPasswordWithLineBreaks()
    {
        var error = Assert.Throws<PublisherException>(() =>
            PsignTool.Sign("unused-tool", "package.msix", "signer.pfx", "line\nbreak", null));
        Assert.Contains("line breaks", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckSigningCertificate_RequiresMatchingSubjectCodeSigningAndValidity()
    {
        const string subject = "CN=Pinget Signer, O=\"Example, Inc.\"";
        var valid = CreateCertificate(subject, codeSigning: true, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        PsignTool.CheckSigningCertificate(valid, subject);
        PsignTool.CheckSigningCertificate(valid, "cn=pinget signer, o=\"example, inc.\"");
        Assert.Throws<PublisherException>(() => PsignTool.CheckSigningCertificate(valid, subject + " "));

        var mismatch = Assert.Throws<PublisherException>(() => PsignTool.CheckSigningCertificate(valid, "CN=Someone Else"));
        Assert.Contains("does not match --publisher", mismatch.Message, StringComparison.Ordinal);
        var noEku = Assert.Throws<PublisherException>(() => PsignTool.CheckSigningCertificate(
            CreateCertificate(subject, codeSigning: false, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)), subject));
        Assert.Contains("Code Signing", noEku.Message, StringComparison.Ordinal);
        var expired = Assert.Throws<PublisherException>(() => PsignTool.CheckSigningCertificate(
            CreateCertificate(subject, codeSigning: true, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2)), subject));
        Assert.Contains("not currently valid", expired.Message, StringComparison.Ordinal);
        Assert.Throws<PublisherException>(() => PsignTool.CheckSigningCertificate([1, 2, 3], subject));
    }

    [Fact]
    public void PortableMsZipDecoder_ReadsNativeCompressorOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var input = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 200_000).Select(value => $"  - v: \"{value % 977}\"\n")));

        var compressed = MsZipCompressor.Compress(input);

        Assert.Equal(input, MsZipCompressor.DecompressPortable(compressed));
        Assert.Equal(input, MsZipCompressor.Decompress(compressed));
        Assert.Throws<PublisherException>(() => MsZipCompressor.DecompressPortable(compressed[..^5]));
    }

    private static byte[] CreateCertificate(string subject, bool codeSigning, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var usages = new OidCollection { new(codeSigning ? "1.3.6.1.5.5.7.3.3" : "1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return certificate.Export(X509ContentType.Cert);
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry(name));
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        for (var index = value.IndexOf(search, StringComparison.Ordinal); index >= 0; index = value.IndexOf(search, index + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pinget-msix-writer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
