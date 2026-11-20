use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};

use anyhow::{Context, Result, anyhow, bail};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use quick_xml::Reader;
use quick_xml::events::Event;
use x509_cert::Certificate;
use x509_cert::der::asn1::ObjectIdentifier;
use x509_cert::der::{Decode, Encode, Tag, Tagged};
use x509_cert::ext::pkix::ExtendedKeyUsage;
use zip::ZipArchive;

use crate::SourceRecord;

const HOME_BREW_SOURCE_IDENTITY: &str = "Pinget.HomebrewSource";
const MAX_INDEX_BYTES: u64 = 512 * 1024 * 1024;
const MAX_SIGNATURE_BYTES: u64 = 16 * 1024 * 1024;
const CODE_SIGNING: ObjectIdentifier = ObjectIdentifier::new_unwrap("1.3.6.1.5.5.7.3.3");
const EXTENDED_KEY_USAGE: ObjectIdentifier = ObjectIdentifier::new_unwrap("2.5.29.37");
static TEMP_ID: AtomicU64 = AtomicU64::new(0);

pub(super) fn find_psign_tool() -> Option<PathBuf> {
    let names: &[&str] = if cfg!(windows) {
        &["psign-tool.exe", "psign-tool.cmd"]
    } else {
        &["psign-tool"]
    };
    let mut directories: Vec<PathBuf> = std::env::var_os("PATH")
        .map(|value| std::env::split_paths(&value).collect())
        .unwrap_or_default();
    if let Some(home) = std::env::var_os(if cfg!(windows) { "USERPROFILE" } else { "HOME" }) {
        directories.push(PathBuf::from(home).join(".dotnet").join("tools"));
    }
    directories
        .iter()
        .flat_map(|directory| names.iter().map(move |name| directory.join(name)))
        .find(|path| path.is_file())
}

pub(super) fn verify_msix(path: &Path, source: &SourceRecord, explicit_tool: Option<&Path>) -> Result<Vec<u8>> {
    let publisher = source
        .publisher
        .as_deref()
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| anyhow!("Homebrew source is missing its expected MSIX publisher"))?;
    let tool = if let Some(path) = explicit_tool {
        if !path.is_file() {
            bail!("psign-tool does not exist at {}", path.display());
        }
        path.to_path_buf()
    } else {
        find_psign_tool().ok_or_else(|| {
            anyhow!("psign-tool is required to verify Homebrew sources; install Devolutions.Psign.Tool")
        })?
    };

    let (manifest, signature) = {
        let file = File::open(path).context("could not open downloaded Homebrew source MSIX")?;
        let mut archive = ZipArchive::new(file).context("Homebrew source is not a valid MSIX archive")?;
        let manifest = read_member(&mut archive, "AppxManifest.xml", 1024 * 1024)?;
        let signature = read_member(&mut archive, "AppxSignature.p7x", MAX_SIGNATURE_BYTES)?;
        (manifest, signature)
    };
    let identity = parse_identity(&manifest)?;
    if identity.0 != HOME_BREW_SOURCE_IDENTITY {
        bail!(
            "Homebrew source MSIX identity mismatch (expected {HOME_BREW_SOURCE_IDENTITY}, found {})",
            identity.0
        );
    }
    if !identity.1.eq_ignore_ascii_case(publisher) {
        bail!(
            "Homebrew source MSIX publisher mismatch (expected {publisher:?}, found {:?})",
            identity.1
        );
    }

    run_psign(
        &tool,
        "MSIX digest verification",
        &["portable", "verify-msix", path_to_str(path)?],
    )?;
    let mut trust_args = vec![
        "portable".to_owned(),
        "trust-verify-msix".to_owned(),
        path_to_str(path)?.to_owned(),
    ];
    for anchor in &source.trusted_ca {
        trust_args.push("--trusted-ca".to_owned());
        trust_args.push(anchor.clone());
    }
    run_psign(
        &tool,
        "MSIX signature trust verification",
        &trust_args.iter().map(String::as_str).collect::<Vec<_>>(),
    )?;

    let workspace = TempWorkspace::create()?;
    let signature_path = workspace.path.join("AppxSignature.p7x");
    fs::write(&signature_path, signature)?;
    let output = run_psign(
        &tool,
        "MSIX signature inspection",
        &["portable", "inspect-pkcs7", path_to_str(&signature_path)?],
    )?;
    let report: serde_json::Value =
        serde_json::from_slice(&output).context("psign-tool returned an unreadable signature report")?;
    let encoded = report["signers"][0]["signer_certificate_der_base64"]
        .as_str()
        .ok_or_else(|| anyhow!("Homebrew source signature has no signer certificate"))?;
    let der = BASE64
        .decode(encoded)
        .context("Homebrew source signer certificate is not valid base64")?;
    verify_signer_identity(&der, publisher)?;

    let mut file = File::open(path)?;
    let mut archive = ZipArchive::new(&mut file).context("could not reopen verified Homebrew source MSIX")?;
    read_member(&mut archive, "Public/index.db", MAX_INDEX_BYTES)
        .context("verified Homebrew source MSIX has no usable Public/index.db")
}

