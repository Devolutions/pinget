using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
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
        _ = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var options = new PublisherPublishOptions(
            state,
            Path.Combine(temp.Path, "destination"),
            packageName,
            publisher,
            version,
            UnsignedDevelopment: true);

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(options));

        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(options.DestinationDirectory));
    }

    [Fact]
    public void Publish_LineFeedInPublisher_IsRejected()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        _ = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var options = new PublisherPublishOptions(
            state,
            Path.Combine(temp.Path, "destination"),
            PackageName,
            "CN=Invalid\nPublisher",
            "1.0.0.0",
            UnsignedDevelopment: true);

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(options));

        Assert.Contains("Publisher", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(options.DestinationDirectory));
    }

    [Fact]
    public void Publish_RequiresExplicitUnsignedDevelopmentOrSigningConfiguration()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        _ = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var options = new PublisherPublishOptions(
            state,
            Path.Combine(temp.Path, "destination"),
            PackageName,
            Publisher,
            "1.0.0.0");

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(options));

        Assert.Contains("Choose exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(options.DestinationDirectory));
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
        var changedIdentity = new PublisherPublishOptions(
            state,
            destination,
            "Pinget.Publisher.Other",
            Publisher,
            "1.0.0.0",
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
        const string password = "safe-test-password";
        var pfx = CreateSigningPfx(Path.Combine(temp.Path, "signer.pfx"), Publisher, password);
        const string passwordVariable = "PINGET_PUBLISHER_TEST_MISSING_TOOL_PASSWORD";
        var previousPassword = Environment.GetEnvironmentVariable(passwordVariable);
        Environment.SetEnvironmentVariable(passwordVariable, password);
        try
        {
            var options = new PublisherPublishOptions(
                state,
                destination,
                PackageName,
                Publisher,
                "1.0.0.0",
                PfxPath: pfx,
                PasswordEnvironmentVariable: passwordVariable,
                PsignToolPath: Path.Combine(temp.Path, "missing", "psign-tool.exe"),
                BuildId: build.BuildId);

            var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(options));

            Assert.Contains("psign-tool does not exist at", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(destination, "source2.msix")));
            Assert.False(File.Exists(Path.Combine(destination, ".pinget-publisher.json")));
            Assert.Null(new PublisherWorkspace(state).Status().LastPublishedBuildId);
            Assert.Empty(Directory.EnumerateDirectories(state, ".publish-*"));

            var retry = PublisherPackaging.Publish(UnsignedOptions(state, destination, "1.0.0.0", build.BuildId));
            Assert.True(retry.Valid);
            Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
            Assert.Equal(build.BuildId, new PublisherWorkspace(state).Status().LastPublishedBuildId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, previousPassword);
        }
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
        const string password = "safe-test-password";
        var pfx = CreateSigningPfx(Path.Combine(temp.Path, "signer.pfx"), Publisher, password);
        const string passwordVariable = "PINGET_PUBLISHER_TEST_PFX_PASSWORD";
        var previousPassword = Environment.GetEnvironmentVariable(passwordVariable);
        Environment.SetEnvironmentVariable(passwordVariable, password);
        try
        {
            var options = new PublisherPublishOptions(
                state,
                destination,
                PackageName,
                Publisher,
                "1.0.0.0",
                PfxPath: pfx,
                PasswordEnvironmentVariable: passwordVariable,
                PsignToolPath: where,
                BuildId: build.BuildId);

            var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(options));

            Assert.Contains("MSIX signing with psign-tool failed", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(password, error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(destination, "source2.msix")));
            Assert.Null(new PublisherWorkspace(state).Status().LastPublishedBuildId);

            var retry = PublisherPackaging.Publish(
                UnsignedOptions(state, destination, "1.0.0.0", build.BuildId));
            Assert.True(retry.Valid);
            Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
            Assert.Equal(build.BuildId, new PublisherWorkspace(state).Status().LastPublishedBuildId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, previousPassword);
        }
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
        string buildId) =>
        new(
            state,
            destination,
            PackageName,
            Publisher,
            version,
            UnsignedDevelopment: true,
            BuildId: buildId);

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

    private static string CreateSigningPfx(string path, string subject, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.3") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
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
