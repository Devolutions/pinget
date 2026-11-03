//! Writes MSIX packages in the AppxPackaging (`makeappx pack`) ZIP layout.
//!
//! Windows AppxSip computes package digests over the exact ZIP structure, so the
//! writer mirrors what `makeappx` and psign's signing normalizer emit: stored
//! entries without data descriptors, central-directory entries with "version
//! made by" 45 and zero external attributes, and ZIP64 end records followed by a
//! classic end record holding the ZIP64 sentinels.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use sha2::{Digest, Sha256};

use crate::{PublisherError, Result};

const LOCAL_HEADER_SIGNATURE: u32 = 0x0403_4b50;
const CENTRAL_HEADER_SIGNATURE: u32 = 0x0201_4b50;
const ZIP64_END_SIGNATURE: u32 = 0x0606_4b50;
const ZIP64_LOCATOR_SIGNATURE: u32 = 0x0706_4b50;
const END_SIGNATURE: u32 = 0x0605_4b50;
const LOCAL_HEADER_LEN: usize = 30;
const VERSION_NEEDED: u16 = 20;
const VERSION_MADE_BY: u16 = 45;
const ZIP64_END_RECORD_SIZE: u64 = 44;
/// MS-DOS date for 1980-01-01, keeping package bytes reproducible.
const DOS_DATE: u16 = 0x0021;
const BLOCK_SIZE: usize = 64 * 1024;
const BLOCK_MAP: &str = "AppxBlockMap.xml";
const CONTENT_TYPES: &str = "[Content_Types].xml";

/// Builds an unsigned MSIX from payload files, adding the block map and content types.
///
/// `files` are package-relative paths using `/` separators. Signing tools replace
/// the block map and add `AppxSignature.p7x`; an unsigned package is complete as is.
pub(crate) fn write_package(files: &[(String, Vec<u8>)]) -> Result<Vec<u8>> {
    let mut names = std::collections::BTreeSet::new();
    for (name, _) in files {
        validate_part_name(name)?;
        if !names.insert(name.to_ascii_lowercase()) {
            return Err(PublisherError::Invalid(format!("duplicate MSIX part name: {name}")));
        }
    }
    let block_map = block_map(files);
    let content_types = content_types(files);
    let mut writer = ZipWriter::default();
    for (name, data) in files {
        writer.add(name, data)?;
    }
    writer.add(BLOCK_MAP, block_map.as_bytes())?;
    writer.add(CONTENT_TYPES, content_types.as_bytes())?;
    writer.finish()
}

fn validate_part_name(name: &str) -> Result<()> {
    let reserved = [
        BLOCK_MAP,
        CONTENT_TYPES,
        "AppxSignature.p7x",
        "AppxMetadata/CodeIntegrity.cat",
    ];
    if name.is_empty()
        || name.starts_with('/')
        || name.ends_with('/')
        || name.contains('\\')
        || name
            .split('/')
            .any(|segment| segment.is_empty() || segment == "." || segment == "..")
        || reserved.iter().any(|part| part.eq_ignore_ascii_case(name))
    {
        return Err(PublisherError::Invalid(format!("invalid MSIX part name: {name}")));
    }
    Ok(())
}

fn block_map(files: &[(String, Vec<u8>)]) -> String {
    let mut xml = String::from(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\r\n<BlockMap xmlns=\"http://schemas.microsoft.com/appx/2010/blockmap\" HashMethod=\"http://www.w3.org/2001/04/xmlenc#sha256\">",
    );
    for (name, data) in files {
        // Block-map names use backslashes even though ZIP entry names use slashes.
        xml.push_str(&format!(
            "<File Name=\"{}\" Size=\"{}\" LfhSize=\"{}\">",
            escape_attribute(&name.replace('/', "\\")),
            data.len(),
            LOCAL_HEADER_LEN + name.len()
        ));
        for block in data.chunks(BLOCK_SIZE) {
            xml.push_str(&format!("<Block Hash=\"{}\"/>", BASE64.encode(Sha256::digest(block))));
        }
        xml.push_str("</File>");
    }
    xml.push_str("</BlockMap>");
    xml
}

