using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

public sealed class PublisherWorkspaceTests
{
    [Fact]
    public void Build_MultiFileVersionsLocalesAndInstallerMetadata_WritesCompleteV2Snapshot()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);

        var build = workspace.Build(new PublisherBuildOptions(input, state));

        Assert.Equal(1, build.PackageCount);
        Assert.Equal(2, build.ManifestCount);
        Assert.Equal(0, build.ChangedFiles);
        Assert.Equal(0, build.RemovedFiles);
        Assert.Equal(build.BuildId, workspace.Status().LastBuildId);
        Assert.True(workspace.Verify(build.BuildId).Valid);

        var snapshot = build.SnapshotDirectory;
        var indexPath = Path.Combine(snapshot, "Public", "index.db");
        var feed = Path.Combine(snapshot, "feed");
        Assert.True(File.Exists(indexPath));
        using (var snapshotMetadata = JsonDocument.Parse(
                   File.ReadAllBytes(Path.Combine(snapshot, "snapshot.json"))))
        {
            Assert.Equal(1, snapshotMetadata.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(2, snapshotMetadata.RootElement.GetProperty("artifactFormatVersion").GetInt32());
            var inventory = snapshotMetadata.RootElement.GetProperty("inventory");
            Assert.Equal(
                BuildIdForSalt(inventory, "pinget-publisher-state-v2\n"),
                build.BuildId);
            Assert.NotEqual(
                BuildIdForSalt(inventory, "pinget-publisher-state-v1\n"),
                build.BuildId);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(indexPath))).ToLowerInvariant(),
                snapshotMetadata.RootElement.GetProperty("indexHash").GetString());
        }
        using (var stateDatabase = OpenReadOnly(Path.Combine(state, "publisher.db")))
        {
            Assert.Equal("2", ReadMetadataValue(stateDatabase, "artifactFormatVersion"));
            Assert.Equal("1", ReadMetadataValue(stateDatabase, "schemaVersion"));
            using var command = stateDatabase.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        using var database = OpenReadOnly(indexPath);

        Assert.Equal(
            ("Pinget.Test.Matrix", "Matrix Package", "1.10.0"),
            ReadPackage(database));
        Assert.Equal(
            new[] { "Matrix", "matrice", "publisher-conformance" },
            ReadMappedValues(database, "tags2", "tag", "tags2_map", "tag", "Pinget.Test.Matrix"));
        Assert.Equal(
            new[] { "Matrix", "OtherCmd", "matrix-cli" },
            ReadMappedValues(database, "commands2", "command", "commands2_map", "command", "Pinget.Test.Matrix"));
        Assert.Equal(
            new[] { "{00112233-4455-6677-8899-aabbccddeeff}" },
            ReadCorrelation(database, "productcodes2", "productcode", "Pinget.Test.Matrix"));
        Assert.Equal(
            new[] { "{11223344-5566-7788-99aa-bbccddeeff00}" },
            ReadCorrelation(database, "upgradecodes2", "upgradecode", "Pinget.Test.Matrix"));
        Assert.Equal(
            new[] { "pinget.matrix_1234567890abc" },
            ReadCorrelation(database, "pfns2", "pfn", "Pinget.Test.Matrix"));
        Assert.Equal(
            new[] { "matrice", "matrixdesktop", "matrixpackage" },
            ReadCorrelation(database, "norm_names2", "norm_name", "Pinget.Test.Matrix"));
        Assert.Equal(
            new[] { "pingetlocalizationlab", "pingettestfixtures" },
            ReadCorrelation(database, "norm_publishers2", "norm_publisher", "Pinget.Test.Matrix"));

        using (var command = database.CreateCommand())
        {
            command.CommandText = "SELECT arp_min_version, arp_max_version, hash FROM packages WHERE id=$id";
            command.Parameters.AddWithValue("$id", "Pinget.Test.Matrix");
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            // WinGet stores the latest version's ARP range on the package row.
            Assert.Equal("1.10.0", reader.GetString(0));
            Assert.Equal("1.10.0", reader.GetString(1));
            var sidecarHash = (byte[])reader[2];
            Assert.Equal(32, sidecarHash.Length);
            var hex = Convert.ToHexString(sidecarHash).ToLowerInvariant();
            var relative = $"packages/Pinget.Test.Matrix/{hex[..8]}/versionData.mszyml";
            var sidecar = File.ReadAllBytes(Path.Combine(feed, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(hex, Convert.ToHexString(SHA256.HashData(sidecar)).ToLowerInvariant());
        }

        var manifests = Directory.GetFiles(Path.Combine(feed, "manifests"), "*.yaml", SearchOption.AllDirectories);
        Assert.Equal(2, manifests.Length);
        var englishCurrent = manifests.Single(path =>
            path.Contains($"{Path.DirectorySeparatorChar}1.10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        using var mergedCurrent = JsonDocument.Parse(File.ReadAllBytes(englishCurrent));
        var currentRoot = mergedCurrent.RootElement;
        Assert.Equal("en-US", currentRoot.GetProperty("PackageLocale").GetString());
        Assert.Equal("Matrix Package", currentRoot.GetProperty("PackageName").GetString());
        Assert.Equal("Pinget Test Fixtures", currentRoot.GetProperty("Publisher").GetString());
        Assert.Equal("1.10.0", currentRoot.GetProperty("PackageVersion").GetString());
        var frenchLocalization = Assert.Single(currentRoot.GetProperty("Localization").EnumerateArray());
        Assert.Equal("fr-FR", frenchLocalization.GetProperty("PackageLocale").GetString());
        Assert.Equal("Matrice", frenchLocalization.GetProperty("PackageName").GetString());
        Assert.Equal("Pinget Localization Lab", frenchLocalization.GetProperty("Publisher").GetString());
        Assert.False(frenchLocalization.TryGetProperty("ManifestType", out _));
        Assert.Equal(2, currentRoot.GetProperty("Installers").GetArrayLength());
        Assert.Equal(
            "1111111111111111111111111111111111111111111111111111111111111111",
            currentRoot.GetProperty("Installers")[0].GetProperty("InstallerSha256").GetString());
        Assert.Equal(
            "2222222222222222222222222222222222222222222222222222222222222222",
            currentRoot.GetProperty("Installers")[1].GetProperty("InstallerSha256").GetString());
        var olderEnglishManifest = Directory.GetFiles(
            Path.Combine(feed, "manifests", "Pinget.Test.Matrix", "1.9.0", "en-US"),
            "*.yaml").Single();
        using var olderManifest = JsonDocument.Parse(File.ReadAllBytes(olderEnglishManifest));
        Assert.Equal(
            "0000000000000000000000000000000000000000000000000000000000000000",
            olderManifest.RootElement.GetProperty("Installers")[0]
                .GetProperty("InstallerSha256").GetString());
        Assert.Equal(
            "Pinget.Test.Runtime",
            currentRoot.GetProperty("Installers")[0]
                .GetProperty("Dependencies")
                .GetProperty("PackageDependencies")[0]
                .GetProperty("PackageIdentifier")
                .GetString());
        Assert.Equal("/quiet", currentRoot.GetProperty("Installers")[0]
            .GetProperty("InstallerSwitches").GetProperty("Silent").GetString());
        Assert.Contains(
            "portable",
            currentRoot.GetProperty("Installers").EnumerateArray()
                .Select(installer => installer.GetProperty("InstallerType").GetString()));
    }

    [Fact]
    public void Build_UsesDeclaredDefaultLocaleWhenAnotherLocaleSortsFirst()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "manifests");
        Directory.CreateDirectory(input);
        const string packageId = "Pinget.Test.LocaleOrder";
        WriteLocaleOrderManifests(input, packageId);
        var state = Path.Combine(temp.Path, "state");

        var build = new PublisherWorkspace(state)
            .Build(new PublisherBuildOptions(input, state));

        var versionDirectory = Path.Combine(
            build.SnapshotDirectory, "feed", "manifests", packageId, "1.0.0");
        var mergedPaths = Directory.GetFiles(versionDirectory, "*.yaml", SearchOption.AllDirectories);
        Assert.Single(mergedPaths);
        using var merged = JsonDocument.Parse(File.ReadAllBytes(mergedPaths[0]));
        var root = merged.RootElement;
        Assert.Equal("en-US", root.GetProperty("PackageLocale").GetString());
        Assert.Equal("Locale Order English", root.GetProperty("PackageName").GetString());
        var localizations = root.GetProperty("Localization");
        var localization = Assert.Single(localizations.EnumerateArray());
        Assert.Equal("de-DE", localization.GetProperty("PackageLocale").GetString());
        Assert.Equal("Locale Reihenfolge", localization.GetProperty("PackageName").GetString());
        Assert.False(localization.TryGetProperty("ManifestType", out _));
        Assert.False(localization.TryGetProperty("ManifestVersion", out _));
        Assert.Equal(
            "Pinget.Test.LocaleOrder|Locale Order English|1.0.0",
            ReadPackageFromSnapshot(build.SnapshotDirectory));
        Assert.True(new PublisherWorkspace(state).Verify(build.BuildId).Valid);
    }

    [Fact]
    public void Build_PreservesSchemaStringScalarsInMergedManifest()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "manifests");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "scalar.yaml"), """
            PackageIdentifier: Pinget.Test.ScalarTypes
            PackageVersion: 1.0
            PackageLocale: en-US
            Publisher: Pinget Test Fixtures
            PackageName: Scalar Types
            DisplayVersion: 2024
            SignatureSha256: 0000000000000000000000000000000000000000000000000000000000000000
            GenericNumber: 1.0
            Small: 0.5
            Tiny: 1e-6
            Threshold: 1e-5
            Large: 1e16
            NegativeZero: -0.0
            MaxUInt: 18446744073709551615
            Installers: [{Architecture: x64, InstallerType: exe, InstallerUrl: 'https://example.invalid/scalar.exe', InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000}]
            ManifestType: singleton
            ManifestVersion: 1.9.0
            """, new UTF8Encoding(false));
        var state = Path.Combine(temp.Path, "state");

        var build = new PublisherWorkspace(state)
            .Build(new PublisherBuildOptions(input, state));

        var mergedPath = Directory.GetFiles(
            Path.Combine(build.SnapshotDirectory, "feed", "manifests"),
            "*.yaml",
            SearchOption.AllDirectories).Single();
        using var merged = JsonDocument.Parse(File.ReadAllBytes(mergedPath));
        var root = merged.RootElement;
        Assert.Equal("1.0", root.GetProperty("PackageVersion").GetString());
        Assert.Equal("2024", root.GetProperty("DisplayVersion").GetString());
        Assert.Equal(
            "0000000000000000000000000000000000000000000000000000000000000000",
            root.GetProperty("SignatureSha256").GetString());
        Assert.Equal(
            "0000000000000000000000000000000000000000000000000000000000000000",
            root.GetProperty("Installers")[0].GetProperty("InstallerSha256").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("GenericNumber").ValueKind);
        Assert.Equal(1.0, root.GetProperty("GenericNumber").GetDouble());
        Assert.Equal(0.5, root.GetProperty("Small").GetDouble());
        Assert.Equal(1e-6, root.GetProperty("Tiny").GetDouble());
        Assert.Equal(1e-5, root.GetProperty("Threshold").GetDouble());
        Assert.Equal(1e16, root.GetProperty("Large").GetDouble());
        Assert.Equal(-0.0, root.GetProperty("NegativeZero").GetDouble());
        Assert.Equal(ulong.MaxValue, root.GetProperty("MaxUInt").GetUInt64());
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", "1.0.1")]
    [InlineData("1.0", "1.0.0-alpha", "1.0")]
    public void Build_VersionOrderingHandlesMissingComponentsAndSuffixes(
        string firstVersion,
        string secondVersion,
        string expectedLatestVersion)
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "manifests");
        Directory.CreateDirectory(input);
        const string packageId = "Pinget.Test.VersionOrdering";
        File.WriteAllText(
            Path.Combine(input, "first.yaml"),
            SingletonManifest(packageId, firstVersion),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(input, "second.yaml"),
            SingletonManifest(packageId, secondVersion),
            new UTF8Encoding(false));
        var state = Path.Combine(temp.Path, "state");

        var build = new PublisherWorkspace(state)
            .Build(new PublisherBuildOptions(input, state));

        using var database = OpenReadOnly(
            Path.Combine(build.SnapshotDirectory, "Public", "index.db"));
        Assert.Equal(
            (packageId, packageId, expectedLatestVersion),
            ReadPackage(database));
    }

    [Fact]
    public void Build_EquivalentInputTreesHaveSameBuildIdentity()
    {
        using var temp = new TempDirectory();
        var firstInput = CopyFixture("multi", Path.Combine(temp.Path, "first"));
        var secondInput = CopyFixture("multi", Path.Combine(temp.Path, "second"));
        var firstState = Path.Combine(temp.Path, "state-a");
        var secondState = Path.Combine(temp.Path, "state-b");

        var first = new PublisherWorkspace(firstState)
            .Build(new PublisherBuildOptions(firstInput, firstState));
        var second = new PublisherWorkspace(secondState)
            .Build(new PublisherBuildOptions(secondInput, secondState));

        Assert.Equal(first.BuildId, second.BuildId);
        Assert.Equal(ReadPackageFromSnapshot(first.SnapshotDirectory), ReadPackageFromSnapshot(second.SnapshotDirectory));
    }

    [Fact]
    public void Diff_RawMetadataEditsAdditionsAndRemovals_AreReportedByRelativePath()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var first = workspace.Build(new PublisherBuildOptions(input, state));
        Assert.Empty(workspace.Diff(input).Added);
        Assert.Empty(workspace.Diff(input).Changed);
        Assert.Empty(workspace.Diff(input).Removed);

        var changed = Path.Combine(input, "Pinget.Test.Matrix_1.10.0.locale.en-US.yaml");
        File.AppendAllText(changed, "# raw-byte change\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(input, "Pinget.Test.Added.yaml"), SingletonManifest("Pinget.Test.Added", "1.0.0"));
        var removed = Path.Combine(input, "Pinget.Test.Matrix_1.9.0.yaml");
        File.Delete(removed);

        var diff = workspace.Diff(input);
        Assert.False(diff.Published);
        Assert.Equal(new[] { "Pinget.Test.Added.yaml" }, diff.Added);
        Assert.Equal(new[] { "Pinget.Test.Matrix_1.10.0.locale.en-US.yaml" }, diff.Changed);
        Assert.Equal(new[] { "Pinget.Test.Matrix_1.9.0.yaml" }, diff.Removed);

        var second = workspace.Build(new PublisherBuildOptions(input, state));
        Assert.NotEqual(first.BuildId, second.BuildId);
        Assert.True(workspace.Verify(second.BuildId).Valid);
        Assert.Equal(2, workspace.Status().BuildCount);
    }

    [Theory]
    [InlineData("missing-version", "requires exactly one version")]
    [InlineData("duplicate-version", "requires exactly one version")]
    [InlineData("unsupported-schema", "Unsupported ManifestVersion")]
    [InlineData("invalid-version", "Unsafe PackageVersion")]
    [InlineData("unsafe-identifier", "Unsafe PackageIdentifier")]
    [InlineData("duplicate-locale", "Duplicate locale")]
    [InlineData("duplicate-singleton", "Duplicate singleton")]
    [InlineData("missing-installer", "installer")]
    [InlineData("empty-default-locale", "DefaultLocale")]
    [InlineData("duplicate-default-locale", "duplicate")]
    [InlineData("missing-singleton-locale", "PackageLocale")]
    public void Build_InvalidManifestPartition_IsRejected(string scenario, string expectedMessage)
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        ApplyInvalidManifestMutation(input, scenario);
        var state = Path.Combine(temp.Path, "state");

        var error = Assert.Throws<PublisherException>(() =>
            new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(state, "publisher.db")));
        Assert.Empty(Directory.Exists(Path.Combine(state, "builds"))
            ? Directory.GetDirectories(Path.Combine(state, "builds"))
            : []);
    }

    [Fact]
    public void Build_RejectsCaseConflictingPackageIdentity()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        File.WriteAllText(
            Path.Combine(input, "case-conflict.yaml"),
            SingletonManifest("pinget.test.matrix", "2.0.0"),
            new UTF8Encoding(false));
        var state = Path.Combine(temp.Path, "state");

        var error = Assert.Throws<PublisherException>(() =>
            new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)));

        Assert.Contains("casing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOnlyOperations_MissingState_DoNotInitializeIt()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "not-created");
        var workspace = new PublisherWorkspace(state);

        Assert.Throws<PublisherException>(workspace.Status);
        Assert.Throws<PublisherException>(() => workspace.Diff(input));

        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void Build_FutureUserVersionIsRejectedWithoutChangingTheStateDatabase()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        _ = workspace.Build(new PublisherBuildOptions(input, state));
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(state, "publisher.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "PRAGMA user_version=999;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var stateBytes = File.ReadAllBytes(Path.Combine(state, "publisher.db"));

        var error = Assert.ThrowsAny<Exception>(() =>
            workspace.Build(new PublisherBuildOptions(input, state)));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(stateBytes, File.ReadAllBytes(Path.Combine(state, "publisher.db")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("1")]
    [InlineData("999")]
    public void Build_UnsupportedArtifactFormatStateIsRejectedWithoutMutation(string formatVersion)
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        _ = workspace.Build(new PublisherBuildOptions(input, state));
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(state, "publisher.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = formatVersion == "missing"
                ? "DELETE FROM metadata WHERE name='artifactFormatVersion';"
                : "UPDATE metadata SET value=$value WHERE name='artifactFormatVersion';";
            if (formatVersion != "missing")
                command.Parameters.AddWithValue("$value", formatVersion);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();
        var databasePath = Path.Combine(state, "publisher.db");
        var legacyStateBytes = File.ReadAllBytes(databasePath);

        var error = Assert.ThrowsAny<Exception>(() =>
            workspace.Build(new PublisherBuildOptions(input, state)));

        Assert.Contains("artifactFormatVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--state", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(legacyStateBytes, File.ReadAllBytes(databasePath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("1")]
    [InlineData("999")]
    public void Verify_UnsupportedArtifactFormatSnapshotIsRejectedWithoutMutation(string formatVersion)
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var build = workspace.Build(new PublisherBuildOptions(input, state));
        var metadataPath = Path.Combine(build.SnapshotDirectory, "snapshot.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        Assert.Equal(2, metadata["artifactFormatVersion"]!.GetValue<int>());
        if (formatVersion == "missing")
            metadata.Remove("artifactFormatVersion");
        else
            metadata["artifactFormatVersion"] = int.Parse(formatVersion, System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(metadataPath, metadata.ToJsonString(), new UTF8Encoding(false));
        var legacySnapshotBytes = File.ReadAllBytes(metadataPath);

        var error = Assert.ThrowsAny<Exception>(() => workspace.Verify(build.BuildId));

        Assert.Contains("format", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(legacySnapshotBytes, File.ReadAllBytes(metadataPath));
    }

    [Fact]
    public void Build_ForeignSqliteDatabaseIsRejectedWithoutInitializationOrMutation()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        Directory.CreateDirectory(state);
        var databasePath = Path.Combine(state, "publisher.db");
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated_owner(value TEXT); INSERT INTO unrelated_owner VALUES('preserve-me');";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var originalDatabase = File.ReadAllBytes(databasePath);

        Assert.ThrowsAny<Exception>(() =>
            new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)));

        Assert.Equal(originalDatabase, File.ReadAllBytes(databasePath));
        Assert.Empty(Directory.Exists(Path.Combine(state, "builds"))
            ? Directory.GetDirectories(Path.Combine(state, "builds"))
            : []);
    }

    [Fact]
    public void Build_StaleStagingDirectoryDoesNotBlockAValidSnapshot()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var staleStaging = Path.Combine(state, "builds", ".staging-stale-owned-attempt");
        Directory.CreateDirectory(staleStaging);
        var staleMarker = Path.Combine(staleStaging, "preserve.txt");
        File.WriteAllText(staleMarker, "not owned by this invocation", new UTF8Encoding(false));

        var build = new PublisherWorkspace(state)
            .Build(new PublisherBuildOptions(input, state));

        Assert.True(new PublisherWorkspace(state).Verify(build.BuildId).Valid);
        Assert.Equal("not owned by this invocation", File.ReadAllText(staleMarker));
        Assert.True(Directory.Exists(staleStaging));
    }

    [Fact]
    public void Verify_UnrecordedCopiedSnapshotIsRejected()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var build = workspace.Build(new PublisherBuildOptions(input, state));
        var copiedId = new string('f', 64);
        var copiedSnapshot = Path.Combine(state, "builds", copiedId);
        DirectoryCopy(build.SnapshotDirectory, copiedSnapshot);
        var metadataPath = Path.Combine(copiedSnapshot, "snapshot.json");
        var metadata = File.ReadAllText(metadataPath);
        File.WriteAllText(
            metadataPath,
            metadata.Replace(build.BuildId, copiedId, StringComparison.Ordinal),
            new UTF8Encoding(false));

        var error = Assert.ThrowsAny<Exception>(() => workspace.Verify(copiedId));

        Assert.Contains("build", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(workspace.Verify(build.BuildId).Valid);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("build-id")]
    [InlineData("inventory")]
    public void Verify_SnapshotSchemaIdentityAndInventoryTamperingAreRejected(string mutation)
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var build = workspace.Build(new PublisherBuildOptions(input, state));
        var snapshotMetadataPath = Path.Combine(build.SnapshotDirectory, "snapshot.json");
        var snapshot = JsonNode.Parse(File.ReadAllText(snapshotMetadataPath))!.AsObject();
        switch (mutation)
        {
            case "schema":
                snapshot["schemaVersion"] = 99;
                break;
            case "build-id":
                snapshot["buildId"] = new string('f', 64);
                break;
            case "inventory":
                var inventory = snapshot["inventory"]!.AsObject();
                var first = inventory.First();
                inventory[first.Key] = new string('0', 64);
                break;
        }
        File.WriteAllText(snapshotMetadataPath, snapshot.ToJsonString(), new UTF8Encoding(false));

        Assert.ThrowsAny<Exception>(() => workspace.Verify(build.BuildId));
    }

    [Fact]
    public void Verify_UntrackedFeedArtifactIsRejected()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var build = workspace.Build(new PublisherBuildOptions(input, state));
        File.WriteAllText(
            Path.Combine(build.SnapshotDirectory, "feed", "unexpected.txt"),
            "not listed in snapshot inventory",
            new UTF8Encoding(false));

        var error = Assert.ThrowsAny<Exception>(() => workspace.Verify(build.BuildId));

        Assert.True(
            error.Message.Contains("inventory", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("artifact", StringComparison.OrdinalIgnoreCase),
            $"Unexpected untracked-artifact diagnostic: {error.Message}");
    }

    [Fact]
    public void ReadOnlyOperations_RejectReparsePointsInInputAndStateRoots()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var build = workspace.Build(new PublisherBuildOptions(input, state));
        var inputLink = Path.Combine(temp.Path, "manifests-link");
        var stateLink = Path.Combine(temp.Path, "state-link");
        Directory.CreateSymbolicLink(inputLink, input);
        Directory.CreateSymbolicLink(stateLink, state);
        try
        {
            Assert.ThrowsAny<Exception>(() => workspace.Diff(inputLink));
            Assert.ThrowsAny<Exception>(() => new PublisherWorkspace(stateLink).Status());
            Assert.ThrowsAny<Exception>(() => new PublisherWorkspace(stateLink).Verify(build.BuildId));
        }
        finally
        {
            Directory.Delete(inputLink);
            Directory.Delete(stateLink);
        }
    }

    [Fact]
    public void Diff_RejectsInputRootNestedInsidePublisherState()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var build = workspace.Build(new PublisherBuildOptions(input, state));

        var error = Assert.ThrowsAny<Exception>(() =>
            workspace.Diff(Path.Combine(state, "builds", build.BuildId, "feed")));

        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(workspace.Verify(build.BuildId).Valid);
    }

    [Fact]
    public void Build_RejectsReparsePointWithinManifestRoot()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var external = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(external);
        var link = Path.Combine(input, "linked");
        Directory.CreateSymbolicLink(link, external);
        try
        {
            var error = Assert.Throws<PublisherException>(() =>
                new PublisherWorkspace(Path.Combine(temp.Path, "state"))
                    .Build(new PublisherBuildOptions(input, Path.Combine(temp.Path, "state"))));
            Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Build_InputAndStateRootsMayNotOverlap()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(input, "publisher-state");

        var error = Assert.Throws<PublisherException>(() =>
            new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)));

        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void Build_ExistingWriterLock_PreventsConcurrentMutation()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        Directory.CreateDirectory(state);
        using var heldLock = new FileStream(
            Path.Combine(state, ".publisher.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var error = Assert.Throws<PublisherException>(() =>
            new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)));

        Assert.Contains("lock", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(state, "publisher.db")));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("manifest")]
    [InlineData("sidecar")]
    [InlineData("index")]
    public void Verify_CorruptedSnapshotComponent_IsRejected(string component)
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var snapshot = build.SnapshotDirectory;

        switch (component)
        {
            case "snapshot":
                File.WriteAllText(Path.Combine(snapshot, "snapshot.json"), "{not-json");
                break;
            case "manifest":
                MutateFirstFile(Path.Combine(snapshot, "feed", "manifests"), bytes => bytes.Concat(new byte[] { 0x20 }).ToArray());
                break;
            case "sidecar":
                MutateFirstFile(Path.Combine(snapshot, "feed", "packages"), bytes => bytes.Concat(new byte[] { 0x20 }).ToArray());
                break;
            case "index":
                File.WriteAllBytes(Path.Combine(snapshot, "Public", "index.db"), new byte[] { 0x01, 0x02, 0x03 });
                break;
        }

        Assert.ThrowsAny<Exception>(() => new PublisherWorkspace(state).Verify(build.BuildId));
    }

    [Fact]
    public void Status_CorruptStateDatabase_IsRejectedWithoutReinitializingIt()
    {
        using var temp = new TempDirectory();
        var input = CopyFixture("multi", Path.Combine(temp.Path, "manifests"));
        var state = Path.Combine(temp.Path, "state");
        new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        var database = Path.Combine(state, "publisher.db");
        File.WriteAllBytes(database, Encoding.ASCII.GetBytes("not a sqlite database"));

        Assert.ThrowsAny<Exception>(() => new PublisherWorkspace(state).Status());
        Assert.Equal("not a sqlite database", Encoding.ASCII.GetString(File.ReadAllBytes(database)));
    }

    private static string ReadPackageFromSnapshot(string snapshot)
    {
        using var database = OpenReadOnly(Path.Combine(snapshot, "Public", "index.db"));
        var (id, name, version) = ReadPackage(database);
        return $"{id}|{name}|{version}";
    }

    private static string ReadMetadataValue(SqliteConnection database, string key)
    {
        using var command = database.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE name=$name";
        command.Parameters.AddWithValue("$name", key);
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException($"Missing publisher state metadata '{key}'.");
    }

    private static string BuildIdForSalt(JsonElement inventory, string salt)
    {
        var inventoryJson = "{"
            + string.Join(
                ",",
                inventory.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property =>
                        $"{JsonSerializer.Serialize(property.Name)}:{JsonSerializer.Serialize(property.Value.GetString() ?? throw new InvalidOperationException("Inventory values must be strings."))}"))
            + "}";
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(salt + inventoryJson)))
            .ToLowerInvariant();
    }

    private static (string Id, string Name, string Version) ReadPackage(SqliteConnection database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "SELECT id, name, latest_version FROM packages";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var package = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
        Assert.False(reader.Read());
        return package;
    }

    private static string[] ReadMappedValues(
        SqliteConnection database,
        string valueTable,
        string valueColumn,
        string mapTable,
        string mapColumn,
        string packageId)
    {
        using var command = database.CreateCommand();
        command.CommandText = $"""
            SELECT valuesTable.{valueColumn}
              FROM {valueTable} AS valuesTable
              JOIN {mapTable} AS valueMap ON valueMap.{mapColumn}=valuesTable.rowid
              JOIN packages AS package ON package.rowid=valueMap.package
             WHERE package.id=$id
             ORDER BY valuesTable.{valueColumn}
            """;
        command.Parameters.AddWithValue("$id", packageId);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static string[] ReadCorrelation(SqliteConnection database, string table, string column, string packageId)
    {
        using var command = database.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} AS correlation JOIN packages AS package ON package.rowid=correlation.package WHERE package.id=$id ORDER BY {column}";
        command.Parameters.AddWithValue("$id", packageId);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
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

    private static void ApplyInvalidManifestMutation(string input, string scenario)
    {
        var version = Path.Combine(input, "Pinget.Test.Matrix_1.10.0.version.yaml");
        var french = Path.Combine(input, "Pinget.Test.Matrix_1.10.0.locale.fr-FR.yaml");
        switch (scenario)
        {
            case "missing-version":
                File.Delete(version);
                break;
            case "duplicate-version":
                File.Copy(version, Path.Combine(input, "duplicate-version.yaml"));
                break;
            case "unsupported-schema":
                File.WriteAllText(version, File.ReadAllText(version).Replace("ManifestVersion: 1.9.0", "ManifestVersion: 9.9.9", StringComparison.Ordinal));
                break;
            case "invalid-version":
                File.WriteAllText(version, File.ReadAllText(version).Replace("PackageVersion: 1.10.0", "PackageVersion: ../escape", StringComparison.Ordinal));
                break;
            case "unsafe-identifier":
                File.WriteAllText(version, File.ReadAllText(version).Replace("Pinget.Test.Matrix", "../escape", StringComparison.Ordinal));
                break;
            case "duplicate-locale":
                File.Copy(french, Path.Combine(input, "duplicate-fr.yaml"));
                break;
            case "duplicate-singleton":
                File.Copy(Path.Combine(input, "Pinget.Test.Matrix_1.9.0.yaml"), Path.Combine(input, "duplicate-singleton.yaml"));
                break;
            case "missing-installer":
                File.Delete(Path.Combine(input, "Pinget.Test.Matrix_1.10.0.installer.yaml"));
                break;
            case "empty-default-locale":
                File.WriteAllText(version, File.ReadAllText(version).Replace(
                    "DefaultLocale: en-US", "DefaultLocale: ''", StringComparison.Ordinal));
                break;
            case "duplicate-default-locale":
                File.Copy(
                    Path.Combine(input, "Pinget.Test.Matrix_1.10.0.locale.en-US.yaml"),
                    Path.Combine(input, "duplicate-default-locale.yaml"));
                File.WriteAllText(
                    Path.Combine(input, "duplicate-default-locale.yaml"),
                    File.ReadAllText(Path.Combine(input, "duplicate-default-locale.yaml"))
                        .Replace("PackageLocale: en-US", "PackageLocale: en-us", StringComparison.Ordinal));
                break;
            case "missing-singleton-locale":
                var missingLocale = Regex.Replace(
                    SingletonManifest("Pinget.Test.MissingLocale", "1.0.0"),
                    @"(?m)^PackageLocale: en-US\r?\n",
                    "",
                    RegexOptions.CultureInvariant);
                Assert.DoesNotContain("PackageLocale:", missingLocale, StringComparison.Ordinal);
                File.WriteAllText(
                    Path.Combine(input, "missing-locale.yaml"),
                    missingLocale);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    private static string SingletonManifest(string id, string version) => $$"""
        PackageIdentifier: {{id}}
        PackageVersion: {{version}}
        PackageLocale: en-US
        Publisher: Pinget Test Fixtures
        PackageName: {{id}}
        License: MIT
        ShortDescription: temporary fixture
        Installers:
          - Architecture: x64
            InstallerType: exe
            InstallerUrl: https://example.invalid/fixture.exe
            InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000
        ManifestType: singleton
        ManifestVersion: 1.9.0
        """;

    private static void WriteLocaleOrderManifests(string input, string id)
    {
        foreach (var (suffix, locale, name, type) in new[]
                 {
                     ("locale.en-US", "en-US", "Locale Order English", "defaultLocale"),
                     ("locale.de-DE", "de-DE", "Locale Reihenfolge", "locale"),
                 })
        {
            File.WriteAllText(Path.Combine(input, $"{id}_1.0.0.{suffix}.yaml"), $$"""
                PackageIdentifier: {{id}}
                PackageVersion: 1.0.0
                PackageLocale: {{locale}}
                Publisher: Pinget Test Fixtures
                PackageName: {{name}}
                License: MIT
                ShortDescription: locale-order fixture
                ManifestType: {{type}}
                ManifestVersion: 1.9.0
                """, new UTF8Encoding(false));
        }

        File.WriteAllText(Path.Combine(input, $"{id}_1.0.0.version.yaml"), $$"""
            PackageIdentifier: {{id}}
            PackageVersion: 1.0.0
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.9.0
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(input, $"{id}_1.0.0.installer.yaml"), $$"""
            PackageIdentifier: {{id}}
            PackageVersion: 1.0.0
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.invalid/locale-order.exe
                InstallerSha256: 1111111111111111111111111111111111111111111111111111111111111111
            ManifestType: installer
            ManifestVersion: 1.9.0
            """, new UTF8Encoding(false));
    }

    private static void MutateFirstFile(string directory, Func<byte[], byte[]> mutation)
    {
        var file = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        File.WriteAllBytes(file, mutation(File.ReadAllBytes(file)));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pinget-publisher-tests",
                Guid.NewGuid().ToString("N"));
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
