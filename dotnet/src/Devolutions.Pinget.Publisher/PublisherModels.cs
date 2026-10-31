namespace Pinget.Publisher;

public sealed record PublisherBuildOptions(string InputDirectory, string StateDirectory);

public sealed record PublisherBuildResult(
    string BuildId,
    string SnapshotDirectory,
    int ManifestCount,
    int PackageCount,
    int ChangedFiles,
    int RemovedFiles);

public sealed record PublisherDiffResult(
    string? LastBuildId,
    bool Published,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Removed);

public sealed record PublisherStatusResult(
    string? LastBuildId,
    string? LastPublishedBuildId,
    int BuildCount,
    string? LastBuildTimestamp);

public sealed record PublisherDoctorResult(
    string OperatingSystem,
    string? MakeAppxPath,
    string? SignToolPath,
    bool NativeMsZipAvailable);

public sealed record PublisherPublishOptions(
    string StateDirectory,
    string DestinationDirectory,
    string PackageName,
    string Publisher,
    string PackageVersion,
    string? PfxPath = null,
    string? PasswordEnvironmentVariable = null,
    bool UnsignedDevelopment = false,
    string? MakeAppxPath = null,
    string? SignToolPath = null,
    string? BuildId = null,
    string? TimestampUrl = null);

public sealed record PublisherVerifyResult(
    bool Valid,
    string Subject,
    int ArtifactCount,
    string? BuildId);

public sealed class PublisherException(string message, Exception? inner = null)
    : Exception(message, inner);
