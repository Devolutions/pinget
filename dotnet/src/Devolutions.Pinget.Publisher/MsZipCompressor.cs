using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Pinget.Publisher;

internal static class MsZipCompressor
{
    private const uint CompressAlgorithmMsZip = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumSize = 64 * 1024 * 1024;
    private const int BlockSize = 32 * 1024;
    private static ReadOnlySpan<byte> Signature => [0x0a, 0x51, 0xe5, 0xc0];
    private static ReadOnlySpan<byte> LegacyPortableSignature => [0x0a, 0x51, 0xe5, 0xc0, 0, 0, 0, 0];

    public static byte[] Compress(byte[] input)
    {
        ValidateInputSize(input);
        if (!OperatingSystem.IsWindows())
            return CompressPortable(input);
        if (!CreateCompressor(CompressAlgorithmMsZip, IntPtr.Zero, out var compressor))
            throw new PublisherException("Windows Compression API could not create an MSZIP compressor.", LastError());
        try
        {
            _ = CompressBuffer(compressor, input, (nuint)input.Length, null, 0, out var required);
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer || required == 0)
                throw new PublisherException("Windows Compression API did not report the required MSZIP output buffer size.", new Win32Exception(error));

            var output = new byte[checked((int)required)];
            if (!CompressBuffer(compressor, input, (nuint)input.Length, output, (nuint)output.Length, out var written))
                throw new PublisherException("Windows Compression API failed to create the MSZIP stream.", LastError());
            Array.Resize(ref output, checked((int)written));
            return output;
        }
        finally
        {
            _ = CloseCompressor(compressor);
        }
    }

    internal static byte[] CompressPortable(byte[] input)
    {
        ValidateInputSize(input);

        using var output = new MemoryStream();
        var header = new byte[24];
        Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), (ushort)header.Length);
        header[7] = (byte)CompressAlgorithmMsZip;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8, 8), (ulong)input.Length);
        var blockSize = Math.Min(input.Length, BlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16, 8), (ulong)blockSize);
        Span<byte> checksumInput = stackalloc byte[23];
        header.AsSpan(0, 6).CopyTo(checksumInput);
        header.AsSpan(7).CopyTo(checksumInput[6..]);
        header[6] = unchecked((byte)Crc32.Compute(checksumInput));
        output.Write(header);

        var storedHeader = new byte[5];
        storedHeader[0] = 1;
        for (var offset = 0; offset < input.Length; offset += BlockSize)
        {
            var block = input.AsSpan(offset, Math.Min(BlockSize, input.Length - offset));
            using var compressed = new MemoryStream();
            using (var encoder = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                encoder.Write(block);
            var bytes = compressed.ToArray();
            var stored = bytes.Length > block.Length + 5;
            var framedLength = checked((uint)(stored ? block.Length + 7 : bytes.Length + 2));
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, framedLength);
            output.Write(length);
            output.Write("CK"u8);
            if (stored)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(storedHeader.AsSpan(1), (ushort)block.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(storedHeader.AsSpan(3), (ushort)(ushort.MaxValue - block.Length));
                output.Write(storedHeader);
                output.Write(block);
            }
            else
                output.Write(bytes);
        }
        return output.ToArray();
    }

    private static void ValidateInputSize(byte[] input)
    {
        if (input.Length == 0)
            throw new PublisherException("MSZIP input must not be empty.");
        if (input.Length > MaximumSize)
            throw new PublisherException("MSZIP input exceeds the 64 MiB limit.");
    }

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (!CreateCompressor(CompressAlgorithmMsZip, IntPtr.Zero, out var compressor))
            return false;
        return CloseCompressor(compressor);
    }

    public static byte[] Decompress(byte[] input)
    {
        if (HasLegacyPortableHeader(input))
            throw new PublisherException("Legacy portable MSZIP header is not Windows-compatible; rebuild with a fresh --state directory.");
        if (HasOversizedMsZipBlock(input))
            throw new PublisherException("Portable MSZIP block is not Windows-compatible; rebuild with a fresh --state directory.");
        if (!OperatingSystem.IsWindows())
            return DecompressPortable(input);
        if (!CreateDecompressor(CompressAlgorithmMsZip, IntPtr.Zero, out var decompressor))
            throw new PublisherException("Windows Compression API could not create an MSZIP decompressor.", LastError());
        try
        {
            _ = DecompressBuffer(decompressor, input, (nuint)input.Length, null, 0, out var required);
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer)
                throw new PublisherException("Windows Compression API did not report the required decompressed buffer size.", new Win32Exception(error));
            if (required > MaximumSize)
                throw new PublisherException("MSZIP output exceeds the 64 MiB decompression limit.");
            var output = new byte[checked((int)required)];
            if (!DecompressBuffer(decompressor, input, (nuint)input.Length, output, (nuint)output.Length, out var written))
                throw new PublisherException("Windows Compression API failed to decode the MSZIP stream.", LastError());
            Array.Resize(ref output, checked((int)written));
            return output;
        }
        finally
        {
            _ = CloseDecompressor(decompressor);
        }
    }

    internal static bool HasLegacyPortableHeader(ReadOnlySpan<byte> input) =>
        input.StartsWith(LegacyPortableSignature);

    private static bool HasOversizedMsZipBlock(ReadOnlySpan<byte> input)
    {
        if (input.Length < 24 || !input.StartsWith(Signature)
            || BinaryPrimitives.ReadUInt16LittleEndian(input[4..]) != 24 || input[7] != CompressAlgorithmMsZip
            || BinaryPrimitives.ReadUInt32LittleEndian(input[20..]) != 0)
            return false;

        var remaining = BinaryPrimitives.ReadUInt64LittleEndian(input[8..]);
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(input[16..]);
        if (blockSize == 0 || blockSize > BlockSize)
            return false;
        var offset = 24;
        while (remaining > 0)
        {
            if (offset > input.Length - 4)
                return false;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(input[offset..]);
            var expected = Math.Min(remaining, blockSize);
            if (length > BlockSize + 12)
                return true;
            if (length > input.Length - offset - 4)
                return false;
            offset += 4 + (int)length;
            remaining -= expected;
        }
        return false;
    }

    /// <summary>
    /// Decodes a Windows Compression API MSZIP buffer with the managed client decoder,
    /// so verification and publication work on hosts without cabinet.dll.
    /// </summary>
    internal static byte[] DecompressPortable(byte[] input)
    {
        try
        {
            return Devolutions.Pinget.Core.NativeMsZip.DecompressManaged(input);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            throw new PublisherException($"invalid MSZIP version data: {ex.Message}", ex);
        }
    }

    private static Exception LastError() => new Win32Exception(Marshal.GetLastWin32Error());

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateCompressor(uint algorithm, IntPtr options, out IntPtr compressor);

    [DllImport("cabinet.dll", SetLastError = true, EntryPoint = "Compress")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompressBuffer(
        IntPtr compressor,
        byte[] input,
        nuint inputSize,
        byte[]? output,
        nuint outputSize,
        out nuint outputWritten);

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseCompressor(IntPtr compressor);

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDecompressor(uint algorithm, IntPtr options, out IntPtr decompressor);

    [DllImport("cabinet.dll", SetLastError = true, EntryPoint = "Decompress")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DecompressBuffer(
        IntPtr decompressor,
        byte[] input,
        nuint inputSize,
        byte[]? output,
        nuint outputSize,
        out nuint outputWritten);

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDecompressor(IntPtr decompressor);
}
