using System.Runtime.InteropServices;
using System.Text.Json;

namespace Devolutions.Pinget.Core;

internal sealed record HomebrewKegSpec(
    string PackageIdentifier,
    string PackageVersion,
    string Name,
    string Formula,
    string KegVersion,
    string OriginalPrefix,
    bool KegOnly,
    string SourceIdentifier,
    string SourceName,
    IReadOnlyList<string> Dependencies);

internal sealed record HomebrewKegReceipt(
    int SchemaVersion,
    HomebrewKegSpec Package,
    Dictionary<string, string> Links);

internal static class HomebrewKegStore
{
    internal static string Prefix(string appRoot) => Path.GetFullPath(Path.Combine(appRoot, "homebrew"));

    internal static bool InstallerMatchesHost(Installer installer)
    {
        if (installer.InstallerType != "homebrew-bottle")
            return true;
        var platform = OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : null;
        return platform is not null && installer.Architecture == Repository.CurrentArchitecture() &&
               installer.Platforms.Contains(platform, StringComparer.Ordinal);
    }

    internal static HomebrewKegSpec Spec(Manifest manifest, Installer installer, string sourceIdentifier, string sourceName)
    {
        if (!InstallerMatchesHost(installer))
            throw new InvalidOperationException("Homebrew bottle does not match the native platform and architecture.");
        var formula = manifest.Homebrew ?? throw new InvalidDataException("Missing Homebrew metadata.");
        var bottle = formula.Bottles.Single(bottle =>
            bottle.Url == installer.Url && bottle.Sha256.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase));
        if (bottle.Relocation is not ("any" or "any_skip_relocation") || formula.VersionScheme != 0)
            throw new InvalidDataException("Native bottles currently require relocatable archives and version scheme 0.");
        string originalPrefix;
        if (bottle.TargetOS == "Linux")
        {
            if (bottle.Loader != "glibc")
                throw new InvalidDataException("Native Linux bottles require an explicit glibc loader policy.");
            var actual = HomebrewBottleRelocation.Run("getconf", "GNU_LIBC_VERSION").Trim();
            if (!actual.StartsWith("glibc ", StringComparison.Ordinal))
                throw new PlatformNotSupportedException("Host is not glibc Linux.");
            CheckVersion(actual[6..], bottle.MinimumGlibc ?? throw new InvalidDataException("Missing MinimumGlibc."), maximum: false);
            originalPrefix = "/home/linuxbrew/.linuxbrew";
        }
        else if (bottle.TargetOS == "macOS")
        {
            var actual = HomebrewBottleRelocation.Run("/usr/bin/sw_vers", "-productVersion").Trim();
            CheckVersion(actual, bottle.MinimumOSVersion ?? throw new InvalidDataException("Missing MinimumOSVersion."), maximum: false);
            if (bottle.MaximumOSVersion is not null)
                CheckVersion(actual, bottle.MaximumOSVersion, maximum: true);
            originalPrefix = bottle.Architecture == "arm64" ? "/opt/homebrew" : "/usr/local";
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported native bottle platform.");
        }
        var kegVersion = formula.Revision == 0 ? formula.UpstreamVersion : $"{formula.UpstreamVersion}_{formula.Revision}";
        var spec = new HomebrewKegSpec(manifest.Id, manifest.Version, manifest.Name, formula.Formula, kegVersion, originalPrefix,
            formula.KegOnly, sourceIdentifier, sourceName,
            Repository.SelectedInstallerDependencies(manifest, installer).Select(dependency => dependency.PackageIdentifier).ToArray());
        Validate(spec);
        return spec;
    }

    private static void CheckVersion(string actual, string boundary, bool maximum)
    {
        if (!Version.TryParse(actual, out var actualVersion) || !Version.TryParse(boundary, out var boundaryVersion))
            throw new InvalidDataException($"Invalid host/bottle ABI version: {actual} / {boundary}.");
        if ((!maximum && actualVersion < boundaryVersion) || (maximum && actualVersion > boundaryVersion))
            throw new PlatformNotSupportedException($"Host version {actual} does not satisfy bottle {(maximum ? "maximum" : "minimum")} version {boundary}.");
    }

    internal static List<InstalledPackage> InstalledPackages(string appRoot, string? scope)
    {
        if (scope is not null && scope != "user")
            return [];
        return ReadAll(appRoot).Select(receipt => new InstalledPackage
        {
            Name = receipt.Package.Name,
            LocalId = receipt.Package.PackageIdentifier,
            InstalledVersion = receipt.Package.PackageVersion,
            Scope = "user",
            InstallerCategory = "homebrew-bottle",
            InstallLocation = Path.Combine(Prefix(appRoot), "Cellar", receipt.Package.Formula, receipt.Package.KegVersion),
            WinGetPackageIdentifier = receipt.Package.PackageIdentifier,
            WinGetSourceIdentifier = receipt.Package.SourceIdentifier,
            InstalledVersionCanonical = true,
        }).ToList();
    }

    internal static IReadOnlyList<HomebrewKegReceipt> ReadAll(string appRoot)
    {
        var root = Prefix(appRoot);
        if (new DirectoryInfo(root).LinkTarget is not null)
            throw new IOException($"Homebrew storage must not be a symbolic link: {root}");
        if (!Directory.Exists(root))
            return [];
        CheckDirectory(root);
        var directory = Path.Combine(root, "receipts");
        if (new DirectoryInfo(directory).LinkTarget is not null)
            throw new IOException($"Homebrew receipts must not be a symbolic link: {directory}");
        if (!Directory.Exists(directory))
            return [];
        CheckDirectory(directory);
        return Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)
            .Select(path =>
            {
                if (new FileInfo(path).LinkTarget is not null)
                    throw new IOException($"Homebrew receipt must not be a symbolic link: {path}");
                var receipt = JsonSerializer.Deserialize<HomebrewKegReceipt>(File.ReadAllText(path))
                    ?? throw new InvalidDataException($"Invalid Homebrew receipt: {path}");
                Validate(receipt.Package);
                if (receipt.SchemaVersion != 1 || Path.GetFileName(path) != receipt.Package.Formula + ".json" ||
                    receipt.Links is null || receipt.Links.Any(link => !IsCommandLink(link.Key)))
                    throw new InvalidDataException($"Unsupported or invalid Homebrew receipt: {path}");
                return receipt;
            }).ToArray();
    }

    internal static void Install(string appRoot, string archive, HomebrewKegSpec package, List<string> warnings)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Native Homebrew bottles require Linux or macOS.");
        Validate(package);
        var root = Prepare(appRoot);
        using var operation = new OperationLock(root);
        var receipts = ReadAll(appRoot);
        var previous = receipts.SingleOrDefault(receipt => receipt.Package.Formula == package.Formula);
        if (previous is not null && (previous.Package.PackageIdentifier != package.PackageIdentifier ||
                                    previous.Package.SourceIdentifier != package.SourceIdentifier))
            throw new InvalidOperationException($"Formula {package.Formula} is already owned by another package or source.");
        foreach (var dependency in package.Dependencies)
        {
            if (!receipts.Any(receipt => receipt.Package.PackageIdentifier == dependency))
                throw new InvalidOperationException($"Required bottle dependency is not installed: {dependency}");
        }
        var formulaDirectory = Path.Combine(root, "Cellar", package.Formula);
        Directory.CreateDirectory(formulaDirectory);
        CheckDirectory(formulaDirectory);
        var keg = Path.Combine(formulaDirectory, package.KegVersion);
        var stage = Path.Combine(root, ".staging", Guid.NewGuid().ToString("N"));
        var backup = stage + ".previous";
        var linksBefore = new Dictionary<string, string?>(StringComparer.Ordinal);
        var movedKeg = false;
        var committed = false;
        try
        {
            var files = HomebrewBottleArchive.Extract(archive, stage, package.Formula, package.KegVersion);
            HomebrewBottleRelocation.Relocate(files, root, package.OriginalPrefix);
            var links = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["opt/" + package.Formula] = $"../Cellar/{package.Formula}/{package.KegVersion}",
            };
            if (!package.KegOnly)
            {
                foreach (var directory in new[] { "bin", "sbin" })
                {
                    var source = Path.Combine(stage, directory);
                    if (!Directory.Exists(source))
                        continue;
                    foreach (var file in Directory.EnumerateFileSystemEntries(source))
                    {
                        if (Directory.Exists(file))
                            throw new InvalidDataException($"Bottle command directories are unsupported: {file}");
                        var name = Path.GetFileName(file);
                        ValidateComponent(name);
                        links.Add(directory + "/" + name, $"../opt/{package.Formula}/{directory}/{name}");
                    }
                }
            }
            var oldLinks = previous is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(previous.Links, StringComparer.Ordinal);
            if (previous is not null)
                oldLinks["opt/" + package.Formula] = $"../Cellar/{package.Formula}/{previous.Package.KegVersion}";
            foreach (var relative in links.Keys.Concat(oldLinks.Keys).Distinct(StringComparer.Ordinal))
            {
                var path = HomebrewBottleArchive.ChildPath(root, relative);
                var target = new FileInfo(path).LinkTarget;
                var exists = target is not null || File.Exists(path) || Directory.Exists(path);
                if (exists && (!oldLinks.TryGetValue(relative, out var ownedTarget) || target != ownedTarget))
                    throw new IOException($"Refusing to overwrite an unowned Homebrew link: {path}");
                linksBefore[relative] = target;
            }
            if (Directory.Exists(keg) || File.Exists(keg))
            {
                if (previous?.Package.KegVersion != package.KegVersion || new DirectoryInfo(keg).LinkTarget is not null)
                    throw new IOException($"Refusing to replace an unowned Homebrew keg: {keg}");
                Directory.Move(keg, backup);
            }
            Directory.Move(stage, keg);
            movedKeg = true;
            foreach (var (relative, _) in linksBefore)
            {
                var path = HomebrewBottleArchive.ChildPath(root, relative);
                if (links.TryGetValue(relative, out var target))
                    SetLink(path, target);
                else
                    File.Delete(path);
            }
            var receipt = new HomebrewKegReceipt(1, package,
                links.Where(link => IsCommandLink(link.Key)).ToDictionary(StringComparer.Ordinal));
            SaveReceipt(root, receipt);
            committed = true;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var (relative, target) in linksBefore)
            {
                try
                {
                    var path = HomebrewBottleArchive.ChildPath(root, relative);
                    if (target is null)
                        File.Delete(path);
                    else
                        SetLink(path, target);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    errors.Add(rollback);
                }
            }
            try
            {
                if (movedKeg)
                    Directory.Delete(keg, recursive: true);
                if (Directory.Exists(backup))
                    Directory.Move(backup, keg);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                errors.Add(rollback);
            }
            if (errors.Count > 1)
                throw new AggregateException("Bottle installation failed and rollback requires attention.", errors);
            throw;
        }
        finally
        {
            Cleanup(stage, warnings);
            if (committed)
            {
                Cleanup(backup, warnings);
                if (previous is not null && previous.Package.KegVersion != package.KegVersion)
                    Cleanup(Path.Combine(formulaDirectory, previous.Package.KegVersion), warnings);
            }
        }
    }

    internal static void Uninstall(string appRoot, string packageId, bool force, List<string> warnings)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Native Homebrew bottles require Linux or macOS.");
        var root = Prepare(appRoot);
        using var operation = new OperationLock(root);
        var receipts = ReadAll(appRoot);
        var receipt = receipts.SingleOrDefault(item => item.Package.PackageIdentifier == packageId)
            ?? throw new InvalidOperationException($"No native bottle is installed for {packageId}.");
        var dependent = receipts.FirstOrDefault(item => item.Package.PackageIdentifier != packageId &&
            item.Package.Dependencies.Contains(packageId, StringComparer.Ordinal));
        if (!force && dependent is not null)
            throw new InvalidOperationException($"Cannot uninstall {packageId}; {dependent.Package.PackageIdentifier} depends on it.");
        var package = receipt.Package;
        var links = new Dictionary<string, string>(receipt.Links, StringComparer.Ordinal)
        {
            ["opt/" + package.Formula] = $"../Cellar/{package.Formula}/{package.KegVersion}",
        };
        foreach (var (relative, target) in links)
        {
            var path = HomebrewBottleArchive.ChildPath(root, relative);
            if (new FileInfo(path).LinkTarget != target)
                throw new IOException($"Homebrew link was modified; refusing to remove it: {path}");
        }
        var keg = Path.Combine(root, "Cellar", package.Formula, package.KegVersion);
        CheckDirectory(Path.GetDirectoryName(keg)!);
        CheckDirectory(keg);
        var trash = Path.Combine(root, ".staging", Guid.NewGuid().ToString("N") + ".uninstalled");
        Directory.Move(keg, trash);
        try
        {
            foreach (var relative in links.Keys)
                File.Delete(HomebrewBottleArchive.ChildPath(root, relative));
            File.Delete(Path.Combine(root, "receipts", package.Formula + ".json"));
        }
        catch
        {
            Directory.Move(trash, keg);
            foreach (var (relative, target) in links)
                SetLink(HomebrewBottleArchive.ChildPath(root, relative), target);
            throw;
        }
        Cleanup(trash, warnings);
    }

    private static void Validate(HomebrewKegSpec package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateComponent(package.Formula);
        ValidateComponent(package.KegVersion);
        if (string.IsNullOrWhiteSpace(package.PackageIdentifier) || string.IsNullOrWhiteSpace(package.PackageVersion) ||
            string.IsNullOrWhiteSpace(package.Name) || string.IsNullOrWhiteSpace(package.SourceIdentifier) ||
            string.IsNullOrWhiteSpace(package.SourceName) ||
            string.IsNullOrWhiteSpace(package.OriginalPrefix) || !package.OriginalPrefix.StartsWith('/') ||
            package.Dependencies is null || package.Dependencies.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Invalid native Homebrew package identity or relocation prefix.");
    }

    private static void ValidateComponent(string value)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '+' or '@')))
            throw new InvalidDataException($"Unsafe Homebrew path component: {value}");
    }

    private static bool IsCommandLink(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || parts[0] is not ("bin" or "sbin"))
            return false;
        ValidateComponent(parts[1]);
        return true;
    }

    private static string Prepare(string appRoot)
    {
        var root = Prefix(appRoot);
        Directory.CreateDirectory(root);
        CheckDirectory(root);
        foreach (var child in new[] { "Cellar", "opt", "bin", "sbin", "receipts", ".staging" })
        {
            var directory = Path.Combine(root, child);
            Directory.CreateDirectory(directory);
            CheckDirectory(directory);
        }
        return root;
    }

    private static void CheckDirectory(string path)
    {
        if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException($"Homebrew storage must be an existing real directory: {path}");
    }

    private static void SetLink(string path, string target)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.CreateSymbolicLink(temporary, target);
            if (Rename(temporary, path) != 0)
                throw new IOException($"Could not atomically update Homebrew link: {path}",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    private static void SaveReceipt(string root, HomebrewKegReceipt receipt)
    {
        var path = Path.Combine(root, "receipts", receipt.Package.Formula + ".json");
        var temporary = Path.Combine(root, ".staging", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, receipt);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void Cleanup(string path, List<string> warnings)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not remove obsolete Homebrew staging data '{path}': {exception.Message}");
        }
    }

    private sealed class OperationLock : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _stream;

        internal OperationLock(string root)
        {
            _path = Path.Combine(root, "operations.lock");
            _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        public void Dispose()
        {
            _stream.Dispose();
            File.Delete(_path);
        }
    }
}
