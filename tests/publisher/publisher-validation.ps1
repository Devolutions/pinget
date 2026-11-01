Set-StrictMode -Version Latest

function Initialize-PublisherValidation {
    param(
        [Parameter(Mandatory = $true)][string] $SqliteLibraryPath,
        [string] $YamlDotNetPath
    )

    foreach ($path in @($SqliteLibraryPath, $YamlDotNetPath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required validation dependency does not exist: $path"
        }
    }

    if (-not ('PingetPublisher.Validation.NativeSqlite' -as [type])) {
        $nativeSqlite = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PingetPublisher.Validation
{
    public static class NativeSqlite
    {
        private const int Ok = 0;
        private const int Row = 100;
        private const int Done = 101;
        private const int OpenReadOnly = 0x00000001;
        private const int OpenReadWrite = 0x00000002;
        private const int OpenCreate = 0x00000004;
        private static bool resolverInstalled;

        public static void Initialize(string libraryPath)
        {
            if (!resolverInstalled)
            {
                var fullPath = System.IO.Path.GetFullPath(libraryPath);
                NativeLibrary.SetDllImportResolver(
                    typeof(NativeSqlite).Assembly,
                    (name, assembly, searchPath) =>
                        name == "pinget_validation_sqlite"
                            ? NativeLibrary.Load(fullPath)
                            : IntPtr.Zero);
                resolverInstalled = true;
            }
        }

        public static void Execute(string path, string sql)
        {
            IntPtr database = Open(path, OpenReadWrite | OpenCreate);
            IntPtr error = IntPtr.Zero;
            try
            {
                int result = sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out error);
                if (result != Ok)
                {
                    string message = error == IntPtr.Zero
                        ? Utf8(sqlite3_errmsg(database))
                        : Utf8(error);
                    throw new InvalidOperationException("SQLite execute failed (" + result + "): " + message);
                }
            }
            finally
            {
                if (error != IntPtr.Zero) sqlite3_free(error);
                sqlite3_close(database);
            }
        }

        public static List<Dictionary<string, object>> Query(string path, string sql)
        {
            IntPtr database = Open(path, OpenReadOnly);
            IntPtr statement = IntPtr.Zero;
            try
            {
                int result = sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero);
                Check(database, result, "prepare query");
                var rows = new List<Dictionary<string, object>>();
                while (true)
                {
                    result = sqlite3_step(statement);
                    if (result == Done) break;
                    if (result != Row) Check(database, result, "execute query");

                    int count = sqlite3_column_count(statement);
                    var row = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (int column = 0; column < count; column++)
                    {
                        string name = Utf8(sqlite3_column_name(statement, column));
                        int type = sqlite3_column_type(statement, column);
                        object value;
                        switch (type)
                        {
                            case 1: value = sqlite3_column_int64(statement, column); break;
                            case 2: value = sqlite3_column_double(statement, column); break;
                            case 3: value = Utf8(sqlite3_column_text(statement, column)); break;
                            case 4:
                                int length = sqlite3_column_bytes(statement, column);
                                var bytes = new byte[length];
                                if (length != 0) Marshal.Copy(sqlite3_column_blob(statement, column), bytes, 0, length);
                                value = bytes;
                                break;
                            case 5: value = null; break;
                            default: throw new InvalidOperationException("Unknown SQLite column type " + type);
                        }
                        row.Add(name, value);
                    }
                    rows.Add(row);
                }
                return rows;
            }
            finally
            {
                if (statement != IntPtr.Zero) sqlite3_finalize(statement);
                sqlite3_close(database);
            }
        }

        private static IntPtr Open(string path, int flags)
        {
            int result = sqlite3_open_v2(path, out IntPtr database, flags, IntPtr.Zero);
            if (result != Ok)
            {
                string message = database == IntPtr.Zero ? "no database handle" : Utf8(sqlite3_errmsg(database));
                if (database != IntPtr.Zero) sqlite3_close(database);
                throw new InvalidOperationException("SQLite open failed (" + result + "): " + message);
            }
            return database;
        }

        private static void Check(IntPtr database, int result, string operation)
        {
            if (result != Ok)
                throw new InvalidOperationException("SQLite " + operation + " failed (" + result + "): " + Utf8(sqlite3_errmsg(database)));
        }

        private static string Utf8(IntPtr value)
        {
            return value == IntPtr.Zero ? String.Empty : Marshal.PtrToStringUTF8(value);
        }

        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            out IntPtr database, int flags, IntPtr vfs);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(
            IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            IntPtr callback, IntPtr argument, out IntPtr error);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr value);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr database);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(
            IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            int byteCount, out IntPtr statement, IntPtr tail);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr statement, int column);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
        [DllImport("pinget_validation_sqlite", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    }

    public static class NativeMsZip
    {
        private const uint AlgorithmMsZip = 2;
        private const int ErrorInsufficientBuffer = 122;

        public static byte[] Compress(byte[] input) => Transform(input, true);
        public static byte[] Decompress(byte[] input) => Transform(input, false);

        private static byte[] Transform(byte[] input, bool compress)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            IntPtr handle;
            bool created = compress
                ? CreateCompressor(AlgorithmMsZip, IntPtr.Zero, out handle)
                : CreateDecompressor(AlgorithmMsZip, IntPtr.Zero, out handle);
            if (!created) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                UIntPtr required;
                bool sized = compress
                    ? Compress(handle, input, (UIntPtr)input.Length, null, UIntPtr.Zero, out required)
                    : Decompress(handle, input, (UIntPtr)input.Length, null, UIntPtr.Zero, out required);
                int error = Marshal.GetLastWin32Error();
                if (sized || error != ErrorInsufficientBuffer)
                    throw new InvalidOperationException("Windows Compression API did not report the required buffer size (error " + error + ").");
                ulong size = required.ToUInt64();
                if (size > Int32.MaxValue || (!compress && size > 64UL * 1024 * 1024))
                    throw new InvalidOperationException("Native MSZIP output exceeds the supported validation limit.");
                var output = new byte[(int)size];
                UIntPtr written;
                bool succeeded = compress
                    ? Compress(handle, input, (UIntPtr)input.Length, output, (UIntPtr)output.Length, out written)
                    : Decompress(handle, input, (UIntPtr)input.Length, output, (UIntPtr)output.Length, out written);
                if (!succeeded) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Array.Resize(ref output, checked((int)written.ToUInt64()));
                return output;
            }
            finally
            {
                if (compress) CloseCompressor(handle);
                else CloseDecompressor(handle);
            }
        }

        [DllImport("cabinet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateCompressor(uint algorithm, IntPtr options, out IntPtr compressor);
        [DllImport("cabinet.dll", SetLastError = true, EntryPoint = "Compress")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Compress(IntPtr compressor, byte[] input, UIntPtr inputSize, byte[] output, UIntPtr outputSize, out UIntPtr written);
        [DllImport("cabinet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseCompressor(IntPtr compressor);
        [DllImport("cabinet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDecompressor(uint algorithm, IntPtr options, out IntPtr decompressor);
        [DllImport("cabinet.dll", SetLastError = true, EntryPoint = "Decompress")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Decompress(IntPtr decompressor, byte[] input, UIntPtr inputSize, byte[] output, UIntPtr outputSize, out UIntPtr written);
        [DllImport("cabinet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDecompressor(IntPtr decompressor);
    }
}
'@
        Add-Type -TypeDefinition $nativeSqlite -Language CSharp -ErrorAction Stop
    }

    if (-not ('PingetPublisher.Validation.NativeMsZip' -as [type])) {
        throw 'Native MSZIP validation helper did not load.'
    }
    [PingetPublisher.Validation.NativeSqlite]::Initialize($SqliteLibraryPath)

    if (-not [string]::IsNullOrWhiteSpace($YamlDotNetPath) -and
        -not ('YamlDotNet.RepresentationModel.YamlStream' -as [type])) {
        Add-Type -Path $YamlDotNetPath -ErrorAction Stop
    }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]] $Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([Convert]::ToHexString($sha.ComputeHash($Bytes))).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory = $true)][string] $Path)
    return Get-Sha256Hex ([System.IO.File]::ReadAllBytes($Path))
}

function Test-ByteArrayEqual {
    param([byte[]] $Left, [byte[]] $Right)
    if ($null -eq $Left -or $null -eq $Right -or $Left.Length -ne $Right.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { return $false }
    }
    return $true
}

function ConvertFrom-YamlNode {
    param([Parameter(Mandatory = $true)][YamlDotNet.RepresentationModel.YamlNode] $Node)

    if ($Node -is [YamlDotNet.RepresentationModel.YamlScalarNode]) {
        $scalar = [YamlDotNet.RepresentationModel.YamlScalarNode]$Node
        if ($scalar.Tag -eq 'tag:yaml.org,2002:null' -or
            ($scalar.Style -eq [YamlDotNet.Core.ScalarStyle]::Plain -and
                ($scalar.Value -eq '' -or $scalar.Value -eq '~' -or $scalar.Value -eq 'null' -or $scalar.Value -eq 'Null' -or $scalar.Value -eq 'NULL'))) {
            return $null
        }
        return [string]$scalar.Value
    }
    if ($Node -is [YamlDotNet.RepresentationModel.YamlSequenceNode]) {
        $items = [System.Collections.Generic.List[object]]::new()
        foreach ($child in ([YamlDotNet.RepresentationModel.YamlSequenceNode]$Node).Children) {
            $items.Add((ConvertFrom-YamlNode $child))
        }
        return ,$items.ToArray()
    }
    if ($Node -is [YamlDotNet.RepresentationModel.YamlMappingNode]) {
        $map = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        foreach ($entry in ([YamlDotNet.RepresentationModel.YamlMappingNode]$Node).Children.GetEnumerator()) {
            if ($entry.Key -isnot [YamlDotNet.RepresentationModel.YamlScalarNode]) {
                throw 'Independent YAML validation does not permit non-scalar mapping keys.'
            }
            $key = [string]([YamlDotNet.RepresentationModel.YamlScalarNode]$entry.Key).Value
            if ($map.ContainsKey($key)) { throw "Duplicate YAML mapping key: $key" }
            $map.Add($key, (ConvertFrom-YamlNode $entry.Value))
        }
        return $map
    }
    throw "Unsupported YAML node type: $($Node.GetType().FullName)"
}

function ConvertFrom-YamlDocuments {
    param([Parameter(Mandatory = $true)][string] $Text)
    $stream = [YamlDotNet.RepresentationModel.YamlStream]::new()
    $reader = [System.IO.StringReader]::new($Text)
    try {
        $stream.Load($reader)
        $documents = [System.Collections.Generic.List[object]]::new()
        foreach ($document in $stream.Documents) {
            $documents.Add((ConvertFrom-YamlNode $document.RootNode))
        }
        return ,$documents.ToArray()
    }
    finally {
        $reader.Dispose()
    }
}

function ConvertTo-CanonicalValue {
    param([AllowNull()][object] $Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $map = [System.Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
        foreach ($key in $Value.Keys) {
            $map.Add([string]$key, (ConvertTo-CanonicalValue $Value[$key]))
        }
        return $map
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string] -and $Value -isnot [byte[]]) {
        $items = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Value) { $items.Add((ConvertTo-CanonicalValue $item)) }
        return ,$items.ToArray()
    }
    return $Value
}

