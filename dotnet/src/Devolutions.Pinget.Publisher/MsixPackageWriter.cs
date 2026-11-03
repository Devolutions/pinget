using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pinget.Publisher;

/// <summary>
/// Writes MSIX packages in the AppxPackaging (<c>makeappx pack</c>) ZIP layout.
/// </summary>
/// <remarks>
/// Windows AppxSip computes package digests over the exact ZIP structure, so the writer
/// mirrors what <c>makeappx</c> and psign's signing normalizer emit: stored entries without
/// data descriptors, central-directory entries with "version made by" 45 and zero external
/// attributes, and ZIP64 end records followed by a classic end record holding the ZIP64
/// sentinels. The output is byte-identical to the Rust publisher's <c>msix.rs</c> writer.
/// <see cref="System.IO.Compression.ZipArchive"/> must not be used to write these packages.
/// </remarks>
internal static class MsixPackageWriter
{
    internal const string BlockMapName = "AppxBlockMap.xml";
    internal const string ContentTypesName = "[Content_Types].xml";
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint EndSignature = 0x06054b50;
    private const int LocalHeaderLength = 30;
    private const ushort VersionNeeded = 20;
    private const ushort VersionMadeBy = 45;
    private const ulong Zip64EndRecordSize = 44;
    // MS-DOS date for 1980-01-01, keeping package bytes reproducible.
    private const ushort DosDate = 0x0021;
    private const int BlockSize = 64 * 1024;

