using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Devolutions.Pinget.Cli;
using Devolutions.Pinget.PowerShell.Engine;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Regression tests for the CLI / PowerShell / COM surface defect fixes (wave 11).</summary>
public class Wave11DefectTests
{
    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pinget-wave11-tests", Guid.NewGuid().ToString("N"));
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

    // ── R11-1: PowerShell -Scope maps to the core scope names ──

    [Theory]
    [InlineData(PSPackageInstallScope.Any, null)]
    [InlineData(PSPackageInstallScope.User, "user")]
    [InlineData(PSPackageInstallScope.UserOrUnknown, "user")]
    [InlineData(PSPackageInstallScope.System, "machine")]
    [InlineData(PSPackageInstallScope.SystemOrUnknown, "machine")]
    public void PowerShellScope_MapsToCoreInstallScope(PSPackageInstallScope scope, string? expected)
    {
        Assert.Equal(expected, PSEnumMapping.ToInstallScope(scope));
        if (expected is not null)
        {
            // The core accepts the mapped value (System used to fail with "Unsupported install scope").
            _ = InstallerDispatch.ResolvePortableScope(expected, null, null);
            Assert.Throws<InvalidOperationException>(() => InstallerDispatch.ResolvePortableScope(scope.ToString() + "X", null, null));
        }
    }

    // ── R11-2: WinGet common flags ──

    [Fact]
    public void CommonOptions_AcceptWinGetFlagsOnAnySubcommand()
    {
        var root = new RootCommand();
        var install = new Command("install");
        var id = new Option<string?>("--id");
        install.AddOption(id);
        root.AddCommand(install);
        var common = new CliCommonOptions();
        common.AddTo(root);

        var result = root.Parse(
            "install", "--id", "Git.Git", "--accept-source-agreements", "--disable-interactivity", "--wait",
            "--verbose", "--nowarn", "--no-vt", "--no-progress");

        Assert.Empty(result.Errors);
        Assert.Equal("Git.Git", result.GetValueForOption(id));
        Assert.True(result.GetValueForOption(common.DisableInteractivity));
        Assert.True(result.GetValueForOption(common.IgnoreWarnings));
        Assert.Empty(root.Parse("install", "--verbose-logs", "--ignore-warnings").Errors);
        Assert.False(root.Parse("install").GetValueForOption(common.DisableInteractivity));
    }

    [Fact]
    public void DisableInteractivity_SuppressesTheAgreementsPrompt()
    {
        Assert.True(CliSession.AgreementsPromptAllowed(disableInteractivity: false, inputRedirected: false, errorRedirected: false));
        Assert.False(CliSession.AgreementsPromptAllowed(disableInteractivity: true, inputRedirected: false, errorRedirected: false));
        Assert.False(CliSession.AgreementsPromptAllowed(disableInteractivity: false, inputRedirected: true, errorRedirected: false));
        Assert.False(CliSession.AgreementsPromptAllowed(disableInteractivity: false, inputRedirected: false, errorRedirected: true));
    }

    [Fact]
    public void Cli_AcceptsWinGetCommonFlags()
    {
        var run = RunCli("error", "0x8A150001", "--accept-source-agreements", "--disable-interactivity", "--wait",
            "--verbose-logs", "--ignore-warnings", "--no-vt", "--no-progress");

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Contains("APPINSTALLER_CLI_ERROR_INTERNAL_ERROR", run.Stdout);
    }

    // ── R11-3: tables are not truncated when stdout is redirected ──

    [Fact]
    public void Table_IsNotTruncatedWithoutAConsoleWidth()
    {
        const string longId = "Microsoft.VisualStudio.2022.Community.Preview.With.A.Very.Long.Identifier";
        var rows = new List<string[]> { new[] { "Visual Studio Community 2022 Preview", longId, "17.10.0" } };

        var redirected = TableRenderer.Render(["Name", "Id", "Version"], rows, consoleWidth: null);
        Assert.Contains(longId, redirected[2]);

        var narrow = TableRenderer.Render(["Name", "Id", "Version"], rows, consoleWidth: 60);
        Assert.DoesNotContain(longId, narrow[2]);
        Assert.True(narrow[1].Length < 60);
    }

