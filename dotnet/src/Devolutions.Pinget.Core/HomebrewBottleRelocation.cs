using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Devolutions.Pinget.Core;

internal static class HomebrewBottleRelocation
{
    internal static void Relocate(IReadOnlyList<string> files, string prefix, string originalPrefix)
    {
        foreach (var path in files)
        {
            if (OperatingSystem.IsWindows())
            {
                RelocateFile(path, prefix, originalPrefix);
                continue;
            }
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try
            {
                RelocateFile(path, prefix, originalPrefix);
            }
            finally
            {
                File.SetUnixFileMode(path, mode);
            }
        }
    }

    private static void RelocateFile(string path, string prefix, string originalPrefix)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024)
            throw new InvalidDataException($"Bottle relocation exceeds the 256 MiB per-file limit: {path}");
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith("\x7f"u8) && bytes.Length >= 4 &&
            bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
        {
            if (!OperatingSystem.IsLinux())
                throw new InvalidDataException("A Linux ELF bottle cannot be installed on this platform.");
            if (HasElfProgramHeader(bytes, 2))
            {
                var rpath = Run("patchelf", "--print-rpath", path).TrimEnd('\r', '\n');
                var relocated = Replace(rpath, prefix, originalPrefix);
                if (rpath != relocated)
                    Run("patchelf", "--set-rpath", relocated, path);
            }
            if (HasElfProgramHeader(bytes, 3))
            {
                var interpreter = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "/lib64/ld-linux-x86-64.so.2",
                    Architecture.Arm64 => "/lib/ld-linux-aarch64.so.1",
                    _ => throw new PlatformNotSupportedException("Bottle relocation supports x64 and arm64 Linux."),
                };
                if (!File.Exists(interpreter))
                    throw new PlatformNotSupportedException($"The host glibc interpreter is missing: {interpreter}");
                Run("patchelf", "--set-interpreter", interpreter, path);
            }
        }
        else if (IsMachO(bytes))
        {
            if (!OperatingSystem.IsMacOS())
                throw new InvalidDataException("A macOS Mach-O bottle cannot be installed on this platform.");
            RelocateMachO(path, prefix, originalPrefix);
        }
        else if (!bytes.Contains((byte)0))
        {
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return;
            }
            var relocated = Replace(text, prefix, originalPrefix);
            if (relocated != text)
                File.WriteAllText(path, relocated, new UTF8Encoding(false));
        }
    }

    private static string Replace(string value, string prefix, string originalPrefix) =>
        value.Replace(originalPrefix + "/", prefix + "/", StringComparison.Ordinal)
            .Replace("@@HOMEBREW_CELLAR@@", prefix + "/Cellar", StringComparison.Ordinal)
            .Replace("@@HOMEBREW_PREFIX@@", prefix, StringComparison.Ordinal);

    private static bool HasElfProgramHeader(byte[] bytes, uint programType)
    {
        if (bytes.Length < 64 || bytes[4] != 2 || bytes[5] != 1)
            throw new InvalidDataException("Bottle relocation requires a little-endian 64-bit ELF file.");
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32));
        var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(54));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(56));
        if (size < 56 || offset > (ulong)bytes.Length || (ulong)count * size > (ulong)bytes.Length - offset)
            throw new InvalidDataException("Invalid ELF program-header table.");
        for (var index = 0; index < count; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checked((int)(offset + (ulong)index * size)))) == programType)
                return true;
        }
        return false;
    }

    private static bool IsMachO(byte[] bytes)
    {
        if (bytes.Length < 4)
            return false;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return magic is 0xFEEDFACF or 0xCFFAEDFE or 0xBEBAFECA or 0xCAFEBABE or 0xBFBAFECA or 0xCAFEBABF;
    }

    private static void RelocateMachO(string path, string prefix, string originalPrefix)
    {
        var changed = false;
        var lines = Run("/usr/bin/otool", "-L", path).Split('\n').Skip(1);
        foreach (var line in lines)
        {
            var dependency = line.Trim().Split(" (", 2, StringSplitOptions.None)[0];
            var relocated = Replace(dependency, prefix, originalPrefix);
            if (dependency == relocated)
                continue;
            Run("/usr/bin/install_name_tool", "-change", dependency, relocated, path);
            changed = true;
        }
        var command = "";
        foreach (var line in Run("/usr/bin/otool", "-l", path).Split('\n'))
        {
            var value = line.Trim();
            if (value.StartsWith("cmd ", StringComparison.Ordinal))
                command = value[4..];
            if (command is not ("LC_RPATH" or "LC_ID_DYLIB"))
                continue;
            var field = command == "LC_RPATH" ? "path " : "name ";
            if (!value.StartsWith(field, StringComparison.Ordinal))
                continue;
            var name = value[field.Length..].Split(" (offset ", 2, StringSplitOptions.None)[0];
            var relocated = Replace(name, prefix, originalPrefix);
            if (name == relocated)
                continue;
            if (command == "LC_RPATH")
                Run("/usr/bin/install_name_tool", "-rpath", name, relocated, path);
            else
                Run("/usr/bin/install_name_tool", "-id", relocated, path);
            changed = true;
        }
        if (changed)
            Run("/usr/bin/codesign", "--force", "--sign", "-", path);
    }

    internal static string Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new IOException($"Could not start bottle relocation tool: {executable}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var result = output.GetAwaiter().GetResult();
        var diagnostic = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException($"Bottle relocation tool {executable} exited with {process.ExitCode}: {diagnostic.Trim()}");
        return result;
    }
}
