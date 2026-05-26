using System.Text.Json;
using Devolutions.Pinget.Cli;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class CliJsonCompatibilityTests
{
    [Fact]
    public void StructuredJsonSerializer_UsesPascalCaseForListResponses()
    {
        var response = new ListResponse
        {
            Matches =
            [
                new ListMatch
                {
                    Name = "PowerToys",
                    Id = "Microsoft.PowerToys",
                    LocalId = @"ARP\Machine\X64\PowerToys",
                    InstalledVersion = "0.98.1",
                    AvailableVersion = "0.99.0",
                    SourceName = "winget",
                    Publisher = "Microsoft",
                }
            ],
            Warnings = [],
            Truncated = false,
        };

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(response, PingetJsonContext.Default.ListResponse));
        var match = document.RootElement.GetProperty("Matches")[0];

        Assert.Equal("Microsoft.PowerToys", match.GetProperty("Id").GetString());
        Assert.Equal(@"ARP\Machine\X64\PowerToys", match.GetProperty("LocalId").GetString());
        Assert.Equal("0.98.1", match.GetProperty("InstalledVersion").GetString());
        Assert.Equal("0.99.0", match.GetProperty("AvailableVersion").GetString());
        Assert.Equal("winget", match.GetProperty("SourceName").GetString());
        Assert.False(match.TryGetProperty("local_id", out _));
        Assert.False(match.TryGetProperty("installed_version", out _));
        Assert.False(match.TryGetProperty("available_version", out _));
        Assert.False(match.TryGetProperty("source_name", out _));
    }

    [Fact]
    public void StructuredJsonSerializer_UsesPascalCaseForSearchResponses()
    {
        var response = new SearchResponse
        {
            Matches =
            [
                new SearchMatch
                {
                    SourceName = "winget",
                    SourceKind = SourceKind.PreIndexed,
                    Id = "Microsoft.PowerToys",
                    Name = "PowerToys",
                    Version = "0.99.0",
                    MatchCriteria = "Tag",
                }
            ],
            Warnings = [],
            Truncated = false,
        };

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(response, PingetJsonContext.Default.SearchResponse));
        var match = document.RootElement.GetProperty("Matches")[0];

        Assert.Equal("winget", match.GetProperty("SourceName").GetString());
        Assert.Equal("Tag", match.GetProperty("MatchCriteria").GetString());
        Assert.False(match.TryGetProperty("source_name", out _));
        Assert.False(match.TryGetProperty("match_criteria", out _));
    }

    [Fact]
    public void StructuredJsonSerializer_PreservesExistingShowManifestPropertyNames()
    {
        var manifest = new SerializableShowManifest
        {
            PackageIdentifier = "Microsoft.PowerToys",
            PackageName = "PowerToys",
            PackageVersion = "0.99.0",
            SourceName = "winget",
            SourceKind = SourceKind.PreIndexed,
            Author = "Contoso",
            Description = "Fancy tools",
            ShortDescription = "Tools",
            Publisher = "Microsoft",
            PackageUrl = "https://example.test/package",
            LicenseUrl = "https://example.test/license",
            ReleaseNotesUrl = "https://example.test/release-notes",
            Tags = ["utilities", "powertoys"],
            Installers =
            [
                new SerializableInstaller
                {
                    InstallerUrl = "https://example.test/installer.exe",
                    InstallerSha256 = "ABC123",
                    InstallerType = "exe",
                    ReleaseDate = "2026-05-22",
                }
            ],
        };

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(manifest, PingetJsonContext.Default.SerializableShowManifest));
        var root = document.RootElement;

        Assert.Equal("Microsoft.PowerToys", root.GetProperty(nameof(SerializableShowManifest.PackageIdentifier)).GetString());
        Assert.Equal("PowerToys", root.GetProperty(nameof(SerializableShowManifest.PackageName)).GetString());
        Assert.Equal("winget", root.GetProperty(nameof(SerializableShowManifest.SourceName)).GetString());
        Assert.Equal("Contoso", root.GetProperty(nameof(SerializableShowManifest.Author)).GetString());
        Assert.Equal("Fancy tools", root.GetProperty(nameof(SerializableShowManifest.Description)).GetString());
        Assert.Equal("Tools", root.GetProperty(nameof(SerializableShowManifest.ShortDescription)).GetString());
        Assert.Equal("Microsoft", root.GetProperty(nameof(SerializableShowManifest.Publisher)).GetString());
        Assert.Equal("https://example.test/package", root.GetProperty(nameof(SerializableShowManifest.PackageUrl)).GetString());
        Assert.Equal("https://example.test/license", root.GetProperty(nameof(SerializableShowManifest.LicenseUrl)).GetString());
        Assert.Equal("https://example.test/release-notes", root.GetProperty(nameof(SerializableShowManifest.ReleaseNotesUrl)).GetString());
        Assert.Equal(["utilities", "powertoys"], root.GetProperty(nameof(SerializableShowManifest.Tags)).EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
        var installer = root.GetProperty(nameof(SerializableShowManifest.Installers))[0];
        Assert.Equal("https://example.test/installer.exe", installer.GetProperty(nameof(SerializableInstaller.InstallerUrl)).GetString());
        Assert.Equal("ABC123", installer.GetProperty(nameof(SerializableInstaller.InstallerSha256)).GetString());
        Assert.Equal("exe", installer.GetProperty(nameof(SerializableInstaller.InstallerType)).GetString());
        Assert.Equal("2026-05-22", installer.GetProperty(nameof(SerializableInstaller.ReleaseDate)).GetString());
        Assert.False(root.TryGetProperty("package_identifier", out _));
        Assert.False(root.TryGetProperty("package_name", out _));
        Assert.False(root.TryGetProperty("source_name", out _));
        Assert.False(root.TryGetProperty("packageIdentifier", out _));
        Assert.False(root.TryGetProperty("packageName", out _));
        Assert.False(root.TryGetProperty("sourceName", out _));
    }

    [Fact]
    public void StructuredJsonSerializer_PreservesSourceExportPascalCaseShape()
    {
        var export = new SourceExportPayload(
        [
            new SourceExportEntry(
                Name: "winget",
                Type: "Microsoft.PreIndexed.Package",
                Arg: "https://cdn.winget.microsoft.com/cache",
                Data: "Microsoft.Winget.Source_8wekyb3d8bbwe",
                Identifier: "Microsoft.Winget.Source_8wekyb3d8bbwe",
                TrustLevel: "Trusted",
                Explicit: false,
                Priority: 0)
        ]);

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(export, CliJsonContext.Default.SourceExportPayload));
        var source = document.RootElement.GetProperty("Sources")[0];

        Assert.Equal("winget", source.GetProperty("Name").GetString());
        Assert.Equal("Microsoft.PreIndexed.Package", source.GetProperty("Type").GetString());
        Assert.Equal("https://cdn.winget.microsoft.com/cache", source.GetProperty("Arg").GetString());
        Assert.Equal("Microsoft.Winget.Source_8wekyb3d8bbwe", source.GetProperty("Data").GetString());
        Assert.Equal("Microsoft.Winget.Source_8wekyb3d8bbwe", source.GetProperty("Identifier").GetString());
        Assert.Equal("Trusted", source.GetProperty("TrustLevel").GetString());
        Assert.False(source.GetProperty("Explicit").GetBoolean());
        Assert.Equal(0, source.GetProperty("Priority").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("sources", out _));
    }

    [Fact]
    public void StructuredJsonSerializer_UsesPascalCaseForVersionsResults()
    {
        var result = new VersionsResult
        {
            Package = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Microsoft.PowerToys",
                Name = "PowerToys",
                Version = "0.99.0",
            },
            Versions =
            [
                new VersionKey { Version = "0.99.0", Channel = "stable" },
                new VersionKey { Version = "0.100.0-preview", Channel = "preview" },
            ],
        };

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(result, PingetJsonContext.Default.VersionsResult));
        var versions = document.RootElement.GetProperty("Versions");

        Assert.Equal("0.99.0", versions[0].GetProperty("Version").GetString());
        Assert.Equal("stable", versions[0].GetProperty("Channel").GetString());
        Assert.Equal("0.100.0-preview", versions[1].GetProperty("Version").GetString());
        Assert.Equal("preview", versions[1].GetProperty("Channel").GetString());
        Assert.False(document.RootElement.TryGetProperty("versions", out _));
    }

    [Fact]
    public void StructuredJsonSerializer_PreservesNullableListFields()
    {
        var response = new ListResponse
        {
            Matches =
            [
                new ListMatch
                {
                    Name = "Contoso Tool",
                    Id = "Contoso.Tool",
                    LocalId = @"ARP\User\X64\Contoso.Tool",
                    InstalledVersion = "1.2.3",
                    AvailableVersion = null,
                    SourceName = null,
                    Publisher = null,
                    Scope = null,
                    InstallerCategory = null,
                    InstallLocation = null,
                }
            ],
            Warnings = [],
            Truncated = false,
        };

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(response, PingetJsonContext.Default.ListResponse));
        var match = document.RootElement.GetProperty("Matches")[0];

        Assert.Equal(JsonValueKind.Null, match.GetProperty("AvailableVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, match.GetProperty("SourceName").ValueKind);
        Assert.Equal(JsonValueKind.Null, match.GetProperty("Publisher").ValueKind);
        Assert.Equal(JsonValueKind.Null, match.GetProperty("Scope").ValueKind);
        Assert.Equal(JsonValueKind.Null, match.GetProperty("InstallerCategory").ValueKind);
        Assert.Equal(JsonValueKind.Null, match.GetProperty("InstallLocation").ValueKind);
    }

    [Fact]
    public void StructuredJsonSerializer_PreservesMinimumSearchShapeUsedByUnigetui()
    {
        var response = new SearchResponse
        {
            Matches =
            [
                new SearchMatch
                {
                    SourceName = "msstore",
                    SourceKind = SourceKind.Rest,
                    Id = "9WZDNCRFJBMP",
                    Name = "Microsoft To Do",
                    Version = "2.123.456.0",
                }
            ],
            Warnings = [],
            Truncated = false,
        };

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(response, PingetJsonContext.Default.SearchResponse));
        var match = document.RootElement.GetProperty("Matches")[0];

        Assert.Equal("Microsoft To Do", match.GetProperty("Name").GetString());
        Assert.Equal("9WZDNCRFJBMP", match.GetProperty("Id").GetString());
        Assert.Equal("2.123.456.0", match.GetProperty("Version").GetString());
        Assert.Equal("msstore", match.GetProperty("SourceName").GetString());
    }
}