    [Fact]
    public void Table_UsesTheFullConsoleWidth()
    {
        // 59 + 1 + 59 = 119 columns: fits a 120-column console without shrinking.
        var rows = new List<string[]> { new[] { new string('a', 59), new string('b', 59) } };
        var lines = TableRenderer.Render(["Name", "Id"], rows, consoleWidth: 120);
        Assert.Equal($"{new string('a', 59)} {new string('b', 59)}", lines[2]);
    }

    // ── R11-4: CLI output is UTF-8 ──

    [Fact]
    public void Cli_WritesNonAsciiTextAsUtf8()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = Path.Combine(directory, "Intel®-腾讯QQ.yaml");
            File.WriteAllText(manifest, "PackageIdentifier: Contoso.Tool\nManifestType: singleton\nManifestVersion: 1.6.0\n");

            var run = RunCli("validate", manifest);

            Assert.Equal(1, run.ExitCode);
            Assert.Contains("Intel®-腾讯QQ.yaml", run.Stdout);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── R11-5: source add defaults to Microsoft.PreIndexed.Package ──

    [Fact]
    public void SourceAdd_WithoutType_DefaultsToPreIndexed()
    {
        Assert.Equal(SourceKind.PreIndexed, SourceTypes.Parse(null));
        Assert.Equal(SourceKind.PreIndexed, SourceTypes.Parse(" "));
        Assert.Equal(SourceKind.Rest, SourceTypes.Parse("Microsoft.Rest"));
        Assert.Equal(SourceKind.Rest, SourceTypes.Parse("rest"));
        Assert.Equal(SourceKind.PreIndexed, SourceTypes.Parse("Microsoft.PreIndexed.Package"));
        Assert.Throws<InvalidOperationException>(() => SourceTypes.Parse("bogus"));

        Assert.Equal(SourceKind.PreIndexed, PingetClient.ParseSourceKind(null));
        Assert.Equal(SourceKind.Rest, PingetClient.ParseSourceKind("Microsoft.Rest"));
    }

    // ── R11-6: -MatchOption reaches the engine ──

    [Theory]
    [InlineData(PSPackageFieldMatchOption.Equals, true)]
    [InlineData(PSPackageFieldMatchOption.EqualsCaseInsensitive, true)]
    [InlineData(PSPackageFieldMatchOption.StartsWithCaseInsensitive, false)]
    [InlineData(PSPackageFieldMatchOption.ContainsCaseInsensitive, false)]
    public void MatchOption_ControlsExactMatching(PSPackageFieldMatchOption matchOption, bool exact)
    {
        var query = PingetClient.BuildPackageQuery(null, "Visual Studio Code", null, null, null, null, null, 0, matchOption);
        Assert.Equal(exact, query.Exact);
    }

    [Theory]
    [InlineData(nameof(PingetClient.InstallPackage))]
    [InlineData(nameof(PingetClient.UninstallPackage))]
    [InlineData(nameof(PingetClient.UpdatePackages))]
    [InlineData(nameof(PingetClient.RepairPackage))]
    [InlineData(nameof(PingetClient.DownloadPackage))]
    public void PackageActions_AcceptAMatchOption(string methodName)
    {
        var method = typeof(PingetClient).GetMethod(methodName)!;
        Assert.Contains(method.GetParameters(), p => p.Name == "matchOption" && p.ParameterType == typeof(PSPackageFieldMatchOption));
    }

    // ── R11-7: RebootRequired is surfaced ──

    [Fact]
    public void CompletedInstallResult_ReportsRebootRequiredForMsi3010()
    {
        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes("""
            PackageIdentifier: Contoso.Tool
            PackageVersion: 1.2.3
            PackageName: Contoso Tool
            ManifestType: merged
            ManifestVersion: 1.10.0
            Installers:
              - InstallerType: msi
                InstallerUrl: https://example.test/setup.msi
                InstallerSha256: ABC123
            """));

        var reboot = Repository.CompletedInstallResult(manifest, new Installer(), "setup.msi", "msi", 3010, []);
        Assert.True(reboot.Success);
        Assert.True(reboot.RebootRequired);
        Assert.Contains(InstallerDispatch.InstallRebootRequiredWarning, reboot.Warnings);

        var plain = Repository.CompletedInstallResult(manifest, new Installer(), "setup.exe", "exe", 0, []);
        Assert.True(plain.Success);
        Assert.False(plain.RebootRequired);

