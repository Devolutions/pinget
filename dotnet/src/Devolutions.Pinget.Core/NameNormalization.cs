using System.Text.RegularExpressions;

namespace Devolutions.Pinget.Core;

/// <summary>
/// Pinget port of winget-cli's <c>NameNormalization.cpp</c> (the "Initial"
/// version). Produces the same normalized name + publisher strings that
/// winget stores in the catalog's <c>norm_names2</c> / <c>norm_publishers2</c>
/// tables for an ARP entry's DisplayName and Publisher.
///
/// Without this, identity correlation only succeeds when the installed
/// display name happens to match the catalog's PackageName after our naive
/// alphanumeric normalization — winget can match many more entries because
/// it strips version-like tokens, locales, architectures, and legal-entity
/// suffixes before comparing. This class reproduces those transformations
/// so we can correlate the same set of ARP rows winget does.
/// </summary>
internal static partial class NameNormalization
{
    internal enum Architecture
    {
        Unknown,
        X86,
        X64,
    }

    internal readonly record struct NormalizedName(string Name, Architecture Architecture, string Locale);

    /// <summary>
    /// Normalizes an ARP DisplayName the same way winget's NameNormalizer
    /// (Initial version, preserveWhiteSpace = false) does — producing the
    /// string that ends up in the catalog's norm_names2 table.
    /// </summary>
    public static NormalizedName NormalizeName(string value)
    {
        var name = PrepareForValidation(value);
        while (Unwrap(ref name)) { }

        // SAP Business Object program names follow a specific pattern that
        // breaks under the regular flow; winget short-circuits them.
        if (SapPackage().IsMatch(name))
        {
            return new NormalizedName(name, Architecture.Unknown, string.Empty);
        }

        var architecture = RemoveArchitecture(ref name);
        var locale = RemoveLocale(ref name);

        // Preserve KB numbers from within parens before the bracket strippers
        // would eat them — winget keeps `KB1234567` as part of the normalized
        // name because it's the only meaningful identifier on some patches.
        name = KbNumbers().Replace(name, "$1");

        while (RemoveAll(ProgramNameRegexes, ref name)) { }

        var tokens = SplitWithLegalSuffixExclusion(ProgramNameSplit(), name, stopOnExclusion: false);
        name = string.Concat(tokens);
        name = NonLettersAndDigits().Replace(name, string.Empty);

        return new NormalizedName(name.ToLowerInvariant(), architecture, locale.ToLowerInvariant());
    }

    /// <summary>
    /// Normalizes a publisher string. Strips the same set of patterns as
    /// the name path plus splits on word boundaries with the
    /// legal-entity-suffix list — so "Microsoft Corporation" → "microsoft",
    /// "JetBrains s.r.o." → "jetbrains", but "The Git Development Community"
    /// stays intact because no token in it is a recognized suffix.
    /// </summary>
    public static string NormalizePublisher(string value)
    {
        var publisher = PrepareForValidation(value);
        while (Unwrap(ref publisher)) { }

        while (RemoveAll(PublisherNameRegexes, ref publisher)) { }

        // Publisher split stops at the FIRST legal-entity suffix it sees
        // (after the first token), so "Foo Inc Internal Sub Bar" keeps just
        // "Foo" — "Inc" cuts off everything beyond.
        var tokens = SplitWithLegalSuffixExclusion(PublisherNameSplit(), publisher, stopOnExclusion: true);
        publisher = string.Concat(tokens);
        publisher = NonLettersAndDigits().Replace(publisher, string.Empty);
        return publisher.ToLowerInvariant();
    }

    // ── Internal helpers ──────────────────────────────────────────────────

    private static string PrepareForValidation(string value)
    {
        var s = value.Trim();
        // winget supports an `@@`-delimited suffix on internal display names
        // that should be stripped before normalization — keep parity even
        // though it's unusual in the wild.
        var idx = s.IndexOf("@@", StringComparison.Ordinal);
        if (idx >= 3)
        {
            s = s[..idx];
        }
        return s;
    }

