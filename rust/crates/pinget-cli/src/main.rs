#![allow(clippy::print_stdout)]

use std::fmt;
use std::io::{self, IsTerminal, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};

use anyhow::{Result, anyhow, bail};
use clap::{Args, Parser, Subcommand};
use pinget_core::{
    CacheWarmResult, Documentation, InstallProgress, InstallRequest, InstallResult, Installer, InstallerMode,
    ListMatch, ListQuery, ListResponse, Manifest, PackageAgreement, PackageQuery, PinRecord, PinType, RepairRequest,
    Repository, RepositoryOptions, SearchMatch, SearchResponse, ShowResult, SourceKind, SourceRecord,
    SourceUpdateResult, UninstallRequest, VersionsResult, pin_blocks_upgrade,
};
use pinget_publisher::{BuildOptions as PublisherBuildOptions, PublishOptions as PublisherPublishOptions, Signing};

mod error_codes;
mod file_hash;

const VERSION: &str = env!("CARGO_PKG_VERSION");
const UPGRADE_UNSUPPORTED_WARNING: &str = "Upgrading packages is not supported on this platform; no changes were made.";
const COUNT_OUT_OF_BOUNDS_ERROR: &str = "The requested number of results must be between 1 and 1000.";
const NO_APPLICABLE_UPGRADE_MESSAGE: &str = "No applicable upgrade found.";

/// `--count` accepts 1..=1000, as in WinGet.
fn parse_count(value: &str) -> std::result::Result<usize, String> {
    match value.trim().parse::<usize>() {
        Ok(count) if (1..=1000).contains(&count) => Ok(count),
        _ => Err(COUNT_OUT_OF_BOUNDS_ERROR.to_owned()),
    }
}

/// A named `upgrade` that selected nothing is an error (WinGet reports
/// `APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE`); `upgrade --all` with nothing to do is not.
fn no_applicable_upgrade(all: bool) -> Result<()> {
    if all {
        println!("{NO_APPLICABLE_UPGRADE_MESSAGE}");
        Ok(())
    } else {
        bail!("{NO_APPLICABLE_UPGRADE_MESSAGE}")
    }
}

#[derive(Parser)]
#[command(name = "pinget", about = "Pinget: portable winget in pure Rust", version = VERSION)]
struct Cli {
    #[command(subcommand)]
    command: Option<Commands>,
    #[arg(long = "info", global = true)]
    info: bool,
    #[arg(long = "output", short = 'o', global = true, value_parser = ["json", "text", "yaml"])]
    output: Option<String>,
    #[command(flatten)]
    common: CommonArgs,
}

/// Flags WinGet accepts on every command (`Argument::GetCommon`) plus
/// `--accept-source-agreements`, so scripts written for WinGet parse unchanged.
#[derive(Args, Clone, Copy, Debug, Default)]
struct CommonArgs {
    /// Suppress prompts; package agreements must then be accepted on the command line.
    #[arg(long = "disable-interactivity", global = true)]
    disable_interactivity: bool,
    /// Suppress warning output.
    #[arg(long = "ignore-warnings", visible_alias = "nowarn", global = true)]
    ignore_warnings: bool,
    // The flags below are accepted for compatibility and have no effect in Pinget.
    #[arg(long = "accept-source-agreements", global = true, hide = true)]
    #[allow(dead_code)]
    accept_source_agreements: bool,
    #[arg(long = "wait", global = true, hide = true)]
    #[allow(dead_code)]
    wait: bool,
    #[arg(long = "verbose-logs", visible_alias = "verbose", global = true, hide = true)]
    #[allow(dead_code)]
    verbose_logs: bool,
    #[arg(long = "no-vt", global = true, hide = true)]
    #[allow(dead_code)]
    no_vt: bool,
    #[arg(long = "no-progress", global = true, hide = true)]
    #[allow(dead_code)]
    no_progress: bool,
}

static IGNORE_WARNINGS: AtomicBool = AtomicBool::new(false);
static DISABLE_INTERACTIVITY: AtomicBool = AtomicBool::new(false);

fn apply_common_args(common: CommonArgs) {
    IGNORE_WARNINGS.store(common.ignore_warnings, Ordering::Relaxed);
    DISABLE_INTERACTIVITY.store(common.disable_interactivity, Ordering::Relaxed);
}

fn warnings_enabled() -> bool {
    !IGNORE_WARNINGS.load(Ordering::Relaxed)
}

/// The agreements prompt needs a terminal on both ends and must not run under `--disable-interactivity`.
fn agreements_prompt_allowed(disable_interactivity: bool, stdin_is_terminal: bool, stderr_is_terminal: bool) -> bool {
    !disable_interactivity && stdin_is_terminal && stderr_is_terminal
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum OutputFormat {
    Text,
    Json,
    Yaml,
}

impl OutputFormat {
    fn from_option(value: Option<&str>) -> Result<Self> {
        match value.unwrap_or("text") {
            "text" => Ok(Self::Text),
            "json" => Ok(Self::Json),
            "yaml" => Ok(Self::Yaml),
            other => bail!("unsupported output format: {other}"),
        }
    }

    fn is_text(self) -> bool {
        matches!(self, Self::Text)
    }
}

#[derive(Subcommand)]
enum Commands {
    #[command(alias = "ls")]
    List(ListArgs),
    Show(ShowArgs),
    Search(SearchArgs),
    #[command(alias = "update")]
    Upgrade(UpgradeArgs),
    Source {
        #[command(subcommand)]
        command: SourceCommands,
    },
    Cache {
        #[command(subcommand)]
        command: CacheCommands,
    },
    Hash(HashArgs),
    Export(ExportArgs),
    #[command(name = "error")]
    ErrorLookup(ErrorArgs),
    #[command(alias = "config")]
    Settings {
        #[command(subcommand)]
        command: Option<SettingsCommands>,
        #[arg(long)]
        enable: Option<String>,
        #[arg(long)]
        disable: Option<String>,
    },
    Features,
    Validate(ValidateArgs),
    #[command(alias = "dl")]
    Download(DownloadArgs),
    Pin {
        #[command(subcommand)]
        command: PinCommands,
    },
    Install(InstallArgs),
    Uninstall(UninstallArgs),
    #[command(alias = "fix")]
    Repair(RepairArgs),
    Import(ImportArgs),
    Repository {
        #[command(subcommand)]
        command: RepositoryCommands,
    },
}

#[derive(Subcommand)]
enum RepositoryCommands {
    Build(RepositoryBuildArgs),
    Diff(RepositoryBuildArgs),
    Status(RepositoryStateArgs),
    Verify(RepositoryVerifyArgs),
    Publish(RepositoryPublishArgs),
    Doctor(RepositoryDoctorArgs),
}

#[derive(Args, Clone)]
struct RepositoryBuildArgs {
    #[arg(long = "input", visible_alias = "manifests")]
    input: PathBuf,
    #[arg(long)]
    state: PathBuf,
}

#[derive(Args)]
struct RepositoryStateArgs {
    #[arg(long)]
    state: PathBuf,
}

#[derive(Args)]
struct RepositoryVerifyArgs {
    #[arg(long, required_unless_present = "destination", conflicts_with = "destination")]
    state: Option<PathBuf>,
    #[arg(long)]
    build: Option<String>,
    #[arg(long, conflicts_with = "build")]
    destination: Option<PathBuf>,
    #[arg(long = "psign-tool", requires = "destination")]
    psign_tool: Option<PathBuf>,
    #[arg(long = "trusted-ca", requires = "destination", value_name = "CERTIFICATE")]
    trusted_ca: Vec<PathBuf>,
}

#[derive(Args)]
struct RepositoryPublishArgs {
    #[arg(long)]
    state: PathBuf,
    #[arg(long)]
    destination: PathBuf,
    #[arg(long)]
    name: String,
    #[arg(long)]
    publisher: String,
    #[arg(long = "version")]
    package_version: String,
    #[arg(long)]
    build: Option<String>,
    #[arg(long = "pfx", requires = "password_env", conflicts_with = "unsigned_development")]
    pfx: Option<PathBuf>,
    #[arg(long = "password-env", requires = "pfx", value_name = "ENVIRONMENT_VARIABLE")]
    password_env: Option<String>,
    #[arg(
        long = "unsigned-development",
        required_unless_present = "pfx",
        conflicts_with_all = ["pfx", "password_env"]
    )]
    unsigned_development: bool,
    #[arg(long = "psign-tool")]
    psign_tool: Option<PathBuf>,
    #[arg(long = "trusted-ca", value_name = "CERTIFICATE")]
    trusted_ca: Vec<PathBuf>,
    #[arg(long, requires = "pfx", conflicts_with = "unsigned_development")]
    timestamp_url: Option<String>,
}

#[derive(Args)]
struct RepositoryDoctorArgs {
    #[arg(long = "psign-tool")]
    psign_tool: Option<PathBuf>,
}

#[derive(Subcommand)]
enum SourceCommands {
    List,
    Update {
        source: Option<String>,
    },
    Export,
    Add(SourceAddArgs),
    #[command(alias = "config", alias = "set")]
    Edit(SourceEditArgs),
    Remove {
        name: String,
    },
    Reset {
        #[arg(short = 'n', long = "name")]
        name: Option<String>,
        #[arg(long)]
        force: bool,
    },
}

#[derive(Subcommand)]
enum SettingsCommands {
    Export,
    Set(SettingsSetArgs),
    Reset(SettingsResetArgs),
}

#[derive(Subcommand)]
enum CacheCommands {
    Warm(QueryArgs),
}

#[derive(Args)]
struct ShowArgs {
    #[command(flatten)]
    query: QueryArgs,
    #[arg(long = "versions")]
    versions: bool,
}

#[derive(Args, Clone)]
struct UpgradeArgs {
    #[arg(conflicts_with = "query_option")]
    query: Option<String>,
    #[arg(long = "query", short = 'q', value_name = "QUERY", conflicts_with = "query")]
    query_option: Option<String>,
    #[arg(long)]
    id: Option<String>,
    #[arg(long)]
    name: Option<String>,
    #[arg(long)]
    moniker: Option<String>,
    #[arg(long, short = 's')]
    source: Option<String>,
    /// Version to upgrade to. Applies to the install, not to the installed-package filter.
    #[arg(long, short = 'v')]
    version: Option<String>,
    #[arg(long)]
    locale: Option<String>,
    #[arg(long = "installer-type")]
    installer_type: Option<String>,
    #[arg(long = "architecture", short = 'a')]
    installer_architecture: Option<String>,
    #[arg(long = "platform")]
    platform: Option<String>,
    #[arg(long = "os-version")]
    os_version: Option<String>,
    #[arg(long = "scope")]
    install_scope: Option<String>,
    #[arg(long, short = 'n', value_parser = parse_count)]
    count: Option<usize>,
    #[arg(long, short = 'e')]
    exact: bool,
    #[arg(long = "include-unknown", short = 'u', visible_alias = "unknown")]
    include_unknown: bool,
    #[arg(long = "include-pinned", visible_alias = "pinned")]
    include_pinned: bool,
    #[arg(long = "ignore-security-hash")]
    ignore_security_hash: bool,
    #[arg(long = "dependency-source")]
    dependency_source: Option<String>,
    #[arg(long = "accept-package-agreements")]
    accept_package_agreements: bool,
    #[arg(long)]
    all: bool,
    #[arg(long)]
    silent: bool,
}

#[derive(Args)]
struct HashArgs {
    file: String,
    #[arg(long)]
    msix: bool,
}

#[derive(Args)]
struct ExportArgs {
    #[arg(long, short = 'o')]
    output: String,
    #[arg(long, short = 's')]
    source: Option<String>,
    #[arg(long = "include-versions")]
    include_versions: bool,
}

#[derive(Args)]
struct ErrorArgs {
    #[arg(allow_negative_numbers = true)]
    input: String,
}

#[derive(Args)]
struct ValidateArgs {
    manifest: String,
}

#[derive(Args, Clone)]
struct DownloadArgs {
    #[command(flatten)]
    query: QueryArgs,
    #[arg(long = "manifest")]
    manifest: Option<PathBuf>,
    #[arg(short = 'd', long = "download-directory")]
    download_directory: Option<String>,
    #[arg(long = "ignore-security-hash")]
    ignore_security_hash: bool,
    #[arg(long = "accept-package-agreements")]
    accept_package_agreements: bool,
}

#[derive(Subcommand)]
enum PinCommands {
    List(PinListArgs),
    Add(PinAddArgs),
    Remove(PinRemoveArgs),
    Reset {
        #[arg(long)]
        force: bool,
        #[arg(long, short = 's')]
        source: Option<String>,
    },
}

#[derive(Args)]
struct SourceAddArgs {
    name: Option<String>,
    arg: Option<String>,
    #[arg(short = 'n', long = "name")]
    name_option: Option<String>,
    #[arg(short = 'a', long = "arg")]
    arg_option: Option<String>,
    /// Source type; WinGet treats an omitted type as `Microsoft.PreIndexed.Package`.
    #[arg(short = 't', long = "type", default_value = "Microsoft.PreIndexed.Package")]
    kind: String,
    #[arg(long = "trust-level")]
    trust_level: Option<String>,
    #[arg(long)]
    explicit: bool,
}

#[derive(Args)]
struct SourceEditArgs {
    #[arg(short = 'n', long = "name")]
    name: String,
    #[arg(short = 'e', long = "explicit")]
    explicit: Option<bool>,
}

#[derive(Args, Clone)]
struct PinQueryArgs {
    #[arg(conflicts_with = "query_option")]
    query: Option<String>,
    #[arg(long = "query", short = 'q', value_name = "QUERY", conflicts_with = "query")]
    query_option: Option<String>,
    #[arg(long)]
    id: Option<String>,
    #[arg(long)]
    name: Option<String>,
    #[arg(long)]
    moniker: Option<String>,
    #[arg(long)]
    tag: Option<String>,
    #[arg(long)]
    command: Option<String>,
    #[arg(long, short = 's')]
    source: Option<String>,
    #[arg(long, short = 'e')]
    exact: bool,
}

#[derive(Args, Clone)]
struct PinListArgs {
    #[command(flatten)]
    query: PinQueryArgs,
}

#[derive(Args, Clone)]
struct PinAddArgs {
    #[command(flatten)]
    query: PinQueryArgs,
    #[arg(long)]
    version: Option<String>,
    #[arg(long)]
    blocking: bool,
    #[arg(long)]
    installed: bool,
    #[arg(long)]
    force: bool,
}

#[derive(Args, Clone)]
struct PinRemoveArgs {
    #[command(flatten)]
    query: PinQueryArgs,
    #[arg(long)]
    installed: bool,
}

#[derive(Args)]
struct SettingsSetArgs {
    #[arg(long = "setting")]
    setting: String,
    #[arg(long = "value")]
    value: String,
}

#[derive(Args)]
struct SettingsResetArgs {
    #[arg(long = "setting")]
    setting: Option<String>,
    #[arg(short = 'r', long = "recurse", alias = "all")]
    all: bool,
}

#[derive(Args, Clone)]
struct InstallArgs {
    #[command(flatten)]
    query: QueryArgs,
    #[arg(long = "manifest")]
    manifest: Option<PathBuf>,
    #[arg(long = "log")]
    log: Option<PathBuf>,
    #[arg(long = "custom")]
    custom: Option<String>,
    #[arg(long = "override")]
    override_args: Option<String>,
    #[arg(long = "location")]
    location: Option<String>,
    #[arg(long = "skip-dependencies")]
    skip_dependencies: bool,
    #[arg(long = "dependencies")]
    dependencies_only: bool,
    #[arg(long = "accept-package-agreements")]
    accept_package_agreements: bool,
    #[arg(long = "force")]
    force: bool,
    #[arg(long = "rename")]
    rename: Option<String>,
    #[arg(long = "uninstall-previous")]
    uninstall_previous: bool,
    #[arg(long = "ignore-security-hash")]
    ignore_security_hash: bool,
    #[arg(long = "dependency-source")]
    dependency_source: Option<String>,
    #[arg(long = "no-upgrade")]
    no_upgrade: bool,
    #[arg(long, conflicts_with = "interactive")]
    silent: bool,
    #[arg(long, conflicts_with = "silent")]
    interactive: bool,
}

#[derive(Args, Clone)]
struct UninstallArgs {
    #[command(flatten)]
    query: QueryArgs,
    #[arg(long = "manifest")]
    manifest: Option<PathBuf>,
    #[arg(long = "product-code")]
    product_code: Option<String>,
    #[arg(long = "all-versions")]
    all_versions: bool,
    #[arg(long, conflicts_with = "silent")]
    interactive: bool,
    #[arg(long)]
    silent: bool,
    #[arg(long = "force")]
    force: bool,
    #[arg(long = "purge")]
    purge: bool,
    #[arg(long = "preserve")]
    preserve: bool,
    #[arg(long = "log")]
    log: Option<PathBuf>,
}

#[derive(Args, Clone)]
struct RepairArgs {
    #[command(flatten)]
    query: QueryArgs,
    #[arg(long = "manifest")]
    manifest: Option<PathBuf>,
    #[arg(long = "product-code")]
    product_code: Option<String>,
    #[arg(long = "log")]
    log: Option<PathBuf>,
    #[arg(long = "accept-package-agreements")]
    accept_package_agreements: bool,
    #[arg(long = "ignore-security-hash")]
    ignore_security_hash: bool,
    #[arg(long = "force")]
    force: bool,
    #[arg(long, conflicts_with = "interactive")]
    silent: bool,
    #[arg(long, conflicts_with = "silent")]
    interactive: bool,
}

