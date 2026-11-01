using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pinget.Publisher;

internal static class MsZipCompressor
{
    private const uint CompressAlgorithmMsZip = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumSize = 64 * 1024 * 1024;

    public static byte[] Compress(byte[] input)
    {
        if (input.Length > MaximumSize)
            throw new PublisherException("MSZIP input exceeds the 64 MiB limit.");
        if (!OperatingSystem.IsWindows())
            throw new PublisherException("WinGet v2 MSZIP generation currently requires Windows Compression API support.");
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
        if (!OperatingSystem.IsWindows())
            throw new PublisherException("WinGet v2 MSZIP verification currently requires Windows Compression API support.");
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
