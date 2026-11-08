//! Hashing for `pinget hash`. Kept in step with `dotnet/src/Devolutions.Pinget.Cli/FileHasher.cs`:
//! lowercase hex, files streamed rather than read whole, and `--msix` hashes the package's
//! `AppxSignature.p7x` entry.

use std::fs::File;
use std::io::{BufReader, Read};
use std::path::Path;

use anyhow::{Context, Result, anyhow};
use sha2::{Digest, Sha256};

const MSIX_SIGNATURE_ENTRY: &str = "AppxSignature.p7x";

/// SHA256 of everything `reader` yields, as lowercase hex.
pub(crate) fn sha256_reader_hex(reader: &mut impl Read) -> std::io::Result<String> {
    let mut hasher = Sha256::new();
    let mut buffer = vec![0u8; 64 * 1024];
    loop {
        let read = reader.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hasher.finalize().iter().map(|byte| format!("{byte:02x}")).collect())
}

/// SHA256 of a file, streamed, as lowercase hex.
pub(crate) fn sha256_file_hex(path: &Path) -> Result<String> {
    let file = File::open(path).with_context(|| format!("failed to read file '{}'", path.display()))?;
    sha256_reader_hex(&mut BufReader::new(file)).with_context(|| format!("failed to read file '{}'", path.display()))
}

/// SHA256 of the `AppxSignature.p7x` entry of an MSIX/Appx package, as lowercase hex.
pub(crate) fn msix_signature_sha256_hex(path: &Path) -> Result<String> {
    let file = File::open(path).with_context(|| format!("failed to read file '{}'", path.display()))?;
    let mut archive =
        zip::ZipArchive::new(BufReader::new(file)).map_err(|_| anyhow!("Not a valid MSIX/Appx package"))?;
    let mut signature = archive
        .by_name(MSIX_SIGNATURE_ENTRY)
        .map_err(|_| anyhow!("The MSIX/Appx package is not signed ({MSIX_SIGNATURE_ENTRY} not found)"))?;
    Ok(sha256_reader_hex(&mut signature)?)
}
