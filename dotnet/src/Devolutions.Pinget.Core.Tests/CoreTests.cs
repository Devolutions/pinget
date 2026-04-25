using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Xunit;
using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Core.Tests;

public class VersionCompareTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("0.98.1", "0.98.0", 1)]
    [InlineData("0.98.1", "0.98.1", 0)]
    [InlineData("10.0.0", "9.0.0", 1)]
    [InlineData("1.3.18-stable", "1.3.17-stable", 1)]
    public void CompareVersionStrings_ReturnsCorrectOrdering(string a, string b, int expected)
    {
        var result = RestSource.CompareVersionStrings(a, b);
        Assert.Equal(expected, Math.Sign(result));
    }
}

public class SourceStoreTests
{
    [Fact]
    public void DefaultSources_ContainsWingetAndMsstore()
    {
        var store = SourceStoreManager.Load();
        Assert.Contains(store.Sources, s => s.Name == "winget");
        Assert.Contains(store.Sources, s => s.Name == "msstore");
    }

        [Fact]
        public void PackagedLayout_DefaultsToPackagedSettingsAndCachePaths()
        {
                if (!OperatingSystem.IsWindows())
                        return;

                var appRoot = SourceStoreManager.NormalizeAppRoot(null);
                Assert.EndsWith(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName, "LocalState"), appRoot, StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName, "LocalState", "settings.json"), SettingsStoreManager.UserSettingsPath(appRoot), StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName, "LocalState", "Microsoft", "Windows Package Manager"), SourceStoreManager.GetPackagedFileCacheRoot(appRoot), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PackagedSourceYaml_OverlaysDefaultsAndMetadata()
        {
                const string userSourcesYaml = @"Sources:
    - Name: winget
        Type: Microsoft.PreIndexed.Package
        Arg: https://cdn.winget.microsoft.com/cache
        Data: Microsoft.Winget.Source_8wekyb3d8bbwe
        IsTombstone: true
    - Name: corp
        Type: Microsoft.Rest
        Arg: ""https://packages.contoso.test/api""
        Data: Contoso.Rest
        Explicit: true
        Priority: 7
        TrustLevel: 1
        IsTombstone: false
";

                const string metadataYaml = @"Sources:
    - Name: corp
        LastUpdate: 1700000000
        SourceVersion: 1.2.3
";

                var store = SourceStoreManager.ParsePackagedSourceStore(userSourcesYaml, metadataYaml);
                Assert.NotNull(store);
                Assert.DoesNotContain(store!.Sources, source => source.Name == "winget");

                var corp = Assert.Single(store.Sources, source => source.Name == "corp");
                Assert.Equal(SourceKind.Rest, corp.Kind);
                Assert.Equal("Contoso.Rest", corp.Identifier);
                Assert.Equal("Trusted", corp.TrustLevel);
                Assert.True(corp.Explicit);
                Assert.Equal(7, corp.Priority);
                Assert.Equal("1.2.3", corp.SourceVersion);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, corp.LastUpdate);
        }

    [Fact]
    public void RepositoryOpen_UsesCustomAppRoot()
    {
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                UserAgent = "pinget-dotnet-tests/1.0",
            });

            repo.AddSource("test", "https://example.com/test", SourceKind.Rest);

            Assert.Equal(Path.GetFullPath(appRoot), repo.AppRoot);
            Assert.True(File.Exists(Path.Combine(appRoot, "sources.json")));

            var store = SourceStoreManager.Load(appRoot);
            Assert.Contains(store.Sources, s => s.Name == "test");
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void EditSourceAndResetSource_PreserveCustomMetadata()
    {
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            repo.AddSource("test", "https://example.com/test", SourceKind.Rest, trustLevel: "trusted", explicitSource: true, priority: 4);

            repo.EditSource("test", explicitSource: false);
            var edited = Assert.Single(repo.ListSources(), source => source.Name == "test");
            Assert.Equal("Trusted", edited.TrustLevel);
            Assert.False(edited.Explicit);
            Assert.Equal(4, edited.Priority);

            repo.ResetSource("test");
            var reset = Assert.Single(repo.ListSources(), source => source.Name == "test");
            Assert.Equal("Trusted", reset.TrustLevel);
            Assert.False(reset.Explicit);
            Assert.Equal(4, reset.Priority);
            Assert.Null(reset.LastUpdate);
            Assert.Null(reset.SourceVersion);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void UserAndAdminSettings_RoundTripAndReset()
    {
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });

            repo.SetUserSettings(new JsonObject
            {
                ["visual"] = new JsonObject
                {
                    ["progressBar"] = "retro"
                }
            }, merge: false);
            repo.SetUserSettings(new JsonObject
            {
                ["experimentalFeatures"] = new JsonObject
                {
                    ["directMSI"] = true
                }
            }, merge: true);

            var userSettings = repo.GetUserSettings();
            Assert.Equal("retro", userSettings["visual"]?["progressBar"]?.GetValue<string>());
            Assert.True(userSettings["experimentalFeatures"]?["directMSI"]?.GetValue<bool>());
            Assert.True(repo.TestUserSettings(new JsonObject
            {
                ["experimentalFeatures"] = new JsonObject
                {
                    ["directMSI"] = true
                }
            }, ignoreNotSet: true));

            repo.SetAdminSetting("LocalManifestFiles", true);
            repo.SetAdminSetting("InstallerHashOverride", true);
            var adminSettings = repo.GetAdminSettings();
            Assert.True(adminSettings["LocalManifestFiles"]?.GetValue<bool>());
            Assert.True(adminSettings["InstallerHashOverride"]?.GetValue<bool>());

            repo.ResetAdminSetting("LocalManifestFiles");
            Assert.False(repo.GetAdminSettings()["LocalManifestFiles"]?.GetValue<bool>());

            repo.ResetAdminSetting(resetAll: true);
            foreach (var setting in Repository.SupportedAdminSettings)
            {
                Assert.False(repo.GetAdminSettings()[setting]?.GetValue<bool>());
            }
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }
}

