# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.14.2](https://github.com/Devolutions/pinget/compare/pinget-core-v0.14.1...pinget-core-v0.14.2) - 2026-11-06

### Other

- Fix PR #66 review follow-ups: export --source, admin gates, upgrade selection, version compare ([#67](https://github.com/Devolutions/pinget/pull/67))
- Fix 18 correctness and security defects in Rust and C# ([#66](https://github.com/Devolutions/pinget/pull/66))

## [0.14.1](https://github.com/Devolutions/pinget/compare/pinget-core-v0.14.0...pinget-core-v0.14.1) - 2026-11-05

### Other

- Support machine scope for zip and portable installers ([#61](https://github.com/Devolutions/pinget/pull/61))
- Replace makeappx and signtool with in-process MSIX writing and psign-tool ([#60](https://github.com/Devolutions/pinget/pull/60))
- Add native WinGet preindexed repository publishers in Rust and C# ([#59](https://github.com/Devolutions/pinget/pull/59))
- Fixes uninstall dispatch for EXE/Burn ARP entries whose registry subkey is GUID-shaped. ([#57](https://github.com/Devolutions/pinget/pull/57))

## [0.11.0](https://github.com/Devolutions/pinget/compare/pinget-core-v0.10.0...pinget-core-v0.11.0) - 2026-09-01

### Other

- Address review: unsafe env mutation and Auto precedence
- Drop a redundant Read import from the core test module
- Bump version to 0.11.0
- Cover the source-mode environment variable end to end
- Let a host relocate storage without losing the machine's sources

## [0.8.1](https://github.com/Devolutions/pinget/compare/pinget-core-v0.8.0...pinget-core-v0.8.1) - 2026-06-02

### Other

- Fix empty/hanging WinGet Discover after the Turso migration ([#32](https://github.com/Devolutions/pinget/pull/32))

## [0.4.2](https://github.com/Devolutions/pinget/compare/pinget-core-v0.4.1...pinget-core-v0.4.2) - 2026-05-13

### Other

- apply stable rustfmt to correlation fix
- bump version to 0.4.2
- Fix some missing update
- Fix uninstalling not working for some packages

## [0.4.0](https://github.com/Devolutions/pinget/compare/v0.3.1...v0.4.0) - 2026-05-11

### Other

- Bump Pinget to 0.4.0

## [0.3.1](https://github.com/Devolutions/pinget/compare/v0.3.0...v0.3.1) - 2026-05-08

### Other

- Add release-plz workflow for publishing pinget-core to crates.io ([#13](https://github.com/Devolutions/pinget/pull/13))