fn read_member<R: Read + std::io::Seek>(archive: &mut ZipArchive<R>, name: &str, max_bytes: u64) -> Result<Vec<u8>> {
    let entry = archive
        .by_name(name)
        .map_err(|_| anyhow!("source MSIX is missing {name}"))?;
    if entry.size() > max_bytes {
        bail!("source MSIX member {name} exceeds the {max_bytes}-byte limit");
    }
    let capacity = usize::try_from(entry.size()).context("MSIX member exceeds the host address space")?;
    let mut bytes = Vec::with_capacity(capacity);
    entry
        .take(max_bytes + 1)
        .read_to_end(&mut bytes)
        .with_context(|| format!("could not read source MSIX member {name}"))?;
    if bytes.len() as u64 > max_bytes {
        bail!("source MSIX member {name} exceeds the {max_bytes}-byte limit");
    }
    Ok(bytes)
}

fn parse_identity(manifest: &[u8]) -> Result<(String, String)> {
    let mut reader = Reader::from_reader(manifest);
    reader.config_mut().trim_text(true);
    let mut buffer = Vec::new();
    loop {
        match reader
            .read_event_into(&mut buffer)
            .context("could not parse source AppxManifest.xml")?
        {
            Event::Start(element) | Event::Empty(element) if element.local_name().as_ref() == b"Identity" => {
                let mut name = None;
                let mut publisher = None;
                for attribute in element.attributes() {
                    let attribute = attribute.context("invalid source identity attribute")?;
                    let value = attribute
                        .decode_and_unescape_value(reader.decoder())
                        .context("invalid source identity attribute value")?
                        .into_owned();
                    match attribute.key.local_name().as_ref() {
                        b"Name" => name = Some(value),
                        b"Publisher" => publisher = Some(value),
                        _ => {}
                    }
                }
                return Ok((
                    name.ok_or_else(|| anyhow!("source MSIX identity has no Name"))?,
                    publisher.ok_or_else(|| anyhow!("source MSIX identity has no Publisher"))?,
                ));
            }
            Event::Eof => bail!("source AppxManifest.xml has no Identity element"),
            _ => {}
        }
        buffer.clear();
    }
}

fn verify_signer_identity(der: &[u8], publisher: &str) -> Result<()> {
    let certificate = Certificate::from_der(der).context("Homebrew source signer certificate could not be parsed")?;
    let tbs = &certificate.tbs_certificate;
    let subject = windows_name(&tbs.subject);
    if !subject.eq_ignore_ascii_case(publisher) {
        bail!("Homebrew source signer does not match the expected publisher");
    }
    let code_signing = tbs
        .extensions
        .iter()
        .flatten()
        .filter(|extension| extension.extn_id == EXTENDED_KEY_USAGE)
        .filter_map(|extension| ExtendedKeyUsage::from_der(extension.extn_value.as_bytes()).ok())
        .any(|usage| usage.0.contains(&CODE_SIGNING));
    if !code_signing {
        bail!("Homebrew source signer lacks the Code Signing enhanced key usage");
    }
    Ok(())
}

