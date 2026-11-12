using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

/// <summary>Wave 12 publisher regression tests (publisher round 2), mirroring <c>wave12_tests.rs</c>.</summary>
public sealed class Wave12DefectTests
{
    private const string Hash = "1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly UTF8Encoding Utf8 = new(false);

    private static string Manifest(string id, string version, string root, string installers) =>
        $"PackageIdentifier: {id}\nPackageVersion: {version}\nPackageLocale: en-US\nPackageName: Wave Twelve\n"
        + $"Publisher: Pinget\nLicense: MIT\nShortDescription: Wave 12\n{root}Installers:\n{installers}"
        + "ManifestType: singleton\nManifestVersion: 1.9.0\n";

    private static string Installer(string installerType, string extra) =>
        $"  - Architecture: x64\n    InstallerType: {installerType}\n    InstallerUrl: https://example.invalid/a.exe\n    "
        + $"InstallerSha256: {Hash}\n{extra}";

    private static string Entries(params string[] versions) =>
        "    AppsAndFeaturesEntries:\n" + string.Concat(versions.Select(version => $"      - DisplayVersion: '{version}'\n"));

    private static string WriteInput(TempDirectory temp, params (string Name, string Contents)[] files)
    {
        var input = Path.Combine(temp.Path, "input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        foreach (var (name, contents) in files)
            File.WriteAllText(Path.Combine(input, name), contents, Utf8);
        return input;
    }

    private static IndexedPackage ScanSingle(string yaml)
    {
        using var temp = new TempDirectory();
        return Assert.Single(ManifestScanner.Scan(WriteInput(temp, ("manifest.yaml", yaml))).Packages);
    }

    [Fact]
    public void ArpRange_UsesEffectiveEntriesOfSupportedInstallerTypes()
    {
        // Installer entries override the root entries (WinGet copies root entries only when missing).
        var overriding = ScanSingle(Manifest(
            "Pinget.Wave12",
            "1.0.0",
            "AppsAndFeaturesEntries:\n  - DisplayVersion: '1.0'\n",
            Installer("exe", "    AppsAndFeaturesEntries:\n      - DisplayVersion: 1.0.5\n")
            + Installer("msi", "    AppsAndFeaturesEntries:\n      - DisplayVersion: 1.0.5\n")));
        Assert.Equal(("1.0.5", "1.0.5"), (overriding.ArpMinimumVersion, overriding.ArpMaximumVersion));

        // Portable and msstore installers never contribute an ARP version range.
        foreach (var installerType in new[] { "portable", "msstore" })
        {
            var unsupported = ScanSingle(Manifest(
                "Pinget.Wave12", "1.0.0", string.Empty,
                Installer(installerType, "    AppsAndFeaturesEntries:\n      - DisplayVersion: 2.0.0\n")));
            Assert.Null(unsupported.ArpMinimumVersion);
            Assert.Null(unsupported.ArpMaximumVersion);
        }

        // Root entries are inherited by an exe installer, and a zip uses its nested installer type.
        var inherited = ScanSingle(Manifest(
            "Pinget.Wave12",
            "1.0.0",
            "NestedInstallerType: msi\nAppsAndFeaturesEntries:\n  - DisplayVersion: 3.0.0\n",
            Installer("exe", string.Empty) + Installer("zip", string.Empty) + Installer("portable", string.Empty)));
        Assert.Equal(("3.0.0", "3.0.0"), (inherited.ArpMinimumVersion, inherited.ArpMaximumVersion));

        // msix supports ranges but does not inherit root entries.
        var msix = ScanSingle(Manifest(
            "Pinget.Wave12",
            "1.0.0",
            "AppsAndFeaturesEntries:\n  - DisplayVersion: 4.0.0\n",
            Installer("msix", "    AppsAndFeaturesEntries:\n      - DisplayVersion: 4.1.0\n")));
        Assert.Equal(("4.1.0", "4.1.0"), (msix.ArpMinimumVersion, msix.ArpMaximumVersion));
    }

    [Fact]
    public void SystemReferences_RootDefaultsAreInheritedOnlyWhenMissingAndTypeAppropriate()
    {
        var overridden = ScanSingle(Manifest(
            "Pinget.Wave12",
            "1.0.0",
            "ProductCode: '{AAAA}'\nPackageFamilyName: Root.Family_abc\nAppsAndFeaturesEntries:\n  - DisplayName: Root Name\n    "
            + "Publisher: Root Publisher\n    ProductCode: '{RRRR}'\n    UpgradeCode: '{UUUU}'\n",
            Installer("exe", "    ProductCode: '{BBBB}'\n    AppsAndFeaturesEntries:\n      - DisplayName: Own Name\n")
            + Installer("msi", "    ProductCode: '{CCCC}'\n    AppsAndFeaturesEntries:\n      - Publisher: Own Publisher\n")));
        Assert.Empty(overridden.Pfns);
        Assert.Equal(new[] { "{bbbb}", "{cccc}" }, overridden.ProductCodes);
        Assert.Empty(overridden.UpgradeCodes);
        Assert.Equal(new[] { "ownname", "wavetwelve" }, overridden.NormalizedNames);
        Assert.Equal(new[] { "ownpublisher", "pinget" }, overridden.NormalizedPublishers);

        var inherited = ScanSingle(Manifest(
            "Pinget.Wave12",
            "1.0.0",
            "ProductCode: '{AAAA}'\nPackageFamilyName: Root.Family_abc\n",
            Installer("msix", string.Empty) + Installer("exe", string.Empty)));
        Assert.Equal(new[] { "root.family_abc" }, inherited.Pfns);
        Assert.Equal(new[] { "{aaaa}" }, inherited.ProductCodes);
    }

    [Fact]
    public void OverlappingArpRangesAcrossVersions_FailTheBuild()
    {
        using var temp = new TempDirectory();
        var overlapping = WriteInput(
            temp,
            ("a.yaml", Manifest("Pinget.Wave12", "1.0", string.Empty, Installer("exe", Entries("1.0")))),
            ("b.yaml", Manifest("Pinget.Wave12", "2.0", string.Empty, Installer("exe", Entries("1.0", "2.0")))));
        var error = Assert.Throws<PublisherException>(() => ManifestScanner.Scan(overlapping));
        // Same text as wave12_tests::r12_6_overlapping_arp_ranges_across_versions_fail_the_build (Rust).
        Assert.Equal("ARP version ranges overlap for Pinget.Wave12: 1.0 [1.0, 1.0] and 2.0 [1.0, 2.0]", error.Message);

        var disjoint = WriteInput(
            temp,
            ("a.yaml", Manifest("Pinget.Wave12", "1.0", string.Empty, Installer("exe", Entries("1.0")))),
            ("b.yaml", Manifest("Pinget.Wave12", "2.0", string.Empty, Installer("exe", Entries("1.5", "2.0")))),
            ("c.yaml", Manifest("Pinget.Wave12", "3.0", string.Empty, Installer("exe", string.Empty))));
        Assert.Single(ManifestScanner.Scan(disjoint).Packages);
    }

    [Fact]
    public void Build_PackageRows_UseLatestArpRangeAndOrdinalIgnoreCaseOrder()
    {
        using var temp = new TempDirectory();
        var input = WriteInput(
            temp,
            ("a.yaml", Manifest("Pinget.Wave12", "1.0", string.Empty, Installer("exe", Entries("1.0")))),
            ("b.yaml", Manifest("Pinget.Wave12", "2.0", string.Empty, Installer("exe", string.Empty))),
            ("c.yaml", Manifest("Pinget.A_b", "1.0", string.Empty, Installer("exe", string.Empty))),
            ("d.yaml", Manifest("Pinget.Ab", "1.0", string.Empty, Installer("exe", string.Empty))));
        var state = Path.Combine(temp.Path, "state");
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));