        var ps = PingetClient.ToPsInstallResult(reboot, new InstallRequest { Query = new PackageQuery { Id = "Contoso.Tool" } }, null);
        Assert.True(ps.RebootRequired);
        Assert.Equal("Ok", ps.Status);
    }

    // ── R11-8: validate really validates ──

    [Fact]
    public void Validate_AcceptsAValidManifestWithNumericLookingVersion()
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = Path.Combine(directory, "good.yaml");
            File.WriteAllText(manifest, "﻿PackageIdentifier: Contoso.Tool\nPackageVersion: 2.0\nManifestType: singleton\nManifestVersion: 1.6.0\nInstallers:\n  - InstallerUrl: https://example.test/a.exe\n");

            var report = ManifestValidator.Validate(manifest);
            Assert.Empty(report.Errors);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_ReportsMissingRequiredFieldsAndUnparsableFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "bad.yaml"), "PackageIdentifier: Contoso.Tool\nManifestType: singleton\nManifestVersion: 1.6.0\n");
            var report = ManifestValidator.Validate(directory);
            Assert.Contains(report.Errors, e => e.Contains("'Installers'"));
            Assert.Contains(report.Errors, e => e.Contains("'PackageVersion'"));

            var binary = Path.Combine(directory, "tool.exe");
            File.WriteAllBytes(binary, [0x4D, 0x5A, 0x90, 0x00, 0xFF, 0xFE]);
            var error = Assert.Throws<InvalidOperationException>(() => ManifestValidator.Validate(binary));
            Assert.StartsWith("YAML parse error in ", error.Message);

            Assert.Throws<InvalidOperationException>(() => ManifestValidator.Validate(Path.Combine(directory, "missing.yaml")));
            var empty = Path.Combine(directory, "empty");
            Directory.CreateDirectory(empty);
            Assert.Throws<InvalidOperationException>(() => ManifestValidator.Validate(empty));

            var run = RunCli("validate", binary);
            Assert.Equal(1, run.ExitCode);
            run = RunCli("validate", Path.Combine(directory, "bad.yaml"));
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("Manifest validation failed.", run.Stdout);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── R11-9: Update-PingetPackage continues after a failure ──

    [Fact]
    public void UpdateBatch_ContinuesAfterAFailureAndKeepsAllResults()
    {
        var warnings = new List<string>();
        var results = UpdateBatch.Run(
            new[] { "A.First", "B.Broken", "C.Third" },
            id => (id, $"{id} name", "winget"),
            id => id == "B.Broken"
                ? throw new InvalidOperationException("No applicable installer found")
                : new PSInstallResult { Id = id, Name = id, CorrelationData = string.Empty, Status = "Ok" },
            warnings);

        Assert.Equal(["A.First", "B.Broken", "C.Third"], results.Select(r => r.Id).ToArray());
        Assert.Equal(["Ok", "Error", "Ok"], results.Select(r => r.Status).ToArray());
        Assert.Equal("B.Broken name", results[1].Name);
        Assert.Equal("winget", results[1].Source);
        Assert.Contains(warnings, w => w.Contains("B.Broken") && w.Contains("No applicable installer found"));
    }

    // ── R11-11: import files with a BOM or UTF-16 (parity with the Rust fix) ──

    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public void ImportFile_WithBomOrUtf16_Parses(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            _ => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        };
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "packages.json");
            File.WriteAllText(path, """{"Sources":[{"SourceDetails":{"Name":"winget"},"Packages":[{"PackageIdentifier":"Git.Git"}]}]}""", encoding);

            var packages = ImportCommandHandler.ParseImportFile(File.ReadAllText(path), ignoreVersions: false);
            Assert.Equal("Git.Git", Assert.Single(packages).Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RepositoryPackageAndPublish_CliCommandsEmitExpectedJsonAndRejectRemovedSigningFlags()
    {
        var outputOption = new Option<string?>("--output");
        var repository = PublisherCommands.Create(outputOption);
        var packageCommand = Assert.Single(repository.Subcommands, c => c.Name == "package");
        var publishCommand = Assert.Single(repository.Subcommands, c => c.Name == "publish");
        Assert.Equal(
            ["--build", "--destination", "--msix", "--name", "--publisher", "--state", "--version"],
            packageCommand.Options.Select(o => $"--{o.Name}").Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["--build", "--destination", "--msix", "--psign-tool", "--state", "--trusted-ca", "--unsigned-development"],
            publishCommand.Options.Select(o => $"--{o.Name}").Order(StringComparer.Ordinal).ToArray());

        var directory = CreateTempDirectory();
        try
        {
            var input = Path.Combine(directory, "manifests");
            var state = Path.Combine(directory, "state");
            var destination = Path.Combine(directory, "published");
            var msixJsonPath = Path.Combine(directory, "out-json.msix");
            var msixTextPath = Path.Combine(directory, "out-text.msix");
            Directory.CreateDirectory(input);
            File.WriteAllText(
                Path.Combine(input, "pkg.yaml"),
                "PackageIdentifier: Contoso.CliTest\n"
                + "PackageVersion: 1.0.0\n"
                + "PackageLocale: en-US\n"
                + "Publisher: Contoso\n"
                + "PackageName: Contoso CLI Test\n"
                + "License: MIT\n"
                + "ShortDescription: CLI test\n"
                + "Installers:\n"
                + "  - Architecture: x64\n"
                + "    InstallerType: exe\n"
                + "    InstallerUrl: https://example.invalid/setup.exe\n"
                + "    InstallerSha256: 1111111111111111111111111111111111111111111111111111111111111111\n"
                + "ManifestType: singleton\n"
                + "ManifestVersion: 1.9.0\n",
                new UTF8Encoding(false));

            var buildRun = RunCli("repository", "build", "--state", state, "--input", input, "--output", "json");
            Assert.Equal(0, buildRun.ExitCode);
            using var buildDoc = JsonDocument.Parse(buildRun.Stdout);
            var buildId = buildDoc.RootElement.GetProperty("buildId").GetString()!;

            var packageJsonRun = RunCli(
                "repository", "package",
                "--state", state,
                "--name", "Contoso.Source",
                "--publisher", "CN=Contoso Source",
                "--version", "1.0.0.0",
                "--msix", msixJsonPath,
                "--destination", destination,
                "--output", "json");
            Assert.Equal(0, packageJsonRun.ExitCode);
            using var packageDoc = JsonDocument.Parse(packageJsonRun.Stdout);
            Assert.Equal(
                ["buildId", "msixPath", "packageName", "publisher", "packageVersion", "indexHash", "packageHash"],
                packageDoc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(buildId, packageDoc.RootElement.GetProperty("buildId").GetString());
            Assert.Equal(msixJsonPath, packageDoc.RootElement.GetProperty("msixPath").GetString());
            Assert.Equal("Contoso.Source", packageDoc.RootElement.GetProperty("packageName").GetString());
            Assert.Equal("CN=Contoso Source", packageDoc.RootElement.GetProperty("publisher").GetString());
            Assert.Equal("1.0.0.0", packageDoc.RootElement.GetProperty("packageVersion").GetString());

            var packageTextRun = RunCli(
                "repository", "package",
                "--state", state,
                "--name", "Contoso.Source",
                "--publisher", "CN=Contoso Source",
                "--version", "1.0.0.0",
                "--msix", msixTextPath);
            Assert.Equal(0, packageTextRun.ExitCode);
            Assert.Equal($"Packaged unsigned source MSIX for build {buildId} at {msixTextPath}.", packageTextRun.Stdout.Trim());

            var publishRun = RunCli(
                "repository", "publish",
                "--state", state,
                "--destination", destination,
                "--msix", msixJsonPath,
                "--unsigned-development",
                "--output", "json");
            Assert.Equal(0, publishRun.ExitCode);
            using var publishDoc = JsonDocument.Parse(publishRun.Stdout);
            Assert.Equal(
                ["valid", "subject", "artifactCount", "buildId"],
                publishDoc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.True(publishDoc.RootElement.GetProperty("valid").GetBoolean());
            Assert.Equal(buildId, publishDoc.RootElement.GetProperty("buildId").GetString());

            var removedPfx = RunCli(
                "repository", "publish",
                "--state", state,
                "--destination", destination,
                "--msix", msixJsonPath,
                "--pfx", "cert.pfx");
            Assert.NotEqual(0, removedPfx.ExitCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
