using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pinget.Publisher;

namespace Devolutions.Pinget.Cli;

internal static class PublisherCommands
{
    public static Command Create(Option<string?> outputOption)
    {
        var root = new Command("repository", "Build and publish WinGet v2 preindexed sources");

        var stateBuild = new Option<string>("--state", "Publisher working directory") { IsRequired = true };
        var inputBuild = InputOption();
        var build = new Command("build", "Build an immutable source snapshot");
        build.AddOption(stateBuild);
        build.AddOption(inputBuild);
        build.SetHandler(context => Run(context, outputOption, () =>
        {
            var result = new PublisherWorkspace(context.ParseResult.GetValueForOption(stateBuild)!).Build(
                new PublisherBuildOptions(
                    context.ParseResult.GetValueForOption(inputBuild)!,
                    context.ParseResult.GetValueForOption(stateBuild)!));
            return (Object(
                ("buildId", JsonValue.Create(result.BuildId)),
                ("snapshotDirectory", JsonValue.Create(result.SnapshotDirectory)),
                ("manifestCount", JsonValue.Create(result.ManifestCount)),
                ("packageCount", JsonValue.Create(result.PackageCount)),
                ("changedFiles", JsonValue.Create(result.ChangedFiles)),
                ("removedFiles", JsonValue.Create(result.RemovedFiles))),
                $"Built {result.PackageCount} package(s), {result.ManifestCount} manifest(s). Build: {result.BuildId}");
        }));

        var stateDiff = new Option<string>("--state", "Publisher working directory") { IsRequired = true };
        var inputDiff = InputOption();
        var diff = new Command("diff", "Compare manifests with the last successful build");
        diff.AddOption(stateDiff);
        diff.AddOption(inputDiff);
        diff.SetHandler(context => Run(context, outputOption, () =>
        {
            var result = new PublisherWorkspace(context.ParseResult.GetValueForOption(stateDiff)!)
                .Diff(context.ParseResult.GetValueForOption(inputDiff)!);
            return (Object(
                ("lastBuildId", JsonValue.Create(result.LastBuildId)),
                ("published", JsonValue.Create(result.Published)),
                ("added", Array(result.Added)),
                ("changed", Array(result.Changed)),
                ("removed", Array(result.Removed))),
                $"Added {result.Added.Count}, changed {result.Changed.Count}, removed {result.Removed.Count}; last build published: {result.Published}");
        }));

        var stateStatus = new Option<string>("--state", "Publisher working directory") { IsRequired = true };
        var status = new Command("status", "Show publisher state without rescanning manifests");
        status.AddOption(stateStatus);
        status.SetHandler(context => Run(context, outputOption, () =>
        {
            var result = new PublisherWorkspace(context.ParseResult.GetValueForOption(stateStatus)!).Status();
            return (Object(
                ("lastBuildId", JsonValue.Create(result.LastBuildId)),
                ("lastPublishedBuildId", JsonValue.Create(result.LastPublishedBuildId)),
                ("buildCount", JsonValue.Create(result.BuildCount)),
                ("lastBuildTimestamp", JsonValue.Create(result.LastBuildTimestamp))),
                result.LastBuildId is null ? "No publisher builds have been recorded." : $"Builds: {result.BuildCount}; latest: {result.LastBuildId}; published: {result.LastPublishedBuildId ?? "never"}");
        }));

        var stateVerify = new Option<string?>("--state", "Publisher working directory");
        var buildVerify = new Option<string?>("--build", "Build snapshot ID");
        var destinationVerify = new Option<string?>("--destination", "Published static source directory");
        var psignToolVerify = PsignToolOption();
        var trustedCaVerify = TrustedCaOption();
        var verify = new Command("verify", "Verify a build snapshot or published directory");
        verify.AddOption(stateVerify);
        verify.AddOption(buildVerify);
        verify.AddOption(destinationVerify);
        verify.AddOption(psignToolVerify);
        verify.AddOption(trustedCaVerify);
        verify.SetHandler(context => Run(context, outputOption, () =>
        {
            var state = context.ParseResult.GetValueForOption(stateVerify);
            var destination = context.ParseResult.GetValueForOption(destinationVerify);
            if ((state is null) == (destination is null))
                throw new PublisherException("Provide exactly one of --state or --destination.");
            if (destination is not null && context.ParseResult.GetValueForOption(buildVerify) is not null)
                throw new PublisherException("--build cannot be combined with --destination.");
            var psignTool = context.ParseResult.GetValueForOption(psignToolVerify);
            var trustedCa = context.ParseResult.GetValueForOption(trustedCaVerify) ?? [];
            if (destination is null && (psignTool is not null || trustedCa.Length > 0))
                throw new PublisherException("--psign-tool and --trusted-ca require --destination.");
            var result = destination is not null
                ? PublisherPackaging.VerifyDestination(destination, psignTool, trustedCa)
                : new PublisherWorkspace(state!).Verify(context.ParseResult.GetValueForOption(buildVerify));
            return (Object(
                ("valid", JsonValue.Create(result.Valid)),
                ("subject", JsonValue.Create(result.Subject)),
                ("artifactCount", JsonValue.Create(result.ArtifactCount)),
                ("buildId", JsonValue.Create(result.BuildId))),
                $"Verified {result.Subject}; artifacts: {result.ArtifactCount}.");
        }));

        var psignToolDoctor = PsignToolOption();
        var doctor = new Command("doctor", "Check local publisher capabilities and signing tools");
        doctor.AddOption(psignToolDoctor);
        doctor.SetHandler(context => Run(context, outputOption, () =>
        {
            var result = PublisherPackaging.Doctor(context.ParseResult.GetValueForOption(psignToolDoctor));
            return (Object(
                ("operatingSystem", JsonValue.Create(result.OperatingSystem)),
                ("psignToolPath", JsonValue.Create(result.PsignToolPath)),
                ("nativeMsZipAvailable", JsonValue.Create(result.NativeMsZipAvailable))),
                $"MSZIP: {(result.NativeMsZipAvailable ? "available" : "unavailable")}; psign-tool: {result.PsignToolPath ?? "not found"}; signed publishing available: {(result.SignedPublishAvailable ? "yes" : "no")}.");
        }));

        var statePublish = new Option<string>("--state", "Publisher working directory") { IsRequired = true };
        var destinationPublish = new Option<string>("--destination", "Static source output directory") { IsRequired = true };
        var namePublish = new Option<string>("--name", "MSIX package identity name") { IsRequired = true };
        var publisherPublish = new Option<string>("--publisher", "MSIX publisher distinguished name") { IsRequired = true };
        var versionPublish = new Option<string>("--version", "Four-part MSIX package version") { IsRequired = true };
        var pfxPublish = new Option<string?>("--pfx", "PFX file used to sign the source MSIX");
        var passwordEnvPublish = new Option<string?>("--password-env", "Environment variable containing the PFX password");
        var unsignedPublish = new Option<bool>("--unsigned-development", "Allow explicitly unsigned development publishing");
        var psignToolPublish = PsignToolOption();
        var trustedCaPublish = TrustedCaOption();
        var timestampPublish = new Option<string?>("--timestamp-url", "HTTP(S) RFC 3161 timestamp service URL");
        var buildPublish = new Option<string?>("--build", "Build snapshot ID (defaults to latest)");
        var publish = new Command("publish", "Package and publish a source2.msix and immutable artifacts");
        foreach (var option in new Option[]
                 {
                     statePublish, destinationPublish, namePublish, publisherPublish, versionPublish,
                     pfxPublish, passwordEnvPublish, unsignedPublish, psignToolPublish, trustedCaPublish, buildPublish, timestampPublish,
                 })
            publish.AddOption(option);
        publish.SetHandler(context => Run(context, outputOption, () =>
        {
            var result = PublisherPackaging.Publish(new PublisherPublishOptions(
                context.ParseResult.GetValueForOption(statePublish)!,
                context.ParseResult.GetValueForOption(destinationPublish)!,
                context.ParseResult.GetValueForOption(namePublish)!,
                context.ParseResult.GetValueForOption(publisherPublish)!,
                context.ParseResult.GetValueForOption(versionPublish)!,
                context.ParseResult.GetValueForOption(pfxPublish),
                context.ParseResult.GetValueForOption(passwordEnvPublish),
                context.ParseResult.GetValueForOption(unsignedPublish),
                context.ParseResult.GetValueForOption(psignToolPublish),
                context.ParseResult.GetValueForOption(buildPublish),
                context.ParseResult.GetValueForOption(timestampPublish),
                context.ParseResult.GetValueForOption(trustedCaPublish)));
            return (Object(
                ("valid", JsonValue.Create(result.Valid)),
                ("subject", JsonValue.Create(result.Subject)),
                ("artifactCount", JsonValue.Create(result.ArtifactCount)),
                ("buildId", JsonValue.Create(result.BuildId))),
                $"Published and verified source for build {result.BuildId}; artifacts: {result.ArtifactCount}.");
        }));

        root.AddCommand(build);
        root.AddCommand(diff);
        root.AddCommand(status);
        root.AddCommand(verify);
        root.AddCommand(doctor);
        root.AddCommand(publish);
        return root;
    }

