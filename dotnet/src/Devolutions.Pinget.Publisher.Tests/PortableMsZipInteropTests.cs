using System.Security.Cryptography;
using System.Text.Json;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

public sealed class PortableMsZipInteropTests
{
    private const int MaximumSize = 64 * 1024 * 1024;
    private const string RootVariable = "PINGET_MSZIP_FIXTURE_ROOT";
    private const string RunVariable = "PINGET_MSZIP_RUN_ID";
    private const string Protocol = "pinget-portable-mszip-v1";

    [Fact]
    public void PortableEncoderEncodesFixtureMatrixWhenRequested()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        var runId = Environment.GetEnvironmentVariable(RunVariable);
        if (root is null && runId is null)
        {
            var original = "portable encoder smoke check"u8.ToArray();
            var compressed = MsZipCompressor.CompressPortable(original);
            Assert.Equal(original, MsZipCompressor.DecompressPortable(compressed));
            return;
        }

        Assert.False(string.IsNullOrEmpty(root), $"{RootVariable} must be set");
        Assert.False(string.IsNullOrEmpty(runId), $"{RunVariable} must be set");
        Assert.True(OperatingSystem.IsLinux(), "fixture production must run on Linux");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root!, "run.json")));
        var document = manifest.RootElement;
        Assert.Equal(Protocol, document.GetProperty("protocol").GetString());
        Assert.Equal(runId, document.GetProperty("run_id").GetString());
        var cases = document.GetProperty("cases");
        Assert.NotEmpty(cases.EnumerateArray());

        var output = Path.Combine(root!, "dotnet");
        Assert.False(Directory.Exists(output), "fixture directory must be fresh");
        Directory.CreateDirectory(output);
        foreach (var item in cases.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            Assert.NotNull(name);
            Assert.Matches("^[a-z0-9_]+$", name!);
            var input = File.ReadAllBytes(Path.Combine(root!, "inputs", name + ".bin"));
            Assert.Equal(item.GetProperty("size").GetInt32(), input.Length);
            Assert.Equal(
                item.GetProperty("sha256").GetString(),
                Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());

            var compressed = MsZipCompressor.CompressPortable(input);
            Assert.True(compressed.Length >= 24, $"missing MSZIP header for {name}");
            using var file = new FileStream(Path.Combine(output, name + ".mszip"), FileMode.CreateNew);
            file.Write(compressed);
        }
    }

    [Fact]
    public void PortableEncoderRejectsInputOver64MiB()
    {
        var input = new byte[MaximumSize + 1];
        var error = Assert.Throws<PublisherException>(() => MsZipCompressor.CompressPortable(input));
        Assert.Equal("MSZIP input exceeds the 64 MiB limit.", error.Message);
    }
}
