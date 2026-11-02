//! Signs and verifies MSIX packages with the cross-platform `psign-tool` from
//! <https://github.com/Devolutions/psign>.
//!
//! Every invocation passes its arguments through a response file in a private
//! temporary directory so the PFX password never appears on a process command
//! line, and so `.cmd` tool shims never re-parse arguments.

use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use x509_cert::Certificate;
use x509_cert::der::asn1::ObjectIdentifier;
use x509_cert::der::{Decode, Tag, Tagged};
use x509_cert::ext::pkix::ExtendedKeyUsage;
use zip::ZipArchive;

use crate::{PublisherError, Result};

const EXTENDED_KEY_USAGE: ObjectIdentifier = ObjectIdentifier::new_unwrap("2.5.29.37");
const CODE_SIGNING: ObjectIdentifier = ObjectIdentifier::new_unwrap("1.3.6.1.5.5.7.3.3");

/// Locates `psign-tool` from an explicit path, `PATH`, or the .NET global tool directory.
pub(crate) fn find_psign_tool(explicit: Option<&Path>) -> Option<PathBuf> {
    if let Some(path) = explicit {
        return path.is_file().then(|| path.to_path_buf());
    }
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

/// Resolves `psign-tool` or explains how to install it.
pub(crate) fn require_psign_tool(explicit: Option<&Path>) -> Result<PathBuf> {
    find_psign_tool(explicit).ok_or_else(|| {
        PublisherError::Tool(
            "psign-tool was not found; install it with `dotnet tool install -g Devolutions.Psign.Tool` or pass --psign-tool"
                .to_owned(),
        )
    })
}

/// Signs `package` in place with a PFX, using portable Authenticode signing.
pub(crate) fn sign(tool: &Path, package: &Path, pfx: &Path, password: &str, timestamp_url: Option<&str>) -> Result<()> {
    if password.contains(['\r', '\n']) {
        return Err(PublisherError::Invalid(
            "the PFX password must not contain line breaks".to_owned(),
        ));
    }
    let mut arguments = vec![
        "--mode".to_owned(),
        "portable".to_owned(),
        "sign".to_owned(),
        "--pfx".to_owned(),
        path_argument(pfx)?,
        // `--password=<value>` keeps a leading `@` from being read as a response-file include.
        format!("--password={password}"),
        "--digest".to_owned(),
        "sha256".to_owned(),
    ];
    if let Some(url) = timestamp_url {
        arguments.extend([
            "--timestamp-url".to_owned(),
            url.to_owned(),
            "--timestamp-digest".to_owned(),
            "sha256".to_owned(),
        ]);
    }
    arguments.push(path_argument(package)?);
    run(tool, "MSIX signing", &arguments, Some(password)).map(|_| ())
}

/// Verifies the package digests, its signer against `publisher`, and its certificate chain.
///
/// Without `trusted_ca`, Windows uses the operating-system trust store (`signtool verify /pa`
/// semantics) and other platforms use psign's Microsoft AuthRoot anchors.
pub(crate) fn verify(tool: &Path, package: &Path, publisher: &str, trusted_ca: &[PathBuf]) -> Result<()> {
    let package_argument = path_argument(package)?;
    run(
        tool,
        "MSIX digest verification",
        &[
            "portable".to_owned(),
            "verify-msix".to_owned(),
            package_argument.clone(),
        ],
        None,
    )?;
    verify_signer(tool, package, publisher)?;
    let untrusted = |error: PublisherError| match error {
        PublisherError::Tool(message) if trusted_ca.is_empty() => PublisherError::Tool(format!(
            "{message}
Pass --trusted-ca with the signing root certificate when it is not trusted by default."
        )),
        other => other,
    };
    if trusted_ca.is_empty() && cfg!(windows) {
        run(
            tool,
            "MSIX signature trust verification",
            &[
                "--mode".to_owned(),
                "windows".to_owned(),
                "verify".to_owned(),
                "--policy".to_owned(),
                "pa".to_owned(),
                package_argument,
            ],
            None,
        )
        .map_err(untrusted)?;
    } else {
        let mut arguments = vec!["portable".to_owned(), "trust-verify-msix".to_owned(), package_argument];
        for anchor in trusted_ca {
            arguments.push("--trusted-ca".to_owned());
            arguments.push(path_argument(anchor)?);
        }
        run(tool, "MSIX signature trust verification", &arguments, None).map_err(untrusted)?;
    }
    Ok(())
}

fn verify_signer(tool: &Path, package: &Path, publisher: &str) -> Result<()> {
    let mut archive = ZipArchive::new(File::open(package)?)
        .map_err(|error| PublisherError::Invalid(format!("could not read source MSIX: {error}")))?;
    let mut signature = Vec::new();
    archive
        .by_name("AppxSignature.p7x")
        .map_err(|_| PublisherError::Invalid("source MSIX is not signed".to_owned()))?
        .read_to_end(&mut signature)?;
    let workspace = Workspace::create()?;
    let signature_path = workspace.write("AppxSignature.p7x", &signature)?;
    let output = run(
        tool,
        "MSIX signature inspection",
        &[
            "portable".to_owned(),
            "inspect-pkcs7".to_owned(),
            path_argument(&signature_path)?,
        ],
        None,
    )?;
    let report: serde_json::Value = serde_json::from_slice(&output)
        .map_err(|_| PublisherError::Tool("psign-tool returned an unreadable signature report".to_owned()))?;
    let encoded = report["signers"][0]["signer_certificate_der_base64"]
        .as_str()
        .ok_or_else(|| PublisherError::Invalid("source MSIX signature has no signer certificate".to_owned()))?;
    let certificate = BASE64
        .decode(encoded)
        .map_err(|_| PublisherError::Invalid("source MSIX signer certificate is not valid base64".to_owned()))?;
    check_signing_certificate(&certificate, publisher)
}

/// Requires the signer subject to match the package publisher, the Code Signing EKU and current validity.
pub(crate) fn check_signing_certificate(der: &[u8], publisher: &str) -> Result<()> {
    let certificate = Certificate::from_der(der)
        .map_err(|_| PublisherError::Invalid("signing certificate could not be parsed".to_owned()))?;
    let tbs = &certificate.tbs_certificate;
    let subject = windows_name(&tbs.subject);
    if !subject.eq_ignore_ascii_case(publisher.trim()) {
        return Err(PublisherError::Invalid(format!(
            "signing certificate subject does not match --publisher (expected {publisher:?}, found {subject:?})"
        )));
    }
    let now = SystemTime::now();
    if now < tbs.validity.not_before.to_system_time() || now > tbs.validity.not_after.to_system_time() {
        return Err(PublisherError::Invalid(
            "signing certificate is not currently valid".to_owned(),
        ));
    }
    let code_signing = tbs
        .extensions
        .iter()
        .flatten()
        .filter(|extension| extension.extn_id == EXTENDED_KEY_USAGE)
        .filter_map(|extension| ExtendedKeyUsage::from_der(extension.extn_value.as_bytes()).ok())
        .any(|usage| usage.0.contains(&CODE_SIGNING));
    if !code_signing {
        return Err(PublisherError::Invalid(
            "signing certificate must include the Code Signing enhanced key usage".to_owned(),
        ));
    }
    Ok(())
}

/// Formats a distinguished name like Windows `X509Certificate2.Subject`, which MSIX
/// `Identity/@Publisher` must match: most specific RDN first, `, ` separators.
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
                        quote_value(&attribute_value(&attribute.value))
                    )
                })
                .collect::<Vec<_>>()
                .join(" + ")
        })
        .collect::<Vec<_>>()
        .join(", ")
}

