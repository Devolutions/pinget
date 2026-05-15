//! Pinget port of winget-cli's `NameNormalization.cpp` (the "Initial"
//! version). Produces the same normalized name + publisher strings that
//! winget stores in the catalog's `norm_names2` / `norm_publishers2` tables
//! for an ARP entry's `DisplayName` and `Publisher`.
//!
//! Without this, identity correlation only succeeds when the installed
//! display name happens to match the catalog's `PackageName` after our
//! naive alphanumeric normalization — winget can match many more entries
//! because it strips version-like tokens, locales, architectures, and
//! legal-entity suffixes before comparing. This module reproduces those
//! transformations so we can correlate the same set of ARP rows winget
//! does.
//!
//! ## fancy-regex caveats
//!
//! winget's patterns use lookbehind extensively. fancy-regex supports
//! lookbehind, but each alternation arm must be fixed-length. The C++
//! patterns include constructs like `(?<=^|[^\p{L}\p{Nd}])` that mix
//! 0-length (`^`) with 1-length (`[^...]`), which fails to compile. The
//! ported patterns rewrite these as captured boundaries: `(^|[^...])` is
//! captured in group 1 and re-emitted via `$1` in the replacement string,
//! which gives the same semantics.

use std::sync::OnceLock;

use fancy_regex::Regex;

/// Result of normalizing a display name. The architecture is extracted as
/// a side-channel because winget can later append it to the normalized
/// name to disambiguate multi-arch installs.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub(crate) struct NormalizedName {
    pub(crate) name: String,
    pub(crate) architecture: Architecture,
    pub(crate) locale: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub(crate) enum Architecture {
    #[default]
    Unknown,
    X86,
    X64,
}

/// One pass over the input — captures both the regex and the replacement
/// string used. Boundary-preserving patterns use `$1`; all-consuming
/// patterns use `""`.
struct Stripper {
    re: &'static Regex,
    replacement: &'static str,
}

/// Normalizes an ARP DisplayName the same way winget's NameNormalizer
/// (Initial version, `preserveWhiteSpace = false`) does — producing the
/// string that ends up in the catalog's `norm_names2` table.
pub(crate) fn normalize_name(value: &str) -> NormalizedName {
    let mut name = prepare_for_validation(value);
    while unwrap(&mut name) {}

    // SAP Business Object program names follow a specific pattern that
    // breaks under the regular flow; winget short-circuits them.
    if sap_package().is_match(&name).unwrap_or(false) {
        return NormalizedName {
            name,
            architecture: Architecture::Unknown,
            locale: String::new(),
        };
    }

    let architecture = remove_architecture(&mut name);
    let locale = remove_locale(&mut name);

    // Preserve KB numbers from within parens before the bracket strippers
    // would eat them — winget keeps `KB1234567` as part of the normalized
    // name because it's the only meaningful identifier on some patches.
    let kb_replaced = kb_numbers().replace_all(&name, "$1").into_owned();
    name = kb_replaced;

    while apply_strippers(program_name_strippers(), &mut name) {}

    let tokens = split_with_legal_suffix_exclusion(program_name_split(), &name, false);
    name = tokens.join("");
    let nonletters_replaced = non_letters_and_digits().replace_all(&name, "").into_owned();
    name = nonletters_replaced;

    NormalizedName {
        name: name.to_lowercase(),
        architecture,
        locale: locale.to_lowercase(),
    }
}

/// Normalizes a publisher string. Strips the same set of patterns as the
/// name path plus splits on word boundaries with the legal-entity-suffix
/// list — so `Microsoft Corporation` → `microsoft`, `JetBrains s.r.o.` →
/// `jetbrains`, but `The Git Development Community` stays intact because
/// no token in it is a recognized suffix.
pub(crate) fn normalize_publisher(value: &str) -> String {
    let mut publisher = prepare_for_validation(value);
    while unwrap(&mut publisher) {}

    while apply_strippers(publisher_name_strippers(), &mut publisher) {}

    // Publisher split stops at the FIRST legal-entity suffix it sees
    // (after the first token), so `Foo Inc Internal Sub Bar` keeps just
    // `Foo` — `Inc` cuts off everything beyond.
    let tokens = split_with_legal_suffix_exclusion(publisher_name_split(), &publisher, true);
    publisher = tokens.join("");
    let cleaned = non_letters_and_digits().replace_all(&publisher, "").into_owned();
    cleaned.to_lowercase()
}

