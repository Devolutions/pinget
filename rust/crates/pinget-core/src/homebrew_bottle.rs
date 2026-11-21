use std::collections::HashSet;
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::process::Command;

use anyhow::{Context, Result, anyhow, bail};
use flate2::read::GzDecoder;

pub(crate) fn extract(archive: &Path, destination: &Path, formula: &str, version: &str) -> Result<Vec<PathBuf>> {
    if destination.exists() {
        bail!(
            "Bottle extraction destination already exists: {}",
            destination.display()
        );
    }
    fs::create_dir_all(destination)?;
    let expected = format!("{formula}/{version}/");
    let mut files = Vec::new();
    let mut links = Vec::new();
    let mut names = HashSet::new();
    let mut parents = HashSet::new();
    let mut expanded_size = 0u64;
    let mut tar = tar::Archive::new(GzDecoder::new(fs::File::open(archive)?));
    for entry in tar.entries()? {
        let mut entry = entry?;
        let path = entry.path()?;
        let name = path.to_str().context("Bottle contains a non-UTF-8 path")?;
        let name = name.trim_end_matches('/');
        if name == formula || name == format!("{formula}/{version}") {
            if !entry.header().entry_type().is_dir() {
                bail!("Invalid bottle root entry: {name}");
            }
            continue;
        }
        let relative = name
            .strip_prefix(&expected)
            .context("Bottle entry is outside the expected keg")?;
        let target = child_path(destination, relative)?;
        if !names.insert(relative.to_owned()) || names.len() > 100_000 {
            bail!("Duplicate or excessive bottle entry: {name}");
        }
        for (index, _) in relative.match_indices('/') {
            parents.insert(relative[..index].to_owned());
        }
        expanded_size = expanded_size
            .checked_add(entry.size())
            .context("Bottle size overflow")?;
        if expanded_size > 2 * 1024 * 1024 * 1024 {
            bail!("Bottle exceeds the 2 GiB expanded-size limit");
        }
        let kind = entry.header().entry_type();
        if kind.is_dir() {
            fs::create_dir_all(target)?;
        } else if kind.is_file() {
            fs::create_dir_all(target.parent().context("Bottle file has no parent")?)?;
            let mut output = fs::OpenOptions::new().write(true).create_new(true).open(&target)?;
            std::io::copy(&mut entry, &mut output)?;
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt as _;
                fs::set_permissions(&target, fs::Permissions::from_mode(entry.header().mode()? & 0o777))?;
            }
            files.push(target);
        } else if kind.is_symlink() || kind.is_hard_link() {
            let link = entry.link_name()?.context("Bottle link has no target")?.into_owned();
            links.push((relative.to_owned(), target, link, kind.is_hard_link()));
        } else {
            bail!("Unsupported bottle entry type: {kind:?}");
        }
    }
    for (relative, path, link, hard) in links {
        if parents.contains(&relative) {
            bail!("Bottle places an entry below a symbolic link: {relative}");
        }
        let link_text = link.to_str().context("Bottle link is not UTF-8")?;
        if link.is_absolute() || link_text.contains('\\') {
            bail!("Unsafe bottle link target: {link_text}");
        }
        let target = if hard {
            child_path(
                destination,
                link_text
                    .strip_prefix(&expected)
                    .context("Bottle hard link is outside its keg")?,
            )?
        } else {
            normalize_link(destination, path.parent().context("Bottle link has no parent")?, &link)?
        };
        let mut current = target.as_path();
        while current != destination {
            if fs::symlink_metadata(current)?.file_type().is_symlink() {
                bail!("Bottle link target traverses another link: {link_text}");
            }
            current = current.parent().context("Bottle link target escapes its keg")?;
        }
        fs::create_dir_all(path.parent().context("Bottle link has no parent")?)?;
        if hard {
            if !target.is_file() {
                bail!("Bottle hard link has no regular-file target: {link_text}");
            }
            fs::copy(target, &path)?;
            files.push(path);
        } else {
            #[cfg(unix)]
            std::os::unix::fs::symlink(link, path)?;
            #[cfg(not(unix))]
            bail!("Native bottle installation requires Linux or macOS");
        }
    }
    if files.is_empty() {
        bail!("Bottle contains no files");
    }
    Ok(files)
}

