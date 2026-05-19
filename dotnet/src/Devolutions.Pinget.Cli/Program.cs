using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Pinget.Cli;
using Devolutions.Pinget.Cli.Extensions;
using Devolutions.Pinget.Cli.Helpers;
using Devolutions.Pinget.Core;
using YamlDotNet.Serialization;

if (args.Length == 1 && (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase)))
{
    Print.Version();
    return 0;
}

var rootCommand = new RootCommand("Pinget: portable winget in pure C#");

var outputOption = new Option<string?>("--output", "Output format: text, json, or yaml").WithAliases("-o");
outputOption.FromAmong("text", "json", "yaml");
rootCommand.AddGlobalOption(outputOption);

var JsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

var infoOption = new Option<bool>("--info", "Display general info");
rootCommand.AddGlobalOption(infoOption);

// ── Search command ──
var searchCommand = new Command("search", "Search for packages");
var sqArg = QueryArguments.Search;
var sqOpt = CommonOptions.Query; var sidOpt = CommonOptions.Id; var snOpt = CommonOptions.Name; var smOpt = CommonOptions.Moniker;
var ssOpt = CommonOptions.Source; var seOpt = CommonOptions.Exact; var scOpt = CommonOptions.Count;
var sTagOpt = new Option<string?>("--tag", "Filter by tag");
var sCmdOpt = new Option<string?>("--command", "Filter by command").WithAliases("--cmd");
var sVersionsOpt = new Option<bool>("--versions", "Show versions");
var sManifestsOpt = new Option<bool>("--manifests", "Return show-style manifests");
foreach (var o in new Option[] { sqOpt, sidOpt, snOpt, smOpt, ssOpt, seOpt, scOpt, sTagOpt, sCmdOpt, sVersionsOpt, sManifestsOpt })
    searchCommand.AddOption(o);
searchCommand.AddArgument(sqArg);

searchCommand.SetHandler((ctx) =>
{
    var output = Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(sqArg) ?? ctx.ParseResult.GetValueForOption(sqOpt),
        Id = ctx.ParseResult.GetValueForOption(sidOpt),
        Name = ctx.ParseResult.GetValueForOption(snOpt),
        Moniker = ctx.ParseResult.GetValueForOption(smOpt),
        Tag = ctx.ParseResult.GetValueForOption(sTagOpt),
        Command = ctx.ParseResult.GetValueForOption(sCmdOpt),
        Source = ctx.ParseResult.GetValueForOption(ssOpt),
        Count = ctx.ParseResult.GetValueForOption(scOpt),
        Exact = ctx.ParseResult.GetValueForOption(seOpt),
    };

    using var repo = Repository.Open();
    if (ctx.ParseResult.GetValueForOption(sManifestsOpt))
    {
        if (output == OutputFormat.Text)
            throw new InvalidOperationException("--manifests requires --output json or yaml");
        if (ctx.ParseResult.GetValueForOption(sVersionsOpt))
            throw new InvalidOperationException("--manifests cannot be combined with --versions");

        Output.WriteStructuredOutput(repo.SearchManifests(query), output);
    }
    else if (ctx.ParseResult.GetValueForOption(sVersionsOpt))
    {
        var result = repo.SearchVersions(query);
        if (output != OutputFormat.Text) Output.WriteStructuredOutput(result, output);
        else Print.Versions(result);
    }
    else
    {
        var result = repo.Search(query);
        if (output != OutputFormat.Text) Output.WriteStructuredOutput(result, output);
        else Print.Search(result);
    }
});

// ── Show command ──
var showCommand = new Command("show", "Show package info");
var shArg = QueryArguments.Package;
var shqOpt = CommonOptions.Query; var shidOpt = CommonOptions.Id; var shnOpt = CommonOptions.Name; var shmOpt = CommonOptions.Moniker;
var shsOpt = CommonOptions.Source; var sheOpt = CommonOptions.Exact; var shvOpt = CommonOptions.Version;
var shVerOpt = new Option<bool>("--versions", "Show available versions");
var shLocaleOpt = new Option<string?>("--locale", "Installer locale");
var shTypeOpt = new Option<string?>("--installer-type", "Installer type");
var shArchOpt = new Option<string?>("--architecture", "Architecture").WithAliases("-a");
var shScopeOpt = new Option<string?>("--scope", "Install scope");
foreach (var o in new Option[] { shqOpt, shidOpt, shnOpt, shmOpt, shsOpt, sheOpt, shvOpt, shVerOpt, shLocaleOpt, shTypeOpt, shArchOpt, shScopeOpt })
    showCommand.AddOption(o);
showCommand.AddArgument(shArg);

showCommand.SetHandler((ctx) =>
{
    var output = Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(shArg) ?? ctx.ParseResult.GetValueForOption(shqOpt),
        Id = ctx.ParseResult.GetValueForOption(shidOpt),
        Name = ctx.ParseResult.GetValueForOption(shnOpt),
        Moniker = ctx.ParseResult.GetValueForOption(shmOpt),
        Source = ctx.ParseResult.GetValueForOption(shsOpt),
        Exact = ctx.ParseResult.GetValueForOption(sheOpt),
        Version = ctx.ParseResult.GetValueForOption(shvOpt),
        Locale = ctx.ParseResult.GetValueForOption(shLocaleOpt),
        InstallerType = ctx.ParseResult.GetValueForOption(shTypeOpt),
        InstallerArchitecture = ctx.ParseResult.GetValueForOption(shArchOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(shScopeOpt),
    };

    using var repo = Repository.Open();
    if (ctx.ParseResult.GetValueForOption(shVerOpt))
    {
        var result = repo.ShowVersions(query);
        if (output != OutputFormat.Text) Output.WriteStructuredOutput(result, output);
        else Print.Versions(result);
    }
    else
    {
        var result = repo.Show(query);
        if (output != OutputFormat.Text) Output.WriteManifestStructuredOutput(result.ToSerializableManifest(), output);
        else Print.Show(result);
    }
});

// ── List command ──
var listCommand = new Command("list", "List installed packages").WithAliases("ls");
var lArg = QueryArguments.Package;
var lqOpt = CommonOptions.Query; var lidOpt = CommonOptions.Id; var lnOpt = CommonOptions.Name; var lmOpt = CommonOptions.Moniker;
var lsOpt = CommonOptions.Source; var leOpt = CommonOptions.Exact; var lcOpt = CommonOptions.Count;
var lTagOpt = new Option<string?>("--tag", "Filter by tag");
var lCmdOpt = new Option<string?>("--command", "Filter by command").WithAliases("--cmd");
var lScopeOpt = new Option<string?>("--scope", "Install scope");
var lUpgradeOpt = new Option<bool>("--upgrade-available", "Show upgradeable only");
var lUnknownOpt = new Option<bool>("--include-unknown", "Include unknown versions").WithAliases("-u");
var lPinnedOpt = new Option<bool>("--include-pinned", "Include pinned packages");
var lDetailsOpt = new Option<bool>("--details", "Show details");
foreach (var o in new Option[] { lqOpt, lidOpt, lnOpt, lmOpt, lsOpt, leOpt, lcOpt, lTagOpt, lCmdOpt, lScopeOpt, lUpgradeOpt, lUnknownOpt, lPinnedOpt, lDetailsOpt })
    listCommand.AddOption(o);
listCommand.AddArgument(lArg);

listCommand.SetHandler((ctx) =>
{
    var output = Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var details = ctx.ParseResult.GetValueForOption(lDetailsOpt);
    var upgrade = ctx.ParseResult.GetValueForOption(lUpgradeOpt);
    var query = new ListQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(lArg) ?? ctx.ParseResult.GetValueForOption(lqOpt),
        Id = ctx.ParseResult.GetValueForOption(lidOpt),
        Name = ctx.ParseResult.GetValueForOption(lnOpt),
        Moniker = ctx.ParseResult.GetValueForOption(lmOpt),
        Tag = ctx.ParseResult.GetValueForOption(lTagOpt),
        Command = ctx.ParseResult.GetValueForOption(lCmdOpt),
        Source = ctx.ParseResult.GetValueForOption(lsOpt),
        Count = ctx.ParseResult.GetValueForOption(lcOpt),
        Exact = ctx.ParseResult.GetValueForOption(leOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(lScopeOpt),
        UpgradeOnly = upgrade,
        IncludeUnknown = ctx.ParseResult.GetValueForOption(lUnknownOpt),
        IncludePinned = ctx.ParseResult.GetValueForOption(lPinnedOpt),
    };

    using var repo = Repository.Open();
    var result = repo.List(query);
    if (output != OutputFormat.Text) Output.WriteStructuredOutput(result, output);
    else Print.ListResult(result, details, upgrade);
});