#[derive(Args)]
struct ImportArgs {
    #[arg(short = 'i', long = "import-file")]
    import_file: String,
    #[arg(long = "dry-run")]
    dry_run: bool,
    #[arg(long = "ignore-unavailable")]
    ignore_unavailable: bool,
    #[arg(long = "ignore-versions")]
    ignore_versions: bool,
    #[arg(long = "no-upgrade")]
    no_upgrade: bool,
    #[arg(long = "accept-package-agreements")]
    accept_package_agreements: bool,
}

#[derive(Args, Clone)]
struct QueryArgs {
    #[arg(conflicts_with = "query_option")]
    query: Option<String>,
    #[arg(long = "query", short = 'q', value_name = "QUERY", conflicts_with = "query")]
    query_option: Option<String>,
    #[arg(long)]
    id: Option<String>,
    #[arg(long)]
    name: Option<String>,
    #[arg(long)]
    moniker: Option<String>,
    #[arg(long, short = 's')]
    source: Option<String>,
    #[arg(long, short = 'e')]
    exact: bool,
    #[arg(long, short = 'v')]
    version: Option<String>,
    #[arg(long, short = 'c')]
    channel: Option<String>,
    #[arg(long)]
    locale: Option<String>,
    #[arg(long = "installer-type")]
    installer_type: Option<String>,
    #[arg(long = "architecture", short = 'a')]
    installer_architecture: Option<String>,
    #[arg(long = "platform")]
    platform: Option<String>,
    #[arg(long = "os-version")]
    os_version: Option<String>,
    #[arg(long = "scope")]
    install_scope: Option<String>,
}

#[derive(Args, Clone)]
struct SearchArgs {
    #[arg(conflicts_with = "query_option")]
    query: Option<String>,
    #[arg(long = "query", short = 'q', value_name = "QUERY", conflicts_with = "query")]
    query_option: Option<String>,
    #[arg(long)]
    id: Option<String>,
    #[arg(long)]
    name: Option<String>,
    #[arg(long)]
    moniker: Option<String>,
    #[arg(long)]
    tag: Option<String>,
    #[arg(long, visible_alias = "cmd")]
    command: Option<String>,
    #[arg(long, short = 's')]
    source: Option<String>,
    #[arg(long, short = 'n', value_parser = parse_count)]
    count: Option<usize>,
    #[arg(long, short = 'e')]
    exact: bool,
    #[arg(long = "versions")]
    versions: bool,
    #[arg(long = "manifests", conflicts_with = "versions")]
    manifests: bool,
}

#[derive(Args, Clone)]
struct ListArgs {
    #[arg(conflicts_with = "query_option")]
    query: Option<String>,
    #[arg(long = "query", short = 'q', value_name = "QUERY", conflicts_with = "query")]
    query_option: Option<String>,
    #[arg(long)]
    id: Option<String>,
    #[arg(long)]
    name: Option<String>,
    #[arg(long)]
    moniker: Option<String>,
    #[arg(long)]
    tag: Option<String>,
    #[arg(long, visible_alias = "cmd")]
    command: Option<String>,
    #[arg(long, short = 's')]
    source: Option<String>,
    #[arg(long, short = 'n', value_parser = parse_count)]
    count: Option<usize>,
    #[arg(long, short = 'e')]
    exact: bool,
    #[arg(long = "scope")]
    install_scope: Option<String>,
    #[arg(long = "upgrade-available")]
    upgrade: bool,
    #[arg(long = "include-unknown", short = 'u', visible_alias = "unknown")]
    include_unknown: bool,
    #[arg(long = "include-pinned", visible_alias = "pinned")]
    include_pinned: bool,
    #[arg(long = "details")]
    details: bool,
}

fn main() {
    if let Err(error) = run() {
        write_stderr_line(format_args!("error: {error:#}"));
        std::process::exit(1);
    }
}

fn resolve_output_format(cli: &Cli) -> Result<OutputFormat> {
    // `export` defines its own `--output`/`-o` (the destination file, as in WinGet). Clap
    // shares the arg id with the global output-format flag, so the file path also lands in
    // `cli.output`; it is not an output format there.
    if matches!(cli.command, Some(Commands::Export(_))) {
        return Ok(OutputFormat::Text);
    }
    OutputFormat::from_option(cli.output.as_deref())
}

fn run() -> Result<()> {
    let cli = Cli::parse();
    apply_common_args(cli.common);
    let output = resolve_output_format(&cli)?;

    if cli.info {
        print_info();
        return Ok(());
    }

    let command = match cli.command {
        Some(command) => command,
        None => {
            Cli::parse_from(["winget", "--help"]);
            return Ok(());
        }
    };

    match command {
        Commands::List(args) => {
            let mut repository = Repository::open()?;
            let details = args.details;
            let upgrade = args.upgrade;
            let result = repository.list(&args.into())?;
            if output.is_text() {
                print_list_result(result, details, upgrade);
            } else {
                print_serialized(&result, output)?;
            }
        }
        Commands::Show(args) => {
            let mut repository = Repository::open()?;
            if args.versions {
                let result = repository.show_versions(&args.query.into())?;
                if output.is_text() {
                    print_versions(result);
                } else {
                    print_serialized(&result, output)?;
                }
            } else {
                let result = repository.show(&args.query.into())?;
                if output.is_text() {
                    print_show(result);
                } else {
                    print_manifest_serialized(&result.structured_document(), output)?;
                }
            }
        }
        Commands::Search(args) => {
            let mut repository = Repository::open()?;
            if args.manifests {
                if output.is_text() {
                    bail!("--manifests requires --output json or yaml");
                }

                let result = repository.search_manifests(&args.into())?;
                print_serialized(&result, output)?;
            } else if args.versions {
                let result = repository.search_versions(&args.into())?;
                if output.is_text() {
                    print_versions(result);
                } else {
                    print_serialized(&result, output)?;
                }
            } else {
                let result = repository.search(&args.into())?;
                if output.is_text() {
                    print_search(result);
                } else {
                    print_serialized(&result, output)?;
                }
            }
        }
        Commands::Upgrade(args) => {
            let do_install = args.all || has_explicit_upgrade_selector(&args);
            let mut repository = if do_install {
                open_repository_with_install_progress()?
            } else {
                Repository::open()?
            };
            if do_install && !cfg!(windows) {
                print_warnings(&[UPGRADE_UNSUPPORTED_WARNING.to_owned()]);
                println!("No changes were made.");
                return Ok(());
            }

            let mut list_query = ListQuery::from(args.clone());
            let explicit_selector = has_explicit_upgrade_selector(&args);
            list_query.include_pinned = list_query.include_pinned || explicit_selector;
            // A named upgrade checks pins below against the requested target
            // version and reports a pinned package; the list itself only hides
            // pinned rows for implicit upgrades.
            let result = if explicit_selector {
                repository.list_ignoring_pins(&list_query)?
            } else {
                repository.list(&list_query)?
            };
            let mode = if args.silent {
                InstallerMode::Silent
            } else {
                InstallerMode::SilentWithProgress
            };

            if !do_install {
                if output.is_text() {
                    print_list_result(result, false, true);
                } else {
                    print_serialized(&result, output)?;
                }
            } else {
                let upgradeable = select_upgrade_targets(&result.matches, &args)?;
                if upgradeable.is_empty() {
                    no_applicable_upgrade(args.all)?;
                } else {
                    let pins = repository.list_pins(None)?;
                    let mut success_count = 0usize;
                    let mut failure_count = 0usize;
                    for m in &upgradeable {
                        println!(
                            "Upgrading {} from {} to {} ...",
                            m.id,
                            m.installed_version,
                            m.available_version.as_deref().unwrap_or("?")
                        );
                        let target_version = args.version.as_deref().or(m.available_version.as_deref());
                        if let Some(message) = find_matching_pin(m, &pins).and_then(|pin| {
                            upgrade_pin_block_message(
                                pin,
                                target_version.unwrap_or_default(),
                                list_query.include_pinned,
                            )
                        }) {
                            println!("  {message}");
                            failure_count += 1;
                            continue;
                        }
                        let query = PackageQuery {
                            id: Some(m.id.clone()),
                            source: m.source_name.clone(),
                            exact: true,
                            version: args.version.clone(),
                            locale: args.locale.clone(),
                            installer_type: args.installer_type.clone(),
                            installer_architecture: args.installer_architecture.clone(),
                            platform: args.platform.clone(),
                            os_version: args.os_version.clone(),
                            install_scope: args.install_scope.clone(),
                            ..PackageQuery::default()
                        };
                        match repository.install_request(&InstallRequest {
                            query,
                            manifest_path: None,
                            mode,
                            log_path: None,
                            custom: None,
                            override_args: None,
                            install_location: None,
                            skip_dependencies: false,
                            dependencies_only: false,
                            accept_package_agreements: args.accept_package_agreements,
                            force: false,
                            rename: None,
                            uninstall_previous: false,
                            ignore_security_hash: args.ignore_security_hash,
                            dependency_source: args.dependency_source.clone(),
                            no_upgrade: false,
                        }) {
                            Ok(r) if r.no_op => {
                                print_warnings(&r.warnings);
                                println!("  No changes were made for {}", m.id);
                            }
                            Ok(r) if r.success => {
                                print_warnings(&r.warnings);
                                println!("  Successfully upgraded {}", m.id);
                                success_count += 1;
                            }
                            Ok(r) => {
                                print_warnings(&r.warnings);
                                write_stderr_line(format_args!(
                                    "  Failed to upgrade {} (exit code: {})",
                                    m.id, r.exit_code
                                ));
                                failure_count += 1;
                            }
                            Err(e) => {
                                write_stderr_line(format_args!("  Error upgrading {}: {e}", m.id));
                                failure_count += 1;
                            }
                        }
                    }
                    println!("{} of {} package(s) upgraded.", success_count, upgradeable.len());
                    if failure_count > 0 {
                        // Surface the failure to the caller (UniGetUI, scripts, etc.)
                        // so they don't treat a partial failure as success.
                        bail!("{failure_count} package(s) failed to upgrade during upgrade --all");
                    }
                }
            }
        }
        Commands::Source { command } => {
            let mut repository = Repository::open()?;
            match command {
                SourceCommands::List => print_sources(repository.list_sources()),
                SourceCommands::Update { source } => {
                    print_source_updates(repository.update_sources(source.as_deref())?)?
                }
                SourceCommands::Export => print_source_export(&repository)?,
                SourceCommands::Add(args) => {
                    let name = resolve_source_add_value(args.name.as_deref(), args.name_option.as_deref(), "name")?;
                    let arg = resolve_source_add_value(args.arg.as_deref(), args.arg_option.as_deref(), "argument")?;
                    let kind = parse_source_kind(&args.kind)?;
                    repository.add_source_with_metadata(
                        &name,
                        &arg,
                        kind,
                        args.trust_level.as_deref(),
                        args.explicit,
                        0,
                    )?;
                    println!("Done");
                }
                SourceCommands::Edit(args) => {
                    let explicit = args
                        .explicit
                        .ok_or_else(|| anyhow!("source edit requires --explicit true|false"))?;
                    repository.edit_source(&args.name, Some(explicit), None)?;
                    println!("Done");
                }
                SourceCommands::Remove { name } => {
                    repository.remove_source(&name)?;
                    println!("Done");
                }
                SourceCommands::Reset { name, force } => {
                    if let Some(name) = name {
                        repository.reset_source(&name)?;
                    } else {
                        if !force {
                            bail!("Resetting all sources requires --force");
                        }
                        repository.reset_sources()?;
                    }
                    println!("Done");
                }
            }
        }
        Commands::Cache { command } => {
            let mut repository = Repository::open()?;
            match command {
                CacheCommands::Warm(args) => {
                    let result = repository.warm_cache(&args.into())?;
                    if output.is_text() {
                        print_cache_warm(result);
                    } else {
                        print_serialized(&result, output)?;
                    }
                }
            }
        }
        Commands::Hash(args) => {
            print_hash(&args.file, args.msix)?;
        }
        Commands::Export(args) => {
            let mut repository = Repository::open()?;
            do_export(&mut repository, &args)?;
        }
        Commands::ErrorLookup(args) => {
            print_error_lookup(&args.input)?;
        }
        Commands::Settings {
            command,
            enable,
            disable,
        } => {
            if enable.is_some() && disable.is_some() {
                bail!("--enable and --disable cannot be used together");
            }

            let repository = Repository::open()?;
            if let Some(setting) = enable {
                repository.set_admin_setting(&setting, true)?;
                println!("Enabled admin setting '{setting}'.");
            } else if let Some(setting) = disable {
                repository.set_admin_setting(&setting, false)?;
                println!("Disabled admin setting '{setting}'.");
            } else {
                match command {
                    Some(SettingsCommands::Export) | None => {
                        print_json_value(&repository.get_user_settings()?, output)?
                    }
                    Some(SettingsCommands::Set(args)) => {
                        let value = parse_boolean_setting_value(&args.value)?;
                        repository.set_admin_setting(&args.setting, value)?;
                        if output.is_text() {
                            println!(
                                "Set admin setting '{}' to {}.",
                                args.setting,
                                if value { "true" } else { "false" }
                            );
                        } else {
                            print_json_value(&repository.get_admin_settings()?, output)?;
                        }
                    }
                    Some(SettingsCommands::Reset(args)) => {
                        if args.setting.is_none() && !args.all {
                            bail!("settings reset requires --setting or --all");
                        }
                        repository.reset_admin_setting(args.setting.as_deref(), args.all)?;
                        if output.is_text() {
                            if args.all {
                                println!("Reset all admin settings.");
                            } else {
                                println!("Reset admin setting '{}'.", args.setting.as_deref().unwrap_or_default());
                            }
                        } else {
                            print_json_value(&repository.get_admin_settings()?, output)?;
                        }
                    }
                }
            }
        }
        Commands::Features => {
            print_features();
        }
        Commands::Validate(args) => {
            print_validate(&args.manifest, !warnings_enabled())?;
        }
        Commands::Download(args) => {
            // Download shows the package agreements prompt like install (WinGet DownloadCommand).
            let mut repository = open_repository_with_install_progress()?;
            let mut request = InstallRequest::new(args.query.into());
            request.manifest_path = args.manifest;
            request.ignore_security_hash = args.ignore_security_hash;
            request.accept_package_agreements = args.accept_package_agreements;
            do_download(&mut repository, &request, args.download_directory.as_deref())?;
        }
        Commands::Pin { command } => {
            let mut repository = Repository::open()?;
            match command {
                PinCommands::List(args) => {
                    let query: PackageQuery = args.query.into();
                    let pins = filter_pins(&mut repository, &query)?;
                    if !output.is_text() {
                        print_serialized(&pins, output)?;
                    } else if pins.is_empty() {
                        println!("No pins found.");
                    } else {
                        println!("{:<40} {:<20} {:<15} Pin Type", "Package Id", "Version", "Source");
                        println!("{}", "-".repeat(85));
                        for pin in &pins {
                            println!(
                                "{:<40} {:<20} {:<15} {}",
                                pin.package_id, pin.version, pin.source_id, pin.pin_type
                            );
                        }
                    }
                }
                PinCommands::Add(args) => {
                    let query: PackageQuery = args.query.clone().into();
                    ensure_pin_query_provided(&query, "pin add")?;

                    let (package_id, source_id, resolved_version) = if args.installed {
                        let target = resolve_single_installed_pin_target(&mut repository, &query)?;
                        (
                            target.id,
                            target
                                .source_name
                                .unwrap_or_else(|| query.source.clone().unwrap_or_default()),
                            Some(target.installed_version),
                        )
                    } else {
                        let target = resolve_single_available_pin_target(&mut repository, &query)?;
                        let installed_version =
                            installed_version_for_pin(&mut repository, &target.id, &target.source_name);
                        (target.id, target.source_name, installed_version)
                    };

                    let existing = repository.list_pins(Some(source_id.as_str()))?;
                    if existing
                        .iter()
                        .any(|pin| pin.package_id.eq_ignore_ascii_case(&package_id))
                        && !args.force
                    {
                        bail!("A pin for the selected package already exists. Rerun with --force to replace it.");
                    }

                    let pin_type = pin_type_for_add(args.blocking, args.version.as_deref());
                    let pin_version = pin_version_for_add(args.version.as_deref(), args.blocking, resolved_version);

                    repository.add_pin(&package_id, &pin_version, &source_id, pin_type)?;
                    println!("Pin added for {package_id}");
                }
                PinCommands::Remove(args) => {
                    let query: PackageQuery = args.query.clone().into();
                    ensure_pin_query_provided(&query, "pin remove")?;

                    let pin = if args.installed {
                        let target = resolve_single_installed_pin_target(&mut repository, &query)?;
                        // Same key as `pin add --installed`: an installed package with no
                        // correlated source is pinned under the empty (source-less) source id.
                        let source_id = target
                            .source_name
                            .as_deref()
                            .or(query.source.as_deref())
                            .unwrap_or_default();
                        repository
                            .list_pins(Some(source_id))?
                            .into_iter()
                            .find(|candidate| candidate.package_id.eq_ignore_ascii_case(&target.id))
                    } else {
                        let pins = filter_pins(&mut repository, &query)?;
                        if pins.is_empty() {
                            None
                        } else if pins.len() > 1 {
                            bail!("Multiple pins matched the query; refine the query.");
                        } else {
                            pins.into_iter().next()
                        }
                    };

                    if let Some(pin) = pin {
                        if repository.remove_pin(&pin.package_id, Some(pin.source_id.as_str()))? {
                            println!("Pin removed for {}", pin.package_id);
                        } else {
                            println!("No pin found for {}", pin.package_id);
                        }
                    } else {
                        println!("No pin found matching the query.");
                    }
                }
                PinCommands::Reset { force, source } => {
                    if !force {
                        bail!("Resetting all pins requires --force");
                    }
                    repository.reset_pins(source.as_deref())?;
                    if let Some(source) = source {
                        println!("All pins for source '{source}' have been reset.");
                    } else {
                        println!("All pins have been reset.");
                    }
                }
            }
        }
        Commands::Install(args) => {
            let mut repository = open_repository_with_install_progress()?;
            let mode = if args.interactive {
                InstallerMode::Interactive
            } else if args.silent {
                InstallerMode::Silent
            } else {
                InstallerMode::SilentWithProgress
            };
            let result = repository.install_request(&InstallRequest {
                query: args.query.into(),
                manifest_path: args.manifest,
                mode,
                log_path: args.log,
                custom: args.custom,
                override_args: args.override_args,
                install_location: args.location,
                skip_dependencies: args.skip_dependencies,
                dependencies_only: args.dependencies_only,
                accept_package_agreements: args.accept_package_agreements,
                force: args.force,
                rename: args.rename,
                uninstall_previous: args.uninstall_previous,
                ignore_security_hash: args.ignore_security_hash,
                dependency_source: args.dependency_source,
                no_upgrade: args.no_upgrade,
            })?;
            print_install_result(&result);
        }
        Commands::Uninstall(args) => {
            let mut repository = Repository::open()?;
            let mode = if args.interactive {
                InstallerMode::Interactive
            } else if args.silent {
                InstallerMode::Silent
            } else {
                InstallerMode::SilentWithProgress
            };
            let result = repository.uninstall_request(&UninstallRequest {
                query: args.query.into(),
                manifest_path: args.manifest,
                product_code: args.product_code,
                mode,
                all_versions: args.all_versions,
                force: args.force,
                purge: args.purge,
                preserve: args.preserve,
                log_path: args.log,
            })?;
            print_install_result(&result);
        }
        Commands::Repair(args) => {
            let mut repository = open_repository_with_install_progress()?;
            let mode = if args.interactive {
                InstallerMode::Interactive
            } else if args.silent {
                InstallerMode::Silent
            } else {
                InstallerMode::SilentWithProgress
            };
            let result = repository.repair(&RepairRequest {
                query: args.query.into(),
                manifest_path: args.manifest,
                product_code: args.product_code,
                mode,
                log_path: args.log,
                accept_package_agreements: args.accept_package_agreements,
                force: args.force,
                ignore_security_hash: args.ignore_security_hash,
            })?;
            print_package_action_result(&result, "repaired", "repair");
        }
        Commands::Import(args) => {
            let mut repository = open_repository_with_install_progress()?;
            do_import(
                &mut repository,
                &args.import_file,
                args.dry_run,
                args.ignore_unavailable,
                args.ignore_versions,
                args.no_upgrade,
                args.accept_package_agreements,
            )?;
        }
        Commands::Repository { command } => run_repository_command(command, output)?,
    }

    Ok(())
}