fn content_types(files: &[(String, Vec<u8>)]) -> String {
    let mut defaults = std::collections::BTreeMap::new();
    let mut overrides = Vec::new();
    for (name, _) in files {
        let file_name = name.rsplit('/').next().unwrap_or(name);
        match file_name.rsplit_once('.').filter(|(stem, _)| !stem.is_empty()) {
            Some((_, extension)) => {
                let extension = extension.to_ascii_lowercase();
                let content_type = content_type_for(&extension);
                defaults.entry(extension).or_insert(content_type);
            }
            None => overrides.push(format!("/{name}")),
        }
    }
    let mut xml = String::from(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">",
    );
    for (extension, content_type) in defaults {
        xml.push_str(&format!(
            "<Default Extension=\"{}\" ContentType=\"{content_type}\"/>",
            escape_attribute(&extension)
        ));
    }
    for part in overrides {
        xml.push_str(&format!(
            "<Override PartName=\"{}\" ContentType=\"application/octet-stream\"/>",
            escape_attribute(&part)
        ));
    }
    xml.push_str(
        "<Override PartName=\"/AppxBlockMap.xml\" ContentType=\"application/vnd.ms-appx.blockmap+xml\"/></Types>",
    );
    xml
}

fn content_type_for(extension: &str) -> &'static str {
    match extension {
        // makeappx maps the manifest's extension to the AppX manifest type.
        "xml" => "application/vnd.ms-appx.manifest+xml",
        "png" => "image/png",
        _ => "application/octet-stream",
    }
}