// ── Internal helpers ──────────────────────────────────────────────────────

fn prepare_for_validation(value: &str) -> String {
    let mut s = value.trim().to_owned();
    // winget supports an `@@`-delimited suffix on internal display names
    // that should be stripped before normalization — keep parity even
    // though it's unusual in the wild.
    if let Some(idx) = s.find("@@")
        && idx >= 3
    {
        s.truncate(idx);
    }
    s
}

fn unwrap(value: &mut String) -> bool {
    if value.len() < 2 {
        return false;
    }
    let bytes = value.as_bytes();
    let first = bytes[0];
    let last = bytes[bytes.len() - 1];
    let matches = match first {
        b'"' => last == b'"',
        b'(' => last == b')',
        _ => false,
    };
    if !matches {
        return false;
    }
    *value = value[1..value.len() - 1].to_string();
    true
}

fn apply(stripper: &Stripper, value: &mut String) -> bool {
    let replaced = stripper.re.replace_all(value, stripper.replacement);
    if replaced == value.as_str() {
        return false;
    }
    *value = replaced.into_owned();
    true
}

fn apply_strippers(strippers: &[Stripper], value: &mut String) -> bool {
    let mut changed = false;
    for s in strippers {
        if apply(s, value) {
            changed = true;
        }
    }
    changed
}

fn remove_architecture(value: &mut String) -> Architecture {
    // Order matters: "32/64-bit" is a superstring of "64-bit"; "X64"/
    // "AMD64" must beat "X32"/"X86" because of "x86-64".
    if apply(architecture_32_or_64_bit(), value) {
        return Architecture::Unknown;
    }
    if apply(architecture_x64(), value) || apply(architecture_64_bit(), value) {
        return Architecture::X64;
    }
    if apply(architecture_x32(), value) || apply(architecture_32_bit(), value) {
        return Architecture::X86;
    }
    Architecture::Unknown
}

fn remove_locale(value: &mut String) -> String {
    // Walk locale matches; only treat them as locales if they're in
    // winget's known list. Unknown locale-shaped tokens (e.g. `XY-AB` for
    // a non-real locale) get preserved instead of stripped.
    let re = locale();
    let mut new_value = String::with_capacity(value.len());
    let mut locale_found: Option<String> = None;
    let mut last_end = 0usize;

    for capture in re.captures_iter(value) {
        let Ok(capture) = capture else { continue };
        let Some(m) = capture.get(0) else { continue };
        let folded = m.as_str().to_uppercase();
        let is_known = LOCALES.binary_search(&folded.as_str()).is_ok();

        new_value.push_str(&value[last_end..m.start()]);
        if !is_known {
            new_value.push_str(m.as_str());
        } else {
            match &locale_found {
                None => locale_found = Some(folded),
                Some(existing) if existing == &folded => {}
                Some(existing) => {
                    let existing_lang = existing.split('-').next().unwrap_or("");
                    let new_lang = folded.split('-').next().unwrap_or("");
                    if existing_lang != new_lang {
                        locale_found = Some(String::new());
                    }
                }
            }
        }
        last_end = m.end();
    }
    new_value.push_str(&value[last_end..]);
    *value = new_value;
    locale_found.unwrap_or_default()
}

fn split_with_legal_suffix_exclusion(re: &Regex, value: &str, stop_on_exclusion: bool) -> Vec<String> {
    let mut result = Vec::new();
    let mut last_end = 0usize;
    let push_segment = |segment: &str, out: &mut Vec<String>| -> bool {
        let trimmed = segment.trim();
        if trimmed.is_empty() {
            return true;
        }
        let folded = trimmed.to_uppercase();
        if !out.is_empty() && LEGAL_ENTITY_SUFFIXES.binary_search(&folded.as_str()).is_ok() {
            return !stop_on_exclusion;
        }
        out.push(trimmed.to_owned());
        true
    };

    for m in re.find_iter(value) {
        let Ok(m) = m else { continue };
        let segment = &value[last_end..m.start()];
        if !push_segment(segment, &mut result) {
            return result;
        }
        last_end = m.end();
    }
    let segment = &value[last_end..];
    push_segment(segment, &mut result);
    result
}