fn run_repository_command(command: RepositoryCommands, output: OutputFormat) -> Result<()> {
    match command {
        RepositoryCommands::Build(args) => {
            let result = pinget_publisher::build(&PublisherBuildOptions {
                input: args.input,
                state: args.state,
            })?;
            if output.is_text() {
                println!(
                    "Built {} package(s) into snapshot {} ({} manifest(s), {} file(s)).",
                    result.packages, result.build_id, result.manifests, result.files
                );
            } else {
                print_serialized(&result, output)?;
            }
        }
        RepositoryCommands::Diff(args) => {
            let result = pinget_publisher::diff(&PublisherBuildOptions {
                input: args.input,
                state: args.state,
            })?;
            if output.is_text() {
                println!(
                    "Added: {}; changed: {}; removed: {}; last build published: {}.",
                    result.added.len(),
                    result.changed.len(),
                    result.removed.len(),
                    result.last_build_published
                );
                for path in &result.added {
                    println!("  added   {path}");
                }
                for path in &result.changed {
                    println!("  changed {path}");
                }
                for path in &result.removed {
                    println!("  removed {path}");
                }
            } else {
                print_serialized(&result, output)?;
            }
        }
        RepositoryCommands::Status(args) => {
            let result = pinget_publisher::status(args.state)?;
            if output.is_text() {
                match &result.last_build {
                    Some(build) => println!(
                        "Latest build {}: {} package(s), {} manifest(s); published: {}.",
                        build.build_id, build.packages, build.manifests, result.last_build_published
                    ),
                    None => println!("No successful repository builds are recorded."),
                }
            } else {
                print_serialized(&result, output)?;
            }
        }
        RepositoryCommands::Verify(args) => {
            let result = if let Some(destination) = args.destination.as_deref() {
                pinget_publisher::verify_published_destination(
                    destination,
                    args.psign_tool.as_deref(),
                    &args.trusted_ca,
                )?
            } else {
                pinget_publisher::verify(
                    args.state.as_deref().unwrap_or_else(|| Path::new(".")),
                    args.build.as_deref(),
                    None,
                )?
            };
            if output.is_text() {
                println!(
                    "Verified {} ({} artifact(s)){}.",
                    result.subject,
                    result.artifact_count,
                    result
                        .build_id
                        .as_deref()
                        .map(|id| format!(" for build {id}"))
                        .unwrap_or_default()
                );
            } else {
                print_serialized(&result, output)?;
            }
        }
        RepositoryCommands::Publish(args) => {
            let signing = if args.unsigned_development {
                Signing::UnsignedDevelopment
            } else {
                Signing::Pfx {
                    path: args.pfx.ok_or_else(|| anyhow!("normal publication requires --pfx"))?,
                    password_env: args
                        .password_env
                        .ok_or_else(|| anyhow!("normal publication requires --password-env"))?,
                }
            };
            let result = pinget_publisher::publish(&PublisherPublishOptions {
                state: args.state,
                build_id: args.build,
                destination: args.destination,
                name: args.name,
                publisher: args.publisher,
                version: args.package_version,
                signing,
                psign_tool: args.psign_tool,
                trusted_ca: args.trusted_ca,
                timestamp_url: args.timestamp_url,
            })?;
            if output.is_text() {
                println!(
                    "Published snapshot {} to {}{}.",
                    result.build_id,
                    result.destination.display(),
                    if result.unsigned_development {
                        " (unsigned development package)"
                    } else {
                        ""
                    }
                );
            } else {
                print_serialized(&result, output)?;
            }
        }
        RepositoryCommands::Doctor(args) => {
            let result = pinget_publisher::doctor_with_tools(args.psign_tool.as_deref());
            if output.is_text() {
                println!(
                    "Native MSZIP: {}; psign-tool: {}; signed publishing available: {}.",
                    result.native_mszip_available,
                    result
                        .psign_tool_path
                        .as_deref()
                        .map_or_else(|| "not found".to_owned(), |path| path.display().to_string()),
                    result.signed_publish
                );
                if !result.native_mszip_available {
                    println!("  Note: MSZIP build output is currently supported only on Windows.");
                }
            } else {
                print_serialized(&result, output)?;
            }
        }
    }
    Ok(())
}

fn print_serialized<T: serde::Serialize>(value: &T, output: OutputFormat) -> Result<()> {
    match output {
        OutputFormat::Text => bail!("structured output requested without a serializer"),
        OutputFormat::Json => println!("{}", serialize_json_pretty(value)?),
        OutputFormat::Yaml => print!("{}", serde_yaml::to_string(value)?),
    }
    Ok(())
}

fn print_manifest_serialized(value: &serde_json::Value, output: OutputFormat) -> Result<()> {
    match output {
        OutputFormat::Text => bail!("structured output requested without a serializer"),
        OutputFormat::Json => println!("{}", serialize_json_pretty(value)?),
        OutputFormat::Yaml => {
            if let serde_json::Value::Array(documents) = value {
                for document in documents {
                    print!("---\n{}", serde_yaml::to_string(document)?);
                }
            } else {
                print!("{}", serde_yaml::to_string(value)?);
            }
        }
    }
    Ok(())
}

fn serialize_json_pretty<T: serde::Serialize>(value: &T) -> Result<String> {
    Ok(serde_json::to_string_pretty(value)?)
}

impl From<QueryArgs> for PackageQuery {
    fn from(value: QueryArgs) -> Self {
        Self {
            query: value.query.or(value.query_option),
            id: value.id,
            name: value.name,
            moniker: value.moniker,
            tag: None,
            command: None,
            source: value.source,
            count: None,
            exact: value.exact,
            version: value.version,
            channel: value.channel,
            locale: value.locale,
            installer_type: value.installer_type,
            installer_architecture: value.installer_architecture,
            platform: value.platform,
            os_version: value.os_version,
            install_scope: value.install_scope,
        }
    }
}

impl From<SearchArgs> for PackageQuery {
    fn from(value: SearchArgs) -> Self {
        Self {
            query: value.query.or(value.query_option),
            id: value.id,
            name: value.name,
            moniker: value.moniker,
            tag: value.tag,
            command: value.command,
            source: value.source,
            count: value.count,
            exact: value.exact,
            version: None,
            channel: None,
            locale: None,
            installer_type: None,
            installer_architecture: None,
            platform: None,
            os_version: None,
            install_scope: None,
        }
    }
}

impl From<ListArgs> for ListQuery {
    fn from(value: ListArgs) -> Self {
        Self {
            query: value.query.or(value.query_option),
            id: value.id,
            name: value.name,
            moniker: value.moniker,
            tag: value.tag,
            command: value.command,
            product_code: None,
            version: None,
            source: value.source,
            count: value.count,
            exact: value.exact,
            install_scope: value.install_scope,
            upgrade_only: value.upgrade,
            include_unknown: value.include_unknown,
            include_pinned: value.include_pinned,
        }
    }
}

impl From<UpgradeArgs> for ListQuery {
    fn from(value: UpgradeArgs) -> Self {
        Self {
            query: value.query.or(value.query_option),
            id: value.id,
            name: value.name,
            moniker: value.moniker,
            tag: None,
            command: None,
            product_code: None,
            // `--version` is the upgrade target; ListQuery.version filters on the *installed*
            // version, so it must not be set here. The target goes on the install query.
            version: None,
            source: value.source,
            count: value.count,
            exact: value.exact,
            install_scope: value.install_scope,
            upgrade_only: true,
            include_unknown: value.include_unknown,
            include_pinned: value.include_pinned,
        }
    }
}

impl From<PinQueryArgs> for PackageQuery {
    fn from(value: PinQueryArgs) -> Self {
        Self {
            query: value.query.or(value.query_option),
            id: value.id,
            name: value.name,
            moniker: value.moniker,
            tag: value.tag,
            command: value.command,
            source: value.source,
            exact: value.exact,
            count: Some(200),
            ..PackageQuery::default()
        }
    }
}

fn print_sources(sources: Vec<SourceRecord>) {
    println!("{:<12} {:<8} {:<8} Argument", "Name", "Trust", "Explicit");
    for source in sources {
        println!(
            "{:<12} {:<8} {:<8} {}",
            source.name, source.trust_level, source.explicit, source.arg
        );
    }
}

fn print_source_updates(results: Vec<SourceUpdateResult>) -> Result<()> {
    for result in &results {
        println!("{}", format_source_update(result));
    }
    source_update_outcome(&results)
}

fn format_source_update(result: &SourceUpdateResult) -> String {
    if result.success {
        format!("{} [{}]: {}", result.name, result.kind, result.detail)
    } else {
        format!("{} [{}]: failed: {}", result.name, result.kind, result.detail)
    }
}

/// Every source is attempted and reported, but the command still fails when any did.
fn source_update_outcome(results: &[SourceUpdateResult]) -> Result<()> {
    let failed: Vec<_> = results
        .iter()
        .filter(|result| !result.success)
        .map(|result| result.name.as_str())
        .collect();
    if failed.is_empty() {
        Ok(())
    } else {
        bail!("{} source(s) failed to update: {}", failed.len(), failed.join(", "))
    }
}

fn print_search(result: SearchResponse) {
    print_warnings(&result.warnings);
    if result.matches.is_empty() {
        println!("No package matched the supplied query.");
        return;
    }

    let show_match_column = result
        .matches
        .iter()
        .any(|item| item.match_criteria.as_deref().is_some());
    if show_match_column {
        println!("{:<32} {:<40} {:<18} {:<24} Source", "Name", "Id", "Version", "Match");
        for item in result.matches {
            println!(
                "{:<32} {:<40} {:<18} {:<24} {}",
                truncate(&item.name, 32),
                truncate(&item.id, 40),
                item.version.unwrap_or_default(),
                truncate(item.match_criteria.as_deref().unwrap_or_default(), 24),
                item.source_name
            );
        }
    } else {
        println!("{:<36} {:<42} {:<18} Source", "Name", "Id", "Version");
        for item in result.matches {
            println!(
                "{:<36} {:<42} {:<18} {}",
                truncate(&item.name, 36),
                truncate(&item.id, 42),
                item.version.unwrap_or_default(),
                item.source_name
            );
        }
    }

    if result.truncated {
        println!("<additional entries truncated due to result limit>");
    }
}

fn print_list_result(result: ListResponse, details: bool, upgrade_only: bool) {
    let ListResponse {
        matches,
        warnings,
        truncated,
    } = result;

    print_warnings(&warnings);
    if matches.is_empty() {
        println!("No installed package found matching input criteria.");
        return;
    }

    let match_count = matches.len();

    if details {
        let total = matches.len();
        for (index, item) in matches.iter().enumerate() {
            if total > 1 {
                println!("({}/{}) {} [{}]", index + 1, total, item.name, item.id);
            } else {
                println!("{} [{}]", item.name, item.id);
            }
            print_field("Version", &item.installed_version);
            if let Some(value) = &item.publisher {
                print_field("Publisher", value);
            }
            if item.local_id != item.id {
                print_field("Local Identifier", &item.local_id);
            }
            if !item.package_family_names.is_empty() {
                print_field("Package Family Name", &item.package_family_names.join(", "));
            }
            if !item.product_codes.is_empty() {
                print_field("Product Code", &item.product_codes.join(", "));
            }
            if !item.upgrade_codes.is_empty() {
                print_field("Upgrade Code", &item.upgrade_codes.join(", "));
            }
            if let Some(value) = &item.installer_category {
                print_field("Installer Category", value);
            }
            if let Some(value) = &item.scope {
                print_field("Installed Scope", value);
            }
            if let Some(value) = &item.install_location {
                print_field("Installed Location", value);
            }
            if let Some(value) = &item.source_name {
                print_field("Source", value);
            }
            if let Some(value) = &item.available_version {
                print_field("Available", value);
            }
        }
    } else {
        let show_available = matches
            .iter()
            .any(|item| item.available_version.as_deref().is_some_and(|value| !value.is_empty()));
        if show_available {
            let rows = matches
                .into_iter()
                .map(|item| {
                    vec![
                        item.name,
                        item.id,
                        item.installed_version,
                        item.available_version.unwrap_or_default(),
                        item.source_name.unwrap_or_default(),
                    ]
                })
                .collect::<Vec<_>>();
            print_table(&["Name", "Id", "Version", "Available", "Source"], &rows);
        } else {
            let rows = matches
                .into_iter()
                .map(|item| {
                    vec![
                        item.name,
                        item.id,
                        item.installed_version,
                        item.source_name.unwrap_or_default(),
                    ]
                })
                .collect::<Vec<_>>();
            print_table(&["Name", "Id", "Version", "Source"], &rows);
        }
    }

    if truncated {
        println!("<additional entries truncated due to result limit>");
    }
    if upgrade_only {
        println!("{} upgrades available.", match_count);
    }
}

fn print_versions(result: VersionsResult) {
    print_warnings(&result.warnings);
    println!("Found {} [{}]", result.package.name, result.package.id);
    let show_channel = result.versions.iter().any(|version| !version.channel.is_empty());
    if show_channel {
        println!("{:<20} Channel", "Version");
        for version in result.versions {
            println!("{:<20} {}", version.version, version.channel);
        }
    } else {
        println!("Version");
        println!("-------");
        for version in result.versions {
            println!("{}", version.version);
        }
    }
}