fn attribute_name(oid: &ObjectIdentifier) -> String {
    let name = match oid.to_string().as_str() {
        "2.5.4.3" => "CN",
        "2.5.4.4" => "SN",
        "2.5.4.5" => "SERIALNUMBER",
        "2.5.4.6" => "C",
        "2.5.4.7" => "L",
        "2.5.4.8" => "S",
        "2.5.4.9" => "STREET",
        "2.5.4.10" => "O",
        "2.5.4.11" => "OU",
        "2.5.4.12" => "T",
        "2.5.4.17" => "PostalCode",
        "2.5.4.42" => "G",
        "2.5.4.43" => "I",
        "1.2.840.113549.1.9.1" => "E",
        "0.9.2342.19200300.100.1.1" => "UID",
        "0.9.2342.19200300.100.1.25" => "DC",
        other => return format!("OID.{other}"),
    };
    name.to_owned()
}

fn attribute_value(value: &x509_cert::attr::AttributeValue) -> String {
    let bytes = value.value();
    match value.tag() {
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
            let encoded = x509_cert::der::Encode::to_der(value).unwrap_or_default();
            format!(
                "#{}",
                encoded.iter().map(|byte| format!("{byte:02X}")).collect::<String>()
            )
        }
    }
}

fn quote_value(value: &str) -> String {
    let needs_quotes = value.is_empty()
        || value.starts_with(' ')
        || value.ends_with(' ')
        || value.contains([',', '+', '=', '"', '\n', '\r', '<', '>', '#', ';']);
    if needs_quotes {
        format!("\"{}\"", value.replace('"', "\"\""))
    } else {
        value.to_owned()
    }
}

