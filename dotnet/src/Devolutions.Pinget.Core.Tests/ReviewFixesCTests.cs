using Microsoft.Data.Sqlite;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>
/// Review fixes C regression tests: pin source filters and casing, source-cache path safety, and portable
/// directory tracking.
/// </summary>
public class ReviewFixesCTests
{
    private static string CreateAppRoot()
    {
        var appRoot = Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appRoot);
        return appRoot;
    }

    private static void DeleteAppRoot(string appRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(appRoot))
            Directory.Delete(appRoot, recursive: true);
    }

    private static List<(string PackageId, string SourceId)> PinIds(IEnumerable<PinRecord> pins) =>
        pins.Select(pin => (pin.PackageId, pin.SourceId)).OrderBy(pin => pin.PackageId, StringComparer.Ordinal)
            .ThenBy(pin => pin.SourceId, StringComparer.Ordinal).ToList();

    // ── #4: null = no source filter, "" = the source-less pin ──

    [Fact]
    public void EmptySourceId_SelectsOnlySourceLessPins()
    {
        var appRoot = CreateAppRoot();
        try
        {
            PinStore.Add("Contoso.Tool", "1.0", "winget", PinType.Pinning, appRoot);
            PinStore.Add("Contoso.Tool", "*", "", PinType.Blocking, appRoot);

            Assert.Equal(2, PinStore.List(appRoot).Count);
            Assert.Equal([("Contoso.Tool", "")], PinIds(PinStore.List(appRoot, "")));

            Assert.True(PinStore.Remove("Contoso.Tool", appRoot, ""));
            Assert.Equal([("Contoso.Tool", "winget")], PinIds(PinStore.List(appRoot)));

            PinStore.Add("Fabrikam.Tool", "*", "", PinType.Pinning, appRoot);
            PinStore.Reset(appRoot, "");
            Assert.Equal([("Contoso.Tool", "winget")], PinIds(PinStore.List(appRoot)));

            Assert.True(PinStore.Remove("contoso.tool", appRoot));
            Assert.Empty(PinStore.List(appRoot));
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── #4: Add replaces a pin whose id/source differ only by case ──

    [Fact]
    public void Add_ReplacesCaseVariantPinInsteadOfDuplicating()
    {
        var appRoot = CreateAppRoot();
        try
        {
            PinStore.Add("contoso.tool", "1.0", "WINGET", PinType.Pinning, appRoot);
            PinStore.Add("Contoso.Tool", "*", "winget", PinType.Blocking, appRoot);

            var pin = Assert.Single(PinStore.List(appRoot));
            Assert.Equal("Contoso.Tool", pin.PackageId);
            Assert.Equal("winget", pin.SourceId);
            Assert.Equal(PinType.Blocking, pin.PinType);

            // Other packages and sources are untouched, and WinGet's index is kept.
            PinStore.Add("Contoso.Tool", "1.0", "msstore", PinType.Pinning, appRoot);
            PinStore.Add("Contoso.Other", "1.0", "winget", PinType.Pinning, appRoot);
            Assert.Equal(3, PinStore.List(appRoot).Count);

            using var conn = new SqliteConnection($"Data Source={SourceStoreManager.PinsDbPath(appRoot)};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'pin_index'";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── #6: source index path segments must be Windows-safe file names ──

    [Theory]
    [InlineData("manifests/c/foo./a.yaml")]
    [InlineData("manifests/c/foo /a.yaml")]
    [InlineData("manifests/CON/a.yaml")]
    [InlineData("manifests/c/com1.yaml")]
    [InlineData("manifests/c/NUL.txt")]
    [InlineData("manifests/c/lpt9")]
    [InlineData("manifests/c/a?b.yaml")]
    [InlineData("manifests/c/a#b.yaml")]
    [InlineData("manifests/c/a*b.yaml")]
    [InlineData("manifests/c/a\"b.yaml")]
    [InlineData("manifests/c/a<b.yaml")]
    [InlineData("manifests/c/a>b.yaml")]
    [InlineData("manifests/c/a|b.yaml")]
    [InlineData("manifests/c/a\u0001b.yaml")]
    [InlineData("manifests//a.yaml")]
    public void SourceRelativePaths_RejectWindowsUnsafeSegments(string path)
    {
        Assert.Throws<InvalidOperationException>(() => PreIndexedSource.ValidatedSourceRelativeSegments(path));
    }

    [Fact]
    public void SourceRelativePaths_AcceptIndexPaths()
    {
        Assert.Equal(["manifests", "c", "Contoso", "App", "1.0.0", "Contoso.App.installer.yaml"],
            PreIndexedSource.ValidatedSourceRelativeSegments("manifests/c/Contoso/App/1.0.0/Contoso.App.installer.yaml"));
        Assert.Equal(["packages", "Contoso.Console", "0123abcd", "versionData.mszyml"],
            PreIndexedSource.ValidatedSourceRelativeSegments("packages/Contoso.Console/0123abcd/versionData.mszyml"));
    }

    [Theory]
    [InlineData("Contoso.Rest", "Contoso.Rest")]
    [InlineData("Contoso.", "Contoso")]
    [InlineData("Contoso. .", "Contoso")]
    [InlineData("a?.", "a_")]
    [InlineData("a\u0001b", "a_b")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1.", "_Com1")]
    [InlineData(" lead", "_ lead")]
    [InlineData("...", "_")]
    [InlineData(" . ", "_")]
    public void SanitizePathSegment_YieldsWindowsSafeNames(string value, string expected)
    {
        Assert.Equal(expected, SourceStoreManager.SanitizePathSegment(value));
    }

    [Fact]
    public void SanitizePathSegment_FallsBackForOverlongNames()
    {
        Assert.Equal("_", SourceStoreManager.SanitizePathSegment(new string('x', 300)));
    }

    // ── #7: removed obsolete directories are not tracked after an upgrade ──

    [Fact]
    public void RetainedPortableDirectories_DropRemovedObsoleteDirectories()
    {
        var root = CreateAppRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            Directory.CreateDirectory(Path.Combine(root, "kept"));
            File.WriteAllText(Path.Combine(root, "kept", "user.txt"), "user");
            Directory.CreateDirectory(Path.Combine(root, "old"));
            File.WriteAllText(Path.Combine(root, "old", "tool.dll"), "old");

            var payload = new InstallerDispatch.PortablePayload([Path.Combine("bin", "tool.exe")], ["bin"]);
            string[] previousFiles = [Path.Combine("bin", "tool.exe"), Path.Combine("old", "tool.dll")];
            string[] previousDirectories = ["bin", "kept", "old"];
            var planned = InstallerDispatch.PlanPortablePayload(payload, root, true, previousFiles, previousDirectories);
            Assert.Equal(["bin", "kept", "old"], planned);

            InstallerDispatch.RemoveObsoletePortableFiles(root, payload, previousFiles, previousDirectories);
            Assert.False(Directory.Exists(Path.Combine(root, "old")));
            Assert.True(File.Exists(Path.Combine(root, "kept", "user.txt")));

            Assert.Equal(["bin", "kept"], InstallerDispatch.RetainedPortableDirectories(root, payload, planned));
        }
        finally
        {
            DeleteAppRoot(root);
        }
    }
}
