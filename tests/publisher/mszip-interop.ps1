#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('produce', 'validate-windows')][string]$Mode,
    [string]$Output,
    [string]$Reports,
    [string]$RunId,
    [Alias('InputPath')][string]$InputDirectory,
    [switch]$DotnetNoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Protocol = 'pinget-portable-mszip-v1'
$script:Repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script:Block = 32768
$script:Limit = 67108864
$script:Producers = @('rust', 'dotnet')
$script:Cases = @(
    @{ name = 'single_byte'; size = 1; pattern = 'single' }
    @{ name = 'block_32k_before'; size = 32767; pattern = 'random' }
    @{ name = 'block_32k_at'; size = 32768; pattern = 'random' }
    @{ name = 'block_32k_after'; size = 32769; pattern = 'random' }
    @{ name = 'block_64k_before'; size = 65535; pattern = 'random' }
    @{ name = 'block_64k_at'; size = 65536; pattern = 'random' }
    @{ name = 'block_64k_after'; size = 65537; pattern = 'random' }
    @{ name = 'multiblock_compressible'; size = 98441; pattern = 'repeated' }
    @{ name = 'multiblock_incompressible'; size = 98441; pattern = 'random' }
    @{ name = 'limit_64m_before'; size = 67108863; pattern = 'repeated' }
    @{ name = 'limit_64m_at'; size = 67108864; pattern = 'repeated' }
)
$script:RustTests = @(
    'wave21_tests::mszip_interop_tests::portable_encoder_encodes_fixture_matrix_when_requested'
    'wave21_tests::mszip_interop_tests::portable_encoder_rejects_empty_input'
    'wave21_tests::mszip_interop_tests::portable_encoder_rejects_input_over_64_mib'
)
$script:DotnetTests = @(
    'Devolutions.Pinget.Publisher.Tests.PortableMsZipInteropTests.PortableEncoderEncodesFixtureMatrixWhenRequested'
    'Devolutions.Pinget.Publisher.Tests.PortableMsZipInteropTests.PortableEncoderRejectsEmptyInput'
    'Devolutions.Pinget.Publisher.Tests.PortableMsZipInteropTests.PortableEncoderRejectsInputOver64MiB'
)