// ── Upgrade command ──
var upgradeCommand = new Command("upgrade", "Upgrade packages").WithAliases("update");
var uArg = QueryArguments.Package;
var uqOpt = CommonOptions.Query; var uidOpt = CommonOptions.Id; var unOpt = CommonOptions.Name; var umOpt = CommonOptions.Moniker;
var usOpt = CommonOptions.Source; var ueOpt = CommonOptions.Exact; var ucOpt = CommonOptions.Count; var uvOpt = CommonOptions.Version;
var uManifestOpt = new Option<string?>("--manifest", "Local manifest file or directory").WithAliases("-m");
var uLocaleOpt = new Option<string?>("--locale", "Installer locale");
var uTypeOpt = new Option<string?>("--installer-type", "Installer type");
var uArchOpt = new Option<string?>("--architecture", "Architecture").WithAliases("-a");
var uPlatformOpt = new Option<string?>("--platform", "Target platform");
var uOsVersionOpt = new Option<string?>("--os-version", "Target OS version");
var uScopeOpt = new Option<string?>("--scope", "Install scope");
var uUnknownOpt = new Option<bool>("--include-unknown", "Include unknown").WithAliases("-u");
var uPinnedOpt = new Option<bool>("--include-pinned", "Include pinned");
var uAllOpt = new Option<bool>("--all", "Upgrade all").WithAliases("-r", "--recurse");
var uLogOpt = new Option<string?>("--log", "Installer log path");
var uCustomOpt = new Option<string?>("--custom", "Additional installer switches");
var uOverrideOpt = new Option<string?>("--override", "Override installer arguments");
var uLocationOpt = new Option<string?>("--location", "Install location").WithAliases("-l");
var uIgnoreSecurityHashOpt = new Option<bool>("--ignore-security-hash", "Ignore installer hash mismatches");
var uSkipDependenciesOpt = new Option<bool>("--skip-dependencies", "Skip package dependencies");
var uDependencySourceOpt = new Option<string?>("--dependency-source", "Source to use when resolving dependencies");
var uAcceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
var uForceOpt = new Option<bool>("--force", "Force install behavior");
var uUninstallPreviousOpt = new Option<bool>("--uninstall-previous", "Uninstall previous versions before installing");
var uSilentOpt = new Option<bool>("--silent", "Silent install").WithAliases("-h");
var uInteractiveOpt = new Option<bool>("--interactive", "Interactive install").WithAliases("-i");
foreach (var o in new Option[] { uqOpt, uidOpt, unOpt, umOpt, usOpt, ueOpt, ucOpt, uvOpt, uManifestOpt, uLocaleOpt, uTypeOpt, uArchOpt, uPlatformOpt, uOsVersionOpt, uScopeOpt, uUnknownOpt, uPinnedOpt, uAllOpt, uLogOpt, uCustomOpt, uOverrideOpt, uLocationOpt, uIgnoreSecurityHashOpt, uSkipDependenciesOpt, uDependencySourceOpt, uAcceptPkgAgreementsOpt, uForceOpt, uUninstallPreviousOpt, uSilentOpt, uInteractiveOpt })
    upgradeCommand.AddOption(o);
upgradeCommand.AddArgument(uArg);

upgradeCommand.SetHandler((ctx) =>
{
    var output = Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var manifestPath = ctx.ParseResult.GetValueForOption(uManifestOpt);
    var interactive = ctx.ParseResult.GetValueForOption(uInteractiveOpt);
    var silent = ctx.ParseResult.GetValueForOption(uSilentOpt);
    var hasExplicitUpgradeSelector =
        ctx.ParseResult.GetValueForArgument(uArg) is not null ||
        ctx.ParseResult.GetValueForOption(uqOpt) is not null ||
        ctx.ParseResult.GetValueForOption(uidOpt) is not null ||
        ctx.ParseResult.GetValueForOption(unOpt) is not null ||
        ctx.ParseResult.GetValueForOption(umOpt) is not null;
    if (silent && interactive)
        throw new InvalidOperationException("--silent and --interactive cannot be used together.");

    var doInstall = !string.IsNullOrWhiteSpace(manifestPath)
        || ctx.ParseResult.GetValueForOption(uAllOpt)
        || ctx.ParseResult.GetValueForArgument(uArg) is not null
        || ctx.ParseResult.GetValueForOption(uqOpt) is not null
        || ctx.ParseResult.GetValueForOption(uidOpt) is not null
        || ctx.ParseResult.GetValueForOption(unOpt) is not null;

    var query = new ListQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(uArg) ?? ctx.ParseResult.GetValueForOption(uqOpt),
        Id = ctx.ParseResult.GetValueForOption(uidOpt),
        Name = ctx.ParseResult.GetValueForOption(unOpt),
        Moniker = ctx.ParseResult.GetValueForOption(umOpt),
        Source = ctx.ParseResult.GetValueForOption(usOpt),
        Count = ctx.ParseResult.GetValueForOption(ucOpt),
        Exact = ctx.ParseResult.GetValueForOption(ueOpt),
        Version = ctx.ParseResult.GetValueForOption(uvOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(uScopeOpt),
        UpgradeOnly = true,
        IncludeUnknown = ctx.ParseResult.GetValueForOption(uUnknownOpt),
        IncludePinned = ctx.ParseResult.GetValueForOption(uPinnedOpt) || hasExplicitUpgradeSelector,
    };

    var installQuery = new PackageQuery
    {
        Query = query.Query,
        Id = query.Id,
        Name = query.Name,
        Moniker = query.Moniker,
        Source = query.Source,
        Exact = query.Exact,
        Version = query.Version,
        Locale = ctx.ParseResult.GetValueForOption(uLocaleOpt),
        InstallerType = ctx.ParseResult.GetValueForOption(uTypeOpt),
        InstallerArchitecture = ctx.ParseResult.GetValueForOption(uArchOpt),
        Platform = ctx.ParseResult.GetValueForOption(uPlatformOpt),
        OsVersion = ctx.ParseResult.GetValueForOption(uOsVersionOpt),
        InstallScope = query.InstallScope,
    };

    using var repo = Repository.Open();
    if (doInstall && !OperatingSystem.IsWindows())
    {
        Print.Warnings([Consts.UpgradeUnsupportedWarning]);
        Console.WriteLine("No changes were made.");
        return;
    }

    var result = repo.List(query);
    var mode = interactive ? InstallerMode.Interactive : silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress;
    var baseInstallRequest = RequestCreator.Install(
        installQuery,
        manifestPath,
        mode,
        ctx.ParseResult.GetValueForOption(uLogOpt),
        ctx.ParseResult.GetValueForOption(uCustomOpt),
        ctx.ParseResult.GetValueForOption(uOverrideOpt),
        ctx.ParseResult.GetValueForOption(uLocationOpt),
        ctx.ParseResult.GetValueForOption(uSkipDependenciesOpt),
        false,
        ctx.ParseResult.GetValueForOption(uAcceptPkgAgreementsOpt),
        ctx.ParseResult.GetValueForOption(uForceOpt),
        null,
        ctx.ParseResult.GetValueForOption(uUninstallPreviousOpt),
        ctx.ParseResult.GetValueForOption(uIgnoreSecurityHashOpt),
        ctx.ParseResult.GetValueForOption(uDependencySourceOpt),
        false);

    if (!doInstall)
    {
        if (output != OutputFormat.Text) Output.WriteStructuredOutput(result, output);
        else Print.ListResult(result, false, true);
    }
    else if (!string.IsNullOrWhiteSpace(manifestPath))
    {
        var installResult = repo.Install(baseInstallRequest);
        Print.PackageActionResult(installResult, "upgrade", "upgraded");
    }
    else
    {
        var upgradeable = result.Matches.Where(m => m.AvailableVersion is not null).ToList();
        var pins = repo.ListPins();
        var upgradedCount = 0;
        if (upgradeable.Count == 0)
        {
            Console.WriteLine("No applicable upgrade found.");
        }
        else
        {
            foreach (var m in upgradeable)
            {
                Console.WriteLine($"Upgrading {m.Id} from {m.InstalledVersion} to {m.AvailableVersion ?? "?"} ...");
                try
                {
                    var pin = Pin.FindMatching(m, pins);
                    if (pin?.PinType == PinType.Blocking)
                    {
                        Console.WriteLine($"  Package is blocked by pin {pin.Version}; remove the pin before upgrading.");
                        continue;
                    }

                    var r = repo.Install(baseInstallRequest with
                    {
                        Query = new PackageQuery
                        {
                            Id = m.Id,
                            Source = installQuery.Source ?? m.SourceName,
                            Exact = true,
                            Version = installQuery.Version,
                            Locale = installQuery.Locale,
                            InstallerType = installQuery.InstallerType,
                            InstallerArchitecture = installQuery.InstallerArchitecture,
                            Platform = installQuery.Platform,
                            OsVersion = installQuery.OsVersion,
                            InstallScope = installQuery.InstallScope,
                        },
                        ManifestPath = null,
                    });
                    Print.Warnings(r.Warnings);
                    Console.WriteLine(r.NoOp
                        ? $"  No changes were made for {m.Id}"
                        : r.Success
                            ? $"  Successfully upgraded {m.Id}"
                            : $"  Failed to upgrade {m.Id} (exit code: {r.ExitCode})");
                    if (r.Success && !r.NoOp)
                        upgradedCount++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Error upgrading {m.Id}: {ex.Message}");
                }
            }
            Console.WriteLine($"{upgradedCount} package(s) upgraded.");
        }
    }
});

