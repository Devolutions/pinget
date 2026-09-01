using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class RepositoryStateCollection
{
    public const string Name = "RepositoryState";
}

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
    [InlineData("25.4.1", "< 25.4.1", 1)]
    [InlineData("< 25.4.1", "25.4.1", -1)]
    public void CompareVersionStrings_ReturnsCorrectOrdering(string a, string b, int expected)
    {
        var result = RestSource.CompareVersionStrings(a, b);
        Assert.Equal(expected, Math.Sign(result));
    }

    public class VersionSelectionTests
    {
        [Fact]
        public void SelectV2Version_WithoutRequestedVersion_ReturnsLatest()
        {
            var selected = Repository.SelectV2VersionForTesting(
            [
                new PreIndexedSource.V2VersionDataEntry { Version = "1.0.0" },
                new PreIndexedSource.V2VersionDataEntry { Version = "1.2.0" },
                new PreIndexedSource.V2VersionDataEntry { Version = "1.1.0" },
            ], requestedVersion: null);

            Assert.Equal("1.2.0", selected.Version);
        }

        [Fact]
        public void SelectV2Version_WithExistingRequestedVersion_ReturnsRequestedVersion()
        {
            var selected = Repository.SelectV2VersionForTesting(
            [
                new PreIndexedSource.V2VersionDataEntry { Version = "1.2.0" },
                new PreIndexedSource.V2VersionDataEntry { Version = "1.1.0" },
            ], "1.1.0");

            Assert.Equal("1.1.0", selected.Version);
        }

        [Fact]
        public void SelectV2Version_WithMissingRequestedVersion_Throws()
        {
            var ex = Assert.Throws<MissingPackageVersionException>(() => Repository.SelectV2VersionForTesting(
            [
                new PreIndexedSource.V2VersionDataEntry { Version = "1.2.0" },
                new PreIndexedSource.V2VersionDataEntry { Version = "1.1.0" },
            ], "9.9.9"));

            Assert.Contains("9.9.9", ex.Message);
        }

        [Fact]
        public void SelectV2Version_WithRequestedChannel_ThrowsInsteadOfIgnoringChannel()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Repository.SelectV2VersionForTesting(
            [
                new PreIndexedSource.V2VersionDataEntry { Version = "1.2.0" },
                new PreIndexedSource.V2VersionDataEntry { Version = "1.1.0" },
            ], "1.2.0", "beta"));

            Assert.Contains("beta", ex.Message);
            Assert.Contains("channel metadata", ex.Message);
        }

        [Fact]
        public void SelectV1Version_WithRequestedChannelMismatch_Throws()
        {
            var ex = Assert.Throws<MissingPackageVersionException>(() => Repository.SelectV1VersionForTesting(
            [
                new PreIndexedSource.V1VersionRow { Version = "1.2.0", Channel = "stable" },
                new PreIndexedSource.V1VersionRow { Version = "1.1.0", Channel = "beta" },
            ], "1.2.0", "beta"));

            Assert.Equal("1.2.0", ex.RequestedVersion);
            Assert.Equal("beta", ex.RequestedChannel);
        }

        [Fact]
        public void SelectRestVersion_WithRequestedChannelMismatch_Throws()
        {
            var ex = Assert.Throws<MissingPackageVersionException>(() => Repository.SelectRestVersionForTesting(
            [
                new VersionKey { Version = "1.2.0", Channel = "stable" },
                new VersionKey { Version = "1.1.0", Channel = "beta" },
            ], "1.2.0", "beta"));

            Assert.Equal("1.2.0", ex.RequestedVersion);
            Assert.Equal("beta", ex.RequestedChannel);
        }
    }
}

