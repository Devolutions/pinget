use super::*;

// ── R8-1: download accepts --accept-package-agreements (WinGet parity) ──

#[test]
fn download_accepts_package_agreements_flag() {
    let cli = Cli::try_parse_from([
        "pinget",
        "download",
        "--id",
        "Test.Package",
        "--accept-package-agreements",
    ])
    .expect("download should accept --accept-package-agreements");
    match cli.command {
        Some(Commands::Download(args)) => assert!(args.accept_package_agreements),
        _ => panic!("expected download command"),
    }

    let cli = Cli::try_parse_from(["pinget", "download", "--id", "Test.Package"]).expect("download parses");
    match cli.command {
        Some(Commands::Download(args)) => assert!(!args.accept_package_agreements),
        _ => panic!("expected download command"),
    }
}