fn path_argument(path: &Path) -> Result<String> {
    let value = path
        .to_str()
        .ok_or_else(|| PublisherError::Invalid(format!("path is not valid Unicode: {}", path.display())))?;
    if value.contains(['\r', '\n']) {
        return Err(PublisherError::Invalid(format!(
            "path contains a line break: {value:?}"
        )));
    }
    Ok(value.to_owned())
}

/// Runs `psign-tool @<response file>` and returns standard output.
fn run(tool: &Path, operation: &str, arguments: &[String], secret: Option<&str>) -> Result<Vec<u8>> {
    let workspace = Workspace::create()?;
    let mut response = String::new();
    for argument in arguments {
        // One quoted argument per line; doubled quotes encode a literal quote.
        response.push('"');
        response.push_str(&argument.replace('"', "\"\""));
        response.push_str("\"\n");
    }
    let response_path = workspace.write("arguments.rsp", response.as_bytes())?;
    let output = Command::new(tool)
        .arg(format!("@{}", path_argument(&response_path)?))
        .output()
        .map_err(|error| PublisherError::Tool(format!("could not start {}: {error}", tool.display())))?;
    drop(workspace);
    if output.status.success() {
        return Ok(output.stdout);
    }
    let mut message = String::from_utf8_lossy(&output.stderr).trim().to_owned();
    if message.is_empty() {
        message = String::from_utf8_lossy(&output.stdout).trim().to_owned();
    }
    if let Some(secret) = secret.filter(|secret| !secret.is_empty()) {
        message = message.replace(secret, "<redacted>");
    }
    Err(PublisherError::Tool(format!(
        "{operation} with psign-tool failed ({}): {message}",
        output.status
    )))
}

/// A private temporary directory removed on drop.
struct Workspace {
    path: PathBuf,
}

impl Workspace {
    fn create() -> Result<Self> {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|error| PublisherError::Invalid(format!("system clock is before Unix epoch: {error}")))?
            .as_nanos();
        let path = std::env::temp_dir().join(format!("pinget-psign-{}-{nonce}", std::process::id()));
        #[cfg_attr(not(unix), allow(unused_mut, reason = "permissions are only set on Unix"))]
        let mut builder = fs::DirBuilder::new();
        #[cfg(unix)]
        {
            use std::os::unix::fs::DirBuilderExt;
            builder.mode(0o700);
        }
        builder.create(&path)?;
        Ok(Self { path })
    }

    fn write(&self, name: &str, contents: &[u8]) -> Result<PathBuf> {
        let path = self.path.join(name);
        let mut options = OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        let mut file = options.open(&path)?;
        file.write_all(contents)?;
        Ok(path)
    }
}

impl Drop for Workspace {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn missing_explicit_tool_is_not_found() {
        assert!(find_psign_tool(Some(Path::new("definitely-missing-psign-tool"))).is_none());
    }

    #[test]
    fn quoting_matches_windows_subject_format() {
        assert_eq!(quote_value("Example, Inc."), "\"Example, Inc.\"");
        assert_eq!(quote_value("Pinget Source"), "Pinget Source");
        assert_eq!(quote_value("say \"hi\""), "\"say \"\"hi\"\"\"");
    }
}
