using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Devolutions.Pinget.Core;

internal static class RestSource
{
    private static readonly string[] SupportedContracts =
        ["1.12.0", "1.10.0", "1.9.0", "1.7.0", "1.6.0", "1.5.0", "1.4.0", "1.1.0", "1.0.0"];

    internal record RestInformation
    {
        public string SourceIdentifier { get; init; } = "";
        public List<string> ServerSupportedVersions { get; init; } = [];
        public List<string> RequiredPackageMatchFields { get; init; } = [];
        public List<string> UnsupportedPackageMatchFields { get; init; } = [];
        public List<string> RequiredQueryParameters { get; init; } = [];
        public List<string> UnsupportedQueryParameters { get; init; } = [];
    }

    internal record RestInfoCache
    {
        public DateTime ExpiresAt { get; init; }
        public RestInformation Value { get; init; } = new();
    }

    public static RestInformation LoadInformation(HttpClient client, SourceRecord source, string? appRoot = null, bool identifierKeyed = false)
    {
        var stateDir = SourceStoreManager.SourceStateDir(source, appRoot, identifierKeyed);
        Directory.CreateDirectory(stateDir);
        var cachePath = Path.Combine(stateDir, "rest_info.json");

        // Try cache first
        if (File.Exists(cachePath))
        {
            try
            {
                var cacheJson = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize(cacheJson, RestSourceJsonContext.Default.RestInfoCache);
                if (cache is not null && cache.ExpiresAt > DateTime.UtcNow)
                    return cache.Value;
            }
            catch { /* cache invalid, refetch */ }
        }

        var url = $"{source.Arg.TrimEnd('/')}/information";
        using var response = client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        var json = document.RootElement;

        // The REST response wraps data in a "Data" property
        var data = json.TryGetProperty("Data", out var d) ? d : json;

        var info = new RestInformation
        {
            SourceIdentifier = data.TryGetProperty("SourceIdentifier", out var si) ? si.GetString() ?? "" : "",
            ServerSupportedVersions = data.TryGetProperty("ServerSupportedVersions", out var sv)
                ? sv.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
                : [],
            RequiredPackageMatchFields = data.TryGetProperty("RequiredPackageMatchFields", out var rpmf)
                ? rpmf.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
                : [],
            UnsupportedPackageMatchFields = data.TryGetProperty("UnsupportedPackageMatchFields", out var upmf)
                ? upmf.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
                : [],
            RequiredQueryParameters = data.TryGetProperty("RequiredQueryParameters", out var rqp)
                ? rqp.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
                : [],
            UnsupportedQueryParameters = data.TryGetProperty("UnsupportedQueryParameters", out var uqp)
                ? uqp.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
                : [],
        };

        // Cache for 1 hour
        var cacheEntry = new RestInfoCache { ExpiresAt = DateTime.UtcNow.AddHours(1), Value = info };
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cacheEntry, RestSourceJsonContext.Default.RestInfoCache));

        return info;
    }

    public static string? ChooseContract(IEnumerable<string> serverVersions)
    {
        var serverSet = new HashSet<string>(serverVersions);
        return SupportedContracts.FirstOrDefault(serverSet.Contains);
    }

    public static (List<RestMatchResult> Results, bool Truncated) Search(
        HttpClient client, SourceRecord source, PackageQuery query,
        RestInformation info, SearchSemantics semantics)
    {
        var contract = ChooseContract(info.ServerSupportedVersions)
            ?? throw new InvalidOperationException($"No compatible REST contract for {source.Name}");

        var url = $"{source.Arg.TrimEnd('/')}/manifestSearch";
        var body = BuildSearchBody(query, info, semantics);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("Version", contract);

        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(responseBody);
        var json = document.RootElement;

        var data = json.TryGetProperty("Data", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().ToList()
            : [];

        int maxResults = SourceFetchResults(query, semantics);
        var results = new List<RestMatchResult>();

        foreach (var item in data)
        {
            var packageId = item.GetProperty("PackageIdentifier").GetString() ?? "";
            var packageName = item.TryGetProperty("PackageName", out var pn) ? pn.GetString() ?? "" : packageId;
            var versions = ParseVersions(item);
            var (packageFamilyNames, productCodes, upgradeCodes) = ParseVersionIdentities(item);
            SortVersionsDesc(versions);
            var latest = versions.FirstOrDefault() ?? new VersionKey { Version = "?", Channel = "" };

            results.Add(new RestMatchResult
            {
                PackageId = packageId,
                PackageName = packageName,
                Moniker = item.TryGetProperty("Moniker", out var mk) ? mk.GetString() : null,
                LatestVersion = latest,
                Versions = versions,
                PackageFamilyNames = packageFamilyNames,
                ProductCodes = productCodes,
                UpgradeCodes = upgradeCodes,
                MatchCriteria = DetermineRestMatchCriteria(item, query, semantics)
            });
        }

        return (results, results.Count >= maxResults);
    }

    public static (Manifest Manifest, object StructuredDocuments) FetchManifestWithDocuments(HttpClient client, SourceRecord source,
        RestInformation info, string packageId, string version, string channel)
    {
        var contract = ChooseContract(info.ServerSupportedVersions) ?? "1.0.0";
        var url = BuildManifestRequestUrl(source, info, packageId, version, channel);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Version", contract);

        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        var json = document.RootElement;

        return ParseRestManifest(json, packageId, version, channel);
    }

    public static string UpdateRest(HttpClient client, SourceRecord source, string? appRoot = null, bool identifierKeyed = false)
    {
        var info = LoadInformation(client, source, appRoot, identifierKeyed);
        source.SourceVersion = info.ServerSupportedVersions.FirstOrDefault();
        return $"REST source up to date (contract: {source.SourceVersion})";
    }

    private static string BuildSearchBody(PackageQuery query, RestInformation info, SearchSemantics semantics)
    {
        var obj = new JsonObject();
        int maxResults = SourceFetchResults(query, semantics);
        obj["MaximumResults"] = maxResults;

        var filters = new JsonArray();
        void AddFilter(string field, string value, string matchType)
        {
            filters.Add((JsonNode?)new JsonObject
            {
                ["PackageMatchField"] = field,
                ["RequestMatch"] = new JsonObject
                {
                    ["KeyWord"] = value,
                    ["MatchType"] = matchType
                }
            });
        }

        string matchType = query.Exact ? "Exact" : "Substring";

        if (query.Query is not null && semantics == SearchSemantics.Single)
        {
            // For single-match semantics, use filters only (no Query field)
            AddFilter("PackageIdentifier", query.Query, "Exact");
            AddFilter("PackageName", query.Query, "Exact");
            AddFilter("Moniker", query.Query, "Exact");
            AppendRequiredFilters(filters, info);
            obj["Filters"] = filters;
            return obj.ToJsonString();
        }

        if (query.Query is not null)
            obj["Query"] = new JsonObject { ["KeyWord"] = query.Query, ["MatchType"] = matchType };

        if (query.Id is not null) AddFilter("PackageIdentifier", query.Id, matchType);
        if (query.Name is not null) AddFilter("PackageName", query.Name, matchType);
        if (query.Moniker is not null) AddFilter("Moniker", query.Moniker, matchType);
        if (query.Tag is not null) AddFilter("Tag", query.Tag, "Exact");
        if (query.Command is not null) AddFilter("Command", query.Command, "Exact");

        AppendRequiredFilters(filters, info);

        if (filters.Count > 0)
            obj["Filters"] = filters;

        return obj.ToJsonString();
    }

    private static void AppendRequiredFilters(JsonArray filters, RestInformation info)
    {
        if (info.RequiredPackageMatchFields.Any(f => f.Equals("market", StringComparison.OrdinalIgnoreCase)))
        {
            var market = DefaultMarket();
            filters.Add((JsonNode?)new JsonObject
            {
                ["PackageMatchField"] = "Market",
                ["RequestMatch"] = new JsonObject
                {
                    ["KeyWord"] = market,
                    ["MatchType"] = "Exact"
                }
            });
        }
    }

    internal static string DefaultMarket()
    {
        foreach (var varName in new[] { "PINGET_MARKET", "WINGET_RS_MARKET", "WINGET_DOTNET_MARKET" })
        {
            var value = Environment.GetEnvironmentVariable(varName)?.Trim();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return OsGeographicRegion() ?? "US";
    }

    private static string? OsGeographicRegion()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var geo = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
                var name = (geo?.GetValue("Name") as string)?.Trim();
                if (name is { Length: 2 } && char.IsAsciiLetter(name[0]) && char.IsAsciiLetter(name[1]))
                    return name.ToUpperInvariant();
            }
            catch
            {
            }
        }

        return null;
    }

    internal static List<(string Key, string Value)> BuildManifestQueryParameters(string version, string channel, RestInformation info)
    {
        var parameters = new List<(string Key, string Value)>();
        if (!string.IsNullOrWhiteSpace(version) &&
            !IsUnknownVersion(version) &&
            !info.UnsupportedQueryParameters.Any(p => p.Equals("Version", StringComparison.OrdinalIgnoreCase)))
        {
            parameters.Add(("Version", version));
        }

        if (!string.IsNullOrWhiteSpace(channel) &&
            !info.UnsupportedQueryParameters.Any(p => p.Equals("Channel", StringComparison.OrdinalIgnoreCase)))
        {
            parameters.Add(("Channel", channel));
        }

        if (info.RequiredQueryParameters.Any(p => p.Equals("Market", StringComparison.OrdinalIgnoreCase)) &&
            !info.UnsupportedQueryParameters.Any(p => p.Equals("Market", StringComparison.OrdinalIgnoreCase)))
        {
            parameters.Add(("Market", DefaultMarket()));
        }

        return parameters;
    }

    internal static string BuildManifestRequestUrl(SourceRecord source, RestInformation info, string packageId, string version, string channel)
    {
        var baseUrl = $"{source.Arg.TrimEnd('/')}/packageManifests/{Uri.EscapeDataString(packageId)}";
        var queryParams = BuildManifestQueryParameters(version, channel, info);
        if (queryParams.Count == 0)
            return baseUrl;

        var queryString = string.Join("&", queryParams.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return $"{baseUrl}?{queryString}";
    }

    private static int SourceFetchResults(PackageQuery query, SearchSemantics semantics)
    {
        if (semantics == SearchSemantics.Single)
            return 2;
        return Math.Max(query.Count ?? 50, 50);
    }

    private static List<VersionKey> ParseVersions(JsonElement item)
    {
        if (!item.TryGetProperty("Versions", out var versionsArr) || versionsArr.ValueKind != JsonValueKind.Array)
            return [];

        return versionsArr.EnumerateArray()
            .Select(v => new VersionKey
            {
                Version = v.TryGetProperty("PackageVersion", out var pv) ? pv.GetString() ?? "" : "",
                Channel = v.TryGetProperty("Channel", out var ch) ? ch.GetString() ?? "" : ""
            })
            .Where(v => !string.IsNullOrEmpty(v.Version))
            .ToList();
    }

    private static (List<string> PackageFamilyNames, List<string> ProductCodes, List<string> UpgradeCodes) ParseVersionIdentities(JsonElement item)
    {
        var packageFamilyNames = new List<string>();
        var productCodes = new List<string>();
        var upgradeCodes = new List<string>();

        if (!item.TryGetProperty("Versions", out var versionsArr) || versionsArr.ValueKind != JsonValueKind.Array)
            return (packageFamilyNames, productCodes, upgradeCodes);

        static void AddDistinctStrings(JsonElement parent, string prop, List<string> target)
        {
            if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in arr.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.String) continue;
                var val = elem.GetString();
                if (string.IsNullOrWhiteSpace(val)) continue;
                if (!target.Any(existing => existing.Equals(val, StringComparison.OrdinalIgnoreCase)))
                    target.Add(val);
            }
        }

        foreach (var v in versionsArr.EnumerateArray())
        {
            AddDistinctStrings(v, "PackageFamilyNames", packageFamilyNames);
            AddDistinctStrings(v, "ProductCodes", productCodes);
            AddDistinctStrings(v, "UpgradeCodes", upgradeCodes);
        }

        return (packageFamilyNames, productCodes, upgradeCodes);
    }

    internal static (Manifest Manifest, object StructuredDocuments) ParseRestManifest(JsonElement json, string packageId, string version, string channel)
    {
        var data = json.TryGetProperty("Data", out var d) ? d : json;

        // REST manifests can have DefaultLocale + Versions structure
        var defaultLocale = data.TryGetProperty("DefaultLocale", out var dl) ? dl : data;
        var versionsArr = data.TryGetProperty("Versions", out var va) ? va : default;

        // Try to find the matching version data, or fall back to the highest version in the payload
        JsonElement? versionData = null;
        if (versionsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in versionsArr.EnumerateArray())
            {
                var pv = v.TryGetProperty("PackageVersion", out var pvVal) ? pvVal.GetString() : null;
                var ch = v.TryGetProperty("Channel", out var chVal) ? chVal.GetString() ?? "" : "";
                if (string.Equals(pv, version, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ch, channel, StringComparison.OrdinalIgnoreCase))
                {
                    versionData = v;
                    break;
                }
            }

            if (versionData is null)
            {
                if (!string.IsNullOrWhiteSpace(version) && !IsUnknownVersion(version))
                    throw new InvalidOperationException($"REST manifest response did not contain a version payload for version '{version}'.");

                foreach (var v in versionsArr.EnumerateArray())
                {
                    var ch = v.TryGetProperty("Channel", out var chVal) ? chVal.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(channel) && !string.Equals(ch, channel, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (versionData is null)
                    {
                        versionData = v;
                        continue;
                    }

                    var candidateVer = v.TryGetProperty("PackageVersion", out var cv) ? cv.GetString() ?? "" : "";
                    var currentVer = versionData.Value.TryGetProperty("PackageVersion", out var curV) ? curV.GetString() ?? "" : "";
                    if (CompareVersionStrings(candidateVer, currentVer) > 0)
                        versionData = v;
                }

                if (versionData is null)
                    throw new InvalidOperationException("REST manifest response did not contain a version payload.");
            }

            if (versionData is { } selectedVersion && selectedVersion.TryGetProperty("DefaultLocale", out var vdl))
                defaultLocale = vdl;
        }

        var resolvedVersion = versionData is { } svVer &&
                              svVer.TryGetProperty("PackageVersion", out var resolvedPv) &&
                              resolvedPv.GetString() is { Length: > 0 } actualVersion
            ? actualVersion
            : version;
        var resolvedChannel = versionData is { } svCh &&
                              svCh.TryGetProperty("Channel", out var resolvedCh) &&
                              resolvedCh.ValueKind == JsonValueKind.String
            ? resolvedCh.GetString() ?? channel
            : channel;

        string GetStr(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

        string? GetOptStr(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        List<string> GetStrArray(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return [];
            return v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList();
        }

        List<NestedInstallerFile> ReadNestedInstallerFilesJson(JsonElement el)
        {
            if (!el.TryGetProperty("NestedInstallerFiles", out var v) || v.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<NestedInstallerFile>();
            foreach (var entry in v.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var relative = GetOptStr(entry, "RelativeFilePath");
                if (string.IsNullOrWhiteSpace(relative)) continue;
                result.Add(new NestedInstallerFile
                {
                    RelativeFilePath = relative!,
                    PortableCommandAlias = GetOptStr(entry, "PortableCommandAlias"),
                });
            }
            return result;
        }

        List<string> GetPackageDependencies(JsonElement el)
        {
            var direct = GetStrArray(el, "PackageDependencies");
            if (direct.Count > 0)
                return direct;

            if (!el.TryGetProperty("Dependencies", out var dependencies) ||
                dependencies.ValueKind != JsonValueKind.Object ||
                !dependencies.TryGetProperty("PackageDependencies", out var packageDependencies) ||
                packageDependencies.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var dependency in packageDependencies.EnumerateArray())
            {
                if (dependency.ValueKind == JsonValueKind.String)
                    result.Add(dependency.GetString() ?? "");
                else if (dependency.ValueKind == JsonValueKind.Object &&
                         dependency.TryGetProperty("PackageIdentifier", out var packageIdentifier))
                    result.Add(packageIdentifier.GetString() ?? "");
            }

            return result.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        InstallerSwitches GetSwitches(JsonElement el)
        {
            if (!el.TryGetProperty("InstallerSwitches", out var switches) && !el.TryGetProperty("Switches", out switches))
                return new InstallerSwitches();
            if (switches.ValueKind != JsonValueKind.Object)
                return new InstallerSwitches();

            string? GetSwitch(string name) =>
                switches.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            return new InstallerSwitches
            {
                Silent = GetSwitch("Silent"),
                SilentWithProgress = GetSwitch("SilentWithProgress"),
                Interactive = GetSwitch("Interactive"),
                Custom = GetSwitch("Custom"),
                Log = GetSwitch("Log"),
                InstallLocation = GetSwitch("InstallLocation"),
            };
        }

        bool GetBool(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) &&
            (v.ValueKind == JsonValueKind.True ||
             (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

        var installers = new List<Installer>();
        var installersSource = versionData ?? data;
        var defaultSwitches = GetSwitches(installersSource);
        var topPlatforms = GetStrArray(installersSource, "Platform");
        var topMinimumOsVersion = GetOptStr(installersSource, "MinimumOSVersion");
        var topInstallerType = GetOptStr(installersSource, "InstallerType");
        var topNestedInstallerType = GetOptStr(installersSource, "NestedInstallerType") ?? GetOptStr(data, "NestedInstallerType");
        var topMsStoreProductIdentifier = GetOptStr(installersSource, "MSStoreProductIdentifier");
        if (installersSource.TryGetProperty("Installers", out var instArr) && instArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var inst in instArr.EnumerateArray())
            {
                var nestedFiles = ReadNestedInstallerFilesJson(inst);
                if (nestedFiles.Count == 0)
                    nestedFiles = ReadNestedInstallerFilesJson(installersSource);
                if (nestedFiles.Count == 0)
                    nestedFiles = ReadNestedInstallerFilesJson(data);
                var platforms = GetStrArray(inst, "Platform");
                installers.Add(new Installer
                {
                    Platforms = platforms.Count > 0 ? platforms : [.. topPlatforms],
                    MinimumOsVersion = GetOptStr(inst, "MinimumOSVersion") ?? topMinimumOsVersion,
                    Architecture = GetOptStr(inst, "Architecture"),
                    InstallerType = GetOptStr(inst, "InstallerType") ?? topInstallerType,
                    NestedInstallerType = GetOptStr(inst, "NestedInstallerType") ?? topNestedInstallerType,
                    NestedInstallerFiles = nestedFiles,
                    Url = GetOptStr(inst, "InstallerUrl"),
                    Sha256 = GetOptStr(inst, "InstallerSha256"),
                    ProductCode = GetOptStr(inst, "ProductCode"),
                    MSStoreProductIdentifier = GetOptStr(inst, "MSStoreProductIdentifier") ?? topMsStoreProductIdentifier,
                    Locale = GetOptStr(inst, "InstallerLocale"),
                    Scope = GetOptStr(inst, "Scope"),
                    ReleaseDate = GetOptStr(inst, "ReleaseDate"),
                    PackageFamilyName = GetOptStr(inst, "PackageFamilyName"),
                    UpgradeCode = GetOptStr(inst, "UpgradeCode"),
                    Switches = GetSwitches(inst).MergeWith(defaultSwitches),
                    Commands = GetStrArray(inst, "Commands"),
                    PackageDependencies = GetPackageDependencies(inst),
                    RequireExplicitUpgrade = GetBool(inst, "RequireExplicitUpgrade"),
                });
            }
        }

        var docs = new List<Documentation>();
        if (defaultLocale.TryGetProperty("Documentations", out var docsArr) && docsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docsArr.EnumerateArray())
            {
                var url = GetOptStr(doc, "DocumentUrl");
                if (url is not null)
                    docs.Add(new Documentation { Label = GetOptStr(doc, "DocumentLabel"), Url = url });
            }
        }

        var agreements = new List<PackageAgreement>();
        if (defaultLocale.TryGetProperty("Agreements", out var agreementsArr) && agreementsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var agreement in agreementsArr.EnumerateArray())
            {
                agreements.Add(new PackageAgreement
                {
                    Label = GetOptStr(agreement, "AgreementLabel"),
                    Text = GetOptStr(agreement, "Agreement"),
                    Url = GetOptStr(agreement, "AgreementUrl"),
                });
            }
        }

        List<PackageIcon> GetIcons(JsonElement locale)
        {
            var result = new List<PackageIcon>();
            if (!locale.TryGetProperty("Icons", out var iconsArr) || iconsArr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var iconElement in iconsArr.EnumerateArray())
            {
                if (iconElement.ValueKind != JsonValueKind.Object)
                    continue;

                var icon = new PackageIcon
                {
                    IconUrl = GetOptStr(iconElement, "IconUrl"),
                    IconFileType = GetOptStr(iconElement, "IconFileType"),
                    IconResolution = GetOptStr(iconElement, "IconResolution"),
                    IconTheme = GetOptStr(iconElement, "IconTheme"),
                    IconSha256 = GetOptStr(iconElement, "IconSha256"),
                };
                if (!string.IsNullOrWhiteSpace(icon.IconUrl) ||
                    !string.IsNullOrWhiteSpace(icon.IconFileType) ||
                    !string.IsNullOrWhiteSpace(icon.IconResolution) ||
                    !string.IsNullOrWhiteSpace(icon.IconTheme) ||
                    !string.IsNullOrWhiteSpace(icon.IconSha256))
                {
                    result.Add(icon);
                }
            }

            return result;
        }

        var icons = GetIcons(defaultLocale);
        if (icons.Count == 0 && data.TryGetProperty("DefaultLocale", out var packageDefaultLocale))
            icons = GetIcons(packageDefaultLocale);

        var topLevelRequireExplicit = GetBool(installersSource, "RequireExplicitUpgrade");
        var anyInstallerRequireExplicit = installers.Any(i => i.RequireExplicitUpgrade);

        var manifest = new Manifest
        {
            Id = GetStr(data, "PackageIdentifier").Length > 0 ? GetStr(data, "PackageIdentifier") : packageId,
            Name = GetOptStr(defaultLocale, "PackageName") ?? GetStr(data, "PackageName"),
            Version = resolvedVersion,
            Channel = resolvedChannel,
            Publisher = GetOptStr(defaultLocale, "Publisher"),
            Description = GetOptStr(defaultLocale, "Description") ?? GetOptStr(defaultLocale, "ShortDescription"),
            ShortDescription = GetOptStr(defaultLocale, "ShortDescription"),
            Moniker = GetOptStr(defaultLocale, "Moniker") ?? GetOptStr(data, "Moniker"),
            PackageUrl = GetOptStr(defaultLocale, "PackageUrl"),
            PublisherUrl = GetOptStr(defaultLocale, "PublisherUrl"),
            PublisherSupportUrl = GetOptStr(defaultLocale, "PublisherSupportUrl"),
            License = GetOptStr(defaultLocale, "License"),
            LicenseUrl = GetOptStr(defaultLocale, "LicenseUrl"),
            PrivacyUrl = GetOptStr(defaultLocale, "PrivacyUrl"),
            Author = GetOptStr(defaultLocale, "Author"),
            Copyright = GetOptStr(defaultLocale, "Copyright"),
            CopyrightUrl = GetOptStr(defaultLocale, "CopyrightUrl"),
            ReleaseNotes = GetOptStr(defaultLocale, "ReleaseNotes"),
            ReleaseNotesUrl = GetOptStr(defaultLocale, "ReleaseNotesUrl"),
            Tags = GetStrArray(defaultLocale, "Tags"),
            Agreements = agreements,
            PackageDependencies = GetPackageDependencies(installersSource),
            Documentation = docs,
            Icons = icons,
            Installers = installers,
            RequireExplicitUpgrade = topLevelRequireExplicit || anyInstallerRequireExplicit,
        };

        return (manifest, BuildStructuredDocuments(data, installersSource, defaultLocale, packageId, resolvedVersion, resolvedChannel));
    }

    private static object BuildStructuredDocuments(JsonElement data, JsonElement installerSource, JsonElement defaultLocale, string packageId, string version, string channel)
    {
        var packageIdentifier = GetString(data, "PackageIdentifier") ?? packageId;
        var packageLocale = GetString(defaultLocale, "PackageLocale") ?? "en-US";
        var manifestVersion = GetString(installerSource, "ManifestVersion")
            ?? GetString(defaultLocale, "ManifestVersion")
            ?? GetString(data, "ManifestVersion")
            ?? "1.10.0";

        var defaultLocaleDocument = CloneObject(defaultLocale);
        defaultLocaleDocument["PackageIdentifier"] = packageIdentifier;
        defaultLocaleDocument["PackageVersion"] = version;
        defaultLocaleDocument["PackageLocale"] = packageLocale;
        defaultLocaleDocument["ManifestType"] = "defaultLocale";
        defaultLocaleDocument["ManifestVersion"] = manifestVersion;

        var versionDocument = new Dictionary<string, object?>
        {
            ["PackageIdentifier"] = packageIdentifier,
            ["PackageVersion"] = version,
            ["DefaultLocale"] = packageLocale,
            ["ManifestType"] = "version",
            ["ManifestVersion"] = manifestVersion,
        };

        var installerDocument = CloneObject(installerSource);
        installerDocument.Remove("DefaultLocale");
        installerDocument.Remove("Locales");
        installerDocument["PackageIdentifier"] = packageIdentifier;
        installerDocument["PackageVersion"] = version;
        if (!string.IsNullOrWhiteSpace(channel))
            installerDocument["Channel"] = channel;
        installerDocument["ManifestType"] = "installer";
        installerDocument["ManifestVersion"] = manifestVersion;

        var documents = new List<Dictionary<string, object?>>
        {
            versionDocument,
            defaultLocaleDocument,
            installerDocument,
        };

        if (installerSource.TryGetProperty("Locales", out var locales) && locales.ValueKind == JsonValueKind.Array)
        {
            foreach (var locale in locales.EnumerateArray())
            {
                var localeDocument = CloneObject(locale);
                localeDocument["PackageIdentifier"] = packageIdentifier;
                localeDocument["PackageVersion"] = version;
                localeDocument["ManifestType"] = "locale";
                localeDocument["ManifestVersion"] = manifestVersion;
                documents.Add(localeDocument);
            }
        }

        return documents;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, object?> CloneObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            result[property.Name] = NormalizeJsonValue(property.Value);
        return result;
    }

    private static object? NormalizeJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => CloneObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJsonValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    private static string? DetermineRestMatchCriteria(JsonElement item, PackageQuery query, SearchSemantics semantics)
    {
        if (query.Tag is not null) return $"Tag: {query.Tag}";
        if (query.Command is not null) return $"Command: {query.Command}";
        return null;
    }

    internal static void SortVersionsDesc(List<VersionKey> versions)
    {
        versions.Sort((a, b) => -CompareVersionStrings(a.Version, b.Version));
    }

    internal static bool IsUnknownVersion(string? version) =>
        version is not null && version.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    internal static int CompareVersionStrings(string a, string b)
    {
        bool aUnknown = IsUnknownVersion(a);
        bool bUnknown = IsUnknownVersion(b);
        if (aUnknown != bUnknown)
            return aUnknown ? -1 : 1;
        if (aUnknown)
            return 0;

        var aLessThan = StripLessThanPrefix(a);
        var bLessThan = StripLessThanPrefix(b);
        if (aLessThan is not null && bLessThan is not null)
            return CompareVersionStrings(aLessThan, bLessThan);
        if (aLessThan is not null && CompareVersionStrings(aLessThan, b) == 0)
            return -1;
        if (bLessThan is not null && CompareVersionStrings(a, bLessThan) == 0)
            return 1;

        var partsA = TokenizeVersion(a);
        var partsB = TokenizeVersion(b);
        int maxLen = Math.Max(partsA.Count, partsB.Count);
        for (int i = 0; i < maxLen; i++)
        {
            var pa = i < partsA.Count ? partsA[i] : "0";
            var pb = i < partsB.Count ? partsB[i] : "0";
            int cmp = CompareVersionToken(pa, pb);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    // Same rules as the Rust compare_version: numbers compare numerically; a text token against a
    // number (or a missing part, padded as "0") is a pre-release style suffix ranked below it, so
    // "1.0.0" > "1.0.0-rc1" and "1.0.1" > "1.0b"; text tokens compare ASCII case-insensitively.
    private static int CompareVersionToken(string a, string b)
    {
        bool numberA = IsNumericToken(a);
        bool numberB = IsNumericToken(b);
        if (numberA && numberB) return CompareNumericTokens(a, b);
        if (numberA) return 1;
        if (numberB) return -1;
        return string.CompareOrdinal(ToAsciiLower(a), ToAsciiLower(b));
    }

    private static bool IsNumericToken(string token)
    {
        foreach (var ch in token)
        {
            if (!char.IsAsciiDigit(ch))
                return false;
        }
        return token.Length > 0;
    }

    // Digit runs of any length: ignore leading zeros, then the longer run is larger.
    private static int CompareNumericTokens(string a, string b)
    {
        var trimmedA = a.TrimStart('0');
        var trimmedB = b.TrimStart('0');
        if (trimmedA.Length != trimmedB.Length)
            return trimmedA.Length.CompareTo(trimmedB.Length);
        return string.CompareOrdinal(trimmedA, trimmedB);
    }

    private static string ToAsciiLower(string value)
        => string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
                span[i] = char.IsAsciiLetterUpper(source[i]) ? (char)(source[i] | 0x20) : source[i];
        });

    /// <summary>
    /// Splits a version on '.', '-', '_' and '+', and between digit and non-digit runs, so
    /// "1.0.0-rc10" becomes ["1", "0", "0", "rc", "10"]. Mirrors the Rust tokenize_version.
    /// </summary>
    internal static List<string> TokenizeVersion(string value)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool? currentIsDigit = null;
        foreach (var ch in value)
        {
            if (ch is '.' or '-' or '_' or '+')
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                currentIsDigit = null;
                continue;
            }

            bool isDigit = char.IsAsciiDigit(ch);
            if (currentIsDigit is bool previous && previous != isDigit)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            current.Append(ch);
            currentIsDigit = isDigit;
        }

        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private static string? StripLessThanPrefix(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith('<') ? trimmed[1..].TrimStart() : null;
    }

    internal record RestMatchResult
    {
        public required string PackageId { get; init; }
        public required string PackageName { get; init; }
        public string? Moniker { get; init; }
        public required VersionKey LatestVersion { get; init; }
        public required List<VersionKey> Versions { get; init; }
        public List<string> PackageFamilyNames { get; init; } = [];
        public List<string> ProductCodes { get; init; } = [];
        public List<string> UpgradeCodes { get; init; } = [];
        public string? MatchCriteria { get; init; }
    }
}

[JsonSerializable(typeof(RestSource.RestInfoCache))]
internal partial class RestSourceJsonContext : JsonSerializerContext;
