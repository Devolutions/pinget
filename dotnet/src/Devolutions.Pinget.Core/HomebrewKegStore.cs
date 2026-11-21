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

internal sealed record HomebrewLinkChange(string? Before, string? After);

/// <summary>
/// Durable write-ahead journal shared with the Rust runtime. Its presence means a mutation of the
/// prefix may be incomplete; <c>Prepared</c> is rolled back and <c>Committed</c> is finished on recovery.
/// </summary>
internal sealed record HomebrewTransaction(
    int SchemaVersion,
    string Operation,
    string State,
    string Formula,
    string KegVersion,
    string? Stage,
    string? Backup,
    string? Trash,
    string? ObsoleteKegVersion,
    Dictionary<string, HomebrewLinkChange> Links,
    HomebrewKegReceipt? PreviousReceipt,
    HomebrewKegReceipt? Receipt);

internal static class HomebrewKegStore
{
    private const string JournalName = "transaction.json";
    private const string Prepared = "Prepared";
    private const string Committed = "Committed";

    /// <summary>Test-only seam: <c>formula:step</c> terminates the process at that step to simulate a crash.</summary>
    private const string InterruptVariable = "PINGET_HOMEBREW_TEST_INTERRUPT";

    internal static readonly AsyncLocal<string?> InterruptAt = new();

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
        if (EntryExists(Path.Combine(root, JournalName)))
        {
            // An active operation owns the journal; otherwise recover before reporting state.
            using var operation = OperationLock.TryAcquire(root);
            if (operation is not null)
                Recover(root, []);
        }
        return ReadReceipts(root);
    }

    private static IReadOnlyList<HomebrewKegReceipt> ReadReceipts(string root)
    {
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
                if (!IsValidReceipt(receipt) || Path.GetFileName(path) != receipt.Package.Formula + ".json")
                    throw new InvalidDataException($"Unsupported or invalid Homebrew receipt: {path}");
                return receipt;
            }).ToArray();
    }

    private static bool IsValidReceipt(HomebrewKegReceipt receipt)
    {
        if (receipt?.Package is null)
            return false;
        Validate(receipt.Package);
        return receipt.SchemaVersion == 1 && receipt.Links is not null && receipt.Links.All(link => IsCommandLink(link.Key));
    }

    internal static void Install(string appRoot, string archive, HomebrewKegSpec package, List<string> warnings)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Native Homebrew bottles require Linux or macOS.");
        Validate(package);
        var root = Prepare(appRoot);
        using var operation = Lock(root, warnings);
        var receipts = ReadReceipts(root);
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
        var stageName = Unique();
        var stage = Path.Combine(root, ".staging", stageName);
        HomebrewTransaction transaction;
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
            var changes = new SortedDictionary<string, HomebrewLinkChange>(StringComparer.Ordinal);
            foreach (var relative in links.Keys.Concat(oldLinks.Keys).Distinct(StringComparer.Ordinal))
            {
                var path = HomebrewBottleArchive.ChildPath(root, relative);
                var target = new FileInfo(path).LinkTarget;
                if (EntryExists(path) && (!oldLinks.TryGetValue(relative, out var ownedTarget) || target != ownedTarget))
                    throw new IOException($"Refusing to overwrite an unowned Homebrew link: {path}");
                changes[relative] = new HomebrewLinkChange(target, links.GetValueOrDefault(relative));
            }
            string? backup = null;
            if (EntryExists(keg))
            {
                if (previous?.Package.KegVersion != package.KegVersion || new DirectoryInfo(keg).LinkTarget is not null)
                    throw new IOException($"Refusing to replace an unowned Homebrew keg: {keg}");
                backup = stageName + ".previous";
            }
            transaction = new HomebrewTransaction(1, "install", Prepared, package.Formula, package.KegVersion,
                stageName, backup, null,
                previous is not null && previous.Package.KegVersion != package.KegVersion ? previous.Package.KegVersion : null,
                new Dictionary<string, HomebrewLinkChange>(changes, StringComparer.Ordinal), previous,
                new HomebrewKegReceipt(1, package,
                    links.Where(link => IsCommandLink(link.Key)).ToDictionary(StringComparer.Ordinal)));
        }
        catch
        {
            Cleanup(stage, warnings);
            throw;
        }
        RunTransaction(root, transaction, warnings, ApplyInstall);
        if (previous is not null)
            ReportDependencyChanges(previous.Package, package, warnings);
    }

    private static void ReportDependencyChanges(HomebrewKegSpec previous, HomebrewKegSpec current, List<string> warnings)
    {
        var added = current.Dependencies.Where(dependency => !previous.Dependencies.Contains(dependency, StringComparer.Ordinal)).ToArray();
        var removed = previous.Dependencies.Where(dependency => !current.Dependencies.Contains(dependency, StringComparer.Ordinal)).ToArray();
        if (added.Length > 0)
            warnings.Add($"{current.PackageIdentifier} now depends on: {string.Join(", ", added)}");
        if (removed.Length > 0)
            warnings.Add($"{current.PackageIdentifier} no longer depends on: {string.Join(", ", removed)}; those packages remain installed");
    }

    private static void ApplyInstall(string root, HomebrewTransaction transaction)
    {
        var staging = Path.Combine(root, ".staging");
        var formulaDirectory = Path.Combine(root, "Cellar", transaction.Formula);
        var keg = Path.Combine(formulaDirectory, transaction.KegVersion);
        if (transaction.Backup is not null)
        {
            Directory.Move(keg, Path.Combine(staging, transaction.Backup));
            SyncDirectory(formulaDirectory);
            SyncDirectory(staging);
            Checkpoint(transaction.Formula, "install-backup");
        }
        Directory.Move(Path.Combine(staging, transaction.Stage!), keg);
        SyncDirectory(formulaDirectory);
        SyncDirectory(staging);
        Checkpoint(transaction.Formula, "install-keg");
        ApplyLinks(root, transaction, "install-link");
        SaveReceipt(root, transaction.Receipt!);
        Checkpoint(transaction.Formula, "install-receipt");
    }

    private static void ApplyUninstall(string root, HomebrewTransaction transaction)
    {
        var staging = Path.Combine(root, ".staging");
        var formulaDirectory = Path.Combine(root, "Cellar", transaction.Formula);
        Directory.Move(Path.Combine(formulaDirectory, transaction.KegVersion), Path.Combine(staging, transaction.Trash!));
        SyncDirectory(formulaDirectory);
        SyncDirectory(staging);
        Checkpoint(transaction.Formula, "uninstall-trash");
        ApplyLinks(root, transaction, "uninstall-link");
        File.Delete(Path.Combine(root, "receipts", transaction.Formula + ".json"));
        SyncDirectory(Path.Combine(root, "receipts"));
        Checkpoint(transaction.Formula, "uninstall-receipt");
    }

    private static void ApplyLinks(string root, HomebrewTransaction transaction, string step)
    {
        var first = true;
        foreach (var (relative, change) in transaction.Links.OrderBy(link => link.Key, StringComparer.Ordinal))
        {
            var path = HomebrewBottleArchive.ChildPath(root, relative);
            if (change.After is null)
                File.Delete(path);
            else
                SetLink(root, path, change.After);
            if (first)
                Checkpoint(transaction.Formula, step);
            first = false;
        }
        foreach (var directory in new[] { "opt", "bin", "sbin" })
            SyncDirectory(Path.Combine(root, directory));
    }

    private static void RunTransaction(string root, HomebrewTransaction transaction, List<string> warnings,
        Action<string, HomebrewTransaction> apply)
    {
        WriteJournal(root, transaction);
        try
        {
            Checkpoint(transaction.Formula, transaction.Operation + "-journal");
            apply(root, transaction);
            WriteJournal(root, transaction with { State = Committed });
        }
        catch (HomebrewInterruptedException)
        {
            throw;
        }
        catch (Exception failure)
        {
            try
            {
                Rollback(root, transaction);
            }
            catch (Exception rollback) when (rollback is not HomebrewInterruptedException)
            {
                throw new AggregateException(
                    $"Homebrew {transaction.Operation} of {transaction.Formula} failed; rollback requires attention and the recovery journal was kept at {Path.Combine(root, JournalName)}.",
                    failure, rollback);
            }
            throw;
        }
        transaction = transaction with { State = Committed };
        Checkpoint(transaction.Formula, transaction.Operation + "-commit");
        Finish(root, transaction, warnings);
    }

    private static void Recover(string root, List<string> warnings)
    {
        var path = Path.Combine(root, JournalName);
        if (!EntryExists(path))
            return;
        var unrecoverable = $"Unrecoverable Homebrew transaction journal; inspect {path} manually.";
        if (new FileInfo(path).LinkTarget is not null || !File.Exists(path))
            throw new InvalidDataException(unrecoverable);
        HomebrewTransaction transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<HomebrewTransaction>(File.ReadAllText(path))
                ?? throw new InvalidDataException(unrecoverable);
            ValidateTransaction(transaction);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException(unrecoverable, exception);
        }
        if (transaction.State == Prepared)
        {
            try
            {
                Rollback(root, transaction);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new IOException(
                    $"Could not roll back the interrupted Homebrew {transaction.Operation} of {transaction.Formula}; the journal was kept at {path} for manual recovery.",
                    exception);
            }
            warnings.Add($"Rolled back an interrupted Homebrew {transaction.Operation} of {transaction.Formula}");
        }
        else
        {
            Finish(root, transaction, warnings);
            warnings.Add($"Completed an interrupted Homebrew {transaction.Operation} of {transaction.Formula}");
        }
    }

    private static void ValidateTransaction(HomebrewTransaction transaction)
    {
        ValidateComponent(transaction.Formula);
        ValidateComponent(transaction.KegVersion);
        foreach (var name in new[] { transaction.Stage, transaction.Backup, transaction.Trash, transaction.ObsoleteKegVersion })
        {
            if (name is not null)
                ValidateComponent(name);
        }
        var install = transaction.Operation == "install";
        if (transaction.SchemaVersion != 1 || transaction.State is not (Prepared or Committed) || transaction.Links is null ||
            (install && (transaction.Stage is null || transaction.Receipt is null)) ||
            (!install && (transaction.Operation != "uninstall" || transaction.Trash is null || transaction.PreviousReceipt is null)))
            throw new InvalidDataException("Unsupported Homebrew transaction.");
        foreach (var receipt in new[] { transaction.PreviousReceipt, transaction.Receipt })
        {
            if (receipt is not null && (!IsValidReceipt(receipt) || receipt.Package.Formula != transaction.Formula))
                throw new InvalidDataException("Homebrew transaction receipt does not match its formula.");
        }
        if (transaction.Links.Any(link => link.Value is null ||
                                          (link.Key != "opt/" + transaction.Formula && !IsCommandLink(link.Key))))
            throw new InvalidDataException("Homebrew transaction contains an unsupported link.");
    }

    /// <summary>Restores the pre-transaction state. Every step is idempotent so recovery can itself be interrupted.</summary>
    private static void Rollback(string root, HomebrewTransaction transaction)
    {
        var staging = Path.Combine(root, ".staging");
        foreach (var (relative, change) in transaction.Links.OrderBy(link => link.Key, StringComparer.Ordinal))
        {
            var path = HomebrewBottleArchive.ChildPath(root, relative);
            if (MatchesEntry(path, change.Before))
                continue;
            if (!MatchesEntry(path, change.After))
                throw new IOException($"Homebrew link was changed outside Pinget during an interrupted operation: {path}");
            if (change.Before is null)
                File.Delete(path);
            else
                SetLink(root, path, change.Before);
        }
        foreach (var directory in new[] { "opt", "bin", "sbin" })
            SyncDirectory(Path.Combine(root, directory));
        Checkpoint(transaction.Formula, "rollback-links");
        var formulaDirectory = Path.Combine(root, "Cellar", transaction.Formula);
        var keg = Path.Combine(formulaDirectory, transaction.KegVersion);
        if (transaction.Operation == "install")
        {
            if (transaction.Backup is null)
            {
                RemoveOwnedDirectory(keg);
            }
            else
            {
                var backup = Path.Combine(staging, transaction.Backup);
                if (EntryExists(backup))
                {
                    RemoveOwnedDirectory(keg);
                    Directory.Move(backup, keg);
                }
            }
        }
        else
        {
            var trash = Path.Combine(staging, transaction.Trash!);
            if (EntryExists(trash))
            {
                if (EntryExists(keg))
                    throw new IOException($"A Homebrew keg reappeared during an interrupted uninstall: {keg}");
                Directory.Move(trash, keg);
            }
        }
        if (Directory.Exists(formulaDirectory))
        {
            if (transaction.PreviousReceipt is null && !Directory.EnumerateFileSystemEntries(formulaDirectory).Any())
            {
                Directory.Delete(formulaDirectory);
                SyncDirectory(Path.Combine(root, "Cellar"));
            }
            else
            {
                SyncDirectory(formulaDirectory);
            }
        }
        if (transaction.PreviousReceipt is null)
        {
            File.Delete(Path.Combine(root, "receipts", transaction.Formula + ".json"));
            SyncDirectory(Path.Combine(root, "receipts"));
        }
        else
        {
            SaveReceipt(root, transaction.PreviousReceipt);
        }
        if (transaction.Stage is not null)
            RemoveTree(Path.Combine(staging, transaction.Stage));
        RemoveJournal(root);
    }

    /// <summary>Completes cleanup for a committed transaction; leftovers only produce warnings.</summary>
    private static void Finish(string root, HomebrewTransaction transaction, List<string> warnings)
    {
        var staging = Path.Combine(root, ".staging");
        foreach (var name in new[] { transaction.Stage, transaction.Backup, transaction.Trash })
        {
            if (name is not null)
                Cleanup(Path.Combine(staging, name), warnings);
        }
        if (transaction.ObsoleteKegVersion is not null && transaction.ObsoleteKegVersion != transaction.KegVersion)
            Cleanup(Path.Combine(root, "Cellar", transaction.Formula, transaction.ObsoleteKegVersion), warnings);
        RemoveJournal(root);
    }

    private static bool EntryExists(string path) =>
        new FileInfo(path).LinkTarget is not null || File.Exists(path) || Directory.Exists(path);

    private static bool MatchesEntry(string path, string? linkTarget) =>
        linkTarget is null ? !EntryExists(path) : new FileInfo(path).LinkTarget == linkTarget;

    private static void RemoveOwnedDirectory(string path)
    {
        if (!EntryExists(path))
            return;
        if (new DirectoryInfo(path).LinkTarget is not null || !Directory.Exists(path))
            throw new IOException($"Homebrew keg was replaced outside Pinget during an interrupted operation: {path}");
        Directory.Delete(path, recursive: true);
    }

    private static void RemoveTree(string path)
    {
        if (new FileInfo(path).LinkTarget is null && Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
    }

    internal static void Uninstall(string appRoot, string packageId, bool force, List<string> warnings)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Native Homebrew bottles require Linux or macOS.");
        var root = Prepare(appRoot);
        using var operation = Lock(root, warnings);
        var receipts = ReadReceipts(root);
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
        var transaction = new HomebrewTransaction(1, "uninstall", Prepared, package.Formula, package.KegVersion,
            null, null, Unique() + ".uninstalled", null,
            links.ToDictionary(link => link.Key, link => new HomebrewLinkChange(link.Value, null), StringComparer.Ordinal),
            receipt, null);
        RunTransaction(root, transaction, warnings, ApplyUninstall);
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

    private static void SetLink(string root, string path, string target)
    {
        // Stage the link privately so an interruption never leaves temporary entries in bin/opt.
        var temporary = Path.Combine(root, ".staging", Unique() + ".link");
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

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDescriptor([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FileSync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void ExitImmediately(int status);

    private static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var descriptor = OpenDescriptor(path, 0);
        if (descriptor < 0)
            throw new IOException($"Could not open Homebrew directory for synchronization: {path}",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            if (FileSync(descriptor) != 0)
                throw new IOException($"Could not synchronize Homebrew directory: {path}",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally
        {
            CloseDescriptor(descriptor);
        }
    }

    private static void WriteDurable(string root, string destination, Action<Stream> write)
    {
        var temporary = Path.Combine(root, ".staging", Unique() + ".json");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            SyncDirectory(Path.GetDirectoryName(destination)!);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void SaveReceipt(string root, HomebrewKegReceipt receipt) =>
        WriteDurable(root, Path.Combine(root, "receipts", receipt.Package.Formula + ".json"),
            stream => JsonSerializer.Serialize(stream, receipt));

    private static void WriteJournal(string root, HomebrewTransaction transaction) =>
        WriteDurable(root, Path.Combine(root, JournalName), stream => JsonSerializer.Serialize(stream, transaction));

    private static void RemoveJournal(string root)
    {
        File.Delete(Path.Combine(root, JournalName));
        SyncDirectory(root);
    }

    private static void Cleanup(string path, List<string> warnings)
    {
        try
        {
            RemoveTree(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not remove obsolete Homebrew staging data '{path}': {exception.Message}");
        }
    }

    private static void Checkpoint(string formula, string step)
    {
        var point = formula + ":" + step;
        if (InterruptAt.Value == point)
            throw new HomebrewInterruptedException(point);
        if (Environment.GetEnvironmentVariable(InterruptVariable) == point)
            ExitImmediately(86);
    }

    private static string Unique() => $"{Environment.ProcessId}-{Guid.NewGuid():N}";

    private static OperationLock Lock(string root, List<string> warnings)
    {
        var operation = OperationLock.TryAcquire(root)
            ?? throw new IOException("Another Pinget Homebrew operation is in progress for this prefix.");
        try
        {
            Recover(root, warnings);
            // Without a journal, anything left in the private staging area is from an interrupted
            // extraction and is owned by Pinget.
            foreach (var entry in Directory.EnumerateFileSystemEntries(Path.Combine(root, ".staging")))
                Cleanup(entry, warnings);
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Advisory operation lock shared with the Rust runtime (<c>flock</c> on Unix). The OS releases it
    /// when the process exits, so an interrupted operation never leaves a stale lock.
    /// </summary>
    private sealed class OperationLock : IDisposable
    {
        private readonly FileStream _stream;

        private OperationLock(FileStream stream) => _stream = stream;

        internal static OperationLock? TryAcquire(string root)
        {
            try
            {
                return new OperationLock(new FileStream(Path.Combine(root, "operations.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException exception) when (exception is not FileNotFoundException and not DirectoryNotFoundException)
            {
                return null;
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}

internal sealed class HomebrewInterruptedException(string point) : Exception($"Simulated interruption at {point}.");