public class ModelsTests
{
    [Fact]
    public void SearchMatch_RequiredProperties()
    {
        var match = new SearchMatch
        {
            SourceName = "winget",
            SourceKind = SourceKind.PreIndexed,
            Id = "Test.Package",
            Name = "Test Package",
        };
        Assert.Equal("Test.Package", match.Id);
        Assert.Equal("Test Package", match.Name);
        Assert.Equal("winget", match.SourceName);
        Assert.Null(match.MatchCriteria);
    }

    [Fact]
    public void Installer_DefaultValues()
    {
        var installer = new Installer();
        Assert.Null(installer.Architecture);
        Assert.Null(installer.InstallerType);
        Assert.Null(installer.NestedInstallerType);
        Assert.Null(installer.Url);
        Assert.Null(installer.Scope);
        Assert.Null(installer.ProductCode);
        Assert.True(installer.Switches.IsEmpty());
        Assert.Empty(installer.Commands);
        Assert.Empty(installer.PackageDependencies);
    }

    [Fact]
    public void Manifest_DefaultCollections()
    {
        var manifest = new Manifest
        {
            Id = "Test.Id",
            Name = "Test",
            Version = "1.0.0",
            Channel = "",
        };
        Assert.Empty(manifest.Tags);
        Assert.Empty(manifest.Installers);
        Assert.Empty(manifest.PackageDependencies);
        Assert.Empty(manifest.Documentation);
    }