    private static bool Unwrap(ref string value)
    {
        if (value.Length < 2) return false;
        var first = value[0];
        var last = value[^1];
        var wrapped = first switch
        {
            '"' => last == '"',
            '(' => last == ')',
            _ => false,
        };
        if (!wrapped) return false;
        value = value[1..^1];
        return true;
    }

    private static bool Remove(Regex re, ref string value)
    {
        var replaced = re.Replace(value, string.Empty);
        if (replaced == value) return false;
        value = replaced;
        return true;
    }

    private static bool RemoveAll(IReadOnlyList<Regex> regexes, ref string value)
    {
        var changed = false;
        foreach (var re in regexes)
        {
            if (Remove(re, ref value)) changed = true;
        }
        return changed;
    }

    private static Architecture RemoveArchitecture(ref string value)
    {
        // Order matters: "32/64-bit" is a superstring of "64-bit"; "X64"/
        // "AMD64" must beat "X32"/"X86" because of "x86-64".
        if (Remove(Architecture32Or64Bit(), ref value))
            return Architecture.Unknown;
        if (Remove(ArchitectureX64(), ref value) || Remove(Architecture64Bit(), ref value))
            return Architecture.X64;
        if (Remove(ArchitectureX32(), ref value) || Remove(Architecture32Bit(), ref value))
            return Architecture.X86;
        return Architecture.Unknown;
    }

    private static string RemoveLocale(ref string value)
    {
        var matches = Locale().Matches(value);
        if (matches.Count == 0) return string.Empty;

        var newValue = new System.Text.StringBuilder(value.Length);
        string? localeFound = null;
        var lastEnd = 0;

        foreach (Match m in matches)
        {
            var folded = m.Value.ToUpperInvariant();
            var isKnown = Array.BinarySearch(Locales, folded) >= 0;
            newValue.Append(value, lastEnd, m.Index - lastEnd);
            if (!isKnown)
            {
                newValue.Append(m.Value);
            }
            else if (localeFound is null)
            {
                localeFound = folded;
            }
            else if (!string.Equals(localeFound, folded, StringComparison.Ordinal))
            {
                // Multiple distinct locales: keep only if the language matches.
                var existingLang = localeFound.Split('-')[0];
                var newLang = folded.Split('-')[0];
                if (!string.Equals(existingLang, newLang, StringComparison.Ordinal))
                {
                    localeFound = string.Empty;
                }
            }
            lastEnd = m.Index + m.Length;
        }
        newValue.Append(value, lastEnd, value.Length - lastEnd);
        value = newValue.ToString();
        return localeFound ?? string.Empty;
    }

