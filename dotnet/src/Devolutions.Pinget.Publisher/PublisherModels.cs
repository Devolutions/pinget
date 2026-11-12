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
    string? PsignToolPath,
    bool NativeMsZipAvailable)
{
    /// <summary>Signed MSIX publication is available when psign-tool was found.</summary>
    public bool SignedPublishAvailable => PsignToolPath is not null;
}

public sealed record PublisherPackageOptions(
    string StateDirectory,
    string MsixPath,
    string PackageName,
    string Publisher,
    string PackageVersion,
    string? BuildId = null,
    string? DestinationDirectory = null);

public sealed record PublisherPackageResult(
    string BuildId,
    string MsixPath,
    string PackageName,
    string Publisher,
    string PackageVersion,
    string IndexHash,
    string PackageHash);

public sealed record PublisherPublishOptions(
    string StateDirectory,
    string DestinationDirectory,
    string MsixPath,
    bool UnsignedDevelopment = false,
    string? PsignToolPath = null,
    string? BuildId = null,
    IReadOnlyList<string>? TrustedCertificateAuthorities = null);

public sealed record PublisherVerifyResult(
    bool Valid,
    string Subject,
    int ArtifactCount,
    string? BuildId);

public sealed class PublisherException(string message, Exception? inner = null)
    : Exception(message, inner);
