use std::fs::{self, File};
use std::io::Read;
use std::path::Path;
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, bail, ensure};

const MAX_ARCHIVE_BYTES: u64 = 10 * 1024 * 1024;
const GIT_TIMEOUT: Duration = Duration::from_secs(120);

fn run_git(dir: &Path, args: &[&str], archive: Option<&Path>, protocol: &str) -> Result<()> {
    let error_path = dir.join("git-error.log");
    let error_file = File::create(&error_path)?;
    let mut command = Command::new("git");
    command
        .current_dir(dir)
        .args(args)
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GIT_ALLOW_PROTOCOL", protocol)
        .env("GIT_LFS_SKIP_SMUDGE", "1")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::from(error_file));
    let mut child = command
        .spawn()
        .context("Git is required to install a Git-based AgentSkill")?;
    let deadline = Instant::now() + GIT_TIMEOUT;
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break status;
        }
        if archive.is_some_and(|path| fs::metadata(path).is_ok_and(|meta| meta.len() > MAX_ARCHIVE_BYTES)) {
            child.kill()?;
            child.wait()?;
            bail!("Git skill archive exceeds the 10 MiB download limit");
        }
        if Instant::now() >= deadline {
            child.kill()?;
            child.wait()?;
            bail!("Git skill operation timed out after 120 seconds");
        }
        thread::sleep(Duration::from_millis(50));
    };
    if !status.success() {
        let mut error = Vec::new();
        File::open(&error_path)?.take(4096).read_to_end(&mut error)?;
        bail!("Git skill operation failed: {}", String::from_utf8_lossy(&error).trim());
    }
    Ok(())
}

pub(super) fn fetch_git_zip(
    url: &str,
    commit: &str,
    skill_path: &str,
    plugin_manifest: Option<&str>,
) -> Result<Vec<u8>> {
    fetch_git_zip_with_protocol(url, commit, skill_path, plugin_manifest, "https")
}

fn fetch_git_zip_with_protocol(
    url: &str,
    commit: &str,
    skill_path: &str,
    plugin_manifest: Option<&str>,
    protocol: &str,
) -> Result<Vec<u8>> {
    let nonce = SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos();
    let temp = std::env::temp_dir().join(format!("pinget-skill-git-{}-{nonce}", std::process::id()));
    fs::create_dir(&temp).with_context(|| format!("failed to create Git staging directory {}", temp.display()))?;
    let result = (|| {
        run_git(&temp, &["init", "--quiet"], None, protocol)?;
        run_git(
            &temp,
            &["fetch", "--quiet", "--no-tags", "--depth=1", url, commit],
            None,
            protocol,
        )?;
        let output = Command::new("git")
            .current_dir(&temp)
            .args(["rev-parse", "--verify", "FETCH_HEAD^{commit}"])
            .output()
            .context("failed to verify fetched Git commit")?;
        ensure!(output.status.success(), "fetched Git reference is not a commit");
        ensure!(
            String::from_utf8(output.stdout)?.trim().eq_ignore_ascii_case(commit),
            "fetched Git commit does not match the manifest"
        );
        let archive = temp.join("skill.zip");
        let output_arg = format!("--output={}", archive.display());
        let mut args = vec!["archive", "--format=zip", &output_arg, "FETCH_HEAD"];
        if !skill_path.is_empty() {
            args.extend(["--", skill_path]);
        }
        if let Some(plugin_manifest) = plugin_manifest {
            if skill_path.is_empty() {
                args.push("--");
            }
            args.push(plugin_manifest);
        }
        run_git(&temp, &args, Some(&archive), protocol)?;
        let bytes = fs::read(&archive).context("failed to read Git skill archive")?;
        ensure!(
            bytes.len() as u64 <= MAX_ARCHIVE_BYTES,
            "Git skill archive exceeds the 10 MiB download limit"
        );
        Ok(bytes)
    })();
    let cleanup = fs::remove_dir_all(&temp);
    if let Err(error) = cleanup
        && result.is_ok()
    {
        return Err(error).context("failed to clean up Git skill staging directory");
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fetches_pinned_skill_and_plugin_manifest_without_other_repo_files() {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock time")
            .as_nanos();
        let root = std::env::temp_dir().join(format!("pinget-git-test-{}-{nonce}", std::process::id()));
        let repo = root.join("repo");
        fs::create_dir_all(repo.join("skills/demo")).expect("create fixture repository");
        let git = |args: &[&str]| {
            let output = Command::new("git")
                .args(args)
                .current_dir(&repo)
                .output()
                .expect("run Git");
            assert!(
                output.status.success(),
                "Git failed: {}",
                String::from_utf8_lossy(&output.stderr)
            );
            String::from_utf8(output.stdout)
                .expect("Git output is UTF-8")
                .trim()
                .to_owned()
        };
        git(&["init", "--quiet"]);
        git(&["config", "user.name", "Pinget Test"]);
        git(&["config", "user.email", "pinget@example.test"]);
        fs::write(
            repo.join("skills/demo/SKILL.md"),
            b"---\nname: demo\ndescription: Demo\n---\nInstructions\n",
        )
        .expect("write skill");
        fs::write(
            repo.join("plugin.json"),
            br#"{"$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json","name":"example.plugin"}"#,
        )
        .expect("write plugin");
        fs::write(repo.join("unrelated.txt"), b"not part of selected skill").expect("write unrelated file");
        git(&["add", "."]);
        git(&["commit", "--quiet", "-m", "Fixture"]);
        let commit = git(&["rev-parse", "HEAD"]);
        let url = reqwest::Url::from_file_path(&repo).expect("file URL").to_string();
        let zip = fetch_git_zip_with_protocol(&url, &commit, "skills/demo", Some("plugin.json"), "file")
            .expect("fetch pinned skill");
        let mut archive = zip::ZipArchive::new(std::io::Cursor::new(zip)).expect("read archive");
        assert!(archive.by_name("skills/demo/SKILL.md").is_ok());
        assert!(archive.by_name("plugin.json").is_ok());
        assert!(archive.by_name("unrelated.txt").is_err());
        fs::remove_dir_all(root).expect("remove fixture repository");
    }
}