function ConvertTo-CanonicalJson {
    param([AllowNull()][object] $Value)
    return ConvertTo-Json -InputObject (ConvertTo-CanonicalValue $Value) -Depth 100 -Compress
}

function Get-ContainedFilePath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )
    if ([System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Artifact path is not a safe relative path: $RelativePath"
    }
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Artifact root is not an existing directory: $Root"
    }
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $volumeRoot = [System.IO.Path]::GetPathRoot($rootPath)
    $rootTail = $rootPath.Substring($volumeRoot.Length)
    $rootCursor = $volumeRoot
    foreach ($rootPart in $rootTail.Split([System.IO.Path]::DirectorySeparatorChar, [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $rootCursor = Join-Path $rootCursor $rootPart
        $rootAttributes = [System.IO.File]::GetAttributes($rootCursor)
        if (($rootAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Artifact root traverses a reparse point: $rootCursor"
        }
    }
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
    $prefix = $rootPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact path escapes its snapshot root: $RelativePath"
    }
    $cursor = $rootPath
    $relativeTail = $candidate.Substring($prefix.Length)
    $parts = $relativeTail.Split([System.IO.Path]::DirectorySeparatorChar, [System.StringSplitOptions]::RemoveEmptyEntries)
    foreach ($part in $parts) {
        $cursor = Join-Path $cursor $part
        if (-not (Test-Path -LiteralPath $cursor)) { throw "Artifact file does not exist: $RelativePath" }
        $attributes = [System.IO.File]::GetAttributes($cursor)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Artifact path traverses a reparse point: $RelativePath"
        }
    }
    if (([System.IO.File]::GetAttributes($candidate) -band [System.IO.FileAttributes]::Directory) -ne 0) {
        throw "Artifact path is not a file: $RelativePath"
    }
    return $candidate
}

function Get-PackageSidecar {
    param([string] $FeedPath, [string] $PackageId, [byte[]] $HashBytes)
    if ($HashBytes.Length -ne 32) { throw "Package $PackageId sidecar hash is not 32 bytes." }
    $hash = ([Convert]::ToHexString($HashBytes)).ToLowerInvariant()
    $relative = "packages/$PackageId/$($hash.Substring(0, 8))/versionData.mszyml"
    return [pscustomobject]@{
        Hash = $hash
        RelativePath = $relative
        Path = Get-ContainedFilePath -Root $FeedPath -RelativePath $relative
    }
}

function Get-SqliteTableNames {
    param([string] $DatabasePath)
    $rows = [PingetPublisher.Validation.NativeSqlite]::Query(
        $DatabasePath,
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
    )
    return @($rows | ForEach-Object { [string]$_.name })
}

function Assert-Equal {
    param([object] $Actual, [object] $Expected, [string] $Message)
    $actualJson = ConvertTo-CanonicalJson $Actual
    $expectedJson = ConvertTo-CanonicalJson $Expected
    if ($actualJson -cne $expectedJson) {
        throw "$Message`nExpected: $expectedJson`nActual:   $actualJson"
    }
}