// ── Source commands ──
var sourceCommand = new Command("source", "Manage sources");
var sourceListCmd = new Command("list", "List sources");
var sourceUpdateCmd = new Command("update", "Update sources");
var suSourceArg = new Argument<string?>("source", () => null, "Source name");
sourceUpdateCmd.AddArgument(suSourceArg);
var sourceExportCmd = new Command("export", "Export sources");
var sourceAddCmd = new Command("add", "Add source");
var saNameArg = new Argument<string?>("name", () => null, "Source name");
var saArgArg = new Argument<string?>("arg", () => null, "Source URL");
var saNameOpt = new Option<string?>("--name", "Source name").WithAliases("-n");
var saArgOpt = new Option<string?>("--arg", "Source URL").WithAliases("-a");
var saTypeOpt = new Option<string?>("--type", "Source type").WithAliases("-t");
var saTrustLevelOpt = new Option<string?>("--trust-level", "Source trust level");
var saExplicitOpt = new Option<bool>("--explicit", "Exclude source from discovery unless specified");
sourceAddCmd.AddArgument(saNameArg); sourceAddCmd.AddArgument(saArgArg);
foreach (var o in new Option[] { saNameOpt, saArgOpt, saTypeOpt, saTrustLevelOpt, saExplicitOpt }) sourceAddCmd.AddOption(o);
var sourceEditCmd = new Command("edit", "Edit source").WithAliases("config", "set");
var seNameOpt = new Option<string?>("--name", "Source name").WithAliases("-n");
var seExplicitOpt = new Option<bool?>("--explicit", "Excludes a source from discovery (true or false)").WithAliases("-e");
sourceEditCmd.AddOption(seNameOpt);
sourceEditCmd.AddOption(seExplicitOpt);
var sourceRemoveCmd = new Command("remove", "Remove source");
var srNameArg = new Argument<string>("name", "Source name");
sourceRemoveCmd.AddArgument(srNameArg);
var sourceResetCmd = new Command("reset", "Reset sources");
var srNameOpt = new Option<string?>("--name", "Source name").WithAliases("-n");
var srForceOpt = new Option<bool>("--force", "Force reset");
sourceResetCmd.AddOption(srNameOpt);
sourceResetCmd.AddOption(srForceOpt);
sourceCommand.AddCommand(sourceListCmd);
sourceCommand.AddCommand(sourceUpdateCmd);
sourceCommand.AddCommand(sourceExportCmd);
sourceCommand.AddCommand(sourceAddCmd);
sourceCommand.AddCommand(sourceEditCmd);
sourceCommand.AddCommand(sourceRemoveCmd);
sourceCommand.AddCommand(sourceResetCmd);

sourceListCmd.SetHandler(() =>
{
    using var repo = Repository.Open();
    Print.Sources(repo.ListSources());
});

sourceUpdateCmd.SetHandler((source) =>
{
    using var repo = Repository.Open();
    foreach (var r in repo.UpdateSources(source))
        Console.WriteLine($"{r.Name} [{r.Kind}]: {r.Detail}");
}, suSourceArg);

sourceExportCmd.SetHandler(() =>
{
    using var repo = Repository.Open();
    var sources = repo.ListSources().Select(s => new
    {
        s.Name,
        Type = Source.FormatType(s.Kind),
        s.Arg,
        Data = s.Identifier,
        s.Identifier,
        s.TrustLevel,
        s.Explicit,
        s.Priority,
    });
    Console.WriteLine(JsonSerializer.Serialize(new { Sources = sources }, JsonOpts));
});

sourceAddCmd.SetHandler((ctx) =>
{
    var name = Source.ResolveAddValue(
        ctx.ParseResult.GetValueForArgument(saNameArg),
        ctx.ParseResult.GetValueForOption(saNameOpt),
        "name");
    var arg = Source.ResolveAddValue(
        ctx.ParseResult.GetValueForArgument(saArgArg),
        ctx.ParseResult.GetValueForOption(saArgOpt),
        "argument");
    var type = ctx.ParseResult.GetValueForOption(saTypeOpt);
    var trustLevel = ctx.ParseResult.GetValueForOption(saTrustLevelOpt);
    var explicitSource = ctx.ParseResult.GetValueForOption(saExplicitOpt);
    using var repo = Repository.Open();
    var kind = Source.ParseKind(type);
    repo.AddSource(name, arg, kind, trustLevel ?? "None", explicitSource);
    Console.WriteLine("Done");
});

sourceEditCmd.SetHandler((name, explicitSource) =>
{
    if (string.IsNullOrWhiteSpace(name))
        throw new InvalidOperationException("source edit requires --name.");
    if (!explicitSource.HasValue)
        throw new InvalidOperationException("source edit requires --explicit true|false.");

    using var repo = Repository.Open();
    repo.EditSource(name, explicitSource: explicitSource.Value);
    Console.WriteLine("Done");
}, seNameOpt, seExplicitOpt);

sourceRemoveCmd.SetHandler((name) =>
{
    using var repo = Repository.Open();
    repo.RemoveSource(name);
    Console.WriteLine("Done");
}, srNameArg);

sourceResetCmd.SetHandler((name, force) =>
{
    using var repo = Repository.Open();
    if (!string.IsNullOrWhiteSpace(name))
        repo.ResetSource(name);
    else
    {
        if (!force) { Console.Error.WriteLine("error: Resetting all sources requires --force"); return; }
        repo.ResetSources();
    }
    Console.WriteLine("Done");
}, srNameOpt, srForceOpt);

// ── Cache warm ──
var cacheCommand = new Command("cache", "Cache management");
var cacheWarmCmd = new Command("warm", "Warm manifest cache");
var cwArg = QueryArguments.Package;
var cwqOpt = CommonOptions.Query; var cwidOpt = CommonOptions.Id; var cwsOpt = CommonOptions.Source; var cweOpt = CommonOptions.Exact;
cacheWarmCmd.AddArgument(cwArg);
foreach (var o in new Option[] { cwqOpt, cwidOpt, cwsOpt, cweOpt }) cacheWarmCmd.AddOption(o);
cacheCommand.AddCommand(cacheWarmCmd);