fn print_show(result: ShowResult) {
    print_warnings(&result.warnings);
    println!("Found {} [{}]", result.package.name, result.package.id);
    print_field("Version", &result.manifest.version);
    if !result.manifest.channel.is_empty() {
        print_field("Channel", &result.manifest.channel);
    }
    print_optional_field("Publisher", result.manifest.publisher.as_deref());
    print_optional_field("Publisher Url", result.manifest.publisher_url.as_deref());
    print_optional_field(
        "Publisher Support Url",
        result.manifest.publisher_support_url.as_deref(),
    );
    print_optional_field("Author", result.manifest.author.as_deref());
    print_optional_field("Moniker", result.manifest.moniker.as_deref());
    if let Some(value) = result.manifest.description.as_deref().filter(|value| !value.is_empty()) {
        print_multiline_with_colon("Description", value);
    }
    print_optional_field("Homepage", result.manifest.package_url.as_deref());
    print_optional_field("License", result.manifest.license.as_deref());
    print_optional_field("License Url", result.manifest.license_url.as_deref());
    print_optional_field("Privacy Url", result.manifest.privacy_url.as_deref());
    print_optional_field("Copyright", result.manifest.copyright.as_deref());
    print_optional_field("Copyright Url", result.manifest.copyright_url.as_deref());
    if let Some(value) = result
        .manifest
        .release_notes
        .as_deref()
        .filter(|value| !value.is_empty())
    {
        print_multiline_with_colon("Release Notes", value);
    }
    print_optional_field("Release Notes Url", result.manifest.release_notes_url.as_deref());
    if !result.manifest.package_dependencies.is_empty() {
        print_field("Dependencies", &result.manifest.package_dependencies.join(", "));
    }
    print_documentation(&result.manifest.documentation);
    if !result.manifest.tags.is_empty() {
        print_list("Tags", &result.manifest.tags);
    }
    print_agreements(&result.manifest.agreements);

    println!("Installer:");
    if let Some(installer) = result.selected_installer.as_ref() {
        if let Some(value) = &installer.installer_type {
            print_indented_field("Type", value);
        }
        if let Some(value) = &installer.architecture {
            print_indented_field("Architecture", value);
        }
        if let Some(value) = &installer.locale {
            print_indented_field("Locale", value);
        }
        if let Some(value) = &installer.scope {
            print_indented_field("Scope", value);
        }
        if !installer.platforms.is_empty() {
            print_indented_field("Platform", &installer.platforms.join(", "));
        }
        if let Some(value) = &installer.minimum_os_version {
            print_indented_field("MinimumOSVersion", value);
        }
        if let Some(value) = &installer.url {
            print_indented_field("Url", value);
        }
        if let Some(value) = &installer.sha256 {
            print_indented_field("Sha256", value);
        }
        if let Some(value) = &installer.product_code {
            print_indented_field("ProductCode", value);
        }
        if let Some(value) = &installer.msstore_product_identifier {
            print_indented_field("Store Product Id", value);
        }
        if let Some(value) = &installer.package_family_name {
            print_indented_field("PackageFamilyName", value);
        }
        if let Some(value) = &installer.upgrade_code {
            print_indented_field("UpgradeCode", value);
        }
        if let Some(value) = &installer.release_date {
            print_indented_field("ReleaseDate", value);
        }
        if !installer.commands.is_empty() {
            print_indented_field("Commands", &installer.commands.join(", "));
        }
        print_installer_dependencies(installer);
    } else if !result.manifest.installers.is_empty() {
        println!("  No applicable installer found; see logs for more details.");
    }
}

/// Prints the selected installer's dependencies: package dependencies (with WinGet's
/// `[>= MinimumVersion]` suffix), then the Windows feature, Windows library and external ones.
fn print_installer_dependencies(installer: &Installer) {
    let dependencies = &installer.dependencies;
    let packages = if dependencies.package_dependencies.is_empty() {
        installer.package_dependencies.clone()
    } else {
        dependencies
            .package_dependencies
            .iter()
            .map(|dependency| match dependency.minimum_version.as_deref() {
                Some(minimum) => format!("{} [>= {minimum}]", dependency.package_identifier),
                None => dependency.package_identifier.clone(),
            })
            .collect()
    };
    if !packages.is_empty() {
        print_indented_field("Dependencies", &packages.join(", "));
    }
    if !dependencies.windows_features.is_empty() {
        print_indented_field("Windows Features", &dependencies.windows_features.join(", "));
    }
    if !dependencies.windows_libraries.is_empty() {
        print_indented_field("Windows Libraries", &dependencies.windows_libraries.join(", "));
    }
    if !dependencies.external_dependencies.is_empty() {
        print_indented_field("External Dependencies", &dependencies.external_dependencies.join(", "));
    }
}

fn print_cache_warm(result: CacheWarmResult) {
    print_warnings(&result.warnings);
    let version = result.package.version.unwrap_or_default();
    println!(
        "Warmed cache for {} [{}] {}",
        result.package.name, result.package.id, version
    );
    for path in result.cached_files {
        println!("  {}", path.display());
    }
}

fn print_warnings(warnings: &[String]) {
    for warning in warnings {
        print_warning(format_args!("{warning}"));
    }
}

/// Writes a `warning:` line to stderr unless `--ignore-warnings` was given.
fn print_warning(message: fmt::Arguments<'_>) {
    if warnings_enabled() {
        write_stderr_line(format_args!("warning: {message}"));
    }
}

fn open_repository_with_install_progress() -> Result<Repository> {
    let mut options = RepositoryOptions::for_current_user()?.with_install_progress(print_install_progress);
    if agreements_prompt_allowed(
        DISABLE_INTERACTIVITY.load(Ordering::Relaxed),
        io::stdin().is_terminal(),
        io::stderr().is_terminal(),
    ) {
        options = options.with_package_agreements_prompt(prompt_package_agreements);
    }
    Repository::open_with_options(options)
}

fn prompt_package_agreements(manifest: &Manifest) -> Result<bool> {
    println!(
        "Agreements for {} [{}] Version {}",
        manifest.name, manifest.id, manifest.version
    );
    print_field("Version", &manifest.version);
    print_optional_field("Publisher", manifest.publisher.as_deref());
    print_optional_field("Publisher Url", manifest.publisher_url.as_deref());
    print_optional_field("Publisher Support Url", manifest.publisher_support_url.as_deref());
    print_optional_field("Author", manifest.author.as_deref());
    print_optional_field("Homepage", manifest.package_url.as_deref());
    print_optional_field("License", manifest.license.as_deref());
    print_optional_field("License Url", manifest.license_url.as_deref());
    print_optional_field("Privacy Url", manifest.privacy_url.as_deref());
    print_optional_field("Copyright", manifest.copyright.as_deref());
    print_optional_field("Copyright Url", manifest.copyright_url.as_deref());
    print_agreements(&manifest.agreements);
    println!();
    println!("Do you agree to all the application agreements terms?");
    print!("[Y] Yes  [N] No: ");
    io::stdout().flush()?;

    let mut input = String::new();
    io::stdin().read_line(&mut input)?;
    let answer = input.trim();
    Ok(answer.eq_ignore_ascii_case("y") || answer.eq_ignore_ascii_case("yes"))
}

fn print_install_progress(progress: &InstallProgress) {
    match progress {
        InstallProgress::ResolvingManifest => write_stderr_line(format_args!("Resolving package manifest...")),
        InstallProgress::ResolvedManifest { package_id, version } => {
            write_stderr_line(format_args!("Resolved {package_id} {version}."));
        }
        InstallProgress::CheckingInstalled => write_stderr_line(format_args!("Checking installed package state...")),
        InstallProgress::InstallingDependencies => write_stderr_line(format_args!("Checking package dependencies...")),
        InstallProgress::SelectingInstaller => {
            write_stderr_line(format_args!("Selecting applicable installer for this system..."));
        }
        InstallProgress::DownloadingInstaller { url, path } => {
            write_stderr_line(format_args!(
                "Downloading installer from {url} to {}...",
                path.display()
            ));
        }
        InstallProgress::VerifyingInstaller { path } => {
            write_stderr_line(format_args!("Verifying installer hash for {}...", path.display()));
        }
        InstallProgress::StartingInstaller { installer_type, path } => {
            write_stderr_line(format_args!(
                "Starting {installer_type} installer: {}...",
                path.display()
            ));
        }
        InstallProgress::WaitingForInstaller { installer_type } => {
            write_stderr_line(format_args!("Waiting for {installer_type} installer to finish..."));
        }
        InstallProgress::InstallerFinished { exit_code } => {
            write_stderr_line(format_args!("Installer exited with code {exit_code}."));
        }
    }
}

fn write_stderr_line(args: fmt::Arguments<'_>) {
    let mut stderr = io::stderr().lock();
    if writeln!(stderr, "{args}").is_err() {}
}

fn print_field(label: &str, value: &str) {
    println!("{label}: {value}");
}

fn print_optional_field(label: &str, value: Option<&str>) {
    if let Some(value) = value.filter(|value| !value.is_empty()) {
        print_field(label, value);
    }
}

fn print_indented_field(label: &str, value: &str) {
    println!("  {label}: {value}");
}

fn print_multiline_with_colon(label: &str, value: &str) {
    println!("{label}:");
    for line in value.lines() {
        println!("  {line}");
    }
}

fn print_list(label: &str, values: &[String]) {
    println!("{label}:");
    for value in values {
        println!("  {value}");
    }
}

fn print_documentation(entries: &[Documentation]) {
    if entries.is_empty() {
        return;
    }

    println!("Documentation:");
    for entry in entries {
        match entry.label.as_deref() {
            Some(label) if !label.is_empty() => println!("  {label}: {}", entry.url),
            _ => println!("  {}", entry.url),
        }
    }
}

fn print_agreements(agreements: &[PackageAgreement]) {
    if agreements.is_empty() {
        return;
    }

    println!("Agreements:");
    for agreement in agreements {
        let label = agreement.label.as_deref().filter(|value| !value.is_empty());
        let text = agreement.text.as_deref().filter(|value| !value.is_empty());
        let url = agreement.url.as_deref().filter(|value| !value.is_empty());
        match (label, text, url) {
            (Some(label), Some(text), Some(url)) => {
                println!("  {label}: {text}");
                println!("  {label} Url: {url}");
            }
            (Some(label), Some(text), None) => println!("  {label}: {text}"),
            (Some(label), None, Some(url)) => println!("  {label}: {url}"),
            (None, Some(text), Some(url)) => {
                println!("  {text}");
                println!("  {url}");
            }
            (None, Some(text), None) => println!("  {text}"),
            (None, None, Some(url)) => println!("  {url}"),
            (Some(label), None, None) => println!("  {label}"),
            (None, None, None) => {}
        }
    }
}

fn print_info() {
    println!("pinget v{VERSION}");
    println!("Pure Rust subset of the Windows Package Manager CLI");
    println!();

    #[cfg(windows)]
    {
        use std::env;
        let os_version = get_os_version();
        let arch = env::var("PROCESSOR_ARCHITECTURE").unwrap_or_else(|_| "Unknown".to_owned());
        println!("Windows: Windows.Desktop v{os_version}");
        println!("System Architecture: {arch}");
        println!();

        println!("WinGet Directories");
        println!("{}", "-".repeat(80));
        let local_app_data = env::var("LOCALAPPDATA").unwrap_or_default();
        let user_profile = env::var("USERPROFILE").unwrap_or_default();
        let source_cache = format!(
            "{}\\Packages\\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\\LocalState\\Microsoft\\Windows Package Manager",
            local_app_data
        );
        let settings_path = format!(
            "{}\\Packages\\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\\LocalState\\settings.json",
            local_app_data
        );
        let portable_links_user = format!("{}\\Microsoft\\WinGet\\Links", local_app_data);
        let downloads = format!("{}\\Downloads", user_profile);

        println!("{:<40} {}", "Source Cache", source_cache);
        println!("{:<40} {}", "User Settings", settings_path);
        println!("{:<40} {}", "Portable Links Directory (User)", portable_links_user);
        println!(
            "{:<40} C:\\Program Files\\WinGet\\Links",
            "Portable Links Directory (Machine)"
        );
        println!(
            "{:<40} {}",
            "Portable Package Root (User)",
            format_args!("{local_app_data}\\Microsoft\\WinGet\\Packages")
        );
        println!("{:<40} C:\\Program Files\\WinGet\\Packages", "Portable Package Root");
        println!(
            "{:<40} C:\\Program Files (x86)\\WinGet\\Packages",
            "Portable Package Root (x86)"
        );
        println!("{:<40} {}", "Installer Downloads", downloads);
    }
    #[cfg(not(windows))]
    {
        println!("Platform: {}", std::env::consts::OS);
        println!("Architecture: {}", std::env::consts::ARCH);
    }

    println!();
    println!("Links");
    println!("{}", "-".repeat(80));
    println!("{:<20} https://aka.ms/winget", "Homepage");
    println!("{:<20} https://aka.ms/winget-privacy", "Privacy Statement");
    println!("{:<20} https://aka.ms/winget-license", "License Agreement");
}

#[cfg(windows)]
fn get_os_version() -> String {
    use winreg::RegKey;
    use winreg::enums::HKEY_LOCAL_MACHINE;
    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    if let Ok(key) = hklm.open_subkey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion") {
        let build: String = key.get_value("CurrentBuildNumber").unwrap_or_default();
        let ubr: u32 = key.get_value("UBR").unwrap_or(0);
        let major: u32 = key.get_value("CurrentMajorVersionNumber").unwrap_or(10);
        let minor: u32 = key.get_value("CurrentMinorVersionNumber").unwrap_or(0);
        format!("{major}.{minor}.{build}.{ubr}")
    } else {
        "Unknown".to_owned()
    }
}

fn print_hash(file_path: &str, msix: bool) -> Result<()> {
    let path = Path::new(file_path);
    println!("SHA256: {}", file_hash::sha256_file_hex(path)?);
    if msix {
        println!("SignatureSha256: {}", file_hash::msix_signature_sha256_hex(path)?);
    }
    Ok(())
}

fn do_export(repository: &mut Repository, args: &ExportArgs) -> Result<()> {
    // List everything (plain `list` already correlates installed packages to their source),
    // then filter by source here: `list --source` needs a text query, and any query filters.
    let result = repository.list(&ListQuery::default())?;
    let creation_date = chrono::Utc::now().format("%Y-%m-%dT%H:%M:%S%.3f").to_string();
    let (export, package_count) = build_export_payload(
        &result.matches,
        &repository.list_sources(),
        args.source.as_deref(),
        args.include_versions,
        &creation_date,
    );

    let json = serde_json::to_string_pretty(&export)?;
    std::fs::write(&args.output, &json)
        .map_err(|e| anyhow::anyhow!("failed to write export file '{}': {}", args.output, e))?;
    println!("Exported {package_count} packages to {}", args.output);
    Ok(())
}

const PACKAGES_EXPORT_SCHEMA: &str = "https://aka.ms/winget-packages.schema.2.0.json";

/// Groups installed packages by the source they correlate to, in the WinGet packages export
/// format. Packages with no source cannot be re-imported and are skipped; `source_filter`
/// keeps one source. Returns the payload and the number of exported packages.
fn build_export_payload(
    matches: &[ListMatch],
    sources: &[SourceRecord],
    source_filter: Option<&str>,
    include_versions: bool,
    creation_date: &str,
) -> (serde_json::Value, usize) {
    let mut groups: Vec<(&str, Vec<&ListMatch>)> = Vec::new();
    for m in matches {
        let Some(source_name) = m.source_name.as_deref().filter(|name| !name.trim().is_empty()) else {
            continue;
        };
        if source_filter.is_some_and(|filter| !source_name.eq_ignore_ascii_case(filter)) {
            continue;
        }
        match groups
            .iter_mut()
            .find(|(name, _)| name.eq_ignore_ascii_case(source_name))
        {
            Some((_, rows)) => rows.push(m),
            None => groups.push((source_name, vec![m])),
        }
    }

    let mut package_count = 0usize;
    let export_sources: Vec<serde_json::Value> = groups
        .into_iter()
        .map(|(source_name, rows)| {
            let record = sources
                .iter()
                .find(|source| source.name.eq_ignore_ascii_case(source_name));
            let source_type = match record.map(|source| &source.kind) {
                Some(SourceKind::Rest) => "Microsoft.Rest",
                _ => "Microsoft.PreIndexed.Package",
            };
            let packages: Vec<serde_json::Value> = rows
                .iter()
                .map(|m| {
                    let mut obj = serde_json::json!({ "PackageIdentifier": m.id });
                    if include_versions {
                        obj["Version"] = serde_json::Value::String(m.installed_version.clone());
                    }
                    obj
                })
                .collect();
            package_count += packages.len();
            serde_json::json!({
                "SourceDetails": {
                    "Name": record.map_or(source_name, |source| source.name.as_str()),
                    "Argument": record.map_or("", |source| source.arg.as_str()),
                    "Identifier": record.map_or("", |source| source.identifier.as_str()),
                    "Type": source_type,
                },
                "Packages": packages,
            })
        })
        .collect();

    let export = serde_json::json!({
        "$schema": PACKAGES_EXPORT_SCHEMA,
        "CreationDate": creation_date,
        "Sources": export_sources,
        "WinGetVersion": VERSION,
    });
    (export, package_count)
}

fn print_error_lookup(input: &str) -> Result<()> {
    let Some(code) = error_codes::parse_error_code(input) else {
        bail!("Could not parse '{input}' as an error code");
    };

    if let Some((symbol, description)) = error_codes::lookup_hresult(code) {
        // Upstream only shows the symbol for APPINSTALLER codes (0x8A15xxxx)
        if code & 0xFFFF0000 == 0x8A150000 {
            println!("0x{code:08x} : {symbol}");
        } else {
            println!("0x{code:08x}");
        }
        println!("{description}");
    } else {
        println!("0x{code:08x}");
        println!("Unknown error code");
    }
    Ok(())
}