    private static readonly string[] ReservedNames =
        [BlockMapName, ContentTypesName, "AppxSignature.p7x", "AppxMetadata/CodeIntegrity.cat"];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Builds an unsigned MSIX from payload files, adding the block map and content types.
    /// </summary>
    /// <param name="files">Package-relative part names using <c>/</c> separators, in package order.</param>
    public static byte[] Write(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in files)
        {
            ValidatePartName(name);
            if (!names.Add(AsciiLower(name)))
                throw new PublisherException($"duplicate MSIX part name: {name}");
        }
        var blockMap = BlockMap(files);
        var contentTypes = ContentTypes(files);
        var writer = new ZipWriter();
        foreach (var (name, data) in files)
            writer.Add(name, data);
        writer.Add(BlockMapName, Utf8.GetBytes(blockMap));
        writer.Add(ContentTypesName, Utf8.GetBytes(contentTypes));
        return writer.Finish();
    }

    private static void ValidatePartName(string name)
    {
        if (name.Length == 0
            || name.StartsWith('/')
            || name.EndsWith('/')
            || name.Contains('\\')
            || name.Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == "..")
            || ReservedNames.Any(part => AsciiEqualsIgnoreCase(part, name)))
            throw new PublisherException($"invalid MSIX part name: {name}");
    }

    private static string BlockMap(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var xml = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\r\n<BlockMap xmlns=\"http://schemas.microsoft.com/appx/2010/blockmap\" HashMethod=\"http://www.w3.org/2001/04/xmlenc#sha256\">");
        foreach (var (name, data) in files)
        {
            // Block-map names use backslashes even though ZIP entry names use slashes.
            xml.Append("<File Name=\"").Append(EscapeAttribute(name.Replace('/', '\\')))
                .Append("\" Size=\"").Append(data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" LfhSize=\"").Append((LocalHeaderLength + Utf8.GetByteCount(name)).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("\">");
            for (var offset = 0; offset < data.Length; offset += BlockSize)
            {
                var block = data.AsSpan(offset, Math.Min(BlockSize, data.Length - offset));
                xml.Append("<Block Hash=\"").Append(Convert.ToBase64String(SHA256.HashData(block))).Append("\"/>");
            }
            xml.Append("</File>");
        }
        xml.Append("</BlockMap>");
        return xml.ToString();
    }

    private static string ContentTypes(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        // Rust orders defaults with a BTreeMap over UTF-8 bytes.
        var defaults = new SortedDictionary<string, string>(Utf8OrdinalComparer.Instance);
        var overrides = new List<string>();
        foreach (var (name, _) in files)
        {
            var fileName = name[(name.LastIndexOf('/') + 1)..];
            var dot = fileName.LastIndexOf('.');
            if (dot > 0)
            {
                var extension = AsciiLower(fileName[(dot + 1)..]);
                defaults.TryAdd(extension, ContentTypeFor(extension));
            }
            else
            {
                overrides.Add("/" + name);
            }
        }
        var xml = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        foreach (var (extension, contentType) in defaults)
            xml.Append("<Default Extension=\"").Append(EscapeAttribute(extension)).Append("\" ContentType=\"").Append(contentType).Append("\"/>");
        foreach (var part in overrides)
            xml.Append("<Override PartName=\"").Append(EscapeAttribute(part)).Append("\" ContentType=\"application/octet-stream\"/>");
        xml.Append("<Override PartName=\"/AppxBlockMap.xml\" ContentType=\"application/vnd.ms-appx.blockmap+xml\"/></Types>");
        return xml.ToString();
    }

    private static string ContentTypeFor(string extension) => extension switch
    {
        // makeappx maps the manifest's extension to the AppX manifest type.
        "xml" => "application/vnd.ms-appx.manifest+xml",
        "png" => "image/png",
        _ => "application/octet-stream",
    };

    private static string EscapeAttribute(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string AsciiLower(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
                span[index] = char.IsAsciiLetterUpper(source[index]) ? (char)(source[index] | 0x20) : source[index];
        });

    private static bool AsciiEqualsIgnoreCase(string left, string right) =>
        string.Equals(AsciiLower(left), AsciiLower(right), StringComparison.Ordinal);

    private sealed class Utf8OrdinalComparer : IComparer<string>
    {
        public static readonly Utf8OrdinalComparer Instance = new();

        public int Compare(string? x, string? y) =>
            Utf8.GetBytes(x ?? "").AsSpan().SequenceCompareTo(Utf8.GetBytes(y ?? ""));
    }

    private sealed class ZipWriter
    {
        private readonly MemoryStream output = new();
        private readonly MemoryStream central = new();
        private ulong entries;

        public void Add(string name, byte[] data)
        {
            var nameBytes = Utf8.GetBytes(name);
            if (output.Length > uint.MaxValue || (ulong)data.LongLength > uint.MaxValue || nameBytes.Length > ushort.MaxValue)
                throw new PublisherException($"MSIX part is too large for this writer: {name}");
            var offset = (uint)output.Length;
            var size = (uint)data.Length;
            var nameLength = (ushort)nameBytes.Length;
            var crc = Crc32.Compute(data);

            PutU32(output, LocalHeaderSignature);
            PutU16(output, VersionNeeded);
            PutU16(output, 0); // flags
            PutU16(output, 0); // stored
            PutU16(output, 0); // time
            PutU16(output, DosDate);
            PutU32(output, crc);
            PutU32(output, size);
            PutU32(output, size);
            PutU16(output, nameLength);
            PutU16(output, 0); // extra
            output.Write(nameBytes);
            output.Write(data);

            PutU32(central, CentralHeaderSignature);
            PutU16(central, VersionMadeBy);
            PutU16(central, VersionNeeded);
            PutU16(central, 0); // flags
            PutU16(central, 0); // stored
            PutU16(central, 0); // time
            PutU16(central, DosDate);
            PutU32(central, crc);
            PutU32(central, size);
            PutU32(central, size);
            PutU16(central, nameLength);
            PutU16(central, 0); // extra
            PutU16(central, 0); // comment
            PutU16(central, 0); // disk
            PutU16(central, 0); // internal attributes
            PutU32(central, 0); // external attributes
            PutU32(central, offset);
            central.Write(nameBytes);
            entries++;
        }

        public byte[] Finish()
        {
            var centralOffset = (ulong)output.Length;
            if (centralOffset > uint.MaxValue)
                throw new PublisherException("MSIX package is too large for this writer");
            var centralSize = (ulong)central.Length;
            central.Position = 0;
            central.CopyTo(output);
            var zip64EndOffset = centralOffset + centralSize;

            PutU32(output, Zip64EndSignature);
            PutU64(output, Zip64EndRecordSize);
            PutU16(output, VersionMadeBy);
            PutU16(output, VersionMadeBy);
            PutU32(output, 0); // this disk
            PutU32(output, 0); // central directory disk
            PutU64(output, entries);
            PutU64(output, entries);
            PutU64(output, centralSize);
            PutU64(output, centralOffset);

            PutU32(output, Zip64LocatorSignature);
            PutU32(output, 0); // ZIP64 end record disk
            PutU64(output, zip64EndOffset);
            PutU32(output, 1); // total disks

            PutU32(output, EndSignature);
            for (var index = 0; index < 4; index++)
                PutU16(output, ushort.MaxValue);
            PutU32(output, uint.MaxValue);
            PutU32(output, uint.MaxValue);
            PutU16(output, 0); // comment
            return output.ToArray();
        }

        private static void PutU16(Stream stream, ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        private static void PutU32(Stream stream, uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        private static void PutU64(Stream stream, ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            stream.Write(buffer);
        }
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }
}
