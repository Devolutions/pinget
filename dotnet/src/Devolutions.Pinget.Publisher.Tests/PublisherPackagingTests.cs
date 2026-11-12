using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

public sealed class PublisherPackagingTests
{
    private const string PackageName = "Pinget.Publisher.Conformance";
    private const string Publisher = "CN=Pinget Publisher Conformance Test";

    [Theory]
    // Hyphens are valid in AppX package names; underscores are not.
    [InlineData("bad_name", Publisher, "1.0.0.0", "Package identity")]
    [InlineData(PackageName, Publisher, "1.2.3", "four numeric components")]
    [InlineData(PackageName, Publisher, "1.2.3.65536", "four numeric components")]
    public void Publish_InvalidIdentityOrPackageVersion_IsRejected(
        string packageName,
        string publisher,
        string version,
        string diagnostic)
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var msixOut = Path.Combine(temp.Path, "invalid.msix");

        var packageError = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(
            new PublisherPackageOptions(state, msixOut, packageName, publisher, version, BuildId: build.BuildId)));
        Assert.Contains(diagnostic, packageError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(msixOut));

        var craftedMsix = CraftMsix(
            temp,
            state,
            build.BuildId,
            manifest =>
            {
                XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                var identity = manifest.Root!.Element(foundation + "Identity")!;
                identity.SetAttributeValue("Name", packageName);
                identity.SetAttributeValue("Publisher", publisher);
                identity.SetAttributeValue("Version", version);
            });
        var destination = Path.Combine(temp.Path, "destination");
        var publishError = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, craftedMsix, UnsignedDevelopment: true, BuildId: build.BuildId)));

        Assert.Contains(diagnostic, publishError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Publish_LineFeedInPublisher_IsRejected()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var msixOut = Path.Combine(temp.Path, "invalid-publisher.msix");

        var packageError = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(
            new PublisherPackageOptions(state, msixOut, PackageName, "CN=Invalid\nPublisher", "1.0.0.0", BuildId: build.BuildId)));
        Assert.Contains("Publisher", packageError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(msixOut));

        var craftedMsix = CraftMsixWithRawManifestReplacement(
            temp,
            state,
            build.BuildId,
            $"Publisher=\"{Publisher}\"",
            "Publisher=\"CN=Invalid&#xA;Publisher\"");
        var destination = Path.Combine(temp.Path, "destination");
        var publishError = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, craftedMsix, UnsignedDevelopment: true, BuildId: build.BuildId)));

        Assert.Contains("Publisher", publishError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Package_WritesDeterministicUnsignedMsixAndReturnsMetadata()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var build = workspace.Build(new PublisherBuildOptions(input, state));
        var msixFirst = Path.Combine(temp.Path, "out", "source-a.msix");
        var msixSecond = Path.Combine(temp.Path, "out", "source-b.msix");

        var first = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            msixFirst,
            PackageName,
            Publisher,
            "1.0.0.0"));
        var second = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            msixSecond,
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: build.BuildId));

        var indexPath = Path.Combine(build.SnapshotDirectory, "Public", "index.db");
        var expectedIndexHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(indexPath))).ToLowerInvariant();
        var bytes = File.ReadAllBytes(msixFirst);
        Assert.Equal(bytes, File.ReadAllBytes(msixSecond));
        Assert.Equal(build.BuildId, first.BuildId);
        Assert.Equal(msixFirst, first.MsixPath);
        Assert.Equal(PackageName, first.PackageName);
        Assert.Equal(Publisher, first.Publisher);
        Assert.Equal("1.0.0.0", first.PackageVersion);
        Assert.Equal(expectedIndexHash, first.IndexHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), first.PackageHash);
        Assert.Equal(first.PackageHash, second.PackageHash);
        AssertMsixContents(msixFirst, indexPath, "1.0.0.0");
        Assert.Null(workspace.Status().LastPublishedBuildId);
    }

    [Fact]
    public void Package_PreflightDestinationValidation_RejectsInvalidTargetsWithoutMutatingDestination()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var firstBuild = workspace.Build(new PublisherBuildOptions(input, state));

        // Non-existent destination is accepted without creating it.
        var missingDestination = Path.Combine(temp.Path, "not-yet-created");
        var preflightNew = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            Path.Combine(temp.Path, "new-dest.msix"),
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: firstBuild.BuildId,
            DestinationDirectory: missingDestination));
        Assert.True(File.Exists(preflightNew.MsixPath));
        Assert.False(Directory.Exists(missingDestination));

        // Unowned non-empty destination with unrelated file is rejected without mutation.
        var unowned = Path.Combine(temp.Path, "unowned");
        Directory.CreateDirectory(unowned);
        File.WriteAllText(Path.Combine(unowned, "keep.txt"), "keep", new UTF8Encoding(false));
        var unownedMsix = Path.Combine(temp.Path, "unowned.msix");
        var unownedError = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            unownedMsix,
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: firstBuild.BuildId,
            DestinationDirectory: unowned)));
        Assert.Contains("non-empty unowned destination", unownedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(unownedMsix));
        Assert.Equal(new[] { "keep.txt" }, Directory.GetFileSystemEntries(unowned).Select(static path => Path.GetFileName(path)).ToArray());

        // Publish build 1 at 1.0.0.0 to an owned destination.
        var destination = Path.Combine(temp.Path, "published");
        _ = PublisherPackaging.Publish(UnsignedOptions(state, destination, "1.0.0.0", firstBuild.BuildId));
        var originalMarker = File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json"));
        var originalMsix = File.ReadAllBytes(Path.Combine(destination, "source2.msix"));

        var wrongIdentityMsix = Path.Combine(temp.Path, "wrong-id.msix");
        var identityError = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            wrongIdentityMsix,
            "Pinget.Publisher.Other",
            Publisher,
            "1.0.0.1",
            BuildId: firstBuild.BuildId,
            DestinationDirectory: destination)));
        Assert.Contains("different source package identity or publisher", identityError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(wrongIdentityMsix));

        var wrongPublisherMsix = Path.Combine(temp.Path, "wrong-pub.msix");
        var publisherError = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            wrongPublisherMsix,
            PackageName,
            "CN=Another Publisher",
            "1.0.0.1",
            BuildId: firstBuild.BuildId,
            DestinationDirectory: destination)));
        Assert.Contains("different source package identity or publisher", publisherError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(wrongPublisherMsix));

        File.AppendAllText(
            Path.Combine(input, "Pinget.Test.Matrix_1.10.0.locale.en-US.yaml"),
            "# second build\n",
            new UTF8Encoding(false));
        var secondBuild = workspace.Build(new PublisherBuildOptions(input, state));

        var downgradeMsix = Path.Combine(temp.Path, "downgrade.msix");
        var downgradeError = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            downgradeMsix,
            PackageName,
            Publisher,
            "0.9.9.9",
            BuildId: secondBuild.BuildId,
            DestinationDirectory: destination)));
        Assert.Contains("greater than published version", downgradeError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(downgradeMsix));

        var reusedVersionDifferentBuildMsix = Path.Combine(temp.Path, "same-version-diff-build.msix");
        var sameVersionError = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            reusedVersionDifferentBuildMsix,
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: secondBuild.BuildId,
            DestinationDirectory: destination)));
        Assert.Contains("already been used for different build content", sameVersionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(reusedVersionDifferentBuildMsix));

        // Same version + same buildId succeeds, and higher version + new buildId succeeds without mutating destination.
        var idempotent = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            Path.Combine(temp.Path, "idempotent.msix"),
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: firstBuild.BuildId,
            DestinationDirectory: destination));
        Assert.True(File.Exists(idempotent.MsixPath));

        var upgraded = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            Path.Combine(temp.Path, "upgraded.msix"),
            PackageName,
            Publisher,
            "1.0.0.1",
            BuildId: secondBuild.BuildId,
            DestinationDirectory: destination));
        Assert.True(File.Exists(upgraded.MsixPath));
        Assert.Equal(originalMarker, File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json")));
        Assert.Equal(originalMsix, File.ReadAllBytes(Path.Combine(destination, "source2.msix")));

        // Interrupted first publish (pendingCommit with previous: null) is treated as unowned without mutating destination.
        var interruptedFirst = Path.Combine(temp.Path, "interrupted-first");
        _ = PublisherPackaging.Publish(UnsignedOptions(state, interruptedFirst, "1.0.0.0", firstBuild.BuildId));
        var interruptedMarkerPath = Path.Combine(interruptedFirst, ".pinget-publisher.json");
        var interruptedMarker = JsonNode.Parse(File.ReadAllText(interruptedMarkerPath))!.AsObject();
        interruptedMarker["pendingCommit"] = new JsonObject { ["previous"] = null };
        File.WriteAllText(interruptedMarkerPath, interruptedMarker.ToJsonString(), new UTF8Encoding(false));
        var interruptedMarkerBytes = File.ReadAllBytes(interruptedMarkerPath);
        File.Delete(Path.Combine(interruptedFirst, "source2.msix"));

        var retryPackage = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            Path.Combine(temp.Path, "interrupted-retry.msix"),
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: firstBuild.BuildId,
            DestinationDirectory: interruptedFirst));
        Assert.True(File.Exists(retryPackage.MsixPath));
        Assert.Equal(interruptedMarkerBytes, File.ReadAllBytes(interruptedMarkerPath));
    }

    [Fact]
    public void Package_And_Publish_RejectOverlappingOrDirectoryMsixPaths()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var existingDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "dir-as-msix")).FullName;
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));

        var packageInState = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(
            new PublisherPackageOptions(state, Path.Combine(state, "out.msix"), PackageName, Publisher, "1.0.0.0", BuildId: build.BuildId)));
        Assert.Contains("state and msix directories must not overlap", packageInState.Message, StringComparison.Ordinal);

        var packageInDestination = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(
            new PublisherPackageOptions(
                state,
                Path.Combine(destination, "out.msix"),
                PackageName,
                Publisher,
                "1.0.0.0",
                BuildId: build.BuildId,
                DestinationDirectory: destination)));
        Assert.Contains("destination and msix directories must not overlap", packageInDestination.Message, StringComparison.Ordinal);

        var packageIsDirectory = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(
            new PublisherPackageOptions(state, existingDirectory, PackageName, Publisher, "1.0.0.0", BuildId: build.BuildId)));
        Assert.Contains("MSIX output path is a directory", packageIsDirectory.Message, StringComparison.Ordinal);

        var publishInState = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, Path.Combine(state, "source2.msix"), UnsignedDevelopment: true, BuildId: build.BuildId)));
        Assert.Contains("state and msix directories must not overlap", publishInState.Message, StringComparison.Ordinal);

        var publishInDestination = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, Path.Combine(destination, "source2.msix"), UnsignedDevelopment: true, BuildId: build.BuildId)));
        Assert.Contains("destination and msix directories must not overlap", publishInDestination.Message, StringComparison.Ordinal);

        var publishIsDirectory = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, existingDirectory, UnsignedDevelopment: true, BuildId: build.BuildId)));
        Assert.Contains("Source MSIX does not exist", publishIsDirectory.Message, StringComparison.Ordinal);

        var publishMissingFile = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, Path.Combine(temp.Path, "missing.msix"), UnsignedDevelopment: true, BuildId: build.BuildId)));
        Assert.Contains("Source MSIX does not exist", publishMissingFile.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Publish_UnsignedMsixWithoutUnsignedDevelopment_IsRejected()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "destination");
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var packaged = PublisherPackaging.Package(
            new PublisherPackageOptions(state, Path.Combine(temp.Path, "unsigned.msix"), PackageName, Publisher, "1.0.0.0", BuildId: build.BuildId));
        var fakeTool = CreateNoOpVerifyTool(temp.Path);

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, packaged.MsixPath, UnsignedDevelopment: false, PsignToolPath: fakeTool, BuildId: build.BuildId)));

        Assert.Contains("source MSIX is not signed", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        Assert.Null(new PublisherWorkspace(state).Status().LastPublishedBuildId);
        Assert.Empty(Directory.EnumerateDirectories(state, ".publish-*"));
    }

    [Theory]
    [InlineData("AppxSignature.p7x")]
    [InlineData("appxsignature.p7x")]
    [InlineData("AppxMetadata/CodeIntegrity.cat")]
    public void Publish_And_VerifyDestination_RejectSignedMsixInUnsignedDevelopmentMode(string signatureEntry)
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "destination");
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var unsignedPackage = PublisherPackaging.Package(
            new PublisherPackageOptions(state, Path.Combine(temp.Path, "unsigned.msix"), PackageName, Publisher, "1.0.0.0", BuildId: build.BuildId));
        var signedLookingMsix = AddZipEntry(unsignedPackage.MsixPath, Path.Combine(temp.Path, "signed-looking.msix"), signatureEntry, "PKCX"u8.ToArray());

        var publishError = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, signedLookingMsix, UnsignedDevelopment: true, BuildId: build.BuildId)));

        Assert.Contains("Cannot publish a signed MSIX with --unsigned-development", publishError.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        Assert.Null(new PublisherWorkspace(state).Status().LastPublishedBuildId);

        _ = PublisherPackaging.Publish(
            new PublisherPublishOptions(state, destination, unsignedPackage.MsixPath, UnsignedDevelopment: true, BuildId: build.BuildId));
        var publishedMsix = Path.Combine(destination, "source2.msix");
        File.Copy(signedLookingMsix, publishedMsix, overwrite: true);
        var markerPath = Path.Combine(destination, ".pinget-publisher.json");
        var marker = JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject();
        marker["artifacts"]!["source2.msix"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(publishedMsix))).ToLowerInvariant();
        File.WriteAllText(markerPath, marker.ToJsonString(), new UTF8Encoding(false));

        var verifyError = Assert.Throws<PublisherException>(() => PublisherPackaging.VerifyDestination(destination));
        Assert.Contains("Unsigned development destination contains a signed source2.msix.", verifyError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_UnsignedDevelopment_EnforcesMonotonicVersionAndRetainsOldArtifacts()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var workspace = new PublisherWorkspace(state);
        var firstBuild = workspace.Build(new PublisherBuildOptions(input, state));
        var options = UnsignedOptions(state, destination, "1.0.0.0", firstBuild.BuildId);

        var first = PublisherPackaging.Publish(options);
        var repeated = PublisherPackaging.Publish(options);
        Assert.True(first.Valid);
        Assert.True(repeated.Valid);
        Assert.Equal(first.BuildId, repeated.BuildId);
        using (var marker = JsonDocument.Parse(
                   File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json"))))
        {
            Assert.Equal(1, marker.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.False(marker.RootElement.TryGetProperty("artifactFormatVersion", out _));
        }
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        AssertMsixContents(
            Path.Combine(destination, "source2.msix"),
            Path.Combine(firstBuild.SnapshotDirectory, "Public", "index.db"),
            "1.0.0.0");

        var oldRootIndex = File.ReadAllBytes(Path.Combine(destination, "source2.msix"));
        var firstSnapshot = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(firstBuild.SnapshotDirectory, "snapshot.json")));
        var oldArtifacts = firstSnapshot.RootElement.GetProperty("artifacts")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        File.AppendAllText(
            Path.Combine(input, "Pinget.Test.Matrix_1.10.0.locale.en-US.yaml"),
            "# source content update\n",
            new UTF8Encoding(false));
        var secondBuild = workspace.Build(new PublisherBuildOptions(input, state));
        Assert.NotEqual(firstBuild.BuildId, secondBuild.BuildId);

        var lowerVersion = UnsignedOptions(state, destination, "0.9.9.9", secondBuild.BuildId);
        var downgrade = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(lowerVersion));
        Assert.Contains("greater", downgrade.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(oldRootIndex, File.ReadAllBytes(Path.Combine(destination, "source2.msix")));

        var nextVersion = UnsignedOptions(state, destination, "1.0.0.1", secondBuild.BuildId);
        var updated = PublisherPackaging.Publish(nextVersion);
        Assert.True(updated.Valid);
        Assert.Equal(secondBuild.BuildId, updated.BuildId);
        Assert.NotEqual(
            Convert.ToHexString(SHA256.HashData(oldRootIndex)),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(destination, "source2.msix")))));
        foreach (var relative in oldArtifacts)
            Assert.True(File.Exists(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
    }

    [Fact]
    public void Publish_UnownedDestinationWithUnrelatedFile_IsRejectedWithoutMutation()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        Directory.CreateDirectory(destination);
        var unrelatedPath = Path.Combine(destination, "keep-me.txt");
        var unrelatedBytes = Encoding.UTF8.GetBytes("unrelated destination content");
        File.WriteAllBytes(unrelatedPath, unrelatedBytes);

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            UnsignedOptions(state, destination, "1.0.0.0", build.BuildId)));

        Assert.Contains("non-empty unowned destination", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unrelatedBytes, File.ReadAllBytes(unrelatedPath));
        Assert.False(File.Exists(Path.Combine(destination, ".pinget-publisher.json")));
        Assert.False(File.Exists(Path.Combine(destination, "source2.msix")));
        Assert.Null(new PublisherWorkspace(state).Status().LastPublishedBuildId);
    }

    [Fact]
    public void Publish_DifferentIdentityOnOwnedDestination_IsRejectedWithoutMutation()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var workspace = new PublisherWorkspace(state);
        var build = workspace.Build(new PublisherBuildOptions(input, state));
        _ = PublisherPackaging.Publish(UnsignedOptions(state, destination, "1.0.0.0", build.BuildId));
        var oldIndex = File.ReadAllBytes(Path.Combine(destination, "source2.msix"));
        var oldMarker = File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json"));
        var changedIdentityPackage = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            Path.Combine(temp.Path, "other-identity.msix"),
            "Pinget.Publisher.Other",
            Publisher,
            "1.0.0.0",
            BuildId: build.BuildId));
        var changedIdentity = new PublisherPublishOptions(
            state,
            destination,
            changedIdentityPackage.MsixPath,
            UnsignedDevelopment: true,
            BuildId: build.BuildId);

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(changedIdentity));

        Assert.Contains("different source package identity or publisher", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(oldIndex, File.ReadAllBytes(Path.Combine(destination, "source2.msix")));
        Assert.Equal(oldMarker, File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json")));
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
    }

    [Fact]
    public void Publish_UntrackedFileInOwnedDestination_IsRejectedAndPreserved()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var workspace = new PublisherWorkspace(state);
        var firstBuild = workspace.Build(new PublisherBuildOptions(input, state));
        _ = PublisherPackaging.Publish(UnsignedOptions(state, destination, "1.0.0.0", firstBuild.BuildId));
        var oldIndex = File.ReadAllBytes(Path.Combine(destination, "source2.msix"));
        var oldMarker = File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json"));
        var unrelatedPath = Path.Combine(destination, "keep-me.txt");
        var unrelatedBytes = Encoding.UTF8.GetBytes("unrelated owned-destination content");
        File.WriteAllBytes(unrelatedPath, unrelatedBytes);
        var verifyError = Assert.Throws<PublisherException>(
            () => PublisherPackaging.VerifyDestination(destination));
        Assert.Contains("untracked artifact", verifyError.Message, StringComparison.OrdinalIgnoreCase);
        File.AppendAllText(
            Path.Combine(input, "Pinget.Test.Matrix_1.10.0.locale.en-US.yaml"),
            "# source content update\n",
            new UTF8Encoding(false));
        var secondBuild = workspace.Build(new PublisherBuildOptions(input, state));

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(
            UnsignedOptions(state, destination, "1.0.0.1", secondBuild.BuildId)));

        Assert.Contains("untracked artifact", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unrelatedBytes, File.ReadAllBytes(unrelatedPath));
        Assert.Equal(oldIndex, File.ReadAllBytes(Path.Combine(destination, "source2.msix")));
        Assert.Equal(oldMarker, File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json")));
        var verifyErrorAfterRejection = Assert.Throws<PublisherException>(
            () => PublisherPackaging.VerifyDestination(destination));
        Assert.Contains("untracked artifact", verifyErrorAfterRejection.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Publish_MissingSigningTool_DoesNotExposeAnIndexOrRecordPublication()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var packaged = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            Path.Combine(temp.Path, "source.msix"),
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: build.BuildId));
        var options = new PublisherPublishOptions(
            state,
            destination,
            packaged.MsixPath,
            UnsignedDevelopment: false,
            PsignToolPath: Path.Combine(temp.Path, "missing", "psign-tool.exe"),
            BuildId: build.BuildId);

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(options));

        Assert.Contains("psign-tool does not exist at", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(destination, "source2.msix")));
        Assert.False(File.Exists(Path.Combine(destination, ".pinget-publisher.json")));
        Assert.Null(new PublisherWorkspace(state).Status().LastPublishedBuildId);
        Assert.Empty(Directory.EnumerateDirectories(state, ".publish-*"));

        var retry = PublisherPackaging.Publish(UnsignedOptions(state, destination, "1.0.0.0", build.BuildId));
        Assert.True(retry.Valid);
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        Assert.Equal(build.BuildId, new PublisherWorkspace(state).Status().LastPublishedBuildId);
    }

    [Fact]
    public void Publish_InitialPostCopyMsixVerificationFailure_CleansArtifactsAndAllowsRetry()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var snapshotFeed = Path.Combine(build.SnapshotDirectory, "feed");
        var copiedArtifactPaths = Directory.EnumerateFiles(snapshotFeed, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(snapshotFeed, path))
            .ToArray();
        Assert.NotEmpty(copiedArtifactPaths);

        var reachedAfterCopy = false;
        var failedPublish = UnsignedOptions(state, destination, "1.0.0.0", build.BuildId);
        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(failedPublish, (feed, stagedPackage) =>
        {
            reachedAfterCopy = true;
            foreach (var relativePath in copiedArtifactPaths)
            {
                var copied = Path.Combine(feed, relativePath);
                Assert.True(File.Exists(copied), $"Artifact must exist before the injected failure: {relativePath}");
                Assert.Equal(File.ReadAllBytes(Path.Combine(snapshotFeed, relativePath)), File.ReadAllBytes(copied));
            }
            Assert.False(File.Exists(Path.Combine(feed, ".pinget-publisher.json")), "No ownership marker before promotion.");
            Assert.False(File.Exists(Path.Combine(feed, "source2.msix")), "No root package before promotion.");
            // Swap the staged package for one whose identity disagrees with the ownership marker,
            // so the real post-copy MSIX verification fails.
            RewriteStagedPackageIdentity(stagedPackage, "Pinget.Publisher.WrongIdentity");
        }));

        Assert.True(reachedAfterCopy, "The failure must occur after the verified artifact copies.");
        Assert.Contains("MSIX identity does not match", error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.Null(new PublisherWorkspace(state).Status().LastPublishedBuildId);
        Assert.Empty(Directory.EnumerateDirectories(state, ".publish-*"));
        foreach (var relativePath in copiedArtifactPaths)
        {
            var destinationPath = Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.False(File.Exists(destinationPath), $"Failed initial publication left copied artifact '{relativePath}'.");
        }

        var retry = PublisherPackaging.Publish(UnsignedOptions(state, destination, "1.0.0.0", build.BuildId));

        Assert.True(retry.Valid);
        Assert.Equal(build.BuildId, retry.BuildId);
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        Assert.Equal(build.BuildId, new PublisherWorkspace(state).Status().LastPublishedBuildId);
    }

    [Fact]
    public void Publish_FailedSignatureTool_DoesNotExposeAnIndexOrRecordPublication()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var where = Path.Combine(Environment.SystemDirectory, "where.exe");
        Assert.True(File.Exists(where), "Windows where.exe is required for the controlled failing-tool case.");
        var packaged = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            Path.Combine(temp.Path, "source.msix"),
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: build.BuildId));
        var options = new PublisherPublishOptions(
            state,
            destination,
            packaged.MsixPath,
            UnsignedDevelopment: false,
            PsignToolPath: where,
            BuildId: build.BuildId);

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(options));

        Assert.Contains("MSIX digest verification with psign-tool failed", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(destination, "source2.msix")));
        Assert.Null(new PublisherWorkspace(state).Status().LastPublishedBuildId);
        Assert.Empty(Directory.EnumerateDirectories(state, ".publish-*"));

        var retry = PublisherPackaging.Publish(
            UnsignedOptions(state, destination, "1.0.0.0", build.BuildId));
        Assert.True(retry.Valid);
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        Assert.Equal(build.BuildId, new PublisherWorkspace(state).Status().LastPublishedBuildId);
    }

    [Fact]
    public void Publish_RootIndexPromotionFailure_PreservesPreviousIndexAndOwnershipMarker()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var destination = Path.Combine(temp.Path, "published");
        var workspace = new PublisherWorkspace(state);
        var firstBuild = workspace.Build(new PublisherBuildOptions(input, state));
        var firstOptions = UnsignedOptions(state, destination, "1.0.0.0", firstBuild.BuildId);
        _ = PublisherPackaging.Publish(firstOptions);
        var originalIndex = File.ReadAllBytes(Path.Combine(destination, "source2.msix"));
        var originalMarker = File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json"));

        var changedManifest = Path.Combine(input, "Pinget.Test.Matrix_1.10.0.locale.en-US.yaml");
        var oldInput = File.ReadAllText(changedManifest);
        var updatedInput = oldInput.Replace(
            "PackageName: Matrix Package",
            "PackageName: Matrix Package Updated",
            StringComparison.Ordinal);
        Assert.NotEqual(oldInput, updatedInput);
        File.WriteAllText(changedManifest, updatedInput, new UTF8Encoding(false));
        var secondBuild = workspace.Build(new PublisherBuildOptions(input, state));
        var next = UnsignedOptions(state, destination, "1.0.0.1", secondBuild.BuildId);

        using (var lockedIndex = new FileStream(
                   Path.Combine(destination, "source2.msix"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            Assert.Throws<UnauthorizedAccessException>(() => PublisherPackaging.Publish(next));
            Assert.True(lockedIndex.Length > 0);
        }

        Assert.Equal(originalIndex, File.ReadAllBytes(Path.Combine(destination, "source2.msix")));
        Assert.Equal(originalMarker, File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json")));
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        Assert.Equal(firstBuild.BuildId, workspace.Status().LastPublishedBuildId);

        var retry = PublisherPackaging.Publish(next);
        Assert.True(retry.Valid);
        Assert.Equal(secondBuild.BuildId, retry.BuildId);
        Assert.NotEqual(originalIndex, File.ReadAllBytes(Path.Combine(destination, "source2.msix")));
        Assert.NotEqual(originalMarker, File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json")));
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        Assert.Equal(secondBuild.BuildId, workspace.Status().LastPublishedBuildId);
    }

    [Fact]
    public void Publish_DestinationLockSerializesDifferentStateRootsAndAllowsRetry()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var firstState = Path.Combine(temp.Path, "first-state");
        var secondState = Path.Combine(temp.Path, "second-state");
        var destination = Path.Combine(temp.Path, "published");
        var firstBuild = new PublisherWorkspace(firstState)
            .Build(new PublisherBuildOptions(input, firstState));
        var secondBuild = new PublisherWorkspace(secondState)
            .Build(new PublisherBuildOptions(input, secondState));
        Assert.Equal(firstBuild.BuildId, secondBuild.BuildId);
        _ = PublisherPackaging.Publish(UnsignedOptions(firstState, destination, "1.0.0.0", firstBuild.BuildId));
        var oldIndex = File.ReadAllBytes(Path.Combine(destination, "source2.msix"));
        var oldMarker = File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json"));
        var siblingLock = destination + ".pinget-publisher.lock";

        using (var heldLock = new FileStream(
                   siblingLock,
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var error = Assert.ThrowsAny<Exception>(() => PublisherPackaging.Publish(
                UnsignedOptions(secondState, destination, "1.0.0.1", secondBuild.BuildId)));
            Assert.Contains("lock", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(oldIndex, File.ReadAllBytes(Path.Combine(destination, "source2.msix")));
            Assert.Equal(oldMarker, File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json")));
            Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        }

        var retry = PublisherPackaging.Publish(
            UnsignedOptions(secondState, destination, "1.0.0.1", secondBuild.BuildId));
        Assert.True(retry.Valid);
        Assert.Equal(secondBuild.BuildId, retry.BuildId);
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        Assert.Equal(secondBuild.BuildId, new PublisherWorkspace(secondState).Status().LastPublishedBuildId);
    }

    private static PublisherPublishOptions UnsignedOptions(
        string state,
        string destination,
        string version,
        string buildId)
    {
        var msixPath = Path.Combine(Path.GetDirectoryName(state)!, $"staged-{Guid.NewGuid():N}.msix");
        _ = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            msixPath,
            PackageName,
            Publisher,
            version,
            BuildId: buildId));
        return new PublisherPublishOptions(
            state,
            destination,
            msixPath,
            UnsignedDevelopment: true,
            BuildId: buildId);
    }

    private static void AssertMsixContents(string packagePath, string expectedIndexPath, string version)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var indexEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("Public/index.db"));
        using (var entryStream = indexEntry.Open())
        using (var actualIndex = new MemoryStream())
        {
            entryStream.CopyTo(actualIndex);
            Assert.Equal(File.ReadAllBytes(expectedIndexPath), actualIndex.ToArray());
        }

        var manifestEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("AppxManifest.xml"));
        using var manifestStream = manifestEntry.Open();
        var manifest = XDocument.Load(manifestStream);
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
        var identity = Assert.IsType<XElement>(manifest.Root?.Element(foundation + "Identity"));
        Assert.Equal(PackageName, (string?)identity.Attribute("Name"));
        Assert.Equal(Publisher, (string?)identity.Attribute("Publisher"));
        Assert.Equal(version, (string?)identity.Attribute("Version"));

        var extension = Assert.Single(manifest.Descendants(uap3 + "AppExtension"));
        Assert.Equal("com.microsoft.winget.source", (string?)extension.Attribute("Name"));
        Assert.Equal("IndexDB", (string?)extension.Attribute("Id"));
        Assert.Equal("Public", (string?)extension.Attribute("PublicFolder"));
        Assert.Equal(
            "windows.appExtension",
            (string?)extension.Parent?.Attribute("Category"));
    }

    private static void RewriteStagedPackageIdentity(string packagePath, string identityName)
    {
        var parts = new List<(string Name, byte[] Data)>();
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName is "AppxBlockMap.xml" or "[Content_Types].xml")
                    continue;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var data = buffer.ToArray();
                if (entry.FullName == "AppxManifest.xml")
                {
                    var manifest = XDocument.Parse(Encoding.UTF8.GetString(data));
                    XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                    manifest.Root!.Element(foundation + "Identity")!.SetAttributeValue("Name", identityName);
                    data = Encoding.UTF8.GetBytes(manifest.ToString(SaveOptions.DisableFormatting));
                }
                parts.Add((entry.FullName, data));
            }
        }
        File.WriteAllBytes(packagePath, MsixPackageWriter.Write(parts));
    }

    private static string CraftMsix(TempDirectory temp, string state, string buildId, Action<XDocument> mutateManifest)
    {
        var templatePath = Path.Combine(temp.Path, $"template-{Guid.NewGuid():N}.msix");
        _ = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            templatePath,
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: buildId));
        var parts = new List<(string Name, byte[] Data)>();
        using (var archive = ZipFile.OpenRead(templatePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName is "AppxBlockMap.xml" or "[Content_Types].xml")
                    continue;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var data = buffer.ToArray();
                if (entry.FullName == "AppxManifest.xml")
                {
                    var manifest = XDocument.Parse(Encoding.UTF8.GetString(data));
                    mutateManifest(manifest);
                    data = Encoding.UTF8.GetBytes(manifest.ToString(SaveOptions.DisableFormatting));
                }
                parts.Add((entry.FullName, data));
            }
        }
        var craftedPath = Path.Combine(temp.Path, $"crafted-{Guid.NewGuid():N}.msix");
        File.WriteAllBytes(craftedPath, MsixPackageWriter.Write(parts));
        return craftedPath;
    }

    private static string CraftMsixWithRawManifestReplacement(
        TempDirectory temp,
        string state,
        string buildId,
        string oldValue,
        string newValue)
    {
        var templatePath = Path.Combine(temp.Path, $"template-{Guid.NewGuid():N}.msix");
        _ = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            templatePath,
            PackageName,
            Publisher,
            "1.0.0.0",
            BuildId: buildId));
        var parts = new List<(string Name, byte[] Data)>();
        using (var archive = ZipFile.OpenRead(templatePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName is "AppxBlockMap.xml" or "[Content_Types].xml")
                    continue;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var data = buffer.ToArray();
                if (entry.FullName == "AppxManifest.xml")
                {
                    var xml = Encoding.UTF8.GetString(data);
                    var replaced = xml.Replace(oldValue, newValue, StringComparison.Ordinal);
                    Assert.NotEqual(xml, replaced);
                    data = new UTF8Encoding(false).GetBytes(replaced);
                }
                parts.Add((entry.FullName, data));
            }
        }
        var craftedPath = Path.Combine(temp.Path, $"crafted-{Guid.NewGuid():N}.msix");
        File.WriteAllBytes(craftedPath, MsixPackageWriter.Write(parts));
        return craftedPath;
    }

    private static string AddZipEntry(string sourceMsixPath, string outputMsixPath, string entryName, byte[] entryData)
    {
        File.Copy(sourceMsixPath, outputMsixPath, overwrite: true);
        using var archive = ZipFile.Open(outputMsixPath, ZipArchiveMode.Update);
        using var stream = archive.CreateEntry(entryName).Open();
        stream.Write(entryData);
        return outputMsixPath;
    }

    private static string CreateNoOpVerifyTool(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(directory, "noop-psign-tool.cmd");
            File.WriteAllText(cmd, "@echo off\r\nexit /b 0\r\n", new UTF8Encoding(false));
            return cmd;
        }
        var sh = Path.Combine(directory, "noop-psign-tool");
        File.WriteAllText(sh, "#!/bin/sh\nexit 0\n", new UTF8Encoding(false));
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private static string CopyFixture(string name, string destination)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        DirectoryCopy(source, destination);
        return destination;
    }

    private static void DirectoryCopy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            DirectoryCopy(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pinget-publisher-packaging-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