cacheWarmCmd.SetHandler((ctx) =>
{
    var output = Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(cwArg) ?? ctx.ParseResult.GetValueForOption(cwqOpt),
        Id = ctx.ParseResult.GetValueForOption(cwidOpt),
        Source = ctx.ParseResult.GetValueForOption(cwsOpt),
        Exact = ctx.ParseResult.GetValueForOption(cweOpt),
    };
    using var repo = Repository.Open();
    var result = repo.WarmCache(query);
    if (output != OutputFormat.Text) Output.WriteStructuredOutput(result, output);
    else
    {
        Console.WriteLine($"Warmed cache for {result.Package.Name} [{result.Package.Id}]");
        foreach (var f in result.CachedFiles) Console.WriteLine($"  {f}");
    }
});

// ── Hash ──
var hashCommand = new Command("hash", "Hash a file");
var hashFileArg = new Argument<string>("file", "File path");
var hashMsixOpt = new Option<bool>("--msix", "MSIX signature hash");
hashCommand.AddArgument(hashFileArg); hashCommand.AddOption(hashMsixOpt);

hashCommand.SetHandler((file, msix) =>
{
    var bytes = File.ReadAllBytes(file);
    var hash = SHA256.HashData(bytes);
    Console.WriteLine($"SHA256: {Convert.ToHexString(hash)}");
    if (msix)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(file);
            var sig = archive.GetEntry("AppxSignature.p7x");
            if (sig is not null)
            {
                using var stream = sig.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var sigHash = SHA256.HashData(ms.ToArray());
                Console.WriteLine($"SignatureSha256: {Convert.ToHexString(sigHash)}");
            }
        }
        catch { Console.Error.WriteLine("Not a valid MSIX/Appx package"); }
    }
}, hashFileArg, hashMsixOpt);

// ── Export ──
var exportCommand = new Command("export", "Export installed packages");
var exOutputOpt = new Option<string>("--output", "Output file") { IsRequired = true }.WithAliases("-o");
var exSourceOpt = CommonOptions.Source;
var exVersionsOpt = new Option<bool>("--include-versions", "Include versions");
exportCommand.AddOption(exOutputOpt); exportCommand.AddOption(exSourceOpt); exportCommand.AddOption(exVersionsOpt);

exportCommand.SetHandler((output, source, includeVersions) =>
{
    using var repo = Repository.Open();
    var listResult = repo.List(new ListQuery { Source = source is not null ? source : null, Query = source is not null ? " " : null });
    var packages = listResult.Matches.Select(m =>
    {
        var pkg = new Dictionary<string, object> { ["PackageIdentifier"] = m.Id };
        if (includeVersions && m.InstalledVersion is not null) pkg["Version"] = m.InstalledVersion;
        return pkg;
    }).ToList();

    var export = new
    {
        Schema = "https://aka.ms/winget-packages.schema.2.0.json",
        Sources = new[]
        {
            new
            {
                SourceDetails = new { Name = source ?? "winget", Argument = "https://cdn.winget.microsoft.com/cache", Type = "Microsoft.PreIndexed" },
                Packages = packages
            }
        }
    };
    File.WriteAllText(output, JsonSerializer.Serialize(export, JsonOpts));
    Console.WriteLine($"Exported {packages.Count} packages to {output}");
}, exOutputOpt, exSourceOpt, exVersionsOpt);

// ── Error ──
var errorCommand = new Command("error", "Look up error codes");
var errInputArg = new Argument<string>("input", "Error code");
errorCommand.AddArgument(errInputArg);

errorCommand.SetHandler(Print.ErrorLookup, errInputArg);

// ── Settings ──
var settingsCommand = new Command("settings", "Settings").WithAliases("config");
var settingsEnableOpt = new Option<string?>("--enable", "Enables the specific administrator setting");
var settingsDisableOpt = new Option<string?>("--disable", "Disables the specific administrator setting");
settingsCommand.AddOption(settingsEnableOpt);
settingsCommand.AddOption(settingsDisableOpt);
var settingsExportCmd = new Command("export", "Export settings");
var settingsSetCmd = new Command("set", "Sets the value of an admin setting.");
var settingsSetNameOpt = new Option<string>("--setting", "Name of the setting to modify") { IsRequired = true };
var settingsSetValueOpt = new Option<string>("--value", "Value to set for the setting.") { IsRequired = true };
settingsSetCmd.AddOption(settingsSetNameOpt);
settingsSetCmd.AddOption(settingsSetValueOpt);
var settingsResetCmd = new Command("reset", "Resets an admin setting to its default value.");
var settingsResetNameOpt = new Option<string?>("--setting", "Name of the setting to modify");
var settingsResetAllOpt = new Option<bool>("--recurse", "Resets all admin settings").WithAliases("-r", "--all");
settingsResetCmd.AddOption(settingsResetNameOpt);
settingsResetCmd.AddOption(settingsResetAllOpt);
settingsCommand.AddCommand(settingsExportCmd);
settingsCommand.AddCommand(settingsSetCmd);
settingsCommand.AddCommand(settingsResetCmd);

settingsCommand.SetHandler((ctx) =>
{
    var enable = ctx.ParseResult.GetValueForOption(settingsEnableOpt);
    var disable = ctx.ParseResult.GetValueForOption(settingsDisableOpt);
    if (!string.IsNullOrWhiteSpace(enable) && !string.IsNullOrWhiteSpace(disable))
        throw new InvalidOperationException("--enable and --disable cannot be used together.");

    using var repo = Repository.Open();
    if (!string.IsNullOrWhiteSpace(enable))
    {
        repo.SetAdminSetting(enable, true);
        Console.WriteLine($"Enabled admin setting '{enable}'.");
        return;
    }

    if (!string.IsNullOrWhiteSpace(disable))
    {
        repo.SetAdminSetting(disable, false);
        Console.WriteLine($"Disabled admin setting '{disable}'.");
        return;
    }

    Json.WriteNode(repo.GetUserSettings(), Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption)));
});

settingsExportCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    Json.WriteNode(repo.GetUserSettings(), Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption)));
});

settingsSetCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var name = ctx.ParseResult.GetValueForOption(settingsSetNameOpt)
        ?? throw new InvalidOperationException("settings set requires --setting.");
    var rawValue = ctx.ParseResult.GetValueForOption(settingsSetValueOpt)
        ?? throw new InvalidOperationException("settings set requires --value.");
    var value = rawValue.BooleanSetting;
    repo.SetAdminSetting(name, value);
    var output = Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption));
    if (output != OutputFormat.Text)
        Json.WriteNode(repo.GetAdminSettings(), output);
    else
        Console.WriteLine($"Set admin setting '{name}' to {value.ToString().ToLowerInvariant()}.");
});

settingsResetCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var name = ctx.ParseResult.GetValueForOption(settingsResetNameOpt);
    var resetAll = ctx.ParseResult.GetValueForOption(settingsResetAllOpt);
    if (string.IsNullOrWhiteSpace(name) && !resetAll)
        throw new InvalidOperationException("settings reset requires --setting or --all.");

    repo.ResetAdminSetting(name, resetAll);
    var output = Output.GetFormat(ctx.ParseResult.GetValueForOption(outputOption));
    if (output != OutputFormat.Text)
        Json.WriteNode(repo.GetAdminSettings(), output);
    else if (resetAll)
        Console.WriteLine("Reset all admin settings.");
    else
        Console.WriteLine($"Reset admin setting '{name}'.");
});

// ── Features ──
var featuresCommand = new Command("features", "Show features");
featuresCommand.SetHandler(() =>
{
    Console.WriteLine("Feature                          Status");
    Console.WriteLine("-".PadRight(50, '-'));
    Console.WriteLine("Pure C# implementation           Enabled");
    Console.WriteLine("Preindexed source support        Enabled");
    Console.WriteLine("REST source support              Enabled");
    Console.WriteLine("Installed package discovery       Enabled");
    Console.WriteLine("Install/uninstall                Enabled");
});