    [Fact]
    public void ShowResult_ToStructuredDocument_UsesManifestSchema()
    {
        var result = new ShowResult
        {
            Package = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Test.Package",
                Name = "Test Package",
                MatchCriteria = "Id",
            },
            Manifest = new Manifest
            {
                Id = "Test.Package",
                Name = "Test Package",
                Version = "1.2.3",
                Channel = "stable",
                Publisher = "Contoso",
                Description = "Structured output",
                Tags = ["utility"],
                PackageDependencies = ["Microsoft.VCRedist.2015+.x64"],
                Documentation =
                [
                    new Documentation { Label = "Docs", Url = "https://example.test/docs" }
                ],
                Installers =
                [
                    new Installer
                    {
                        Architecture = "x64",
                        InstallerType = "msix",
                        Url = "https://example.test/Test.Package.msix",
                        Sha256 = "ABC123",
                        Locale = "en-US",
                        Scope = "machine",
                        Switches = new InstallerSwitches { Silent = "/quiet" },
                        Commands = ["testpkg"],
                        PackageDependencies = ["Microsoft.UI.Xaml.2.8"],
                    }
                ],
            },
            SelectedInstaller = new Installer
            {
                Architecture = "x64",
                InstallerType = "msix",
                Url = "https://example.test/Test.Package.msix",
                Sha256 = "ABC123",
                Locale = "en-US",
                Scope = "machine",
                Switches = new InstallerSwitches { Silent = "/quiet" },
                Commands = ["testpkg"],
                PackageDependencies = ["Microsoft.UI.Xaml.2.8"],
            },
            CachedFiles = [@"C:\temp\cache\Test.Package.yaml"],
            Warnings = ["cache warmed"],
            StructuredDocument = new List<Dictionary<string, object?>>
            {
                new Dictionary<string, object?>
                {
                    ["PackageIdentifier"] = "Test.Package",
                    ["PackageVersion"] = "1.2.3",
                    ["DefaultLocale"] = "en-US",
                    ["ManifestType"] = "version",
                    ["ManifestVersion"] = "1.10.0",
                },
                new Dictionary<string, object?>
                {
                    ["PackageIdentifier"] = "Test.Package",
                    ["PackageVersion"] = "1.2.3",
                    ["PackageLocale"] = "en-US",
                    ["PackageName"] = "Test Package",
                    ["Publisher"] = "Example",
                    ["License"] = "MIT",
                    ["ShortDescription"] = "Structured output",
                    ["ManifestType"] = "defaultLocale",
                    ["ManifestVersion"] = "1.10.0",
                },
                new Dictionary<string, object?>
                {
                    ["PackageIdentifier"] = "Test.Package",
                    ["PackageVersion"] = "1.2.3",
                    ["ManifestType"] = "installer",
                    ["ManifestVersion"] = "1.10.0",
                    ["Installers"] = new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["Architecture"] = "x64",
                            ["InstallerType"] = "msix",
                            ["InstallerUrl"] = "https://example.test/Test.Package.msix",
                            ["InstallerSha256"] = "ABC123",
                            ["Commands"] = new List<string> { "testpkg" },
                            ["InstallerSwitches"] = new Dictionary<string, object?> { ["Silent"] = "/quiet" },
                            ["Dependencies"] = new Dictionary<string, object?>
                            {
                                ["PackageDependencies"] = new List<Dictionary<string, object?>>
                                {
                                    new() { ["PackageIdentifier"] = "Microsoft.VCRedist.2015+.x64" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var document = Assert.IsType<Dictionary<string, object?>>(result.ToStructuredDocument());
        Assert.Equal("singleton", document["ManifestType"]);
        Assert.Equal("1.10.0", document["ManifestVersion"]);
        Assert.Equal("en-US", document["PackageLocale"]);

        var installers = Assert.IsType<List<Dictionary<string, object?>>>(document["Installers"]);
        var selectedInstaller = installers[0];
        var dependencies = Assert.IsType<Dictionary<string, object?>>(selectedInstaller["Dependencies"]);
        var packageDependencies = Assert.IsType<List<Dictionary<string, object?>>>(dependencies["PackageDependencies"]);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", packageDependencies[0]["PackageIdentifier"]);
        var commands = Assert.IsType<List<string>>(selectedInstaller["Commands"]);
        Assert.Equal("testpkg", commands[0]);
        var switches = Assert.IsType<Dictionary<string, object?>>(selectedInstaller["InstallerSwitches"]);
        Assert.Equal("/quiet", switches["Silent"]);
    }

    [Fact]
    public void ParseYamlManifestDocuments_PreservesManifestDocuments()
    {
        var yaml = """
            PackageIdentifier: Test.Package
            PackageVersion: 1.2.3
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.10.0
            ---
            PackageIdentifier: Test.Package
            PackageVersion: 1.2.3
            PackageLocale: en-US
            PackageName: Test Package
            Publisher: Example
            License: MIT
            ShortDescription: Structured output
            ManifestType: defaultLocale
            ManifestVersion: 1.10.0
            ---
            PackageIdentifier: Test.Package
            PackageVersion: 1.2.3
            ManifestType: installer
            ManifestVersion: 1.10.0
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/Test.Package.exe
                InstallerSha256: ABC123
            """;

        var documents = Assert.IsType<List<Dictionary<string, object?>>>(Repository.ParseYamlManifestDocuments(System.Text.Encoding.UTF8.GetBytes(yaml)));
        var document = new ShowResult
        {
            Package = new SearchMatch
            {
                Id = "Test.Package",
                Name = "Test Package",
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
            },
            Manifest = new Manifest
            {
                Id = "Test.Package",
                Name = "Test Package",
                Version = "1.2.3",
                Installers = [],
            },
            StructuredDocument = documents,
        }.ToStructuredDocument();

        var collapsed = Assert.IsType<Dictionary<string, object?>>(document);
        Assert.Equal("singleton", collapsed["ManifestType"]);
        Assert.Equal("Test.Package", collapsed["PackageIdentifier"]);
        Assert.Equal("Test Package", collapsed["PackageName"]);
    }

    [Fact]
    public void CollapseManifestResults_ReturnsPluralShowDocuments()
    {
        var results = StructuredOutput.CollapseManifestResults(
        [
            new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["PackageIdentifier"] = "Test.Package.One",
                    ["PackageVersion"] = "1.0.0",
                    ["DefaultLocale"] = "en-US",
                    ["ManifestType"] = "version",
                    ["ManifestVersion"] = "1.10.0",
                },
                new()
                {
                    ["PackageIdentifier"] = "Test.Package.One",
                    ["PackageVersion"] = "1.0.0",
                    ["PackageLocale"] = "en-US",
                    ["PackageName"] = "Test Package One",
                    ["ManifestType"] = "defaultLocale",
                    ["ManifestVersion"] = "1.10.0",
                },
                new()
                {
                    ["PackageIdentifier"] = "Test.Package.One",
                    ["PackageVersion"] = "1.0.0",
                    ["ManifestType"] = "installer",
                    ["ManifestVersion"] = "1.10.0",
                    ["Installers"] = new List<Dictionary<string, object?>>
                    {
                        new()
                        {
                            ["Architecture"] = "x64",
                            ["InstallerType"] = "exe",
                            ["InstallerUrl"] = "https://example.test/one.exe",
                            ["InstallerSha256"] = "ABC123",
                        }
                    }
                }
            },
            new Dictionary<string, object?>
            {
                ["PackageIdentifier"] = "Test.Package.Two",
                ["PackageVersion"] = "2.0.0",
                ["PackageLocale"] = "en-US",
                ["PackageName"] = "Test Package Two",
                ["ManifestType"] = "singleton",
                ["ManifestVersion"] = "1.12.0",
            }
        ]);

        Assert.Equal(2, results.Count);
        Assert.Equal("singleton", results[0]["ManifestType"]);
        Assert.Equal("Test.Package.One", results[0]["PackageIdentifier"]);
        Assert.Equal("Test Package One", results[0]["PackageName"]);
        Assert.Equal("singleton", results[1]["ManifestType"]);
        Assert.Equal("Test.Package.Two", results[1]["PackageIdentifier"]);
    }

    [Fact]
    public void CreateUnsupportedActionResult_MarksNoOpAndWarning()
    {
        var result = Repository.CreateUnsupportedActionResult(
            "Contoso.App",
            "1.2.3",
            "install",
            Repository.InstallUnsupportedWarning);

        Assert.True(result.Success);
        Assert.True(result.NoOp);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Warnings);
        Assert.Equal(Repository.InstallUnsupportedWarning, result.Warnings[0]);
    }

    [Fact]
    public void ParseYamlManifest_ReadsInstallerSwitches()
    {
        var yaml = """
            PackageIdentifier: Test.Package
            PackageVersion: 1.2.3
            PackageName: Test Package
            InstallerSwitches:
              SilentWithProgress: /SILENT
            Installers:
              - Architecture: x64
                InstallerType: inno
                InstallerUrl: https://example.test/Test.Package.exe
                InstallerSha256: ABC123
                InstallerSwitches:
                  Silent: /VERYSILENT
                  Interactive: /HELP
            """;

        var manifest = Repository.ParseYamlManifest(System.Text.Encoding.UTF8.GetBytes(yaml));
        var installer = Assert.Single(manifest.Installers);

        Assert.Equal("/SILENT", installer.Switches.SilentWithProgress);
        Assert.Equal("/VERYSILENT", installer.Switches.Silent);
        Assert.Equal("/HELP", installer.Switches.Interactive);
    }

    [Fact]
    public void ParseYamlManifest_PreservesPlatformAndMinimumOsVersion()
    {
        var yaml = """
            PackageIdentifier: Test.Package
            PackageVersion: 1.2.3
            PackageName: Test Package
            Platform:
              - Windows.Desktop
            MinimumOSVersion: 10.0.19041.0
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/Test.Package.exe
                InstallerSha256: ABC123
              - Architecture: x64
                InstallerType: msix
                Platform:
                  - Windows.Universal
                MinimumOSVersion: 10.0.22621.0
                InstallerUrl: https://example.test/Test.Package.msix
                InstallerSha256: DEF456
            """;

        var manifest = Repository.ParseYamlManifest(System.Text.Encoding.UTF8.GetBytes(yaml));

        Assert.Equal(["Windows.Desktop"], manifest.Installers[0].Platforms);
        Assert.Equal("10.0.19041.0", manifest.Installers[0].MinimumOsVersion);
        Assert.Equal(["Windows.Universal"], manifest.Installers[1].Platforms);
        Assert.Equal("10.0.22621.0", manifest.Installers[1].MinimumOsVersion);
    }

    [Fact]
    public void ParseYamlManifest_PreservesTopLevelNestedInstallerType()
    {
        var yaml = """
            PackageIdentifier: Test.Package
            PackageVersion: 1.2.3
            PackageName: Test Package
            InstallerType: zip
            NestedInstallerType: portable
            Installers:
              - Architecture: x64
                InstallerUrl: https://example.test/Test.Package.zip
                InstallerSha256: ABC123
            """;

        var manifest = Repository.ParseYamlManifest(System.Text.Encoding.UTF8.GetBytes(yaml));
        var installer = Assert.Single(manifest.Installers);

        Assert.Equal("zip", installer.InstallerType);
        Assert.Equal("portable", installer.NestedInstallerType);
    }
}

public class PinStoreTests
{
    [Fact]
    public void AddListRemove_RoundTrips()
    {
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            PinStore.Add("Test.Package.Unit", "1.0.0", "winget", PinType.Pinning, appRoot);

            var pins = PinStore.List(appRoot);
            Assert.Contains(pins, p => p.PackageId == "Test.Package.Unit");
            var pin = pins.First(p => p.PackageId == "Test.Package.Unit");
            Assert.Equal("1.0.0", pin.Version);
            Assert.Equal(PinType.Pinning, pin.PinType);

            PinStore.Remove("Test.Package.Unit", appRoot);
            Assert.DoesNotContain(PinStore.List(appRoot), p => p.PackageId == "Test.Package.Unit");
        }
        finally
        {
            PinStore.Reset(appRoot);
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void RemoveAndReset_CanTargetSpecificSource()
    {
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            PinStore.Add("Test.Package.Unit", "1.0.0", "winget", PinType.Pinning, appRoot);
            PinStore.Add("Test.Package.Unit", "1.0.0", "msstore", PinType.Blocking, appRoot);

            Assert.True(PinStore.Remove("Test.Package.Unit", appRoot, "winget"));
            var remaining = PinStore.List(appRoot);
            Assert.Single(remaining);
            Assert.Equal("msstore", remaining[0].SourceId);

            PinStore.Reset(appRoot, "msstore");
            Assert.Empty(PinStore.List(appRoot));
        }
        finally
        {
            PinStore.Reset(appRoot);
            TestPaths.DeleteAppRoot(appRoot);
        }
    }
}

public class RepositoryParityTests
{
    [Fact]
    public void FindApplicablePin_PrefersSourceSpecificPin()
    {
        var match = new ListMatch
        {
            Name = "Test Package",
            Id = "Test.Package",
            LocalId = @"ARP\Machine\X64\Test.Package",
            InstalledVersion = "1.0.0",
            AvailableVersion = "2.0.0",
            SourceName = "winget",
        };

        var pins = new List<PinRecord>
        {
            new() { PackageId = "Test.Package", Version = "1.*", SourceId = "", PinType = PinType.Pinning },
            new() { PackageId = "Test.Package", Version = "1.0.0", SourceId = "winget", PinType = PinType.Blocking },
        };

        var selected = Repository.FindApplicablePin(match, pins);

        Assert.NotNull(selected);
        Assert.Equal(PinType.Blocking, selected!.PinType);
        Assert.Equal("winget", selected.SourceId);
    }

    [Fact]
    public void IsUpgradeBlockedByPin_RespectsBlockingAndVersionPatterns()
    {
        var match = new ListMatch
        {
            Name = "Test Package",
            Id = "Test.Package",
            LocalId = @"ARP\Machine\X64\Test.Package",
            InstalledVersion = "1.0.0",
            AvailableVersion = "2.0.0",
            SourceName = "winget",
        };

        Assert.True(Repository.IsUpgradeBlockedByPin(match,
        [
            new PinRecord { PackageId = "Test.Package", Version = "*", SourceId = "winget", PinType = PinType.Blocking }
        ]));

        Assert.True(Repository.IsUpgradeBlockedByPin(match,
        [
            new PinRecord { PackageId = "Test.Package", Version = "1.5.*", SourceId = "winget", PinType = PinType.Pinning }
        ]));

        Assert.False(Repository.IsUpgradeBlockedByPin(match with { AvailableVersion = "1.5.9" },
        [
            new PinRecord { PackageId = "Test.Package", Version = "1.5.*", SourceId = "winget", PinType = PinType.Pinning }
        ]));
    }

    [Fact]
    public void CreateInstallNoOpResult_RespectsNoUpgrade()
    {
        var result = Repository.CreateInstallNoOpResult(
            new InstallRequest
            {
                Query = new PackageQuery(),
                NoUpgrade = true,
            },
            new Manifest
            {
                Id = "Test.Package",
                Name = "Test Package",
                Version = "2.0.0",
            },
            new ListMatch
            {
                Name = "Test Package",
                Id = "Test.Package",
                LocalId = @"ARP\Machine\X64\Test.Package",
                InstalledVersion = "1.0.0",
            });

        Assert.NotNull(result);
        Assert.True(result!.NoOp);
        Assert.Equal("1.0.0", result.Version);
        Assert.Contains(result.Warnings, warning => warning.Contains("--no-upgrade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateInstallNoOpResult_SkipsReinstallWhenAlreadyCurrent()
    {
        var result = Repository.CreateInstallNoOpResult(
            new InstallRequest
            {
                Query = new PackageQuery(),
            },
            new Manifest
            {
                Id = "Test.Package",
                Name = "Test Package",
                Version = "2.0.0",
            },
            new ListMatch
            {
                Name = "Test Package",
                Id = "Test.Package",
                LocalId = @"ARP\Machine\X64\Test.Package",
                InstalledVersion = "2.0.0",
            });

        Assert.NotNull(result);
        Assert.True(result!.NoOp);
        Assert.Contains(result.Warnings, warning => warning.Contains("up to date", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateInstallNoOpResult_AllowsUpgradeWhenNewerVersionExists()
    {
        var result = Repository.CreateInstallNoOpResult(
            new InstallRequest
            {
                Query = new PackageQuery(),
            },
            new Manifest
            {
                Id = "Test.Package",
                Name = "Test Package",
                Version = "2.0.0",
            },
            new ListMatch
            {
                Name = "Test Package",
                Id = "Test.Package",
                LocalId = @"ARP\Machine\X64\Test.Package",
                InstalledVersion = "1.0.0",
            });

        Assert.Null(result);
    }

    [Fact]
    public void DownloadInstaller_RejectsHashMismatchByDefault()
    {
        var payload = "pinget-test-payload"u8.ToArray();
        using var server = new TestHttpServer(payload);
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            var manifestPath = TestPaths.WriteManifest(appRoot, $$"""
                PackageIdentifier: Test.Package
                PackageVersion: 1.0.0
                PackageName: Test Package
                ManifestType: merged
                ManifestVersion: 1.10.0
                Installers:
                  - InstallerType: exe
                    InstallerUrl: {{server.Url}}
                    InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000
                """);

            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            var request = new InstallRequest
            {
                Query = new PackageQuery(),
                ManifestPath = manifestPath,
            };

            var ex = Assert.Throws<InvalidOperationException>(() => repo.DownloadInstaller(request, Path.Combine(appRoot, "downloads")));
            Assert.Contains("Installer hash mismatch", ex.Message);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void DownloadInstaller_IgnoresHashMismatchWhenRequested()
    {
        var payload = "pinget-test-payload"u8.ToArray();
        using var server = new TestHttpServer(payload);
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            var manifestPath = TestPaths.WriteManifest(appRoot, $$"""
                PackageIdentifier: Test.Package
                PackageVersion: 1.0.0
                PackageName: Test Package
                ManifestType: merged
                ManifestVersion: 1.10.0
                Installers:
                  - InstallerType: exe
                    InstallerUrl: {{server.Url}}
                    InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000
                """);

            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            var request = new InstallRequest
            {
                Query = new PackageQuery(),
                ManifestPath = manifestPath,
                IgnoreSecurityHash = true,
            };

            var (_, installerPath) = repo.DownloadInstaller(request, Path.Combine(appRoot, "downloads"));
            Assert.True(File.Exists(installerPath));
            Assert.Equal(payload, File.ReadAllBytes(installerPath));
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void DownloadInstaller_SendsConfiguredRequestHeaders()
    {
        var payload = "pinget-test-payload"u8.ToArray();
        string? authorizationHeader = null;
        using var server = new TestHttpServer(payload, context =>
        {
            authorizationHeader = context.Request.Headers["Authorization"];
        });
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            var manifestPath = TestPaths.WriteManifest(appRoot, $$"""
                PackageIdentifier: Test.Package
                PackageVersion: 1.0.0
                PackageName: Test Package
                ManifestType: merged
                ManifestVersion: 1.10.0
                Installers:
                  - InstallerType: exe
                    InstallerUrl: {{server.Url}}
                """);

            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            repo.SetRequestHeader("Authorization", "Bearer test-token");

            var request = new InstallRequest
            {
                Query = new PackageQuery(),
                ManifestPath = manifestPath,
            };

            var (_, installerPath) = repo.DownloadInstaller(request, Path.Combine(appRoot, "downloads"));
            Assert.True(File.Exists(installerPath));
            Assert.Equal("Bearer test-token", authorizationHeader);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void BuildArguments_UsesManifestSwitchesByMode()
    {
        var installer = new Installer
        {
            InstallerType = "inno",
            Switches = new InstallerSwitches
            {
                Silent = "/mysilent",
                SilentWithProgress = "/mysilentwithprogress",
                Interactive = "/myinteractive"
            }
        };

        Assert.Equal(["/mysilent"], InstallerDispatch.BuildArguments("inno", InstallerMode.Silent, installer));
        Assert.Equal(["/mysilentwithprogress"], InstallerDispatch.BuildArguments("inno", InstallerMode.SilentWithProgress, installer));
        Assert.Equal(["/myinteractive"], InstallerDispatch.BuildArguments("inno", InstallerMode.Interactive, installer));
    }

    [Fact]
    public void BuildArguments_UsesInnoDefaultsWhenManifestOmitsSwitches()
    {
        var installer = new Installer { InstallerType = "inno" };

        Assert.Equal(["/SP-", "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], InstallerDispatch.BuildArguments("inno", InstallerMode.SilentWithProgress, installer));
        Assert.Equal(["/SP-", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], InstallerDispatch.BuildArguments("inno", InstallerMode.Silent, installer));
    }

    [Fact]
    public void BuildArguments_AppendsManifestAndCliSwitches()
    {
        var installer = new Installer
        {
            InstallerType = "msi",
            Switches = new InstallerSwitches
            {
                Custom = "ADDLOCAL=Core",
                Log = "/log \"<LOGPATH>\"",
                InstallLocation = "TARGETDIR=\"<INSTALLPATH>\"",
            }
        };

        var args = InstallerDispatch.BuildArguments(
            "msi",
            new InstallRequest
            {
                Query = new PackageQuery(),
                Mode = InstallerMode.Silent,
                LogPath = @"C:\temp\winget.log",
                Custom = "REBOOT=ReallySuppress",
                InstallLocation = @"C:\Apps\ShareX",
            },
            new Manifest { Id = "ShareX.ShareX", Name = "ShareX", Version = "19.0.2" },
            installer,
            @"C:\temp\ShareX.msi");

        Assert.Equal(["/i", @"C:\temp\ShareX.msi", "/quiet", "/norestart", "/log", @"C:\temp\winget.log", "ADDLOCAL=Core", "REBOOT=ReallySuppress", @"TARGETDIR=C:\Apps\ShareX"], args);
    }

    [Fact]
    public void BuildArguments_UsesOverrideInsteadOfSynthesizedArguments()
    {
        var installer = new Installer { InstallerType = "inno" };
        var args = InstallerDispatch.BuildArguments(
            "inno",
            new InstallRequest
            {
                Query = new PackageQuery(),
                Override = "/custom /args",
            },
            new Manifest { Id = "Test.Package", Name = "Test", Version = "1.0.0" },
            installer);

        Assert.Equal(["/custom", "/args"], args);
    }

    [Fact]
    public void GetArpSubkeyName_ExtractsRegistrySubkey()
    {
        Assert.Equal("ShareX", InstallerDispatch.GetArpSubkeyName(@"ARP\Machine\X64\ShareX"));
        Assert.Null(InstallerDispatch.GetArpSubkeyName(@"MSIX\ShareX_19.0.2_x64__name"));
    }

    [Fact]
    public void RegistryEntryMatchesInstalledPackage_UsesLocalIdentityInsteadOfCorrelatedId()
    {
        var installed = new ListMatch
        {
            Name = "ShareX",
            Id = "ShareX.ShareX",
            LocalId = @"ARP\Machine\X64\ShareX",
            InstalledVersion = "19.0.2",
            ProductCodes = [],
        };

        Assert.True(InstallerDispatch.RegistryEntryMatchesInstalledPackage("ShareX", "ShareX", null, installed));
        Assert.False(InstallerDispatch.RegistryEntryMatchesInstalledPackage("ShareX.ShareX", "ShareX.ShareX", null, installed));
    }

    [Fact]
    public void BuildUninstallCommand_AppendsSilentSwitchOnlyWhenNeeded()
    {
        Assert.Equal("\"C:\\Program Files\\ShareX\\unins000.exe\" /S",
            InstallerDispatch.BuildUninstallCommand("\"C:\\Program Files\\ShareX\\unins000.exe\"", silent: true, hasQuietUninstallCommand: false));
        Assert.Equal("\"C:\\Program Files\\ShareX\\unins000.exe\" /VERYSILENT",
            InstallerDispatch.BuildUninstallCommand("\"C:\\Program Files\\ShareX\\unins000.exe\" /VERYSILENT", silent: true, hasQuietUninstallCommand: false));
        Assert.Equal("\"C:\\Program Files\\ShareX\\unins000.exe\"",
            InstallerDispatch.BuildUninstallCommand("\"C:\\Program Files\\ShareX\\unins000.exe\"", silent: false, hasQuietUninstallCommand: false));

        Assert.Equal(
            "winget uninstall --product-code JesseDuffield.lazygit_Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallerDispatch.BuildUninstallCommand(
                "winget uninstall --product-code JesseDuffield.lazygit_Microsoft.Winget.Source_8wekyb3d8bbwe",
                silent: true,
                hasQuietUninstallCommand: false));
    }

    [Fact]
    public void SelectInstaller_PrefersRustStyleRanking()
    {
        var installers = new List<Installer>
        {
            new()
            {
                Architecture = "x64",
                InstallerType = "exe",
                Scope = "user",
                Locale = "en-US",
                Switches = new InstallerSwitches(),
            },
            new()
            {
                Architecture = "x64",
                InstallerType = "exe",
                Scope = "machine",
                Locale = "en-US",
                Switches = new InstallerSwitches(),
                Commands = ["powertoys"],
            },
        };

        var selected = Repository.SelectInstaller(installers, new PackageQuery { InstallerType = "exe" });

        Assert.NotNull(selected);
        Assert.Equal("machine", selected!.Scope);
    }

    [Fact]
    public void SelectInstaller_PrefersLanguageFallbackOverMismatchedLocale()
    {
        var installers = new List<Installer>
        {
            new() { Architecture = "x64", InstallerType = "exe", Locale = "fr-FR", Switches = new InstallerSwitches() },
            new() { Architecture = "x64", InstallerType = "exe", Locale = "en-GB", Switches = new InstallerSwitches() },
        };

        var selected = Repository.SelectInstaller(installers, new PackageQuery
        {
            InstallerType = "exe",
            Locale = "en-US",
        });

        Assert.NotNull(selected);
        Assert.Equal("en-GB", selected!.Locale);
    }

    [Fact]
    public void SelectInstaller_RespectsRequestedPlatform()
    {
        var installers = new List<Installer>
        {
            new() { Architecture = "x64", InstallerType = "exe", Platforms = ["Windows.Universal"], Switches = new InstallerSwitches() },
            new() { Architecture = "x64", InstallerType = "exe", Platforms = ["Windows.Desktop"], Switches = new InstallerSwitches() },
        };

        var selected = Repository.SelectInstaller(installers, new PackageQuery
        {
            InstallerType = "exe",
            Platform = "Windows.Desktop",
        });

        Assert.NotNull(selected);
        Assert.Equal(["Windows.Desktop"], selected!.Platforms);
    }

    [Fact]
    public void SelectInstaller_RespectsRequestedOsVersion()
    {
        var installers = new List<Installer>
        {
            new() { Architecture = "x64", InstallerType = "exe", MinimumOsVersion = "10.0.22621.0", Switches = new InstallerSwitches() },
            new() { Architecture = "x64", InstallerType = "exe", MinimumOsVersion = "10.0.19041.0", Switches = new InstallerSwitches() },
        };

        var selected = Repository.SelectInstaller(installers, new PackageQuery
        {
            InstallerType = "exe",
            OsVersion = "10.0.19045.0",
        });

        Assert.NotNull(selected);
        Assert.Equal("10.0.19041.0", selected!.MinimumOsVersion);
    }

    [Fact]
    public void SelectInstaller_AllowsRequestedScopeWhenInstallerScopeMissing()
    {
        var installers = new List<Installer>
        {
            new() { Architecture = "x64", InstallerType = "zip", Scope = null, Switches = new InstallerSwitches() },
            new() { Architecture = "x64", InstallerType = "exe", Scope = "machine", Switches = new InstallerSwitches() },
        };

        var selected = Repository.SelectInstaller(installers, new PackageQuery
        {
            InstallerType = "zip",
            InstallScope = "user",
        });

        Assert.NotNull(selected);
        Assert.Equal("zip", selected!.InstallerType);
        Assert.Null(selected.Scope);
    }

    [Fact]
    public void BuildWingetPortableInstallArguments_PreservesWingetCoherenceFlags()
    {
        var manifest = new Manifest
        {
            Id = "JesseDuffield.lazygit",
            Name = "lazygit",
            Version = "0.61.1",
        };

        var request = new InstallRequest
        {
            Query = new PackageQuery
            {
                Id = "JesseDuffield.lazygit",
                Source = "winget",
                InstallScope = "user",
            },
            Mode = InstallerMode.Silent,
            AcceptPackageAgreements = true,
        };

        Assert.Equal(
            new[]
            {
                "install",
                "--id",
                "JesseDuffield.lazygit",
                "--exact",
                "--accept-source-agreements",
                "--disable-interactivity",
                "--source",
                "winget",
                "--scope",
                "user",
                "--accept-package-agreements",
                "--silent",
            },
            InstallerDispatch.BuildWingetPortableInstallArguments(request, manifest));
    }

    [Fact]
    public void ShouldDelegatePortableZipInstall_ForNestedPortableZip()
    {
        var manifest = new Manifest
        {
            Id = "JesseDuffield.lazygit",
            Name = "lazygit",
            Version = "0.61.1",
        };

        var request = new InstallRequest
        {
            Query = new PackageQuery
            {
                Id = "JesseDuffield.lazygit",
                Source = "winget",
            },
        };

        var installer = new Installer
        {
            InstallerType = "zip",
            NestedInstallerType = "portable",
        };

        Assert.True(InstallerDispatch.ShouldDelegatePortableZipInstall(request, manifest, installer));
    }

    [Fact]
    public void CreateRepairListQuery_IncludesInstalledSelectors()
    {
        var request = new RepairRequest
        {
            Query = new PackageQuery
            {
                Query = "powertoys",
                Id = "Microsoft.PowerToys",
                Name = "PowerToys",
                Moniker = "powertoys",
                Source = "winget",
                Version = "0.90.1",
                InstallScope = "machine",
                Exact = true,
            },
            ProductCode = "{1234-5678}",
        };

        var listQuery = Repository.CreateRepairListQuery(request);

        Assert.Equal("powertoys", listQuery.Query);
        Assert.Equal("Microsoft.PowerToys", listQuery.Id);
        Assert.Equal("PowerToys", listQuery.Name);
        Assert.Equal("powertoys", listQuery.Moniker);
        Assert.Equal("{1234-5678}", listQuery.ProductCode);
        Assert.Equal("0.90.1", listQuery.Version);
        Assert.Equal("winget", listQuery.Source);
        Assert.Equal("machine", listQuery.InstallScope);
        Assert.True(listQuery.Exact);
        Assert.Equal(100, listQuery.Count);
    }

    [Fact]
    public void CreateRepairInstallRequest_ForcesReinstallOfResolvedInstalledPackage()
    {
        var request = new RepairRequest
        {
            Query = new PackageQuery
            {
                Name = "PowerToys",
                Source = "winget",
                Version = "0.90.1",
                InstallerArchitecture = "x64",
                Locale = "en-US",
                InstallScope = "machine",
            },
            Mode = InstallerMode.Interactive,
            LogPath = @"C:\temp\repair.log",
            AcceptPackageAgreements = true,
            IgnoreSecurityHash = true,
        };
        var installed = new ListMatch
        {
            Id = "Microsoft.PowerToys",
            LocalId = @"ARP\Machine\X64\PowerToys",
            Name = "PowerToys",
            InstalledVersion = "0.90.1",
            SourceName = "winget",
            ProductCodes = [],
        };

        var installRequest = Repository.CreateRepairInstallRequest(request, installed);

        Assert.Equal("Microsoft.PowerToys", installRequest.Query.Id);
        Assert.Equal("winget", installRequest.Query.Source);
        Assert.Equal("0.90.1", installRequest.Query.Version);
        Assert.Equal("x64", installRequest.Query.InstallerArchitecture);
        Assert.Equal("en-US", installRequest.Query.Locale);
        Assert.Equal("machine", installRequest.Query.InstallScope);
        Assert.True(installRequest.Query.Exact);
        Assert.True(installRequest.Force);
        Assert.True(installRequest.AcceptPackageAgreements);
        Assert.True(installRequest.IgnoreSecurityHash);
        Assert.Equal(InstallerMode.Interactive, installRequest.Mode);
        Assert.Equal(@"C:\temp\repair.log", installRequest.LogPath);
    }
}

file static class TestPaths
{
    public static string CreateTempAppRoot() =>
        Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", Guid.NewGuid().ToString("N"));

    public static string WriteManifest(string appRoot, string yaml)
    {
        Directory.CreateDirectory(appRoot);
        var manifestPath = Path.Combine(appRoot, "manifest.yaml");
        File.WriteAllText(manifestPath, yaml);
        return manifestPath;
    }

    public static void DeleteAppRoot(string appRoot)
    {
        if (Directory.Exists(appRoot))
            Directory.Delete(appRoot, recursive: true);
    }
}

file sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<HttpListenerContext>? _onRequest;

    public TestHttpServer(byte[] payload, Action<HttpListenerContext>? onRequest = null)
    {
        _onRequest = onRequest;
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        Url = $"{prefix}installer.bin";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _loopTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                    _onRequest?.Invoke(context);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload, _cts.Token);
                }
                catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpListenerException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                finally
                {
                    context?.Response.OutputStream.Dispose();
                    context?.Response.Close();
                }
            }
        }, _cts.Token);
    }

    public string Url { get; }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            _loopTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
