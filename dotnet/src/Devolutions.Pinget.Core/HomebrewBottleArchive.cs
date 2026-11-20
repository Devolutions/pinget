using System.Formats.Tar;
using System.IO.Compression;

namespace Devolutions.Pinget.Core;

internal static class HomebrewBottleArchive
{
    internal static IReadOnlyList<string> Extract(string archivePath, string destination, string formula, string version)
    {
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException($"Bottle extraction destination already exists: {root}");
        var expected = $"{formula}/{version}/";
        var files = new List<string>();
        var links = new List<(string Path, string Target, bool Hard)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        long expandedSize = 0;
        Directory.CreateDirectory(root);
        using var input = File.OpenRead(archivePath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = entry.Name;
            if (name.EndsWith('/'))
                name = name[..^1];
            if (name == $"{formula}/{version}" || name == formula)
            {
                if (entry.EntryType != TarEntryType.Directory)
                    throw new InvalidDataException($"Invalid bottle root entry: {entry.Name}");
                continue;
            }
            if (!name.StartsWith(expected, StringComparison.Ordinal))
                throw new InvalidDataException($"Bottle entry is outside {expected}: {entry.Name}");
            var relative = name[expected.Length..];
            var path = ChildPath(root, relative);
            if (!names.Add(relative) || names.Count > 100_000)
                throw new InvalidDataException($"Duplicate or excessive bottle entry: {entry.Name}");
            for (var separator = relative.IndexOf('/'); separator >= 0; separator = relative.IndexOf('/', separator + 1))
                parents.Add(relative[..separator]);
            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Bottle exceeds the 2 GiB expanded-size limit.");
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                        entry.DataStream?.CopyTo(output);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(path, entry.Mode & (UnixFileMode)0x1FF);
                    files.Add(path);
                    break;
                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    links.Add((path, entry.LinkName, entry.EntryType == TarEntryType.HardLink));
                    break;
                default:
                    throw new InvalidDataException($"Unsupported bottle entry type: {entry.EntryType}");
            }
        }
        foreach (var link in links)
        {
            if (parents.Contains(Path.GetRelativePath(root, link.Path).Replace(Path.DirectorySeparatorChar, '/')))
                throw new InvalidDataException($"Bottle places an entry below a symbolic link: {link.Path}");
            if (Path.IsPathRooted(link.Target) || link.Target.Contains('\\'))
                throw new InvalidDataException($"Unsafe bottle link target: {link.Target}");
            var target = link.Hard
                ? link.Target.StartsWith(expected, StringComparison.Ordinal)
                    ? ChildPath(root, link.Target[expected.Length..])
                    : throw new InvalidDataException($"Hard link is outside the keg: {link.Target}")
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.Path)!, link.Target));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Bottle link escapes its keg: {link.Target}");
            for (var current = target; current != root; current = Path.GetDirectoryName(current)!)
            {
                if (new FileInfo(current).LinkTarget is not null)
                    throw new InvalidDataException($"Bottle link target traverses another link: {link.Target}");
            }
            if (!File.Exists(target) && !Directory.Exists(target))
                throw new InvalidDataException($"Bottle link target does not exist: {link.Target}");
            Directory.CreateDirectory(Path.GetDirectoryName(link.Path)!);
            if (link.Hard)
            {
                if (!File.Exists(target) || new FileInfo(target).LinkTarget is not null)
                    throw new InvalidDataException($"Bottle hard link has no regular-file target: {link.Target}");
                File.Copy(target, link.Path, overwrite: false);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(link.Path, File.GetUnixFileMode(target));
                files.Add(link.Path);
            }
            else
            {
                File.CreateSymbolicLink(link.Path, link.Target);
            }
        }
        if (files.Count == 0)
            throw new InvalidDataException("Bottle contains no files.");
        return files;
    }

    internal static string ChildPath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') ||
            relative.Split('/').Any(part => part is "" or "." or "..") || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Unsafe bottle path: {relative}");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Bottle path escapes its keg: {relative}");
        return path;
    }
}
