using System.Text;

namespace Devolutions.Pinget.Cli;

internal static class CliConsole
{
    /// <summary>
    /// Writes console and redirected output as UTF-8 for the rest of the process, like WinGet
    /// (Core.cpp switches the console output code page to CP_UTF8 and restores it on exit).
    /// Without it, non-ASCII package names print as '?' and pipes receive OEM code page bytes.
    /// </summary>
    internal static IDisposable UseUtf8Output()
    {
        try
        {
            Encoding? previous = Console.OutputEncoding;
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return new Restore(previous);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // No console to switch (e.g. a detached process): still emit UTF-8 into a redirected stdout.
            if (Console.IsOutputRedirected)
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            }

            return new Restore(null);
        }
    }

    private sealed class Restore(Encoding? previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous is null)
                return;

            try
            {
                Console.Out.Flush();
                Console.OutputEncoding = previous;
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
            }
        }
    }
}