[Collection(RepositoryStateCollection.Name)]
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
        public void PackagedLayout_PathsResolveFromPackagedAppRoot()
        {
                if (!OperatingSystem.IsWindows())
                        return;

                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var appRoot = Path.Combine(localAppData, "Packages", SourceStoreManager.PackagedFamilyName, "LocalState");
                var source = SourceStore.Default().Sources.First(s => s.Name == "winget");
                Assert.EndsWith(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName, "LocalState"), appRoot, StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName, "LocalState", "settings.json"), SettingsStoreManager.UserSettingsPath(appRoot), StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName, "LocalState", "Microsoft", "Windows Package Manager"), SourceStoreManager.GetPackagedFileCacheRoot(appRoot), StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName, "LocalState", "Microsoft.PreIndexed.Package", "Microsoft.Winget.Source_8wekyb3d8bbwe"), SourceStoreManager.SourceStateDir(source, appRoot), StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName, "LocalState", "Microsoft.Winget.Source_8wekyb3d8bbwe", "installed.db"), SourceStoreManager.SourceInstalledDbPath(source, appRoot), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DefaultAppRootOutsidePackage_AvoidsPackagedLayout()
        {
                // Tests run without AppX package identity, so the default app root must
                // not resolve to the WinGet packaged LocalState — that location requires
                // a brokered/elevated writer for its secure-settings stream.
                if (!OperatingSystem.IsWindows())
                        return;

                var appRoot = SourceStoreManager.NormalizeAppRoot(null);
                Assert.EndsWith(Path.Combine("Devolutions", "Pinget"), appRoot, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Path.Combine("Packages", SourceStoreManager.PackagedFamilyName), appRoot, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShowMetadata_RetriesWingetManagerAliasOnlyForCleanMisses()
        {
                Assert.True(Repository.ShouldRetryShowAcrossSystemWingetSources(
                        usesSystemWingetSources: true,
                        source: "winget",
                        matchCount: 0,
                        failureCount: 0));
                Assert.False(Repository.ShouldRetryShowAcrossSystemWingetSources(
                        usesSystemWingetSources: false,
                        source: "winget",
                        matchCount: 0,
                        failureCount: 0));
                Assert.False(Repository.ShouldRetryShowAcrossSystemWingetSources(
                        usesSystemWingetSources: true,
                        source: "winget.pro",
                        matchCount: 0,
                        failureCount: 0));
                Assert.False(Repository.ShouldRetryShowAcrossSystemWingetSources(
                        usesSystemWingetSources: true,
                        source: "winget",
                        matchCount: 0,
                        failureCount: 1));
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
        public void SystemWingetExport_ParsesJsonLines()
        {
                const string output = """
{"Arg":"https://api.winget.pro/feed","Data":"","Explicit":false,"Identifier":"api.winget.pro","Name":"winget.pro","TrustLevel":["Trusted"],"Type":"Microsoft.Rest"}
{"Arg":"https://storeedgefd.dsx.mp.microsoft.com/v9.0","Data":"","Explicit":false,"Identifier":"StoreEdgeFD","Name":"msstore","TrustLevel":["Trusted"],"Type":"Microsoft.Rest"}
{"Arg":"https://cdn.winget.microsoft.com/cache","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","Explicit":false,"Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Name":"winget","TrustLevel":["Trusted","StoreOrigin"],"Type":"Microsoft.PreIndexed.Package"}
{"Arg":"https://cdn.winget.microsoft.com/fonts","Data":"Microsoft.Winget.Fonts.Source_8wekyb3d8bbwe","Explicit":true,"Identifier":"Microsoft.Winget.Fonts.Source_8wekyb3d8bbwe","Name":"winget-font","TrustLevel":["Trusted","StoreOrigin"],"Type":"Microsoft.PreIndexed.Package"}
""";

                var sources = SystemWingetSourceStore.ParseExport(output);

                Assert.Equal(["winget.pro", "msstore", "winget", "winget-font"], sources.Select(source => source.Name).ToArray());
                var rest = sources[0];
                Assert.Equal(SourceKind.Rest, rest.Kind);
                Assert.Equal("https://api.winget.pro/feed", rest.Arg);
                Assert.Equal("api.winget.pro", rest.Identifier);
                Assert.Equal("Trusted", rest.TrustLevel);
                Assert.False(rest.Explicit);

                var preIndexed = sources[2];
                Assert.Equal(SourceKind.PreIndexed, preIndexed.Kind);
                Assert.Equal("Microsoft.Winget.Source_8wekyb3d8bbwe", preIndexed.Identifier);
                Assert.False(preIndexed.Explicit);
        }

        [Fact]
        public void SystemWingetExport_ParsesWrappedSources()
        {
                const string output = """
{
  "Sources": [
    {
      "Name": "corp",
      "Type": "Microsoft.Rest",
      "Arg": "https://packages.contoso.test/api",
      "Identifier": "Contoso.Rest",
      "TrustLevel": "Trusted",
      "Explicit": true,
      "Priority": 9
    }
  ]
}
""";

                var source = Assert.Single(SystemWingetSourceStore.ParseExport(output));
                Assert.Equal("corp", source.Name);
                Assert.Equal(SourceKind.Rest, source.Kind);
                Assert.Equal("Contoso.Rest", source.Identifier);
                Assert.Equal("Trusted", source.TrustLevel);
                Assert.True(source.Explicit);
                Assert.Equal(9, source.Priority);
        }

        [Fact]
        public void SystemWingetSourceStore_DetectsSecureSettingsStub()
        {
                Assert.True(SystemWingetSourceStore.IsSecureSettingsStub("SHA256: d52f7aa273206e81585b714fc627ecb6f6e17b6aeba7b28025124da0d25db334"));
                Assert.False(SystemWingetSourceStore.IsSecureSettingsStub("Sources:\n  - Name: winget\n"));
        }

        [Fact]
        public void SystemWingetSourceStore_BuildsAddArguments()
        {
                var args = SystemWingetSourceStore.BuildAddArguments(
                        "corp",
                        "https://packages.contoso.test/api",
                        SourceKind.Rest,
                        "Trusted",
                        explicitSource: true);

                Assert.Equal(
                [
                        "source",
                        "add",
                        "--name",
                        "corp",
                        "--arg",
                        "https://packages.contoso.test/api",
                        "--type",
                        "Microsoft.Rest",
                        "--disable-interactivity",
                        "--trust-level",
                        "trusted",
                        "--explicit"
                ], args);
        }

        [Fact]
        public void SystemWingetSourceStore_LoadUsesExportCommand()
        {
                var originalRunner = SystemWingetSourceStore.CommandRunner;
                try
                {
                        IReadOnlyList<string>? capturedArgs = null;
                        SystemWingetSourceStore.CommandRunner = args =>
                        {
                                capturedArgs = args.ToArray();
                                return new WingetCommandResult(
                                        0,
                                        """
{"Arg":"https://api.contoso.test/feed","Data":"","Explicit":false,"Identifier":"api.contoso.test","Name":"contoso","TrustLevel":["Trusted"],"Type":"Microsoft.Rest"}
""",
                                        "");
                        };

                        var store = SystemWingetSourceStore.Load();

                        Assert.Equal(SystemWingetSourceStore.BuildExportArguments(), capturedArgs);
                        Assert.Equal("contoso", Assert.Single(store.Sources).Name);
                }
                finally
                {
                        SystemWingetSourceStore.CommandRunner = originalRunner;
                }
        }

        [Fact]
        public void PackagedSecureSettingsStub_DelegatesToSystemWingetExport()
        {
                var originalRunner = SystemWingetSourceStore.CommandRunner;
                try
                {
                        IReadOnlyList<string>? capturedArgs = null;
                        SystemWingetSourceStore.CommandRunner = args =>
                        {
                                capturedArgs = args.ToArray();
                                return new WingetCommandResult(
                                        0,
                                        """
{"Arg":"https://api.contoso.test/feed","Data":"","Explicit":false,"Identifier":"api.contoso.test","Name":"contoso","TrustLevel":["Trusted"],"Type":"Microsoft.Rest"}
""",
                                        "");
                        };

                        var store = SourceStoreManager.LoadPackagedStoreFromStreams(
                                "SHA256: d52f7aa273206e81585b714fc627ecb6f6e17b6aeba7b28025124da0d25db334",
                                null);

                        Assert.NotNull(store);
                        Assert.Equal(SystemWingetSourceStore.BuildExportArguments(), capturedArgs);
                        Assert.Equal("contoso", Assert.Single(store!.Sources).Name);
                }
                finally
                {
                        SystemWingetSourceStore.CommandRunner = originalRunner;
                }
        }

    [Theory]
    [InlineData("auto", SourceMode.Auto)]
    [InlineData("  Auto  ", SourceMode.Auto)]
    [InlineData("PRIVATE", SourceMode.Private)]
    [InlineData("system-winget-mirror", SourceMode.SystemWingetMirror)]
    [InlineData("SystemWingetMirror", SourceMode.SystemWingetMirror)]
    public void TryParseSourceMode_AcceptsKnownSpellings(string value, SourceMode expected)
    {
        Assert.True(Repository.TryParseSourceMode(value, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mirror")]
    [InlineData("system_winget_mirror")]
    public void TryParseSourceMode_RejectsUnknownSpellings(string? value)
    {
        Assert.False(Repository.TryParseSourceMode(value, out _));
    }

    [Fact]
    public void ResolveRequestedSourceMode_KeepsPrivateDefaultForACustomAppRoot()
    {
        Assert.Equal(
            SourceMode.Private,
            Repository.ResolveRequestedSourceMode(null, @"C:\custom", null));
        Assert.Equal(
            SourceMode.Auto,
            Repository.ResolveRequestedSourceMode(null, null, null));
    }

    [Fact]
    public void ResolveRequestedSourceMode_LetsTheEnvironmentOverrideACustomAppRoot()
    {
        Assert.Equal(
            SourceMode.Auto,
            Repository.ResolveRequestedSourceMode(null, @"C:\custom", "auto"));
        Assert.Equal(
            SourceMode.SystemWingetMirror,
            Repository.ResolveRequestedSourceMode(null, @"C:\custom", "system-winget-mirror"));
        Assert.Equal(
            SourceMode.Private,
            Repository.ResolveRequestedSourceMode(null, null, "private"));
    }

    [Fact]
    public void ResolveRequestedSourceMode_IgnoresTheEnvironmentWhenTheCallerIsExplicit()
    {
        Assert.Equal(
            SourceMode.SystemWingetMirror,
            Repository.ResolveRequestedSourceMode(SourceMode.SystemWingetMirror, @"C:\custom", "private"));
        Assert.Equal(
            SourceMode.Private,
            Repository.ResolveRequestedSourceMode(SourceMode.Private, null, "auto"));
    }

    [Fact]
    public void ResolveRequestedSourceMode_TreatsAnExplicitAutoAsAChoiceRatherThanAnUnsetValue()
    {
        Assert.Equal(
            SourceMode.Auto,
            Repository.ResolveRequestedSourceMode(SourceMode.Auto, @"C:\custom", "private"));
        Assert.Equal(
            SourceMode.Auto,
            Repository.ResolveRequestedSourceMode(SourceMode.Auto, @"C:\custom", null));
    }

    [Fact]
    public void ResolveRequestedSourceMode_IgnoresAnUnparseableEnvironmentValue()
    {
        Assert.Equal(
            SourceMode.Private,
            Repository.ResolveRequestedSourceMode(null, @"C:\custom", "not-a-mode"));
        Assert.Equal(
            SourceMode.Auto,
            Repository.ResolveRequestedSourceMode(null, null, "not-a-mode"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("auto", true)]
    [InlineData("system-winget-mirror", true)]
    [InlineData("private", false)]
    [InlineData("not-a-mode", false)]
    public void RepositoryOpen_ReadsTheSourceModeEnvironmentVariable(string? sourceMode, bool expectsMirror)
    {
        var appRoot = TestPaths.CreateTempAppRoot();
        var originalRunner = SystemWingetSourceStore.CommandRunner;
        var originalSourceMode = Environment.GetEnvironmentVariable("PINGET_SOURCE_MODE");
        try
        {
            SystemWingetSourceStore.CommandRunner = _ => new WingetCommandResult(
                0,
                """
{"Arg":"https://api.contoso.test/feed","Data":"","Explicit":false,"Identifier":"api.contoso.test","Name":"contoso","TrustLevel":["Trusted"],"Type":"Microsoft.Rest"}
""",
                "");
            Environment.SetEnvironmentVariable("PINGET_SOURCE_MODE", sourceMode);

            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                UserAgent = "pinget-dotnet-tests/1.0",
            });

            var mirrorPath = Path.Combine(appRoot, "system-sources.json");
            Assert.Equal(expectsMirror, File.Exists(mirrorPath));

            if (expectsMirror)
                Assert.Contains("contoso", File.ReadAllText(mirrorPath));
        }
        finally
        {
            SystemWingetSourceStore.CommandRunner = originalRunner;
            Environment.SetEnvironmentVariable("PINGET_SOURCE_MODE", originalSourceMode);
            TestPaths.DeleteAppRoot(appRoot);
        }
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
    public void SystemWingetMirror_UsesPrivateCacheAndPreservesMetadata()
    {
        var appRoot = TestPaths.CreateTempAppRoot();
        var originalRunner = SystemWingetSourceStore.CommandRunner;
        try
        {
            SystemWingetSourceStore.CommandRunner = _ => new WingetCommandResult(
                0,
                """
{"Arg":"https://api.contoso.test/feed","Data":"","Explicit":false,"Identifier":"api.contoso.test","Name":"contoso","TrustLevel":["Trusted"],"Type":"Microsoft.Rest"}
""",
                "");

            var (mode, store) = SourceStoreManager.LoadEffective(appRoot, SourceMode.SystemWingetMirror);

            Assert.Equal(EffectiveSourceMode.SystemWingetMirror, mode);
            Assert.Single(store.Sources);
            Assert.True(File.Exists(Path.Combine(appRoot, "system-sources.json")));
            Assert.False(File.Exists(Path.Combine(appRoot, "sources.json")));

            var source = store.Sources[0];
            source.LastUpdate = DateTime.UtcNow;
            source.SourceVersion = "cached-contract";
            source.ETag = "\"etag\"";
            source.LastModified = "Wed, 03 Jun 2026 12:00:00 GMT";
            SourceStoreManager.SaveSystemWingetMirrorStore(appRoot, store);

            var refreshed = SourceStoreManager.RefreshSystemWingetMirrorStore(appRoot);
            var refreshedSource = Assert.Single(refreshed.Sources);

            Assert.Equal("cached-contract", refreshedSource.SourceVersion);
            Assert.Equal("\"etag\"", refreshedSource.ETag);
            Assert.Equal(Path.Combine(appRoot, "sources", "api.contoso.test"), SourceStoreManager.SourceStateDir(refreshedSource, appRoot, identifierKeyed: true));
            Assert.Equal(Path.Combine(appRoot, "sources", "contoso"), SourceStoreManager.SourceStateDir(refreshedSource, appRoot));
        }
        finally
        {
            SystemWingetSourceStore.CommandRunner = originalRunner;
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
    public void ParseYamlManifest_ReadsIconsAndSelectsBestIcon()
    {
        var yaml = """
            PackageIdentifier: Test.Package
            PackageVersion: 1.2.3
            PackageName: Test Package
            Icons:
              - IconUrl: https://example.test/dark-512.png
                IconFileType: png
                IconResolution: 512x512
                IconTheme: dark
                IconSha256: DARK
              - IconUrl: https://example.test/default-64.png
                IconFileType: png
                IconResolution: 64x64
                IconTheme: default
              - IconUrl: https://example.test/default-256.ico
                IconFileType: ico
                IconResolution: 256x256
                IconTheme: default
              - IconFileType: png
                IconResolution: 1024x1024
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/Test.Package.exe
                InstallerSha256: ABC123
            """;

        var manifest = Repository.ParseYamlManifest(System.Text.Encoding.UTF8.GetBytes(yaml));

        Assert.Equal(4, manifest.Icons.Count);
        Assert.Equal("https://example.test/dark-512.png", manifest.Icons[0].IconUrl);
        Assert.Equal("https://example.test/default-256.ico", manifest.IconUrl);
        Assert.NotNull(manifest.Icon);
        Assert.Equal("ico", manifest.Icon!.IconFileType);
        Assert.Equal("256x256", manifest.Icon.IconResolution);
    }

    [Fact]
    public void ParseYamlManifest_WithoutIconsReturnsEmptyIconListAndNoSelection()
    {
        var yaml = """
            PackageIdentifier: Test.Package
            PackageVersion: 1.2.3
            PackageName: Test Package
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/Test.Package.exe
                InstallerSha256: ABC123
            """;

        var manifest = Repository.ParseYamlManifest(System.Text.Encoding.UTF8.GetBytes(yaml));

        Assert.Empty(manifest.Icons);
        Assert.Null(manifest.Icon);
        Assert.Null(manifest.IconUrl);
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

    [Fact]
    public void GetSqliteNativeLibraryCandidates_ProbesRootAndRidFolder()
    {
        var candidates = Repository.GetSqliteNativeLibraryCandidates(@"C:\module").ToList();

        Assert.Equal(@"C:\module\e_sqlite3.dll", candidates[0]);
        Assert.Contains(Path.Combine(@"C:\module", "runtimes"), candidates[1]);
        Assert.EndsWith(Path.Combine("native", "e_sqlite3.dll"), candidates[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetWinGetPackageIdentityFromLocalId_UsesSourceIdentifierSuffix()
    {
        SourceRecord source = new()
        {
            Name = "winget",
            Kind = SourceKind.PreIndexed,
            Arg = "https://cdn.winget.microsoft.com/cache",
            Identifier = "Microsoft.Winget.Source_8wekyb3d8bbwe",
        };

        bool result = Repository.TryGetWinGetPackageIdentityFromLocalId(
            @"ARP\User\X64\Atlassian.AtlassianCLI_Microsoft.Winget.Source_8wekyb3d8bbwe",
            [source],
            out string? packageId,
            out string? sourceName
        );

        Assert.True(result);
        Assert.Equal("Atlassian.AtlassianCLI", packageId);
        Assert.Equal("winget", sourceName);
    }

    [Fact]
    public void TryGetWinGetPackageIdentityFromLocalId_IgnoresUnknownSourceIdentifier()
    {
        SourceRecord source = new()
        {
            Name = "winget",
            Kind = SourceKind.PreIndexed,
            Arg = "https://cdn.winget.microsoft.com/cache",
            Identifier = "Microsoft.Winget.Source_8wekyb3d8bbwe",
        };

        bool result = Repository.TryGetWinGetPackageIdentityFromLocalId(
            @"ARP\Machine\X64\Contoso.Tool_Unknown.Source_1234",
            [source],
            out string? packageId,
            out string? sourceName
        );

        Assert.False(result);
        Assert.Null(packageId);
        Assert.Null(sourceName);
    }

    [Fact]
    public void ListMatchFromInstalled_UsesWinGetArpIdentityMetadata()
    {
        var package = new InstalledPackage
        {
            Name = "tessl",
            LocalId = @"ARP\User\X64\tessl.tessl_api.winget.pro",
            InstalledVersion = "0.77.0",
            Publisher = "tessl.io",
            Scope = "User",
            InstallerCategory = "portable",
            WinGetPackageIdentifier = "tessl.tessl",
            WinGetSourceIdentifier = "api.winget.pro",
        };

        var item = Repository.ListMatchFromInstalledForTesting(package, []);

        Assert.Equal("tessl.tessl", item.Id);
        Assert.Equal("api.winget.pro", item.SourceName);
    }

    [Fact]
    public void ListMatchFromInstalled_MapsWinGetArpSourceIdentifierToConfiguredName()
    {
        var package = new InstalledPackage
        {
            Name = "tessl",
            LocalId = @"ARP\User\X64\tessl.tessl_api.winget.pro",
            InstalledVersion = "0.77.0",
            Publisher = "tessl.io",
            Scope = "User",
            InstallerCategory = "portable",
            WinGetPackageIdentifier = "tessl.tessl",
            WinGetSourceIdentifier = "api.winget.pro",
        };
        SourceRecord source = new()
        {
            Name = "winget.pro",
            Kind = SourceKind.Rest,
            Arg = "https://api.winget.pro/4259fd23-6fcd-46bf-9287-be8833cfbdd5",
            Identifier = "api.winget.pro",
        };

        var item = Repository.ListMatchFromInstalledForTesting(package, [source]);

        Assert.Equal("tessl.tessl", item.Id);
        Assert.Equal("winget.pro", item.SourceName);
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

    [Fact]
    public void AddAndList_WorkWithPackagedPinSchema()
    {
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            var dbPath = SourceStoreManager.PinsDbPath(appRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE pin (
                        package_id TEXT NOT NULL,
                        source_id TEXT NOT NULL,
                        type INTEGER NOT NULL,
                        version TEXT NOT NULL,
                        PRIMARY KEY (package_id, source_id)
                    )";
                cmd.ExecuteNonQuery();
            }

            PinStore.Add("Test.Package.Unit", "1.2.*", "winget", PinType.Gating, appRoot);

            var pin = Assert.Single(PinStore.List(appRoot));
            Assert.Equal("Test.Package.Unit", pin.PackageId);
            Assert.Equal("1.2.*", pin.Version);
            Assert.Equal("winget", pin.SourceId);
            Assert.Equal(PinType.Gating, pin.PinType);

            using var verifyConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            verifyConn.Open();
            using var verifyCmd = verifyConn.CreateCommand();
            verifyCmd.CommandText = "SELECT type, version FROM pin WHERE package_id = @id AND source_id = @src";
            verifyCmd.Parameters.AddWithValue("@id", "Test.Package.Unit");
            verifyCmd.Parameters.AddWithValue("@src", "winget");

            using var reader = verifyCmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal("1.2.*", reader.GetString(1));
        }
        finally
        {
            PinStore.Reset(appRoot);
            TestPaths.DeleteAppRoot(appRoot);
        }
    }
}

[Collection(RepositoryStateCollection.Name)]
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
    public void CreateDependencyInstallRequest_IsSilentAndNotForced()
    {
        var result = Repository.CreateDependencyInstallRequest(
            "Microsoft.VCRedist.2015+.x64",
            new InstallRequest
            {
                Query = new PackageQuery { Source = "winget" },
                Mode = InstallerMode.SilentWithProgress,
                Force = true,
                AcceptPackageAgreements = true,
                IgnoreSecurityHash = true,
                DependencySource = "dependencies",
            });

        Assert.Equal("Microsoft.VCRedist.2015+.x64", result.Query.Id);
        Assert.Equal("dependencies", result.Query.Source);
        Assert.True(result.Query.Exact);
        Assert.Equal(InstallerMode.Silent, result.Mode);
        Assert.False(result.Force);
        Assert.True(result.NoUpgrade);
        Assert.True(result.AcceptPackageAgreements);
        Assert.True(result.IgnoreSecurityHash);
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

            var downloadResult = repo.DownloadInstaller(request, Path.Combine(appRoot, "downloads"));
            Assert.True(File.Exists(downloadResult.InstallerPath));
            Assert.Equal(payload, File.ReadAllBytes(downloadResult.InstallerPath));
            Assert.True(File.Exists(downloadResult.ManifestPath));
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

            var downloadResult = repo.DownloadInstaller(request, Path.Combine(appRoot, "downloads"));
            Assert.True(File.Exists(downloadResult.InstallerPath));
            Assert.Equal("Bearer test-token", authorizationHeader);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void DownloadInstaller_WritesManifestYamlAlongsideInstaller()
    {
        var payload = "pinget-test-payload"u8.ToArray();
        using var server = new TestHttpServer(payload);
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            var manifestPath = TestPaths.WriteManifest(appRoot, $$"""
                PackageIdentifier: Vendor.App
                PackageVersion: 3.4.5
                PackageName: Vendor App
                Publisher: Vendor
                ManifestType: merged
                ManifestVersion: 1.10.0
                Installers:
                  - Architecture: x64
                    InstallerType: exe
                    Scope: user
                    InstallerLocale: en-US
                    InstallerUrl: {{server.Url}}
                """);

            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            var request = new InstallRequest
            {
                Query = new PackageQuery(),
                ManifestPath = manifestPath,
                IgnoreSecurityHash = true,
            };

            var result = repo.DownloadInstaller(request, Path.Combine(appRoot, "downloads"));
            Assert.True(File.Exists(result.ManifestPath));

            // Filename follows winget's `{Name}_{Version}_{Scope}_{Arch}_{Type}_{Locale}.yaml`.
            Assert.EndsWith("Vendor App_3.4.5_User_X64_exe_en-US.yaml", result.ManifestPath);

            var yaml = File.ReadAllText(result.ManifestPath);
            Assert.Contains("PackageIdentifier: Vendor.App", yaml);
            Assert.Contains("PackageVersion: 3.4.5", yaml);
            Assert.Contains("ManifestType: merged", yaml);
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
    public void BuildArguments_CanOmitMsiTargetForDirectMsiExecution()
    {
        var installer = new Installer
        {
            InstallerType = "wix",
            Switches = new InstallerSwitches
            {
                Log = "/log \"<LOGPATH>\"",
                InstallLocation = "INSTALLDIR=\"<INSTALLPATH>\"",
            }
        };

        var args = InstallerDispatch.BuildArguments(
            "wix",
            new InstallRequest
            {
                Query = new PackageQuery(),
                Mode = InstallerMode.Silent,
                LogPath = @"C:\temp\node.log",
                InstallLocation = @"C:\Program Files\nodejs",
            },
            new Manifest { Id = "OpenJS.NodeJS.22", Name = "Node.js", Version = "22.22.3" },
            installer,
            @"C:\temp\node.msi",
            includeMsiInstallTarget: false);

        Assert.Equal(["/quiet", "/norestart", "/log", @"C:\temp\node.log", @"INSTALLDIR=C:\Program Files\nodejs"], args);
    }

    [Fact]
    public void ParseMsiDirectArguments_EnablesUacOnlyForQuietUi()
    {
        var parsed = InstallerDispatch.ParseMsiDirectArguments([
            "/quiet",
            "/norestart",
            "/log",
            @"C:\temp\node.log",
            @"INSTALLDIR=C:\Program Files\nodejs",
        ]);

        Assert.Equal(0x102u, parsed.UiLevel);
        Assert.Equal(@"REBOOT=ReallySuppress INSTALLDIR=""C:\Program Files\nodejs""", parsed.Properties);
        Assert.Equal(@"C:\temp\node.log", parsed.LogPath);
    }

    [Fact]
    public void ShouldRunMsiDirect_DisablesDirectMsiForMachineScopeSilentInstallers()
    {
        var request = new InstallRequest { Query = new PackageQuery(), Mode = InstallerMode.Silent };
        var installer = new Installer { InstallerType = "wix", Scope = "machine" };

        Assert.False(InstallerDispatch.ShouldRunMsiDirect(request, installer));
        Assert.True(InstallerDispatch.ShouldElevateMsiShellExecute(installer));
    }

    [Fact]
    public void InstallerDownloadIfRange_PrefersStrongEtagAndRejectsWeakEtag()
    {
        var strong = new Repository.InstallerDownloadMetadata(
            "https://example.test/app.exe",
            @"""abc""",
            "Wed, 03 Jun 2026 21:00:00 GMT",
            100,
            50);
        var weak = strong with { ETag = @"W/""abc""" };

        Assert.Equal(@"""abc""", Repository.InstallerDownloadIfRange(strong));
        Assert.Equal("Wed, 03 Jun 2026 21:00:00 GMT", Repository.InstallerDownloadIfRange(weak));
    }

    [Fact]
    public void ParseContentRange_ValidatesResumeStartAndTotal()
    {
        var range = new System.Net.Http.Headers.ContentRangeHeaderValue(50, 99, 100);

        Assert.Equal(100, Repository.ParseContentRange(range, 50));
        Assert.Throws<InvalidOperationException>(() => Repository.ParseContentRange(range, 49));
    }

    [Fact]
    public void InstallerDownloadSidecarPaths_AppendSuffixes()
    {
        Assert.Equal(@"C:\temp\installer.msi.part", Repository.SidecarPath(@"C:\temp\installer.msi", "part"));
        Assert.Equal(@"C:\temp\installer.msi.part.json", Repository.SidecarPath(@"C:\temp\installer.msi", "part.json"));
    }

    [Fact]
    public void JoinArguments_QuotesArgumentsWithSpaces()
    {
        Assert.Equal(
            @"/quiet ""INSTALLDIR=C:\Program Files\nodejs"" ""VALUE=has \""quotes\""""",
            InstallerDispatch.JoinArguments(["/quiet", @"INSTALLDIR=C:\Program Files\nodejs", "VALUE=has \"quotes\""]));
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
    public void CorrelateInstalledPackage_TiebreaksOnUnnormalizedNamePrefix()
    {
        // `Notepad++` and `Notepad--` both normalize to `notepad` (alphanumeric filter
        // strips the trailing punctuation). Without a tiebreaker, iteration order
        // decided the winner. The installed display name's prefix picks the correct one.
        var installed = new InstalledPackage
        {
            Name = "Notepad++ (ARM 64-bit)",
            LocalId = @"ARP\Machine\X64\Notepad++",
            InstalledVersion = "8.8.9",
            Scope = "Machine",
            InstallerCategory = "exe",
            PackageFamilyNames = [],
            ProductCodes = [],
            UpgradeCodes = [],
        };
        var candidates = new List<SearchMatch>
        {
            new()
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "ndd.Notepad--",
                Name = "Notepad--",
                Version = "1.0.0",
            },
            new()
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Notepad++.Notepad++",
                Name = "Notepad++",
                Version = "8.9.5",
            },
        };

        var correlated = Repository.CorrelateInstalledPackage(installed, candidates, loose: true);
        Assert.NotNull(correlated);
        Assert.Equal("Notepad++.Notepad++", correlated!.Id);
    }

    [Fact]
    public void CorrelateInstalledPackage_PrefersAnchoredCandidateOverWordFragment()
    {
        // Loose substring match used to pick up `Studio` anywhere in the installed name
        // — so `ZeroBrane.Studio` could outrank `Microsoft.DotNet.SDK.10` because both
        // scored 700 and iteration order favored the alphabetically-later candidate.
        var installed = new InstalledPackage
        {
            Name = "Microsoft .NET SDK 10.0.101 (arm64) from Visual Studio",
            LocalId = @"ARP\Machine\X64\{7E9F8584-06E7-445E-9165-7486CC1B56C3}",
            InstalledVersion = "40.10.18029",
            Scope = "Machine",
            InstallerCategory = "msi",
            PackageFamilyNames = [],
            ProductCodes = [],
            UpgradeCodes = [],
        };
        var candidates = new List<SearchMatch>
        {
            new() { SourceName = "winget", SourceKind = SourceKind.PreIndexed, Id = "ZeroBrane.Studio", Name = "Studio", Version = "1.0" },
            new() { SourceName = "winget", SourceKind = SourceKind.PreIndexed, Id = "BrickLink.Studio", Name = "Studio", Version = "1.0" },
            new() { SourceName = "winget", SourceKind = SourceKind.PreIndexed, Id = "Microsoft.DotNet.SDK.10", Name = "Microsoft .NET SDK 10.0", Version = "10.0.204" },
        };

        var correlated = Repository.CorrelateInstalledPackage(installed, candidates, loose: true);
        Assert.NotNull(correlated);
        Assert.Equal("Microsoft.DotNet.SDK.10", correlated!.Id);
    }

    [Fact]
    public void CorrelateInstalledPackage_MsixPackagesDoNotCorrelateViaName()
    {
        // MSIX correlation must go through the v2 index's `pfns2` table —
        // name fallback is wrong because two MSIX packages can legitimately
        // share a display name without sharing identity (Microsoft Edge
        // Stable MSIX vs the catalog Microsoft.Edge MSI; Notepad++ Store
        // stub MSIX vs the catalog Inno installer). The PFN lookup happens
        // earlier in CorrelateInstalledViaIndex.
        var installed = new InstalledPackage
        {
            Name = "Microsoft Teams",
            LocalId = @"MSIX\MSTeams_25290.205.4069.4894_arm64__8wekyb3d8bbwe",
            InstalledVersion = "25290.205.4069.4894",
            Scope = "User",
            InstallerCategory = "msix",
            PackageFamilyNames = ["MSTeams_8wekyb3d8bbwe"],
            ProductCodes = [],
            UpgradeCodes = [],
        };
        var candidates = new List<SearchMatch>
        {
            new()
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Microsoft.Teams",
                Name = "Microsoft Teams",
                Version = "26106.1906.4665.7308",
            },
        };

        Assert.Null(Repository.CorrelateInstalledPackage(installed, candidates, loose: true));
    }

    [Fact]
    public void CorrelateInstalledPackage_RefusesAmbiguousWinners()
    {
        // Two catalog packages both expose name "Git" (Git.Git and
        // Microsoft.Git). Without publisher disambiguation they score
        // identically; winget refuses to correlate (the install lists with
        // empty Source). pinget must do the same to avoid manufacturing an
        // upgrade against the wrong catalog package.
        var installed = new InstalledPackage
        {
            Name = "Git",
            LocalId = @"ARP\Machine\X64\Git_is1",
            InstalledVersion = "2.53.0",
            Publisher = "The Git Development Community",
            Scope = "Machine",
            InstallerCategory = "exe",
            PackageFamilyNames = [],
            ProductCodes = [],
            UpgradeCodes = [],
        };
        var candidates = new List<SearchMatch>
        {
            new() { SourceName = "winget", SourceKind = SourceKind.PreIndexed, Id = "Git.Git", Name = "Git", Version = "2.54.0" },
            new() { SourceName = "winget", SourceKind = SourceKind.PreIndexed, Id = "Microsoft.Git", Name = "Git", Version = "2.53.0.0.7" },
        };

        Assert.Null(Repository.CorrelateInstalledPackage(installed, candidates, loose: true));
    }

    [Fact]
    public void MapArpVersionToCatalog_ReturnsCatalogVersionInsideRange()
    {
        // .NET SDK 10.0.108 declares its ARP DisplayVersion is
        // `10.1.826.23019`. Without this mapping the upgrade was silently
        // dropped because compare_version says `10.1.x > 10.0.108`.
        var entries = new List<PreIndexedSource.V2VersionDataEntry>
        {
            new() { Version = "10.0.300", ArpMinVersion = "10.3.26.23102", ArpMaxVersion = "10.3.26.23102" },
            new() { Version = "10.0.108", ArpMinVersion = "10.1.826.23019", ArpMaxVersion = "10.1.826.23019" },
            new() { Version = "10.0.107", ArpMinVersion = "10.1.726.21808", ArpMaxVersion = "10.1.726.21808" },
        };

        Assert.Equal("10.0.108", Repository.MapArpVersionToCatalog(entries, "10.1.826.23019"));
    }

    [Fact]
    public void MapArpVersionToCatalog_ReturnsNullWhenNoRangeMatches()
    {
        var entries = new List<PreIndexedSource.V2VersionDataEntry>
        {
            new() { Version = "10.0.300", ArpMinVersion = "10.3.26.23102", ArpMaxVersion = "10.3.26.23102" },
        };
        Assert.Null(Repository.MapArpVersionToCatalog(entries, "40.10.18029"));
        Assert.Null(Repository.MapArpVersionToCatalog(entries, "Unknown"));
        Assert.Null(Repository.MapArpVersionToCatalog(entries, ""));
    }

    [Fact]
    public void LessThanLatestArpAnchoredVersion_HandlesCoarseDisplayVersion()
    {
        // Snagit 2025 reports ARP DisplayVersion `25.4.1`, but the catalog's
        // latest ARP build is `25.4.1.10325`. winget renders this as
        // installed `< 25.4.1` so the latest installer remains applicable.
        var entries = new List<PreIndexedSource.V2VersionDataEntry>
        {
            new() { Version = "25.4.1", ArpMinVersion = "25.4.1.10325", ArpMaxVersion = "25.4.1.10325" },
            new() { Version = "25.4.0", ArpMinVersion = "25.4.0", ArpMaxVersion = "25.4.0.8498" },
        };

        Assert.Null(Repository.MapArpVersionToCatalog(entries, "25.4.1"));
        Assert.Equal("< 25.4.1", Repository.LessThanLatestArpAnchoredVersion(entries, "25.4.1"));
        Assert.Null(Repository.LessThanLatestArpAnchoredVersion(entries, "25.4.0"));
    }

    [Fact]
    public void GreaterThanLatestArpAnchoredVersion_HandlesServicedMsixFrameworkVersion()
    {
        // WindowsAppRuntime packages can be serviced beyond the latest winget
        // catalog versionData range. winget renders those as `> latest` and
        // does not report an available downgrade.
        var entries = new List<PreIndexedSource.V2VersionDataEntry>
        {
            new() { Version = "1.5.8", ArpMinVersion = "5001.373.1736.0", ArpMaxVersion = "5001.373.1736.0" },
            new() { Version = "1.5.7", ArpMinVersion = "5001.337.1906.0", ArpMaxVersion = "5001.337.1906.0" },
        };

        Assert.Null(Repository.MapArpVersionToCatalog(entries, "5001.400.42.0"));
        Assert.Null(Repository.LessThanLatestArpAnchoredVersion(entries, "5001.400.42.0"));
        Assert.Equal("> 1.5.8", Repository.GreaterThanLatestArpAnchoredVersion(entries, "5001.400.42.0", "1.5.8"));
        Assert.Null(Repository.GreaterThanLatestArpAnchoredVersion(entries, "5001.337.1906.0", "1.5.8"));

        var staleNormalAppEntries = new List<PreIndexedSource.V2VersionDataEntry>
        {
            new() { Version = "2024.1.1", ArpMinVersion = "2024.1.1.0", ArpMaxVersion = "2024.1.1.0" },
        };
        Assert.Null(Repository.GreaterThanLatestArpAnchoredVersion(staleNormalAppEntries, "2026.1.3.0", "2026.2.0.0"));
    }

    [Fact]
    public void LatestArpAnchoredVersion_SkipsInternalRows()
    {
        // Microsoft.WindowsAppRuntime.1.8 publishes both an internal build
        // version (`8000.836.2153.0`, no ARP bounds) and user-facing
        // versions (`1.8.6`, `1.8.5`, …). The internal row shouldn't win.
        var entries = new List<PreIndexedSource.V2VersionDataEntry>
        {
            new() { Version = "8000.836.2153.0" },
            new() { Version = "1.8.6", ArpMinVersion = "8000.806.2252.0", ArpMaxVersion = "8000.806.2252.0" },
            new() { Version = "1.8.5", ArpMinVersion = "8000.770.947.0",  ArpMaxVersion = "8000.770.947.0" },
        };
        Assert.Equal("1.8.6", Repository.LatestArpAnchoredVersion(entries));
    }

    [Fact]
    public void LatestArpAnchoredVersion_ReturnsNullWhenNoBounds()
    {
        var entries = new List<PreIndexedSource.V2VersionDataEntry>
        {
            new() { Version = "1.28.240.0" },
            new() { Version = "1.27.470.0" },
        };
        Assert.Null(Repository.LatestArpAnchoredVersion(entries));
    }

    [Fact]
    public void SuppressDuplicateAvailableVersions_KeepsAvailableOnlyOnNewestInstalledRow()
    {
        var rows = new List<ListMatch>
        {
            new()
            {
                Name = "Example 1.0",
                Id = "Example.Tool",
                LocalId = @"ARP\Machine\X64\old",
                InstalledVersion = "1.0.0",
                AvailableVersion = "3.0.0",
                SourceName = "winget",
            },
            new()
            {
                Name = "Example 2.0",
                Id = "Example.Tool",
                LocalId = @"ARP\Machine\X64\new",
                InstalledVersion = "2.0.0",
                AvailableVersion = "3.0.0",
                SourceName = "winget",
            },
            new()
            {
                Name = "Other",
                Id = "Other.Tool",
                LocalId = @"ARP\Machine\X64\other",
                InstalledVersion = "1.0.0",
                AvailableVersion = "2.0.0",
                SourceName = "winget",
            },
        };

        var result = Repository.SuppressDuplicateAvailableVersions(rows);

        Assert.Null(result[0].AvailableVersion);
        Assert.Equal("3.0.0", result[1].AvailableVersion);
        Assert.Equal("2.0.0", result[2].AvailableVersion);
    }

    [Fact]
    public void NormalizePublisher_Microsoft_Corporation_Strips_To_Microsoft()
    {
        // Fixture observed in the live catalog's norm_publishers2.
        Assert.Equal("microsoft", NameNormalization.NormalizePublisher("Microsoft Corporation"));
    }

    [Fact]
    public void NormalizePublisher_JetBrains_Sro_Strips_To_JetBrains()
    {
        Assert.Equal("jetbrains", NameNormalization.NormalizePublisher("JetBrains s.r.o."));
    }

    [Fact]
    public void NormalizePublisher_Without_LegalSuffix_Keeps_All_Tokens()
    {
        // "The Git Development Community" has no recognized legal-entity
        // suffix, so all tokens stay — matches the live catalog row.
        Assert.Equal(
            "thegitdevelopmentcommunity",
            NameNormalization.NormalizePublisher("The Git Development Community"));
    }

    [Fact]
    public void NormalizePublisher_Strips_Common_Suffixes()
    {
        Assert.Equal("foo", NameNormalization.NormalizePublisher("Foo Inc"));
        Assert.Equal("foobar", NameNormalization.NormalizePublisher("Foo Bar LLC"));
        Assert.Equal("foo", NameNormalization.NormalizePublisher("Foo GmbH"));
    }

    [Fact]
    public void NormalizePublisher_Software_Strips_To_BaseName()
    {
        Assert.Equal("sweetscape", NameNormalization.NormalizePublisher("SweetScape Software"));
    }

    [Fact]
    public void NormalizeName_Strips_VersionDelimited_Token()
    {
        // `2025.3.0.1` matches VersionDelimited. Bare `2026` doesn't.
        Assert.Equal("jetbrainsrider", NameNormalization.NormalizeName("JetBrains Rider 2025.3.0.1").Name);
        Assert.Equal(
            "visualstudioprofessional2026",
            NameNormalization.NormalizeName("Visual Studio Professional 2026").Name);
    }

    [Fact]
    public void NormalizeName_Strips_Architecture_Suffix()
    {
        var r = NameNormalization.NormalizeName("PowerToys (Preview) x64");
        Assert.Equal("powertoys", r.Name);
        Assert.Equal(NameNormalization.Architecture.X64, r.Architecture);
    }

    [Fact]
    public void NormalizeName_Strips_Known_Locale()
    {
        var r = NameNormalization.NormalizeName("Foo en-US Edition");
        Assert.Equal("en-us", r.Locale);
    }

    [Fact]
    public void NormalizeName_Keeps_Unknown_Locale_Shaped_Tokens()
    {
        var r = NameNormalization.NormalizeName("Foo XY-AB");
        Assert.Equal(string.Empty, r.Locale);
    }

    [Fact]
    public void NormalizeName_Strips_Parens_Content()
    {
        Assert.Equal("foo", NameNormalization.NormalizeName("Foo (beta)").Name);
    }

    [Fact]
    public void NormalizeName_Microsoft_Edge_Matches_Catalog()
    {
        Assert.Equal("microsoftedge", NameNormalization.NormalizeName("Microsoft Edge").Name);
    }

    [Fact]
    public void NormalizeName_Keeps_Year_Only_Suffix()
    {
        Assert.Equal("foo2026", NameNormalization.NormalizeName("Foo 2026").Name);
    }

    [Fact]
    public void Manifest_Parses_RequireExplicitUpgrade_AtTopLevel()
    {
        // Top-level RequireExplicitUpgrade flag propagates to
        // Manifest.RequireExplicitUpgrade. winget catalogs put the flag
        // here for browser packages and self-updating apps that opt out
        // of bulk `upgrade`.
        var yaml = @"
PackageIdentifier: Test.Package
PackageVersion: 1.2.3
DefaultLocale: en-US
ManifestType: singleton
ManifestVersion: 1.10.0
PackageLocale: en-US
PackageName: Test Package
Publisher: Example
License: MIT
ShortDescription: explicit-upgrade fixture
RequireExplicitUpgrade: true
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/Test.Package.exe
    InstallerSha256: ABC123
";
        var manifest = Repository.ParseYamlManifest(System.Text.Encoding.UTF8.GetBytes(yaml));
        Assert.True(manifest.RequireExplicitUpgrade);
    }

    [Fact]
    public void Manifest_Parses_RequireExplicitUpgrade_OnInstaller()
    {
        // Per-installer flag — only one of several installers declares
        // it, but the Manifest aggregate is still true because the user
        // could pick that installer when upgrading.
        var yaml = @"
PackageIdentifier: Test.Package
PackageVersion: 1.2.3
DefaultLocale: en-US
ManifestType: singleton
ManifestVersion: 1.10.0
PackageLocale: en-US
PackageName: Test Package
Publisher: Example
License: MIT
ShortDescription: explicit-upgrade fixture
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/Test.Package.x64.exe
    InstallerSha256: ABC123
  - Architecture: arm64
    InstallerType: exe
    InstallerUrl: https://example.test/Test.Package.arm64.exe
    InstallerSha256: DEF456
    RequireExplicitUpgrade: true
";
        var manifest = Repository.ParseYamlManifest(System.Text.Encoding.UTF8.GetBytes(yaml));
        Assert.True(manifest.RequireExplicitUpgrade);
        Assert.False(manifest.Installers[0].RequireExplicitUpgrade);
        Assert.True(manifest.Installers[1].RequireExplicitUpgrade);
    }

    [Fact]
    public void Manifest_WithoutRequireExplicitUpgrade_DefaultsToFalse()
    {
        var yaml = @"
PackageIdentifier: Test.Package
PackageVersion: 1.2.3
DefaultLocale: en-US
ManifestType: singleton
ManifestVersion: 1.10.0
PackageLocale: en-US
PackageName: Test Package
Publisher: Example
License: MIT
ShortDescription: baseline fixture
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/Test.Package.exe
    InstallerSha256: ABC123
";
        var manifest = Repository.ParseYamlManifest(System.Text.Encoding.UTF8.GetBytes(yaml));
        Assert.False(manifest.RequireExplicitUpgrade);
    }

    [Fact]
    public void LookupUniqueNormalizedIdentity_ReturnsUniqueMatch()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE norm_names2 (norm_name TEXT, package INT64);
                CREATE TABLE norm_publishers2 (norm_publisher TEXT, package INT64);
                INSERT INTO norm_names2 VALUES ('microsoftedge', 100);
                INSERT INTO norm_publishers2 VALUES ('microsoft', 100);";
            cmd.ExecuteNonQuery();
        }

        var rowid = Repository.LookupUniqueNormalizedIdentityForTesting(connection, "microsoftedge", "microsoft");
        Assert.Equal(100L, rowid);
    }

    [Fact]
    public void LookupUniqueNormalizedIdentity_RejectsAmbiguousMatch()
    {
        // Two distinct packages share the same (norm_name, norm_publisher)
        // — winget refuses to correlate when it can't disambiguate.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE norm_names2 (norm_name TEXT, package INT64);
                CREATE TABLE norm_publishers2 (norm_publisher TEXT, package INT64);
                INSERT INTO norm_names2 VALUES ('git', 100), ('git', 200);
                INSERT INTO norm_publishers2 VALUES ('thegitdevelopmentcommunity', 100), ('thegitdevelopmentcommunity', 200);";
            cmd.ExecuteNonQuery();
        }

        var rowid = Repository.LookupUniqueNormalizedIdentityForTesting(connection, "git", "thegitdevelopmentcommunity");
        Assert.Null(rowid);
    }

    [Fact]
    public void LookupUniqueNormalizedIdentity_RequiresPublisherIntersect()
    {
        // norm_name has multiple matches; only one shares the publisher
        // with the installed package. Intersect picks the right one.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE norm_names2 (norm_name TEXT, package INT64);
                CREATE TABLE norm_publishers2 (norm_publisher TEXT, package INT64);
                INSERT INTO norm_names2 VALUES ('git', 100), ('git', 200);
                INSERT INTO norm_publishers2 VALUES ('thegitdevelopmentcommunity', 100), ('microsoft', 200);";
            cmd.ExecuteNonQuery();
        }

        var rowid = Repository.LookupUniqueNormalizedIdentityForTesting(connection, "git", "thegitdevelopmentcommunity");
        Assert.Equal(100L, rowid);
    }

    [Fact]
    public void LookupUniqueNormalizedIdentity_MissesWhenPublisherDoesNotMatch()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE norm_names2 (norm_name TEXT, package INT64);
                CREATE TABLE norm_publishers2 (norm_publisher TEXT, package INT64);
                INSERT INTO norm_names2 VALUES ('foo', 100);
                INSERT INTO norm_publishers2 VALUES ('bar', 200);";
            cmd.ExecuteNonQuery();
        }

        var rowid = Repository.LookupUniqueNormalizedIdentityForTesting(connection, "foo", "bar");
        Assert.Null(rowid);
    }

    [Fact]
    public void LookupInstalledDbIdentityMatch_ReturnsProductCodeMatch()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE ids (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL);
                CREATE TABLE names (rowid INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE manifest (rowid INTEGER PRIMARY KEY, id INT64 NOT NULL, name INT64 NOT NULL);
                CREATE TABLE productcodes (rowid INTEGER PRIMARY KEY, productcode TEXT NOT NULL);
                CREATE TABLE productcodes_map (manifest INT64 NOT NULL, productcode INT64 NOT NULL);
                INSERT INTO ids VALUES (1, 'Git.Git');
                INSERT INTO names VALUES (1, 'Git');
                INSERT INTO manifest VALUES (10, 1, 1);
                INSERT INTO productcodes VALUES (20, 'git_is1');
                INSERT INTO productcodes_map VALUES (10, 20);";
            cmd.ExecuteNonQuery();
        }

        var package = new InstalledPackage
        {
            Name = "Git",
            LocalId = @"ARP\Machine\X64\Git_is1",
            InstalledVersion = "2.54.0",
            ProductCodes = ["git_is1"],
        };

        var hit = Repository.LookupInstalledDbIdentityMatchForTesting(connection, package);

        Assert.NotNull(hit);
        Assert.Equal("Git.Git", hit.Value.Id);
        Assert.Equal("Git", hit.Value.Name);
        Assert.Equal("ProductCode", hit.Value.MatchedBy);
    }

    [Fact]
    public void UpgradeFilter_HidesRequireExplicitUpgrade_ByDefault()
    {
        // winget hides RequireExplicitUpgrade rows from bulk `upgrade`
        // (Edge, Steam, Discord). pinget must do the same.
        var pkg = new InstalledPackage
        {
            Name = "Edge",
            LocalId = @"ARP\Machine\X64\Edge",
            InstalledVersion = "100.0",
            Scope = "Machine",
            InstallerCategory = "exe",
            Correlated = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Microsoft.Edge",
                Name = "Microsoft Edge",
                Version = "110.0",
            },
            CorrelatedRequiresExplicitUpgrade = true,
        };

        var bulkQuery = new ListQuery { UpgradeOnly = true };
        Assert.False(
            Repository.InstalledPackageMatchesUpgradeFilterForTesting(pkg, bulkQuery),
            "RequireExplicitUpgrade row must be hidden from bulk upgrade");

        // When the user explicitly targets it by id, winget shows it.
        var filteredQuery = new ListQuery { UpgradeOnly = true, Id = "Microsoft.Edge" };
        Assert.True(
            Repository.InstalledPackageMatchesUpgradeFilterForTesting(pkg, filteredQuery),
            "RequireExplicitUpgrade row must surface when the user filters for it");

        // Without the flag, the row appears in bulk upgrade.
        pkg.CorrelatedRequiresExplicitUpgrade = false;
        Assert.True(Repository.InstalledPackageMatchesUpgradeFilterForTesting(pkg, bulkQuery));
    }

    [Fact]
    public void UpgradeFilter_HidesLacksCompatibleInstaller_ByDefault()
    {
        var pkg = new InstalledPackage
        {
            Name = "Foo",
            LocalId = @"ARP\Machine\X64\Foo",
            InstalledVersion = "1.0",
            Scope = "Machine",
            InstallerCategory = "exe",
            Correlated = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Test.Foo",
                Name = "Foo",
                Version = "2.0",
            },
            CorrelatedLacksCompatibleInstaller = true,
        };

        var bulkQuery = new ListQuery { UpgradeOnly = true };
        Assert.False(Repository.InstalledPackageMatchesUpgradeFilterForTesting(pkg, bulkQuery));

        var filteredQuery = new ListQuery { UpgradeOnly = true, Id = "Test.Foo" };
        Assert.True(Repository.InstalledPackageMatchesUpgradeFilterForTesting(pkg, filteredQuery));

        pkg.CorrelatedLacksCompatibleInstaller = false;
        Assert.True(Repository.InstalledPackageMatchesUpgradeFilterForTesting(pkg, bulkQuery));
    }

    private static Manifest SyntheticManifestWithInstallerArches(params string[] arches)
    {
        var installers = arches.Select(a => new Installer { Architecture = a }).ToList();
        return new Manifest { Id = "Test", Name = "Test", Version = "1.0", Installers = installers };
    }

    [Fact]
    public void ManifestHasCompatibleInstaller_NeutralAlwaysMatches()
    {
        Assert.True(Repository.ManifestHasCompatibleInstallerForTesting(
            SyntheticManifestWithInstallerArches("neutral")));
    }

    [Fact]
    public void ManifestHasCompatibleInstaller_EmptyInstallersPassThrough()
    {
        Assert.True(Repository.ManifestHasCompatibleInstallerForTesting(
            SyntheticManifestWithInstallerArches()));
    }

    [Fact]
    public void ManifestHasCompatibleInstaller_RejectsAlienArchOnly()
    {
        Assert.False(Repository.ManifestHasCompatibleInstallerForTesting(
            SyntheticManifestWithInstallerArches("ppc")));
    }

    [Fact]
    public void ManifestHasCompatibleInstaller_MixedSetPassesIfAnyMatch()
    {
        Assert.True(Repository.ManifestHasCompatibleInstallerForTesting(
            SyntheticManifestWithInstallerArches("ppc", "neutral")));
    }

    [Fact]
    public void ApplyMsixResourceStringNameFix_ResolvesPlaceholderToCatalogName()
    {
        // App Installer's MSIX manifest stores DisplayName as
        // `ms-resource:appDisplayName`. Once we correlate it via PFN, we
        // know the catalog calls it "App Installer" — show that instead of
        // the unresolved placeholder, matching winget's output.
        var package = new InstalledPackage
        {
            Name = "ms-resource:appDisplayName",
            LocalId = @"MSIX\Microsoft.DesktopAppInstaller_1.28.239.0_arm64__8wekyb3d8bbwe",
            InstalledVersion = "1.28.239.0",
            Scope = "User",
            InstallerCategory = "msix",
            PackageFamilyNames = ["Microsoft.DesktopAppInstaller_8wekyb3d8bbwe"],
            Correlated = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Microsoft.AppInstaller",
                Name = "App Installer",
                Version = "1.28.240.0",
                MatchCriteria = "PackageFamilyName",
            },
        };
        Repository.ApplyMsixResourceStringNameFix(package);
        Assert.Equal("App Installer", package.Name);
    }

    [Fact]
    public void ApplyMsixResourceStringNameFix_NoopForNonMsix()
    {
        // The fix is gated on LocalId starting with "MSIX\\" so an unusual
        // ARP DisplayName that happens to contain "ms-resource:" doesn't
        // get silently rewritten.
        var package = new InstalledPackage
        {
            Name = "ms-resource:appDisplayName",
            LocalId = @"ARP\Machine\X64\{deadbeef}",
            InstalledVersion = "1.0",
            Scope = "Machine",
            InstallerCategory = "msi",
            Correlated = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Some.Package",
                Name = "Should Not Apply",
                Version = "1.0",
            },
        };
        Repository.ApplyMsixResourceStringNameFix(package);
        Assert.Equal("ms-resource:appDisplayName", package.Name);
    }

    [Fact]
    public void ApplyMsixResourceStringNameFix_SkipsResolvedNames()
    {
        // MSIX entries with already-resolved names must not be touched —
        // installed Name and catalog Name may legitimately differ.
        var package = new InstalledPackage
        {
            Name = "Microsoft Teams",
            LocalId = @"MSIX\MSTeams_25290.205.4069.4894_arm64__8wekyb3d8bbwe",
            InstalledVersion = "25290.205.4069.4894",
            Scope = "User",
            InstallerCategory = "msix",
            PackageFamilyNames = ["MSTeams_8wekyb3d8bbwe"],
            Correlated = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Microsoft.Teams",
                Name = "Microsoft Teams Catalog Name",
                Version = "26106.1906.4665.7308",
                MatchCriteria = "PackageFamilyName",
            },
        };
        Repository.ApplyMsixResourceStringNameFix(package);
        Assert.Equal("Microsoft Teams", package.Name);
    }

    [Fact]
    public void UnflipPackedGuid_ReversesMsiInstallerPacking()
    {
        // Verified against the live Installer hive: the user's installed
        // Node.js ProductCode `{9292CBD9-...}` packs to
        // `9DBC2929593B4D2488740C8E00C4F652`.
        Assert.Equal(
            "{9292cbd9-b395-42d4-8847-c0e8004c6f25}",
            InstalledPackages.UnflipPackedGuid("9DBC2929593B4D2488740C8E00C4F652"));
        Assert.Equal(
            "{47c07a3a-42ef-4213-a85d-8f5a59077c28}",
            InstalledPackages.UnflipPackedGuid("A3A70C74FE2431248AD5F8A59570C782"));
        Assert.Null(InstalledPackages.UnflipPackedGuid("nothex"));
        Assert.Null(InstalledPackages.UnflipPackedGuid("9DBC2929593B4D2488740C8E00C4F65"));
        Assert.Null(InstalledPackages.UnflipPackedGuid("ZZZZZZZZ593B4D2488740C8E00C4F652"));
    }

    [Fact]
    public void ParseMsixFullName_ReturnsBasePackageMetadata()
    {
        var parsed = InstalledPackages.ParseMsixFullName(
            "Microsoft.XboxSpeechToTextOverlay_1.97.17002.0_x64__8wekyb3d8bbwe");

        Assert.NotNull(parsed);
        Assert.Equal("1.97.17002.0", parsed.Value.Version);
        Assert.Equal("", parsed.Value.ResourceId);
        Assert.Equal("Microsoft.XboxSpeechToTextOverlay_8wekyb3d8bbwe", parsed.Value.FamilyName);
        Assert.False(InstalledPackages.IsMsixSplitResourcePackage(parsed.Value.ResourceId));
    }

    [Fact]
    public void ParseMsixFullName_ClassifiesSplitResourcePackages()
    {
        var parsed = InstalledPackages.ParseMsixFullName(
            "Microsoft.XboxSpeechToTextOverlay_1.97.17002.0_neutral_split.scale-125_8wekyb3d8bbwe");

        Assert.NotNull(parsed);
        Assert.Equal("1.97.17002.0", parsed.Value.Version);
        Assert.Equal("split.scale-125", parsed.Value.ResourceId);
        Assert.Equal(
            "Microsoft.XboxSpeechToTextOverlay_split.scale-125_8wekyb3d8bbwe",
            parsed.Value.FamilyName);
        Assert.True(InstalledPackages.IsMsixSplitResourcePackage(parsed.Value.ResourceId));
    }

    [Fact]
    public void DedupeCorrelatedForUpgrade_PrefersCanonicalRowOverRawArp()
    {
        // VS-installed .NET SDK has ARP `40.10.18029` (no canonical mapping)
        // while the proper install has `10.0.108` (canonical). Without the
        // canonical preference, compare_version picks the wrong row and the
        // upgrade disappears.
        var raw = InstalledWithCorrelation("Microsoft.DotNet.SDK.10", "40.10.18029", canonical: false);
        var canonical = InstalledWithCorrelation("Microsoft.DotNet.SDK.10", "10.0.108", canonical: true);

        var result = Repository.DedupeCorrelatedForUpgrade([raw, canonical]);
        Assert.Single(result);
        Assert.Equal("10.0.108", result[0].InstalledVersion);
        Assert.True(result[0].InstalledVersionCanonical);
    }

    [Fact]
    public void DedupeCorrelatedForUpgrade_KeepsHighestAmongCanonical()
    {
        var lower = InstalledWithCorrelation("Microsoft.WindowsAppRuntime.1.7", "1.7.7", canonical: true);
        var higher = InstalledWithCorrelation("Microsoft.WindowsAppRuntime.1.7", "1.7.9", canonical: true);

        var result = Repository.DedupeCorrelatedForUpgrade([lower, higher]);
        Assert.Single(result);
        Assert.Equal("1.7.9", result[0].InstalledVersion);
    }

    [Fact]
    public void DedupeCorrelatedForUpgrade_LeavesUncorrelatedAlone()
    {
        var uncorrelated = new InstalledPackage
        {
            Name = "Foo",
            LocalId = @"ARP\Machine\X64\Foo",
            InstalledVersion = "1.0",
            Scope = "Machine",
            InstallerCategory = "exe",
        };
        var result = Repository.DedupeCorrelatedForUpgrade([uncorrelated]);
        Assert.Single(result);
    }

    private static InstalledPackage InstalledWithCorrelation(string id, string installedVersion, bool canonical)
    {
        return new InstalledPackage
        {
            Name = $"{id} install",
            LocalId = $@"ARP\Machine\X64\{id}",
            InstalledVersion = installedVersion,
            Scope = "Machine",
            InstallerCategory = "msi",
            Correlated = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = id,
                Name = id,
                Version = "99.0.0",
            },
            InstalledVersionCanonical = canonical,
        };
    }

    [Fact]
    public void CorrelateInstalledPackage_MsixWithResourceStringNameDoesNotCorrelate()
    {
        // Some MSIX entries have unresolved resource-string display names
        // (e.g. `ms-resource:appDisplayName`). They shouldn't latch onto an
        // unrelated catalog package via the loose substring rule.
        var installed = new InstalledPackage
        {
            Name = "ms-resource:appDisplayName",
            LocalId = @"MSIX\Microsoft.DesktopAppInstaller_1.28.239.0_arm64__8wekyb3d8bbwe",
            InstalledVersion = "1.28.239.0",
            Scope = "User",
            InstallerCategory = "msix",
            PackageFamilyNames = ["Microsoft.DesktopAppInstaller_8wekyb3d8bbwe"],
            ProductCodes = [],
            UpgradeCodes = [],
        };
        var candidates = new List<SearchMatch>
        {
            new()
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Microsoft.AppInstaller",
                Name = "App Installer",
                Version = "1.28.240.0",
            },
        };

        Assert.Null(Repository.CorrelateInstalledPackage(installed, candidates, loose: true));
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
    public void IsPortableZipInstaller_DetectsNestedPortableZip()
    {
        var installer = new Installer
        {
            InstallerType = "zip",
            NestedInstallerType = "portable",
        };

        Assert.True(InstallerDispatch.IsPortableZipInstaller(installer));
    }

    [Fact]
    public void IsPortableZipInstaller_RejectsPlainZipWithoutNestedPortable()
    {
        var installer = new Installer
        {
            InstallerType = "zip",
        };

        Assert.False(InstallerDispatch.IsPortableZipInstaller(installer));
    }

    [Fact]
    public void PathStartsWith_RejectsStringPrefixCollisions()
    {
        // Regression: a naive string.StartsWith would falsely consider
        // C:\Foobar a child of C:\Foo and let the Links\ sweep delete unrelated
        // symlinks.
        Assert.False(InstallerDispatch.PathStartsWithCaseInsensitive(
            @"C:\Foobar\bin.exe", @"C:\Foo"));
        Assert.False(InstallerDispatch.PathStartsWithCaseInsensitive(
            @"C:\Foo\bin.exe.bak", @"C:\Foo\bin.exe"));
    }

    [Fact]
    public void PathStartsWith_AcceptsRealChildrenAndSelf()
    {
        // Dir install: target lives inside the install dir.
        Assert.True(InstallerDispatch.PathStartsWithCaseInsensitive(
            @"C:\Foo\sub\bin.exe", @"C:\Foo"));
        // File install: target is the same file we removed.
        Assert.True(InstallerDispatch.PathStartsWithCaseInsensitive(
            @"C:\Foo\bin.exe", @"C:\Foo\bin.exe"));
    }

    [Fact]
    public void PathStartsWith_IsCaseInsensitive()
    {
        Assert.True(InstallerDispatch.PathStartsWithCaseInsensitive(
            @"c:\users\test\appdata\local\microsoft\winget\packages\foo\bin.exe",
            @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Foo"));
    }

    [Fact]
    public void PathStartsWith_HandlesMixedSeparators()
    {
        // NestedInstallerFiles RelativeFilePath uses '/' in manifests, so the
        // path we joined can end up mixed-separator on Windows.
        Assert.True(InstallerDispatch.PathStartsWithCaseInsensitive(
            @"C:\Foo\sub/bin.exe", @"C:\Foo"));
    }

    [Fact]
    public void PortableSourceIdentifier_MapsWingetCatalogToCanonicalId()
    {
        Assert.Equal(
            "Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallerDispatch.PortableSourceIdentifier(new PackageQuery { Source = "winget" }));
        Assert.Equal(
            "Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallerDispatch.PortableSourceIdentifier(new PackageQuery { Source = "WINGET" }));
    }

    [Fact]
    public void PortableSourceIdentifier_PassesThroughCustomSources()
    {
        Assert.Equal(
            "internal-feed",
            InstallerDispatch.PortableSourceIdentifier(new PackageQuery { Source = "internal-feed" }));
    }

    [Fact]
    public void PortableSourceIdentifier_DefaultsForMissingOrEmptySource()
    {
        Assert.Equal(
            "Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallerDispatch.PortableSourceIdentifier(new PackageQuery()));
        Assert.Equal(
            "Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallerDispatch.PortableSourceIdentifier(new PackageQuery { Source = "" }));
    }

    [Fact]
    public void PortableSubkeyName_MatchesWingetFormat()
    {
        Assert.Equal(
            "BurntSushi.ripgrep.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallerDispatch.PortableSubkeyName("BurntSushi.ripgrep.MSVC", "Microsoft.Winget.Source_8wekyb3d8bbwe"));
    }

    [Fact]
    public void DeterminePortableAlias_PrefersNestedInstallerAlias()
    {
        var installer = new Installer
        {
            NestedInstallerFiles = new List<NestedInstallerFile>
            {
                new() { RelativeFilePath = "ripgrep/rg.exe", PortableCommandAlias = "rg" }
            },
            Commands = new List<string> { "other-name" }
        };
        Assert.Equal("rg", InstallerDispatch.DeterminePortableAlias(installer));
    }

    [Fact]
    public void DeterminePortableAlias_FallsBackToCommands()
    {
        var installer = new Installer
        {
            Commands = new List<string> { "nuget" }
        };
        Assert.Equal("nuget", InstallerDispatch.DeterminePortableAlias(installer));

        // Nested files exist but none declare PortableCommandAlias.
        installer = new Installer
        {
            NestedInstallerFiles = new List<NestedInstallerFile>
            {
                new() { RelativeFilePath = "subdir/bin.exe" }
            },
            Commands = new List<string> { "bin" }
        };
        Assert.Equal("bin", InstallerDispatch.DeterminePortableAlias(installer));
    }

    [Fact]
    public void DeterminePortableAlias_ReturnsNullWithoutAliasOrCommands()
    {
        Assert.Null(InstallerDispatch.DeterminePortableAlias(new Installer()));
    }

    [Fact]
    public void ResolvePortableInstallLocation_PrefersRequestOverride()
    {
        var result = InstallerDispatch.ResolvePortableInstallLocationPure(
            @"D:\Tools\rg", @"C:\Software\rg-test", "Sub_Source", @"C:\default");
        Assert.Equal(@"D:\Tools\rg", result);
    }

    [Fact]
    public void ResolvePortableInstallLocation_PreservesExistingLocationForUpgrades()
    {
        // #4823 scenario: upgrade with no --location must resolve to the
        // install_location the prior install recorded, not the default root.
        var result = InstallerDispatch.ResolvePortableInstallLocationPure(
            null, @"C:\Software\rg-test", "Sub_Source", @"C:\default");
        Assert.Equal(@"C:\Software\rg-test", result);
    }

    [Fact]
    public void ResolvePortableInstallLocation_FallsBackToDefaultRoot()
    {
        var result = InstallerDispatch.ResolvePortableInstallLocationPure(
            null, null, "Sub_Source", @"C:\default");
        Assert.Equal(Path.Combine(@"C:\default", "Sub_Source"), result);
    }

    [Fact]
    public void ResolvePortableInstallLocation_TreatsEmptyStringsAsUnset()
    {
        var result = InstallerDispatch.ResolvePortableInstallLocationPure(
            "", "", "Sub_Source", @"C:\default");
        Assert.Equal(Path.Combine(@"C:\default", "Sub_Source"), result);
    }

    [Fact]
    public void CleanDirectoryContents_RemovesFilesAndSubdirsButKeepsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pinget-clean-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "nested", "b.txt"), "world");

        InstallerDispatch.CleanDirectoryContents(root);

        Assert.True(Directory.Exists(root), "root dir itself must survive");
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void CleanDirectoryContents_IsNoOpOnNonexistentDir()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"pinget-clean-test-bogus-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(bogus));
        InstallerDispatch.CleanDirectoryContents(bogus);
        Assert.False(Directory.Exists(bogus));
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

[Collection(RepositoryStateCollection.Name)]
public class RepositoryEmbeddingTests
{
    private const string TesslPackageId = "tessl.tessl";

    [Fact]
    public void ShowManifest_RestExactId_ReturnsSerializableManifestAndSelectedInstaller()
    {
        using var server = new TestRestSourceServer();
        var diagnostics = new List<RepositoryWarning>();
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                Diagnostics = diagnostics.Add,
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.Rest));

            var result = repo.ShowManifest(new PackageQuery
            {
                Id = TesslPackageId,
                Exact = true,
                Source = "test",
                InstallerArchitecture = "x64",
            });

            Assert.Equal(TesslPackageId, result.PackageIdentifier);
            Assert.Equal("Tessl", result.PackageName);
            Assert.Equal("1.2.3", result.PackageVersion);
            Assert.Equal("test", result.SourceName);
            Assert.Equal(SourceKind.Rest, result.SourceKind);
            Assert.Equal("Tessl Publisher", result.Publisher);
            Assert.Equal("Tessl Author", result.Author);
            Assert.Equal("Tessl short description", result.ShortDescription);
            Assert.Equal("https://example.test/tessl", result.PackageUrl);
            Assert.Equal("MIT", result.License);
            Assert.Equal("https://example.test/license", result.LicenseUrl);
            Assert.Equal("Release notes", result.ReleaseNotes);
            Assert.Equal("https://example.test/release-notes", result.ReleaseNotesUrl);
            Assert.Equal(["ai", "cli"], result.Tags);
            Assert.Equal("https://example.test/tessl-256.ico", result.IconUrl);
            Assert.NotNull(result.Icon);
            Assert.Equal("ico", result.Icon!.IconFileType);
            Assert.Equal("256x256", result.Icon.IconResolution);
            Assert.Equal(2, result.Icons.Count);
            Assert.Equal("https://example.test/tessl-128.png", result.Icons[0].IconUrl);
            Assert.Equal(["Contoso.Dependency"], result.PackageDependencies);
            var installer = Assert.Single(result.Installers);
            Assert.Equal("https://example.test/tessl.exe", installer.InstallerUrl);
            Assert.Equal(new string('A', 64), installer.InstallerSha256);
            Assert.Equal("exe", installer.InstallerType);
            Assert.Equal("2026-01-02", installer.ReleaseDate);
            Assert.NotNull(result.SelectedInstaller);
            Assert.Equal(installer.InstallerUrl, result.SelectedInstaller!.InstallerUrl);
            Assert.Equal(["Installer.Dependency"], installer.PackageDependencies);
            Assert.Empty(result.SourceWarnings);
            Assert.Empty(diagnostics);

            var typedResult = repo.Show(new PackageQuery
            {
                Id = TesslPackageId,
                Exact = true,
                Source = "test",
                InstallerArchitecture = "x64",
            });

            Assert.Equal("Tessl short description", typedResult.Manifest.ShortDescription);
            Assert.Equal("https://example.test/tessl-256.ico", typedResult.Manifest.IconUrl);

            var json = JsonSerializer.Serialize(result, PingetJsonContext.Default.SerializableShowManifest);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(TesslPackageId, document.RootElement.GetProperty(nameof(SerializableShowManifest.PackageIdentifier)).GetString());
            Assert.Equal("https://example.test/tessl-256.ico",
                document.RootElement.GetProperty(nameof(SerializableShowManifest.IconUrl)).GetString());
            Assert.Equal("https://example.test/tessl-256.ico",
                document.RootElement.GetProperty(nameof(SerializableShowManifest.Icon)).GetProperty(nameof(PackageIcon.IconUrl)).GetString());
            Assert.Equal("https://example.test/tessl-128.png",
                document.RootElement.GetProperty(nameof(SerializableShowManifest.Icons))[0].GetProperty(nameof(PackageIcon.IconUrl)).GetString());
            Assert.Equal("https://example.test/tessl.exe",
                document.RootElement.GetProperty(nameof(SerializableShowManifest.SelectedInstaller)).GetProperty(nameof(SerializableInstaller.InstallerUrl)).GetString());
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_SingleRestSourceFailure_ThrowsSourceSearchExceptionWithDiagnostics()
    {
        using var server = new TestRestSourceServer(request =>
            request.Url?.AbsolutePath == "/manifestSearch"
                ? new TestRestResponse(503, """{"error":"source unavailable"}""")
                : TestRestSourceServer.DefaultResponse(request));

        var diagnostics = new List<RepositoryWarning>();
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                Diagnostics = diagnostics.Add,
            });
            ReplaceSources(repo, ("winget.pro", server.Url, SourceKind.Rest));

            var ex = Assert.Throws<SourceSearchException>(() => repo.Show(new PackageQuery
            {
                Id = TesslPackageId,
                Exact = true,
                Source = "winget.pro",
            }));

            Assert.Equal("winget.pro", ex.Warning.SourceName);
            Assert.Equal(SourceKind.Rest, ex.Warning.SourceKind);
            Assert.Equal(503, ex.Warning.HttpStatusCode);
            Assert.Contains("/manifestSearch", ex.Warning.RequestUri);
            Assert.Contains("503", ex.Warning.Message);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(ex.Warning.SourceName, diagnostic.SourceName);
            Assert.Equal(ex.Warning.HttpStatusCode, diagnostic.HttpStatusCode);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_RestExplicitMissingVersion_ThrowsInsteadOfReturningLatest()
    {
        using var server = new TestRestSourceServer();
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            ReplaceSources(repo, ("test", server.Url, SourceKind.Rest));

            var ex = Assert.Throws<SourceSearchException>(() => repo.Show(new PackageQuery
            {
                Id = TesslPackageId,
                Exact = true,
                Source = "test",
                Version = "9.9.9",
            }));

            var missing = Assert.IsType<MissingPackageVersionException>(ex.InnerException);
            Assert.Equal("9.9.9", missing.RequestedVersion);
            Assert.Contains("9.9.9", ex.Message);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedExplicitMissingVersion_RefreshesOnceAndReturnsRequestedVersion()
    {
        const string packageId = "Test.RefreshPackage";
        var initialCatalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");
        var refreshedCatalog = PreindexedCatalogFixture.Create(packageId, "1.1.0", "1.0.0");

        using var server = new TestPreindexedSourceServer(refreshedCatalog.MsixBytes, initialCatalog.Files.Concat(refreshedCatalog.Files));
        var diagnostics = new List<RepositoryWarning>();
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                Diagnostics = diagnostics.Add,
                PreIndexedSourceAutoUpdateInterval = null,
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            WritePreindexedIndex(appRoot, repo, "test", initialCatalog.IndexBytes);

            var result = repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
                Version = "1.1.0",
            });

            Assert.Equal("1.1.0", result.PackageVersion);
            Assert.Equal(1, server.SourceUpdateRequests);
            Assert.Contains(diagnostics, warning =>
                warning.Operation == "manifest-refresh" &&
                warning.Message.Contains(packageId, StringComparison.Ordinal) &&
                warning.Message.Contains("1.1.0", StringComparison.Ordinal));
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedExplicitMissingVersion_RefreshesAfterCachedAutoRefreshFailure()
    {
        const string packageId = "Test.RecoveredRefreshPackage";
        var initialCatalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");
        var refreshedCatalog = PreindexedCatalogFixture.Create(packageId, "1.1.0", "1.0.0");

        using var server = new TestPreindexedSourceServer(msixBytes: null, initialCatalog.Files.Concat(refreshedCatalog.Files));
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                PreIndexedSourceAutoUpdateInterval = TimeSpan.FromDays(1),
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            var indexPath = WritePreindexedIndex(appRoot, repo, "test", initialCatalog.IndexBytes);
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddDays(-1));

            var localResult = repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
            });

            Assert.Equal("1.0.0", localResult.PackageVersion);
            Assert.Equal(1, server.SourceUpdateRequests);

            server.SetMsixBytes(refreshedCatalog.MsixBytes);

            var refreshedResult = repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
                Version = "1.1.0",
            });

            Assert.Equal("1.1.0", refreshedResult.PackageVersion);
            Assert.Equal(2, server.SourceUpdateRequests);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedExplicitMissingVersion_AfterRefreshStillThrows()
    {
        const string packageId = "Test.StillMissingPackage";
        var initialCatalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");
        var refreshedCatalog = PreindexedCatalogFixture.Create(packageId, "1.1.0", "1.0.0");

        using var server = new TestPreindexedSourceServer(refreshedCatalog.MsixBytes, initialCatalog.Files.Concat(refreshedCatalog.Files));
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                PreIndexedSourceAutoUpdateInterval = null,
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            WritePreindexedIndex(appRoot, repo, "test", initialCatalog.IndexBytes);

            var ex = Assert.Throws<SourceSearchException>(() => repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
                Version = "2.0.0",
            }));

            var missing = Assert.IsType<MissingPackageVersionException>(ex.InnerException);
            Assert.Equal("2.0.0", missing.RequestedVersion);
            Assert.Equal(1, server.SourceUpdateRequests);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedStaleIndex_RefreshesBeforeLatestSelection()
    {
        const string packageId = "Test.StalePackage";
        var initialCatalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");
        var refreshedCatalog = PreindexedCatalogFixture.Create(packageId, "1.1.0", "1.0.0");

        using var server = new TestPreindexedSourceServer(refreshedCatalog.MsixBytes, initialCatalog.Files.Concat(refreshedCatalog.Files));
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                PreIndexedSourceAutoUpdateInterval = TimeSpan.FromDays(1),
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            var indexPath = WritePreindexedIndex(appRoot, repo, "test", initialCatalog.IndexBytes);
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddDays(-1));

            var result = repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
            });

            Assert.Equal("1.1.0", result.PackageVersion);
            Assert.Equal(1, server.SourceUpdateRequests);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedStaleIndex_NotModifiedKeepsLocalIndex()
    {
        const string packageId = "Test.NotModifiedPackage";
        var initialCatalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");
        var refreshedCatalog = PreindexedCatalogFixture.Create(packageId, "1.1.0", "1.0.0");

        using var server = new TestPreindexedSourceServer(refreshedCatalog.MsixBytes, initialCatalog.Files.Concat(refreshedCatalog.Files))
        {
            ETag = "\"same-index\"",
            LastModified = "Wed, 03 Jun 2026 12:00:00 GMT",
        };
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                PreIndexedSourceAutoUpdateInterval = TimeSpan.FromDays(1),
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            var source = repo.ListSources().Single(source => source.Name == "test");
            source.ETag = server.ETag;
            source.LastModified = server.LastModified;
            var indexPath = WritePreindexedIndex(appRoot, repo, "test", initialCatalog.IndexBytes);
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddDays(-1));

            var result = repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
            });

            Assert.Equal("1.0.0", result.PackageVersion);
            Assert.Equal(1, server.SourceUpdateRequests);
            Assert.Equal(server.ETag, source.ETag);
            Assert.Equal(server.LastModified, source.LastModified);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedFreshIndexMtime_DoesNotRefreshForStaleSourceMetadata()
    {
        const string packageId = "Test.FreshIndexPackage";
        var initialCatalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");
        var refreshedCatalog = PreindexedCatalogFixture.Create(packageId, "1.1.0", "1.0.0");

        using var server = new TestPreindexedSourceServer(refreshedCatalog.MsixBytes, initialCatalog.Files.Concat(refreshedCatalog.Files));
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                PreIndexedSourceAutoUpdateInterval = TimeSpan.FromDays(1),
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            repo.ListSources().Single(source => source.Name == "test").LastUpdate = DateTime.UtcNow.AddDays(-2);
            WritePreindexedIndex(appRoot, repo, "test", initialCatalog.IndexBytes);

            var result = repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
            });

            Assert.Equal("1.0.0", result.PackageVersion);
            Assert.Equal(0, server.SourceUpdateRequests);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedExplicitMissingVersion_DoesNotRefreshTwiceAfterStaleRefresh()
    {
        const string packageId = "Test.NoDoubleRefreshPackage";
        var initialCatalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");
        var refreshedCatalog = PreindexedCatalogFixture.Create(packageId, "1.1.0", "1.0.0");

        using var server = new TestPreindexedSourceServer(refreshedCatalog.MsixBytes, initialCatalog.Files.Concat(refreshedCatalog.Files));
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                PreIndexedSourceAutoUpdateInterval = TimeSpan.FromDays(1),
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            var indexPath = WritePreindexedIndex(appRoot, repo, "test", initialCatalog.IndexBytes);
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddDays(-1));

            var ex = Assert.Throws<SourceSearchException>(() => repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
                Version = "2.0.0",
            }));

            var missing = Assert.IsType<MissingPackageVersionException>(ex.InnerException);
            Assert.Equal("2.0.0", missing.RequestedVersion);
            Assert.Equal(1, server.SourceUpdateRequests);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedStaleIndex_WhenRefreshFails_UsesLocalLatestWithoutExplicitVersion()
    {
        const string packageId = "Test.OfflinePackage";
        var initialCatalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");

        using var server = new TestPreindexedSourceServer(msixBytes: null, initialCatalog.Files);
        var diagnostics = new List<RepositoryWarning>();
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                Diagnostics = diagnostics.Add,
                PreIndexedSourceAutoUpdateInterval = TimeSpan.FromDays(1),
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            var indexPath = WritePreindexedIndex(appRoot, repo, "test", initialCatalog.IndexBytes);
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddDays(-1));

            var result = repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
            });

            Assert.Equal("1.0.0", result.PackageVersion);
            Assert.Equal(1, server.SourceUpdateRequests);
            Assert.Contains(diagnostics, warning => warning.Operation == "source-refresh");
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedV2RequestedChannel_ThrowsInsteadOfIgnoringChannel()
    {
        const string packageId = "Test.ChannelPackage";
        var catalog = PreindexedCatalogFixture.Create(packageId, "1.0.0");

        using var server = new TestPreindexedSourceServer(catalog.MsixBytes, catalog.Files);
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                PreIndexedSourceAutoUpdateInterval = null,
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            WritePreindexedIndex(appRoot, repo, "test", catalog.IndexBytes);

            var ex = Assert.Throws<SourceSearchException>(() => repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
                Version = "1.0.0",
                Channel = "beta",
            }));

            Assert.Contains("beta", ex.Message);
            Assert.Contains("channel metadata", ex.Message);
            Assert.Equal(0, server.SourceUpdateRequests);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_PreindexedDownloadedManifestHashMismatch_ThrowsInsteadOfCachingStaleBytes()
    {
        const string packageId = "Test.HashMismatchPackage";
        const string version = "1.0.0";
        var catalog = PreindexedCatalogFixture.Create(packageId, version);
        var manifestPath = $"manifests/{packageId}/{version}.yaml";
        var staleManifestBytes = Encoding.UTF8.GetBytes($$"""
            PackageIdentifier: {{packageId}}
            PackageVersion: 0.9.0
            DefaultLocale: en-US
            ManifestType: singleton
            ManifestVersion: 1.10.0
            PackageLocale: en-US
            PackageName: {{packageId}}
            Publisher: Example
            License: MIT
            ShortDescription: stale package
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/{{packageId}}/0.9.0.exe
                InstallerSha256: {{new string('B', 64)}}
            """);
        var files = catalog.Files.Select(file =>
            string.Equals(file.Key, manifestPath, StringComparison.OrdinalIgnoreCase)
                ? new KeyValuePair<string, byte[]>(file.Key, staleManifestBytes)
                : file);

        using var server = new TestPreindexedSourceServer(catalog.MsixBytes, files);
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions
            {
                AppRoot = appRoot,
                PreIndexedSourceAutoUpdateInterval = null,
            });
            ReplaceSources(repo, ("test", server.Url, SourceKind.PreIndexed));
            WritePreindexedIndex(appRoot, repo, "test", catalog.IndexBytes);

            var ex = Assert.Throws<SourceSearchException>(() => repo.ShowManifest(new PackageQuery
            {
                Id = packageId,
                Exact = true,
                Source = "test",
                Version = version,
            }));

            Assert.Contains("Hash mismatch", ex.Message);
            Assert.Contains(manifestPath, ex.Message);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void Show_MultipleSourcesExposeStructuredMatches()
    {
        using var firstServer = new TestRestSourceServer();
        using var secondServer = new TestRestSourceServer();
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            ReplaceSources(
                repo,
                ("first", firstServer.Url, SourceKind.Rest),
                ("second", secondServer.Url, SourceKind.Rest));

            var ex = Assert.Throws<MultiplePackageMatchesException>(() => repo.Show(new PackageQuery
            {
                Id = TesslPackageId,
                Exact = true,
            }));

            Assert.Equal(["first", "second"], ex.Matches.Select(match => match.SourceName).ToArray());
            Assert.All(ex.Matches, match =>
            {
                Assert.Equal(TesslPackageId, match.Id);
                Assert.Equal(SourceKind.Rest, match.SourceKind);
            });
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void CliShowJson_MatchesCoreSerializableManifestForRestSource()
    {
        using var server = new TestRestSourceServer();
        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using (var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot }))
            {
                ReplaceSources(repo, ("test", server.Url, SourceKind.Rest));
                var core = repo.ShowManifest(new PackageQuery
                {
                    Id = TesslPackageId,
                    Exact = true,
                    Source = "test",
                    InstallerArchitecture = "x64",
                });

                var cli = RunCliShowJson(appRoot);

                Assert.Equal(core.PackageIdentifier, cli.RootElement.GetProperty(nameof(SerializableShowManifest.PackageIdentifier)).GetString());
                Assert.Equal(core.SourceName, cli.RootElement.GetProperty(nameof(SerializableShowManifest.SourceName)).GetString());
                Assert.Equal(core.SelectedInstaller!.InstallerUrl,
                    cli.RootElement.GetProperty(nameof(SerializableShowManifest.SelectedInstaller)).GetProperty(nameof(SerializableInstaller.InstallerUrl)).GetString());
                Assert.Equal(core.PackageDependencies,
                    cli.RootElement.GetProperty(nameof(SerializableShowManifest.PackageDependencies)).EnumerateArray().Select(item => item.GetString() ?? "").ToList());
            }
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    [SkippableFact]
    public void LiveWingetPro_TesslExactLookup_ReturnsManifestWhenAvailable()
    {
        const string liveSourceUrl = "https://api.winget.pro/4259fd23-6fcd-46bf-9287-be8833cfbdd5";
        Skip.IfNot(string.Equals(Environment.GetEnvironmentVariable("PINGET_LIVE_WINGETPRO_TESTS"), "1", StringComparison.Ordinal),
            "Set PINGET_LIVE_WINGETPRO_TESTS=1 to run the optional winget.pro live smoke test.");

        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = probe.GetAsync($"{liveSourceUrl}/information").GetAwaiter().GetResult();
            Skip.If(!response.IsSuccessStatusCode, $"winget.pro information endpoint returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            Skip.If(true, $"winget.pro is unavailable: {ex.Message}");
        }

        var appRoot = TestPaths.CreateTempAppRoot();
        try
        {
            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            ReplaceSources(repo, ("winget.pro", liveSourceUrl, SourceKind.Rest));

            var result = repo.ShowManifest(new PackageQuery
            {
                Id = TesslPackageId,
                Exact = true,
                Source = "winget.pro",
            });

            Assert.Equal(TesslPackageId, result.PackageIdentifier, ignoreCase: true);
            Assert.Equal("winget.pro", result.SourceName);
            Assert.NotEmpty(result.Installers);
        }
        finally
        {
            TestPaths.DeleteAppRoot(appRoot);
        }
    }

    private static void ReplaceSources(Repository repo, params (string Name, string Arg, SourceKind Kind)[] sources)
    {
        foreach (var source in repo.ListSources())
            repo.RemoveSource(source.Name);

        foreach (var source in sources)
            repo.AddSource(source.Name, source.Arg, source.Kind, trustLevel: "trusted");
    }

    private static string WritePreindexedIndex(string appRoot, Repository repo, string sourceName, byte[] indexBytes)
    {
        var source = repo.ListSources().Single(source => source.Name == sourceName);
        var stateDir = SourceStoreManager.SourceStateDir(source, appRoot);
        Directory.CreateDirectory(stateDir);
        var indexPath = Path.Combine(stateDir, "index.db");
        File.WriteAllBytes(indexPath, indexBytes);
        return indexPath;
    }

    private static JsonDocument RunCliShowJson(string appRoot)
    {
        var root = FindRepositoryRoot();
        var cliProject = Path.Combine(root, "dotnet", "src", "Devolutions.Pinget.Cli", "Devolutions.Pinget.Cli.csproj");
        var cliDll = Path.Combine(root, "dotnet", "src", "Devolutions.Pinget.Cli", "bin", "Release", "net10.0", "pinget.dll");

        var build = RunProcess("dotnet", ["build", cliProject, "-c", "Release", "-v:q"], appRoot);
        Assert.Equal(0, build.ExitCode);
        Assert.True(File.Exists(cliDll));

        var run = RunProcess("dotnet",
        [
            cliDll,
            "show",
            "--id", TesslPackageId,
            "--exact",
            "--source", "test",
            "--architecture", "x64",
            "--output", "json",
        ], appRoot);

        Assert.Equal(0, run.ExitCode);
        return JsonDocument.Parse(run.Stdout);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, IReadOnlyList<string> arguments, string appRoot)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PINGET_APPROOT"] = appRoot;

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "dotnet", "Devolutions.Pinget.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
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
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(appRoot))
            Directory.Delete(appRoot, recursive: true);
    }
}

file sealed record TestRestResponse(int StatusCode, string Body, string ContentType = "application/json");

file sealed record PreindexedCatalogFixture(
    byte[] IndexBytes,
    byte[] MsixBytes,
    IReadOnlyList<KeyValuePair<string, byte[]>> Files)
{
    public static PreindexedCatalogFixture Create(string packageId, params string[] versions)
    {
        if (versions.Length == 0)
            throw new ArgumentException("At least one version is required.", nameof(versions));

        var files = new List<KeyValuePair<string, byte[]>>();
        var versionEntries = new StringBuilder();

        foreach (var version in versions)
        {
            var manifestPath = $"manifests/{packageId}/{version}.yaml";
            var manifestBytes = Encoding.UTF8.GetBytes($$"""
                PackageIdentifier: {{packageId}}
                PackageVersion: {{version}}
                DefaultLocale: en-US
                ManifestType: singleton
                ManifestVersion: 1.10.0
                PackageLocale: en-US
                PackageName: {{packageId}}
                Publisher: Example
                License: MIT
                ShortDescription: test package
                Installers:
                  - Architecture: x64
                    InstallerType: exe
                    InstallerUrl: https://example.test/{{packageId}}/{{version}}.exe
                    InstallerSha256: {{new string('A', 64)}}
                """);
            files.Add(new KeyValuePair<string, byte[]>(manifestPath, manifestBytes));

            versionEntries.AppendLine($"- v: {version}");
            versionEntries.AppendLine($"  rP: {manifestPath}");
            versionEntries.AppendLine($"  s256H: {Sha256Hex(manifestBytes)}");
        }

        var versionDataYaml = "vD:\n" + versionEntries;
        var versionDataBytes = CompressMszyml(versionDataYaml);
        var versionDataHash = SHA256.HashData(versionDataBytes);
        var versionDataHashHex = Convert.ToHexString(versionDataHash).ToLowerInvariant();
        var versionDataPath = $"packages/{packageId}/{versionDataHashHex[..8]}/versionData.mszyml";
        files.Add(new KeyValuePair<string, byte[]>(versionDataPath, versionDataBytes));

        var indexBytes = BuildIndex(packageId, versions[0], versionDataHash);
        var msixBytes = BuildMsix(indexBytes);
        return new PreindexedCatalogFixture(indexBytes, msixBytes, files);
    }

    private static byte[] BuildIndex(string packageId, string latestVersion, byte[] versionDataHash)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"pinget-test-index-{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE packages (
                        id TEXT NOT NULL,
                        name TEXT NOT NULL,
                        moniker TEXT,
                        latest_version TEXT,
                        hash BLOB
                    );
                    INSERT INTO packages (id, name, moniker, latest_version, hash)
                    VALUES (@id, @name, NULL, @version, @hash);";
                cmd.Parameters.AddWithValue("@id", packageId);
                cmd.Parameters.AddWithValue("@name", packageId);
                cmd.Parameters.AddWithValue("@version", latestVersion);
                cmd.Parameters.Add("@hash", SqliteType.Blob).Value = versionDataHash;
                cmd.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            return File.ReadAllBytes(dbPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private static byte[] BuildMsix(byte[] indexBytes)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("Public/index.db");
            using var entryStream = entry.Open();
            entryStream.Write(indexBytes);
        }

        return stream.ToArray();
    }

    private static byte[] CompressMszyml(string yaml)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)'C');
        stream.WriteByte((byte)'K');
        using (var deflate = new DeflateStream(stream, CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(yaml);
            deflate.Write(bytes);
        }

        return stream.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

file sealed class TestPreindexedSourceServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts = new();
    private byte[]? _msixBytes;
    private readonly Dictionary<string, byte[]> _files;

    public TestPreindexedSourceServer(byte[]? msixBytes, IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        _msixBytes = msixBytes;
        _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            _files[file.Key.Replace('\\', '/').TrimStart('/')] = file.Value;

        var port = GetFreePort();
        Url = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{Url}/");
        _listener.Start();
        _loopTask = Task.Run(HandleRequestsAsync, _cts.Token);
    }

    public string Url { get; }
    public int SourceUpdateRequests { get; private set; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }

    public void SetMsixBytes(byte[]? msixBytes) => _msixBytes = msixBytes;

    private async Task HandleRequestsAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync();
                var path = context.Request.Url?.AbsolutePath.TrimStart('/') ?? "";
                if (path.Equals("source2.msix", StringComparison.OrdinalIgnoreCase))
                {
                    SourceUpdateRequests++;
                    if (_msixBytes is null)
                    {
                        context.Response.StatusCode = 503;
                        continue;
                    }

                    if (ETag is not null &&
                        context.Request.Headers["If-None-Match"]?.Split(',').Select(value => value.Trim()).Contains(ETag) == true)
                    {
                        context.Response.StatusCode = 304;
                        AddValidatorHeaders(context.Response);
                        continue;
                    }

                    if (LastModified is not null &&
                        string.Equals(context.Request.Headers["If-Modified-Since"], LastModified, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = 304;
                        AddValidatorHeaders(context.Response);
                        continue;
                    }

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.Headers.Add("x-ms-meta-sourceversion", "test-source-version");
                    AddValidatorHeaders(context.Response);
                    context.Response.ContentLength64 = _msixBytes.Length;
                    await context.Response.OutputStream.WriteAsync(_msixBytes, _cts.Token);
                    continue;
                }

                if (_files.TryGetValue(path, out var bytes))
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    continue;
                }

                context.Response.StatusCode = 404;
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
    }

    private void AddValidatorHeaders(HttpListenerResponse response)
    {
        if (ETag is not null)
            response.Headers.Add("ETag", ETag);

        if (LastModified is not null)
            response.Headers.Add("Last-Modified", LastModified);
    }

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

file sealed class TestRestSourceServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<HttpListenerRequest, TestRestResponse> _handler;

    public TestRestSourceServer(Func<HttpListenerRequest, TestRestResponse>? handler = null)
    {
        _handler = handler ?? DefaultResponse;
        var port = GetFreePort();
        Url = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{Url}/");
        _listener.Start();
        _loopTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                    var response = _handler(context.Request);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(response.Body);
                    context.Response.StatusCode = response.StatusCode;
                    context.Response.ContentType = response.ContentType;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, _cts.Token);
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

    public static TestRestResponse DefaultResponse(HttpListenerRequest request)
    {
        return request.Url?.AbsolutePath switch
        {
            "/information" => new TestRestResponse(200, """
                {
                  "Data": {
                    "SourceIdentifier": "Test.Rest",
                    "ServerSupportedVersions": [ "1.10.0" ],
                    "RequiredPackageMatchFields": [],
                    "UnsupportedPackageMatchFields": []
                  }
                }
                """),
            "/manifestSearch" => new TestRestResponse(200, """
                {
                  "Data": [
                    {
                      "PackageIdentifier": "tessl.tessl",
                      "PackageName": "Tessl",
                      "Versions": [
                        { "PackageVersion": "1.2.3", "Channel": "" }
                      ]
                    }
                  ]
                }
                """),
            "/packageManifests/tessl.tessl" => new TestRestResponse(200, $$"""
                {
                  "Data": {
                    "PackageIdentifier": "tessl.tessl",
                    "PackageName": "Tessl",
                    "DefaultLocale": {
                      "PackageName": "Tessl",
                      "Publisher": "Tessl Publisher",
                      "Author": "Tessl Author",
                      "ShortDescription": "Tessl short description",
                      "Description": "Tessl long description",
                      "PackageUrl": "https://example.test/tessl",
                      "License": "MIT",
                      "LicenseUrl": "https://example.test/license",
                      "ReleaseNotes": "Release notes",
                      "ReleaseNotesUrl": "https://example.test/release-notes",
                      "Tags": [ "ai", "cli" ],
                      "Icons": [
                        {
                          "IconUrl": "https://example.test/tessl-128.png",
                          "IconFileType": "png",
                          "IconResolution": "128x128",
                          "IconTheme": "default"
                        },
                        {
                          "IconUrl": "https://example.test/tessl-256.ico",
                          "IconFileType": "ico",
                          "IconResolution": "256x256"
                        }
                      ]
                    },
                    "Versions": [
                      {
                        "PackageVersion": "1.2.3",
                        "DefaultLocale": {
                          "PackageName": "Tessl",
                          "Publisher": "Tessl Publisher",
                          "Author": "Tessl Author",
                          "ShortDescription": "Tessl short description",
                          "Description": "Tessl long description",
                          "PackageUrl": "https://example.test/tessl",
                          "License": "MIT",
                          "LicenseUrl": "https://example.test/license",
                          "ReleaseNotes": "Release notes",
                          "ReleaseNotesUrl": "https://example.test/release-notes",
                          "Tags": [ "ai", "cli" ]
                        },
                        "Dependencies": {
                          "PackageDependencies": [
                            { "PackageIdentifier": "Contoso.Dependency" }
                          ]
                        },
                        "Installers": [
                          {
                            "Architecture": "x64",
                            "InstallerType": "exe",
                            "InstallerUrl": "https://example.test/tessl.exe",
                            "InstallerSha256": "{{new string('A', 64)}}",
                            "ReleaseDate": "2026-01-02",
                            "Dependencies": {
                              "PackageDependencies": [
                                { "PackageIdentifier": "Installer.Dependency" }
                              ]
                            }
                          }
                        ],
                        "ManifestVersion": "1.10.0"
                      }
                    ],
                    "ManifestVersion": "1.10.0"
                  }
                }
                """),
            _ => new TestRestResponse(404, """{"error":"not found"}"""),
        };
    }

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