fn child_path(root: &Path, relative: &str) -> Result<PathBuf> {
    if relative.is_empty()
        || relative.contains('\\')
        || relative.split('/').any(|part| matches!(part, "" | "." | ".."))
        || Path::new(relative).is_absolute()
    {
        bail!("Unsafe bottle path: {relative}");
    }
    Ok(root.join(relative))
}

fn normalize_link(root: &Path, parent: &Path, link: &Path) -> Result<PathBuf> {
    let mut target = parent.to_path_buf();
    for part in link.components() {
        match part {
            Component::Normal(value) => target.push(value),
            Component::CurDir => {}
            Component::ParentDir if target != root && target.starts_with(root) => {
                target.pop();
            }
            _ => bail!("Bottle link escapes its keg: {}", link.display()),
        }
    }
    if target == root || !target.starts_with(root) {
        bail!("Bottle link escapes its keg: {}", link.display());
    }
    Ok(target)
}

pub(crate) fn relocate(files: &[PathBuf], prefix: &Path, original_prefix: &str) -> Result<()> {
    let prefix = prefix.to_str().context("Bottle prefix is not UTF-8")?;
    for path in files {
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            let permissions = fs::metadata(path)?.permissions();
            fs::set_permissions(path, fs::Permissions::from_mode(permissions.mode() | 0o600))?;
            let result = relocate_file(path, prefix, original_prefix);
            fs::set_permissions(path, permissions)?;
            result?;
        }
        #[cfg(not(unix))]
        relocate_file(path, prefix, original_prefix)?;
    }
    Ok(())
}

fn relocate_file(path: &Path, prefix: &str, original_prefix: &str) -> Result<()> {
    if fs::metadata(path)?.len() > 256 * 1024 * 1024 {
        bail!(
            "Bottle relocation exceeds the 256 MiB per-file limit: {}",
            path.display()
        );
    }
    let bytes = fs::read(path)?;
    if bytes.starts_with(b"\x7fELF") {
        if !cfg!(target_os = "linux") {
            bail!("A Linux ELF bottle cannot be installed on this platform");
        }
        let path_text = path.to_str().context("Bottle path is not UTF-8")?;
        let rpath = run("patchelf", &["--print-rpath", path_text])?;
        let rpath = rpath.trim_end_matches(['\r', '\n']);
        let relocated = replace(rpath, prefix, original_prefix);
        if relocated != rpath {
            run("patchelf", &["--set-rpath", &relocated, path_text])?;
        }
        if has_elf_interpreter(&bytes)? {
            let interpreter = match std::env::consts::ARCH {
                "x86_64" => "/lib64/ld-linux-x86-64.so.2",
                "aarch64" => "/lib/ld-linux-aarch64.so.1",
                _ => bail!("Bottle relocation supports x64 and arm64 Linux"),
            };
            if !Path::new(interpreter).is_file() {
                bail!("The host glibc interpreter is missing: {interpreter}");
            }
            run("patchelf", &["--set-interpreter", interpreter, path_text])?;
        }
    } else if is_macho(&bytes) {
        if !cfg!(target_os = "macos") {
            bail!("A macOS Mach-O bottle cannot be installed on this platform");
        }
        relocate_macho(path, prefix, original_prefix)?;
    } else if !bytes.contains(&0)
        && let Ok(text) = std::str::from_utf8(&bytes)
    {
        let relocated = replace(text, prefix, original_prefix);
        if relocated != text {
            fs::write(path, relocated)?;
        }
    }
    Ok(())
}

fn replace(value: &str, prefix: &str, original_prefix: &str) -> String {
    value
        .replace(&format!("{original_prefix}/"), &format!("{prefix}/"))
        .replace("@@HOMEBREW_CELLAR@@", &format!("{prefix}/Cellar"))
        .replace("@@HOMEBREW_PREFIX@@", prefix)
}

fn has_elf_interpreter(bytes: &[u8]) -> Result<bool> {
    if bytes.len() < 64 || bytes[4] != 2 || bytes[5] != 1 {
        bail!("Bottle relocation requires a little-endian 64-bit ELF file");
    }
    let offset = usize::try_from(u64::from_le_bytes(bytes[32..40].try_into()?))?;
    let size = usize::from(u16::from_le_bytes(bytes[54..56].try_into()?));
    let count = usize::from(u16::from_le_bytes(bytes[56..58].try_into()?));
    if size < 56 || offset > bytes.len() || count * size > bytes.len() - offset {
        bail!("Invalid ELF program-header table");
    }
    Ok((0..count).any(|index| bytes[offset + index * size..].starts_with(&3u32.to_le_bytes())))
}