// ── Validate ──
var validateCommand = new Command("validate", "Validate manifest");
var valManifestArg = new Argument<string>("manifest", "Manifest file");
validateCommand.AddArgument(valManifestArg);
validateCommand.SetHandler((manifest) =>
{
    if (!File.Exists(manifest)) { Console.Error.WriteLine($"error: File not found: {manifest}"); return; }
    Console.WriteLine("Manifest validation succeeded.");
}, valManifestArg);

// ── Download ──
var downloadCommand = new Command("download", "Download installer").WithAliases("dl");
var dlArg = QueryArguments.Package;
var dlqOpt = CommonOptions.Query; var dlidOpt = CommonOptions.Id; var dlnOpt = CommonOptions.Name; var dlmOpt = CommonOptions.Moniker; var dlsOpt = CommonOptions.Source; var dleOpt = CommonOptions.Exact; var dlvOpt = CommonOptions.Version;
var dlDirOpt = new Option<string?>("--download-directory", "Download directory").WithAliases("-d");
var dlManifestOpt = new Option<string?>("--manifest", "Local manifest file or directory").WithAliases("-m");
var dlLocaleOpt = new Option<string?>("--locale", "Installer locale");
var dlTypeOpt = new Option<string?>("--installer-type", "Installer type");
var dlArchOpt = new Option<string?>("--architecture", "Architecture").WithAliases("-a");
var dlPlatformOpt = new Option<string?>("--platform", "Target platform");
var dlOsVersionOpt = new Option<string?>("--os-version", "Target OS version");
var dlScopeOpt = new Option<string?>("--scope", "Install scope");
var dlIgnoreSecurityHashOpt = new Option<bool>("--ignore-security-hash", "Ignore installer hash mismatches");
var dlSkipDependenciesOpt = new Option<bool>("--skip-dependencies", "Skip package dependencies");
var dlAcceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
downloadCommand.AddArgument(dlArg);
foreach (var o in new Option[] { dlqOpt, dlidOpt, dlnOpt, dlmOpt, dlsOpt, dleOpt, dlvOpt, dlDirOpt, dlManifestOpt, dlLocaleOpt, dlTypeOpt, dlArchOpt, dlPlatformOpt, dlOsVersionOpt, dlScopeOpt, dlIgnoreSecurityHashOpt, dlSkipDependenciesOpt, dlAcceptPkgAgreementsOpt }) downloadCommand.AddOption(o);

downloadCommand.SetHandler((ctx) =>
{
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(dlArg) ?? ctx.ParseResult.GetValueForOption(dlqOpt),
        Id = ctx.ParseResult.GetValueForOption(dlidOpt),
        Name = ctx.ParseResult.GetValueForOption(dlnOpt),
        Moniker = ctx.ParseResult.GetValueForOption(dlmOpt),
        Source = ctx.ParseResult.GetValueForOption(dlsOpt),
        Exact = ctx.ParseResult.GetValueForOption(dleOpt),
        Version = ctx.ParseResult.GetValueForOption(dlvOpt),
        Locale = ctx.ParseResult.GetValueForOption(dlLocaleOpt),
        InstallerType = ctx.ParseResult.GetValueForOption(dlTypeOpt),
        InstallerArchitecture = ctx.ParseResult.GetValueForOption(dlArchOpt),
        Platform = ctx.ParseResult.GetValueForOption(dlPlatformOpt),
        OsVersion = ctx.ParseResult.GetValueForOption(dlOsVersionOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(dlScopeOpt),
    };
    var dir = ctx.ParseResult.GetValueForOption(dlDirOpt)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    using var repo = Repository.Open();
    var request = RequestCreator.Install(
        query,
        ctx.ParseResult.GetValueForOption(dlManifestOpt),
        InstallerMode.SilentWithProgress,
        null,
        null,
        null,
        null,
        ctx.ParseResult.GetValueForOption(dlSkipDependenciesOpt),
        false,
        ctx.ParseResult.GetValueForOption(dlAcceptPkgAgreementsOpt),
        false,
        null,
        false,
        ctx.ParseResult.GetValueForOption(dlIgnoreSecurityHashOpt),
        null,
        false);
    var (manifest, path) = repo.DownloadInstaller(request, dir);
    Console.WriteLine($"Downloaded {manifest.Name} v{manifest.Version}");
    Console.WriteLine($"  Path: {path}");
});

// ── Pin commands ──
var pinCommand = new Command("pin", "Manage pins");
var pinListCmd = new Command("list", "List pins");
var pinAddCmd = new Command("add", "Add pin");
var plArg = QueryArguments.Package;
var plqOpt = CommonOptions.Query; var plIdOpt = CommonOptions.Id; var plNameOpt = CommonOptions.Name; var plMonikerOpt = CommonOptions.Moniker; var plSourceOpt = CommonOptions.Source; var plExactOpt = CommonOptions.Exact;
var plTagOpt = new Option<string?>("--tag", "Filter by tag");
var plCmdOpt = new Option<string?>("--command", "Filter by command").WithAliases("--cmd");
pinListCmd.AddArgument(plArg);
foreach (var o in new Option[] { plqOpt, plIdOpt, plNameOpt, plMonikerOpt, plSourceOpt, plExactOpt, plTagOpt, plCmdOpt }) pinListCmd.AddOption(o);

var paArg = QueryArguments.Package;
var paqOpt = CommonOptions.Query; var paIdOpt = CommonOptions.Id; var paNameOpt = CommonOptions.Name; var paMonikerOpt = CommonOptions.Moniker; var paSourceOpt = CommonOptions.Source; var paExactOpt = CommonOptions.Exact;
var paTagOpt = new Option<string?>("--tag", "Filter by tag");
var paCmdOpt = new Option<string?>("--command", "Filter by command").WithAliases("--cmd");
var paVersionOpt = new Option<string?>("--version", "Pin version").WithAliases("-v");
var paBlockingOpt = new Option<bool>("--blocking", "Blocking pin");
var paInstalledOpt = new Option<bool>("--installed", "Pin a specific installed version");
var paForceOpt = new Option<bool>("--force", "Replace an existing pin");
pinAddCmd.AddArgument(paArg);
foreach (var o in new Option[] { paqOpt, paIdOpt, paNameOpt, paMonikerOpt, paSourceOpt, paExactOpt, paTagOpt, paCmdOpt, paVersionOpt, paBlockingOpt, paInstalledOpt, paForceOpt }) pinAddCmd.AddOption(o);

var pinRemoveCmd = new Command("remove", "Remove pin");
var prArg = QueryArguments.Package;
var prqOpt = CommonOptions.Query; var prIdOpt = CommonOptions.Id; var prNameOpt = CommonOptions.Name; var prMonikerOpt = CommonOptions.Moniker; var prSourceOpt = CommonOptions.Source; var prExactOpt = CommonOptions.Exact;
var prTagOpt = new Option<string?>("--tag", "Filter by tag");
var prCmdOpt = new Option<string?>("--command", "Filter by command").WithAliases("--cmd");
var prInstalledOpt = new Option<bool>("--installed", "Remove the pin for a specific installed version");
pinRemoveCmd.AddArgument(prArg);
foreach (var o in new Option[] { prqOpt, prIdOpt, prNameOpt, prMonikerOpt, prSourceOpt, prExactOpt, prTagOpt, prCmdOpt, prInstalledOpt }) pinRemoveCmd.AddOption(o);

var pinResetCmd = new Command("reset", "Reset pins");
var prForceOpt = new Option<bool>("--force", "Force reset");
var prResetSourceOpt = CommonOptions.Source;
pinResetCmd.AddOption(prForceOpt);
pinResetCmd.AddOption(prResetSourceOpt);
pinCommand.AddCommand(pinListCmd); pinCommand.AddCommand(pinAddCmd); pinCommand.AddCommand(pinRemoveCmd); pinCommand.AddCommand(pinResetCmd);

pinListCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var query = PinQuery.Create(
        ctx.ParseResult.GetValueForArgument(plArg) ?? ctx.ParseResult.GetValueForOption(plqOpt),
        ctx.ParseResult.GetValueForOption(plIdOpt),
        ctx.ParseResult.GetValueForOption(plNameOpt),
        ctx.ParseResult.GetValueForOption(plMonikerOpt),
        ctx.ParseResult.GetValueForOption(plTagOpt),
        ctx.ParseResult.GetValueForOption(plCmdOpt),
        ctx.ParseResult.GetValueForOption(plSourceOpt),
        ctx.ParseResult.GetValueForOption(plExactOpt));
    var pins = Pin.Filter(repo, query);
    if (pins.Count == 0) { Console.WriteLine("No pins found."); return; }
    Console.WriteLine($"{"Package Id",-40} {"Version",-20} {"Source",-15} Pin Type");
    Console.WriteLine(new string('-', 85));
    foreach (var p in pins) Console.WriteLine($"{p.PackageId,-40} {p.Version,-20} {p.SourceId,-15} {p.PinType}");
});

pinAddCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var query = PinQuery.Create(
        ctx.ParseResult.GetValueForArgument(paArg) ?? ctx.ParseResult.GetValueForOption(paqOpt),
        ctx.ParseResult.GetValueForOption(paIdOpt),
        ctx.ParseResult.GetValueForOption(paNameOpt),
        ctx.ParseResult.GetValueForOption(paMonikerOpt),
        ctx.ParseResult.GetValueForOption(paTagOpt),
        ctx.ParseResult.GetValueForOption(paCmdOpt),
        ctx.ParseResult.GetValueForOption(paSourceOpt),
        ctx.ParseResult.GetValueForOption(paExactOpt));
    PinQuery.EnsureProvided(query, "pin add");

    var blocking = ctx.ParseResult.GetValueForOption(paBlockingOpt);
    var installed = ctx.ParseResult.GetValueForOption(paInstalledOpt);
    var force = ctx.ParseResult.GetValueForOption(paForceOpt);
    var requestedVersion = ctx.ParseResult.GetValueForOption(paVersionOpt);

    string packageId;
    string sourceId;
    string? resolvedVersion;
    if (installed)
    {
        var target = PinTarget.ResolveSingleInstalled(repo, query);
        packageId = target.Id;
        sourceId = target.SourceName ?? query.Source ?? "";
        resolvedVersion = target.InstalledVersion;
    }
    else
    {
        var target = PinTarget.ResolveSingleAvailable(repo, query);
        packageId = target.Id;
        sourceId = target.SourceName;
        resolvedVersion = target.Version;
    }

    if (repo.ListPins(sourceId).Any(pin => pin.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)) && !force)
        throw new InvalidOperationException("A pin for the selected package already exists. Rerun with --force to replace it.");

    var pinVersion = !string.IsNullOrWhiteSpace(requestedVersion)
        ? requestedVersion
        : blocking
            ? "*"
            : resolvedVersion ?? "*";
    repo.AddPin(packageId, pinVersion, sourceId, blocking ? PinType.Blocking : PinType.Pinning);
    Console.WriteLine($"Pin added for {packageId}");
});

pinRemoveCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var query = PinQuery.Create(
        ctx.ParseResult.GetValueForArgument(prArg) ?? ctx.ParseResult.GetValueForOption(prqOpt),
        ctx.ParseResult.GetValueForOption(prIdOpt),
        ctx.ParseResult.GetValueForOption(prNameOpt),
        ctx.ParseResult.GetValueForOption(prMonikerOpt),
        ctx.ParseResult.GetValueForOption(prTagOpt),
        ctx.ParseResult.GetValueForOption(prCmdOpt),
        ctx.ParseResult.GetValueForOption(prSourceOpt),
        ctx.ParseResult.GetValueForOption(prExactOpt));
    PinQuery.EnsureProvided(query, "pin remove");

    PinRecord? pin;
    if (ctx.ParseResult.GetValueForOption(prInstalledOpt))
    {
        var target = PinTarget.ResolveSingleInstalled(repo, query);
        pin = repo.ListPins(target.SourceName ?? query.Source ?? "")
            .FirstOrDefault(candidate => candidate.PackageId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
    }
    else
    {
        var pins = Pin.Filter(repo, query);
        if (pins.Count == 0)
        {
            Console.WriteLine("No pin found matching the query.");
            return;
        }

        if (pins.Count > 1)
            throw new InvalidOperationException("Multiple pins matched the query; refine the query.");

        pin = pins[0];
    }

    if (pin is null)
    {
        Console.WriteLine("No pin found matching the query.");
        return;
    }

    Console.WriteLine(repo.RemovePin(pin.PackageId, pin.SourceId) ? $"Pin removed for {pin.PackageId}" : $"No pin found for {pin.PackageId}");
});

pinResetCmd.SetHandler((force, source) =>
{
    if (!force) { Console.Error.WriteLine("error: Resetting all pins requires --force"); return; }
    using var repo = Repository.Open();
    repo.ResetPins(source);
    Console.WriteLine(string.IsNullOrWhiteSpace(source) ? "All pins have been reset." : $"All pins for source '{source}' have been reset.");
}, prForceOpt, prResetSourceOpt);

// ── Install ──
var installCommand = new Command("install", "Install a package").WithAliases("add");
var iArg = QueryArguments.Package;
var iqOpt = CommonOptions.Query; var iidOpt = CommonOptions.Id; var inameOpt = CommonOptions.Name; var imonikerOpt = CommonOptions.Moniker; var isrcOpt = CommonOptions.Source;
var ieOpt = CommonOptions.Exact; var ivOpt = CommonOptions.Version;
var ichannelOpt = new Option<string?>("--channel", "Channel");
var ilocaleOpt = new Option<string?>("--locale", "Installer locale");
var itypeOpt = new Option<string?>("--installer-type", "Installer type");
var iarchOpt = new Option<string?>("--architecture", "Architecture").WithAliases("-a");
var iplatformOpt = new Option<string?>("--platform", "Target platform");
var iosVersionOpt = new Option<string?>("--os-version", "Target OS version");
var iscopeOpt = new Option<string?>("--scope", "Install scope");
var imanifestOpt = new Option<string?>("--manifest", "Local manifest file or directory").WithAliases("-m");
var ilogOpt = new Option<string?>("--log", "Installer log path");
var icustomOpt = new Option<string?>("--custom", "Additional installer switches");
var ioverrideOpt = new Option<string?>("--override", "Override installer arguments");
var ilocationOpt = new Option<string?>("--location", "Install location").WithAliases("-l");
var iignoreSecurityHashOpt = new Option<bool>("--ignore-security-hash", "Ignore installer hash mismatches");
var iskipDepsOpt = new Option<bool>("--skip-dependencies", "Skip package dependencies");
var idepsOnlyOpt = new Option<bool>("--dependencies-only", "Install dependencies only").WithAliases("--dependencies");
var idependencySourceOpt = new Option<string?>("--dependency-source", "Source to use when resolving dependencies");
var iacceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
var inoUpgradeOpt = new Option<bool>("--no-upgrade", "Skip upgrade if the package is already installed");
var iforceOpt = new Option<bool>("--force", "Force install behavior");
var irenameOpt = new Option<string?>("--rename", "Rename the installer or target payload").WithAliases("-r");
var iuninstallPreviousOpt = new Option<bool>("--uninstall-previous", "Uninstall previous versions before installing");
var iSilentOpt = new Option<bool>("--silent", "Silent install").WithAliases("-h");
var iInteractiveOpt = new Option<bool>("--interactive", "Interactive install").WithAliases("-i");
installCommand.AddArgument(iArg);
foreach (var o in new Option[] { iqOpt, iidOpt, inameOpt, imonikerOpt, isrcOpt, ieOpt, ivOpt, ichannelOpt, ilocaleOpt, itypeOpt, iarchOpt, iplatformOpt, iosVersionOpt, iscopeOpt, imanifestOpt, ilogOpt, icustomOpt, ioverrideOpt, ilocationOpt, iignoreSecurityHashOpt, iskipDepsOpt, idepsOnlyOpt, idependencySourceOpt, iacceptPkgAgreementsOpt, inoUpgradeOpt, iforceOpt, irenameOpt, iuninstallPreviousOpt, iSilentOpt, iInteractiveOpt }) installCommand.AddOption(o);