        using var database = OpenReadOnly(Path.Combine(build.SnapshotDirectory, "Public", "index.db"));
        using (var command = database.CreateCommand())
        {
            command.CommandText = "SELECT arp_min_version, arp_max_version FROM packages WHERE id='Pinget.Wave12'";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0), "the latest version has no ARP range");
            Assert.True(reader.IsDBNull(1));
        }
        // Same rowids as wave12_tests::r12_7_package_row_uses_latest_version_arp_range_and_rows_follow_ordinal_ignore_case.
        Assert.Equal(new[] { "Pinget.Ab", "Pinget.A_b", "Pinget.Wave12" }, ReadColumn(database, "SELECT id FROM packages ORDER BY rowid"));
    }

    [Fact]
    public void UnicodeCaseVariantPackageIdentifiers_AreRejectedLikeRust()
    {
        using var temp = new TempDirectory();
        var input = WriteInput(
            temp,
            ("a.yaml", Manifest("Contoso.Äpp", "1.0", string.Empty, Installer("exe", string.Empty))),
            ("b.yaml", Manifest("Contoso.äpp", "1.0", string.Empty, Installer("exe", string.Empty))));
        var error = Assert.Throws<PublisherException>(() => ManifestScanner.Scan(input));
        Assert.Contains("differs only by casing", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Contoso-Source", true)]
    [InlineData("abc", true)]
    [InlineData("A.B-C.d", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("AB", false)]
    [InlineData("A..B", false)]
    [InlineData(".abc", false)]
    [InlineData("abc.", false)]
    [InlineData("a_b_c", false)]
    [InlineData("abc def", false)]
    [InlineData("abc\n", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void IdentityName_FollowsAppxPackageNameRule(string name, bool valid)
    {
        using var temp = new TempDirectory();
        var (state, buildId) = BuildFixture(temp);
        var msixPath = Path.Combine(temp.Path, "source.msix");
        var destination = Path.Combine(temp.Path, "feed");
        var packageOptions = new PublisherPackageOptions(
            state,
            msixPath,
            name,
            "CN=Pinget Wave12 Test",
            "1.0.0.0",
            BuildId: buildId);
        if (valid)
        {
            var packaged = PublisherPackaging.Package(packageOptions);
            Assert.Equal(name, packaged.PackageName);
            Assert.True(PublisherPackaging.Publish(new PublisherPublishOptions(
                state,
                destination,
                packaged.MsixPath,
                UnsignedDevelopment: true,
                BuildId: buildId)).Valid);
        }
        else
        {
            var packageError = Assert.Throws<PublisherException>(() => PublisherPackaging.Package(packageOptions));
            Assert.Contains("invalid MSIX package identity name", packageError.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(msixPath));

            var craftedMsix = CraftMsix(
                temp,
                state,
                buildId,
                manifest =>
                {
                    XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                    manifest.Root!.Element(foundation + "Identity")!.SetAttributeValue("Name", name);
                });
            var publishError = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(new PublisherPublishOptions(
                state,
                destination,
                craftedMsix,
                UnsignedDevelopment: true,
                BuildId: buildId)));
            Assert.Contains("invalid MSIX package identity name", publishError.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(destination));
        }
    }

    [Fact]
    public void Publish_NonCanonicalManifestVersionAndDuplicateElements_AreRejected()
    {
        using var temp = new TempDirectory();
        var (state, buildId) = BuildFixture(temp);

        var nonCanonicalMsix = CraftMsix(
            temp,
            state,
            buildId,
            static manifest =>
            {
                XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                manifest.Root!.Element(foundation + "Identity")!.SetAttributeValue("Version", "01.0.0.0");
            });
        var destination = Path.Combine(temp.Path, "feed-noncanonical");
        var versionError = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(new PublisherPublishOptions(
            state,
            destination,
            nonCanonicalMsix,
            UnsignedDevelopment: true,
            BuildId: buildId)));
        Assert.Contains("canonical four-part form", versionError.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));

        var duplicateIdentityMsix = CraftMsix(
            temp,
            state,
            buildId,
            static manifest =>
            {
                XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                var identity = manifest.Root!.Element(foundation + "Identity")!;
                identity.AddAfterSelf(new XElement(identity));
            });
        var duplicateIdentityError = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(new PublisherPublishOptions(
            state,
            Path.Combine(temp.Path, "feed-dup-id"),
            duplicateIdentityMsix,
            UnsignedDevelopment: true,
            BuildId: buildId)));
        Assert.Contains("Identity", duplicateIdentityError.Message, StringComparison.Ordinal);

        var duplicateExtensionMsix = CraftMsix(
            temp,
            state,
            buildId,
            static manifest =>
            {
                XNamespace uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
                var extension = manifest.Descendants(uap3 + "AppExtension").Single();
                extension.AddAfterSelf(new XElement(extension));
            });
        var duplicateExtensionError = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(new PublisherPublishOptions(
            state,
            Path.Combine(temp.Path, "feed-dup-ext"),
            duplicateExtensionMsix,
            UnsignedDevelopment: true,
            BuildId: buildId)));
        Assert.Contains("WinGet preindexed source extension", duplicateExtensionError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_SupersededPackageWithExpiredCertificate_ChecksOnlyDigestsAndSigner()
    {
        const string subject = "CN=Pinget Wave12 Signer";
        var expired = CreateCertificate(subject, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2));
        PsignTool.CheckSigningCertificate(expired, subject, requireCurrentValidity: false);
        Assert.Throws<PublisherException>(() => PsignTool.CheckSigningCertificate(expired, subject));
        Assert.Throws<PublisherException>(() => PsignTool.CheckSigningCertificate(expired, "CN=Someone Else", requireCurrentValidity: false));
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory();
        var log = Path.Combine(temp.Path, "psign.log");
        var tool = Path.Combine(temp.Path, "fake-psign-tool.cmd");
        File.WriteAllText(
            tool,
            string.Join(
                "\r\n",
                [
                    "@echo off",
                    "setlocal DisableDelayedExpansion",
                    "set \"RSP=%~1\"",
                    $"type \"%RSP:~1%\" >> \"{log}\"",
                    $"echo {{\"signers\":[{{\"signer_certificate_der_base64\":\"{Convert.ToBase64String(expired)}\"}}]}}",
                    "exit /b 0",
                ]) + "\r\n",
            Utf8);
        var package = Path.Combine(temp.Path, "source2.msix");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("AppxSignature.p7x").Open();
            stream.Write("PKCX"u8);
        }

        PsignTool.Verify(tool, package, subject, [], SignaturePolicy.Superseded);
        var calls = File.ReadAllText(log);
        Assert.Contains("verify-msix", calls, StringComparison.Ordinal);
        Assert.Contains("inspect-pkcs7", calls, StringComparison.Ordinal);
        Assert.DoesNotContain("--policy", calls, StringComparison.Ordinal);
        Assert.DoesNotContain("trust-verify-msix", calls, StringComparison.Ordinal);

        PsignTool.Verify(tool, package, subject, [], SignaturePolicy.Published);
        Assert.Contains("--policy", File.ReadAllText(log), StringComparison.Ordinal);

        var error = Assert.Throws<PublisherException>(() => PsignTool.Verify(tool, package, subject, [], SignaturePolicy.NewlySigned));
        Assert.Contains("not currently valid", error.Message, StringComparison.Ordinal);
        error = Assert.Throws<PublisherException>(() => PsignTool.Verify(tool, package, "CN=Someone Else", [], SignaturePolicy.Superseded));
        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_CompletedCopiesOfAnInterruptedPublish_AreAdopted()
    {
        using var temp = new TempDirectory();
        var (state, buildA, buildB) = TwoBuilds(temp);
        var destination = Path.Combine(temp.Path, "feed");
        PublisherPackaging.Publish(Options(state, destination, buildA, "1.0.0.0"));
        CopySnapshotArtifacts(state, buildB, destination);
        Assert.Throws<PublisherException>(() => PublisherPackaging.VerifyDestination(destination));

        Assert.Equal(buildB, PublisherPackaging.Publish(Options(state, destination, buildB, "1.0.0.1")).BuildId);
        Assert.Equal(buildB, PublisherPackaging.VerifyDestination(destination).BuildId);
    }

    [Fact]
    public void Publish_InterruptedFirstPublishLeftovers_AreAdoptedButUnrelatedFilesAreNot()
    {
        using var temp = new TempDirectory();
        var (state, _, buildB) = TwoBuilds(temp);
        var destination = Path.Combine(temp.Path, "feed");
        Directory.CreateDirectory(destination);
        var staging = Path.Combine(destination, $".publish-{buildB}.staging");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "source2.msix"), [1]);
        CopySnapshotArtifacts(state, buildB, destination);
        File.WriteAllBytes(Path.Combine(destination, "manifests", "partial.yaml.0123456789abcdef0123456789abcdef.tmp"), [1]);

        PublisherPackaging.Publish(Options(state, destination, buildB, "1.0.0.0"));
        Assert.Equal(buildB, PublisherPackaging.VerifyDestination(destination).BuildId);
        Assert.False(Directory.Exists(staging));

        var unowned = Path.Combine(temp.Path, "unowned");
        Directory.CreateDirectory(unowned);
        CopySnapshotArtifacts(state, buildB, unowned);
        File.WriteAllText(Path.Combine(unowned, "keep-me.txt"), "keep");
        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(Options(state, unowned, buildB, "1.0.0.0")));
        Assert.Contains("unowned", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(unowned, "keep-me.txt")));
    }

    [Fact]
    public void Publish_InterruptedPackageSwap_IsRolledBackOrCompleted()
    {
        using var temp = new TempDirectory();
        var (state, buildA, buildB) = TwoBuilds(temp);
        var destination = Path.Combine(temp.Path, "feed");
        var markerPath = Path.Combine(destination, ".pinget-publisher.json");
        var packagePath = Path.Combine(destination, "source2.msix");
        PublisherPackaging.Publish(Options(state, destination, buildA, "1.0.0.0"));
        var markerA = JsonNode.Parse(File.ReadAllText(markerPath))!;
        var packageA = File.ReadAllBytes(packagePath);
        PublisherPackaging.Publish(Options(state, destination, buildB, "1.0.0.1"));
        var markerB = JsonNode.Parse(File.ReadAllText(markerPath))!;
        var packageB = File.ReadAllBytes(packagePath);
        string Pending(JsonNode? previous)
        {
            var marker = markerB.DeepClone();
            marker["pendingCommit"] = new JsonObject { ["previous"] = previous?.DeepClone() };
            return marker.ToJsonString();
        }

        // Killed after the pending marker was written, before the package swap: rolled back to A.
        File.WriteAllText(markerPath, Pending(markerA), Utf8);
        File.WriteAllBytes(packagePath, packageA);
        Assert.Equal(buildA, PublisherPackaging.VerifyDestination(destination).BuildId);
        Assert.Equal(buildB, PublisherPackaging.Publish(Options(state, destination, buildB, "1.0.0.1")).BuildId);
        Assert.Equal(buildB, PublisherPackaging.VerifyDestination(destination).BuildId);

        // Killed after the package swap, before the final marker: completed as B.
        File.WriteAllText(markerPath, Pending(markerA), Utf8);
        Assert.Equal(packageB, File.ReadAllBytes(packagePath));
        Assert.Equal(buildB, PublisherPackaging.VerifyDestination(destination).BuildId);
        PublisherPackaging.Publish(Options(state, destination, buildB, "1.0.0.1"));
        Assert.Null(JsonNode.Parse(File.ReadAllText(markerPath))!["pendingCommit"]);

        // An interrupted first publication that never swapped its package in can be retried.
        var first = Path.Combine(temp.Path, "first");
        PublisherPackaging.Publish(Options(state, first, buildB, "1.0.0.1"));
        var firstMarkerPath = Path.Combine(first, ".pinget-publisher.json");
        var firstMarker = JsonNode.Parse(File.ReadAllText(firstMarkerPath))!;
        firstMarker["pendingCommit"] = new JsonObject { ["previous"] = null };
        File.WriteAllText(firstMarkerPath, firstMarker.ToJsonString(), Utf8);
        File.Delete(Path.Combine(first, "source2.msix"));
        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.VerifyDestination(first));
        Assert.Contains("interrupted", error.Message, StringComparison.Ordinal);
        PublisherPackaging.Publish(Options(state, first, buildB, "1.0.0.1"));
        Assert.Equal(buildB, PublisherPackaging.VerifyDestination(first).BuildId);
    }

    [Fact]
    public void Publish_PackageIdentifierCasingChange_IsRefusedBeforeCopying()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var buildA = workspace.Build(new PublisherBuildOptions(
            WriteInput(temp, ("a.yaml", Manifest("Pinget.Wave12", "1.0.0", string.Empty, Installer("exe", string.Empty)))), state)).BuildId;
        var destination = Path.Combine(temp.Path, "feed");
        PublisherPackaging.Publish(Options(state, destination, buildA, "1.0.0.0"));
        var buildB = workspace.Build(new PublisherBuildOptions(
            WriteInput(temp, ("a.yaml", Manifest("pinget.Wave12", "1.0.0", string.Empty, Installer("exe", string.Empty)))), state)).BuildId;

        var error = Assert.Throws<PublisherException>(() => PublisherPackaging.Publish(Options(state, destination, buildB, "1.0.0.1")));

        Assert.Contains("differs only by casing", error.Message, StringComparison.Ordinal);
        Assert.Equal(buildA, PublisherPackaging.VerifyDestination(destination).BuildId);
    }

    [Theory]
    [InlineData("1.11.0")]
    [InlineData("1.12.0")]
    [InlineData("1.28.0")]
    [InlineData("1.31.0")]
    public void Scan_ModernManifestVersions_PreservesStringAndBooleanFields(string schemaVersion)
    {
        using var temp = new TempDirectory();
        var versionDoc =
            $"PackageIdentifier: Devolutions.Cli\nPackageVersion: 2026.3.3.0\nDefaultLocale: en-US\n"
            + $"ManifestType: version\nManifestVersion: {schemaVersion}\n";
        var localeDoc =
            $"PackageIdentifier: Devolutions.Cli\nPackageVersion: 2026.3.3.0\nPackageLocale: en-US\n"
            + $"Publisher: Devolutions Inc.\nPackageName: Devolutions CLI\nLicense: Proprietary\n"
            + $"ShortDescription: Devolutions CLI\nManifestType: defaultLocale\nManifestVersion: {schemaVersion}\n";
        var installerDoc =
            $"PackageIdentifier: Devolutions.Cli\nPackageVersion: 2026.3.3.0\nInstallerType: zip\n"
            + $"ArchiveBinariesDependOnPath: true\nInstallationMetadata:\n  DefaultInstallLocation: 1.10\n"
            + $"Authentication:\n  AuthenticationType: microsoftEntraId\n  "
            + $"MicrosoftEntraIdAuthenticationInfo:\n    Resource: 12345\n"
            + $"ModuleName: 2026.1\n"
            + $"Installers:\n  - Architecture: x64\n    NestedInstallerType: portable\n    "
            + $"InstallerUrl: https://cdn.devolutions.net/download/devo.zip\n    InstallerSha256: {Hash}\n    "
            + $"NestedInstallerFiles:\n      - RelativeFilePath: devo.exe\n        PortableCommandAlias: devo\n"
            + $"ManifestType: installer\nManifestVersion: {schemaVersion}\n";

        var scan = ManifestScanner.Scan(WriteInput(
            temp,
            ("Devolutions.Cli.yaml", versionDoc),
            ("Devolutions.Cli.locale.en-US.yaml", localeDoc),
            ("Devolutions.Cli.installer.yaml", installerDoc)));

        var merged = Assert.Single(scan.Manifests);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(merged.Content))!.AsObject();
        Assert.Equal(schemaVersion, json["ManifestVersion"]!.GetValue<string>());
        Assert.True(json["ArchiveBinariesDependOnPath"]!.GetValue<bool>());
        Assert.Equal("1.10", json["InstallationMetadata"]!["DefaultInstallLocation"]!.GetValue<string>());
        Assert.Equal("12345", json["Authentication"]!["MicrosoftEntraIdAuthenticationInfo"]!["Resource"]!.GetValue<string>());
        Assert.Equal("2026.1", json["ModuleName"]!.GetValue<string>());
        Assert.Equal("devo", json["Installers"]![0]!["NestedInstallerFiles"]![0]!["PortableCommandAlias"]!.GetValue<string>());
    }

    [Fact]
    public void Scan_UnsupportedModernManifestVersion_IsRejected()
    {
        var unsupported = Manifest("Pinget.Wave12", "1.0.0", string.Empty, Installer("exe", string.Empty))
            .Replace("ManifestVersion: 1.9.0", "ManifestVersion: 1.99.0", StringComparison.Ordinal);
        var error = Assert.Throws<PublisherException>(() => ScanSingle(unsupported));
        Assert.Contains("Unsupported ManifestVersion", error.Message, StringComparison.Ordinal);
    }

    private static (string State, string BuildA, string BuildB) TwoBuilds(TempDirectory temp)
    {
        var first = Manifest("Pinget.Wave12", "1.0.0", string.Empty, Installer("exe", string.Empty));
        var second = Manifest("Pinget.Wave12", "2.0.0", string.Empty, Installer("exe", string.Empty));
        var state = Path.Combine(temp.Path, "state");
        var workspace = new PublisherWorkspace(state);
        var buildA = workspace.Build(new PublisherBuildOptions(WriteInput(temp, ("a.yaml", first)), state)).BuildId;
        var buildB = workspace.Build(new PublisherBuildOptions(WriteInput(temp, ("a.yaml", first), ("b.yaml", second)), state)).BuildId;
        return (state, buildA, buildB);
    }

    private static (string State, string BuildId) BuildFixture(TempDirectory temp)
    {
        var state = Path.Combine(temp.Path, "state");
        var input = WriteInput(temp, ("a.yaml", Manifest("Pinget.Wave12", "1.0.0", string.Empty, Installer("exe", string.Empty))));
        return (state, new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)).BuildId);
    }

    /// <summary>Copies a snapshot's feed artifacts into <paramref name="destination"/>, as an interrupted publication would.</summary>
    private static void CopySnapshotArtifacts(string state, string buildId, string destination)
    {
        var snapshot = Path.Combine(state, "builds", buildId);
        var artifacts = JsonNode.Parse(File.ReadAllText(Path.Combine(snapshot, "snapshot.json")))!["artifacts"]!.AsObject();
        foreach (var item in artifacts)
        {
            var target = Path.Combine(destination, item.Key.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(snapshot, "feed", item.Key.Replace('/', Path.DirectorySeparatorChar)), target);
        }
    }

    private static PublisherPublishOptions Options(string state, string destination, string buildId, string version)
    {
        var msixPath = Path.Combine(Path.GetDirectoryName(state)!, $"staged-{Guid.NewGuid():N}.msix");
        _ = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            msixPath,
            "Pinget.Test.Wave12",
            "CN=Pinget Wave12 Test",
            version,
            BuildId: buildId));
        return new PublisherPublishOptions(state, destination, msixPath, UnsignedDevelopment: true, BuildId: buildId);
    }

    private static string CraftMsix(TempDirectory temp, string state, string buildId, Action<XDocument> mutateManifest)
    {
        var templatePath = Path.Combine(temp.Path, $"template-{Guid.NewGuid():N}.msix");
        _ = PublisherPackaging.Package(new PublisherPackageOptions(
            state,
            templatePath,
            "Pinget.Test.Wave12",
            "CN=Pinget Wave12 Test",
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
                    var manifest = XDocument.Parse(Utf8.GetString(data));
                    mutateManifest(manifest);
                    data = Utf8.GetBytes(manifest.ToString(SaveOptions.DisableFormatting));
                }
                parts.Add((entry.FullName, data));
            }
        }
        var craftedPath = Path.Combine(temp.Path, $"crafted-{Guid.NewGuid():N}.msix");
        File.WriteAllBytes(craftedPath, MsixPackageWriter.Write(parts));
        return craftedPath;
    }

    private static byte[] CreateCertificate(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, critical: false));
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return certificate.Export(X509ContentType.Cert);
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pinget-publisher-wave12-tests", Guid.NewGuid().ToString("N"));
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