    private static Option<string?> PsignToolOption() =>
        new("--psign-tool", "Path to psign-tool (searched on PATH and in the .NET global tool directory when omitted)");

    private static Option<string[]> TrustedCaOption() =>
        new("--trusted-ca", "Certificate trusted as a signature anchor instead of the platform default (repeatable)")
        {
            ArgumentHelpName = "certificate",
            AllowMultipleArgumentsPerToken = false,
        };

    private static Option<string> InputOption()
    {
        var option = new Option<string>("--input", "Root directory containing WinGet YAML manifests") { IsRequired = true };
        option.AddAlias("--manifests");
        return option;
    }

    private static void Run(System.CommandLine.Invocation.InvocationContext context, Option<string?> outputOption, Func<(JsonObject Result, string Text)> action)
    {
        try
        {
            var output = context.ParseResult.GetValueForOption(outputOption);
            if (output is not null && output.ToLowerInvariant() is not ("text" or "json" or "yaml"))
                throw new PublisherException("--output must be text, json, or yaml.");
            var (result, text) = action();
            if (string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            else if (string.Equals(output, "yaml", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine(Devolutions.Pinget.Core.YamlEmitter.EmitDocument(result));
            else
                Console.WriteLine(text);
        }
        catch (Exception ex)
        {
            var diagnostics = Environment.GetEnvironmentVariable("PINGET_PUBLISHER_DIAGNOSTICS");
            Console.Error.WriteLine(diagnostics == "1" ? ex : $"error: {ex.Message}");
            context.ExitCode = 1;
        }
    }

    private static JsonObject Object(params (string Name, JsonNode? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
            result[name] = value;
        return result;
    }

    private static JsonArray Array(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
