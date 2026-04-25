using System.Text.Json;
using System.Text.Json.Nodes;

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
    }

    internal record RestInfoCache
    {
        public DateTime ExpiresAt { get; init; }
        public RestInformation Value { get; init; } = new();
    }

    public static RestInformation LoadInformation(HttpClient client, SourceRecord source, string? appRoot = null)
    {
        var stateDir = SourceStoreManager.SourceStateDir(source, appRoot);
        Directory.CreateDirectory(stateDir);
        var cachePath = Path.Combine(stateDir, "rest_info.json");

        // Try cache first
        if (File.Exists(cachePath))
        {
            try
            {
                var cacheJson = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<RestInfoCache>(cacheJson);
                if (cache is not null && cache.ExpiresAt > DateTime.UtcNow)
                    return cache.Value;
            }
            catch { /* cache invalid, refetch */ }
        }

        var url = $"{source.Arg.TrimEnd('/')}/information";
        using var response = client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var json = JsonSerializer.Deserialize<JsonElement>(body);

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
        };

        // Cache for 1 hour
        var cacheEntry = new RestInfoCache { ExpiresAt = DateTime.UtcNow.AddHours(1), Value = info };
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cacheEntry));

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
        var json = JsonSerializer.Deserialize<JsonElement>(responseBody);

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
            SortVersionsDesc(versions);
            var latest = versions.FirstOrDefault() ?? new VersionKey { Version = "?", Channel = "" };

            results.Add(new RestMatchResult
            {
                PackageId = packageId,
                PackageName = packageName,
                Moniker = item.TryGetProperty("Moniker", out var mk) ? mk.GetString() : null,
                LatestVersion = latest,
                Versions = versions,
                MatchCriteria = DetermineRestMatchCriteria(item, query, semantics)
            });
        }

        return (results, results.Count >= maxResults);
    }

    public static (Manifest Manifest, object StructuredDocuments) FetchManifestWithDocuments(HttpClient client, SourceRecord source,
        RestInformation info, string packageId, string version, string channel)
    {
        var contract = ChooseContract(info.ServerSupportedVersions) ?? "1.0.0";
        var url = $"{source.Arg.TrimEnd('/')}/packageManifests/{Uri.EscapeDataString(packageId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Version", contract);

        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var json = JsonSerializer.Deserialize<JsonElement>(body);

        return ParseRestManifest(json, packageId, version, channel);
    }

    public static string UpdateRest(HttpClient client, SourceRecord source, string? appRoot = null)
    {
        var info = LoadInformation(client, source, appRoot);
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
            filters.Add(new JsonObject
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
            var market = Environment.GetEnvironmentVariable("WINGET_DOTNET_MARKET") ?? "US";
            filters.Add(new JsonObject
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

    private static (Manifest Manifest, object StructuredDocuments) ParseRestManifest(JsonElement json, string packageId, string version, string channel)
    {
        var data = json.TryGetProperty("Data", out var d) ? d : json;

        // REST manifests can have DefaultLocale + Versions structure
        var defaultLocale = data.TryGetProperty("DefaultLocale", out var dl) ? dl : data;
        var versionsArr = data.TryGetProperty("Versions", out var va) ? va : default;

        // Try to find the matching version data
        JsonElement? versionData = null;
        if (versionsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in versionsArr.EnumerateArray())
            {
                var pv = v.TryGetProperty("PackageVersion", out var pvVal) ? pvVal.GetString() : null;
                if (pv == version)
                {
                    versionData = v;
                    if (v.TryGetProperty("DefaultLocale", out var vdl))
                        defaultLocale = vdl;
                    break;
                }
            }
        }

        string GetStr(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

        string? GetOptStr(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        List<string> GetStrArray(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return [];
            return v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList();
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

        var installers = new List<Installer>();
        var installersSource = versionData ?? data;
        var defaultSwitches = GetSwitches(installersSource);
        if (installersSource.TryGetProperty("Installers", out var instArr) && instArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var inst in instArr.EnumerateArray())
            {
                installers.Add(new Installer
                {
                    Architecture = GetOptStr(inst, "Architecture"),
                    InstallerType = GetOptStr(inst, "InstallerType"),
                    NestedInstallerType = GetOptStr(inst, "NestedInstallerType") ?? GetOptStr(data, "NestedInstallerType"),
                    Url = GetOptStr(inst, "InstallerUrl"),
                    Sha256 = GetOptStr(inst, "InstallerSha256"),
                    ProductCode = GetOptStr(inst, "ProductCode"),
                    Locale = GetOptStr(inst, "InstallerLocale"),
                    Scope = GetOptStr(inst, "Scope"),
                    ReleaseDate = GetOptStr(inst, "ReleaseDate"),
                    PackageFamilyName = GetOptStr(inst, "PackageFamilyName"),
                    UpgradeCode = GetOptStr(inst, "UpgradeCode"),
                    Switches = GetSwitches(inst).MergeWith(defaultSwitches),
                    Commands = GetStrArray(inst, "Commands"),
                    PackageDependencies = GetStrArray(inst, "PackageDependencies"),
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

        var manifest = new Manifest
        {
            Id = GetStr(data, "PackageIdentifier").Length > 0 ? GetStr(data, "PackageIdentifier") : packageId,
            Name = GetOptStr(defaultLocale, "PackageName") ?? GetStr(data, "PackageName"),
            Version = version,
            Channel = channel,
            Publisher = GetOptStr(defaultLocale, "Publisher"),
            Description = GetOptStr(defaultLocale, "Description") ?? GetOptStr(defaultLocale, "ShortDescription"),
            Moniker = GetOptStr(data, "Moniker"),
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
            Documentation = docs,
            Installers = installers,
        };

        return (manifest, BuildStructuredDocuments(data, installersSource, defaultLocale, packageId, version, channel));
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

    internal static int CompareVersionStrings(string a, string b)
    {
        var partsA = a.Split('.', '-');
        var partsB = b.Split('.', '-');
        int maxLen = Math.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < maxLen; i++)
        {
            var pa = i < partsA.Length ? partsA[i] : "0";
            var pb = i < partsB.Length ? partsB[i] : "0";
            if (long.TryParse(pa, out var na) && long.TryParse(pb, out var nb))
            {
                if (na != nb) return na.CompareTo(nb);
            }
            else
            {
                int cmp = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }
        return 0;
    }

    internal record RestMatchResult
    {
        public required string PackageId { get; init; }
        public required string PackageName { get; init; }
        public string? Moniker { get; init; }
        public required VersionKey LatestVersion { get; init; }
        public required List<VersionKey> Versions { get; init; }
        public string? MatchCriteria { get; init; }
    }
}