    private static List<string> SplitWithLegalSuffixExclusion(Regex re, string value, bool stopOnExclusion)
    {
        var result = new List<string>();
        var lastEnd = 0;

        bool PushSegment(string segment)
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0) return true;
            var folded = trimmed.ToUpperInvariant();
            if (result.Count > 0 && Array.BinarySearch(LegalEntitySuffixes, folded) >= 0)
            {
                return !stopOnExclusion;
            }
            result.Add(trimmed);
            return true;
        }

        foreach (Match m in re.Matches(value))
        {
            var segment = value.Substring(lastEnd, m.Index - lastEnd);
            if (!PushSegment(segment)) return result;
            lastEnd = m.Index + m.Length;
        }
        PushSegment(value[lastEnd..]);
        return result;
    }

    // ── Regex patterns. Source-generated for NativeAOT compatibility —
    // RegexOptions.Compiled would silently fall back to the interpreter
    // under AOT, but [GeneratedRegex] produces specialized code at compile
    // time. .NET regex supports variable-length lookbehind directly, so the
    // C++ patterns port over unchanged. ──────────────────────────────────

    [GeneratedRegex(@"(?<=^|[^\p{L}\p{Nd}])(X32|X86)(?=\P{Nd}|$)(?:\sEDITION)?", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureX32();
    [GeneratedRegex(@"(?<=^|[^\p{L}\p{Nd}])(X64|AMD64|X86([\p{Pd}\p{Pc}]64))(?=\P{Nd}|$)(?:\sEDITION)?", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureX64();
    [GeneratedRegex(@"(?<=^|[^\p{L}\p{Nd}])(32[\p{Pd}\p{Pc}\p{Z}]?BIT)S?(?:\sEDITION)?", RegexOptions.IgnoreCase)]
    private static partial Regex Architecture32Bit();
    [GeneratedRegex(@"(?<=^|[^\p{L}\p{Nd}])(64[\p{Pd}\p{Pc}\p{Z}]?BIT)S?(?:\sEDITION)?", RegexOptions.IgnoreCase)]
    private static partial Regex Architecture64Bit();
    [GeneratedRegex(@"(?<=^|[^\p{L}\p{Nd}])((64[\\/]32|32[\\/]64)[\p{Pd}\p{Pc}\p{Z}]?BIT)S?(?:\sEDITION)?", RegexOptions.IgnoreCase)]
    private static partial Regex Architecture32Or64Bit();

    [GeneratedRegex(@"(?<![A-Z])((?:\p{Lu}{2,3}(-(CANS|CYRL|LATN|MONG))?-\p{Lu}{2})(?![A-Z])(?:-VALENCIA)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Locale();

    [GeneratedRegex(@"^(?:[\p{Lu}\p{Nd}]+[\._])+[\p{Lu}\p{Nd}]+(?:-(?:\p{Nd}+\.)+\p{Nd}+)(?:-(?:\p{Lu}{2}(?:_\p{Lu}{2})?|CORE))(?:-(?:\p{Lu}{2}|\p{Nd}{2}))$", RegexOptions.IgnoreCase)]
    private static partial Regex SapPackage();

    [GeneratedRegex(@"\((KB\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex KbNumbers();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]", RegexOptions.IgnoreCase)]
    private static partial Regex NonLettersAndDigits();

    [GeneratedRegex(@"(?<!\p{L})(?:http[s]?|ftp)://", RegexOptions.IgnoreCase)]
    private static partial Regex UriProtocol();
    [GeneratedRegex(@"((?<!\p{L})(?:V|VER|VERSI(?:O|Ó)N|VERSÃO|VERSIE|WERSJA|BUILD|RELEASE|RC|SP)\P{L}?)?\p{Nd}+([\p{Po}\p{Pd}\p{Pc}]\p{Nd}?(RC|B|A|R|SP|K)?\p{Nd}+)+([\p{Po}\p{Pd}\p{Pc}]?[\p{L}\p{Nd}]+)*", RegexOptions.IgnoreCase)]
    private static partial Regex VersionDelimited();
    [GeneratedRegex(@"(FOR\s)?(?<!\p{L})(?:P|V|R|VER|VERSI(?:O|Ó)N|VERSÃO|VERSIE|WERSJA|BUILD|RELEASE|RC|SP)(?:\P{L}|\P{L}\p{L})?(\p{Nd}|\.\p{Nd})+(?:RC|B|A|R|V|SP)?\p{Nd}?", RegexOptions.IgnoreCase)]
    private static partial Regex Version();
    [GeneratedRegex(@"(?<!\p{L})(?:(?:V|VER|VERSI(?:O|Ó)N|VERSÃO|VERSIE|WERSJA|BUILD|RELEASE|RC|SP)\P{L})?\p{Lu}\p{Nd}+(?:[\p{Po}\p{Pd}\p{Pc}]\p{Nd}+)+", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLetter();
    [GeneratedRegex(@"\([^\(\)]*\)|\[[^\[\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex NonNestedBracket();
    [GeneratedRegex("(?:\\p{Ps}.*\\p{Pe}|\".*\")", RegexOptions.IgnoreCase)]
    private static partial Regex BracketEnclosed();
    [GeneratedRegex(@"^[^\p{L}\p{Nd}]+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSymbols();
    [GeneratedRegex(@"\P{L}+$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingNonLetters();
    [GeneratedRegex(@"^\(.*?\)", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixParens();
    [GeneratedRegex("(\\(\\s*\\)|\\[\\s*\\]|\"\\s*\")", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyParens();
    [GeneratedRegex(@"\sEN\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EnSuffix();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSymbols();
    [GeneratedRegex(@"((INSTALLED\sAT|IN)\s)?[CDEF]:\\(.+?\\)*[^\s]*\\?", RegexOptions.IgnoreCase)]
    private static partial Regex FilePath();
    [GeneratedRegex(@"\(CHANGE\s#\d{1,2}\sTO\s[CDEF]:\\(.+?\\)*[^\s]*\\?\)", RegexOptions.IgnoreCase)]
    private static partial Regex FilePathGhs();
    [GeneratedRegex(@"\([CDEF]:\\(.+?\\)*[^\s]*\\?\)", RegexOptions.IgnoreCase)]
    private static partial Regex FilePathParens();
    [GeneratedRegex("\"[CDEF]:\\\\(.+?\\\\)*[^\\s]*\\\\?\"", RegexOptions.IgnoreCase)]
    private static partial Regex FilePathQuotes();
    [GeneratedRegex(@"(?<=^ROBLOX\s(PLAYER|STUDIO))(\sFOR\s.*)", RegexOptions.IgnoreCase)]
    private static partial Regex Roblox();
    [GeneratedRegex(@"(?<=^BOMGAR\s(JUMP\sCLIENT|(ACCESS|REPRESENTATIVE)\sCONSOLE|BUTTON)|^EMBEDDED\sCALLBACK)(\s.*)", RegexOptions.IgnoreCase)]
    private static partial Regex Bomgar();
    [GeneratedRegex(@"(?:(?<=^\p{L})|(?<=\P{L}\p{L}))(\.|/)(?=\p{L}(?:\P{L}|$))", RegexOptions.IgnoreCase)]
    private static partial Regex AcronymSeparators();
    [GeneratedRegex(@"(?<=^|\s)[^\p{L}]+(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NonLetters();
    [GeneratedRegex(@"[^\p{L}\p{Nd}\+\&]", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramNameSplit();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]", RegexOptions.IgnoreCase)]
    private static partial Regex PublisherNameSplit();

    // Source-generated regex methods return cached singletons, so building
    // these arrays once is just storing the cached references.
    private static readonly Regex[] ProgramNameRegexes =
    [
        Roblox(), Bomgar(), PrefixParens(), EmptyParens(),
        FilePathGhs(), FilePathParens(), FilePathQuotes(), FilePath(),
        VersionLetter(), VersionDelimited(), Version(), EnSuffix(),
        NonNestedBracket(), BracketEnclosed(), UriProtocol(),
        LeadingSymbols(), TrailingSymbols(),
    ];

    private static readonly Regex[] PublisherNameRegexes =
    [
        VersionDelimited(), Version(), NonNestedBracket(), BracketEnclosed(),
        UriProtocol(), NonLetters(), TrailingNonLetters(), AcronymSeparators(),
    ];

    // ── Locale + legal-entity-suffix lists ─────────────────────────────────

    // Pre-uppercased and pre-sorted so BinarySearch works against the
    // regex's uppercase output.
    private static readonly string[] Locales =
    [
        "AF-ZA", "AM-ET", "AR-AE", "AR-BH", "AR-DZ", "AR-EG", "AR-IQ", "AR-JO", "AR-KW", "AR-LB",
        "AR-LY", "AR-MA", "AR-OM", "AR-QA", "AR-SA", "AR-SY", "AR-TN", "AR-YE", "ARN-CL", "AS-IN",
        "AZ-CYRL-AZ", "AZ-LATN-AZ", "BA-RU", "BE-BY", "BG-BG", "BN-BD", "BN-IN", "BO-CN", "BR-FR",
        "BS-CYRL-BA", "BS-LATN-BA", "CA-ES", "CA-ES-VALENCIA", "CO-FR", "CS-CZ", "CY-GB", "DA-DK",
        "DE-AT", "DE-CH", "DE-DE", "DE-LI", "DE-LU", "DSB-DE", "DV-MV", "EL-GR", "EN-AU", "EN-BZ",
        "EN-CA", "EN-GB", "EN-IE", "EN-IN", "EN-JM", "EN-MY", "EN-NZ", "EN-PH", "EN-SG", "EN-TT",
        "EN-US", "EN-ZA", "EN-ZW", "ES-AR", "ES-BO", "ES-CL", "ES-CO", "ES-CR", "ES-DO", "ES-EC",
        "ES-ES", "ES-GT", "ES-HN", "ES-MX", "ES-NI", "ES-PA", "ES-PE", "ES-PR", "ES-PY", "ES-SV",
        "ES-US", "ES-UY", "ES-VE", "ET-EE", "EU-ES", "FA-IR", "FI-FI", "FIL-PH", "FO-FO", "FR-BE",
        "FR-CA", "FR-CH", "FR-FR", "FR-LU", "FR-MC", "FY-NL", "GA-IE", "GD-DB", "GL-ES", "GSW-FR",
        "GU-IN", "HA-LATN-NG", "HE-IL", "HI-IN", "HR-BA", "HR-HR", "HSB-DE", "HU-HU", "HY-AM",
        "ID-ID", "IG-NG", "II-CN", "IS-IS", "IT-CH", "IT-IT", "IU-CANS-CA", "IU-LATN-CA", "JA-JP",
        "KA-GE", "KK-KZ", "KL-GL", "KM-KH", "KN-IN", "KO-KR", "KOK-IN", "KY-KG", "LB-LU", "LO-LA",
        "LT-LT", "LV-LV", "MI-NZ", "MK-MK", "ML-IN", "MN-MN", "MN-MONG-CN", "MOH-CA", "MR-IN",
        "MS-BN", "MS-MY", "MT-MT", "NB-NO", "NE-NP", "NL-BE", "NL-NL", "NN-NO", "NSO-ZA", "OC-FR",
        "OR-IN", "PA-IN", "PL-PL", "PRS-AF", "PS-AF", "PT-BR", "PT-PT", "QUT-GT", "QUZ-BO",
        "QUZ-EC", "QUZ-PE", "RM-CH", "RO-RO", "RU-RU", "RW-RW", "SA-IN", "SAH-RU", "SE-FI", "SE-NO",
        "SE-SE", "SI-LK", "SK-SK", "SL-SI", "SMA-NO", "SMA-SE", "SMJ-NO", "SMJ-SE", "SMN-FI",
        "SMS-FI", "SQ-AL", "SR-CYRL-BA", "SR-CYRL-CS", "SR-CYRL-ME", "SR-CYRL-RS", "SR-LATN-BA",
        "SR-LATN-CS", "SR-LATN-ME", "SR-LATN-RS", "SV-FI", "SV-SE", "SW-KE", "SYR-SY", "TA-IN",
        "TE-IN", "TG-CYRL-TJ", "TH-TH", "TK-TM", "TN-ZA", "TR-TR", "TT-RU", "TZM-LATN-DZ", "UG-CN",
        "UK-UA", "UR-PK", "UZ-CYRL-UZ", "UZ-LATN-UZ", "VI-VN", "WO-SN", "XH-ZA", "YO-NG", "ZH-CN",
        "ZH-HK", "ZH-MO", "ZH-SG", "ZH-TW", "ZU-ZA",
    ];

    private static readonly string[] LegalEntitySuffixes =
    [
        "AB", "AD", "AG", "APS", "AS", "ASA", "BV", "CO", "COMPANY", "CORP", "CORPORATION", "CV",
        "DOO", "EV", "GES", "GESMBH", "GMBH", "HOLDING", "HOLDINGS", "INC", "INCORPORATED", "KG",
        "KS", "LIMITED", "LLC", "LP", "LTD", "LTDA", "MBH", "NV", "PLC", "PS", "PTY", "PVT", "SA",
        "SARL", "SC", "SCA", "SL", "SP", "SPA", "SRL", "SRO", "SUBSIDIARY",
    ];
}