// ── Regex cells. Compiled lazily so the cost is paid once per process. ──

macro_rules! regex_cell {
    ($name:ident, $pattern:expr) => {
        fn $name() -> &'static Regex {
            static CELL: OnceLock<Regex> = OnceLock::new();
            CELL.get_or_init(|| {
                let inner: &str = $pattern;
                // (?i) matches the C++ patterns' CaseInsensitive option.
                Regex::new(&format!("(?i){inner}")).expect("invalid normalizer regex")
            })
        }
    };
}

macro_rules! stripper_cell {
    ($name:ident, $re_fn:ident, $replacement:expr) => {
        fn $name() -> &'static Stripper {
            static CELL: OnceLock<Stripper> = OnceLock::new();
            CELL.get_or_init(|| Stripper {
                re: $re_fn(),
                replacement: $replacement,
            })
        }
    };
}

// Architectures: rewritten boundary `(^|[^...])` captured in group 1 and
// re-emitted via `$1` because fancy-regex won't accept the original
// variable-length `(?<=^|[^...])` lookbehind.
regex_cell!(
    architecture_x32_re,
    r"(^|[^\p{L}\p{Nd}])(?:X32|X86)(?=\P{Nd}|$)(?:\sEDITION)?"
);
regex_cell!(
    architecture_x64_re,
    r"(^|[^\p{L}\p{Nd}])(?:X64|AMD64|X86[\p{Pd}\p{Pc}]64)(?=\P{Nd}|$)(?:\sEDITION)?"
);
regex_cell!(
    architecture_32_bit_re,
    r"(^|[^\p{L}\p{Nd}])(?:32[\p{Pd}\p{Pc}\p{Z}]?BIT)S?(?:\sEDITION)?"
);
regex_cell!(
    architecture_64_bit_re,
    r"(^|[^\p{L}\p{Nd}])(?:64[\p{Pd}\p{Pc}\p{Z}]?BIT)S?(?:\sEDITION)?"
);
regex_cell!(
    architecture_32_or_64_bit_re,
    r"(^|[^\p{L}\p{Nd}])(?:(?:64[\\/]32|32[\\/]64)[\p{Pd}\p{Pc}\p{Z}]?BIT)S?(?:\sEDITION)?"
);
stripper_cell!(architecture_x32, architecture_x32_re, "$1");
stripper_cell!(architecture_x64, architecture_x64_re, "$1");
stripper_cell!(architecture_32_bit, architecture_32_bit_re, "$1");
stripper_cell!(architecture_64_bit, architecture_64_bit_re, "$1");
stripper_cell!(architecture_32_or_64_bit, architecture_32_or_64_bit_re, "$1");

// Locale: `(?<![A-Z])` is fixed-length (1 char), so fancy-regex accepts it
// directly. Same for `(?![A-Z])` (lookahead, always supported).
regex_cell!(
    locale,
    r"(?<![A-Z])((?:\p{Lu}{2,3}(-(CANS|CYRL|LATN|MONG))?-\p{Lu}{2})(?![A-Z])(?:-VALENCIA)?)"
);

regex_cell!(
    sap_package,
    r"^(?:[\p{Lu}\p{Nd}]+[\._])+[\p{Lu}\p{Nd}]+(?:-(?:\p{Nd}+\.)+\p{Nd}+)(?:-(?:\p{Lu}{2}(?:_\p{Lu}{2})?|CORE))(?:-(?:\p{Lu}{2}|\p{Nd}{2}))$"
);

regex_cell!(kb_numbers, r"\((KB\d+)\)");
regex_cell!(non_letters_and_digits, r"[^\p{L}\p{Nd}]");

