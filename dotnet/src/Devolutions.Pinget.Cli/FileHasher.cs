using System.IO.Compression;
using System.Security.Cryptography;

namespace Devolutions.Pinget.Cli;

/// <summary>
/// Hashing for <c>pinget hash</c>. Kept in step with <c>rust/crates/pinget-cli/src/file_hash.rs</c>:
/// lowercase hex, files streamed rather than read whole, and <c>--msix</c> hashes the package's
/// <c>AppxSignature.p7x</c> entry.
/// </summary>
internal static class FileHasher
{
    private const string MsixSignatureEntry = "AppxSignature.p7x";

    internal static string Sha256Hex(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    internal static string Sha256FileHex(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256Hex(stream);
    }

    /// <exception cref="InvalidDataException">The file is not an MSIX/Appx package or is not signed.</exception>
    internal static string MsixSignatureSha256Hex(string path)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("Not a valid MSIX/Appx package");
        }

        using (archive)
        {
            var signature = archive.GetEntry(MsixSignatureEntry)
                ?? throw new InvalidDataException($"The MSIX/Appx package is not signed ({MsixSignatureEntry} not found)");
            using var stream = signature.Open();
            return Sha256Hex(stream);
        }
    }
}
