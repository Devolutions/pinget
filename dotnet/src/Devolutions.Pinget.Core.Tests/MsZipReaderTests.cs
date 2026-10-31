using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Devolutions.Pinget.Core;
using Xunit;
using Xunit.Sdk;

namespace Devolutions.Pinget.Core.Tests;

public class MsZipReaderTests
{
    [SkippableFact]
    public void DecompressMszyml_NativeMultiBlockPayload_ReturnsAllVersionRecords()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The fixture uses the Windows Compression API to generate native MSZIP framing.");

        const int versionCount = 500;
        var payload = new StringBuilder("sV: \"1.0\"\nvD:\n");
        for (var i = 0; i < versionCount; i++)
        {
            payload.Append("  - v: 1.0.").Append(i).Append('\n');
            payload.Append("    rP: manifests/Pinget.MultiBlock/1.0.").Append(i).Append(".yaml\n");
            payload.Append("    s256H: ").Append('a', 64).Append('\n');
        }

        var payloadBytes = Encoding.UTF8.GetBytes(payload.ToString());
        Assert.True(payloadBytes.Length > 32768);

        var nativeMsZip = CompressWithWindowsApi(payloadBytes);
        Assert.True(CountBlocks(nativeMsZip) > 1);
        Assert.Equal(payloadBytes, NativeMsZip.DecompressManaged(nativeMsZip));

        var decompressed = PreIndexedSource.DecompressMszyml(nativeMsZip);
        Assert.Equal(payload.ToString(), decompressed);

        var parsed = PreIndexedSource.ParseV2VersionDataDocument(decompressed);
        Assert.NotNull(parsed);
        Assert.Equal(versionCount, parsed.Versions.Count);
        Assert.Equal("1.0.499", parsed.Versions[^1].Version);
        Assert.Equal("manifests/Pinget.MultiBlock/1.0.499.yaml", parsed.Versions[^1].ManifestRelativePath);
    }

    [SkippableFact]
    public void DecompressMszyml_TruncatedNativeMultiBlockPayload_RejectsCorruptFraming()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The fixture uses the Windows Compression API to generate native MSZIP framing.");

        var payloadBytes = Encoding.UTF8.GetBytes("sV: \"1.0\"\nvD:\n" + string.Concat(
            Enumerable.Range(0, 500).Select(i =>
                $"  - v: 1.0.{i}\n    rP: manifests/Pinget.MultiBlock/1.0.{i}.yaml\n    s256H: {new string('b', 64)}\n")));
        var nativeMsZip = CompressWithWindowsApi(payloadBytes);
        var truncated = nativeMsZip[..^1];

        Assert.Throws<InvalidDataException>(() => NativeMsZip.DecompressManaged(truncated));
        Assert.Throws<InvalidDataException>(() => PreIndexedSource.DecompressMszyml(truncated));
    }

    private static int CountBlocks(byte[] data)
    {
        Assert.True(data.AsSpan().StartsWith(new byte[] { 0x0a, 0x51, 0xe5, 0xc0 }));
        var position = 24;
        var count = 0;
        while (position < data.Length)
        {
            Assert.True(data.Length - position >= 6);
            var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
            position += 4;
            Assert.True(compressedLength >= 3);
            Assert.Equal((byte)'C', data[position]);
            Assert.Equal((byte)'K', data[position + 1]);
            Assert.True(compressedLength <= data.Length - position);
            position += (int)compressedLength;
            count++;
        }

        return count;
    }

    private static byte[] CompressWithWindowsApi(byte[] input)
    {
        if (!CreateCompressor(2, IntPtr.Zero, out var compressor))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            _ = Compress(compressor, input, (nuint)input.Length, null, 0, out var required);
            if (Marshal.GetLastWin32Error() != 122)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "MSZIP size query did not report insufficient buffer.");

            var output = new byte[checked((int)required)];
            if (!Compress(compressor, input, (nuint)input.Length, output, (nuint)output.Length, out var written))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            Array.Resize(ref output, checked((int)written));
            return output;
        }
        finally
        {
            _ = CloseCompressor(compressor);
        }
    }

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateCompressor(uint algorithm, IntPtr options, out IntPtr compressor);

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Compress(
        IntPtr compressor, byte[] input, nuint inputSize,
        byte[]? output, nuint outputSize, out nuint outputWritten);

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseCompressor(IntPtr compressor);
}