// `(?<!\p{L})` is fixed-length (1 codepoint) — fancy-regex compatible.
regex_cell!(uri_protocol_re, r"(?<!\p{L})(?:http[s]?|ftp)://");
regex_cell!(
    version_delimited_re,
    r"((?<!\p{L})(?:V|VER|VERSI(?:O|Ó)N|VERSÃO|VERSIE|WERSJA|BUILD|RELEASE|RC|SP)\P{L}?)?\p{Nd}+([\p{Po}\p{Pd}\p{Pc}]\p{Nd}?(RC|B|A|R|SP|K)?\p{Nd}+)+([\p{Po}\p{Pd}\p{Pc}]?[\p{L}\p{Nd}]+)*"
);
regex_cell!(
    version_re,
    r"(FOR\s)?(?<!\p{L})(?:P|V|R|VER|VERSI(?:O|Ó)N|VERSÃO|VERSIE|WERSJA|BUILD|RELEASE|RC|SP)(?:\P{L}|\P{L}\p{L})?(\p{Nd}|\.\p{Nd})+(?:RC|B|A|R|V|SP)?\p{Nd}?"
);
regex_cell!(
    version_letter_re,
    r"(?<!\p{L})(?:(?:V|VER|VERSI(?:O|Ó)N|VERSÃO|VERSIE|WERSJA|BUILD|RELEASE|RC|SP)\P{L})?\p{Lu}\p{Nd}+(?:[\p{Po}\p{Pd}\p{Pc}]\p{Nd}+)+"
);
regex_cell!(non_nested_bracket_re, r"\([^\(\)]*\)|\[[^\[\]]*\]");
regex_cell!(bracket_enclosed_re, r#"(?:\p{Ps}.*\p{Pe}|".*")"#);
regex_cell!(leading_symbols_re, r"^[^\p{L}\p{Nd}]+");
regex_cell!(trailing_non_letters_re, r"\P{L}+$");
regex_cell!(prefix_parens_re, r"^\(.*?\)");
regex_cell!(empty_parens_re, r#"(\(\s*\)|\[\s*\]|"\s*")"#);
regex_cell!(en_suffix_re, r"\sEN\s*$");
regex_cell!(trailing_symbols_re, r"[^\p{L}\p{Nd}]+$");
regex_cell!(file_path_re, r"((INSTALLED\sAT|IN)\s)?[CDEF]:\\(.+?\\)*[^\s]*\\?");
regex_cell!(
    file_path_ghs_re,
    r"\(CHANGE\s#\d{1,2}\sTO\s[CDEF]:\\(.+?\\)*[^\s]*\\?\)"
);
regex_cell!(file_path_parens_re, r"\([CDEF]:\\(.+?\\)*[^\s]*\\?\)");
regex_cell!(file_path_quotes_re, r#""[CDEF]:\\(.+?\\)*[^\s]*\\?""#);

// Roblox/Bomgar: original used a variable-length lookbehind to anchor at
// `^ROBLOX\sPLAYER` (or similar). Rewrite captures that prefix as group 1
// and the suffix-to-remove as the rest of the match; replacing with `$1`
// keeps the prefix and drops the rest.
regex_cell!(roblox_re, r"(^ROBLOX\s(?:PLAYER|STUDIO))(?:\sFOR\s.*)");
regex_cell!(
    bomgar_re,
    r"(^BOMGAR\s(?:JUMP\sCLIENT|(?:ACCESS|REPRESENTATIVE)\sCONSOLE|BUTTON)|^EMBEDDED\sCALLBACK)(?:\s.*)"
);

// AcronymSeparators: alternation of two fixed-length lookbehinds — each
// arm is independently fixed-length, so this compiles unchanged.
regex_cell!(
    acronym_separators_re,
    r"(?:(?<=^\p{L})|(?<=\P{L}\p{L}))(\.|/)(?=\p{L}(?:\P{L}|$))"
);

// NonLetters: rewrite the variable-length `(?<=^|\s)` to a captured
// boundary the same way as the architecture patterns.
regex_cell!(non_letters_re, r"(^|\s)[^\p{L}]+(?=\s|$)");

regex_cell!(program_name_split, r"[^\p{L}\p{Nd}\+\&]");
regex_cell!(publisher_name_split, r"[^\p{L}\p{Nd}]");

// Strippers that consume their match entirely.
stripper_cell!(uri_protocol, uri_protocol_re, "");
stripper_cell!(version_delimited, version_delimited_re, "");
stripper_cell!(version, version_re, "");
stripper_cell!(version_letter, version_letter_re, "");
stripper_cell!(non_nested_bracket, non_nested_bracket_re, "");
stripper_cell!(bracket_enclosed, bracket_enclosed_re, "");
stripper_cell!(leading_symbols, leading_symbols_re, "");
stripper_cell!(trailing_non_letters, trailing_non_letters_re, "");
stripper_cell!(prefix_parens, prefix_parens_re, "");
stripper_cell!(empty_parens, empty_parens_re, "");
stripper_cell!(en_suffix, en_suffix_re, "");
stripper_cell!(trailing_symbols, trailing_symbols_re, "");
stripper_cell!(file_path, file_path_re, "");
stripper_cell!(file_path_ghs, file_path_ghs_re, "");
stripper_cell!(file_path_parens, file_path_parens_re, "");
stripper_cell!(file_path_quotes, file_path_quotes_re, "");

// Strippers that preserve a captured boundary via `$1`.
stripper_cell!(roblox, roblox_re, "$1");
stripper_cell!(bomgar, bomgar_re, "$1");
stripper_cell!(acronym_separators, acronym_separators_re, "");
stripper_cell!(non_letters, non_letters_re, "$1");

fn program_name_strippers() -> &'static [Stripper] {
    static CELL: OnceLock<Vec<Stripper>> = OnceLock::new();
    CELL.get_or_init(|| {
        vec![
            // Order mirrors winget's PROGRAM_NAME_REGEXES.
            clone(roblox()),
            clone(bomgar()),
            clone(prefix_parens()),
            clone(empty_parens()),
            clone(file_path_ghs()),
            clone(file_path_parens()),
            clone(file_path_quotes()),
            clone(file_path()),
            clone(version_letter()),
            clone(version_delimited()),
            clone(version()),
            clone(en_suffix()),
            clone(non_nested_bracket()),
            clone(bracket_enclosed()),
            clone(uri_protocol()),
            clone(leading_symbols()),
            clone(trailing_symbols()),
        ]
    })
}

fn publisher_name_strippers() -> &'static [Stripper] {
    static CELL: OnceLock<Vec<Stripper>> = OnceLock::new();
    CELL.get_or_init(|| {
        vec![
            clone(version_delimited()),
            clone(version()),
            clone(non_nested_bracket()),
            clone(bracket_enclosed()),
            clone(uri_protocol()),
            clone(non_letters()),
            clone(trailing_non_letters()),
            clone(acronym_separators()),
        ]
    })
}

fn clone(s: &Stripper) -> Stripper {
    Stripper {
        re: s.re,
        replacement: s.replacement,
    }
}

// ── Locale + legal-entity-suffix lists ────────────────────────────────────

// Pre-uppercased and pre-sorted so binary_search works against the regex's
// uppercase output (`(?i)` matches case-insensitively but capture text
// preserves the original casing; we uppercase before lookup).
const LOCALES: &[&str] = &[
    "AF-ZA",
    "AM-ET",
    "AR-AE",
    "AR-BH",
    "AR-DZ",
    "AR-EG",
    "AR-IQ",
    "AR-JO",
    "AR-KW",
    "AR-LB",
    "AR-LY",
    "AR-MA",
    "AR-OM",
    "AR-QA",
    "AR-SA",
    "AR-SY",
    "AR-TN",
    "AR-YE",
    "ARN-CL",
    "AS-IN",
    "AZ-CYRL-AZ",
    "AZ-LATN-AZ",
    "BA-RU",
    "BE-BY",
    "BG-BG",
    "BN-BD",
    "BN-IN",
    "BO-CN",
    "BR-FR",
    "BS-CYRL-BA",
    "BS-LATN-BA",
    "CA-ES",
    "CA-ES-VALENCIA",
    "CO-FR",
    "CS-CZ",
    "CY-GB",
    "DA-DK",
    "DE-AT",
    "DE-CH",
    "DE-DE",
    "DE-LI",
    "DE-LU",
    "DSB-DE",
    "DV-MV",
    "EL-GR",
    "EN-AU",
    "EN-BZ",
    "EN-CA",
    "EN-GB",
    "EN-IE",
    "EN-IN",
    "EN-JM",
    "EN-MY",
    "EN-NZ",
    "EN-PH",
    "EN-SG",
    "EN-TT",
    "EN-US",
    "EN-ZA",
    "EN-ZW",
    "ES-AR",
    "ES-BO",
    "ES-CL",
    "ES-CO",
    "ES-CR",
    "ES-DO",
    "ES-EC",
    "ES-ES",
    "ES-GT",
    "ES-HN",
    "ES-MX",
    "ES-NI",
    "ES-PA",
    "ES-PE",
    "ES-PR",
    "ES-PY",
    "ES-SV",
    "ES-US",
    "ES-UY",
    "ES-VE",
    "ET-EE",
    "EU-ES",
    "FA-IR",
    "FI-FI",
    "FIL-PH",
    "FO-FO",
    "FR-BE",
    "FR-CA",
    "FR-CH",
    "FR-FR",
    "FR-LU",
    "FR-MC",
    "FY-NL",
    "GA-IE",
    "GD-DB",
    "GL-ES",
    "GSW-FR",
    "GU-IN",
    "HA-LATN-NG",
    "HE-IL",
    "HI-IN",
    "HR-BA",
    "HR-HR",
    "HSB-DE",
    "HU-HU",
    "HY-AM",
    "ID-ID",
    "IG-NG",
    "II-CN",
    "IS-IS",
    "IT-CH",
    "IT-IT",
    "IU-CANS-CA",
    "IU-LATN-CA",
    "JA-JP",
    "KA-GE",
    "KK-KZ",
    "KL-GL",
    "KM-KH",
    "KN-IN",
    "KO-KR",
    "KOK-IN",
    "KY-KG",
    "LB-LU",
    "LO-LA",
    "LT-LT",
    "LV-LV",
    "MI-NZ",
    "MK-MK",
    "ML-IN",
    "MN-MN",
    "MN-MONG-CN",
    "MOH-CA",
    "MR-IN",
    "MS-BN",
    "MS-MY",
    "MT-MT",
    "NB-NO",
    "NE-NP",
    "NL-BE",
    "NL-NL",
    "NN-NO",
    "NSO-ZA",
    "OC-FR",
    "OR-IN",
    "PA-IN",
    "PL-PL",
    "PRS-AF",
    "PS-AF",
    "PT-BR",
    "PT-PT",
    "QUT-GT",
    "QUZ-BO",
    "QUZ-EC",
    "QUZ-PE",
    "RM-CH",
    "RO-RO",
    "RU-RU",
    "RW-RW",
    "SA-IN",
    "SAH-RU",
    "SE-FI",
    "SE-NO",
    "SE-SE",
    "SI-LK",
    "SK-SK",
    "SL-SI",
    "SMA-NO",
    "SMA-SE",
    "SMJ-NO",
    "SMJ-SE",
    "SMN-FI",
    "SMS-FI",
    "SQ-AL",
    "SR-CYRL-BA",
    "SR-CYRL-CS",
    "SR-CYRL-ME",
    "SR-CYRL-RS",
    "SR-LATN-BA",
    "SR-LATN-CS",
    "SR-LATN-ME",
    "SR-LATN-RS",
    "SV-FI",
    "SV-SE",
    "SW-KE",
    "SYR-SY",
    "TA-IN",
    "TE-IN",
    "TG-CYRL-TJ",
    "TH-TH",
    "TK-TM",
    "TN-ZA",
    "TR-TR",
    "TT-RU",
    "TZM-LATN-DZ",
    "UG-CN",
    "UK-UA",
    "UR-PK",
    "UZ-CYRL-UZ",
    "UZ-LATN-UZ",
    "VI-VN",
    "WO-SN",
    "XH-ZA",
    "YO-NG",
    "ZH-CN",
    "ZH-HK",
    "ZH-MO",
    "ZH-SG",
    "ZH-TW",
    "ZU-ZA",
];

const LEGAL_ENTITY_SUFFIXES: &[&str] = &[
    "AB",
    "AD",
    "AG",
    "APS",
    "AS",
    "ASA",
    "BV",
    "CO",
    "COMPANY",
    "CORP",
    "CORPORATION",
    "CV",
    "DOO",
    "EV",
    "GES",
    "GESMBH",
    "GMBH",
    "HOLDING",
    "HOLDINGS",
    "INC",
    "INCORPORATED",
    "KG",
    "KS",
    "LIMITED",
    "LLC",
    "LP",
    "LTD",
    "LTDA",
    "MBH",
    "NV",
    "PLC",
    "PS",
    "PTY",
    "PVT",
    "SA",
    "SARL",
    "SC",
    "SCA",
    "SL",
    "SP",
    "SPA",
    "SRL",
    "SRO",
    "SUBSIDIARY",
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lists_are_sorted_for_binary_search() {
        let mut sorted = LOCALES.to_vec();
        sorted.sort();
        assert_eq!(sorted, LOCALES.to_vec(), "LOCALES must be pre-sorted");

        let mut sorted_suffixes = LEGAL_ENTITY_SUFFIXES.to_vec();
        sorted_suffixes.sort();
        assert_eq!(
            sorted_suffixes,
            LEGAL_ENTITY_SUFFIXES.to_vec(),
            "LEGAL_ENTITY_SUFFIXES must be pre-sorted"
        );
    }

    // Fixture strings observed in the live catalog's norm_publishers2 /
    // norm_names2 tables on the development machine. These are the actual
    // outputs winget produced for those packages, so matching them is the
    // criterion of success for the port.

    #[test]
    fn publisher_microsoft_corporation_normalizes_to_microsoft() {
        assert_eq!(normalize_publisher("Microsoft Corporation"), "microsoft");
    }

    #[test]
    fn publisher_jetbrains_sro_normalizes_to_jetbrains() {
        assert_eq!(normalize_publisher("JetBrains s.r.o."), "jetbrains");
    }

    #[test]
    fn publisher_without_legal_suffix_is_kept_verbatim() {
        // "The Git Development Community" has no recognized legal-entity
        // suffix, so all tokens stay — matches the live catalog row
        // (`thegitdevelopmentcommunity`).
        assert_eq!(
            normalize_publisher("The Git Development Community"),
            "thegitdevelopmentcommunity"
        );
    }

    #[test]
    fn publisher_strips_inc_and_llc_and_gmbh() {
        assert_eq!(normalize_publisher("Foo Inc"), "foo");
        assert_eq!(normalize_publisher("Foo Bar LLC"), "foobar");
        assert_eq!(normalize_publisher("Foo GmbH"), "foo");
    }

    #[test]
    fn name_strips_version_delimited_token() {
        // `2025.3.0.1` matches VersionDelimited (digits + punctuation +
        // digits). Bare `2026` does NOT match and stays as part of the
        // normalized name — same as the live catalog row
        // (`visualstudioprofessional2026`).
        assert_eq!(normalize_name("JetBrains Rider 2025.3.0.1").name, "jetbrainsrider");
        assert_eq!(
            normalize_name("Visual Studio Professional 2026").name,
            "visualstudioprofessional2026"
        );
    }

    #[test]
    fn name_strips_architecture_suffix() {
        let r = normalize_name("PowerToys (Preview) x64");
        assert_eq!(r.name, "powertoys");
        assert_eq!(r.architecture, Architecture::X64);
    }

    #[test]
    fn name_strips_known_locale_suffix() {
        let r = normalize_name("Foo en-US Edition");
        assert_eq!(r.locale, "en-us");
    }

    #[test]
    fn name_keeps_unknown_locale_shaped_tokens() {
        let r = normalize_name("Foo XY-AB");
        assert!(r.locale.is_empty());
    }

    #[test]
    fn name_strips_parens_content() {
        assert_eq!(normalize_name("Foo (beta)").name, "foo");
    }

    #[test]
    fn name_normalizes_microsoft_edge() {
        assert_eq!(normalize_name("Microsoft Edge").name, "microsoftedge");
    }

    #[test]
    fn name_keeps_year_only_suffix() {
        assert_eq!(normalize_name("Foo 2026").name, "foo2026");
    }
}