fn windows_name(name: &x509_cert::name::Name) -> String {
    name.0
        .iter()
        .rev()
        .map(|rdn| {
            rdn.0
                .iter()
                .map(|attribute| {
                    format!(
                        "{}={}",
                        attribute_name(&attribute.oid),
                        attribute_value(&attribute.value)
                    )
                })
                .collect::<Vec<_>>()
                .join(" + ")
        })
        .collect::<Vec<_>>()
        .join(", ")
}

fn attribute_name(oid: &ObjectIdentifier) -> String {
    match oid.to_string().as_str() {
        "2.5.4.3" => "CN".to_owned(),
        "2.5.4.6" => "C".to_owned(),
        "2.5.4.7" => "L".to_owned(),
        "2.5.4.8" => "S".to_owned(),
        "2.5.4.10" => "O".to_owned(),
        "2.5.4.11" => "OU".to_owned(),
        "2.5.4.5" => "SERIALNUMBER".to_owned(),
        "1.2.840.113549.1.9.1" => "E".to_owned(),
        other => format!("OID.{other}"),
    }
}

fn attribute_value(value: &x509_cert::attr::AttributeValue) -> String {
    let bytes = value.value();
    let text = match value.tag() {
        Tag::Utf8String | Tag::PrintableString | Tag::Ia5String | Tag::VisibleString => {
            String::from_utf8_lossy(bytes).into_owned()
        }
        Tag::TeletexString => bytes.iter().copied().map(char::from).collect(),
        Tag::BmpString => {
            let units: Vec<u16> = bytes
                .as_chunks::<2>()
                .0
                .iter()
                .map(|pair| u16::from_be_bytes(*pair))
                .collect();
            String::from_utf16_lossy(&units)
        }
        _ => {
            let encoded = value.to_der().unwrap_or_default();
            return format!(
                "#{}",
                encoded.iter().map(|byte| format!("{byte:02X}")).collect::<String>()
            );
        }
    };
    if text.is_empty()
        || text.starts_with(' ')
        || text.ends_with(' ')
        || text.contains([',', '+', '=', '"', '\n', '\r', '<', '>', '#', ';'])
    {
        format!("\"{}\"", text.replace('"', "\"\""))
    } else {
        text
    }
}

fn path_to_str(path: &Path) -> Result<&str> {
    path.to_str()
        .filter(|value| !value.contains(['\r', '\n']))
        .ok_or_else(|| anyhow!("path is not valid Unicode or contains a line break: {}", path.display()))
}

fn run_psign(tool: &Path, operation: &str, arguments: &[&str]) -> Result<Vec<u8>> {
    let output = Command::new(tool)
        .args(arguments)
        .stdin(Stdio::null())
        .output()
        .with_context(|| format!("could not start psign-tool at {}", tool.display()))?;
    if !output.status.success() {
        let detail = String::from_utf8_lossy(&output.stderr);
        bail!(
            "{operation} with psign-tool failed ({}): {}",
            output.status,
            detail.trim()
        );
    }
    Ok(output.stdout)
}

struct TempWorkspace {
    path: PathBuf,
}

impl TempWorkspace {
    fn create() -> Result<Self> {
        let parent = std::env::temp_dir();
        for _ in 0..100 {
            let id = TEMP_ID.fetch_add(1, Ordering::Relaxed);
            let path = parent.join(format!("pinget-homebrew-{}-{id}", std::process::id()));
            match fs::create_dir(&path) {
                Ok(()) => return Ok(Self { path }),
                Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
                Err(error) => return Err(error).context("could not create private MSIX verification workspace"),
            }
        }
        bail!("could not allocate a private MSIX verification workspace")
    }
}

impl Drop for TempWorkspace {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}
