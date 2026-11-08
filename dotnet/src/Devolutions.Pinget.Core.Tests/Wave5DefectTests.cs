using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Devolutions.Pinget.Cli;
using Devolutions.Pinget.PowerShell.Engine;
using Xunit;
using YamlDotNet.Serialization;

namespace Devolutions.Pinget.Core.Tests;

public class Wave5DefectTests
{
    // ── C3: YAML emitter quotes strings that would not read back as strings ──

    public static readonly TheoryData<string> AmbiguousStrings = new()
    {
        "", "~", "null", "Null", "NULL", "true", "True", "FALSE", "yes", "No", "ON", "off", "y", "N",
        "1", "-12", "+3", "1.10", "1.0", ".5", "1e5", "-1.5E-3", "0x1F", "0o17", "017", "1_000",
        ".inf", "-.Inf", ".NaN",
    };

    [Theory]
    [MemberData(nameof(AmbiguousStrings))]
    public void YamlEmitter_QuotesStringsThatResolveToNonStrings(string value)
    {
        var yaml = YamlEmitter.EmitDocument(new Dictionary<string, object?> { ["Value"] = value });

        Assert.True(YamlEmitter.ResolvesToNonString(value));
        Assert.Contains($"Value: '{value}'", yaml);
        Assert.Equal(value, ReadBackValue(yaml));
    }

    [Theory]
    [InlineData("winget")]
    [InlineData("Microsoft.PowerToys")]
    [InlineData("1.2.3")]
    [InlineData("v2.0")]
    [InlineData("yesterday")]
    [InlineData("null-terminated")]
    public void YamlEmitter_KeepsOrdinaryStringsPlain(string value)
    {
        var yaml = YamlEmitter.EmitDocument(new Dictionary<string, object?> { ["Value"] = value });

        Assert.False(YamlEmitter.ResolvesToNonString(value));
        Assert.Contains($"Value: {value}", yaml);
        Assert.Equal(value, ReadBackValue(yaml));
    }

    [Fact]
    public void YamlEmitter_JsonStringsRoundTripButNumbersAndBoolsStayNative()
    {
        var node = new JsonObject
        {
            ["PackageVersion"] = "1.10",
            ["Flag"] = "true",
            ["Count"] = 3,
            ["Enabled"] = true,
            ["Empty"] = "",
        };

        var yaml = YamlEmitter.EmitDocument(node).ReplaceLineEndings("\n");

        Assert.Contains("PackageVersion: '1.10'\n", yaml);
        Assert.Contains("Flag: 'true'\n", yaml);
        Assert.Contains("Count: 3\n", yaml);
        Assert.Contains("Enabled: true\n", yaml);
        Assert.Contains("Empty: ''\n", yaml);

        var map = ReadBack(yaml);
        Assert.Equal("1.10", map["PackageVersion"]);
        Assert.Equal("true", map["Flag"]);
        Assert.Equal("", map["Empty"]);
        Assert.IsNotType<string>(map["Count"]);
        Assert.IsType<bool>(map["Enabled"]);
    }

    private static object? ReadBackValue(string yaml) => ReadBack(yaml)["Value"];

    private static Dictionary<string, object?> ReadBack(string yaml) =>
        new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build()
            .Deserialize<Dictionary<string, object?>>(yaml);

    // ── C4: HRESULT table (identical to the Rust table; a Rust test compares the two files) ──

    [Fact]
    public void ErrorCodes_TableIsSortedAndUnique()
    {
        for (var i = 1; i < ErrorCodes.HResults.Length; i++)
            Assert.True(ErrorCodes.HResults[i - 1].Code < ErrorCodes.HResults[i].Code);
    }

    [Theory]
    [InlineData(0x8A150001u, "APPINSTALLER_CLI_ERROR_INTERNAL_ERROR", "Internal Error")]
    [InlineData(0x8A150014u, "APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND", "No packages found")]
    [InlineData(0x8A15002Bu, "APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE", "No applicable update found")]
    [InlineData(0x8A150101u, "APPINSTALLER_CLI_ERROR_INSTALL_PACKAGE_IN_USE", "Application is currently running. Exit the application then try again.")]
    [InlineData(0x80070005u, "E_ACCESSDENIED", "Access is denied.")]
    public void ErrorCodes_LookupMatchesWinGet(uint code, string symbol, string description)
    {
        Assert.Equal((symbol, description), ErrorCodes.Lookup(code));
    }

    [Fact]
    public void ErrorCodes_LookupOfUnknownCodeIsNull()
    {
        Assert.Null(ErrorCodes.Lookup(0x8A15FFFF));
        Assert.Equal("APPINSTALLER_CLI_ERROR_INSTALL_CUSTOM_ERROR", ErrorCodes.Lookup(0x8A150115)?.Symbol);
    }

    // ── C5 / C7: error input parsing ──