if (-not ('Pinget.Interop.Bytes' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace Pinget.Interop {
    public class VerificationError : Exception {
        public VerificationError(string message) : base(message) {}
    }
    public class NativeDecodeError : VerificationError {
        public NativeDecodeError(string message) : base(message) {}
    }
    public class NativeReferenceUnavailable : VerificationError {
        public NativeReferenceUnavailable(string message) : base(message) {}
    }
    public class NativeCompressionError : NativeReferenceUnavailable {
        public int ErrorCode { get; }
        public NativeCompressionError(int code) : base($"native compression control failed: Win32 error {code}") { ErrorCode = code; }
    }
    public static class Bytes {
        public static void Require(bool condition, string message) { if (!condition) throw new VerificationError(message); }
        public static bool Equal(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
        public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        public static byte[] Payload(string name, int size, string pattern) {
            if (pattern == "empty") { Require(size == 0, "empty case has a nonzero size"); return Array.Empty<byte>(); }
            if (pattern == "single") { Require(size == 1, "single-byte case has an incorrect size"); return new byte[]{167}; }
            var result = new byte[size];
            if (pattern == "repeated") {
                var motif = Encoding.ASCII.GetBytes("Pinget MSZIP portable interop block sequence 0123456789\n");
                for (int i = 0; i < size; i += motif.Length) Array.Copy(motif, 0, result, i, Math.Min(motif.Length, size-i));
                return result;
            }
            Require(pattern == "random", $"unknown payload pattern for {name}");
            foreach (char c in name) Require(c <= 127, "payload name is not ASCII");
            var prefix = Encoding.ASCII.GetBytes("pinget-mszip-v1/" + name + "/");
            var seed = new byte[prefix.Length + 8];
            Array.Copy(prefix, seed, prefix.Length);
            for (int i = 0; i < size; i += 32) {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(seed.AsSpan(prefix.Length), (ulong)(i / 32));
                var hash = SHA256.HashData(seed);
                Array.Copy(hash, 0, result, i, Math.Min(32, size-i));
            }
            return result;
        }
        public static uint Crc32(byte[] bytes) {
            uint crc = uint.MaxValue;
            foreach (byte b in bytes) {
                crc ^= b;
                for (int n = 0; n < 8; n++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
            }
            return ~crc;
        }
        public static void CheckJson(string text, string path) {
            using var document = JsonDocument.Parse(text);
            CheckObject(document.RootElement, path);
        }
        static void CheckObject(JsonElement element, string path) {
            if (element.ValueKind == JsonValueKind.Object) {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) {
                    Require(names.Add(property.Name), $"duplicate JSON key '{property.Name}' in {path}");
                    CheckObject(property.Value, path);
                }
            } else if (element.ValueKind == JsonValueKind.Array) {
                foreach (var item in element.EnumerateArray()) CheckObject(item, path);
            }
        }
        // DeflateStream tolerates premature EOF and can buffer trailing bytes. Independently
        // parse the raw bitstream to retain the original strict EOF/trailing-data smoke contract.
        sealed class Bits {
            readonly byte[] data;
            public int Position;
            public Bits(byte[] bytes) { data = bytes; }
            public int Read(int count) {
                Require(Position + count <= data.Length * 8, "incomplete raw DEFLATE data");
                int value = 0;
                for (int i = 0; i < count; i++, Position++) value |= ((data[Position/8] >> (Position%8)) & 1) << i;
                return value;
            }
            public void Align() { Position = (Position + 7) & ~7; }
        }
        sealed class Huffman {
            readonly Dictionary<int, int> table = new Dictionary<int, int>();
            readonly int maximum;
            public Huffman(int[] lengths) {
                var counts = new int[16]; var next = new int[16];
                foreach (int length in lengths) { Require(length >= 0 && length <= 15, "invalid DEFLATE code length"); if (length != 0) counts[length]++; maximum = Math.Max(maximum, length); }
                int code = 0, remaining = 1;
                for (int n = 1; n <= 15; n++) {
                    remaining = (remaining << 1) - counts[n];
                    Require(remaining >= 0, "oversubscribed DEFLATE tree");
                    code = (code + counts[n-1]) << 1; next[n] = code;
                }
                for (int symbol = 0; symbol < lengths.Length; symbol++) {
                    int n = lengths[symbol]; if (n == 0) continue;
                    int c = next[n]++, reverse = 0;
                    for (int i = 0; i < n; i++) { reverse = (reverse << 1) | (c & 1); c >>= 1; }
                    table.Add((n << 16) | reverse, symbol);
                }
            }
            public int Decode(Bits bits) {
                int code = 0;
                for (int n = 1; n <= maximum; n++) {
                    code |= bits.Read(1) << (n-1);
                    if (table.TryGetValue((n << 16) | code, out int symbol)) return symbol;
                }
                throw new VerificationError("invalid raw DEFLATE code");
            }
        }
        static void CheckDeflate(byte[] data, int expected) {
            int[] lengthBase = {3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258};
            int[] lengthExtra = {0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0};
            int[] distanceBase = {1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577};
            int[] distanceExtra = {0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13};
            var bits = new Bits(data); int produced = 0; bool final;
            do {
                final = bits.Read(1) != 0;
                int type = bits.Read(2);
                Require(type != 3, "invalid raw DEFLATE block type");
                if (type == 0) {
                    bits.Align(); int length = bits.Read(16), inverse = bits.Read(16);
                    Require((length ^ inverse) == 65535, "invalid stored DEFLATE length");
                    for (int i = 0; i < length; i++) bits.Read(8);
                    produced += length;
                } else {
                    int[] literals, distances;
                    if (type == 1) {
                        literals = new int[288]; distances = new int[32];
                        for (int i = 0; i < 288; i++) literals[i] = i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8;
                        Array.Fill(distances, 5);
                    } else {
                        int literalCount = bits.Read(5)+257, distanceCount = bits.Read(5)+1, codeCount = bits.Read(4)+4;
                        Require(literalCount <= 286, "invalid DEFLATE literal count");
                        int[] order = {16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15};
                        var codes = new int[19];
                        for (int i = 0; i < codeCount; i++) codes[order[i]] = bits.Read(3);
                        var codeTree = new Huffman(codes); var lengths = new List<int>();
                        while (lengths.Count < literalCount + distanceCount) {
                            int symbol = codeTree.Decode(bits);
                            if (symbol <= 15) lengths.Add(symbol);
                            else {
                                Require(symbol != 16 || lengths.Count != 0, "invalid DEFLATE length repeat");
                                int repeat = symbol == 16 ? bits.Read(2)+3 : symbol == 17 ? bits.Read(3)+3 : bits.Read(7)+11;
                                int value = symbol == 16 ? lengths[lengths.Count-1] : 0;
                                Require(lengths.Count + repeat <= literalCount + distanceCount, "excess DEFLATE code lengths");
                                for (int i = 0; i < repeat; i++) lengths.Add(value);
                            }
                        }
                        literals = lengths.GetRange(0, literalCount).ToArray();
                        distances = lengths.GetRange(literalCount, distanceCount).ToArray();
                        Require(literals[256] != 0, "missing DEFLATE end-of-block code");
                    }
                    var literalTree = new Huffman(literals); var distanceTree = new Huffman(distances);
                    while (true) {
                        int symbol = literalTree.Decode(bits);
                        if (symbol == 256) break;
                        if (symbol < 256) produced++;
                        else {
                            Require(symbol <= 285, "invalid DEFLATE length code");
                            int length = lengthBase[symbol-257] + bits.Read(lengthExtra[symbol-257]);
                            int distance = distanceTree.Decode(bits);
                            Require(distance < 30, "invalid DEFLATE distance code");
                            int back = distanceBase[distance] + bits.Read(distanceExtra[distance]);
                            Require(back <= produced, "invalid DEFLATE back reference");
                            produced += length;
                        }
                        Require(produced <= expected, "incorrect uncompressed MSZIP block length");
                    }
                }
                Require(produced <= expected, "incorrect uncompressed MSZIP block length");
            } while (!final);
            Require((bits.Position + 7)/8 == data.Length, "incomplete or trailing raw DEFLATE data");
            Require(produced == expected, "incorrect uncompressed MSZIP block length");
        }
        public static byte[] CheckFrame(byte[] data, int expected, bool inflate) {
            Require(data.Length >= 24, "truncated MSZIP header");
            Require(data[0] == 10 && data[1] == 81 && data[2] == 229 && data[3] == 192, "invalid MSZIP signature");
            int header = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
            Require(header == 24, $"invalid MSZIP header size {header}");
            Require(data[7] == 2 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)) == 0, "invalid MSZIP algorithm or flags");
            var crcInput = new byte[23]; Array.Copy(data,0,crcInput,0,6); Array.Copy(data,7,crcInput,6,17);
            Require(data[6] == (Crc32(crcInput) & 255), "invalid MSZIP header checksum");
            ulong declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8));
            Require(declared == (ulong)expected, $"MSZIP declared length {declared} != {expected}");
            uint block = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
            Require(block == (expected == 0 ? 32768 : Math.Min(expected,32768)), $"invalid MSZIP block size {block}");
            int offset = 24;
            using var output = new MemoryStream();
            for (int start = 0; start < expected; start += 32768) {
                int count = Math.Min(32768, expected-start);
                Require(offset + 4 <= data.Length, "truncated MSZIP block length");
                uint framed = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)); offset += 4;
                Require(framed >= 2 && framed <= data.Length-offset, "invalid or truncated MSZIP block");
                Require(framed <= 32780, "MSZIP block exceeds the Windows Compression API limit");
                Require(data[offset] == 67 && data[offset+1] == 75, "invalid MSZIP block marker");
                if (inflate) {
                    byte[] raw = data.AsSpan(offset+2,(int)framed-2).ToArray();
                    CheckDeflate(raw, count);
                    try {
                        using var source = new MemoryStream(raw);
                        using var decoder = new DeflateStream(source,CompressionMode.Decompress);
                        var decoded = new byte[count+1]; int read = 0, n;
                        while (read < decoded.Length && (n = decoder.Read(decoded,read,decoded.Length-read)) != 0) read += n;
                        Require(read == count, "incorrect uncompressed MSZIP block length");
                        output.Write(decoded,0,read);
                    } catch (InvalidDataException e) { throw new VerificationError("invalid raw DEFLATE block: " + e.Message); }
                }
                offset += (int)framed;
            }
            Require(offset == data.Length, "unexpected trailing MSZIP data or block");
            return output.ToArray();
        }
    }
    // The interface is a unit-test seam. The command-line gate always constructs CabinetApi.
    public interface INativeApi {
        bool Create(bool compressor, uint algorithm, IntPtr options, out IntPtr handle);
        bool Transform(bool compressor, IntPtr handle, byte[] source, nuint sourceSize, byte[] destination, nuint capacity, out nuint written);
        void Close(bool compressor, IntPtr handle);
        int LastError { get; }
    }
    public sealed class NativeApiCallbacks : INativeApi {
        public Action<bool,uint,IntPtr> OnCreate;
        public Action<bool,IntPtr,byte[],nuint,byte[],nuint> OnTransform;
        public Action<bool,IntPtr> OnClose;
        public IntPtr Handle;
        public nuint Written;
        public bool Success = true;
        public int ErrorCode;
        public int LastError => ErrorCode;
        public bool Create(bool compressor,uint algorithm,IntPtr options,out IntPtr handle) {
            OnCreate?.Invoke(compressor,algorithm,options); handle = Handle; return Success;
        }
        public bool Transform(bool compressor,IntPtr handle,byte[] source,nuint sourceSize,byte[] destination,nuint capacity,out nuint written) {
            OnTransform?.Invoke(compressor,handle,source,sourceSize,destination,capacity); written = Written; return Success;
        }
        public void Close(bool compressor,IntPtr handle) { OnClose?.Invoke(compressor,handle); }
    }
    public sealed class CabinetApi : INativeApi {
        [DllImport("cabinet.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateCompressor(uint algorithm, IntPtr options, out IntPtr handle);
        [DllImport("cabinet.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateDecompressor(uint algorithm, IntPtr options, out IntPtr handle);
        [DllImport("cabinet.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Compress(IntPtr handle, byte[] source, nuint size, [Out] byte[] destination, nuint capacity, out nuint written);
        [DllImport("cabinet.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Decompress(IntPtr handle, byte[] source, nuint size, [Out] byte[] destination, nuint capacity, out nuint written);
        [DllImport("cabinet.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseCompressor(IntPtr handle);
        [DllImport("cabinet.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseDecompressor(IntPtr handle);
        public int LastError { get; private set; }
        public bool Create(bool compressor,uint algorithm,IntPtr options,out IntPtr handle) {
            bool ok = compressor ? CreateCompressor(algorithm,options,out handle) : CreateDecompressor(algorithm,options,out handle);
            LastError = Marshal.GetLastWin32Error(); return ok;
        }
        public bool Transform(bool compressor,IntPtr handle,byte[] source,nuint size,byte[] destination,nuint capacity,out nuint written) {
            bool ok = compressor ? Compress(handle,source,size,destination,capacity,out written) : Decompress(handle,source,size,destination,capacity,out written);
            LastError = Marshal.GetLastWin32Error(); return ok;
        }
        public void Close(bool compressor,IntPtr handle) { if (compressor) CloseCompressor(handle); else CloseDecompressor(handle); }
    }
    public sealed class WindowsNativeMsZip {
        readonly INativeApi api;
        public WindowsNativeMsZip() {
            Bytes.Require(OperatingSystem.IsWindows(), "Windows native validation requires Windows");
            api = new CabinetApi();
        }
        public WindowsNativeMsZip(INativeApi testApi) { api = testApi; }
        public byte[] Decompress(byte[] data,int expected) {
            if (!api.Create(false,2,IntPtr.Zero,out IntPtr handle)) throw new VerificationError($"CreateDecompressor failed: Win32 error {api.LastError}");
            try {
                byte[] source = data.Length == 0 ? new byte[1] : data;
                var destination = new byte[Math.Max(expected,1)];
                if (!api.Transform(false,handle,source,(nuint)data.Length,destination,(nuint)destination.Length,out nuint written))
                    throw new NativeDecodeError($"Decompress failed: Win32 error {api.LastError}");
                Bytes.Require(written == (nuint)expected, $"native decoded length {written} != {expected}");
                return destination.AsSpan(0,(int)written).ToArray();
            } finally { api.Close(false,handle); }
        }
        public byte[] CompressReference(byte[] data) {
            if (!api.Create(true,2,IntPtr.Zero,out IntPtr handle)) throw new NativeReferenceUnavailable($"control CreateCompressor failed: Win32 error {api.LastError}");
            try {
                byte[] source = data.Length == 0 ? new byte[1] : data;
                var destination = new byte[checked(4096+2*data.Length)];
                if (!api.Transform(true,handle,source,(nuint)data.Length,destination,(nuint)destination.Length,out nuint written))
                    throw new NativeCompressionError(api.LastError);
                Bytes.Require(written <= (nuint)destination.Length,"native compression exceeded its output buffer");
                return destination.AsSpan(0,(int)written).ToArray();
            } finally { api.Close(true,handle); }
        }
    }
}
'@
}

function Assert-Interop([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw [Pinget.Interop.VerificationError]::new($Message) }
}
function Assert-RunId([string]$Value) {
    Assert-Interop ($Value -cmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,80}\z') 'run ID must be 1-81 ASCII letters, digits, dot, underscore or hyphen'
}
function Get-Payload($Case) { return ,([Pinget.Interop.Bytes]::Payload($Case.name, $Case.size, $Case.pattern)) }
function Get-Digest([byte[]]$Data) { [Pinget.Interop.Bytes]::Digest($Data) }
function Write-InteropJson([string]$Path, $Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 100) + "`n")
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try { $stream.Write($bytes) } finally { $stream.Dispose() }
}
function Read-InteropJson([string]$Path) {
    try {
        $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
        [Pinget.Interop.Bytes]::CheckJson($text, $Path)
        return ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate
    } catch {
        if ((Get-InteropException $_) -is [Pinget.Interop.VerificationError]) { throw }
        throw [Pinget.Interop.VerificationError]::new("cannot read JSON ${Path}: $_")
    }
}
function Get-InteropException($ErrorRecord) {
    $exception = $ErrorRecord.Exception
    while ($exception.InnerException) { $exception = $exception.InnerException }
    return $exception
}
function Assert-Entries([string]$Directory, [string[]]$Expected) {
    $item = Get-Item -LiteralPath $Directory -Force -ErrorAction SilentlyContinue
    Assert-Interop ($null -ne $item -and $item.PSIsContainer -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) "missing or invalid directory: $Directory"
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    foreach ($entry in $entries) {
        Assert-Interop (-not ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) "fixture symlink is not allowed: $($entry.FullName)"
    }
    $actual = @($entries | ForEach-Object Name)
    $missing = @($Expected | Where-Object { $actual -cnotcontains $_ } | Sort-Object)
    $extra = @($actual | Where-Object { $Expected -cnotcontains $_ } | Sort-Object)
    Assert-Interop ($missing.Count -eq 0 -and $extra.Count -eq 0) "fixture entries differ in $Directory; missing=$($missing -join ', '), extra=$($extra -join ', ')"
}
function Read-Fixture([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    Assert-Interop ($null -ne $item -and -not $item.PSIsContainer -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) "missing or invalid fixture file: $Path"
    return ,([IO.File]::ReadAllBytes($Path))
}
function Test-Keys($Value, [string[]]$Keys) {
    if ($Value -isnot [Collections.IDictionary] -or $Value.Count -ne $Keys.Count) { return $false }
    foreach ($key in $Keys) { if (@($Value.Keys) -cnotcontains $key) { return $false } }
    return $true
}
function Test-Record($Actual, $Expected) {
    if (-not (Test-Keys $Actual @($Expected.Keys))) { return $false }
    foreach ($key in $Expected.Keys) {
        if ($key -ceq 'size' -and $Actual[$key] -isnot [long] -and $Actual[$key] -isnot [int]) { return $false }
        if ($Expected[$key] -is [string] -and $Actual[$key] -isnot [string]) { return $false }
        if ($Actual[$key] -cne $Expected[$key]) { return $false }
    }
    return $true
}
function Initialize-Fixture([string]$Root, [string]$RunId, [object[]]$Cases = $script:Cases) {
    Assert-RunId $RunId
    Assert-Interop (-not (Test-Path -LiteralPath $Root)) "fixture output already exists: $Root"
    $null = [IO.Directory]::CreateDirectory((Join-Path $Root 'inputs'))
    $records = @(foreach ($case in $Cases) {
        $original = Get-Payload $case
        [IO.File]::WriteAllBytes((Join-Path $Root "inputs/$($case.name).bin"), $original)
        @{ name = $case.name; size = $case.size; pattern = $case.pattern; sha256 = Get-Digest $original }
    })
    Write-InteropJson (Join-Path $Root 'run.json') @{ protocol = $script:Protocol; run_id = $RunId; cases = $records }
}
function New-ProducerManifest([string]$Root, [string]$RunId, [string]$Producer, [object[]]$Cases = $script:Cases) {
    $directory = Join-Path $Root $Producer
    Assert-Entries $directory @($Cases | ForEach-Object { "$($_.name).mszip" })
    $records = @(foreach ($case in $Cases) {
        $encoded = Read-Fixture (Join-Path $directory "$($case.name).mszip")
        @{ name = $case.name; size = $encoded.Length; sha256 = Get-Digest $encoded }
    })
    Write-InteropJson (Join-Path $directory 'manifest.json') @{ protocol = $script:Protocol; run_id = $RunId; producer = $Producer; cases = $records }
}
function Get-CompletionRecord([string]$Root, [string]$RunId) {
    return @{
        protocol = $script:Protocol; run_id = $RunId
        run_sha256 = Get-Digest (Read-Fixture (Join-Path $Root 'run.json'))
        rust_sha256 = Get-Digest (Read-Fixture (Join-Path $Root 'rust/manifest.json'))
        dotnet_sha256 = Get-Digest (Read-Fixture (Join-Path $Root 'dotnet/manifest.json'))
    }
}
function Test-Frame([byte[]]$Data, [int]$ExpectedSize) { $null = [Pinget.Interop.Bytes]::CheckFrame($Data, $ExpectedSize, $false) }
function ConvertFrom-PortableMsZip([byte[]]$Data, [int]$ExpectedSize) { return ,([Pinget.Interop.Bytes]::CheckFrame($Data, $ExpectedSize, $true)) }
function Test-FixtureTree(
    [string]$Root, [string]$RunId, [scriptblock]$Decoder,
    [object[]]$Cases = $script:Cases, [bool]$Complete = $true,
    [scriptblock]$FrameCheck = { param($data, $size) Test-Frame $data $size },
    [bool]$VerboseOutput = $true
) {
    Assert-RunId $RunId
    $rootEntries = @('run.json', 'inputs') + $script:Producers
    if ($Complete) { $rootEntries += 'complete.json' }
    Assert-Entries $Root $rootEntries
    Assert-Entries (Join-Path $Root 'inputs') @($Cases | ForEach-Object { "$($_.name).bin" })
    foreach ($producer in $script:Producers) {
        Assert-Entries (Join-Path $Root $producer) (@('manifest.json') + @($Cases | ForEach-Object { "$($_.name).mszip" }))
    }
    if ($Complete) {
        Assert-Interop (Test-Record (Read-InteropJson (Join-Path $Root 'complete.json')) (Get-CompletionRecord $Root $RunId)) 'stale or corrupt completion marker'
    }
    $run = Read-InteropJson (Join-Path $Root 'run.json')
    Assert-Interop (Test-Keys $run @('protocol', 'run_id', 'cases')) 'invalid run manifest'
    Assert-Interop (Test-Record @{ protocol = $run.protocol; run_id = $run.run_id } @{ protocol = $script:Protocol; run_id = $RunId }) 'stale run manifest or run ID'
    $records = $run.cases
    Assert-Interop ($records -is [array] -and $records.Count -eq $Cases.Count) 'missing or extra run cases'
    $manifests = @{}
    foreach ($producer in $script:Producers) {
        $manifest = Read-InteropJson (Join-Path $Root "$producer/manifest.json")
        Assert-Interop (Test-Keys $manifest @('protocol', 'run_id', 'producer', 'cases')) "invalid $producer manifest"
        Assert-Interop (Test-Record @{ protocol = $manifest.protocol; run_id = $manifest.run_id; producer = $manifest.producer } @{ protocol = $script:Protocol; run_id = $RunId; producer = $producer }) "stale $producer manifest"
        Assert-Interop ($manifest.cases -is [array] -and $manifest.cases.Count -eq $Cases.Count) "missing or extra $producer cases"
        $manifests[$producer] = $manifest.cases
    }
    $failures = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $Cases.Count; $index++) {
        $case = $Cases[$index]
        $expected = Get-Payload $case
        $original = Read-Fixture (Join-Path $Root "inputs/$($case.name).bin")
        Assert-Interop ([Pinget.Interop.Bytes]::Equal($original, $expected)) "original payload differs from independently regenerated $($case.name)"
        Assert-Interop (Test-Record $records[$index] @{ name = $case.name; size = $case.size; pattern = $case.pattern; sha256 = Get-Digest $expected }) "stale or corrupt input record for $($case.name)"
        foreach ($producer in $script:Producers) {
            $encoded = Read-Fixture (Join-Path $Root "$producer/$($case.name).mszip")
            Assert-Interop (Test-Record $manifests[$producer][$index] @{ name = $case.name; size = $encoded.Length; sha256 = Get-Digest $encoded }) "stale or corrupt $producer fixture for $($case.name)"
            try {
                $null = & $FrameCheck $encoded $case.size
                [byte[]]$decoded = & $Decoder $encoded $case.size
                Assert-Interop ([Pinget.Interop.Bytes]::Equal($decoded, $expected)) "decoded bytes differ for $producer/$($case.name)"
            } catch {
                $errorValue = Get-InteropException $_
                if ($errorValue -isnot [Pinget.Interop.VerificationError]) { throw }
                $failure = "invalid or undecodable $producer/$($case.name): $($errorValue.Message)"
                $failures.Add($failure)
                if ($VerboseOutput) { [Console]::Error.WriteLine("FAILED $failure") }
                continue
            }
            if ($VerboseOutput) { Write-Host "verified $producer/$($case.name)" }
        }
    }
    Assert-Interop ($failures.Count -eq 0) "$($failures.Count) fixture(s) failed: $($failures -join '; ')"
}
function Test-NativePositiveControl($Native, [string]$Root) {
    [byte[]]$original = @(167)
    [byte[]]$reference = $Native.CompressReference($original)
    Assert-Interop ([Pinget.Interop.Bytes]::Equal($Native.Decompress($reference, 1), $original)) 'native nonempty control failed its round trip'
    Write-Host "Native single-byte reference: $([Convert]::ToHexString($reference).ToLowerInvariant())"
    foreach ($producer in $script:Producers) {
        $encoded = Read-Fixture (Join-Path $Root "$producer/single_byte.mszip")
        Write-Host "Portable $producer single-byte fixture: $([Convert]::ToHexString($encoded).ToLowerInvariant())"
    }
    return ,$reference
}
function Assert-NativeMalformedReference($Native, [byte[]]$Valid, [int]$Size) {
    Assert-Interop ($Valid.Length -ge 24) 'native control frame is truncated'
    [byte[]]$damaged = $Valid.Clone()
    $damaged[0] = $damaged[0] -bxor 255
    foreach ($control in @(@{ name = 'truncated_header'; data = [byte[]]$Valid[0..22] }, @{ name = 'invalid_signature'; data = $damaged })) {
        try { $null = $Native.Decompress($control.data, $Size) } catch {
            if ((Get-InteropException $_) -is [Pinget.Interop.NativeDecodeError]) { continue }
            throw
        }
        throw [Pinget.Interop.VerificationError]::new("native decoder accepted malformed control: $($control.name)")
    }
}
function Test-NativeEmptyRejection($Native) {
    try { $null = $Native.CompressReference([byte[]]::new(0)) } catch {
        $errorValue = Get-InteropException $_
        if ($errorValue -isnot [Pinget.Interop.NativeCompressionError]) { throw }
        Assert-Interop ($errorValue.ErrorCode -eq 87) "unexpected native empty-input failure: $errorValue"
        Write-Host 'Native Compress rejected empty input with Win32 error 87.'
        return
    }
    throw [Pinget.Interop.VerificationError]::new('native Compress unexpectedly accepted empty input')
}
function Get-NativeZeroInputProbe($Native) {
    try { [byte[]]$result = $Native.Decompress([byte[]]::new(0), 0) } catch {
        $errorValue = Get-InteropException $_
        if ($errorValue -is [Pinget.Interop.NativeDecodeError]) { return "rejected ($errorValue)" }
        throw
    }
    Assert-Interop ($result.Length -eq 0) 'native zero-input probe returned nonempty data'
    return 'accepted'
}
function Test-RustResults([string]$Text) {
    $found = @([regex]::Matches($Text, '(?m)^test ([A-Za-z0-9_:]+) \.\.\. ok\r?$') | ForEach-Object { $_.Groups[1].Value })
    Assert-Interop ($found.Count -eq $script:RustTests.Count -and @($script:RustTests | Where-Object { $found -cnotcontains $_ }).Count -eq 0) "unexpected Rust test discovery/results: $($found -join ', ')"
    Assert-Interop ($Text -cmatch '(?m)^test result: ok\. 3 passed; 0 failed; 0 ignored;') 'Rust test summary did not report all passing, non-ignored tests'
}
function Test-DotnetReport([string]$Path) {
    try {
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $reader = [Xml.XmlReader]::Create($Path, $settings)
        try { $document = [Xml.XmlDocument]::new(); $document.Load($reader) } finally { $reader.Dispose() }
    } catch { throw [Pinget.Interop.VerificationError]::new("missing or invalid .NET TRX report ${Path}: $_") }
    $counters = $document.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
    Assert-Interop ($null -ne $counters) 'TRX report has no counters'
    foreach ($entry in @{ total = '3'; executed = '3'; passed = '3'; failed = '0'; notExecuted = '0' }.GetEnumerator()) {
        Assert-Interop ($counters.GetAttribute($entry.Key) -ceq $entry.Value) "unexpected TRX $($entry.Key) counter: $($counters.GetAttribute($entry.Key))"
    }
    $results = @($document.SelectNodes("//*[local-name()='UnitTestResult']"))
    $names = @($results | ForEach-Object { $_.GetAttribute('testName') })
    Assert-Interop ($results.Count -eq 3 -and @($script:DotnetTests | Where-Object { $names -cnotcontains $_ }).Count -eq 0) "unexpected .NET test discovery: $($names -join ', ')"
    Assert-Interop (@($results | Where-Object { $_.GetAttribute('outcome') -cne 'Passed' }).Count -eq 0) 'TRX contains a non-passing test'
}
function Invoke-InteropCommand([string[]]$Command, [Collections.IDictionary]$Environment) {
    Write-Host "+ $($Command -join ' ')"
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Command[0]
    $info.WorkingDirectory = $script:Repo
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Command[1..($Command.Count - 1)]) { $info.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = [string]$Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        Write-Host $text -NoNewline
        Assert-Interop ($process.ExitCode -eq 0) "command exited $($process.ExitCode): $($Command[0])"
        return $text
    } finally { $process.Dispose() }
}
function Get-InteropPlatform {
    if ($IsWindows) { return 'windows' }
    if ($IsMacOS) { return 'macos' }
    if ($IsLinux) { return 'linux' }
    return 'other'
}
function New-WindowsNativeMsZip {
    Assert-Interop ((Get-InteropPlatform) -eq 'windows') 'Windows native validation requires Windows'
    return [Pinget.Interop.WindowsNativeMsZip]::new()
}
function Invoke-Produce([string]$Root, [string]$Reports, [string]$RunId, [switch]$DotnetNoRestore) {
    Assert-Interop ((Get-InteropPlatform) -in @('linux', 'macos')) 'fixture producers require Linux or macOS'
    Assert-RunId $RunId
    $Root = [IO.Path]::GetFullPath($Root)
    $Reports = [IO.Path]::GetFullPath($Reports)
    Assert-Interop (-not (Test-Path -LiteralPath $Root)) "fixture output already exists; choose a fresh path: $Root"
    Assert-Interop (-not (Test-Path -LiteralPath $Reports)) "report output already exists; choose a fresh path: $Reports"
    $separator = [IO.Path]::DirectorySeparatorChar
    Assert-Interop (-not $Root.StartsWith($Reports.TrimEnd($separator) + $separator, [StringComparison]::Ordinal) -and
        -not $Reports.StartsWith($Root.TrimEnd($separator) + $separator, [StringComparison]::Ordinal) -and $Root -cne $Reports) 'fixture and report directories must be separate'
    $null = [IO.Directory]::CreateDirectory($Reports)
    Initialize-Fixture $Root $RunId
    $environment = @{
        PINGET_MSZIP_FIXTURE_ROOT = $Root; PINGET_MSZIP_RUN_ID = $RunId
        CARGO_TARGET_DIR = Join-Path $script:Repo 'rust/target'
        CARGO_PROFILE_DEV_DEBUG = '0'; CARGO_PROFILE_TEST_DEBUG = '0'; CARGO_INCREMENTAL = '0'
        VSTEST_ARTIFACTS_DIRECTORY = $Reports
    }
    $rustOutput = Invoke-InteropCommand @('cargo', 'test', '--manifest-path', 'rust/Cargo.toml', '-p', 'pinget-publisher', '--lib', 'wave21_tests::mszip_interop_tests', '--', '--test-threads=1') $environment
    Test-RustResults $rustOutput
    $dotnetCommand = @(
        'dotnet', 'test', 'dotnet/src/Devolutions.Pinget.Publisher.Tests/Devolutions.Pinget.Publisher.Tests.csproj',
        '-c', 'Release', '--filter', 'FullyQualifiedName~PortableMsZipInteropTests',
        '--logger', 'trx;LogFileName=mszip-portable.trx', '--results-directory', $Reports
    )
    if ($DotnetNoRestore) { $dotnetCommand += '--no-restore' }
    $null = Invoke-InteropCommand $dotnetCommand $environment
    Test-DotnetReport (Join-Path $Reports 'mszip-portable.trx')
    foreach ($producer in $script:Producers) { New-ProducerManifest $Root $RunId $producer }
    Test-FixtureTree $Root $RunId { param($data, $size) ConvertFrom-PortableMsZip $data $size } -Complete $false
    Write-InteropJson (Join-Path $Root 'complete.json') (Get-CompletionRecord $Root $RunId)
    Write-Host "Portable fixture production and smoke checks passed: $Root"
    Write-Host 'Windows native validation has NOT run.'
}
function Invoke-ValidateWindows([string]$Root, [string]$RunId) {
    $native = New-WindowsNativeMsZip
    $reference = Test-NativePositiveControl $native $Root
    Assert-NativeMalformedReference $native $reference 1
    Test-NativeEmptyRejection $native
    Write-Host "Native zero-compressed-byte probe: $(Get-NativeZeroInputProbe $native)."
    Test-FixtureTree $Root $RunId { param($data, $size) return ,($native.Decompress($data, $size)) }
    Write-Host 'All portable fixtures passed the Windows native MSZIP decoder.'
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Assert-Interop (-not [string]::IsNullOrEmpty($Mode)) 'Mode is required'
        Assert-RunId $RunId
        if ($Mode -eq 'produce') {
            Assert-Interop (-not [string]::IsNullOrEmpty($Output) -and -not [string]::IsNullOrEmpty($Reports)) 'produce requires Output and Reports'
            Invoke-Produce $Output $Reports $RunId -DotnetNoRestore:$DotnetNoRestore
        } else {
            Assert-Interop (-not [string]::IsNullOrEmpty($InputDirectory)) 'validate-windows requires InputDirectory'
            Invoke-ValidateWindows $InputDirectory $RunId
        }
        exit 0
    } catch {
        [Console]::Error.WriteLine("MSZIP interoperability validation failed: $(Get-InteropException $_)")
        exit 1
    }
}
