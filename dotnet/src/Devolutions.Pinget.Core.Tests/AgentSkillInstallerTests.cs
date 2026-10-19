using System.IO.Compression;
using System.Formats.Tar;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class AgentSkillInstallerTests
{
    private const string SkillText = "---\nname: sample-skill\ndescription: Sample skill\n---\nInstructions.\n";

    private static string ManifestYaml(string sha, string path = "\n  SkillPath: sample-skill") => $$"""
        ManifestType: pinget.agentSkill
        PingetManifestVersion: 1
        PackageIdentifier: Example.SampleSkill
        PackageVersion: 1.0.0
        PackageName: sample-skill
        Publisher: Example
        AgentSkill:
          ArtifactUrl: https://example.org/skill.zip
          ArtifactSha256: {{sha}}
        {{path}}
        """;

    private static byte[] Zip(params (string Name, string Contents, int? Attributes)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents, attributes) in files)
            {
                var entry = zip.CreateEntry(name);
                if (attributes is int mode) entry.ExternalAttributes = mode;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(contents);
            }
        }
        return stream.ToArray();
    }

    private static byte[] ZipWithLargeEntries(params (string Name, int Size)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var buffer = new byte[81920];
            foreach (var (name, size) in files)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                for (var remaining = size; remaining > 0;)
                {
                    var length = Math.Min(buffer.Length, remaining);
                    entry.Write(buffer, 0, length);
                    remaining -= length;
                }
            }
        }
        return stream.ToArray();
    }

    private static AgentSkillManifest Manifest(byte[] bytes, string path = "\n  SkillPath: sample-skill") =>
        Assert.IsType<AgentSkillManifest>(AgentSkillInstaller.TryParse(
            Encoding.UTF8.GetBytes(ManifestYaml(Convert.ToHexString(SHA256.HashData(bytes)), path))));

    private static AgentSkillManifest SourceManifest(string source, string suffix = "") =>
        Assert.IsType<AgentSkillManifest>(AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes($$"""
            ManifestType: pinget.agentSkill
            PingetManifestVersion: 1
            PackageIdentifier: Example.SampleSkill
            PackageVersion: 1.0.0
            PackageName: sample-skill
            AgentSkill:
              Source:
            {{source}}
            {{suffix}}
            """)));

    private static byte[] Tar(params (string Path, string Text, TarEntryType Kind)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
            foreach (var (path, text, kind) in entries)
            {
                var entry = new PaxTarEntry(kind, path);
                if (kind == TarEntryType.RegularFile)
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                if (kind == TarEntryType.SymbolicLink)
                    entry.LinkName = text;
                writer.WriteEntry(entry);
            }
        return output.ToArray();
    }

    private sealed class StaticHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Response(byte[] data) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static string Git(string repository, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repository);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000) && process.ExitCode == 0, error);
        return output.Trim();
    }

    private static InstallRequest Request(string? agent = "github-copilot", string? scope = null) => new()
    {
        Query = new PackageQuery { InstallScope = scope },
        Agent = agent,
        ManifestPath = "skill.yaml",
    };

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.CurrentDirectory, ".pinget-skill-test-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "project");
        public string Home => Path.Combine(Root, "home");

        public Workspace()
        {
            Directory.CreateDirectory(Project);
            Directory.CreateDirectory(Home);
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void Parse_AgentSkillContract_ReadsSingleDocument()
    {
        var manifest = Manifest(Zip(("sample-skill/SKILL.md", SkillText, null)));
        Assert.Equal("Example.SampleSkill", manifest.Id);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("sample-skill", manifest.Name);
        Assert.Equal("Example", manifest.Publisher);
        Assert.Equal("sample-skill", manifest.SkillPath);
        Assert.Equal("https://example.org/skill.zip", manifest.ArtifactUrl.AbsoluteUri);
        Assert.Equal(64, manifest.ArtifactSha256.Length);
    }

    [Theory]
    [InlineData("PingetManifestVersion: 2", "Unsupported PingetManifestVersion")]
    [InlineData("PackageIdentifier: ", "PackageIdentifier")]
    [InlineData("PackageName: Bad_Name", "valid skill slug")]
    [InlineData("PackageName: sample--skill", "valid skill slug")]
    [InlineData("PackageName: \" sample-skill \"", "valid skill slug")]
    [InlineData("ArtifactUrl: http://example.org/skill.zip", "HTTPS")]
    [InlineData("ArtifactSha256: 00", "64 hexadecimal")]
    [InlineData("SkillPath: ../outside", "unsafe")]
    [InlineData("SkillPath: C:/outside", "unsafe")]
    public void Parse_InvalidContract_Rejects(string replacement, string message)
    {
        var yaml = ManifestYaml(new string('a', 64));
        var key = replacement.Split(':')[0];
        var lines = yaml.Split('\n').Select(line =>
            line.TrimStart().StartsWith(key + ":", StringComparison.Ordinal)
                ? line[..(line.Length - line.TrimStart().Length)] + replacement : line);
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Parse_PackageNameLength_EnforcesSlugBoundary()
    {
        var yaml = ManifestYaml(new string('a', 64));
        var valid = AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(
            yaml.Replace("PackageName: sample-skill", "PackageName: " + new string('a', 64), StringComparison.Ordinal)));
        Assert.Equal(new string('a', 64), valid?.Name);
        var error = Assert.Throws<InvalidOperationException>(() => AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(
            yaml.Replace("PackageName: sample-skill", "PackageName: " + new string('a', 65), StringComparison.Ordinal))));
        Assert.Contains("valid skill slug", error.Message);
    }

    [Fact]
    public void Parse_MultipleDocuments_RejectsAgentSkill()
    {
        var yaml = ManifestYaml(new string('a', 64)) + "\n---\nPackageName: Extra\n";
        Assert.Throws<InvalidOperationException>(() => AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(yaml)));
    }

    [Fact]
    public void Parse_OmittedPublisher_IsAccepted()
    {
        var yaml = ManifestYaml(new string('a', 64)).Replace("Publisher: Example", "", StringComparison.Ordinal);
        var manifest = Assert.IsType<AgentSkillManifest>(AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(yaml)));
        Assert.Equal("sample-skill", manifest.Name);
        Assert.Null(manifest.Publisher);
    }

    [Fact]
    public void InstallArchive_OmittedPublisher_KeepsProvenance()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("SKILL.md", SkillText, null));
        var yaml = ManifestYaml(Convert.ToHexString(SHA256.HashData(bytes)), "")
            .Replace("Publisher: Example", "", StringComparison.Ordinal);
        var manifest = Assert.IsType<AgentSkillManifest>(AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(yaml)));
        var result = AgentSkillInstaller.InstallArchive(manifest, Request(), bytes, workspace.Project, workspace.Home);
        Assert.True(File.Exists(Path.Combine(result.InstallerPath, "SKILL.md")));
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", "github-copilot", "sample-skill.json");
        using var json = JsonDocument.Parse(File.ReadAllText(state));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publisher").ValueKind);
        Assert.Equal("Example.SampleSkill", json.RootElement.GetProperty("packageIdentifier").GetString());
    }

    [Fact]
    public void Parse_WinGetManifest_DoesNotClaimIt()
    {
        var yaml = "ManifestType: installer\nPackageIdentifier: Example.App\nInstallers:\n  - InstallerUrl: https://example.org/app.exe\n";
        Assert.Null(AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(yaml)));
    }

    [Theory]
    [InlineData("github-copilot", ".agents")]
    [InlineData("codex", ".agents")]
    [InlineData("claude-code", ".claude")]
    public void InstallArchive_ProjectAgents_WriteSkillAndProvenance(string agent, string agentFolder)
    {
        using var workspace = new Workspace();
        var bytes = Zip(("sample-skill/SKILL.md", SkillText, null), ("sample-skill/notes.txt", "hello", null));
        var result = AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(agent), bytes, workspace.Project, workspace.Home);
        var destination = Path.Combine(workspace.Project, agentFolder, "skills", "sample-skill");
        Assert.True(result.Success);
        Assert.Equal("agentSkill", result.InstallerType);
        Assert.Equal(destination, result.InstallerPath);
        Assert.Equal(SkillText, File.ReadAllText(Path.Combine(destination, "SKILL.md")).TrimStart('\uFEFF'));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "notes.txt")).TrimStart('\uFEFF'));
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", agent, "sample-skill.json");
        using var json = JsonDocument.Parse(File.ReadAllText(state));
        Assert.Equal("Example.SampleSkill", json.RootElement.GetProperty("packageIdentifier").GetString());
        Assert.Equal("sample-skill", json.RootElement.GetProperty("packageName").GetString());
        Assert.Equal("Example", json.RootElement.GetProperty("publisher").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), json.RootElement.GetProperty("artifactSha256").GetString());
        Assert.Equal("project", json.RootElement.GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData("github-copilot", ".copilot")]
    [InlineData("codex", ".codex")]
    [InlineData("claude-code", ".claude")]
    public void InstallArchive_UserScope_UsesAgentHomeWithoutProjectFiles(string agent, string agentFolder)
    {
        using var workspace = new Workspace();
        var bytes = Zip(("SKILL.md", SkillText, null));
        var result = AgentSkillInstaller.InstallArchive(Manifest(bytes, ""), Request(agent, "user"), bytes, workspace.Project, workspace.Home);
        Assert.Equal(Path.Combine(workspace.Home, agentFolder, "skills", "sample-skill"), result.InstallerPath);
        Assert.True(File.Exists(Path.Combine(result.InstallerPath, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(workspace.Home, ".pinget", "agent-skills", agent, "sample-skill.json")));
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_SkillPathDirectoryMayDifferFromPackageName()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("skills/source-folder/SKILL.md", SkillText, null),
            ("skills/source-folder/details.txt", "included", null));
        var manifest = Manifest(bytes, "\n  SkillPath: skills/source-folder");
        var result = AgentSkillInstaller.InstallArchive(manifest, Request(), bytes, workspace.Project, workspace.Home);
        Assert.Equal(Path.Combine(workspace.Project, ".agents", "skills", "sample-skill"), result.InstallerPath);
        Assert.Equal("included", File.ReadAllText(Path.Combine(result.InstallerPath, "details.txt")));
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents", "skills", "source-folder")));
    }

    [Fact]
    public void InstallArchive_MismatchedFrontmatterName_RejectsEvenWithDifferentSkillPath()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("skills/source-folder/SKILL.md",
            "---\nname: other-skill\ndescription: Other\n---\n", null));
        var manifest = Manifest(bytes, "\n  SkillPath: skills/source-folder");
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(manifest, Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains("must match manifest PackageName", error.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_SameVersion_NoOpPreservesSkillAndProvenance()
    {
        using var workspace = new Workspace();
        var first = Zip(("sample-skill/SKILL.md", SkillText, null), ("sample-skill/old.txt", "old", null));
        var second = Zip(("sample-skill/SKILL.md", SkillText, null), ("sample-skill/new.txt", "new", null));
        var result = AgentSkillInstaller.InstallArchive(Manifest(first), Request(), first, workspace.Project, workspace.Home);
        var repeat = AgentSkillInstaller.InstallArchive(Manifest(second), Request(), second, workspace.Project, workspace.Home);
        Assert.True(repeat.NoOp);
        Assert.Equal("old", File.ReadAllText(Path.Combine(result.InstallerPath, "old.txt")));
        Assert.False(File.Exists(Path.Combine(result.InstallerPath, "new.txt")));
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", "github-copilot", "sample-skill.json");
        using var json = JsonDocument.Parse(File.ReadAllText(state));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), json.RootElement.GetProperty("artifactSha256").GetString());
    }

    [Fact]
    public void InstallArchive_InstalledTreeHash_MatchesRustFileFormatAndIgnoresEmptyDirectories()
    {
        using var workspace = new Workspace();
        var archive = Zip(("sample-skill/SKILL.md", SkillText, null),
            ("sample-skill/notes.txt", "hello", null),
            ("sample-skill/nested/deep.txt", "deep", null));
        var manifest = Manifest(archive);
        var installed = AgentSkillInstaller.InstallArchive(
            manifest, Request(), archive, workspace.Project, workspace.Home);
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", "github-copilot", "sample-skill.json");

        // Independently calculated using sorted UTF-8 paths and u64 little-endian path/content lengths.
        const string rustDigest = "2d4ce9150dae85112f79f7d716739b86f3e1bf2889e2b90f9da6f7b1918a39b5";
        using (var json = JsonDocument.Parse(File.ReadAllText(state)))
            Assert.Equal(rustDigest, json.RootElement.GetProperty("installedTreeSha256").GetString());

        Directory.CreateDirectory(Path.Combine(installed.InstallerPath, "empty"));
        Assert.True(AgentSkillInstaller.InstallArchive(
            manifest, Request(), archive, workspace.Project, workspace.Home).NoOp);
    }

    [Fact]
    public void InstallArchive_InstalledTreeHash_OrdersUnicodePathsByUtf8Bytes()
    {
        using var workspace = new Workspace();
        var archive = Zip(("sample-skill/SKILL.md", SkillText, null),
            ("sample-skill/\U0001F600.txt", "emoji", null),
            ("sample-skill/\uE000.txt", "bmp", null));
        var manifest = Manifest(archive);
        var installed = AgentSkillInstaller.InstallArchive(
            manifest, Request(), archive, workspace.Project, workspace.Home);
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", "github-copilot", "sample-skill.json");

        // UTF-8 orders U+E000 (EE 80 80) before U+1F600 (F0 9F 98 80); UTF-16 ordinal reverses them.
        const string rustDigest = "c7df584ccc30d9a92c36b663214437dc2a65768ee23d4fe5b49e52ff7ce5b394";
        using (var json = JsonDocument.Parse(File.ReadAllText(state)))
            Assert.Equal(rustDigest, json.RootElement.GetProperty("installedTreeSha256").GetString());
        Assert.True(File.Exists(Path.Combine(installed.InstallerPath, "\uE000.txt")));
        Assert.True(File.Exists(Path.Combine(installed.InstallerPath, "\U0001F600.txt")));
        Assert.True(AgentSkillInstaller.InstallArchive(
            manifest, Request(), archive, workspace.Project, workspace.Home).NoOp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallArchive_RustCompatibleProvenance_AllowsMixedCaseHashAndRejectsEditedFiles(bool uppercase)
    {
        using var workspace = new Workspace();
        var first = Zip(("sample-skill/SKILL.md", SkillText, null),
            ("sample-skill/notes.txt", "hello", null),
            ("sample-skill/nested/deep.txt", "deep", null));
        var installed = AgentSkillInstaller.InstallArchive(
            Manifest(first), Request(), first, workspace.Project, workspace.Home);
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", "github-copilot", "sample-skill.json");
        const string rustDigest = "2d4ce9150dae85112f79f7d716739b86f3e1bf2889e2b90f9da6f7b1918a39b5";
        File.WriteAllText(state, JsonSerializer.Serialize(new
        {
            manifestType = "pinget.agentSkill",
            packageIdentifier = "example.sampleskill",
            packageVersion = "1.0.0",
            packageName = "sample-skill",
            agent = "github-copilot",
            scope = "project",
            installedPath = installed.InstallerPath,
            installedTreeSha256 = uppercase ? rustDigest.ToUpperInvariant() : rustDigest,
            sourceType = "archive",
        }));
        var second = Zip(("sample-skill/SKILL.md", SkillText, null),
            ("sample-skill/new.txt", "updated", null));
        var upgradedYaml = ManifestYaml(Convert.ToHexString(SHA256.HashData(second)))
            .Replace("PackageVersion: 1.0.0", "PackageVersion: 2.0.0", StringComparison.Ordinal);
        var upgraded = Assert.IsType<AgentSkillManifest>(AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(upgradedYaml)));
        Assert.True(AgentSkillInstaller.InstallArchive(
            upgraded, Request(), second, workspace.Project, workspace.Home).Success);
        Assert.Equal("updated", File.ReadAllText(Path.Combine(installed.InstallerPath, "new.txt")));

        File.WriteAllText(Path.Combine(installed.InstallerPath, "new.txt"), "edited");
        var thirdYaml = upgradedYaml.Replace("PackageVersion: 2.0.0", "PackageVersion: 3.0.0", StringComparison.Ordinal);
        var third = Assert.IsType<AgentSkillManifest>(AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(thirdYaml)));
        var previousState = File.ReadAllText(state);
        Assert.Contains("changed", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(third, Request(), second, workspace.Project, workspace.Home)).Message);
        Assert.Equal(previousState, File.ReadAllText(state));
        Assert.Equal("edited", File.ReadAllText(Path.Combine(installed.InstallerPath, "new.txt")));
    }

    [Fact]
    public void InstallArchive_ManagedTreeOverTwentyFiveMiB_RejectsAndPreservesProvenance()
    {
        using var workspace = new Workspace();
        var archive = Zip(("sample-skill/SKILL.md", SkillText, null));
        var manifest = Manifest(archive);
        var installed = AgentSkillInstaller.InstallArchive(
            manifest, Request(), archive, workspace.Project, workspace.Home);
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", "github-copilot", "sample-skill.json");
        var previousState = File.ReadAllText(state);
        foreach (var name in new[] { "large-a.bin", "large-b.bin" })
        {
            using var file = File.Create(Path.Combine(installed.InstallerPath, name));
            file.SetLength(13 * 1024 * 1024);
        }
        Assert.Contains("exceeds extraction limits", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(manifest, Request(), archive, workspace.Project, workspace.Home)).Message);
        Assert.Equal(previousState, File.ReadAllText(state));
    }

    [Fact]
    public void InstallArchive_FailedUpgradeAfterSwap_RestoresSkillAndProvenance()
    {
        using var workspace = new Workspace();
        var first = Zip(("sample-skill/SKILL.md", SkillText, null), ("sample-skill/old.txt", "old", null));
        var second = Zip(("sample-skill/SKILL.md", SkillText, null), ("sample-skill/new.txt", "new", null));
        var installed = AgentSkillInstaller.InstallArchive(Manifest(first), Request(), first, workspace.Project, workspace.Home);
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", "github-copilot", "sample-skill.json");
        var oldState = File.ReadAllText(state);
        var upgradedYaml = ManifestYaml(Convert.ToHexString(SHA256.HashData(second)))
            .Replace("PackageVersion: 1.0.0", "PackageVersion: 2.0.0", StringComparison.Ordinal);
        var upgraded = Assert.IsType<AgentSkillManifest>(AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(upgradedYaml)));
        Assert.Throws<IOException>(() => AgentSkillInstaller.InstallArchive(
            upgraded, Request(), second, workspace.Project, workspace.Home,
            beforeProvenanceCommit: () => throw new IOException("Simulated provenance commit failure.")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(installed.InstallerPath, "old.txt")));
        Assert.False(File.Exists(Path.Combine(installed.InstallerPath, "new.txt")));
        Assert.Equal(oldState, File.ReadAllText(state));
        Assert.True(AgentSkillInstaller.InstallArchive(
            upgraded, Request(), second, workspace.Project, workspace.Home).Success);
    }

    [Fact]
    public void InstallArchive_BadDigest_LeavesNoInstall()
    {
        using var workspace = new Workspace();
        var valid = Zip(("sample-skill/SKILL.md", SkillText, null));
        var tampered = Zip(("sample-skill/SKILL.md", SkillText + "tampered", null));
        Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(valid), Request(), tampered, workspace.Project, workspace.Home));
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_IgnoreSecurityHash_CannotBypassDigest()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("sample-skill/SKILL.md", SkillText, null));
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes),
                Request() with { IgnoreSecurityHash = true }, bytes, workspace.Project, workspace.Home));
        Assert.Contains("Installer-specific options", error.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("sample-skill/../../escape")]
    [InlineData("sample-skill\\escape")]
    [InlineData("sample-skill/CON")]
    public void InstallArchive_UnsafeEntry_RejectsWithoutWriting(string entry)
    {
        using var workspace = new Workspace();
        var bytes = Zip(("sample-skill/SKILL.md", SkillText, null), (entry, "bad", null));
        Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_UnixSymlink_RejectsWithoutWriting()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("sample-skill/SKILL.md", SkillText, null),
            ("sample-skill/evil", "../../elsewhere", 0xA000 << 16));
        Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_ExcessiveEntries_RejectsWithoutWriting()
    {
        using var workspace = new Workspace();
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i <= 1000; i++)
                zip.CreateEntry($"sample-skill/{i}.txt");
        var bytes = stream.ToArray();
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains("too many entries", error.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_DownloadOverTenMiB_RejectsBeforeWriting()
    {
        using var workspace = new Workspace();
        var bytes = new byte[10 * 1024 * 1024 + 1];
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains("download limit", error.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_EntryOverTwentyFiveMiB_RejectsBeforeWriting()
    {
        using var workspace = new Workspace();
        var bytes = ZipWithLargeEntries(("sample-skill/large.bin", 25 * 1024 * 1024 + 1));
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains("exceeds extraction limits", error.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_TotalOverTwentyFiveMiB_RejectsBeforeWriting()
    {
        using var workspace = new Workspace();
        var bytes = ZipWithLargeEntries(("sample-skill/first.bin", 13 * 1024 * 1024),
            ("sample-skill/second.bin", 13 * 1024 * 1024));
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains("exceeds extraction limits", error.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_OversizedSkillMarkdown_RejectsWithoutWriting()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("sample-skill/SKILL.md", SkillText + new string('x', 1024 * 1024), null));
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains("SKILL.md", error.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Theory]
    [InlineData("invalid", "resolved directory")]
    [InlineData("---\nname: other-skill\ndescription: Something\n---\n", "must match")]
    [InlineData("---\nname: Bad_Name\ndescription: Something\n---\n", "valid name")]
    public void InstallArchive_MissingOrInvalidSkill_Rejects(string markdown, string message)
    {
        using var workspace = new Workspace();
        var bytes = markdown == "invalid" ? Zip(("sample-skill/other.txt", "text", null))
            : Zip(("sample-skill/SKILL.md", markdown, null));
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void InstallArchive_EmptyInstructions_Rejects()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("sample-skill/SKILL.md",
            "---\nname: sample-skill\ndescription: Sample skill\n---\n", null));
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains("instructions", error.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_UnmanagedSkill_CannotBeOverwritten()
    {
        using var workspace = new Workspace();
        var existing = Path.Combine(workspace.Project, ".agents", "skills", "sample-skill");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "preserve");
        var bytes = Zip(("sample-skill/SKILL.md", SkillText, null));
        Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(existing, "keep.txt")));
    }

    [Fact]
    public void InstallArchive_PreexistingProvenance_RejectsWithoutInstalling()
    {
        using var workspace = new Workspace();
        var state = Path.Combine(workspace.Project, ".pinget", "agent-skills", "github-copilot", "sample-skill.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "existing record");
        var bytes = Zip(("sample-skill/SKILL.md", SkillText, null));
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(Manifest(bytes), Request(), bytes, workspace.Project, workspace.Home));
        Assert.Contains("provenance is missing", error.Message);
        Assert.Equal("existing record", File.ReadAllText(state));
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void RepositoryInstall_AgentSkillWithoutAgent_RejectsBeforePlatformGuard()
    {
        using var workspace = new Workspace();
        var manifestFile = Path.Combine(workspace.Project, "skill.yaml");
        File.WriteAllText(manifestFile, ManifestYaml(new string('a', 64)));
        using var repo = Repository.Open(new RepositoryOptions { AppRoot = Path.Combine(workspace.Project, "repo"), SourceMode = SourceMode.Private });
        var error = Assert.Throws<InvalidOperationException>(() =>
            repo.Install(Request(null) with { ManifestPath = manifestFile }));
        Assert.Contains("--agent", error.Message);
    }

    [Fact]
    public void RepositoryInstall_AgentOptionOnWinGetManifest_FailsInsteadOfIgnoringAgent()
    {
        using var workspace = new Workspace();
        var manifestFile = Path.Combine(workspace.Project, "app.yaml");
        File.WriteAllText(manifestFile, """
            ManifestType: installer
            PackageIdentifier: Example.App
            PackageVersion: 1.0.0
            PackageName: Example App
            Installers:
              - InstallerUrl: https://example.org/app.exe
                InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000
            """);
        using var repo = Repository.Open(new RepositoryOptions { AppRoot = Path.Combine(workspace.Project, "repo"), SourceMode = SourceMode.Private });
        var error = Assert.Throws<InvalidOperationException>(() =>
            repo.Install(Request() with { ManifestPath = manifestFile }));
        Assert.Contains("requires a pinget.agentSkill", error.Message);
    }

    [Fact]
    public void InstallArchive_InvalidAgentOrScope_FailsBeforeWriting()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("sample-skill/SKILL.md", SkillText, null));
        var manifest = Manifest(bytes);
        var agent = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(manifest, Request("other"), bytes, workspace.Project, workspace.Home));
        Assert.Contains("--agent", agent.Message);
        var scope = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(manifest, Request(scope: "machine"), bytes, workspace.Project, workspace.Home));
        Assert.Contains("--scope", scope.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Theory]
    [InlineData("archive", "Url: https://example.org/a.zip\n      Sha256: 0000000000000000000000000000000000000000000000000000000000000000\n      Format: zip")]
    [InlineData("git", "Url: https://example.org/repo.git\n      Commit: 0123456789012345678901234567890123456789")]
    [InlineData("notion-skill", "Id: 123e4567-e89b-12d3-a456-426614174000\n      VersionId: abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("well-known", "IndexUrl: https://example.org/.well-known/skills/index.json\n      Sha256: 0000000000000000000000000000000000000000000000000000000000000000")]
    public void Parse_SourceVariants_SelectsTypedSource(string type, string fields)
    {
        var manifest = SourceManifest($"    Type: {type}\n    {fields.Replace("\n      ", "\n    ", StringComparison.Ordinal)}",
            type == "notion-skill" ? "" : "  SkillPath: sample-skill");
        Assert.Equal(type, manifest.Source?.Type);
        Assert.Equal("sample-skill", manifest.Name);
    }

    [Theory]
    [InlineData("    Type: archive\n    Url: http://example.org/x\n    Sha256: 0000000000000000000000000000000000000000000000000000000000000000\n    Format: zip", "HTTPS")]
    [InlineData("    Type: archive\n    Url: https://example.org/x\n    Sha256: 00\n    Format: zip", "64 hexadecimal")]
    [InlineData("    Type: archive\n    Url: https://example.org/x\n    Sha256: 0000000000000000000000000000000000000000000000000000000000000000\n    Format: rar", "Format")]
    [InlineData("    Type: git\n    Url: https://example.org/repo.git\n    Commit: main", "40-character")]
    [InlineData("    Type: notion-plugin\n    Id: not-a-uuid\n    VersionId: 123", "UUID")]
    [InlineData("    Type: notion-skill\n    Id: 123e4567-e89b-12d3-a456-426614174000\n    VersionId: abc", "64-character")]
    [InlineData("    Type: git\n    Url: https://example.org/repo.git\n    Commit: 0123456789012345678901234567890123456789\n    Sha256: 0000000000000000000000000000000000000000000000000000000000000000", "not valid")]
    public void Parse_InvalidSource_Rejects(string source, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SourceManifest(source));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Parse_LegacyAndSourceOrPluginWithoutPaths_Rejects()
    {
        const string archive = "    Type: archive\n    Url: https://example.org/a.zip\n    Sha256: 0000000000000000000000000000000000000000000000000000000000000000\n    Format: zip";
        var yaml = ManifestYaml(new string('a', 64)) + "\n  Source:\n" + archive + "\n";
        Assert.Contains("mutually exclusive", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.TryParse(Encoding.UTF8.GetBytes(yaml))).Message);
        var plugin = "    Type: notion-plugin\n    Id: 123e4567-e89b-12d3-a456-426614174000\n    VersionId: " + new string('x', 64);
        Assert.Contains("requires PluginPath and SkillPath",
            Assert.Throws<InvalidOperationException>(() => SourceManifest(plugin)).Message);
        Assert.Contains("Plugin SkillPath", Assert.Throws<InvalidOperationException>(() =>
            SourceManifest(archive, "  PluginPath: .")).Message);
        Assert.Contains("unsupported fields", Assert.Throws<InvalidOperationException>(() =>
            SourceManifest(archive, "  Execute: true")).Message);
    }

    [Fact]
    public void InstallArchive_TarGzWrappedPlugin_InstallsOnlySelectedSkill()
    {
        using var workspace = new Workspace();
        const string pluginJson = "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"org.example.plugin\"}";
        var archive = Tar(("wrapper/plugin/plugin.json", pluginJson, TarEntryType.RegularFile),
            ("wrapper/plugin/skills/sample-skill/SKILL.md", SkillText, TarEntryType.RegularFile),
            ("wrapper/plugin/skills/sample-skill/info.txt", "selected", TarEntryType.RegularFile),
            ("wrapper/plugin/commands/ignored.md", "not installed", TarEntryType.RegularFile));
        var source = $"    Type: archive\n    Url: https://example.org/plugin.tar.gz\n    Sha256: {Convert.ToHexString(SHA256.HashData(archive))}\n    Format: tar.gz";
        var manifest = SourceManifest(source, "  PluginPath: wrapper/plugin\n  SkillPath: skills/sample-skill");
        var result = AgentSkillInstaller.InstallArchive(manifest, Request(), archive, workspace.Project, workspace.Home);
        Assert.Equal("selected", File.ReadAllText(Path.Combine(result.InstallerPath, "info.txt")));
        Assert.False(File.Exists(Path.Combine(result.InstallerPath, "plugin.json")));
        Assert.False(Directory.Exists(Path.Combine(result.InstallerPath, "commands")));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Project, ".pinget", "agent-skills",
            "github-copilot", "sample-skill.json")));
        Assert.Equal("archive", state.RootElement.GetProperty("sourceType").GetString());
    }

    [Theory]
    [InlineData("wrong schema")]
    [InlineData("missing name")]
    [InlineData("uppercase name")]
    [InlineData("bad double hyphen")]
    [InlineData("not reverse dns")]
    [InlineData("missing plugin segment")]
    public void InstallArchive_InvalidPluginManifest_Rejects(string invalid)
    {
        using var workspace = new Workspace();
        var json = invalid switch
        {
            "wrong schema" => "{\"$schema\":\"https://wrong\",\"name\":\"org.example.plugin\"}",
            "uppercase name" => "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"MyPlugin\"}",
            "bad double hyphen" => "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"org.example--plugin\"}",
            "not reverse dns" => "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"plugin\"}",
            "missing plugin segment" => "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"org.plugin\"}",
            _ => "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\"}",
        };
        var archive = Zip(("plugin.json", json, null), ("skills/sample-skill/SKILL.md", SkillText, null));
        var manifest = SourceManifest($"    Type: archive\n    Url: https://example.org/p.zip\n    Sha256: {Convert.ToHexString(SHA256.HashData(archive))}\n    Format: zip",
            "  PluginPath: .\n  SkillPath: skills/sample-skill");
        Assert.Contains("plugin.json", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(manifest, Request(), archive, workspace.Project, workspace.Home)).Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_PluginUnknownFields_WarnsWithoutInstallingOtherComponents()
    {
        using var workspace = new Workspace();
        const string pluginJson = "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"org.example.plugin\",\"unknownFutureField\":true,\"commands\":[\"not installed\"]}";
        var archive = Zip(("plugin.json", pluginJson, null),
            ("skills/sample-skill/SKILL.md", SkillText, null),
            ("commands/ignored.md", "ignored", null));
        var manifest = SourceManifest($"    Type: archive\n    Url: https://example.org/plugin.zip\n    Sha256: {Convert.ToHexString(SHA256.HashData(archive))}\n    Format: zip",
            "  PluginPath: .\n  SkillPath: skills/sample-skill");
        var warnings = new List<string>();
        var result = AgentSkillInstaller.InstallArchive(manifest, Request(), archive,
            workspace.Project, workspace.Home, warnings.Add);
        Assert.Contains("commands, unknownFutureField", Assert.Single(warnings));
        Assert.True(File.Exists(Path.Combine(result.InstallerPath, "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(result.InstallerPath, "commands")));
    }

    [Fact]
    public void InstallArchive_TarGzLink_RejectsBeforeWriting()
    {
        using var workspace = new Workspace();
        var archive = Tar(("sample-skill/SKILL.md", SkillText, TarEntryType.RegularFile),
            ("sample-skill/escape", "../../escape", TarEntryType.SymbolicLink));
        var manifest = SourceManifest($"    Type: archive\n    Url: https://example.org/x.tar.gz\n    Sha256: {Convert.ToHexString(SHA256.HashData(archive))}\n    Format: tar.gz",
            "  SkillPath: sample-skill");
        Assert.Contains("link", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(manifest, Request(), archive, workspace.Project, workspace.Home)).Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Theory]
    [InlineData("../outside", "unsafe")]
    [InlineData("sample-skill/skill.md", "duplicate")]
    public void InstallArchive_TarGzTraversalOrCaseCollision_Rejects(string path, string reason)
    {
        using var workspace = new Workspace();
        var archive = Tar(("sample-skill/SKILL.md", SkillText, TarEntryType.RegularFile),
            (path, "not safe", TarEntryType.RegularFile));
        var manifest = SourceManifest($"    Type: archive\n    Url: https://example.org/x.tar.gz\n    Sha256: {Convert.ToHexString(SHA256.HashData(archive))}\n    Format: tar.gz",
            "  SkillPath: sample-skill");
        Assert.Contains(reason, Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallArchive(manifest, Request(), archive, workspace.Project, workspace.Home)).Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void InstallArchive_SkillMd_InstallsSingleSkill()
    {
        using var workspace = new Workspace();
        var bytes = Encoding.UTF8.GetBytes(SkillText);
        var manifest = SourceManifest($"    Type: archive\n    Url: https://example.org/SKILL.md\n    Sha256: {Convert.ToHexString(SHA256.HashData(bytes))}\n    Format: skill-md");
        var result = AgentSkillInstaller.InstallArchive(manifest, Request(), bytes, workspace.Project, workspace.Home);
        Assert.Equal(SkillText, File.ReadAllText(Path.Combine(result.InstallerPath, "SKILL.md")));
    }

    [Fact]
    public void InstallArchive_SignedSourceQuery_IsNotPersisted()
    {
        using var workspace = new Workspace();
        var bytes = Zip(("SKILL.md", SkillText, null));
        var manifest = SourceManifest($"    Type: archive\n    Url: https://example.org/skill.zip?signature=secret\n    Sha256: {Convert.ToHexString(SHA256.HashData(bytes))}\n    Format: zip");
        AgentSkillInstaller.InstallArchive(manifest, Request(), bytes, workspace.Project, workspace.Home);
        var state = File.ReadAllText(Path.Combine(workspace.Project, ".pinget", "agent-skills",
            "github-copilot", "sample-skill.json"));
        Assert.Contains("\"sourceUrl\": \"https://example.org/skill.zip\"", state);
        Assert.DoesNotContain("signature=secret", state);
    }

    [Theory]
    [InlineData("notion-skill")]
    [InlineData("notion-plugin")]
    public void Install_NotionPinnedVersion_UsesAuthenticatedMetadataButUnsignedArchive(string type)
    {
        using var workspace = new Workspace();
        const string id = "123e4567-e89b-12d3-a456-426614174000";
        var version = new string('v', 64);
        var plugin = type == "notion-plugin";
        var archive = Tar(("bundle/plugin.json",
                "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"org.example.plugin\"}",
                TarEntryType.RegularFile),
            ("bundle/skills/sample-skill/SKILL.md", SkillText, TarEntryType.RegularFile));
        var manifest = SourceManifest($"    Type: {type}\n    Id: {id}\n    VersionId: {version}",
            plugin ? "  PluginPath: .\n  SkillPath: skills/sample-skill" : "  SkillPath: skills/sample-skill");
        var apiCalls = 0;
        var downloadCalls = 0;
        var oldKey = Environment.GetEnvironmentVariable("NOTION_API_KEY");
        Environment.SetEnvironmentVariable("NOTION_API_KEY", "secret-test-only");
        try
        {
            using var client = new HttpClient(new StaticHttpHandler(request =>
            {
                if (request.RequestUri!.Host == "api.notion.com")
                {
                    apiCalls++;
                    Assert.Equal($"https://api.notion.com/v1/ai/{(plugin ? "plugins" : "skills")}/{id}", request.RequestUri.AbsoluteUri);
                    Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                    Assert.Equal("secret-test-only", request.Headers.Authorization?.Parameter);
                    Assert.Equal("2026-03-11", Assert.Single(request.Headers.GetValues("Notion-Version")));
                    return Response(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        id, version_id = version, url = "https://cdn.example.org/signed.tar.gz?signature=sensitive",
                    })));
                }
                downloadCalls++;
                Assert.Equal("cdn.example.org", request.RequestUri.Host);
                Assert.Null(request.Headers.Authorization);
                return Response(archive);
            }));
            var result = AgentSkillInstaller.Install(manifest, Request(), client, workspace.Project, workspace.Home);
            Assert.Equal(SkillText, File.ReadAllText(Path.Combine(result.InstallerPath, "SKILL.md")));
            var state = File.ReadAllText(Path.Combine(workspace.Project, ".pinget", "agent-skills",
                "github-copilot", "sample-skill.json"));
            Assert.DoesNotContain("secret-test-only", state);
            Assert.DoesNotContain("signature=sensitive", state);
            using var provenance = JsonDocument.Parse(state);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(archive)),
                provenance.RootElement.GetProperty("artifactSha256").GetString());
            Assert.Equal(version, provenance.RootElement.GetProperty("sourceRevision").GetString());
            Assert.Equal(1, apiCalls);
            Assert.Equal(1, downloadCalls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOTION_API_KEY", oldKey);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Install_NotionIdentityOrVersionMismatch_StopsBeforeSignedDownload(bool badId)
    {
        using var workspace = new Workspace();
        const string id = "123e4567-e89b-12d3-a456-426614174000";
        var manifest = SourceManifest($"    Type: notion-skill\n    Id: {id}\n    VersionId: {new string('v', 64)}");
        var oldKey = Environment.GetEnvironmentVariable("NOTION_API_KEY");
        Environment.SetEnvironmentVariable("NOTION_API_KEY", "secret-test-only");
        try
        {
            var calls = 0;
            using var client = new HttpClient(new StaticHttpHandler(_ =>
            {
                calls++;
                return Response(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    id = badId ? "123e4567-e89b-12d3-a456-426614174001" : id,
                    version_id = badId ? new string('v', 64) : new string('x', 64),
                    url = "https://cdn.example.org/signed.tar.gz",
                })));
            }));
            Assert.Contains("id or version", Assert.Throws<InvalidOperationException>(() =>
                AgentSkillInstaller.Install(manifest, Request(), client, workspace.Project, workspace.Home)).Message);
            Assert.Equal(1, calls);
            Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOTION_API_KEY", oldKey);
        }
    }

    [Fact]
    public void Install_NotionMissingApiKey_RejectsBeforeNetwork()
    {
        using var workspace = new Workspace();
        var manifest = SourceManifest($"    Type: notion-skill\n    Id: 123e4567-e89b-12d3-a456-426614174000\n    VersionId: {new string('v', 64)}");
        var oldKey = Environment.GetEnvironmentVariable("NOTION_API_KEY");
        Environment.SetEnvironmentVariable("NOTION_API_KEY", null);
        try
        {
            using var client = new HttpClient(new StaticHttpHandler(_ =>
                throw new InvalidOperationException("Network must not be called")));
            Assert.Contains("NOTION_API_KEY", Assert.Throws<InvalidOperationException>(() =>
                AgentSkillInstaller.Install(manifest, Request(), client, workspace.Project, workspace.Home)).Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOTION_API_KEY", oldKey);
        }
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("skill-md")]
    public void Install_WellKnownIndex_PinsEntryDigestBeforeDownloading(string type)
    {
        using var workspace = new Workspace();
        var artifact = type == "archive" ? Zip(("SKILL.md", SkillText, null)) : Encoding.UTF8.GetBytes(SkillText);
        var sha = Convert.ToHexString(SHA256.HashData(artifact));
        var manifest = SourceManifest($"    Type: well-known\n    IndexUrl: https://example.org/.well-known/skills/index.json\n    Sha256: {sha}");
        var calls = 0;
        using var client = new HttpClient(new StaticHttpHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("https://example.org/.well-known/skills/index.json", request.RequestUri!.AbsoluteUri);
                return Response(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["$schema"] = "https://schemas.agentskills.io/discovery/0.2.0/schema.json",
                    ["skills"] = new[] { new { name = "other-skill", type,
                        url = "ignored", digest = "sha256:" + new string('0', 64) },
                        new { name = "sample-skill", type,
                            url = "../../artifact", digest = "sha256:" + sha } },
                })));
            }
            Assert.Equal("https://example.org/artifact", request.RequestUri!.AbsoluteUri);
            return Response(artifact);
        }));
        var result = AgentSkillInstaller.Install(manifest, Request(), client, workspace.Project, workspace.Home);
        Assert.True(File.Exists(Path.Combine(result.InstallerPath, "SKILL.md")));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Install_WellKnownMismatchedIndexDigest_DoesNotDownloadArtifact()
    {
        using var workspace = new Workspace();
        var manifest = SourceManifest($"    Type: well-known\n    IndexUrl: https://example.org/.well-known/skills/index.json\n    Sha256: {new string('a', 64)}");
        var calls = 0;
        using var client = new HttpClient(new StaticHttpHandler(_ =>
        {
            calls++;
            return Response(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["$schema"] = "https://schemas.agentskills.io/discovery/0.2.0/schema.json",
                ["skills"] = new[] { new { name = "sample-skill", type = "skill-md",
                    url = "https://example.org/SKILL.md", digest = "sha256:" + new string('b', 64) } },
            })));
        }));
        Assert.Contains("digest", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.Install(manifest, Request(), client, workspace.Project, workspace.Home)).Message);
        Assert.Equal(1, calls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }

    [Fact]
    public void Install_WellKnownRelativeHttpUrl_RejectsBeforeDownload()
    {
        using var workspace = new Workspace();
        var manifest = SourceManifest($"    Type: well-known\n    IndexUrl: https://example.org/.well-known/skills/index.json\n    Sha256: {new string('a', 64)}");
        var calls = 0;
        using var client = new HttpClient(new StaticHttpHandler(_ =>
        {
            calls++;
            return Response(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["$schema"] = "https://schemas.agentskills.io/discovery/0.2.0/schema.json",
                ["skills"] = new[] { new { name = "sample-skill", type = "skill-md",
                    url = "http://example.org/insecure", digest = "sha256:" + new string('a', 64) } },
            })));
        }));
        Assert.Contains("HTTPS", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.Install(manifest, Request(), client, workspace.Project, workspace.Home)).Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InstallGitTree_PinnedCommit_InstallsOnlySelectedPluginSkill()
    {
        using var workspace = new Workspace();
        Git(workspace.Project, "init", "-q");
        Directory.CreateDirectory(Path.Combine(workspace.Project, "plugin", "skills", "sample-skill"));
        Directory.CreateDirectory(Path.Combine(workspace.Project, "plugin", "commands"));
        File.WriteAllText(Path.Combine(workspace.Project, "plugin", "plugin.json"),
            "{\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\",\"name\":\"org.example.plugin\"}");
        File.WriteAllText(Path.Combine(workspace.Project, "plugin", "skills", "sample-skill", "SKILL.md"), SkillText);
        File.WriteAllText(Path.Combine(workspace.Project, "plugin", "commands", "other.md"), "not included");
        Git(workspace.Project, "add", "plugin");
        Git(workspace.Project, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.org",
            "commit", "-qm", "pinned");
        var commit = Git(workspace.Project, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(workspace.Project, "plugin", "skills", "sample-skill", "SKILL.md"),
            "---\nname: wrong-name\ndescription: mutated\n---\nNew instructions.");
        var manifest = SourceManifest($"    Type: git\n    Url: https://example.org/repo.git\n    Commit: {commit}",
            "  PluginPath: plugin\n  SkillPath: skills/sample-skill");
        var result = AgentSkillInstaller.InstallGitTree(manifest, Request(), workspace.Project,
            workspace.Project, workspace.Home);
        Assert.Equal(SkillText, File.ReadAllText(Path.Combine(result.InstallerPath, "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(result.InstallerPath, "other.md")));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Project, ".pinget", "agent-skills",
            "github-copilot", "sample-skill.json")));
        Assert.Equal(commit, state.RootElement.GetProperty("sourceRevision").GetString());
        Assert.Equal("git", state.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("94A0C288B52BA49FCD414971EA758AFAA34172D0323349AA2634978F62AE6FB9",
            state.RootElement.GetProperty("artifactSha256").GetString());
    }

    [Fact]
    public void InstallGitTree_MissingRootSkill_RequiresSkillPath()
    {
        using var workspace = new Workspace();
        Git(workspace.Project, "init", "-q");
        Directory.CreateDirectory(Path.Combine(workspace.Project, "skills", "sample-skill"));
        File.WriteAllText(Path.Combine(workspace.Project, "skills", "sample-skill", "SKILL.md"), SkillText);
        Git(workspace.Project, "add", "skills");
        Git(workspace.Project, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.org",
            "commit", "-qm", "pinned");
        var manifest = SourceManifest($"    Type: git\n    Url: https://example.org/repo.git\n    Commit: {Git(workspace.Project, "rev-parse", "HEAD")}");
        Assert.Contains("specify SkillPath", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallGitTree(manifest, Request(), workspace.Project,
                workspace.Project, workspace.Home)).Message);
    }

    [Fact]
    public void InstallGitTree_RootSkillAtExactCommit_IsAccepted()
    {
        using var workspace = new Workspace();
        Git(workspace.Project, "init", "-q");
        File.WriteAllText(Path.Combine(workspace.Project, "SKILL.md"), SkillText);
        Directory.CreateDirectory(Path.Combine(workspace.Project, "references"));
        File.WriteAllText(Path.Combine(workspace.Project, "references", "guide.md"), "companion reference");
        Git(workspace.Project, "add", "SKILL.md", "references");
        Git(workspace.Project, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.org",
            "commit", "-qm", "pinned");
        var manifest = SourceManifest($"    Type: git\n    Url: https://example.org/repo.git\n    Commit: {Git(workspace.Project, "rev-parse", "HEAD")}");
        var result = AgentSkillInstaller.InstallGitTree(manifest, Request(), workspace.Project,
            workspace.Project, workspace.Home);
        Assert.Equal(SkillText, File.ReadAllText(Path.Combine(result.InstallerPath, "SKILL.md")));
        Assert.Equal("companion reference",
            File.ReadAllText(Path.Combine(result.InstallerPath, "references", "guide.md")));
    }

    [Fact]
    public void InstallGitTree_UnrelatedLargeTreeWithSymlink_DoesNotBlockSelectedSkill()
    {
        using var workspace = new Workspace();
        Git(workspace.Project, "init", "-q");
        Directory.CreateDirectory(Path.Combine(workspace.Project, "skills", "sample-skill"));
        var unrelated = Path.Combine(workspace.Project, "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(workspace.Project, "skills", "sample-skill", "SKILL.md"), SkillText);
        for (var i = 0; i < 1001; i++)
            File.WriteAllText(Path.Combine(unrelated, $"{i:D4}.txt"), "unrelated");
        Git(workspace.Project, "add", "skills", "unrelated");
        var linkTarget = Path.Combine(workspace.Project, "link-target.txt");
        File.WriteAllText(linkTarget, "../outside");
        var linkHash = Git(workspace.Project, "hash-object", "-w", linkTarget);
        Git(workspace.Project, "update-index", "--add", "--cacheinfo", $"120000,{linkHash},unrelated/link");
        Git(workspace.Project, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.org",
            "commit", "-qm", "large repository with unrelated symlink");
        var manifest = SourceManifest($"    Type: git\n    Url: https://example.org/repo.git\n    Commit: {Git(workspace.Project, "rev-parse", "HEAD")}",
            "  SkillPath: skills/sample-skill");
        var result = AgentSkillInstaller.InstallGitTree(manifest, Request(), workspace.Project,
            workspace.Project, workspace.Home);
        Assert.Equal(SkillText, File.ReadAllText(Path.Combine(result.InstallerPath, "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(result.InstallerPath, "0000.txt")));
        Assert.False(File.Exists(Path.Combine(result.InstallerPath, "link")));
    }

    [Fact]
    public void InstallGitTree_SelectedSymlink_RemainsRejected()
    {
        using var workspace = new Workspace();
        Git(workspace.Project, "init", "-q");
        Directory.CreateDirectory(Path.Combine(workspace.Project, "skills", "sample-skill"));
        File.WriteAllText(Path.Combine(workspace.Project, "skills", "sample-skill", "SKILL.md"), SkillText);
        Git(workspace.Project, "add", "skills");
        var linkTarget = Path.Combine(workspace.Project, "link-target.txt");
        File.WriteAllText(linkTarget, "../outside");
        var linkHash = Git(workspace.Project, "hash-object", "-w", linkTarget);
        Git(workspace.Project, "update-index", "--add", "--cacheinfo",
            $"120000,{linkHash},skills/sample-skill/link");
        Git(workspace.Project, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.org",
            "commit", "-qm", "selected symlink");
        var manifest = SourceManifest($"    Type: git\n    Url: https://example.org/repo.git\n    Commit: {Git(workspace.Project, "rev-parse", "HEAD")}",
            "  SkillPath: skills/sample-skill");
        Assert.Contains("link", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillInstaller.InstallGitTree(manifest, Request(), workspace.Project,
                workspace.Project, workspace.Home)).Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.Project, ".agents")));
    }
}
