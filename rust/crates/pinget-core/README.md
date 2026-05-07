# pinget-core

Pure Rust core library for [Pinget](https://github.com/Devolutions/pinget) — a cross-platform package manager that works directly with WinGet source caches, REST endpoints, and installed package state without COM.

## Features

- Search, list, and show package information from WinGet-compatible sources
- Install, upgrade, repair, and uninstall packages (Windows)
- Manage package sources (add, remove, update, export, reset)
- Query installed package state from multiple providers (WinGet, ARP, MSIX)
- No dependency on WinGet COM APIs

## Usage

Add `pinget-core` to your `Cargo.toml`:

```toml
[dependencies]
pinget-core = "0.3"
```

## License

This project is licensed under the [MIT License](https://github.com/Devolutions/pinget/blob/master/LICENSE).