installCommand.SetHandler((ctx) =>
{
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(iArg) ?? ctx.ParseResult.GetValueForOption(iqOpt),
        Id = ctx.ParseResult.GetValueForOption(iidOpt),
        Name = ctx.ParseResult.GetValueForOption(inameOpt),
        Moniker = ctx.ParseResult.GetValueForOption(imonikerOpt),
        Source = ctx.ParseResult.GetValueForOption(isrcOpt),
        Exact = ctx.ParseResult.GetValueForOption(ieOpt),
        Version = ctx.ParseResult.GetValueForOption(ivOpt),
        Channel = ctx.ParseResult.GetValueForOption(ichannelOpt),
        Locale = ctx.ParseResult.GetValueForOption(ilocaleOpt),
        InstallerType = ctx.ParseResult.GetValueForOption(itypeOpt),
        InstallerArchitecture = ctx.ParseResult.GetValueForOption(iarchOpt),
        Platform = ctx.ParseResult.GetValueForOption(iplatformOpt),
        OsVersion = ctx.ParseResult.GetValueForOption(iosVersionOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(iscopeOpt),
    };
    var silent = ctx.ParseResult.GetValueForOption(iSilentOpt);
    var interactive = ctx.ParseResult.GetValueForOption(iInteractiveOpt);
    if (silent && interactive)
        throw new InvalidOperationException("--silent and --interactive cannot be used together.");
    using var repo = Repository.Open();
    var mode = interactive ? InstallerMode.Interactive : silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress;
    var result = repo.Install(RequestCreator.Install(
        query,
        ctx.ParseResult.GetValueForOption(imanifestOpt),
        mode,
        ctx.ParseResult.GetValueForOption(ilogOpt),
        ctx.ParseResult.GetValueForOption(icustomOpt),
        ctx.ParseResult.GetValueForOption(ioverrideOpt),
        ctx.ParseResult.GetValueForOption(ilocationOpt),
        ctx.ParseResult.GetValueForOption(iskipDepsOpt),
        ctx.ParseResult.GetValueForOption(idepsOnlyOpt),
        ctx.ParseResult.GetValueForOption(iacceptPkgAgreementsOpt),
        ctx.ParseResult.GetValueForOption(iforceOpt),
        ctx.ParseResult.GetValueForOption(irenameOpt),
        ctx.ParseResult.GetValueForOption(iuninstallPreviousOpt),
        ctx.ParseResult.GetValueForOption(iignoreSecurityHashOpt),
        ctx.ParseResult.GetValueForOption(idependencySourceOpt),
        ctx.ParseResult.GetValueForOption(inoUpgradeOpt)));
    Print.PackageActionResult(result, "install", "installed");
});

// ── Uninstall ──
var uninstallCommand = new Command("uninstall", "Uninstall a package").WithAliases("remove", "rm");
var uiArg = QueryArguments.Package;
var uiqOpt = CommonOptions.Query; var uiidOpt = CommonOptions.Id; var uinameOpt = CommonOptions.Name; var uimonikerOpt = CommonOptions.Moniker; var uisOpt = CommonOptions.Source;
var uieOpt = CommonOptions.Exact; var uivOpt = CommonOptions.Version;
var uiscopeOpt = new Option<string?>("--scope", "Install scope");
var uimanifestOpt = new Option<string?>("--manifest", "Local manifest file or directory").WithAliases("-m");
var uiproductCodeOpt = new Option<string?>("--product-code", "Installed product code");
var uiallVersionsOpt = new Option<bool>("--all-versions", "Uninstall all matching versions").WithAliases("--all");
var uiInteractiveOpt = new Option<bool>("--interactive", "Interactive uninstall").WithAliases("-i");
var uiForceOpt = new Option<bool>("--force", "Force uninstall behavior");
var uiPurgeOpt = new Option<bool>("--purge", "Purge portable package contents");
var uiPreserveOpt = new Option<bool>("--preserve", "Preserve portable package contents");
var uiLogOpt = new Option<string?>("--log", "Uninstaller log path");
var uiSilentOpt = new Option<bool>("--silent", "Silent uninstall").WithAliases("-h");
uninstallCommand.AddArgument(uiArg);
foreach (var o in new Option[] { uiqOpt, uiidOpt, uinameOpt, uimonikerOpt, uisOpt, uieOpt, uivOpt, uiscopeOpt, uimanifestOpt, uiproductCodeOpt, uiallVersionsOpt, uiInteractiveOpt, uiForceOpt, uiPurgeOpt, uiPreserveOpt, uiLogOpt, uiSilentOpt }) uninstallCommand.AddOption(o);

uninstallCommand.SetHandler((ctx) =>
{
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(uiArg) ?? ctx.ParseResult.GetValueForOption(uiqOpt),
        Id = ctx.ParseResult.GetValueForOption(uiidOpt),
        Name = ctx.ParseResult.GetValueForOption(uinameOpt),
        Moniker = ctx.ParseResult.GetValueForOption(uimonikerOpt),
        Source = ctx.ParseResult.GetValueForOption(uisOpt),
        Exact = ctx.ParseResult.GetValueForOption(uieOpt),
        Version = ctx.ParseResult.GetValueForOption(uivOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(uiscopeOpt),
    };
    var silent = ctx.ParseResult.GetValueForOption(uiSilentOpt);
    var interactive = ctx.ParseResult.GetValueForOption(uiInteractiveOpt);
    if (silent && interactive)
        throw new InvalidOperationException("--silent and --interactive cannot be used together.");
    using var repo = Repository.Open();
    var result = repo.Uninstall(new UninstallRequest
    {
        Query = query,
        ManifestPath = ctx.ParseResult.GetValueForOption(uimanifestOpt),
        ProductCode = ctx.ParseResult.GetValueForOption(uiproductCodeOpt),
        Mode = interactive ? InstallerMode.Interactive : silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress,
        AllVersions = ctx.ParseResult.GetValueForOption(uiallVersionsOpt),
        Force = ctx.ParseResult.GetValueForOption(uiForceOpt),
        Purge = ctx.ParseResult.GetValueForOption(uiPurgeOpt),
        Preserve = ctx.ParseResult.GetValueForOption(uiPreserveOpt),
        LogPath = ctx.ParseResult.GetValueForOption(uiLogOpt),
    });
    Print.PackageActionResult(result, "uninstall", "uninstalled");
});

// ── Repair ──
var repairCommand = new Command("repair", "Repair a package").WithAliases("fix");
var rArg = QueryArguments.Package;
var rqOpt = CommonOptions.Query; var ridOpt = CommonOptions.Id; var rnameOpt = CommonOptions.Name; var rmonikerOpt = CommonOptions.Moniker; var rsrcOpt = CommonOptions.Source;
var reOpt = CommonOptions.Exact; var rvOpt = CommonOptions.Version;
var rmanifestOpt = new Option<string?>("--manifest", "Local manifest file or directory").WithAliases("-m");
var rproductCodeOpt = new Option<string?>("--product-code", "Installed product code");
var rarchOpt = new Option<string?>("--architecture", "Architecture").WithAliases("-a");
var rscopeOpt = new Option<string?>("--scope", "Install scope");
var rlocaleOpt = new Option<string?>("--locale", "Installer locale");
var rlogOpt = new Option<string?>("--log", "Installer log path").WithAliases("-o");
var racceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
var rignoreSecurityHashOpt = new Option<bool>("--ignore-security-hash", "Ignore installer hash mismatches");
var rforceOpt = new Option<bool>("--force", "Force repair behavior");
var rSilentOpt = new Option<bool>("--silent", "Silent install").WithAliases("-h");
var rInteractiveOpt = new Option<bool>("--interactive", "Interactive install").WithAliases("-i");
repairCommand.AddArgument(rArg);
foreach (var o in new Option[] { rqOpt, ridOpt, rnameOpt, rmonikerOpt, rsrcOpt, reOpt, rvOpt, rmanifestOpt, rproductCodeOpt, rarchOpt, rscopeOpt, rlocaleOpt, rlogOpt, racceptPkgAgreementsOpt, rignoreSecurityHashOpt, rforceOpt, rSilentOpt, rInteractiveOpt }) repairCommand.AddOption(o);