fn print_features() {
    println!("The following experimental features are in progress.");
    println!("They can be configured through the settings file 'pinget settings'.");
    println!();

    let features = [
        (
            "Configuration (configure)",
            "configuration",
            "https://aka.ms/winget-settings",
        ),
        ("Direct MSI", "directMSI", "https://aka.ms/winget-settings"),
        (
            "Windows Feature Dependencies",
            "windowsFeature",
            "https://aka.ms/winget-settings",
        ),
        ("Resume", "resume", "https://aka.ms/winget-settings"),
        ("Repair", "repair", "https://aka.ms/winget-settings"),
        (
            "Side-by-side installation",
            "sideBySide",
            "https://aka.ms/winget-settings",
        ),
        ("Pinning", "pinning", "https://aka.ms/winget-settings"),
    ];

    let experimental_features = Repository::open()
        .and_then(|repository| repository.get_user_settings())
        .ok()
        .and_then(|settings| settings.get("experimentalFeatures").cloned())
        .unwrap_or(serde_json::Value::Null);

    println!("{:<40} {:<10} {:<30} Link", "Feature", "Status", "Property");
    println!("{}", "-".repeat(100));
    for (display_name, property, link) in features {
        let enabled = experimental_features
            .get(property)
            .and_then(|v| v.as_bool())
            .unwrap_or(false);
        let status = if enabled { "Enabled" } else { "Disabled" };
        println!("{display_name:<40} {status:<10} {property:<30} {link}");
    }
}

fn print_source_export(repository: &Repository) -> Result<()> {
    let sources = repository.list_sources();
    let source_array: Vec<serde_json::Value> = sources
        .iter()
        .map(|s| {
            serde_json::json!({
                "Name": s.name,
                "Type": format_source_kind(s.kind),
                "Arg": s.arg,
                "Data": s.identifier,
                "Identifier": s.identifier,
                "TrustLevel": s.trust_level,
                "Explicit": s.explicit,
                "Priority": s.priority
            })
        })
        .collect();
    let export = serde_json::json!({
        "Sources": source_array,
    });
    print_serialized(&export, OutputFormat::Json)
}

fn print_table(headers: &[&str], rows: &[Vec<String>]) {
    for line in render_table(headers, rows, get_console_width()) {
        println!("{line}");
    }
}

/// Lays out a table like WinGet's `TableOutput`: columns only shrink when stdout is a console
/// (`console_width` is `Some`); redirected or piped output keeps every value intact.
fn render_table(headers: &[&str], rows: &[Vec<String>], console_width: Option<usize>) -> Vec<String> {
    if headers.is_empty() {
        return Vec::new();
    }

    let mut widths = headers.iter().map(|header| display_width(header)).collect::<Vec<_>>();
    let mut has_data = vec![false; headers.len()];

    for row in rows {
        for (index, value) in row.iter().enumerate() {
            if !value.is_empty() {
                has_data[index] = true;
                widths[index] = widths[index].max(display_width(value));
            }
        }
    }

    for (index, width) in widths.iter_mut().enumerate() {
        if !has_data[index] {
            *width = 0;
        }
    }

    let mut space_after = vec![true; headers.len()];
    if let Some(last) = space_after.last_mut() {
        *last = false;
    }
    for index in (1..headers.len()).rev() {
        if widths[index] == 0 {
            space_after[index - 1] = false;
        } else {
            break;
        }
    }

    let mut total_required = table_total_width(&widths, &space_after);
    if let Some(console_width) = console_width
        && total_required >= console_width
    {
        let mut extra = (total_required - console_width) + 1;
        while extra > 0 {
            let mut target_index = 0;
            let mut target_width = widths[0];
            for (index, width) in widths.iter().copied().enumerate().skip(1) {
                if width > target_width {
                    target_index = index;
                    target_width = width;
                }
            }

            if widths[target_index] > 1 {
                widths[target_index] -= 1;
            }
            extra -= 1;
        }

        total_required = console_width.saturating_sub(1);
    }

    let header_row = headers.iter().map(|header| header.to_string()).collect::<Vec<_>>();
    let mut lines = Vec::with_capacity(rows.len() + 2);
    lines.push(format_table_line(&header_row, &widths, &space_after));
    lines.push("-".repeat(total_required));
    for row in rows {
        lines.push(format_table_line(row, &widths, &space_after));
    }
    lines
}

fn format_table_line(values: &[String], widths: &[usize], space_after: &[bool]) -> String {
    let mut line = String::new();

    for (index, value) in values.iter().enumerate() {
        let width = widths[index];
        if width == 0 {
            continue;
        }

        let value_width = display_width(value);
        if value_width > width {
            line.push_str(&truncate(value, width));
            if space_after[index] {
                line.push(' ');
            }
        } else {
            line.push_str(value);
            if space_after[index] {
                line.push_str(&" ".repeat(width - value_width + 1));
            }
        }
    }

    line
}

fn table_total_width(widths: &[usize], space_after: &[bool]) -> usize {
    widths
        .iter()
        .zip(space_after.iter())
        .map(|(width, space)| width + usize::from(*space))
        .sum()
}

fn get_console_width() -> Option<usize> {
    get_console_width_impl()
}

fn display_width(value: &str) -> usize {
    value.chars().count()
}

/// Width of the console attached to stdout, as WinGet's `GetConsoleWidth` reads it: only the
/// STD_OUTPUT handle counts, so redirected or piped output reports no width.
#[cfg(windows)]
fn get_console_width_impl() -> Option<usize> {
    use windows_sys::Win32::System::Console::{GetStdHandle, STD_OUTPUT_HANDLE};

    // SAFETY: `GetStdHandle` is a leaf Win32 call that does not require Rust-side invariants.
    let stdout_handle = unsafe { GetStdHandle(STD_OUTPUT_HANDLE) };
    try_console_width(stdout_handle)
}

#[cfg(windows)]
fn try_console_width(handle: windows_sys::Win32::Foundation::HANDLE) -> Option<usize> {
    use std::mem::zeroed;

    use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
    use windows_sys::Win32::System::Console::{CONSOLE_SCREEN_BUFFER_INFO, GetConsoleScreenBufferInfo};

    if handle.is_null() || handle == INVALID_HANDLE_VALUE {
        return None;
    }

    // SAFETY: zero-initializing this plain old data Win32 struct is valid before the API fills it in.
    let mut info: CONSOLE_SCREEN_BUFFER_INFO = unsafe { zeroed() };
    // SAFETY: `handle` is checked above, and `info` points to initialized writable storage for the API result.
    if unsafe { GetConsoleScreenBufferInfo(handle, &mut info) } == 0 {
        return None;
    }

    usize::try_from(info.dwSize.X).ok().filter(|width| *width > 0)
}

#[cfg(not(windows))]
fn get_console_width_impl() -> Option<usize> {
    io::stdout().is_terminal().then_some(120)
}

fn truncate(value: &str, width: usize) -> String {
    if value.chars().count() <= width {
        return value.to_owned();
    }

    let mut output = value.chars().take(width.saturating_sub(1)).collect::<String>();
    output.push('.');
    output
}

fn print_validate(manifest_path: &str, ignore_warnings: bool) -> Result<()> {
    let report = validate_manifest_path(manifest_path)?;
    let all_warnings = report.warnings;
    let all_errors = report.errors;

    if !all_warnings.is_empty() && !ignore_warnings {
        println!("Manifest validation warning.");
        for w in &all_warnings {
            println!("{w}");
        }
    }

    if !all_errors.is_empty() {
        println!("Manifest validation failed.");
        for e in &all_errors {
            println!("{e}");
        }
        std::process::exit(1);
    }

    if all_warnings.is_empty() || ignore_warnings {
        println!("Manifest validation succeeded.");
    }

    Ok(())
}

#[derive(Debug, Default)]
struct ManifestValidationReport {
    errors: Vec<String>,
    warnings: Vec<String>,
}

/// Converts a manifest file to JSON for schema validation the way WinGet does
/// (`ManifestYamlNodeToJson`): every scalar keeps its source text as a string
/// (`PackageVersion: 1.10` stays "1.10", not the number 1.1) except the integer and boolean
/// fields WinGet types, and YAML nulls stay `null`. A value of the wrong shape (`Tags: 123`,
/// `Tags:`) is kept as is so the schema reports it instead of failing the parse.
fn parse_manifest_for_validation(content: &[u8]) -> Result<serde_json::Value> {
    let content = content.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(content);
    std::str::from_utf8(content)?;
    let mut documents = serde_yaml::Deserializer::from_slice(content);
    let Some(document) = documents.next() else {
        return Ok(serde_json::Value::Null);
    };
    if documents.next().is_some() {
        bail!("deserializing from YAML containing more than one document is not supported");
    }
    let shape = <serde_yaml::Value as serde::Deserialize>::deserialize(document)?;
    let raw_document = serde_yaml::Deserializer::from_slice(content)
        .next()
        .ok_or_else(|| anyhow!("the YAML document disappeared while re-reading it"))?;
    Ok(serde::de::DeserializeSeed::deserialize(
        ValidationSeed {
            shape: &shape,
            kind: ValidationScalarKind::String,
        },
        raw_document,
    )?)
}

/// The JSON type WinGet gives a manifest scalar during schema validation.
#[derive(Clone, Copy)]
enum ValidationScalarKind {
    String,
    Int,
    Bool,
}

/// WinGet's `ManifestFieldTypes`: the only manifest fields whose scalars are not strings.
fn validation_scalar_kind(key: &str) -> ValidationScalarKind {
    match key {
        "InstallerSuccessCodes" | "InstallerReturnCode" => ValidationScalarKind::Int,
        "InstallerAbortsTerminal"
        | "InstallLocationRequired"
        | "RequireExplicitUpgrade"
        | "DisplayInstallWarnings"
        | "DownloadCommandProhibited"
        | "ArchiveBinariesDependOnPath" => ValidationScalarKind::Bool,
        _ => ValidationScalarKind::String,
    }
}

/// A typed scalar that does not convert stays a string, so the schema reports the bad type.
fn validation_scalar(text: String, kind: ValidationScalarKind) -> serde_json::Value {
    match kind {
        ValidationScalarKind::String => serde_json::Value::String(text),
        ValidationScalarKind::Int => {
            let trimmed = text.trim();
            let parsed = if let Some(hex) = trimmed.strip_prefix("0x").or_else(|| trimmed.strip_prefix("0X")) {
                i64::from_str_radix(hex, 16).ok()
            } else if let Some(octal) = trimmed.strip_prefix("0o") {
                i64::from_str_radix(octal, 8).ok()
            } else {
                trimmed.parse::<i64>().ok()
            };
            parsed.map_or(serde_json::Value::String(text), serde_json::Value::from)
        }
        ValidationScalarKind::Bool => {
            // yaml-cpp's boolean spellings, each in lower, Capitalized or UPPER case.
            let spelled = |words: &[&str]| {
                words.iter().any(|word| {
                    let capitalized = format!("{}{}", word[..1].to_ascii_uppercase(), &word[1..]);
                    text == *word || text == capitalized || text == word.to_ascii_uppercase()
                })
            };
            if spelled(&["y", "yes", "true", "on"]) {
                serde_json::Value::Bool(true)
            } else if spelled(&["n", "no", "false", "off"]) {
                serde_json::Value::Bool(false)
            } else {
                serde_json::Value::String(text)
            }
        }
    }
}

/// Re-reads a YAML document guided by `shape`, the same document already parsed as a
/// `serde_yaml::Value`. serde_yaml keeps a scalar's source text only when it is deserialized as
/// a string, so the shape decides where to ask for one.
struct ValidationSeed<'a> {
    shape: &'a serde_yaml::Value,
    kind: ValidationScalarKind,
}

impl<'de> serde::de::DeserializeSeed<'de> for ValidationSeed<'_> {
    type Value = serde_json::Value;

    fn deserialize<D>(self, deserializer: D) -> std::result::Result<Self::Value, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        use serde::Deserialize;
        use serde::de::Error;

        match self.shape {
            serde_yaml::Value::Null => {
                serde::de::IgnoredAny::deserialize(deserializer)?;
                Ok(serde_json::Value::Null)
            }
            serde_yaml::Value::Sequence(items) => {
                deserializer.deserialize_seq(ValidationSequenceVisitor { items, kind: self.kind })
            }
            serde_yaml::Value::Mapping(mapping) => deserializer.deserialize_map(ValidationMappingVisitor(mapping)),
            serde_yaml::Value::Tagged(_) => {
                serde::de::IgnoredAny::deserialize(deserializer)?;
                serde_json::to_value(self.shape).map_err(D::Error::custom)
            }
            serde_yaml::Value::Bool(_) | serde_yaml::Value::Number(_) | serde_yaml::Value::String(_) => {
                String::deserialize(deserializer).map(|text| validation_scalar(text, self.kind))
            }
        }
    }
}

struct ValidationSequenceVisitor<'a> {
    items: &'a [serde_yaml::Value],
    kind: ValidationScalarKind,
}

impl<'de> serde::de::Visitor<'de> for ValidationSequenceVisitor<'_> {
    type Value = serde_json::Value;

    fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("a YAML sequence")
    }

    fn visit_seq<A>(self, mut sequence: A) -> std::result::Result<Self::Value, A::Error>
    where
        A: serde::de::SeqAccess<'de>,
    {
        let mut items = Vec::with_capacity(self.items.len());
        for shape in self.items {
            let item = sequence
                .next_element_seed(ValidationSeed { shape, kind: self.kind })?
                .ok_or_else(|| serde::de::Error::custom("YAML sequence shape mismatch"))?;
            items.push(item);
        }
        if sequence.next_element::<serde::de::IgnoredAny>()?.is_some() {
            return Err(serde::de::Error::custom("YAML sequence shape mismatch"));
        }
        Ok(serde_json::Value::Array(items))
    }
}

struct ValidationMappingVisitor<'a>(&'a serde_yaml::Mapping);

impl<'de> serde::de::Visitor<'de> for ValidationMappingVisitor<'_> {
    type Value = serde_json::Value;

    fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("a YAML mapping")
    }

    fn visit_map<A>(self, mut map: A) -> std::result::Result<Self::Value, A::Error>
    where
        A: serde::de::MapAccess<'de>,
    {
        // serde_yaml keeps mapping entries in document order, so the shapes line up with the keys.
        let mut result = serde_json::Map::new();
        for shape in self.0.values() {
            let key = map
                .next_key::<String>()?
                .ok_or_else(|| serde::de::Error::custom("YAML mapping shape mismatch"))?;
            let kind = validation_scalar_kind(&key);
            let value = map.next_value_seed(ValidationSeed { shape, kind })?;
            result.insert(key, value);
        }
        if map.next_key::<serde::de::IgnoredAny>()?.is_some() {
            return Err(serde::de::Error::custom("YAML mapping shape mismatch"));
        }
        Ok(serde_json::Value::Object(result))
    }
}

/// A required field counts only with a value: a YAML null, a blank string, or an empty
/// sequence or mapping does not.
fn manifest_field_has_value(manifest: &serde_json::Value, field: &str) -> bool {
    match manifest.get(field) {
        None | Some(serde_json::Value::Null) => false,
        Some(serde_json::Value::String(text)) => !text.trim().is_empty(),
        Some(serde_json::Value::Array(items)) => !items.is_empty(),
        Some(serde_json::Value::Object(entries)) => !entries.is_empty(),
        Some(_) => true,
    }
}

/// A scalar manifest field's text; a YAML null or blank value reads as absent.
fn manifest_field_text<'a>(manifest: &'a serde_json::Value, field: &str) -> Option<&'a str> {
    manifest
        .get(field)
        .and_then(serde_json::Value::as_str)
        .filter(|text| !text.trim().is_empty())
}

