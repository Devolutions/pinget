# Devolutions.Pinget.Tool

`Devolutions.Pinget.Tool` installs the C# Pinget CLI as a framework-dependent .NET tool.

## Install globally

```powershell
dotnet tool install -g Devolutions.Pinget.Tool
```

## Install in a local tool manifest

```powershell
dotnet new tool-manifest
dotnet tool install Devolutions.Pinget.Tool
dotnet tool run pinget -- --help
```

## Run

```powershell
pinget --help
pinget --info
```

This package uses the C# Pinget implementation and requires a compatible .NET runtime on the target machine. It is separate from the `Devolutions.Pinget.Cli.Rust` and `Devolutions.Pinget.Cli.DotNet` packages, which are build-time `PackageReference` packages that copy prebuilt executables into another project's output.