    [Theory]
    [InlineData("0x8A150001", 0x8A150001u)]
    [InlineData("0X8a150001", 0x8A150001u)]
    [InlineData("2316632065", 0x8A150001u)]
    [InlineData("-1978335231", 0x8A150001u)]
    [InlineData("-1", 0xFFFFFFFFu)]
    [InlineData("0", 0u)]
    [InlineData("4294967295", 0xFFFFFFFFu)]
    public void ErrorCodes_ParsesHexUnsignedAndNegativeDecimal(string input, uint expected)
    {
        Assert.True(ErrorCodes.TryParse(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("4294967296")]
    [InlineData("-2147483649")]
    [InlineData("0x100000000")]
    [InlineData("8A150001")]
    [InlineData("not-a-code")]
    [InlineData("")]
    public void ErrorCodes_RejectsOutOfRangeAndGarbage(string input)
    {
        Assert.False(ErrorCodes.TryParse(input, out _));
    }

    // ── C8: named upgrade with nothing to upgrade ──

    [Fact]
    public void NoApplicableUpgrade_IsAnErrorOnlyForNamedUpgrade()
    {
        Assert.Equal(0, UpgradeTargetSelector.ReportNoApplicableUpgrade(all: true));
        Assert.Equal(1, UpgradeTargetSelector.ReportNoApplicableUpgrade(all: false));
    }

    // ── C9: hash ──

    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void FileHasher_ProducesLowercaseSha256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pinget-wave5-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "abc");
        try
        {
            Assert.Equal(AbcSha256, FileHasher.Sha256FileHex(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileHasher_MsixSignatureHashIsSha256OfAppxSignatureEntry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pinget-wave5-{Guid.NewGuid():N}.msix");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("AppxManifest.xml").Open()))
                writer.Write("<Package/>");
            using var signature = archive.CreateEntry("AppxSignature.p7x").Open();
            signature.Write(Encoding.ASCII.GetBytes("abc"));
        }

        try
        {
            Assert.Equal(AbcSha256, FileHasher.MsixSignatureSha256Hex(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileHasher_MsixSignatureHashOfNonPackageThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pinget-wave5-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "abc");
        try
        {
            Assert.Throws<InvalidDataException>(() => FileHasher.MsixSignatureSha256Hex(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── C10: --count range ──

    [Theory]
    [InlineData(1, true)]
    [InlineData(1000, true)]
    [InlineData(0, false)]
    [InlineData(1001, false)]
    [InlineData(-1, false)]
    public void CountMustBeBetween1And1000(int count, bool valid)
    {
        Assert.Equal(valid, CliValidation.IsValidCount(count));
    }

    // ── C12: import ──

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Sources\": 5}")]
    [InlineData("{\"Sources\": [{\"Packages\": [{\"Version\": \"1.0\"}]}]}")]
    public void ImportParse_MalformedFileIsInvalidData(string json)
    {
        Assert.Throws<InvalidDataException>(() => ImportCommandHandler.ParseImportFile(json, ignoreVersions: false));
    }

    [Fact]
    public void ImportParse_ReadsPackagesAndVersions()
    {
        const string json = """
            {"Sources":[{"SourceDetails":{"Name":"winget"},"Packages":[
              {"PackageIdentifier":"Contoso.App","Version":"1.2"},
              {"PackageIdentifier":"Contoso.Tool"}]}]}
            """;

        var packages = ImportCommandHandler.ParseImportFile(json, ignoreVersions: false);

        Assert.Equal(2, packages.Count);
        Assert.Equal(("Contoso.App", "winget", "1.2"), (packages[0].Id, packages[0].SourceName, packages[0].Version));
        Assert.Null(packages[1].Version);
        Assert.Null(ImportCommandHandler.ParseImportFile(json, ignoreVersions: true)[0].Version);
    }

    [Fact]
    public void ImportDryRun_SearchesEachPackage()
    {
        var searched = new List<string>();
        var exitCode = ImportCommandHandler.Run(
            [new ImportPackage("Contoso.App", "winget", null), new ImportPackage("Missing.App", "winget", null)],
            dryRun: true,
            ignoreUnavailable: false,
            noUpgrade: false,
            _ => throw new Xunit.Sdk.XunitException("dry-run must not query installed packages"),
            _ => throw new Xunit.Sdk.XunitException("dry-run must not install packages"),
            package =>
            {
                searched.Add(package.Id);
                return package.Id == "Contoso.App";
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "Contoso.App", "Missing.App" }, searched);
    }

    // ── C14: PowerShell NoOp status ──

    [Theory]
    [InlineData(true, true, "NoOp")]
    [InlineData(false, true, "NoOp")]
    [InlineData(true, false, "Ok")]
    [InlineData(false, false, "Error")]
    public void InstallResultStatus_ReportsNoOpBeforeOk(bool success, bool noOp, string expected)
    {
        Assert.Equal(expected, InstallResultStatus.From(success, noOp));
    }

    // ── C15: pin list structured output (same shape as the Rust CLI) ──

    [Fact]
    public void PinList_StructuredOutputMatchesRustShape()
    {
        var pins = PinListEntry.FromPins(
        [
            new PinRecord { PackageId = "Contoso.App", Version = "1.2.*", SourceId = "winget", PinType = PinType.Gating },
        ]);

        var json = JsonNode.Parse(StructuredOutputSerializer.SerializeJson(pins, CliJsonContext.Default.ListPinListEntry))!;
        var expected = JsonNode.Parse("""[{"PackageId":"Contoso.App","Version":"1.2.*","SourceId":"winget","PinType":"Gating"}]""");
        Assert.True(JsonNode.DeepEquals(expected, json), json.ToJsonString());

        var yaml = StructuredOutputSerializer.SerializeYaml(pins, CliJsonContext.Default.ListPinListEntry);
        Assert.Contains("PinType: Gating", yaml);
    }
}