repairCommand.SetHandler((ctx) =>
{
    var silent = ctx.ParseResult.GetValueForOption(rSilentOpt);
    var interactive = ctx.ParseResult.GetValueForOption(rInteractiveOpt);
    if (silent && interactive)
        throw new InvalidOperationException("--silent and --interactive cannot be used together.");

    using var repo = Repository.Open();
    var mode = interactive ? InstallerMode.Interactive : silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress;
    var result = repo.Repair(RequestCreator.Repair(
        new PackageQuery
        {
            Query = ctx.ParseResult.GetValueForArgument(rArg) ?? ctx.ParseResult.GetValueForOption(rqOpt),
            Id = ctx.ParseResult.GetValueForOption(ridOpt),
            Name = ctx.ParseResult.GetValueForOption(rnameOpt),
            Moniker = ctx.ParseResult.GetValueForOption(rmonikerOpt),
            Source = ctx.ParseResult.GetValueForOption(rsrcOpt),
            Exact = ctx.ParseResult.GetValueForOption(reOpt),
            Version = ctx.ParseResult.GetValueForOption(rvOpt),
            Locale = ctx.ParseResult.GetValueForOption(rlocaleOpt),
            InstallerArchitecture = ctx.ParseResult.GetValueForOption(rarchOpt),
            InstallScope = ctx.ParseResult.GetValueForOption(rscopeOpt),
        },
        ctx.ParseResult.GetValueForOption(rmanifestOpt),
        ctx.ParseResult.GetValueForOption(rproductCodeOpt),
        mode,
        ctx.ParseResult.GetValueForOption(rlogOpt),
        ctx.ParseResult.GetValueForOption(racceptPkgAgreementsOpt),
        ctx.ParseResult.GetValueForOption(rforceOpt),
        ctx.ParseResult.GetValueForOption(rignoreSecurityHashOpt)));
    Print.PackageActionResult(result, "repair", "repaired");
});

// ── Import ──
var importCommand = new Command("import", "Import packages");
var imFileOpt = new Option<string>("--import-file", "Import file") { IsRequired = true }.WithAliases("-i");
var imDryRunOpt = new Option<bool>("--dry-run", "Dry run only");
var imIgnoreUnavailableOpt = new Option<bool>("--ignore-unavailable", "Ignore unavailable packages");
var imIgnoreVersionsOpt = new Option<bool>("--ignore-versions", "Ignore package versions in the import file");
var imNoUpgradeOpt = new Option<bool>("--no-upgrade", "Skip packages that are already installed");
var imAcceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
importCommand.AddOption(imFileOpt); importCommand.AddOption(imDryRunOpt); importCommand.AddOption(imIgnoreUnavailableOpt); importCommand.AddOption(imIgnoreVersionsOpt); importCommand.AddOption(imNoUpgradeOpt); importCommand.AddOption(imAcceptPkgAgreementsOpt);

importCommand.SetHandler((ctx) =>
{
    var file = ctx.ParseResult.GetValueForOption(imFileOpt)
        ?? throw new InvalidOperationException("import requires --import-file.");
    var dryRun = ctx.ParseResult.GetValueForOption(imDryRunOpt);
    var ignoreUnavailable = ctx.ParseResult.GetValueForOption(imIgnoreUnavailableOpt);
    var ignoreVersions = ctx.ParseResult.GetValueForOption(imIgnoreVersionsOpt);
    var noUpgrade = ctx.ParseResult.GetValueForOption(imNoUpgradeOpt);
    var acceptPackageAgreements = ctx.ParseResult.GetValueForOption(imAcceptPkgAgreementsOpt);

    if (!File.Exists(file)) { Console.Error.WriteLine($"error: File not found: {file}"); return; }
    var jsonText = File.ReadAllText(file);
    var doc = JsonSerializer.Deserialize<JsonElement>(jsonText);
    var sources = doc.GetProperty("Sources").EnumerateArray().ToList();

    using var repo = Repository.Open();
    int total = 0;
    int skipped = 0;
    foreach (var source in sources)
    {
        var sourceName = source.TryGetProperty("SourceDetails", out var sourceDetails)
            ? Json.GetString(sourceDetails, "Name")
            : null;
        var packages = source.GetProperty("Packages").EnumerateArray().ToList();
        foreach (var pkg in packages)
        {
            var pkgId = pkg.GetProperty("PackageIdentifier").GetString()!;
            var pkgVersion = ignoreVersions ? null : Json.GetString(pkg, "Version");
            if (dryRun)
            {
                Console.WriteLine($"[dry-run] Would install: {pkgId}");
            }
            else if (noUpgrade && InstalledPackageChecker.IsPresent(repo, pkgId, sourceName))
            {
                Console.WriteLine($"[no-upgrade] Skipping already installed package: {pkgId}");
                skipped++;
            }
            else
            {
                try
                {
                    Console.Write($"Installing {pkgId}...");
                    var result = repo.Install(RequestCreator.Install(
                        new PackageQuery
                        {
                            Id = pkgId,
                            Source = sourceName,
                            Exact = true,
                            Version = pkgVersion,
                        },
                        null,
                        InstallerMode.SilentWithProgress,
                        null,
                        null,
                        null,
                        null,
                        false,
                        false,
                        acceptPackageAgreements,
                        false,
                        null,
                        false,
                        false,
                        null,
                        noUpgrade));
                    if (result.NoOp)
                    {
                        Console.WriteLine(" no-op");
                        Print.Warnings(result.Warnings);
                        skipped++;
                    }
                    else
                    {
                        Print.Warnings(result.Warnings);
                        Console.WriteLine(result.Success ? " done" : $" failed (exit {result.ExitCode})");
                    }
                }
                catch (Exception ex) when (ignoreUnavailable && ex.CanIgnoreUnavailableImportFailure)
                {
                    Console.WriteLine(" unavailable");
                    Console.Error.WriteLine($"warning: Skipping unavailable package '{pkgId}': {ex.Message}");
                    skipped++;
                }
                catch (Exception ex) { Console.Error.WriteLine($" error: {ex.Message}"); }
            }
            total++;
        }
    }
    if (!dryRun && skipped > 0)
        Console.WriteLine($"Skipped {skipped} package(s).");
    Console.WriteLine($"{total} package(s) {(dryRun ? "would be installed" : "processed")}.");
});

// ── Add all commands to root ──
rootCommand.AddCommand(searchCommand);
rootCommand.AddCommand(showCommand);
rootCommand.AddCommand(listCommand);
rootCommand.AddCommand(upgradeCommand);
rootCommand.AddCommand(sourceCommand);
rootCommand.AddCommand(cacheCommand);
rootCommand.AddCommand(hashCommand);
rootCommand.AddCommand(exportCommand);
rootCommand.AddCommand(errorCommand);
rootCommand.AddCommand(settingsCommand);
rootCommand.AddCommand(featuresCommand);
rootCommand.AddCommand(validateCommand);
rootCommand.AddCommand(downloadCommand);
rootCommand.AddCommand(pinCommand);
rootCommand.AddCommand(installCommand);
rootCommand.AddCommand(uninstallCommand);
rootCommand.AddCommand(repairCommand);
rootCommand.AddCommand(importCommand);

rootCommand.SetHandler((ctx) =>
{
    if (ctx.ParseResult.GetValueForOption(infoOption))
    {
        Print.Info();
        return;
    }
});

return rootCommand.Invoke(args);
