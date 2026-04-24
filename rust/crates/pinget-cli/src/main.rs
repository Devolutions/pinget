#![allow(clippy::print_stdout)]

use std::fmt;
use std::io::{self, Write};
use std::path::PathBuf;

use anyhow::{Result, anyhow, bail};
use clap::{Args, Parser, Subcommand};
use pinget_core::{
    CacheWarmResult, Documentation, InstallRequest, InstallResult, InstallerMode, ListMatch, ListQuery, ListResponse,
    PackageQuery, PinRecord, PinType, RepairRequest, Repository, SearchMatch, SearchResponse, ShowResult, SourceKind,
    SourceRecord, SourceUpdateResult, UninstallRequest, VersionsResult,
};

const VERSION: &str = env!("CARGO_PKG_VERSION");
const UPGRADE_UNSUPPORTED_WARNING: &str = "Upgrading packages is not supported on this platform; no changes were made.";

#[derive(Parser)]
#[command(name = "pinget", about = "Pinget: portable winget in pure Rust", version = VERSION)]
struct Cli {
    #[command(subcommand)]
    command: Option<Commands>,
    #[arg(long = "info", global = true)]
    info: bool,
    #[arg(long = "output", short = 'o', global = true, value_parser = ["json", "text", "yaml"])]
    output: Option<String>,
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
    #[arg(long, short = 'n')]
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
    input: String,
}