fn is_macho(bytes: &[u8]) -> bool {
    bytes.get(..4).is_some_and(|magic| {
        matches!(
            magic,
            [0xCF, 0xFA, 0xED, 0xFE]
                | [0xFE, 0xED, 0xFA, 0xCF]
                | [0xCA, 0xFE, 0xBA, 0xBE]
                | [0xBE, 0xBA, 0xFE, 0xCA]
                | [0xCA, 0xFE, 0xBA, 0xBF]
                | [0xBF, 0xBA, 0xFE, 0xCA]
        )
    })
}

fn relocate_macho(path: &Path, prefix: &str, original_prefix: &str) -> Result<()> {
    let path = path.to_str().context("Bottle path is not UTF-8")?;
    let mut changed = false;
    for line in run("/usr/bin/otool", &["-L", path])?.lines().skip(1) {
        let dependency = line.trim().split(" (").next().unwrap_or_default();
        let relocated = replace(dependency, prefix, original_prefix);
        if dependency != relocated {
            run("/usr/bin/install_name_tool", &["-change", dependency, &relocated, path])?;
            changed = true;
        }
    }
    let output = run("/usr/bin/otool", &["-l", path])?;
    let mut command = "";
    for line in output.lines().map(str::trim) {
        if let Some(value) = line.strip_prefix("cmd ") {
            command = value;
        }
        let field = match command {
            "LC_RPATH" => "path ",
            "LC_ID_DYLIB" => "name ",
            _ => continue,
        };
        if let Some(value) = line.strip_prefix(field) {
            let name = value.split(" (offset ").next().unwrap_or_default();
            let relocated = replace(name, prefix, original_prefix);
            if name != relocated {
                if command == "LC_RPATH" {
                    run("/usr/bin/install_name_tool", &["-rpath", name, &relocated, path])?;
                } else {
                    run("/usr/bin/install_name_tool", &["-id", &relocated, path])?;
                }
                changed = true;
            }
        }
    }
    if changed {
        run("/usr/bin/codesign", &["--force", "--sign", "-", path])?;
    }
    Ok(())
}