fn validate_manifest_path(manifest_path: &str) -> Result<ManifestValidationReport> {
    let path = Path::new(manifest_path);
    if !path.exists() {
        bail!("Path does not exist: {manifest_path}");
    }

    let files: Vec<PathBuf> = if path.is_dir() {
        std::fs::read_dir(path)?
            .filter_map(|e| e.ok())
            .map(|e| e.path())
            .filter(|p| {
                p.is_file()
                    && p.extension()
                        .and_then(|ext| ext.to_str())
                        .is_some_and(|ext| ext.eq_ignore_ascii_case("yaml") || ext.eq_ignore_ascii_case("yml"))
            })
            .collect()
    } else {
        vec![path.to_path_buf()]
    };

    if files.is_empty() {
        bail!("No YAML manifest files found in: {manifest_path}");
    }

    let mut all_errors: Vec<String> = Vec::new();
    let mut all_warnings: Vec<String> = Vec::new();

    // Schema base path — relative to the repo root or binary
    let schema_base = find_schema_dir();

    for file in &files {
        let content = std::fs::read(file)?;
        let yaml_value = parse_manifest_for_validation(&content)
            .map_err(|e| anyhow::anyhow!("YAML parse error in {}: {e}", file.display()))?;

        let manifest_type = manifest_field_text(&yaml_value, "ManifestType")
            .unwrap_or("singleton")
            .to_lowercase();
        let manifest_version = manifest_field_text(&yaml_value, "ManifestVersion").unwrap_or("0.1.0");

        let schema_version = map_manifest_version(manifest_version);
        let schema_file_name = format!("manifest.{manifest_type}.{manifest_version}.json");

        if let Some(ref base) = schema_base {
            let schema_path = base.join(&schema_version).join(&schema_file_name);
            if schema_path.exists() {
                let schema_content = std::fs::read_to_string(&schema_path)?;
                let schema_json: serde_json::Value = serde_json::from_str(&schema_content)?;

                let validator = match jsonschema::validator_for(&schema_json) {
                    Ok(v) => v,
                    Err(e) => {
                        all_warnings.push(format!(
                            "  {}: could not compile schema {}: {e}",
                            file.display(),
                            schema_path.display()
                        ));
                        continue;
                    }
                };

                let result = validator.validate(&yaml_value);
                if let Err(error) = result {
                    let msg = format!("  {}: {} (at {})", file.display(), error, error.instance_path);
                    all_errors.push(msg);
                }
            } else {
                all_warnings.push(format!(
                    "  {}: schema not found: {}",
                    file.display(),
                    schema_path.display()
                ));
            }
        } else {
            // No schema dir — just do basic YAML parse validation
            all_warnings.push(format!(
                "  {}: schema directory not found, performing YAML-only validation",
                file.display()
            ));
        }

        // Basic field checks
        if (manifest_type == "singleton" || manifest_type == "installer")
            && !manifest_field_has_value(&yaml_value, "Installers")
        {
            all_errors.push(format!("  {}: required field 'Installers' is missing", file.display()));
        }
        if manifest_type == "singleton" || manifest_type == "defaultlocale" {
            for field in ["PackageIdentifier", "PackageVersion"] {
                if !manifest_field_has_value(&yaml_value, field) && manifest_version != "0.1.0" {
                    all_errors.push(format!("  {}: required field '{field}' is missing", file.display()));
                }
            }
        }
    }

    Ok(ManifestValidationReport {
        errors: all_errors,
        warnings: all_warnings,
    })
}

fn find_schema_dir() -> Option<PathBuf> {
    // Try relative to the current exe first, then walk up looking for schemas/JSON/manifests
    if let Ok(exe) = std::env::current_exe() {
        let mut dir = exe.parent().map(|p| p.to_path_buf());
        for _ in 0..10 {
            if let Some(ref d) = dir {
                let candidate = d.join("schemas").join("JSON").join("manifests");
                if candidate.is_dir() {
                    return Some(candidate);
                }
                dir = d.parent().map(|p| p.to_path_buf());
            } else {
                break;
            }
        }
    }
    // Try from cwd
    let mut dir = std::env::current_dir().ok();
    for _ in 0..10 {
        if let Some(ref d) = dir {
            let candidate = d.join("schemas").join("JSON").join("manifests");
            if candidate.is_dir() {
                return Some(candidate);
            }
            dir = d.parent().map(|p| p.to_path_buf());
        } else {
            break;
        }
    }
    None
}

fn map_manifest_version(version: &str) -> String {
    // Map version strings to schema directory names
    // "1.6.0" -> "v1.6.0", "0.1.0" -> "preview", "latest" -> "latest"
    if version == "0.1.0" {
        "preview".to_owned()
    } else if version.starts_with("1.") {
        format!("v{version}")
    } else {
        "latest".to_owned()
    }
}

fn do_download(repository: &mut Repository, request: &InstallRequest, download_dir: Option<&str>) -> Result<()> {
    let dir = match download_dir {
        Some(d) => PathBuf::from(d),
        None => std::env::current_dir()?,
    };

    let output = repository.download_installer_for_request(request, &dir)?;
    println!("Downloaded {} v{}", output.manifest.name, output.manifest.version);
    println!("  Installer: {}", output.installer_path.display());
    println!("  Manifest: {}", output.manifest_path.display());
    Ok(())
}

fn print_install_result(result: &InstallResult) {
    print_warnings(&result.warnings);
    let target = if result.version.is_empty() {
        result.package_id.clone()
    } else {
        format!("{} v{}", result.package_id, result.version)
    };
    if result.no_op {
        println!("No changes were made for {target}.");
    } else if result.success {
        println!(
            "Successfully {} {}",
            if result.installer_type == "uninstall" {
                "uninstalled"
            } else {
                "installed"
            },
            target
        );
    } else {
        write_stderr_line(format_args!(
            "Failed to {} {} (exit code: {})",
            if result.installer_type == "uninstall" {
                "uninstall"
            } else {
                "install"
            },
            target,
            result.exit_code
        ));
        std::process::exit(result.exit_code);
    }
}

fn print_package_action_result(result: &InstallResult, success_verb: &str, failure_verb: &str) {
    print_warnings(&result.warnings);
    let target = if result.version.is_empty() {
        result.package_id.clone()
    } else {
        format!("{} v{}", result.package_id, result.version)
    };
    if result.no_op {
        println!("No changes were made for {target}.");
    } else if result.success {
        println!("Successfully {success_verb} {target}");
    } else {
        write_stderr_line(format_args!(
            "Failed to {failure_verb} {target} (exit code: {})",
            result.exit_code
        ));
        std::process::exit(result.exit_code);
    }
}

fn resolve_source_add_value(positional: Option<&str>, option: Option<&str>, label: &str) -> Result<String> {
    match (positional, option) {
        (Some(positional), Some(option)) if positional != option => {
            bail!("conflicting source {label} values were provided")
        }
        (_, Some(option)) => Ok(option.to_owned()),
        (Some(positional), _) => Ok(positional.to_owned()),
        (None, None) => bail!("source add requires a {label}"),
    }
}

fn parse_source_kind(value: &str) -> Result<SourceKind> {
    if value.eq_ignore_ascii_case("rest") || value.eq_ignore_ascii_case("Microsoft.Rest") {
        Ok(SourceKind::Rest)
    } else if value.eq_ignore_ascii_case("preindexed") || value.eq_ignore_ascii_case("Microsoft.PreIndexed.Package") {
        Ok(SourceKind::PreIndexed)
    } else {
        bail!("unsupported source type: {value}")
    }
}

fn format_source_kind(kind: SourceKind) -> &'static str {
    match kind {
        SourceKind::Rest => "Microsoft.Rest",
        SourceKind::PreIndexed => "Microsoft.PreIndexed.Package",
    }
}

fn ensure_pin_query_provided(query: &PackageQuery, command_name: &str) -> Result<()> {
    if query.query.is_none()
        && query.id.is_none()
        && query.name.is_none()
        && query.moniker.is_none()
        && query.tag.is_none()
        && query.command.is_none()
    {
        bail!("{command_name} requires a query or explicit filter.");
    }

    Ok(())
}

fn resolve_single_available_pin_target(repository: &mut Repository, query: &PackageQuery) -> Result<SearchMatch> {
    let result = repository.search(query)?;
    match result.matches.len() {
        0 => bail!("No package matched the query."),
        1 => Ok(result.matches.into_iter().next().expect("single match")),
        _ => bail!("Multiple packages matched the query; refine the query."),
    }
}

fn resolve_single_installed_pin_target(repository: &mut Repository, query: &PackageQuery) -> Result<ListMatch> {
    let result = repository.list(&ListQuery {
        query: query.query.clone(),
        id: query.id.clone(),
        name: query.name.clone(),
        moniker: query.moniker.clone(),
        tag: query.tag.clone(),
        command: query.command.clone(),
        product_code: None,
        version: None,
        source: query.source.clone(),
        count: Some(200),
        exact: query.exact,
        install_scope: None,
        upgrade_only: false,
        include_unknown: false,
        include_pinned: false,
    })?;

    match result.matches.len() {
        0 => bail!("No installed package matched the query."),
        1 => Ok(result.matches.into_iter().next().expect("single match")),
        _ => bail!("Multiple installed packages matched the query; refine the query."),
    }
}

/// WinGet pin kinds for `pin add`: `--blocking` blocks every upgrade, `--version`
/// gates upgrades to a version range, and a plain pin blocks implicit upgrades.
fn pin_type_for_add(blocking: bool, version: Option<&str>) -> PinType {
    if blocking {
        PinType::Blocking
    } else if version.is_some_and(|value| !value.trim().is_empty()) {
        PinType::Gating
    } else {
        PinType::Pinning
    }
}

/// The version stored with a new pin: the requested gate, `*` for blocking pins,
/// otherwise the installed version when it is known.
fn pin_version_for_add(requested: Option<&str>, blocking: bool, installed_version: Option<String>) -> String {
    if let Some(version) = requested.filter(|value| !value.trim().is_empty()) {
        version.to_owned()
    } else if blocking {
        "*".to_owned()
    } else {
        installed_version
            .filter(|value| !value.trim().is_empty() && !value.eq_ignore_ascii_case("Unknown"))
            .unwrap_or_else(|| "*".to_owned())
    }
}

/// Installed version of `package_id` (best effort) so `pin add` can record it.
fn installed_version_for_pin(repository: &mut Repository, package_id: &str, source_name: &str) -> Option<String> {
    let result = repository
        .list(&ListQuery {
            id: Some(package_id.to_owned()),
            source: Some(source_name.to_owned()).filter(|value| !value.is_empty()),
            exact: true,
            count: Some(200),
            ..ListQuery::default()
        })
        .ok()?;
    result
        .matches
        .into_iter()
        .find(|item| item.id.eq_ignore_ascii_case(package_id))
        .map(|item| item.installed_version)
}

/// Message explaining why a pin stops this upgrade, or `None` when it may proceed.
fn upgrade_pin_block_message(pin: &PinRecord, target_version: &str, include_pinned: bool) -> Option<String> {
    if !pin_blocks_upgrade(pin, target_version, include_pinned) {
        return None;
    }
    Some(match pin.pin_type {
        PinType::Blocking => format!(
            "Package is blocked by pin {}; remove the pin before upgrading.",
            pin.version
        ),
        PinType::Gating => format!(
            "Version {target_version} is outside the pinned version range {}; remove or update the pin before upgrading.",
            pin.version
        ),
        PinType::Pinning => format!(
            "Package is pinned at {}; use --include-pinned or remove the pin before upgrading.",
            pin.version
        ),
    })
}

fn filter_pins(repository: &mut Repository, query: &PackageQuery) -> Result<Vec<PinRecord>> {
    let mut pins = repository.list_pins(query.source.as_deref())?;
    if let Some(id) = query.id.as_deref() {
        pins.retain(|pin| matches_text(&pin.package_id, id, query.exact));
    }

    let needs_catalog_resolution = query.query.is_some()
        || query.name.is_some()
        || query.moniker.is_some()
        || query.tag.is_some()
        || query.command.is_some();
    if !needs_catalog_resolution {
        return Ok(pins);
    }

    let result = repository.search(query)?;
    Ok(retain_pins_for_matches(pins, &result.matches))
}

/// Keeps the pins that belong to one of `matches`: same package id and source
/// (both compared case-insensitively), or a source-less pin for the same id.
fn retain_pins_for_matches(mut pins: Vec<PinRecord>, matches: &[SearchMatch]) -> Vec<PinRecord> {
    let keys = matches
        .iter()
        .map(|item| pin_match_key(&item.id, &item.source_name))
        .collect::<std::collections::HashSet<_>>();
    let ids = matches
        .iter()
        .map(|item| item.id.to_lowercase())
        .collect::<std::collections::HashSet<_>>();

    pins.retain(|pin| {
        keys.contains(&pin_match_key(&pin.package_id, &pin.source_id))
            || (pin.source_id.is_empty() && ids.contains(&pin.package_id.to_lowercase()))
    });
    pins
}

fn pin_match_key(package_id: &str, source: &str) -> String {
    format!("{}|{}", package_id.to_lowercase(), source.to_lowercase())
}

fn find_matching_pin<'a>(item: &ListMatch, pins: &'a [PinRecord]) -> Option<&'a PinRecord> {
    let mut source_specific = None;
    let mut source_agnostic = None;

    for pin in pins {
        if !pin.package_id.eq_ignore_ascii_case(&item.id) && !pin.package_id.eq_ignore_ascii_case(&item.local_id) {
            continue;
        }

        if !pin.source_id.is_empty() {
            if item
                .source_name
                .as_deref()
                .is_some_and(|source| pin.source_id.eq_ignore_ascii_case(source))
            {
                source_specific = Some(pin);
                break;
            }
        } else if source_agnostic.is_none() {
            source_agnostic = Some(pin);
        }
    }

    source_specific.or(source_agnostic)
}

/// Picks the rows `upgrade` will install. `--all` takes every upgradeable row. A named upgrade
/// (query, `--id`, `--name`, `--moniker`) targets one package, as in WinGet: the list filter is a
/// substring match, so an exact match wins, and any remaining ambiguity is an error instead of
/// upgrading every package that happens to contain the text. `--id` is compared only to the id,
/// `--name` only to the name, and the query to either. `ListMatch` carries no moniker, so a
/// moniker selector narrows only through the list query itself.
fn select_upgrade_targets<'a>(matches: &'a [ListMatch], args: &UpgradeArgs) -> Result<Vec<&'a ListMatch>> {
    let upgradeable: Vec<&ListMatch> = matches.iter().filter(|m| m.available_version.is_some()).collect();
    if args.all || !has_explicit_upgrade_selector(args) {
        return Ok(upgradeable);
    }

    let distinct_ids = |rows: &[&ListMatch]| {
        let mut ids: Vec<&str> = rows.iter().map(|m| m.id.as_str()).collect();
        ids.sort_by_key(|id| id.to_ascii_lowercase());
        ids.dedup_by(|a, b| a.eq_ignore_ascii_case(b));
        ids.len()
    };
    if distinct_ids(&upgradeable) <= 1 {
        return Ok(upgradeable);
    }

    let query = args.query.as_deref().or(args.query_option.as_deref());
    let is_exact = |m: &ListMatch| {
        args.id.as_deref().is_some_and(|id| m.id.eq_ignore_ascii_case(id))
            || args
                .name
                .as_deref()
                .is_some_and(|name| m.name.eq_ignore_ascii_case(name))
            || query.is_some_and(|value| m.id.eq_ignore_ascii_case(value) || m.name.eq_ignore_ascii_case(value))
    };
    let exact: Vec<&ListMatch> = upgradeable.iter().copied().filter(|m| is_exact(m)).collect();
    if !exact.is_empty() && distinct_ids(&exact) == 1 {
        return Ok(exact);
    }

    let candidates = upgradeable
        .iter()
        .map(|m| format!("  {} ({})", m.name, m.id))
        .collect::<Vec<_>>()
        .join("\n");
    bail!("Multiple installed packages found matching input criteria. Please refine the input.\n{candidates}")
}

fn has_explicit_upgrade_selector(args: &UpgradeArgs) -> bool {
    args.query.is_some()
        || args.query_option.is_some()
        || args.id.is_some()
        || args.name.is_some()
        || args.moniker.is_some()
}

fn matches_text(value: &str, query: &str, exact: bool) -> bool {
    if exact {
        value.eq_ignore_ascii_case(query)
    } else {
        value.to_ascii_lowercase().contains(&query.to_ascii_lowercase())
    }
}

fn parse_boolean_setting_value(value: &str) -> Result<bool> {
    match value.trim().to_ascii_lowercase().as_str() {
        "true" | "1" | "on" | "yes" | "enabled" => Ok(true),
        "false" | "0" | "off" | "no" | "disabled" => Ok(false),
        other => bail!("unsupported admin setting value: {other}"),
    }
}

fn print_json_value(value: &serde_json::Value, output: OutputFormat) -> Result<()> {
    match output {
        OutputFormat::Yaml => print!("{}", serde_yaml::to_string(value)?),
        OutputFormat::Json | OutputFormat::Text => {
            println!("{}", serde_json::to_string_pretty(value)?)
        }
    }
    Ok(())
}

trait ImportRepository {
    fn search(&mut self, query: &PackageQuery) -> Result<SearchResponse>;
    fn install_request(&mut self, request: &InstallRequest) -> Result<InstallResult>;
}

impl ImportRepository for Repository {
    fn search(&mut self, query: &PackageQuery) -> Result<SearchResponse> {
        Repository::search(self, query)
    }

    fn install_request(&mut self, request: &InstallRequest) -> Result<InstallResult> {
        Repository::install_request(self, request)
    }
}

fn do_import<R: ImportRepository>(
    repository: &mut R,
    file_path: &str,
    dry_run: bool,
    ignore_unavailable: bool,
    ignore_versions: bool,
    no_upgrade: bool,
    accept_package_agreements: bool,
) -> Result<()> {
    let content = decode_import_file(&std::fs::read(file_path)?)?;
    do_import_from_content(
        repository,
        &content,
        dry_run,
        ignore_unavailable,
        ignore_versions,
        no_upgrade,
        accept_package_agreements,
    )
}

