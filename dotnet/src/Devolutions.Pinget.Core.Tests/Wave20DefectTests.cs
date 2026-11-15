using System.Diagnostics;
using System.Text;
using Devolutions.Pinget.Cli;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Regression tests for the CLI front-end defect fixes (wave 20).</summary>
public class Wave20DefectTests
{
    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pinget-wave20-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] arguments)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "pinget.dll");
        Assert.True(File.Exists(cliDll), $"Missing CLI next to the tests: {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.Environment["PINGET_APPROOT"] = CreateTempDirectory();
        psi.ArgumentList.Add(cliDll);
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "CLI did not exit in time.");
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>
    /// A package selection that cannot match anything (unknown source, exact unknown id), so a
    /// command line that is wrongly accepted still never touches a real package.
    /// </summary>
    private static string[] NoMatch(params string[] command) =>
        [.. command, "--id", "Pinget.Wave20.NoSuchPackage", "--exact", "--source", "pinget-wave20-no-such-source"];

    private static void AssertRejected((int ExitCode, string Stdout, string Stderr) run, string expectedMessage)
    {
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(expectedMessage, run.Stderr + run.Stdout);
    }

    // ── W20-1: table columns measure East Asian wide characters as two columns ──

    [Fact]
    public void Table_MeasuresWideCharactersAsTwoColumns()
    {
        var lines = TableRenderer.Render(["Name", "Id"], [new[] { "中文名", "A" }], consoleWidth: null);
        Assert.Equal("Name   Id", lines[0]);
        Assert.Equal(new string('-', 9), lines[1]);
        Assert.Equal("中文名 A", lines[2]);
    }

    [Fact]
    public void Table_TruncatesWideCharactersToTheColumnWidth()
    {
        // Name column shrinks to 4 columns: "中" (2) + "…" (1) + 1 padding column.
        var lines = TableRenderer.Render(["Name", "Id"], [new[] { "中文名字", "x" }], consoleWidth: 8);
        Assert.Equal("中…  x", lines[2]);
        Assert.Equal(6, TableRenderer.DisplayWidth(lines[2]));
    }

    // ── W20-2: truncation ends with an ellipsis, not a dot ──

    [Fact]
    public void Table_TruncationUsesAnEllipsis()
    {
        var lines = TableRenderer.Render(["Name", "Id"], [new[] { "abcdefghij", "x" }], consoleWidth: 9);
        Assert.Equal("abcd… x", lines[2]);
    }

    // ── W20-3: export sorts packages by id and drops duplicate ids without versions ──

    private static ListMatch Installed(string id, string version) => new()
    {
        Name = id,
        Id = id,
        LocalId = id,
        InstalledVersion = version,
        SourceName = "winget",
    };

    [Fact]
    public void Export_SortsByIdAndCollapsesSideBySideInstalls()
    {
        ListMatch[] rows =
        [
            Installed("Python.Python.3.12", "3.12.1"),
            Installed("Git.Git", "2.45.0"),
            Installed("Python.Python.3.12", "3.12.0"),
        ];

        var withoutVersions = Assert.Single(ExportPayloadBuilder.Build(rows, [], null, includeVersions: false).Sources);
        Assert.Equal(
            [("Git.Git", (string?)null), ("Python.Python.3.12", null)],
            withoutVersions.Packages.Select(p => (p.PackageIdentifier, p.Version)).ToArray());

        var withVersions = Assert.Single(ExportPayloadBuilder.Build(rows, [], null, includeVersions: true).Sources);
        Assert.Equal(
            [("Git.Git", (string?)"2.45.0"), ("Python.Python.3.12", "3.12.1"), ("Python.Python.3.12", "3.12.0")],
            withVersions.Packages.Select(p => (p.PackageIdentifier, p.Version)).ToArray());
    }

    // ── W20-4: import reads schema 1.0 files (`Id` instead of `PackageIdentifier`) ──

    [Fact]
    public void Import_ReadsSchema10PackageIds()
    {
        const string json = """
            {
              "$schema": "https://aka.ms/winget-packages.schema.1.0.json",
              "WinGetVersion": "1.0.0",
              "Sources": [{
                "SourceDetails": { "Name": "winget", "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe",
                                   "Argument": "https://cdn.winget.microsoft.com/cache", "Type": "Microsoft.PreIndexed.Package" },
                "Packages": [ { "Id": "Git.Git" }, { "Id": "Microsoft.PowerToys", "Version": "0.75.0" } ]
              }]
            }
            """;

        var packages = ImportCommandHandler.ParseImportFile(json, ignoreVersions: false);

        Assert.Equal(["Git.Git", "Microsoft.PowerToys"], packages.Select(p => p.Id).ToArray());
        Assert.Equal("0.75.0", packages[1].Version);
        Assert.All(packages, p => Assert.Equal("winget", p.SourceName));
    }

    // (W20-5, a failed package action exiting 0, was Rust-only: PrintPackageActionResult already maps 0 to 1.)

    // ── W20-6: list --include-unknown / --include-pinned need --upgrade-available ──

    [Fact]
    public void List_IncludeFlagsRequireUpgradeAvailable()
    {
        AssertRejected(RunCli("list", "--include-unknown"), "Argument include-unknown can only be used with upgrade-available");
        AssertRejected(RunCli("list", "--include-pinned"), "Argument include-pinned can only be used with upgrade-available");
    }

    // ── W20-7: WinGet exclusive argument sets ──
    // uninstall: PurgePreserve and AllAndTargetVersion

    [Fact]
    public void Uninstall_RejectsMutuallyExclusiveArguments()
    {
        AssertRejected(RunCli([.. NoMatch("uninstall"), "--purge", "--preserve"]), "Multiple mutually exclusive arguments provided: purge|preserve");
        AssertRejected(RunCli([.. NoMatch("uninstall"), "--all-versions", "--version", "1.0"]), "Multiple mutually exclusive arguments provided: all-versions|version");
    }

    // pin add: PinType (--version | --blocking)

    [Fact]
    public void PinAdd_RejectsVersionWithBlocking()
    {
        AssertRejected(RunCli([.. NoMatch("pin", "add"), "--version", "1.2.*", "--blocking"]), "Multiple mutually exclusive arguments provided: version|blocking");
    }

    // install: DependenciesConflict (--skip-dependencies | --dependencies-only)

    [Fact]
    public void Install_RejectsSkipDependenciesWithDependenciesOnly()
    {
        AssertRejected(RunCli([.. NoMatch("install"), "--skip-dependencies", "--dependencies-only"]), "Multiple mutually exclusive arguments provided: skip-dependencies|dependencies-only");
    }

    // ── W20-8: parse errors are reported instead of running the command anyway ──

    [Fact]
    public void Cli_ReportsParseErrorsAndServesHelp()
    {
        var unknown = RunCli("error", "0x8A150001", "--bogus");
        AssertRejected(unknown, "Unrecognized command or argument '--bogus'");
        Assert.DoesNotContain("APPINSTALLER_CLI_ERROR_INTERNAL_ERROR", unknown.Stdout);

        AssertRejected(RunCli("error", "0x8A150001", "--output", "xml"), "'xml'");
        AssertRejected(RunCli("search", "--id", "Pinget.Wave20.NoSuchPackage", "--count", "0"), CliValidation.CountOutOfBoundsMessage);

        var help = RunCli("--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Usage:", help.Stdout);
    }

    // ── W20-10: import/export take their file positionally or by name ──

    [Fact]
    public void ImportAndExport_AcceptTheFilePositionally()
    {
        var directory = CreateTempDirectory();
        var importFile = Path.Combine(directory, "empty.json");
        File.WriteAllText(importFile, """{ "$schema": "https://aka.ms/winget-packages.schema.2.0.json", "Sources": [] }""");

        var import = RunCli("import", importFile, "--dry-run");
        Assert.True(import.ExitCode == 0, import.Stderr);
        Assert.Contains("0 of 0 package(s) would be installed.", import.Stdout);
        AssertRejected(RunCli("import", importFile, "-i", importFile, "--dry-run"), "both as an argument and with --import-file");
        AssertRejected(RunCli("import", "--dry-run"), "Required argument missing: import-file");

        var exportFile = Path.Combine(directory, "packages.json");
        var export = RunCli("export", exportFile, "--source", "pinget-wave20-no-such-source");
        Assert.True(export.ExitCode == 0, export.Stderr);
        Assert.True(File.Exists(exportFile));
        AssertRejected(RunCli("export"), "Required argument missing: output");
    }

    // ── W20-9: hash labels its output like WinGet and accepts --file/-f and -m ──

    [Fact]
    public void Hash_OutputAndArgumentsMatchWinGet()
    {
        var file = Path.Combine(CreateTempDirectory(), "hash.bin");
        File.WriteAllBytes(file, "abc"u8.ToArray());
        const string expected = "InstallerSha256: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        foreach (var arguments in new[] { new[] { "hash", file }, ["hash", "--file", file], ["hash", "-f", file] })
        {
            var run = RunCli(arguments);
            Assert.True(run.ExitCode == 0, run.Stderr);
            Assert.Equal(expected, run.Stdout.Trim());
        }

        // -m is --msix: the file is hashed, then the (non-MSIX) signature hash fails.
        var msix = RunCli("hash", file, "-m");
        Assert.Equal(1, msix.ExitCode);
        Assert.StartsWith(expected, msix.Stdout.Trim());
        Assert.DoesNotContain("Unrecognized", msix.Stderr);

        Assert.NotEqual(0, RunCli("hash").ExitCode);
    }
}