#[derive(Args)]
struct ValidateArgs {
    manifest: String,
    #[arg(long = "ignore-warnings")]
    ignore_warnings: bool,
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
    #[arg(short = 't', long = "type", default_value = "rest")]
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
    #[arg(long, short = 'n')]
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
    #[arg(long, short = 'n')]
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

fn run() -> Result<()> {
    let cli = Cli::parse();
    let output = OutputFormat::from_option(cli.output.as_deref())?;

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
            let mut repository = Repository::open()?;
            let do_install = args.all
                || args.query.is_some()
                || args.query_option.is_some()
                || args.id.is_some()
                || args.name.is_some();
            if do_install && !cfg!(windows) {
                print_warnings(&[UPGRADE_UNSUPPORTED_WARNING.to_owned()]);
                println!("No changes were made.");
                return Ok(());
            }

            let mut list_query = ListQuery::from(args.clone());
            list_query.include_pinned = list_query.include_pinned || has_explicit_upgrade_selector(&args);
            let result = repository.list(&list_query)?;
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
                let upgradeable: Vec<_> = result
                    .matches
                    .iter()
                    .filter(|m| m.available_version.is_some())
                    .collect();
                if upgradeable.is_empty() {
                    println!("No applicable upgrade found.");
                } else {
                    let pins = repository.list_pins(None)?;
                    for m in &upgradeable {
                        println!(
                            "Upgrading {} from {} to {} ...",
                            m.id,
                            m.installed_version,
                            m.available_version.as_deref().unwrap_or("?")
                        );
                        if let Some(pin) = find_matching_pin(m, &pins)
                            && pin.pin_type == PinType::Blocking
                        {
                            println!(
                                "  Package is blocked by pin {}; remove the pin before upgrading.",
                                pin.version
                            );
                            continue;
                        }
                        let query = PackageQuery {
                            id: Some(m.id.clone()),
                            source: m.source_name.clone(),
                            exact: true,
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
                            accept_package_agreements: false,
                            force: false,
                            rename: None,
                            uninstall_previous: false,
                            ignore_security_hash: args.ignore_security_hash,
                            dependency_source: args.dependency_source.clone(),
                            no_upgrade: false,
                        }) {
                            Ok(r) if r.success => {
                                println!("  Successfully upgraded {}", m.id);
                            }
                            Ok(r) => {
                                write_stderr_line(format_args!(
                                    "  Failed to upgrade {} (exit code: {})",
                                    m.id, r.exit_code
                                ));
                            }
                            Err(e) => {
                                write_stderr_line(format_args!("  Error upgrading {}: {e}", m.id));
                            }
                        }
                    }
                    println!("{} package(s) upgraded.", upgradeable.len());
                }
            }
        }
        Commands::Source { command } => {
            let mut repository = Repository::open()?;
            match command {
                SourceCommands::List => print_sources(repository.list_sources()),
                SourceCommands::Update { source } => {
                    print_source_updates(repository.update_sources(source.as_deref())?)
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
            print_error_lookup(&args.input);
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
            print_validate(&args.manifest, args.ignore_warnings)?;
        }
        Commands::Download(args) => {
            let mut repository = Repository::open()?;
            let mut request = InstallRequest::new(args.query.into());
            request.manifest_path = args.manifest;
            request.ignore_security_hash = args.ignore_security_hash;
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
                        (target.id, target.source_name, target.version)
                    };

                    let existing = repository.list_pins(Some(source_id.as_str()))?;
                    if existing
                        .iter()
                        .any(|pin| pin.package_id.eq_ignore_ascii_case(&package_id))
                        && !args.force
                    {
                        bail!("A pin for the selected package already exists. Rerun with --force to replace it.");
                    }

                    let pin_type = if args.blocking {
                        PinType::Blocking
                    } else {
                        PinType::Pinning
                    };
                    let pin_version =
                        if let Some(version) = args.version.as_deref().filter(|value| !value.trim().is_empty()) {
                            version.to_owned()
                        } else if args.blocking {
                            "*".to_owned()
                        } else {
                            resolved_version.unwrap_or_else(|| "*".to_owned())
                        };

                    repository.add_pin(&package_id, &pin_version, &source_id, pin_type)?;
                    println!("Pin added for {package_id}");
                }
                PinCommands::Remove(args) => {
                    let query: PackageQuery = args.query.clone().into();
                    ensure_pin_query_provided(&query, "pin remove")?;

                    let pin = if args.installed {
                        let target = resolve_single_installed_pin_target(&mut repository, &query)?;
                        repository
                            .list_pins(target.source_name.as_deref().or(query.source.as_deref()))?
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
            let mut repository = Repository::open()?;
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
            let mut repository = Repository::open()?;
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
            let mut repository = Repository::open()?;
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
    }

    Ok(())
}

fn print_serialized<T: serde::Serialize>(value: &T, output: OutputFormat) -> Result<()> {
    match output {
        OutputFormat::Text => bail!("structured output requested without a serializer"),
        OutputFormat::Json => println!("{}", serde_json::to_string_pretty(value)?),
        OutputFormat::Yaml => print!("{}", serde_yaml::to_string(value)?),
    }
    Ok(())
}

fn print_manifest_serialized(value: &serde_json::Value, output: OutputFormat) -> Result<()> {
    match output {
        OutputFormat::Text => bail!("structured output requested without a serializer"),
        OutputFormat::Json => println!("{}", serde_json::to_string_pretty(value)?),
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

fn print_source_updates(results: Vec<SourceUpdateResult>) {
    for result in results {
        println!("{} [{}]: {}", result.name, result.kind, result.detail);
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
    if let Some(value) = &result.manifest.publisher {
        print_field("Publisher", value);
    }
    if let Some(value) = &result.manifest.publisher_url {
        print_field("Publisher Url", value);
    }
    if let Some(value) = &result.manifest.publisher_support_url {
        print_field("Publisher Support Url", value);
    }
    if let Some(value) = &result.manifest.author {
        print_field("Author", value);
    }
    if let Some(value) = &result.manifest.moniker {
        print_field("Moniker", value);
    }
    if let Some(value) = &result.manifest.description {
        print_multiline_with_colon("Description", value);
    }
    if let Some(value) = &result.manifest.package_url {
        print_field("Homepage", value);
    }
    if let Some(value) = &result.manifest.license {
        print_field("License", value);
    }
    if let Some(value) = &result.manifest.license_url {
        print_field("License Url", value);
    }
    if let Some(value) = &result.manifest.privacy_url {
        print_field("Privacy Url", value);
    }
    if let Some(value) = &result.manifest.copyright {
        print_field("Copyright", value);
    }
    if let Some(value) = &result.manifest.copyright_url {
        print_field("Copyright Url", value);
    }
    if let Some(value) = &result.manifest.release_notes {
        print_multiline_with_colon("Release Notes", value);
    }
    if let Some(value) = &result.manifest.release_notes_url {
        print_field("Release Notes Url", value);
    }
    if !result.manifest.package_dependencies.is_empty() {
        print_field("Dependencies", &result.manifest.package_dependencies.join(", "));
    }
    print_documentation(&result.manifest.documentation);
    if !result.manifest.tags.is_empty() {
        print_list("Tags", &result.manifest.tags);
    }

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
        if !installer.package_dependencies.is_empty() {
            print_indented_field("Dependencies", &installer.package_dependencies.join(", "));
        }
    } else if !result.manifest.installers.is_empty() {
        println!("  No applicable installer found; see logs for more details.");
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
        write_stderr_line(format_args!("warning: {warning}"));
    }
}

fn write_stderr_line(args: fmt::Arguments<'_>) {
    let mut stderr = io::stderr().lock();
    if writeln!(stderr, "{args}").is_err() {}
}

fn print_field(label: &str, value: &str) {
    println!("{label}: {value}");
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

fn print_hash(file_path: &str, _msix: bool) -> Result<()> {
    use std::fs;

    use sha2::{Digest, Sha256};
    let data = fs::read(file_path).map_err(|e| anyhow::anyhow!("failed to read file '{}': {}", file_path, e))?;
    let hash = Sha256::digest(&data);
    let hex = hash.iter().map(|byte| format!("{byte:02x}")).collect::<String>();
    println!("SHA256: {hex}");
    Ok(())
}

fn do_export(repository: &mut Repository, args: &ExportArgs) -> Result<()> {
    let list_query = ListQuery {
        query: None,
        id: None,
        name: None,
        moniker: None,
        tag: None,
        command: None,
        product_code: None,
        version: None,
        source: args.source.clone(),
        count: None,
        exact: false,
        install_scope: None,
        upgrade_only: false,
        include_unknown: false,
        include_pinned: false,
    };
    let result = repository.list(&list_query)?;

    let packages: Vec<serde_json::Value> = result
        .matches
        .iter()
        .filter(|m| m.source_name.is_some())
        .map(|m| {
            let mut obj = serde_json::json!({
                "PackageIdentifier": m.id,
            });
            if args.include_versions {
                obj["Version"] = serde_json::Value::String(m.installed_version.clone());
            }
            if let Some(source) = &m.source_name {
                obj["SourceDetails"] = serde_json::json!({
                    "Name": source,
                    "Type": "Microsoft.PreIndexed.Package",
                    "Argument": ""
                });
            }
            obj
        })
        .collect();

    let export = serde_json::json!({
        "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
        "CreationDate": chrono::Utc::now().format("%Y-%m-%dT%H:%M:%S%.3f").to_string(),
        "Sources": [{
            "SourceDetails": {
                "Name": "winget",
                "Type": "Microsoft.PreIndexed.Package",
                "Argument": "https://cdn.winget.microsoft.com/cache"
            },
            "Packages": packages
        }],
        "WinGetVersion": VERSION,
    });

    let json = serde_json::to_string_pretty(&export)?;
    std::fs::write(&args.output, &json)
        .map_err(|e| anyhow::anyhow!("failed to write export file '{}': {}", args.output, e))?;
    println!("Exported {} packages to {}", packages.len(), args.output);
    Ok(())
}

fn print_error_lookup(input: &str) {
    let code = if input.starts_with("0x") || input.starts_with("0X") {
        u32::from_str_radix(&input[2..], 16).ok()
    } else {
        input
            .parse::<u32>()
            .ok()
            .or_else(|| input.parse::<i32>().ok().and_then(|value| u32::try_from(value).ok()))
    };

    let code = match code {
        Some(c) => c,
        None => {
            println!("Could not parse input '{input}' as an error code.");
            return;
        }
    };

    if let Some((symbol, description)) = lookup_hresult(code) {
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
}

fn lookup_hresult(code: u32) -> Option<(&'static str, &'static str)> {
    match code {
        0x00000000 => Some(("S_OK", "Operation successful")),
        0x80004001 => Some(("E_NOTIMPL", "Not implemented")),
        0x80004002 => Some(("E_NOINTERFACE", "No such interface supported")),
        0x80004003 => Some(("E_POINTER", "Invalid pointer")),
        0x80004004 => Some(("E_ABORT", "Operation aborted")),
        0x80004005 => Some(("E_FAIL", "Unspecified error")),
        0x80070002 => Some(("E_FILENOTFOUND", "The system cannot find the file specified")),
        0x80070005 => Some(("E_ACCESSDENIED", "General access denied error")),
        0x80070057 => Some(("E_INVALIDARG", "One or more arguments are invalid")),
        0x8007000E => Some(("E_OUTOFMEMORY", "Ran out of memory")),
        // winget-specific HRESULT codes
        0x8A150001 => Some(("APPINSTALLER_CLI_ERROR_INTERNAL_ERROR", "Internal error")),
        0x8A150002 => Some((
            "APPINSTALLER_CLI_ERROR_INVALID_CL_ARGUMENTS",
            "Invalid command line arguments",
        )),
        0x8A150003 => Some(("APPINSTALLER_CLI_ERROR_COMMAND_FAILED", "Command failed")),
        0x8A150004 => Some(("APPINSTALLER_CLI_ERROR_MANIFEST_FAILED", "Opening manifest failed")),
        0x8A150005 => Some((
            "APPINSTALLER_CLI_ERROR_BLOCKED_BY_POLICY",
            "Operation is blocked by policy",
        )),
        0x8A150006 => Some((
            "APPINSTALLER_CLI_ERROR_SHELLEXEC_INSTALL_FAILED",
            "ShellExecute install failed",
        )),
        0x8A150007 => Some((
            "APPINSTALLER_CLI_ERROR_UNSUPPORTED_MANIFESTVERSION",
            "Unsupported manifest version",
        )),
        0x8A150008 => Some(("APPINSTALLER_CLI_ERROR_DOWNLOAD_FAILED", "Download of installer failed")),
        0x8A150009 => Some((
            "APPINSTALLER_CLI_ERROR_CANNOT_WRITE_TO_UPLEVEL_INDEX",
            "Cannot write to the package index",
        )),
        0x8A15000A => Some((
            "APPINSTALLER_CLI_ERROR_INDEX_INTEGRITY_COMPROMISED",
            "Index integrity compromised",
        )),
        0x8A15000B => Some(("APPINSTALLER_CLI_ERROR_SOURCES_INVALID", "Sources are invalid")),
        0x8A15000C => Some((
            "APPINSTALLER_CLI_ERROR_SOURCE_NAME_ALREADY_EXISTS",
            "Source name already exists",
        )),
        0x8A15000D => Some(("APPINSTALLER_CLI_ERROR_INVALID_SOURCE_TYPE", "Invalid source type")),
        0x8A15000E => Some(("APPINSTALLER_CLI_ERROR_PACKAGE_IS_BUNDLE", "Package is a bundle")),
        0x8A15000F => Some(("APPINSTALLER_CLI_ERROR_SOURCE_DATA_MISSING", "Source data is missing")),
        0x8A150010 => Some((
            "APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER",
            "None of the installers are applicable for the current system",
        )),
        0x8A150011 => Some((
            "APPINSTALLER_CLI_ERROR_INSTALLER_HASH_MISMATCH",
            "Installer hash does not match",
        )),
        0x8A150012 => Some((
            "APPINSTALLER_CLI_ERROR_SOURCE_NAME_DOES_NOT_EXIST",
            "Source name does not exist",
        )),
        0x8A150013 => Some((
            "APPINSTALLER_CLI_ERROR_SOURCE_ARG_ALREADY_EXISTS",
            "Source argument already exists",
        )),
        0x8A150014 => Some(("APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND", "No applications found")),
        0x8A150015 => Some(("APPINSTALLER_CLI_ERROR_NO_SOURCES_DEFINED", "No sources defined")),
        0x8A150016 => Some((
            "APPINSTALLER_CLI_ERROR_MULTIPLE_APPLICATIONS_FOUND",
            "Multiple applications found",
        )),
        0x8A150017 => Some((
            "APPINSTALLER_CLI_ERROR_NO_MANIFEST_FOUND",
            "No manifest found matching input criteria",
        )),
        0x8A150019 => Some(("APPINSTALLER_CLI_ERROR_NO_RANGES_PROCESSED", "No ranges processed")),
        0x8A15001A => Some((
            "APPINSTALLER_CLI_ERROR_EXPERIMENTAL_FEATURE_DISABLED",
            "This feature is disabled by Group Policy",
        )),
        0x8A15001B => Some((
            "APPINSTALLER_CLI_ERROR_MSSTORE_BLOCKED_BY_POLICY",
            "This feature is blocked by Group Policy",
        )),
        0x8A15001C => Some((
            "APPINSTALLER_CLI_ERROR_MSSTORE_APP_BLOCKED_BY_POLICY",
            "This Microsoft Store app is blocked by Group Policy",
        )),
        0x8A150022 => Some((
            "APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE",
            "Upgrade version is not newer than installed version",
        )),
        0x8A150023 => Some((
            "APPINSTALLER_CLI_ERROR_UPDATE_ALL_HAS_FAILURE",
            "At least one package had a failure during upgrade --all",
        )),
        0x8A150024 => Some((
            "APPINSTALLER_CLI_ERROR_INSTALLER_SECURITY_CHECK_FAILED",
            "Installer failed security check",
        )),
        0x8A15002B => Some((
            "APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED",
            "The package is already installed",
        )),
        0x8A150038 => Some((
            "APPINSTALLER_CLI_ERROR_PINNED_CERTIFICATE_MISMATCH",
            "Certificate pinning mismatch",
        )),
        _ => None,
    }
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
    if headers.is_empty() {
        return;
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
    let console_width = get_console_width();
    if total_required >= console_width {
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
    print_table_line(&header_row, &widths, &space_after);
    println!("{}", "-".repeat(total_required));
    for row in rows {
        print_table_line(row, &widths, &space_after);
    }
}

fn print_table_line(values: &[String], widths: &[usize], space_after: &[bool]) {
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

    println!("{line}");
}

fn table_total_width(widths: &[usize], space_after: &[bool]) -> usize {
    widths
        .iter()
        .zip(space_after.iter())
        .map(|(width, space)| width + usize::from(*space))
        .sum()
}

fn get_console_width() -> usize {
    get_console_width_impl()
}

fn display_width(value: &str) -> usize {
    value.chars().count()
}

#[cfg(windows)]
fn get_console_width_impl() -> usize {
    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING};
    use windows_sys::Win32::System::Console::{GetStdHandle, STD_OUTPUT_HANDLE};

    // SAFETY: `GetStdHandle` is a leaf Win32 call that does not require Rust-side invariants.
    let stdout_handle = unsafe { GetStdHandle(STD_OUTPUT_HANDLE) };
    if let Some(width) = try_console_width(stdout_handle) {
        return width;
    }

    let mut conout = "CONOUT$\0".encode_utf16().collect::<Vec<_>>();
    // SAFETY: the encoded `CONOUT$` buffer is NUL-terminated and all pointer arguments follow the Win32 contract.
    let console_handle = unsafe {
        CreateFileW(
            conout.as_mut_ptr(),
            0,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            std::ptr::null(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        )
    };
    if console_handle.is_null() || console_handle == INVALID_HANDLE_VALUE {
        return 119;
    }

    let width = try_console_width(console_handle).unwrap_or(119);
    // SAFETY: `console_handle` was returned by `CreateFileW` above and is closed exactly once here.
    unsafe {
        CloseHandle(console_handle);
    }
    width
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

    usize::try_from(info.dwSize.X)
        .ok()
        .and_then(|width| width.checked_sub(2))
        .filter(|width| *width > 0)
}

#[cfg(not(windows))]
fn get_console_width_impl() -> usize {
    120
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
    use std::path::Path;

    let path = Path::new(manifest_path);
    if !path.exists() {
        bail!("Path does not exist: {manifest_path}");
    }

    let files: Vec<PathBuf> = if path.is_dir() {
        std::fs::read_dir(path)?
            .filter_map(|e| e.ok())
            .map(|e| e.path())
            .filter(|p| p.extension().is_some_and(|ext| ext == "yaml" || ext == "yml"))
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
        let content = std::fs::read_to_string(file)?;
        let yaml_value: serde_json::Value = serde_yaml::from_str(&content)
            .map_err(|e| anyhow::anyhow!("YAML parse error in {}: {e}", file.display()))?;

        let manifest_type = yaml_value
            .get("ManifestType")
            .and_then(|v| v.as_str())
            .unwrap_or("singleton")
            .to_lowercase();
        let manifest_version = yaml_value
            .get("ManifestVersion")
            .and_then(|v| v.as_str())
            .unwrap_or("0.1.0");

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
        if (manifest_type == "singleton" || manifest_type == "installer") && yaml_value.get("Installers").is_none() {
            all_errors.push(format!("  {}: required field 'Installers' is missing", file.display()));
        }
        if manifest_type == "singleton" || manifest_type == "defaultlocale" {
            for field in ["PackageIdentifier", "PackageVersion"] {
                if yaml_value.get(field).is_none() && manifest_version != "0.1.0" {
                    all_errors.push(format!("  {}: required field '{field}' is missing", file.display()));
                }
            }
        }
    }

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

    let (manifest, path) = repository.download_installer_for_request(request, &dir)?;
    println!("Downloaded {} v{}", manifest.name, manifest.version);
    println!("  Path: {}", path.display());
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
    let keys = result
        .matches
        .iter()
        .map(|item| format!("{}|{}", item.id, item.source_name))
        .collect::<std::collections::HashSet<_>>();
    let ids = result
        .matches
        .iter()
        .map(|item| item.id.to_ascii_lowercase())
        .collect::<std::collections::HashSet<_>>();

    pins.retain(|pin| {
        keys.contains(&format!("{}|{}", pin.package_id, pin.source_id))
            || (pin.source_id.is_empty() && ids.contains(&pin.package_id.to_ascii_lowercase()))
    });
    Ok(pins)
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

fn do_import(
    repository: &mut Repository,
    file_path: &str,
    dry_run: bool,
    ignore_unavailable: bool,
    ignore_versions: bool,
    no_upgrade: bool,
    accept_package_agreements: bool,
) -> Result<()> {
    let content = std::fs::read_to_string(file_path)?;
    let doc: serde_json::Value = serde_json::from_str(&content)?;

    let sources = doc
        .get("Sources")
        .and_then(|s| s.as_array())
        .ok_or_else(|| anyhow::anyhow!("Invalid import file: missing 'Sources' array"))?;

    let mut total = 0;
    let mut found = 0;
    let mut not_found = 0;
    let mut skipped = 0;

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

            match repository.search(&query) {
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
                        let mut request = InstallRequest::new(query.clone());
                        request.accept_package_agreements = accept_package_agreements;
                        request.no_upgrade = no_upgrade;
                        match repository.install_request(&request) {
                            Ok(r) if r.no_op => {
                                println!("    NO-OP");
                                print_warnings(&r.warnings);
                                skipped += 1;
                            }
                            Ok(r) if r.success => println!("    OK"),
                            Ok(r) => println!("    FAILED (exit {})", r.exit_code),
                            Err(e) if ignore_unavailable && can_ignore_unavailable_import_failure(&e) => {
                                println!("    UNAVAILABLE");
                                write_stderr_line(format_args!("warning: Skipping unavailable package '{id}': {e}"));
                                skipped += 1;
                            }
                            Err(e) => println!("    ERROR: {e}"),
                        }
                    }
                    found += 1;
                }
                _ => {
                    if ignore_unavailable {
                        println!("  [ignored unavailable] {id} ({source_name})");
                        skipped += 1;
                    } else {
                        println!("  [not found] {id} ({source_name})");
                        not_found += 1;
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
    Ok(())
}

fn can_ignore_unavailable_import_failure(error: &anyhow::Error) -> bool {
    let message = error.to_string().to_ascii_lowercase();
    message.contains("no package matched") || message.contains("no applicable installer found")
}

#[cfg(test)]
mod tests {
    use super::*;

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
}