/// Decodes an import file the way WinGet and .NET `File.ReadAllText` do: a UTF-8 BOM is skipped,
/// and a UTF-16 LE/BE BOM selects UTF-16 (what Windows PowerShell 5.1 `Out-File` writes).
fn decode_import_file(bytes: &[u8]) -> Result<String> {
    fn decode_utf16(payload: &[u8], unit: fn([u8; 2]) -> u16) -> Result<String> {
        let (pairs, remainder) = payload.as_chunks::<2>();
        if !remainder.is_empty() {
            bail!("Invalid import file: truncated UTF-16 content");
        }
        let units = pairs.iter().map(|pair| unit(*pair)).collect::<Vec<_>>();
        String::from_utf16(&units).map_err(|_| anyhow!("Invalid import file: malformed UTF-16 content"))
    }

    if let Some(rest) = bytes.strip_prefix(b"\xEF\xBB\xBF") {
        return Ok(std::str::from_utf8(rest)?.to_owned());
    }
    if let Some(rest) = bytes.strip_prefix(b"\xFF\xFE") {
        return decode_utf16(rest, u16::from_le_bytes);
    }
    if let Some(rest) = bytes.strip_prefix(b"\xFE\xFF") {
        return decode_utf16(rest, u16::from_be_bytes);
    }
    Ok(std::str::from_utf8(bytes)?.to_owned())
}

fn do_import_from_content<R: ImportRepository>(
    repository: &mut R,
    content: &str,
    dry_run: bool,
    ignore_unavailable: bool,
    ignore_versions: bool,
    no_upgrade: bool,
    accept_package_agreements: bool,
) -> Result<()> {
    let doc: serde_json::Value = serde_json::from_str(content)?;

    let sources = doc
        .get("Sources")
        .and_then(|s| s.as_array())
        .ok_or_else(|| anyhow::anyhow!("Invalid import file: missing 'Sources' array"))?;

    let mut total = 0;
    let mut found = 0;
    let mut not_found = 0;
    let mut skipped = 0;
    let mut failures = ImportFailureAccumulator::default();

    for source in sources {
        let source_name = source
            .get("SourceDetails")
            .and_then(|s| s.get("Name"))
            .and_then(|n| n.as_str())
            .unwrap_or("unknown");
        let packages = source
            .get("Packages")
            .and_then(|p| p.as_array())
            .unwrap_or(&Vec::new())
            .clone();

        for package in &packages {
            let id = package.get("PackageIdentifier").and_then(|i| i.as_str()).unwrap_or("?");
            let version = if ignore_versions {
                None
            } else {
                package
                    .get("Version")
                    .or_else(|| package.get("PackageVersion"))
                    .and_then(|value| value.as_str())
                    .map(|value| value.to_owned())
            };
            total += 1;

            let query = PackageQuery {
                id: Some(id.to_owned()),
                version,
                source: Some(source_name.to_owned()),
                exact: true,
                ..Default::default()
            };
            let request = create_import_install_request(query, package, accept_package_agreements, no_upgrade);

            match repository.search(&request.query) {
                Ok(result) if !result.matches.is_empty() => {
                    let m = &result.matches[0];
                    if dry_run {
                        println!(
                            "  [found] {} v{} ({})",
                            m.id,
                            m.version.as_deref().unwrap_or("?"),
                            source_name
                        );
                    } else {
                        println!(
                            "  Installing {} v{} from {}...",
                            m.id,
                            m.version.as_deref().unwrap_or("?"),
                            source_name
                        );
                        match repository.install_request(&request) {
                            Ok(r) if r.no_op => {
                                println!("    NO-OP");
                                print_warnings(&r.warnings);
                                skipped += 1;
                            }
                            Ok(r) if r.success => {
                                println!("    OK");
                                failures.record_install_result(true);
                            }
                            Ok(r) => {
                                println!("    FAILED (exit {})", r.exit_code);
                                failures.record_install_result(false);
                            }
                            Err(e) if ignore_unavailable && can_ignore_unavailable_import_failure(&e) => {
                                println!("    UNAVAILABLE");
                                print_warning(format_args!("Skipping unavailable package '{id}': {e}"));
                                skipped += 1;
                            }
                            Err(e) => {
                                println!("    ERROR: {e}");
                                failures.record_error(false);
                            }
                        }
                    }
                    found += 1;
                }
                Ok(_) => {
                    if ignore_unavailable {
                        println!("  [ignored unavailable] {id} ({source_name})");
                        skipped += 1;
                    } else {
                        println!("  [not found] {id} ({source_name})");
                        not_found += 1;
                        if !dry_run {
                            failures.record_error(false);
                        }
                    }
                }
                Err(e) if ignore_unavailable && can_ignore_unavailable_import_failure(&e) => {
                    failures.record_error(true);
                    println!("  [ignored unavailable] {id} ({source_name})");
                    print_warning(format_args!("Skipping unavailable package '{id}': {e}"));
                    skipped += 1;
                }
                Err(e) => {
                    write_stderr_line(format_args!("  ERROR searching for {id}: {e}"));
                    if !dry_run {
                        failures.record_error(false);
                    }
                }
            }
        }
    }

    println!();
    if dry_run {
        println!("Dry run: {total} packages, {found} found, {not_found} not found");
    } else {
        println!("Import: {total} packages, {found} attempted, {not_found} not found");
        if skipped > 0 {
            println!("Skipped {skipped} package(s).");
        }
    }
    if failures.has_failures() {
        bail!("one or more packages failed during import");
    }
    Ok(())
}

fn create_import_install_request(
    query: PackageQuery,
    package: &serde_json::Value,
    accept_package_agreements: bool,
    no_upgrade: bool,
) -> InstallRequest {
    let mut request = InstallRequest::new(query);
    request.query.channel = package
        .get("Channel")
        .and_then(serde_json::Value::as_str)
        .map(str::to_owned);
    request.query.install_scope = package
        .get("Scope")
        .and_then(serde_json::Value::as_str)
        .map(str::to_owned);
    request.custom = package
        .get("InitialCustomSwitches")
        .and_then(serde_json::Value::as_str)
        .map(str::to_owned);
    request.override_args = package
        .get("InitialOverrideArguments")
        .and_then(serde_json::Value::as_str)
        .map(str::to_owned);
    request.accept_package_agreements = accept_package_agreements;
    request.no_upgrade = no_upgrade;
    request
}

#[derive(Default)]
struct ImportFailureAccumulator {
    has_failures: bool,
}

impl ImportFailureAccumulator {
    fn record_install_result(&mut self, success: bool) {
        self.has_failures |= !success;
    }

    fn record_error(&mut self, ignored_unavailable: bool) {
        self.has_failures |= !ignored_unavailable;
    }

    fn has_failures(&self) -> bool {
        self.has_failures
    }
}

fn can_ignore_unavailable_import_failure(error: &anyhow::Error) -> bool {
    let message = error.to_string().to_ascii_lowercase();
    message.contains("no package matched") || message.contains("no applicable installer found")
}

#[cfg(test)]
mod review_a_cli_tests;
#[cfg(test)]
mod wave10_cli_tests;
#[cfg(test)]
mod wave11_cli_tests;
#[cfg(test)]
mod wave2_cli_tests;
#[cfg(test)]
mod wave3_cli_tests;
#[cfg(test)]
mod wave5_cli_tests;
#[cfg(test)]
mod wave8_cli_tests;

#[cfg(test)]
mod tests {
    use pinget_core::VersionKey;

    use super::*;

    #[test]
    fn export_output_file_is_not_treated_as_output_format() {
        for flag in ["-o", "--output"] {
            let cli = Cli::try_parse_from(["pinget", "export", flag, r"C:\tmp\packages.json"]).expect("parse export");
            assert_eq!(resolve_output_format(&cli).expect("format"), OutputFormat::Text);
            match cli.command {
                Some(Commands::Export(args)) => assert_eq!(args.output, r"C:\tmp\packages.json"),
                _ => panic!("expected export command"),
            }
        }
    }

    fn upgradeable_row(id: &str, name: &str) -> ListMatch {
        ListMatch {
            name: name.to_owned(),
            id: id.to_owned(),
            local_id: format!(r"ARP\Machine\X64\{name}"),
            installed_version: "1.0".to_owned(),
            available_version: Some("2.0".to_owned()),
            source_name: Some("winget".to_owned()),
            publisher: None,
            scope: None,
            installer_category: None,
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
        }
    }

    fn upgrade_args(command_line: &[&str]) -> UpgradeArgs {
        let mut full = vec!["pinget", "upgrade"];
        full.extend_from_slice(command_line);
        match Cli::try_parse_from(full).expect("parse upgrade").command {
            Some(Commands::Upgrade(parsed)) => parsed,
            _ => panic!("expected upgrade command"),
        }
    }

    #[test]
    fn named_upgrade_targets_one_package() {
        let rows = vec![
            upgradeable_row("Microsoft.Edge", "Microsoft Edge"),
            upgradeable_row("Microsoft.EdgeWebView2Runtime", "Microsoft Edge WebView2 Runtime"),
            upgradeable_row("Git.Git", "Git"),
        ];

        let exact = select_upgrade_targets(&rows, &upgrade_args(&["--id", "Microsoft.Edge"])).expect("exact id");
        assert_eq!(
            exact.iter().map(|m| m.id.as_str()).collect::<Vec<_>>(),
            ["Microsoft.Edge"]
        );

        let error = select_upgrade_targets(&rows[..2], &upgrade_args(&["edge"])).expect_err("ambiguous query");
        assert!(error.to_string().contains("Multiple installed packages"), "{error}");

        let single = select_upgrade_targets(&rows[2..], &upgrade_args(&["git"])).expect("single substring match");
        assert_eq!(single.len(), 1);

        let all = select_upgrade_targets(&rows, &upgrade_args(&["--all"])).expect("upgrade --all");
        assert_eq!(all.len(), 3);
    }

    fn installed_row(id: &str, source: Option<&str>) -> ListMatch {
        ListMatch {
            available_version: None,
            source_name: source.map(str::to_owned),
            ..upgradeable_row(id, id)
        }
    }

    fn source_record(name: &str, kind: SourceKind, arg: &str, identifier: &str) -> SourceRecord {
        SourceRecord {
            name: name.to_owned(),
            kind,
            arg: arg.to_owned(),
            identifier: identifier.to_owned(),
            trust_level: "Trusted".to_owned(),
            explicit: false,
            priority: 0,
            last_update: None,
            source_version: None,
            etag: None,
            last_modified: None,
        }
    }

    fn export_sources() -> Vec<SourceRecord> {
        vec![
            source_record(
                "winget",
                SourceKind::PreIndexed,
                "https://cdn.winget.microsoft.com/cache",
                "Microsoft.Winget.Source_8wekyb3d8bbwe",
            ),
            source_record("contoso", SourceKind::Rest, "https://contoso.test/api", "contoso-id"),
        ]
    }

    #[test]
    fn export_groups_by_source_and_skips_unsourced_packages() {
        let rows = vec![
            installed_row("Git.Git", Some("winget")),
            installed_row("Contoso.Tool", Some("contoso")),
            installed_row("Local.Only", None),
            installed_row("Microsoft.Edge", Some("WinGet")),
        ];

        let (payload, count) = build_export_payload(&rows, &export_sources(), None, true, "2026-11-06T00:00:00.000");

        assert_eq!(count, 3);
        assert_eq!(payload["$schema"], PACKAGES_EXPORT_SCHEMA);
        assert_eq!(payload["CreationDate"], "2026-11-06T00:00:00.000");
        let sources = payload["Sources"].as_array().expect("sources");
        assert_eq!(sources.len(), 2);

        let winget = &sources[0];
        assert_eq!(winget["SourceDetails"]["Name"], "winget");
        assert_eq!(winget["SourceDetails"]["Type"], "Microsoft.PreIndexed.Package");
        assert_eq!(
            winget["SourceDetails"]["Argument"],
            "https://cdn.winget.microsoft.com/cache"
        );
        assert_eq!(
            winget["SourceDetails"]["Identifier"],
            "Microsoft.Winget.Source_8wekyb3d8bbwe"
        );
        let winget_ids: Vec<&str> = winget["Packages"]
            .as_array()
            .expect("packages")
            .iter()
            .map(|p| p["PackageIdentifier"].as_str().expect("id"))
            .collect();
        assert_eq!(winget_ids, ["Git.Git", "Microsoft.Edge"]);

        let contoso = &sources[1];
        assert_eq!(contoso["SourceDetails"]["Name"], "contoso");
        assert_eq!(contoso["SourceDetails"]["Type"], "Microsoft.Rest");
        assert_eq!(contoso["SourceDetails"]["Argument"], "https://contoso.test/api");
        assert_eq!(contoso["SourceDetails"]["Identifier"], "contoso-id");
        assert_eq!(contoso["Packages"][0]["Version"], "1.0");
        assert!(!payload.to_string().contains("Local.Only"));
    }

    #[test]
    fn export_source_filter_keeps_one_source_without_versions() {
        let rows = vec![
            installed_row("Git.Git", Some("winget")),
            installed_row("Contoso.Tool", Some("contoso")),
        ];

        let (payload, count) = build_export_payload(
            &rows,
            &export_sources(),
            Some("CONTOSO"),
            false,
            "2026-11-06T00:00:00.000",
        );

        assert_eq!(count, 1);
        let sources = payload["Sources"].as_array().expect("sources");
        assert_eq!(sources.len(), 1);
        assert_eq!(sources[0]["SourceDetails"]["Name"], "contoso");
        let package = &sources[0]["Packages"][0];
        assert_eq!(package["PackageIdentifier"], "Contoso.Tool");
        assert!(package.get("Version").is_none());
    }

    #[test]
    fn exported_payload_round_trips_through_import() {
        let rows = vec![
            installed_row("Git.Git", Some("winget")),
            installed_row("Contoso.Tool", Some("contoso")),
        ];
        let (payload, _) = build_export_payload(&rows, &export_sources(), None, false, "2026-11-06T00:00:00.000");
        let mut repository = FakeImportRepository::default();

        do_import_from_content(&mut repository, &payload.to_string(), true, false, false, false, false)
            .expect("import exported payload");

        assert_eq!(repository.searched, ["Git.Git", "Contoso.Tool"]);
        assert_eq!(
            repository.searched_sources,
            [Some("winget".to_owned()), Some("contoso".to_owned())]
        );
    }

    #[test]
    fn upgrade_selectors_match_only_their_own_field() {
        let rows = vec![
            upgradeable_row("Git.Git", "Git"),
            upgradeable_row("GitHub.GitLFS", "Git LFS"),
        ];

        // `--id Git` must not pick Git.Git just because its *name* is "Git".
        let error = select_upgrade_targets(&rows, &upgrade_args(&["--id", "Git"])).expect_err("--id Git");
        assert!(error.to_string().contains("Multiple installed packages"), "{error}");

        let by_name = select_upgrade_targets(&rows, &upgrade_args(&["--name", "Git"])).expect("--name Git");
        assert_eq!(by_name.iter().map(|m| m.id.as_str()).collect::<Vec<_>>(), ["Git.Git"]);

        // `--name Git.Git` must not pick Git.Git through its id.
        let error = select_upgrade_targets(&rows, &upgrade_args(&["--name", "Git.Git"])).expect_err("--name id");
        assert!(error.to_string().contains("Multiple installed packages"), "{error}");

        // The positional query and `--query` match either field.
        for args in [&["Git"][..], &["--query", "GitHub.GitLFS"][..]] {
            let selected = select_upgrade_targets(&rows, &upgrade_args(args)).expect("query");
            assert_eq!(selected.len(), 1);
        }
    }

    #[test]
    fn upgrade_moniker_selects_single_listed_package() {
        let args = upgrade_args(&["--moniker", "git"]);
        assert!(has_explicit_upgrade_selector(&args));

        let rows = vec![upgradeable_row("Git.Git", "Git")];
        let selected = select_upgrade_targets(&rows, &args).expect("single moniker match");
        assert_eq!(selected.len(), 1);

        let rows = vec![
            upgradeable_row("Git.Git", "Git"),
            upgradeable_row("GitHub.GitLFS", "Git LFS"),
        ];
        let error = select_upgrade_targets(&rows, &args).expect_err("ambiguous moniker");
        assert!(error.to_string().contains("Multiple installed packages"), "{error}");
    }

    #[test]
    fn upgrade_version_targets_install_not_installed_filter() {
        for flag in ["--version", "-v"] {
            let args = upgrade_args(&["--id", "Git.Git", flag, "2.45.0"]);
            assert_eq!(args.version.as_deref(), Some("2.45.0"));
            let list_query = ListQuery::from(args);
            assert_eq!(list_query.version, None);
        }
    }

    #[test]
    fn global_output_format_still_applies_to_other_commands() {
        let cli = Cli::try_parse_from(["pinget", "list", "--output", "json"]).expect("parse list");
        assert_eq!(resolve_output_format(&cli).expect("format"), OutputFormat::Json);
        assert!(Cli::try_parse_from(["pinget", "list", "--output", "bogus"]).is_err());
    }

    #[derive(Default)]
    struct FakeImportRepository {
        searched: Vec<String>,
        searched_sources: Vec<Option<String>>,
        installed: Vec<String>,
        failed_package: Option<String>,
        thrown_package: Option<String>,
        unavailable_package: Option<String>,
        missing_package: Option<String>,
        search_error_package: Option<String>,
        no_op_package: Option<String>,
    }

    impl ImportRepository for FakeImportRepository {
        fn search(&mut self, query: &PackageQuery) -> Result<SearchResponse> {
            let id = query.id.clone().unwrap_or_default();
            self.searched.push(id.clone());
            self.searched_sources.push(query.source.clone());
            if self.search_error_package.as_deref() == Some(id.as_str()) {
                return Err(anyhow::anyhow!("source configuration is invalid"));
            }
            let matches = if self.missing_package.as_deref() == Some(id.as_str()) {
                Vec::new()
            } else {
                vec![SearchMatch {
                    source_name: query.source.clone().unwrap_or_default(),
                    source_kind: SourceKind::Rest,
                    id: id.clone(),
                    name: id,
                    moniker: None,
                    version: Some("1.0".to_owned()),
                    channel: None,
                    match_criteria: None,
                }]
            };
            Ok(SearchResponse {
                matches,
                warnings: Vec::new(),
                truncated: false,
            })
        }