fn escape_attribute(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('"', "&quot;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

#[derive(Default)]
struct ZipWriter {
    output: Vec<u8>,
    central: Vec<u8>,
    entries: u64,
}

impl ZipWriter {
    fn add(&mut self, name: &str, data: &[u8]) -> Result<()> {
        let too_large = || PublisherError::Invalid(format!("MSIX part is too large for this writer: {name}"));
        let offset = u32::try_from(self.output.len()).map_err(|_| too_large())?;
        let size = u32::try_from(data.len()).map_err(|_| too_large())?;
        let name_len = u16::try_from(name.len()).map_err(|_| too_large())?;
        let mut crc = flate2::Crc::new();
        crc.update(data);
        let crc = crc.sum();

        let output = &mut self.output;
        put_u32(output, LOCAL_HEADER_SIGNATURE);
        put_u16(output, VERSION_NEEDED);
        put_u16(output, 0); // flags
        put_u16(output, 0); // stored
        put_u16(output, 0); // time
        put_u16(output, DOS_DATE);
        put_u32(output, crc);
        put_u32(output, size);
        put_u32(output, size);
        put_u16(output, name_len);
        put_u16(output, 0); // extra
        output.extend_from_slice(name.as_bytes());
        output.extend_from_slice(data);

        let central = &mut self.central;
        put_u32(central, CENTRAL_HEADER_SIGNATURE);
        put_u16(central, VERSION_MADE_BY);
        put_u16(central, VERSION_NEEDED);
        put_u16(central, 0); // flags
        put_u16(central, 0); // stored
        put_u16(central, 0); // time
        put_u16(central, DOS_DATE);
        put_u32(central, crc);
        put_u32(central, size);
        put_u32(central, size);
        put_u16(central, name_len);
        put_u16(central, 0); // extra
        put_u16(central, 0); // comment
        put_u16(central, 0); // disk
        put_u16(central, 0); // internal attributes
        put_u32(central, 0); // external attributes
        put_u32(central, offset);
        central.extend_from_slice(name.as_bytes());
        self.entries += 1;
        Ok(())
    }

    fn finish(mut self) -> Result<Vec<u8>> {
        let too_large = || PublisherError::Invalid("MSIX package is too large for this writer".to_owned());
        let central_offset = u64::try_from(self.output.len()).map_err(|_| too_large())?;
        u32::try_from(central_offset).map_err(|_| too_large())?;
        let central_size = u64::try_from(self.central.len()).map_err(|_| too_large())?;
        self.output.extend_from_slice(&self.central);
        let zip64_end_offset = central_offset + central_size;

        let output = &mut self.output;
        put_u32(output, ZIP64_END_SIGNATURE);
        put_u64(output, ZIP64_END_RECORD_SIZE);
        put_u16(output, VERSION_MADE_BY);
        put_u16(output, VERSION_MADE_BY);
        put_u32(output, 0); // this disk
        put_u32(output, 0); // central directory disk
        put_u64(output, self.entries);
        put_u64(output, self.entries);
        put_u64(output, central_size);
        put_u64(output, central_offset);

        put_u32(output, ZIP64_LOCATOR_SIGNATURE);
        put_u32(output, 0); // ZIP64 end record disk
        put_u64(output, zip64_end_offset);
        put_u32(output, 1); // total disks

        put_u32(output, END_SIGNATURE);
        for _ in 0..4 {
            put_u16(output, u16::MAX);
        }
        put_u32(output, u32::MAX);
        put_u32(output, u32::MAX);
        put_u16(output, 0); // comment
        Ok(self.output)
    }
}

fn put_u16(output: &mut Vec<u8>, value: u16) {
    output.extend_from_slice(&value.to_le_bytes());
}

fn put_u32(output: &mut Vec<u8>, value: u32) {
    output.extend_from_slice(&value.to_le_bytes());
}

fn put_u64(output: &mut Vec<u8>, value: u64) {
    output.extend_from_slice(&value.to_le_bytes());
}

#[cfg(test)]
mod tests {
    use std::io::{Cursor, Read};

    use super::*;

    fn sample() -> Vec<(String, Vec<u8>)> {
        vec![
            ("AppxManifest.xml".to_owned(), b"<Package/>".to_vec()),
            ("Public/index.db".to_owned(), vec![7; BLOCK_SIZE + 3]),
            ("Assets/Logo.png".to_owned(), vec![1, 2, 3]),
        ]
    }

    #[test]
    fn package_reads_back_with_block_map_and_content_types() {
        let package = write_package(&sample()).expect("package");
        let mut archive = zip::ZipArchive::new(Cursor::new(package.as_slice())).expect("zip");
        let names: Vec<_> = archive.file_names().map(str::to_owned).collect();
        assert_eq!(
            names,
            [
                "AppxManifest.xml",
                "Public/index.db",
                "Assets/Logo.png",
                "AppxBlockMap.xml",
                "[Content_Types].xml"
            ]
        );
        let mut index = Vec::new();
        archive
            .by_name("Public/index.db")
            .expect("index")
            .read_to_end(&mut index)
            .expect("read");
        assert_eq!(index, vec![7; BLOCK_SIZE + 3]);

        let mut block_map = String::new();
        archive
            .by_name("AppxBlockMap.xml")
            .expect("block map")
            .read_to_string(&mut block_map)
            .expect("read");
        assert!(block_map.contains("<File Name=\"Public\\index.db\" Size=\"65539\" LfhSize=\"45\">"));
        assert_eq!(block_map.matches("<Block Hash=").count(), 4);

        let mut content_types = String::new();
        archive
            .by_name("[Content_Types].xml")
            .expect("content types")
            .read_to_string(&mut content_types)
            .expect("read");
        assert!(content_types.contains("<Default Extension=\"db\" ContentType=\"application/octet-stream\"/>"));
        assert!(
            content_types.contains("<Default Extension=\"xml\" ContentType=\"application/vnd.ms-appx.manifest+xml\"/>")
        );
        assert!(content_types.contains("PartName=\"/AppxBlockMap.xml\""));
    }

    #[test]
    fn package_uses_appx_packaging_zip_layout() {
        let package = write_package(&sample()).expect("package");
        let end = package.len() - 22;
        assert_eq!(&package[end..end + 4], &END_SIGNATURE.to_le_bytes());
        assert_eq!(&package[end + 8..end + 20], &[0xff; 12]);
        let locator = end - 20;
        assert_eq!(&package[locator..locator + 4], &ZIP64_LOCATOR_SIGNATURE.to_le_bytes());
        let zip64_end = locator - 56;
        assert_eq!(&package[zip64_end..zip64_end + 4], &ZIP64_END_SIGNATURE.to_le_bytes());
        let read_u64 = |offset: usize| u64::from_le_bytes(package[offset..offset + 8].try_into().expect("u64"));
        assert_eq!(read_u64(locator + 8), zip64_end as u64);
        assert_eq!(read_u64(zip64_end + 24), 5);
        let central_offset = usize::try_from(read_u64(zip64_end + 48)).expect("offset");
        let central_size = usize::try_from(read_u64(zip64_end + 40)).expect("size");
        assert_eq!(central_offset + central_size, zip64_end);

        let mut cursor = central_offset;
        while cursor < zip64_end {
            assert_eq!(&package[cursor..cursor + 4], &CENTRAL_HEADER_SIGNATURE.to_le_bytes());
            assert_eq!(&package[cursor + 4..cursor + 6], &VERSION_MADE_BY.to_le_bytes());
            assert_eq!(&package[cursor + 38..cursor + 42], &[0; 4]);
            let name_len = usize::from(u16::from_le_bytes([package[cursor + 28], package[cursor + 29]]));
            cursor += 46 + name_len;
        }
        assert_eq!(
            write_package(&sample()).expect("package"),
            package,
            "output is reproducible"
        );
    }

    #[test]
    fn package_rejects_reserved_and_unsafe_names() {
        for name in [
            "AppxBlockMap.xml",
            "appxsignature.p7x",
            "../x",
            "a//b",
            "a\\b",
            "/a",
            "",
        ] {
            assert!(write_package(&[(name.to_owned(), Vec::new())]).is_err(), "{name}");
        }
        let duplicate = [("A.xml".to_owned(), Vec::new()), ("a.xml".to_owned(), Vec::new())];
        assert!(write_package(&duplicate).is_err());
    }
}
