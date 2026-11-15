using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Wave 17 (installed packages, ARP, correlation, name normalization) regression tests.</summary>
public class Wave17DefectTests
{
    private static InstalledPackage Installed(string name, string localId) => new()
    {
        Name = name,
        LocalId = localId,
        InstalledVersion = "1.0.0",
    };

    private static SqliteConnection MemoryDb(string sql)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return connection;
    }

    // ── W17-1: the SAP Business Objects short-circuit still case-folds ──

    [Fact]
    public void SapBusinessObjectsNames_AreCaseFolded()
    {
        // winget folds every name before normalizing it, so the catalog stores the SAP short-circuit value
        // lowercase.
        Assert.Equal("boe.webtier-14.0.4-core-64", NameNormalization.NormalizeName("BOE.WebTier-14.0.4-CORE-64").Name);
    }

    // ── W17-2: NFKC normalization before the regexes ──

    [Fact]
    public void CompatibilityCharacters_AreNfkcNormalized()
    {
        // ™ decomposes to TM and full-width letters to ASCII under NFKC.
        Assert.Equal("contosotmstudio", NameNormalization.NormalizeName("Contoso™ Studio").Name);
        Assert.Equal("contosostudio", NameNormalization.NormalizeName("Ｃｏｎｔｏｓｏ Studio").Name);
        Assert.Equal("contoso", NameNormalization.NormalizePublisher("Ｃｏｎｔｏｓｏ Ltd"));
    }

    // ── W17-3: full case folding (ß → ss, ς → σ) ──

    [Fact]
    public void NormalizedValues_UseFullCaseFolding()
    {
        Assert.Equal("strassenavigator", NameNormalization.NormalizeName("Straße Navigator").Name);
        Assert.Equal("grossmann", NameNormalization.NormalizePublisher("Großmann GmbH"));
        Assert.Equal("κοσμοσ", NameNormalization.NormalizeName("κοσμος").Name);
    }

    // ── W17-4: the @@ suffix is searched from the fourth UTF-16 unit ──

    [Fact]
    public void AtAtSuffix_IsSearchedFromTheFourthCharacter()
    {
        // winget: find(L"@@", 3) — an early @@ does not hide a later one.
        Assert.Equal("abcd", NameNormalization.NormalizeName("AB@@CD@@Internal").Name);
        Assert.Equal("abcdef", NameNormalization.NormalizeName("ABCDEF@@Internal").Name);
    }

    // ── W17-5: architecture-qualified normalized-name correlation first ──

    [Fact]
    public void NormalizedIdentity_PrefersArchitectureQualifiedName()
    {
        // The index stores both contosotool and contosotool(X64) / contosotool(X86) for ARP DisplayNames
        // carrying an architecture.
        using var connection = MemoryDb("""
            CREATE TABLE norm_names2 (norm_name TEXT, package INT64);
            CREATE TABLE norm_publishers2 (norm_publisher TEXT, package INT64);
            INSERT INTO norm_names2 VALUES ('contosotool', 100), ('contosotool(X64)', 100),
                                           ('contosotool', 200), ('contosotool(X86)', 200);
            INSERT INTO norm_publishers2 VALUES ('contoso', 100), ('contoso', 200);
            """);

        var x64 = NameNormalization.NormalizeName("Contoso Tool (x64)");
        Assert.Equal(NameNormalization.Architecture.X64, x64.Architecture);
        Assert.Equal(100L, Repository.LookupInstalledNormalizedIdentityForTesting(connection, x64, "contoso"));
        var x86 = NameNormalization.NormalizeName("Contoso Tool (x86)");
        Assert.Equal(200L, Repository.LookupInstalledNormalizedIdentityForTesting(connection, x86, "contoso"));
        // Without an architecture the plain name stays ambiguous.
        var plain = NameNormalization.NormalizeName("Contoso Tool");
        Assert.Null(Repository.LookupInstalledNormalizedIdentityForTesting(connection, plain, "contoso"));
    }

    [Fact]
    public void NormalizedIdentity_FallsBackToPlainNameWithoutArchitectureRows()
    {
        using var connection = MemoryDb("""
            CREATE TABLE norm_names2 (norm_name TEXT, package INT64);
            CREATE TABLE norm_publishers2 (norm_publisher TEXT, package INT64);
            INSERT INTO norm_names2 VALUES ('contosotool', 100);
            INSERT INTO norm_publishers2 VALUES ('contoso', 100);
            """);
        var x64 = NameNormalization.NormalizeName("Contoso Tool (x64)");
        Assert.Equal(100L, Repository.LookupInstalledNormalizedIdentityForTesting(connection, x64, "contoso"));
    }

    // ── W17-6: installed.db without every identity table pair ──

    [Fact]
    public void InstalledDbLookup_SkipsMissingIdentityTables()
    {
        // A tracking DB from a schema before 1.6 has no upgradecodes tables.
        using var connection = MemoryDb("""
            CREATE TABLE ids (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL);
            CREATE TABLE names (rowid INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE manifest (rowid INTEGER PRIMARY KEY, id INT64 NOT NULL, name INT64 NOT NULL);
            CREATE TABLE productcodes (rowid INTEGER PRIMARY KEY, productcode TEXT NOT NULL);
            CREATE TABLE productcodes_map (manifest INT64 NOT NULL, productcode INT64 NOT NULL);
            INSERT INTO ids VALUES (1, 'Contoso.Tool');
            INSERT INTO names VALUES (1, 'Contoso Tool');
            INSERT INTO manifest VALUES (10, 1, 1);
            INSERT INTO productcodes VALUES (20, '{11111111-2222-3333-4444-555555555555}');
            INSERT INTO productcodes_map VALUES (10, 20);
            """);
        var package = Installed("Contoso Tool", @"ARP\Machine\X64\{11111111-2222-3333-4444-555555555555}") with
        {
            PackageFamilyNames = ["Contoso.Tool_abcdefghijklm"],
            UpgradeCodes = ["{99999999-8888-7777-6666-555555555555}"],
            ProductCodes = ["{11111111-2222-3333-4444-555555555555}"],
        };

        var hit = Repository.LookupInstalledDbIdentityMatchForTesting(connection, package);

        Assert.NotNull(hit);
        Assert.Equal("Contoso.Tool", hit.Value.Id);
        Assert.Equal("ProductCode", hit.Value.MatchedBy);
    }

    // ── W17-7: list queries match system reference strings exactly ──

    [Fact]
    public void ListQuery_MatchesPackageFamilyNameAndCodes()
    {
        var msix = Installed("Contoso Terminal", @"MSIX\Contoso.Terminal_1.2.3.0_x64__abcdefghijklm") with
        {
            PackageFamilyNames = ["Contoso.Terminal_abcdefghijklm"],
        };
        Assert.True(Repository.ListPackageMatchesForTesting(msix, new ListQuery { Query = "contoso.terminal_abcdefghijklm" }));

        var msi = Installed("Contoso Agent", @"ARP\Machine\X64\{11111111-2222-3333-4444-555555555555}") with
        {
            ProductCodes = ["{11111111-2222-3333-4444-555555555555}"],
            UpgradeCodes = ["{99999999-8888-7777-6666-555555555555}"],
        };
        Assert.True(Repository.ListPackageMatchesForTesting(
            msi, new ListQuery { Query = "{11111111-2222-3333-4444-555555555555}", Exact = true }));
        Assert.True(Repository.ListPackageMatchesForTesting(
            msi, new ListQuery { Query = "{99999999-8888-7777-6666-555555555555}" }));
    }

    // ── W17-8: --name also matches the correlated catalog name ──

    [Fact]
    public void ListNameFilter_MatchesCorrelatedCatalogName()
    {
        var package = Installed("Contoso Browser (x64 en-US)", @"ARP\Machine\X64\Contoso Browser");
        package.Correlated = new SearchMatch
        {
            SourceName = "winget",
            SourceKind = SourceKind.PreIndexed,
            Id = "Contoso.Browser",
            Name = "Contoso Browser",
            Version = "2.0.0",
        };

        Assert.True(Repository.ListPackageMatchesForTesting(package, new ListQuery { Name = "Contoso Browser", Exact = true }));
        Assert.False(Repository.ListPackageMatchesForTesting(package, new ListQuery { Name = "Fabrikam" }));
    }

    // ── W17-9 / W17-10: registry enumeration parity with WinGet ──

    [SupportedOSPlatform("windows")]
    private sealed class SandboxKey : IDisposable
    {
        public SandboxKey(string label)
        {
            Path = $@"Software\PingetWave17Tests\{label}_{Environment.ProcessId}_{Guid.NewGuid():N}";
            Registry.CurrentUser.CreateSubKey(Path).Dispose();
        }

        public string Path { get; }

        public RegistryKey Create(string relative) => Registry.CurrentUser.CreateSubKey($@"{Path}\{relative}");

        public RegistryKey Open() => Registry.CurrentUser.OpenSubKey(Path)!;

        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
    }

    [Fact]
    public void MsixPackageWithoutDisplayName_UsesPackageName()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var sandbox = new SandboxKey("msix");
        const string fullName = "Contoso.NoDisplayName_1.2.3.0_x64__abcdefghijklm";
        using (var package = sandbox.Create($@"{InstalledPackages.AppModelPackagesPath}\{fullName}"))
            package.SetValue("PackageRootFolder", $@"C:\Program Files\WindowsApps\{fullName}");

        var packages = new List<InstalledPackage>();
        using (var root = sandbox.Open())
            InstalledPackages.CollectAppModelPackages(packages, new HashSet<string>(StringComparer.OrdinalIgnoreCase), root, "User");

        var listed = Assert.Single(packages);
        Assert.Equal("Contoso.NoDisplayName", listed.Name);
        Assert.Equal("1.2.3.0", listed.InstalledVersion);
        Assert.Equal(["Contoso.NoDisplayName_abcdefghijklm"], listed.PackageFamilyNames);
    }

    [Fact]
    public void ArpEntriesWithParentKeyName_AreListed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var sandbox = new SandboxKey("arp");
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        using (var update = sandbox.Create($@"{uninstall}\ContosoUpdate"))
        {
            update.SetValue("DisplayName", "Update for Contoso Suite (KB1234567)");
            update.SetValue("DisplayVersion", "1.0");
            update.SetValue("ParentKeyName", "ContosoSuite");
        }
        using (var hidden = sandbox.Create($@"{uninstall}\ContosoComponent"))
        {
            hidden.SetValue("DisplayName", "Contoso Component");
            hidden.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
        }

        var packages = new List<InstalledPackage>();
        using (var root = sandbox.Open())
        {
            InstalledPackages.CollectArpPackages(
                packages, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), root, "User", "X64");
        }

        var listed = Assert.Single(packages);
        Assert.Equal("Update for Contoso Suite (KB1234567)", listed.Name);
        Assert.Equal(@"ARP\User\X64\ContosoUpdate", listed.LocalId);
    }
}
