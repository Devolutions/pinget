using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Devolutions.Pinget.Core;

internal static partial class AgentSkillInstaller
{
    private static InstallResult InstallGit(AgentSkillManifest manifest, InstallRequest request,
        string projectRoot, string home, string? catalogSource = null, string? catalogManifestSha256 = null)
    {
        var folder = Path.Combine(projectRoot, ".pinget-git-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RunGit(folder, 4096, "init", "-q");
            RunGit(folder, 4096, "fetch", "--quiet", "--no-tags", "--depth=1",
                manifest.Source!.Url!.AbsoluteUri, manifest.Source.Commit!);
            return InstallGitTree(manifest, request, folder, projectRoot, home, catalogSource, catalogManifestSha256);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
    }

    // The local repository argument exists solely to exercise the same pinned-tree reader offline.
    internal static InstallResult InstallGitTree(AgentSkillManifest manifest, InstallRequest request,
        string repository, string projectRoot, string home,
        string? catalogSource = null, string? catalogManifestSha256 = null)
    {
        ValidateRequest(request);
        if (manifest.Source?.Type != "git")
            throw new InvalidOperationException("Expected a pinned Git source.");
        var commit = manifest.Source.Commit!;
        var resolved = Encoding.ASCII.GetString(RunGit(repository, 128,
            "rev-parse", "--verify", commit + "^{commit}")).Trim();
        if (!string.Equals(resolved, commit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Git source did not resolve to the exact pinned commit.");

        var skillDirectory = manifest.PluginPath is null ? manifest.SkillPath :
            JoinPath(manifest.PluginPath, manifest.SkillPath);
        var skillPrefix = string.IsNullOrEmpty(skillDirectory) ? "" : skillDirectory + "/";
        var pluginManifest = manifest.PluginPath is null ? null : JoinPath(manifest.PluginPath, "plugin.json");
        var pathspecs = skillPrefix.Length == 0 ? Array.Empty<string>() :
            pluginManifest is null ? new[] { skillDirectory! } : new[] { skillDirectory!, pluginManifest };
        var tree = RunGit(repository, 1024 * 1024, pathspecs.Length == 0
            ? ["ls-tree", "-r", "-z", "--full-tree", commit]
            : ["ls-tree", "-r", "-z", "--full-tree", commit, "--", .. pathspecs]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = 0;
        while (start < tree.Length)
        {
            var end = Array.IndexOf(tree, (byte)0, start);
            if (end < 0)
                throw new InvalidOperationException("Malformed Git tree.");
            var line = StrictUtf8.GetString(tree, start, end - start);
            var tab = line.IndexOf('\t');
            if (tab < 0)
                throw new InvalidOperationException("Malformed Git tree.");
            var path = line[(tab + 1)..];
            if (!path.StartsWith(skillPrefix, StringComparison.Ordinal) &&
                !string.Equals(path, pluginManifest, StringComparison.Ordinal))
            {
                start = end + 1;
                continue;
            }
            var metadata = line[..tab].Split(' ');
            if (metadata.Length != 3 || metadata[1] != "blob" || metadata[0] != "100644" && metadata[0] != "100755" ||
                metadata[2].Length != 40 || !metadata[2].All(Uri.IsHexDigit))
                throw new InvalidOperationException("Git tree contains a link, submodule, or unsupported entry.");
            path = string.Join('/', SplitSafePath(path));
            if (!seen.Add(path) || seen.Count > MaxEntries)
                throw new InvalidOperationException("Git tree contains duplicate paths or too many entries.");
            paths.Add(path, metadata[2]);
            start = end + 1;
        }
        ValidateFileParents(paths.Keys, seen);
        if (!paths.ContainsKey(skillPrefix + "SKILL.md"))
            throw new InvalidOperationException("Pinned Git tree has no selected root SKILL.md; specify SkillPath.");
        var selected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var (path, value) in paths)
        {
            if (!path.StartsWith(skillPrefix, StringComparison.Ordinal) &&
                path != pluginManifest)
                continue;
            var data = RunGit(repository, MaxEntryBytes, "cat-file", "blob", value);
            if (total > MaxExtractedBytes - data.Length)
                throw new InvalidOperationException("Selected Git skill exceeds the 25 MiB extraction limit.");
            total += data.Length;
            selected.Add(path, data);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var (path, bytes) in selected.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var pathBytes = StrictUtf8.GetBytes(path);
            BinaryPrimitives.WriteInt64LittleEndian(length, pathBytes.Length);
            hash.AppendData(length);
            hash.AppendData(pathBytes);
            BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return InstallEntries(manifest, request, selected, projectRoot, home,
            sourceRevision: resolved.ToLowerInvariant(), artifactSha256: Convert.ToHexString(hash.GetHashAndReset()),
            catalogSource: catalogSource, catalogManifestSha256: catalogManifestSha256);
    }

    private static byte[] RunGit(string repository, long limit, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
            {
                "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
                "-c", "http.followRedirects=false", "-c", "core.hooksPath=NUL",
                "-c", "maintenance.auto=false", "-c", "gc.auto=0",
            }.Concat(arguments))
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(repository, ".pinget-no-global-config");
        start.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var stdout = BoundedProcessOutput(process.StandardOutput.BaseStream, limit, timeout.Token);
            var stderr = BoundedProcessOutput(process.StandardError.BaseStream, 65536, timeout.Token);
            Task.WaitAll([stdout, stderr, process.WaitForExitAsync(timeout.Token)]);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Git could not verify or read the pinned commit.");
            return stdout.Result;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<byte[]> BoundedProcessOutput(Stream stream, long limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > limit)
                throw new InvalidOperationException("Git output exceeds the safety limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
