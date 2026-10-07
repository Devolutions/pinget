using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

internal sealed record ImportPackage(string Id, string? SourceName, string? Version);

internal static class ImportCommandHandler
{
    internal static int Run(
        IEnumerable<ImportPackage> packages,
        bool dryRun,
        bool ignoreUnavailable,
        bool noUpgrade,
        Func<ImportPackage, bool> isInstalled,
        Func<ImportPackage, InstallResult> install)
    {
        int total = 0;
        int skipped = 0;
        bool hasFailures = false;

        foreach (var package in packages)
        {
            if (dryRun)
            {
                Console.WriteLine($"[dry-run] Would install: {package.Id}");
            }
            else
            {
                try
                {
                    if (noUpgrade && isInstalled(package))
                    {
                        Console.WriteLine($"[no-upgrade] Skipping already installed package: {package.Id}");
                        skipped++;
                    }
                    else
                    {
                        Console.Write($"Installing {package.Id}...");
                        var result = install(package);
                        if (result.NoOp)
                        {
                            Console.WriteLine(" no-op");
                            PrintWarnings(result.Warnings);
                            skipped++;
                        }
                        else
                        {
                            PrintWarnings(result.Warnings);
                            Console.WriteLine(result.Success ? " done" : $" failed (exit {result.ExitCode})");
                            hasFailures |= !result.Success;
                        }
                    }
                }
                catch (Exception ex) when (ignoreUnavailable && CanIgnoreUnavailableImportFailure(ex))
                {
                    Console.WriteLine(" unavailable");
                    Console.Error.WriteLine($"warning: Skipping unavailable package '{package.Id}': {ex.Message}");
                    skipped++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($" error: {ex.Message}");
                    hasFailures = true;
                }
            }
            total++;
        }

        if (!dryRun && skipped > 0)
            Console.WriteLine($"Skipped {skipped} package(s).");
        Console.WriteLine($"{total} package(s) {(dryRun ? "would be installed" : "processed")}.");
        return hasFailures ? 1 : 0;
    }

    private static bool CanIgnoreUnavailableImportFailure(Exception ex) =>
        ex is InvalidOperationException &&
        (ex.Message.Contains("No package matched the query", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("No package matched the supplied query", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("No applicable installer found", StringComparison.OrdinalIgnoreCase));

    private static void PrintWarnings(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
            Console.Error.WriteLine($"warning: {warning}");
    }
}
