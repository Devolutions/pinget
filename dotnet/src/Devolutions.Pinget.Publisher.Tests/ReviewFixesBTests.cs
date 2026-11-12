using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

/// <summary>
/// Review fixes B: recovery of a publish killed mid package-swap while publisher temporaries remain,
/// mirroring <c>review_b_tests.rs</c>.
/// </summary>
public sealed class ReviewFixesBTests
{
    private const string Hash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Guid32 = "0123456789abcdef0123456789abcdef";
    private static readonly UTF8Encoding Utf8 = new(false);

    [Fact]
    public void KilledBeforePackageSwap_WithPartialPackageCopyLeft_IsRolledBackThenRepublished()
    {
        using var temp = new TempDirectory();
        var (state, buildA, buildB) = TwoBuilds(temp);
        var (killed, _) = InterruptedCopy(temp, state, buildA, buildB, PublisherPackaging.CommitStep.BeforePackageSwap);
        Assert.NotNull(Marker(killed)["pendingCommit"]);
        // Killed while CopyAtomically copied the package in, plus a Rust publisher's staged package.
        File.WriteAllBytes(Path.Combine(killed, $"source2.msix.{Guid32}.tmp"), [1]);
        File.WriteAllBytes(Path.Combine(killed, $".source2.{buildB}.1.2.new"), [1]);

        Assert.Equal(buildA, PublisherPackaging.VerifyDestination(killed).BuildId);

        // Unrelated files are still untracked while a swap is pending.
        var unrelated = Path.Combine(temp.Path, "unrelated");
        CopyTree(killed, unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep-me.txt"), "keep");
        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.VerifyDestination(unrelated));
        Assert.Contains("untracked", error.Message, StringComparison.Ordinal);
        Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(Options(state, unrelated, buildB, "1.0.0.1")));
        Assert.True(File.Exists(Path.Combine(unrelated, "keep-me.txt")));

        Assert.Equal(buildB, PublisherPackaging.Publish(Options(state, killed, buildB, "1.0.0.1")).BuildId);
        Assert.Equal(buildB, PublisherPackaging.VerifyDestination(killed).BuildId);
        Assert.Null(Marker(killed)["pendingCommit"]);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(killed).Select(Path.GetFileName),
            name => name!.EndsWith(".new", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void KilledAfterPackageSwap_WithTemporariesLeft_IsCompletedThenReused()
    {
        using var temp = new TempDirectory();
        var (state, buildA, buildB) = TwoBuilds(temp);
        var (killed, packageB) = InterruptedCopy(temp, state, buildA, buildB, PublisherPackaging.CommitStep.AfterPackageSwap);
        Assert.NotNull(Marker(killed)["pendingCommit"]);
        Assert.Equal(packageB, File.ReadAllBytes(Path.Combine(killed, "source2.msix")));
        // Killed while writing the final marker, after an earlier Rust layout staged in the destination.
        var markerTemporary = Path.Combine(killed, $".pinget-publisher.json.{Guid32}.tmp");
        File.WriteAllText(markerTemporary, "{");
        var legacy = Path.Combine(killed, $".publish-{buildB}.staging");
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "source2.msix"), packageB);

        Assert.Equal(buildB, PublisherPackaging.VerifyDestination(killed).BuildId);

        Assert.Equal(buildB, PublisherPackaging.Publish(Options(state, killed, buildB, "1.0.0.1")).BuildId);
        Assert.Null(Marker(killed)["pendingCommit"]);
        Assert.False(Directory.Exists(legacy));
        Assert.False(File.Exists(markerTemporary));
        Assert.Equal(buildB, PublisherPackaging.VerifyDestination(killed).BuildId);

        // Without a pending swap, standalone verification stays strict about staging leftovers.
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "source2.msix"), packageB);
        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.VerifyDestination(killed));
        Assert.Contains("untracked", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Publishes A, then publishes B while copying the destination at <paramref name="step"/>, which is exactly
    /// what a process killed at that point leaves on disk. Returns the copy and B's completed package.
    /// </summary>
    private static (string Killed, byte[] PackageB) InterruptedCopy(
        TempDirectory temp, string state, string buildA, string buildB, PublisherPackaging.CommitStep step)
    {
        var destination = Path.Combine(temp.Path, "feed");
        var killed = Path.Combine(temp.Path, "killed-" + step);
        PublisherPackaging.Publish(Options(state, destination, buildA, "1.0.0.0"));
        var captured = false;
        PublisherPackaging.Publish(Options(state, destination, buildB, "1.0.0.1"), afterCopy: null, (current, feed) =>
        {
            if (current == step)
            {
                CopyTree(feed, killed);
                captured = true;
            }
        });
        Assert.True(captured);
        return (killed, File.ReadAllBytes(Path.Combine(destination, "source2.msix")));
    }

    private static JsonNode Marker(string destination) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(destination, ".pinget-publisher.json")))!;

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }

    private static string Manifest(string version) =>
        $"PackageIdentifier: Pinget.ReviewB\nPackageVersion: {version}\nPackageLocale: en-US\nPackageName: Review B\n"
        + "Publisher: Pinget\nLicense: MIT\nShortDescription: Review B\nInstallers:\n  - Architecture: x64\n    "
        + $"InstallerType: exe\n    InstallerUrl: https://example.invalid/a.exe\n    InstallerSha256: {Hash}\n"
        + "ManifestType: singleton\nManifestVersion: 1.9.0\n";

    /// <summary>Builds snapshot A (1.0.0) and snapshot B (1.0.0 + 2.0.0) in one state directory.</summary>
    private static (string State, string BuildA, string BuildB) TwoBuilds(TempDirectory temp)
    {
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        string Build(string label, params string[] versions)
        {
            var input = Path.Combine(temp.Path, "input-" + label);
            Directory.CreateDirectory(input);
            foreach (var version in versions)
                File.WriteAllText(Path.Combine(input, version + ".yaml"), Manifest(version), Utf8);
            return workspace.Build(new PublisherBuildOptions(input, state)).BuildId;
        }

        var buildA = Build("a", "1.0.0");
        return (state, buildA, Build("b", "1.0.0", "2.0.0"));
    }

    private static PublisherPublishOptions Options(string state, string destination, string buildId, string version)
    {
        var msixPath = Path.Combine(Path.GetDirectoryName(state)!, $"staged-{Guid.NewGuid():N}.msix");
        _ = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            msixPath,
            "Pinget.Test.ReviewB",
            "CN=Pinget Review B Test",
            version,
            BuildId: buildId));
        return new PublisherPublishOptions(state, destination, msixPath, UnsignedDevelopment: true, BuildId: buildId);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pinget-publisher-review-b-tests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
