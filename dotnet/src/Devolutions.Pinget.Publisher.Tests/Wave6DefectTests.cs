using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

/// <summary>Wave 6 publisher regression tests (source index builder parity with the Rust publisher).</summary>
public sealed class Wave6DefectTests
{
    private const string Hash = "1111111111111111111111111111111111111111111111111111111111111111";

    private static string Singleton(string id, string extra) =>
        $"PackageIdentifier: {id}\nPackageVersion: 1.0.0\nPackageLocale: en-US\nPackageName: Wave Six\n{extra}"
        + "License: MIT\nShortDescription: Wave 6\nInstallers:\n  - Architecture: x64\n    InstallerType: exe\n    "
        + $"InstallerUrl: https://example.invalid/a.exe\n    InstallerSha256: {Hash}\nManifestType: singleton\n"
        + "ManifestVersion: 1.9.0\n";

    private static readonly UTF8Encoding Utf8 = new(false);

    private static ScanResult ScanFiles(TempDirectory temp, params (string Name, byte[] Bytes)[] files)
    {
        var input = Path.Combine(temp.Path, "input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        foreach (var (name, bytes) in files)
            File.WriteAllBytes(Path.Combine(input, name), bytes);
        return ManifestScanner.Scan(input, generateArtifacts: false);
    }

    private static string CanonicalManifest(TempDirectory temp, byte[] yaml) =>
        Utf8.GetString(Assert.Single(ScanFiles(temp, ("manifest.yaml", yaml)).Manifests).Content);

    // Shared with wave6_tests::p2_p3_p5_canonical_manifest_bytes_match_the_shared_cross_language_vector (Rust).
    private const string TrickyManifest =
        "\uFEFFPackageIdentifier: Pinget.Wave6\n"
        + "PackageVersion: 1.0.0\nPackageLocale: en-US\n"
        + "Publisher: \"Pinget \\U0001F389 \\\"Q\\\" \\\\ \\u00e9\\u2028\\x01\\x7f\\t\"\n"
        + "PackageName: Wave 6 \U0001F389\n"
        + "Author: 0x1F\nInstallationNotes: 12\n"
        + "HexValue: 0x1F\nOctalValue: 0o17\nNegativeHex: -0x10\nLeadingZero: 007\n"
        + "EmptyValue:\nUpperNull: NULL\nMixedNull: nUlL\nTitleBool: True\nMixedBool: tRuE\n"
        + "HugeFloat: 1e999\nFloatValue: 1.50\n"
        + "License: MIT\nShortDescription: Wave 6\n"
        + "Installers:\n  - Architecture: x64\n    InstallerType: exe\n"
        + "    InstallerUrl: https://example.invalid/a.exe\n"
        + "    InstallerSha256: 1111111111111111111111111111111111111111111111111111111111111111\n"
        + "ManifestType: singleton\nManifestVersion: 1.9.0\n";

    private const string TrickyCanonical =
        "{\"Author\":\"0x1F\",\"EmptyValue\":null,\"FloatValue\":1.5,\"HexValue\":31,\"HugeFloat\":\"1e999\","
        + "\"InstallationNotes\":\"12\",\"Installers\":[{\"Architecture\":\"x64\","
        + "\"InstallerSha256\":\"1111111111111111111111111111111111111111111111111111111111111111\","
        + "\"InstallerType\":\"exe\",\"InstallerUrl\":\"https://example.invalid/a.exe\"}],"
        + "\"LeadingZero\":\"007\",\"License\":\"MIT\",\"ManifestType\":\"singleton\",\"ManifestVersion\":\"1.9.0\","
        + "\"MixedBool\":\"tRuE\",\"MixedNull\":\"nUlL\",\"NegativeHex\":-16,\"OctalValue\":15,"
        + "\"PackageIdentifier\":\"Pinget.Wave6\",\"PackageLocale\":\"en-US\",\"PackageName\":\"Wave 6 \U0001F389\","
        + "\"PackageVersion\":\"1.0.0\","
        + "\"Publisher\":\"Pinget \U0001F389 \\\"Q\\\" \\\\ \u00E9\u2028\\u0001\u007f\\t\","
        + "\"ShortDescription\":\"Wave 6\",\"TitleBool\":true,\"UpperNull\":null}\n";

    [Fact]
    public void CanonicalManifest_TrickyInput_MatchesRustBytes()
    {
        using var temp = new TempDirectory();
        Assert.Equal(TrickyCanonical, CanonicalManifest(temp, Utf8.GetBytes(TrickyManifest)));
    }

    [Fact]
    public void CanonicalManifest_Utf8Bom_IsStrippedBeforeParsing()
    {
        using var temp = new TempDirectory();
        var plain = Utf8.GetBytes(Singleton("Pinget.Wave6", "Publisher: Pinget\n"));
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(plain).ToArray();
        Assert.Equal(CanonicalManifest(temp, plain), CanonicalManifest(temp, bom));
        var scan = ScanFiles(temp, ("manifest.yaml", bom));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(bom)).ToLowerInvariant(),
            scan.Inventory["manifest.yaml"]);
    }

    [Theory]
    [InlineData("+12", "12")]
    [InlineData("0b101", "5")]
    [InlineData("-007", "\"-007\"")]
    [InlineData("1e5", "100000.0")]
    [InlineData("inf", "\"inf\"")]
    [InlineData("'0x1F'", "\"0x1F\"")]
    public void UnknownScalars_AreTypedLikeSerdeYaml(string yaml, string expectedJson)
    {
        using var temp = new TempDirectory();
        var canonical = CanonicalManifest(temp, Utf8.GetBytes(Singleton("Pinget.Wave6", $"Publisher: Pinget\nZValue: {yaml}\n")));
        Assert.EndsWith($",\"ZValue\":{expectedJson}}}\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NonBmpPathsAndIdentifiers_UseRawUtf8InBuildIdAndVersionData()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(input);
        var yaml = Utf8.GetBytes(Singleton("Pinget.Wave6\U0001F389", "Publisher: Pinget\n"));
        File.WriteAllBytes(Path.Combine(input, "\U0001F389.yaml"), yaml);
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);

        var build = workspace.Build(new PublisherBuildOptions(input, state));

        var inventory = $"{{\"\U0001F389.yaml\":\"{Convert.ToHexString(SHA256.HashData(yaml)).ToLowerInvariant()}\"}}";
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Utf8.GetBytes("pinget-publisher-state-v2\n" + inventory))).ToLowerInvariant(),
            build.BuildId);
        Assert.True(workspace.Verify(build.BuildId).Valid);
        var feed = Path.Combine(build.SnapshotDirectory, "feed");
        var sidecar = Directory.GetFiles(feed, "versionData.mszyml", SearchOption.AllDirectories).Single();
        var versionData = Utf8.GetString(MsZipCompressor.Decompress(File.ReadAllBytes(sidecar)));
        Assert.Contains("rP: \"manifests/Pinget.Wave6\U0001F389/1.0.0/en-US/", versionData, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", versionData, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CorrelationValues_AreCaseFoldedForLowercaseCoreLookups()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(input);
        var extra = "Publisher: Pinget\nPackageFamilyName: Pinget.Wave6_ABCdef123\nAppsAndFeaturesEntries:\n  - ProductCode: '{AABB-CC}'\n    UpgradeCode: '{DDEE-FF}'\n";
        var yaml = Singleton("Pinget.Wave6", extra).Replace(
            "    InstallerSha256",
            "    ProductCode: '{0011-AABB}'\n    UpgradeCode: '{2233-CCDD}'\n    PackageFamilyName: Other.App_XYZ\n    InstallerSha256",
            StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(input, "manifest.yaml"), yaml, Utf8);
        var state = Path.Combine(temp.Path, "state");

        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));

        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(build.SnapshotDirectory, "Public", "index.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        database.Open();
        // The root PackageFamilyName is not inherited by an exe installer (wave 12, R12-5).
        Assert.Equal(new[] { "other.app_xyz" }, ReadColumn(database, "SELECT pfn FROM pfns2 ORDER BY pfn"));
        Assert.Equal(new[] { "{0011-aabb}", "{aabb-cc}" }, ReadColumn(database, "SELECT productcode FROM productcodes2 ORDER BY productcode"));
        Assert.Equal(new[] { "{2233-ccdd}", "{ddee-ff}" }, ReadColumn(database, "SELECT upgradecode FROM upgradecodes2 ORDER BY upgradecode"));
        // Same query shape as Repository.LookupIdentityMatchV2.
        using var lookup = database.CreateCommand();
        lookup.CommandText = "SELECT package FROM productcodes2 WHERE productcode = @v LIMIT 1";
        lookup.Parameters.AddWithValue("@v", "{0011-AABB}".ToLowerInvariant());
        Assert.Equal(1L, lookup.ExecuteScalar());
    }

    [Theory]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("Foo.B\u00E4r", true)]
    [InlineData("A.B", true)]
    [InlineData("Pinget.Test_1.(x86)", true)]
    [InlineData("a.b.c.d.e.f.g.h", true)]
    [InlineData("Single", false)]
    [InlineData("A..B", false)]
    [InlineData("A.B.", false)]
    [InlineData(".A.B", false)]
    [InlineData("A B.C", false)]
    [InlineData("A.B\t", false)]
    [InlineData("A.B/C", false)]
    [InlineData("A.B\\C", false)]
    [InlineData("A.B:C", false)]
    [InlineData("A.B*", false)]
    [InlineData("A.B?", false)]
    [InlineData("A.\"B\"", false)]
    [InlineData("A.<B>", false)]
    [InlineData("A.B|C", false)]
    [InlineData("A.B\u0001", false)]
    [InlineData("a.b.c.d.e.f.g.h.i", false)]
    [InlineData("A.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false)]
    [InlineData("NUL.Foo", false)]
    [InlineData("con.app", false)]
    [InlineData("COM1.Tool", false)]
    [InlineData("lpt9.x", false)]
    [InlineData("Aux.Win", false)]
    [InlineData("COM\u00B9.x", false)]
    public void PackageIdentifier_FollowsWinGetSchemaAndWindowsPathSafety(string id, bool valid) =>
        Assert.Equal(valid, ManifestScanner.IsValidPackageIdentifier(id));

    [Fact]
    public void PackageIdentifier_LongerThan128Characters_IsRejected()
    {
        var segment = new string('a', 32);
        Assert.False(ManifestScanner.IsValidPackageIdentifier($"{segment}.{segment}.{segment}.{segment}.e"));
    }

    [Theory]
    [InlineData("1.0.")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("aux.1")]
    [InlineData("LPT1")]
    [InlineData("com0")]
    public void PackageVersion_WindowsUnsafeNames_AreRejected(string version)
    {
        using var temp = new TempDirectory();
        var yaml = Singleton("Pinget.Wave6", "Publisher: Pinget\n").Replace("PackageVersion: 1.0.0", $"PackageVersion: {version}", StringComparison.Ordinal);
        Assert.Throws<PublisherException>(() => ScanFiles(temp, ("manifest.yaml", Utf8.GetBytes(yaml))));
    }

    [Theory]
    [InlineData("manifests/NUL/1.0/x.yaml", false)]
    [InlineData("manifests/A.B./1.0/x.yaml", false)]
    [InlineData("manifests/A.B /1.0/x.yaml", false)]
    [InlineData("manifests/A.B/1.0/en-US/0123456789abcdef.yaml", true)]
    public void ResolveContained_RejectsWindowsUnsafeComponents(string relative, bool valid)
    {
        var root = Path.GetFullPath("root");
        if (valid)
            Assert.StartsWith(root, PublisherWorkspace.ResolveContained(root, relative), StringComparison.OrdinalIgnoreCase);
        else
            Assert.Throws<PublisherException>(() => PublisherWorkspace.ResolveContained(root, relative));
    }

    [Fact]
    public void Publish_LeadingZeroVersion_RecordsCanonicalVersionAndRejectsSignedOrSpacedForms()
    {
        using var temp = new TempDirectory();
        var (state, buildId) = BuildFixture(temp);
        var destination = Path.Combine(temp.Path, "feed");

        var packaged = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            Path.Combine(temp.Path, "leading-zero.msix"),
            "Pinget.Test.Wave6",
            "CN=Pinget Wave6 Test",
            "01.0.0.0",
            BuildId: buildId));
        Assert.Equal("1.0.0.0", packaged.PackageVersion);
        PublisherPackaging.Publish(new PublisherPublishOptions(
            state,
            destination,
            packaged.MsixPath,
            UnsignedDevelopment: true,
            BuildId: buildId));

        var marker = JsonNode.Parse(File.ReadAllText(Path.Combine(destination, ".pinget-publisher.json")))!;
        Assert.Equal("1.0.0.0", marker["packageVersion"]!.GetValue<string>());
        Assert.True(PublisherPackaging.Publish(Options(state, destination, "1.0.0.0", buildId)).Valid);
        Assert.Throws<PublisherException>(() => Options(state, destination, "+1.0.0.1", buildId));
        Assert.Throws<PublisherException>(() => Options(state, destination, " 1.0.0.1", buildId));
    }

    [Fact]
    public void Publish_InterruptedPublishTemporaries_DoNotBlockNextPublish()
    {
        using var temp = new TempDirectory();
        var (state, buildId) = BuildFixture(temp);
        var destination = Path.Combine(temp.Path, "feed");
        PublisherPackaging.Publish(Options(state, destination, "1.0.0.0", buildId));

        var staging = Path.Combine(destination, $".publish-{buildId}.staging");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "source2.msix"), [1]);
        File.WriteAllBytes(Path.Combine(destination, $".source2.{buildId}.1.2.new"), [1]);
        File.WriteAllBytes(Path.Combine(destination, ".pinget-publisher.json.new"), [1]);
        const string guid = "0123456789abcdef0123456789abcdef";
        File.WriteAllBytes(Path.Combine(destination, $"source2.msix.{guid}.tmp"), [1]);
        File.WriteAllBytes(Path.Combine(destination, $".pinget-publisher.json.{guid}.tmp"), [1]);
        var sidecarDirectory = Directory.GetDirectories(Path.Combine(destination, "packages", "Pinget.Wave6")).Single();
        File.WriteAllBytes(Path.Combine(sidecarDirectory, $"versionData.mszyml.{guid}.tmp"), [1]);

        var result = PublisherPackaging.Publish(Options(state, destination, "1.0.0.1", buildId));

        Assert.True(result.Valid);
        Assert.True(PublisherPackaging.VerifyDestination(destination).Valid);
        Assert.False(Directory.Exists(staging));

        var unrelated = Path.Combine(destination, "unrelated.txt");
        File.WriteAllText(unrelated, "keep");
        Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(Options(state, destination, "1.0.0.2", buildId)));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void MultiFileManifests_RequireOneManifestVersion()
    {
        using var temp = new TempDirectory();
        var (version, locale, installer) = MultiFile(publisher: true);
        Assert.Single(ScanFiles(temp, ("v.yaml", Utf8.GetBytes(version)), ("l.yaml", Utf8.GetBytes(locale)), ("i.yaml", Utf8.GetBytes(installer)))
            .Manifests);
        var mixed = installer.Replace("ManifestVersion: 1.9.0", "ManifestVersion: 1.10.0", StringComparison.Ordinal);
        var error = Assert.Throws<PublisherException>(() =>
            ScanFiles(temp, ("v.yaml", Utf8.GetBytes(version)), ("l.yaml", Utf8.GetBytes(locale)), ("i.yaml", Utf8.GetBytes(mixed))));
        Assert.Contains("ManifestVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publisher_IsRequiredForSingletonAndMultiFileManifests()
    {
        using var temp = new TempDirectory();
        var error = Assert.Throws<PublisherException>(() =>
            ScanFiles(temp, ("m.yaml", Utf8.GetBytes(Singleton("Pinget.Wave6", string.Empty)))));
        Assert.Contains("Publisher", error.Message, StringComparison.Ordinal);
        Assert.Single(ScanFiles(temp, ("m.yaml", Utf8.GetBytes(Singleton("Pinget.Wave6", "Publisher: Pinget\n")))).Manifests);

        var (version, locale, installer) = MultiFile(publisher: false);
        error = Assert.Throws<PublisherException>(() =>
            ScanFiles(temp, ("v.yaml", Utf8.GetBytes(version)), ("l.yaml", Utf8.GetBytes(locale)), ("i.yaml", Utf8.GetBytes(installer))));
        Assert.Contains("Publisher", error.Message, StringComparison.Ordinal);
    }

    private static (string Version, string Locale, string Installer) MultiFile(bool publisher) =>
        ("PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nDefaultLocale: en-US\nManifestType: version\nManifestVersion: 1.9.0\n",
         "PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nPackageLocale: en-US\n" + (publisher ? "Publisher: Pinget\n" : string.Empty)
            + "PackageName: Wave Six\nLicense: MIT\nShortDescription: Wave 6\nManifestType: defaultLocale\nManifestVersion: 1.9.0\n",
         "PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nInstallers:\n  - Architecture: x64\n    InstallerType: exe\n"
            + $"    InstallerUrl: https://example.invalid/a.exe\n    InstallerSha256: {Hash}\nManifestType: installer\nManifestVersion: 1.9.0\n");

    private static (string State, string BuildId) BuildFixture(TempDirectory temp)
    {
        var input = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "manifest.yaml"), Singleton("Pinget.Wave6", "Publisher: Pinget\n"), Utf8);
        var state = Path.Combine(temp.Path, "state");
        return (state, new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)).BuildId);
    }

    private static PublisherPublishOptions Options(string state, string destination, string version, string buildId)
    {
        var msixPath = Path.Combine(Path.GetDirectoryName(state)!, $"staged-{Guid.NewGuid():N}.msix");
        _ = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            msixPath,
            "Pinget.Test.Wave6",
            "CN=Pinget Wave6 Test",
            version,
            BuildId: buildId));
        return new PublisherPublishOptions(state, destination, msixPath, UnsignedDevelopment: true, BuildId: buildId);
    }

    private static string[] ReadColumn(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pinget-publisher-wave6-tests", Guid.NewGuid().ToString("N"));
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
