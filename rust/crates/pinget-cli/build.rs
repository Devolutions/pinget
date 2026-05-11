#[cfg(windows)]
fn main() {
    let version = env!("CARGO_PKG_VERSION");
    let version_string = format!("{version}.0");
    let version_info = version
        .split('.')
        .chain(std::iter::repeat("0"))
        .take(4)
        .map(|part| part.parse::<u16>().expect("package version segment must fit in u16"))
        .fold(0u64, |acc, part| (acc << 16) | u64::from(part));

    let mut resource = winresource::WindowsResource::new();
    resource
        .set("CompanyName", "Devolutions Inc.")
        .set("FileDescription", "Pinget CLI")
        .set("FileVersion", &version_string)
        .set("InternalName", "pinget")
        .set("LegalCopyright", "Copyright 2021-2026 Devolutions Inc.")
        .set("OriginalFilename", "pinget.exe")
        .set("ProductName", "Pinget")
        .set("ProductVersion", &version_string)
        .set_version_info(winresource::VersionInfo::FILEVERSION, version_info)
        .set_version_info(winresource::VersionInfo::PRODUCTVERSION, version_info);
    resource.compile().expect("failed to compile Windows version resource");
}

#[cfg(not(windows))]
fn main() {}