pub(crate) fn run(executable: &str, arguments: &[&str]) -> Result<String> {
    let output = Command::new(executable)
        .args(arguments)
        .output()
        .with_context(|| format!("Could not start bottle relocation tool {executable}"))?;
    if !output.status.success() {
        bail!(
            "Bottle relocation tool {executable} failed: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }
    String::from_utf8(output.stdout).map_err(|error| anyhow!("Invalid relocation tool output: {error}"))
}

#[cfg(all(test, unix))]
pub(crate) mod tests {
    use std::io::Cursor;
    use std::os::unix::fs::PermissionsExt as _;
    use std::sync::atomic::{AtomicU64, Ordering};

    use super::*;

    static NEXT_ID: AtomicU64 = AtomicU64::new(0);

    pub(crate) struct Fixture(pub(crate) PathBuf);

    impl Fixture {
        pub(crate) fn new() -> Result<Self> {
            let path = std::env::temp_dir().join(format!(
                "pinget-bottle-test-{}-{}",
                std::process::id(),
                NEXT_ID.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir(&path)?;
            Ok(Self(path))
        }

        pub(crate) fn write(&self, entries: &[(&str, Option<&str>)]) -> Result<()> {
            let gzip = flate2::write::GzEncoder::new(
                fs::File::create(self.0.join("bottle.tar.gz"))?,
                flate2::Compression::default(),
            );
            let mut writer = tar::Builder::new(gzip);
            for (name, target) in entries {
                let mut header = tar::Header::new_gnu();
                header.as_mut_bytes()[..name.len()].copy_from_slice(name.as_bytes());
                header.set_mode(0o755);
                if let Some(target) = target {
                    header.set_entry_type(tar::EntryType::Symlink);
                    header.set_size(0);
                    header.set_link_name(target)?;
                    header.set_cksum();
                    writer.append(&header, Cursor::new([]))?;
                } else {
                    header.set_entry_type(tar::EntryType::Regular);
                    header.set_size(1);
                    header.set_cksum();
                    writer.append(&header, Cursor::new(b"x"))?;
                }
            }
            writer.into_inner()?.finish()?;
            Ok(())
        }

        fn extract(&self) -> Result<Vec<PathBuf>> {
            extract(&self.0.join("bottle.tar.gz"), &self.0.join("keg"), "sample", "1.0")
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            if let Err(error) = fs::remove_dir_all(&self.0) {
                panic!("Failed to clean up bottle fixture: {error}");
            }
        }
    }

    #[test]
    fn preserves_executable_and_internal_link() -> Result<()> {
        let fixture = Fixture::new()?;
        fixture.write(&[
            ("sample/1.0/bin/sample", None),
            ("sample/1.0/bin/alias", Some("sample")),
        ])?;
        let files = fixture.extract()?;
        assert_eq!(files.len(), 1);
        assert_ne!(fs::metadata(&files[0])?.permissions().mode() & 0o100, 0);
        assert_eq!(fs::read_link(fixture.0.join("keg/bin/alias"))?, Path::new("sample"));
        Ok(())
    }

    #[test]
    fn rejects_escaping_entries() -> Result<()> {
        for (name, target) in [
            ("sample/1.0/../../escape", None),
            ("sample/1.0/link", Some("../../escape")),
            ("different/1.0/file", None),
        ] {
            let fixture = Fixture::new()?;
            fixture.write(&[(name, target)])?;
            assert!(fixture.extract().is_err(), "{name}");
            assert!(!fixture.0.join("escape").exists());
        }
        Ok(())
    }

    #[test]
    fn rejects_entries_under_archive_links() -> Result<()> {
        let fixture = Fixture::new()?;
        fixture.write(&[
            ("sample/1.0/target/file", None),
            ("sample/1.0/link", Some("target")),
            ("sample/1.0/link/file", None),
        ])?;
        assert!(fixture.extract().is_err());
        Ok(())
    }

    #[test]
    fn relocates_text_without_relocating_inserted_prefix_twice() -> Result<()> {
        let fixture = Fixture::new()?;
        let path = fixture.0.join("sample.pc");
        fs::write(
            &path,
            "prefix=@@HOMEBREW_PREFIX@@\ncellar=@@HOMEBREW_CELLAR@@\nold=/old/lib\n",
        )?;
        relocate(std::slice::from_ref(&path), Path::new("/old/private"), "/old")?;
        assert_eq!(
            fs::read_to_string(path)?,
            "prefix=/old/private\ncellar=/old/private/Cellar\nold=/old/private/lib\n"
        );
        Ok(())
    }

    #[test]
    fn relocates_known_fixed_cellar_paths_into_the_private_prefix() -> Result<()> {
        let fixture = Fixture::new()?;
        let path = fixture.0.join("fixed-cellar.txt");
        fs::write(&path, "/opt/homebrew/Cellar/sample/1.0/bin/sample\n")?;
        relocate(
            std::slice::from_ref(&path),
            Path::new("/private/pinget"),
            "/opt/homebrew",
        )?;
        assert_eq!(
            fs::read_to_string(path)?,
            "/private/pinget/Cellar/sample/1.0/bin/sample\n"
        );
        Ok(())
    }

    #[test]
    fn relocates_read_only_files_and_restores_mode_on_failure() -> Result<()> {
        let fixture = Fixture::new()?;
        let path = fixture.0.join("readonly.pc");
        fs::write(&path, "prefix=@@HOMEBREW_PREFIX@@")?;
        fs::set_permissions(&path, fs::Permissions::from_mode(0o444))?;
        relocate(std::slice::from_ref(&path), Path::new("/private"), "/old")?;
        assert_eq!(fs::read_to_string(&path)?, "prefix=/private");
        assert_eq!(fs::metadata(&path)?.permissions().mode() & 0o777, 0o444);
        fs::set_permissions(&path, fs::Permissions::from_mode(0o644))?;
        fs::write(&path, b"\x7fELF")?;
        fs::set_permissions(&path, fs::Permissions::from_mode(0o444))?;
        assert!(relocate(std::slice::from_ref(&path), Path::new("/private"), "/old").is_err());
        assert_eq!(fs::metadata(&path)?.permissions().mode() & 0o777, 0o444);
        Ok(())
    }
}