        fn install_request(&mut self, request: &InstallRequest) -> Result<InstallResult> {
            let id = request.query.id.clone().unwrap_or_default();
            self.installed.push(id.clone());
            if self.thrown_package.as_deref() == Some(id.as_str()) {
                return Err(anyhow::anyhow!("synthetic installer error"));
            }
            if self.unavailable_package.as_deref() == Some(id.as_str()) {
                return Err(anyhow::anyhow!("No applicable installer found"));
            }
            let success = self.failed_package.as_deref() != Some(id.as_str());
            let no_op = self.no_op_package.as_deref() == Some(id.as_str());
            Ok(InstallResult {
                package_id: id,
                version: "1.0".to_owned(),
                installer_path: PathBuf::new(),
                installer_type: "exe".to_owned(),
                exit_code: if success { 0 } else { 1 },
                success,
                no_op,
                reboot_required: false,
                warnings: Vec::new(),
            })
        }
    }

    fn two_package_import_manifest() -> &'static str {
        r#"{"Sources":[{"SourceDetails":{"Name":"test"},"Packages":[{"PackageIdentifier":"Failing.Package"},{"PackageIdentifier":"Successful.Package"}]}]}"#
    }

    #[test]
    fn import_handler_attempts_success_after_returned_failure_and_returns_error() {
        let mut repository = FakeImportRepository {
            failed_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        let result = do_import_from_content(
            &mut repository,
            two_package_import_manifest(),
            false,
            false,
            false,
            false,
            false,
        );

        assert!(result.is_err());
        assert_eq!(
            repository.installed,
            vec!["Failing.Package".to_owned(), "Successful.Package".to_owned()]
        );
    }

    #[test]
    fn import_handler_attempts_success_after_thrown_error_and_returns_error() {
        let mut repository = FakeImportRepository {
            thrown_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        let result = do_import_from_content(
            &mut repository,
            two_package_import_manifest(),
            false,
            false,
            false,
            false,
            false,
        );

        assert!(result.is_err());
        assert_eq!(
            repository.installed,
            vec!["Failing.Package".to_owned(), "Successful.Package".to_owned()]
        );
    }

    #[test]
    fn ignore_unavailable_omits_classified_failure_from_exit_status() {
        let mut repository = FakeImportRepository {
            unavailable_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        let result = do_import_from_content(
            &mut repository,
            two_package_import_manifest(),
            false,
            true,
            false,
            false,
            false,
        );

        assert!(result.is_ok());
        assert_eq!(
            repository.installed,
            vec!["Failing.Package".to_owned(), "Successful.Package".to_owned()]
        );
    }

    #[test]
    fn ignore_unavailable_does_not_hide_unclassified_installer_error() {
        let mut repository = FakeImportRepository {
            thrown_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        let result = do_import_from_content(
            &mut repository,
            two_package_import_manifest(),
            false,
            true,
            false,
            false,
            false,
        );

        assert!(result.is_err());
        assert_eq!(
            repository.installed,
            vec!["Failing.Package".to_owned(), "Successful.Package".to_owned()]
        );
    }

    #[test]
    fn ignore_unavailable_skips_missing_packages_but_non_ignored_missing_package_fails() {
        let manifest =
            r#"{"Sources":[{"SourceDetails":{"Name":"test"},"Packages":[{"PackageIdentifier":"Missing.Package"}]}]}"#;
        let mut ignored = FakeImportRepository {
            missing_package: Some("Missing.Package".to_owned()),
            ..Default::default()
        };
        assert!(do_import_from_content(&mut ignored, manifest, false, true, false, false, false).is_ok());
        assert!(ignored.installed.is_empty());

        let mut required = FakeImportRepository {
            missing_package: Some("Missing.Package".to_owned()),
            ..Default::default()
        };
        assert!(do_import_from_content(&mut required, manifest, false, false, false, false, false).is_err());
    }

    #[test]
    fn dry_run_searches_all_packages_without_installing_or_failing() {
        let mut repository = FakeImportRepository {
            failed_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        let result = do_import_from_content(
            &mut repository,
            two_package_import_manifest(),
            true,
            false,
            false,
            false,
            false,
        );

        assert!(result.is_ok());
        assert_eq!(
            repository.searched,
            vec!["Failing.Package".to_owned(), "Successful.Package".to_owned()]
        );
        assert!(repository.installed.is_empty());
    }

    #[test]
    fn dry_run_search_errors_do_not_fail_or_stop_later_searches() {
        let mut repository = FakeImportRepository {
            search_error_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        assert!(
            do_import_from_content(
                &mut repository,
                two_package_import_manifest(),
                true,
                false,
                false,
                false,
                false,
            )
            .is_ok()
        );
        assert_eq!(
            repository.searched,
            vec!["Failing.Package".to_owned(), "Successful.Package".to_owned()]
        );
        assert!(repository.installed.is_empty());
    }

    #[test]
    fn dry_run_missing_packages_do_not_fail() {
        let mut repository = FakeImportRepository {
            missing_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        assert!(
            do_import_from_content(
                &mut repository,
                two_package_import_manifest(),
                true,
                false,
                false,
                false,
                false,
            )
            .is_ok()
        );
        assert_eq!(repository.searched.len(), 2);
        assert!(repository.installed.is_empty());
    }

    #[test]
    fn ignore_unavailable_does_not_hide_search_errors_or_stop_later_installs() {
        let mut repository = FakeImportRepository {
            search_error_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        assert!(
            do_import_from_content(
                &mut repository,
                two_package_import_manifest(),
                false,
                true,
                false,
                false,
                false,
            )
            .is_err()
        );
        assert_eq!(repository.installed, vec!["Successful.Package".to_owned()]);
    }

    #[test]
    fn ignore_unavailable_does_not_hide_returned_installer_failures() {
        let mut repository = FakeImportRepository {
            failed_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        assert!(
            do_import_from_content(
                &mut repository,
                two_package_import_manifest(),
                false,
                true,
                false,
                false,
                false,
            )
            .is_err()
        );
        assert_eq!(repository.installed.len(), 2);
    }

    #[test]
    fn no_op_result_does_not_fail_import() {
        let mut repository = FakeImportRepository {
            failed_package: Some("Failing.Package".to_owned()),
            no_op_package: Some("Failing.Package".to_owned()),
            ..Default::default()
        };

        assert!(
            do_import_from_content(
                &mut repository,
                two_package_import_manifest(),
                false,
                false,
                false,
                false,
                false,
            )
            .is_ok()
        );
        assert_eq!(repository.installed.len(), 2);
    }

    #[test]
    fn later_no_op_does_not_clear_earlier_failure() {
        let mut repository = FakeImportRepository {
            failed_package: Some("Failing.Package".to_owned()),
            no_op_package: Some("Successful.Package".to_owned()),
            ..Default::default()
        };

        assert!(
            do_import_from_content(
                &mut repository,
                two_package_import_manifest(),
                false,
                false,
                false,
                false,
                false,
            )
            .is_err()
        );
        assert_eq!(repository.installed.len(), 2);
    }

    #[test]
    fn later_ignored_unavailable_package_does_not_clear_earlier_failure() {
        let mut repository = FakeImportRepository {
            failed_package: Some("Failing.Package".to_owned()),
            unavailable_package: Some("Successful.Package".to_owned()),
            ..Default::default()
        };

        assert!(
            do_import_from_content(
                &mut repository,
                two_package_import_manifest(),
                false,
                true,
                false,
                false,
                false,
            )
            .is_err()
        );
        assert_eq!(repository.installed.len(), 2);
    }

    #[test]
    fn resolve_source_add_value_accepts_option_form() {
        let value = resolve_source_add_value(None, Some("winget.pro"), "name").expect("value");
        assert_eq!(value, "winget.pro");
    }

    #[test]
    fn resolve_source_add_value_rejects_conflicting_values() {
        assert!(resolve_source_add_value(Some("first"), Some("second"), "name").is_err());
    }

    #[test]
    fn parse_source_kind_accepts_winget_rest_type_name() {
        assert_eq!(parse_source_kind("Microsoft.Rest").expect("kind"), SourceKind::Rest);
        assert_eq!(
            parse_source_kind("Microsoft.PreIndexed.Package").expect("kind"),
            SourceKind::PreIndexed
        );
    }

    #[test]
    fn parse_boolean_setting_value_accepts_common_forms() {
        assert!(parse_boolean_setting_value("true").expect("bool"));
        assert!(parse_boolean_setting_value("enabled").expect("bool"));
        assert!(!parse_boolean_setting_value("false").expect("bool"));
        assert!(!parse_boolean_setting_value("0").expect("bool"));
    }

    #[test]
    fn import_install_request_preserves_custom_switches_and_override_arguments() {
        let package = serde_json::json!({
            "InitialCustomSwitches": "/custom-switch",
            "InitialOverrideArguments": "/override-arguments",
            "Channel": "preview",
            "Scope": "machine"
        });
        let request = create_import_install_request(PackageQuery::default(), &package, true, true);

        assert_eq!(request.custom.as_deref(), Some("/custom-switch"));
        assert_eq!(request.override_args.as_deref(), Some("/override-arguments"));
        assert_eq!(request.query.channel.as_deref(), Some("preview"));
        assert_eq!(request.query.install_scope.as_deref(), Some("machine"));
        assert!(request.accept_package_agreements);
        assert!(request.no_upgrade);
    }

    #[test]
    fn import_install_request_without_options_keeps_default_install_behavior() {
        let request = create_import_install_request(
            PackageQuery::default(),
            &serde_json::json!({ "PackageIdentifier": "Test.Package" }),
            false,
            false,
        );

        assert_eq!(request.mode, InstallerMode::SilentWithProgress);
        assert!(request.custom.is_none());
        assert!(request.override_args.is_none());
        assert!(!request.accept_package_agreements);
        assert!(!request.no_upgrade);
    }

    #[test]
    fn serialize_json_pretty_preserves_pascal_case_list_fields() {
        let response = ListResponse {
            matches: vec![ListMatch {
                name: "PowerToys".to_owned(),
                id: "Microsoft.PowerToys".to_owned(),
                local_id: r"ARP\Machine\X64\PowerToys".to_owned(),
                installed_version: "0.98.1".to_owned(),
                available_version: Some("0.99.0".to_owned()),
                source_name: Some("winget".to_owned()),
                publisher: Some("Microsoft".to_owned()),
                scope: Some("Machine".to_owned()),
                installer_category: Some("msi".to_owned()),
                install_location: None,
                package_family_names: Vec::new(),
                product_codes: Vec::new(),
                upgrade_codes: Vec::new(),
            }],
            warnings: Vec::new(),
            truncated: false,
        };

        let json = serialize_json_pretty(&response).expect("json");
        let value: serde_json::Value = serde_json::from_str(&json).expect("parsed json");
        let match_value = &value["Matches"][0];

        assert_eq!(match_value["Id"], "Microsoft.PowerToys");
        assert_eq!(match_value["LocalId"], r"ARP\Machine\X64\PowerToys");
        assert_eq!(match_value["InstalledVersion"], "0.98.1");
        assert_eq!(match_value["AvailableVersion"], "0.99.0");
        assert_eq!(match_value["SourceName"], "winget");
        assert!(match_value.get("local_id").is_none());
        assert!(match_value.get("installed_version").is_none());
        assert!(match_value.get("available_version").is_none());
        assert!(match_value.get("source_name").is_none());
    }

    #[test]
    fn serialize_json_pretty_preserves_pascal_case_search_fields() {
        let response = SearchResponse {
            matches: vec![SearchMatch {
                source_name: "winget".to_owned(),
                source_kind: SourceKind::PreIndexed,
                id: "Microsoft.PowerToys".to_owned(),
                name: "PowerToys".to_owned(),
                moniker: None,
                version: Some("0.99.0".to_owned()),
                channel: None,
                match_criteria: Some("Tag".to_owned()),
            }],
            warnings: Vec::new(),
            truncated: false,
        };

        let json = serialize_json_pretty(&response).expect("json");
        let value: serde_json::Value = serde_json::from_str(&json).expect("parsed json");
        let match_value = &value["Matches"][0];

        assert_eq!(match_value["SourceName"], "winget");
        assert_eq!(match_value["MatchCriteria"], "Tag");
        assert!(match_value.get("source_name").is_none());
        assert!(match_value.get("match_criteria").is_none());
    }

    #[test]
    fn serialize_json_pretty_preserves_pascal_case_versions_fields() {
        let result = VersionsResult {
            package: SearchMatch {
                source_name: "winget".to_owned(),
                source_kind: SourceKind::PreIndexed,
                id: "Microsoft.PowerToys".to_owned(),
                name: "PowerToys".to_owned(),
                moniker: None,
                version: Some("0.99.0".to_owned()),
                channel: None,
                match_criteria: None,
            },
            versions: vec![
                VersionKey {
                    version: "0.99.0".to_owned(),
                    channel: "stable".to_owned(),
                },
                VersionKey {
                    version: "0.100.0-preview".to_owned(),
                    channel: "preview".to_owned(),
                },
            ],
            warnings: Vec::new(),
        };

        let json = serialize_json_pretty(&result).expect("json");
        let value: serde_json::Value = serde_json::from_str(&json).expect("parsed json");

        assert_eq!(value["Versions"][0]["Version"], "0.99.0");
        assert_eq!(value["Versions"][0]["Channel"], "stable");
        assert_eq!(value["Versions"][1]["Version"], "0.100.0-preview");
        assert_eq!(value["Versions"][1]["Channel"], "preview");
        assert!(value.get("versions").is_none());
    }

    #[test]
    fn serialize_json_pretty_preserves_source_export_shape() {
        let export = serde_json::json!({
            "Sources": [
                {
                    "Name": "winget",
                    "Type": "Microsoft.PreIndexed.Package",
                    "Arg": "https://cdn.winget.microsoft.com/cache",
                    "Data": "Microsoft.Winget.Source_8wekyb3d8bbwe",
                    "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe",
                    "TrustLevel": "Trusted",
                    "Explicit": false,
                    "Priority": 0
                }
            ]
        });

        let json = serialize_json_pretty(&export).expect("json");
        let value: serde_json::Value = serde_json::from_str(&json).expect("parsed json");
        let source = &value["Sources"][0];

        assert_eq!(source["Name"], "winget");
        assert_eq!(source["Type"], "Microsoft.PreIndexed.Package");
        assert_eq!(source["Arg"], "https://cdn.winget.microsoft.com/cache");
        assert_eq!(source["Data"], "Microsoft.Winget.Source_8wekyb3d8bbwe");
        assert_eq!(source["Identifier"], "Microsoft.Winget.Source_8wekyb3d8bbwe");
        assert_eq!(source["TrustLevel"], "Trusted");
        assert_eq!(source["Explicit"], false);
        assert_eq!(source["Priority"], 0);
        assert!(value.get("sources").is_none());
    }

    #[test]
    fn serialize_json_pretty_preserves_show_manifest_shape() {
        let document = serde_json::json!({
            "PackageIdentifier": "Microsoft.PowerToys",
            "PackageName": "PowerToys",
            "PackageVersion": "0.99.0",
            "Publisher": "Microsoft",
            "Author": "Contoso",
            "Description": "Fancy tools",
            "PackageUrl": "https://example.test/package",
            "LicenseUrl": "https://example.test/license",
            "ReleaseNotesUrl": "https://example.test/release-notes",
            "Tags": ["utilities", "powertoys"],
            "PackageDependencies": ["Microsoft.VCRedist.2015+.x64"],
            "Installers": [
                {
                    "InstallerUrl": "https://example.test/installer.exe",
                    "InstallerSha256": "ABC123",
                    "InstallerType": "exe",
                    "ReleaseDate": "2026-05-22"
                }
            ]
        });

        let json = serialize_json_pretty(&document).expect("json");
        let value: serde_json::Value = serde_json::from_str(&json).expect("parsed json");

        assert_eq!(value["PackageIdentifier"], "Microsoft.PowerToys");
        assert_eq!(value["PackageName"], "PowerToys");
        assert_eq!(value["Publisher"], "Microsoft");
        assert_eq!(value["Author"], "Contoso");
        assert_eq!(value["Description"], "Fancy tools");
        assert_eq!(value["PackageUrl"], "https://example.test/package");
        assert_eq!(value["LicenseUrl"], "https://example.test/license");
        assert_eq!(value["ReleaseNotesUrl"], "https://example.test/release-notes");
        assert_eq!(value["Tags"][0], "utilities");
        assert_eq!(
            value["Installers"][0]["InstallerUrl"],
            "https://example.test/installer.exe"
        );
        assert_eq!(value["Installers"][0]["InstallerSha256"], "ABC123");
        assert_eq!(value["Installers"][0]["InstallerType"], "exe");
        assert_eq!(value["Installers"][0]["ReleaseDate"], "2026-05-22");
        assert!(value.get("package_identifier").is_none());
        assert!(value.get("source_name").is_none());
    }
}
