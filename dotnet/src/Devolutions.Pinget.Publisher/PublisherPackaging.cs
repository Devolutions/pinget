using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Pinget.Publisher;

public static partial class PublisherPackaging
{
    private const string MarkerName = ".pinget-publisher.json";
    private const string PackageExtensionId = "IndexDB";
    private static readonly byte[] LogoPng = Convert.FromHexString(
        "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000b49444154789c636000020000050001a5f645400000000049454e44ae426082");

    public static PublisherDoctorResult Doctor(string? psignToolPath = null) =>
        new(
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            PsignTool.Find(psignToolPath),
            MsZipCompressor.IsAvailable());

    /// <summary>
    /// Verifies a published static source and, unless unsigned-development, its MSIX signature.
    /// </summary>
    /// <remarks>
    /// Signature trust uses <paramref name="trustedCertificateAuthorities"/> when given; otherwise the
    /// Windows trust store on Windows and Microsoft AuthRoot anchors elsewhere.
    /// </remarks>
    public static PublisherVerifyResult VerifyDestination(
        string destinationDirectory,
        string? psignToolPath = null,
        IReadOnlyList<string>? trustedCertificateAuthorities = null)
    {
        var destination = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(destination))
            throw new PublisherException($"Published source directory does not exist: {destination}");
        PublisherWorkspace.EnsureSafeExistingPath(destination);
        var markerPath = Path.Combine(destination, MarkerName);
        if (!File.Exists(markerPath))
            throw new PublisherException($"Destination is not owned by Pinget publisher; missing {MarkerName}.");
        var marker = ReadMarker(markerPath);
        if (marker["schemaVersion"]?.GetValue<int>() != 1 || marker["owner"]?.GetValue<string>() != "Pinget.Publisher")
            throw new PublisherException("Unsupported publisher ownership marker.");
        var inventory = marker["artifacts"] as JsonObject
            ?? throw new PublisherException("Publisher ownership marker has no artifact inventory.");
        var directories = new Stack<string>();
        directories.Push(destination);
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PublisherException($"Reparse point is not allowed in published source: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }
                var relativePath = Path.GetRelativePath(destination, entry).Replace('\\', '/');
                if (relativePath != MarkerName && !inventory.ContainsKey(relativePath))
                    throw new PublisherException($"Published source contains an untracked artifact: {relativePath}");
            }
        }
        foreach (var item in inventory)
        {
            var path = PublisherWorkspace.ResolveContained(destination, item.Key);
            PublisherWorkspace.EnsureSafeExistingPath(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new PublisherException($"Reparse point is not allowed for published artifact: {item.Key}");
            if (!File.Exists(path))
                throw new PublisherException($"Published artifact is missing: {item.Key}");
            var actual = HashFile(path);
            if (!string.Equals(item.Value?.GetValue<string>(), actual, StringComparison.Ordinal))
                throw new PublisherException($"Published artifact hash mismatch: {item.Key}");
        }

        var packagePath = Path.Combine(destination, "source2.msix");
        if (!File.Exists(packagePath))
            throw new PublisherException("Published source2.msix is missing.");
        VerifyMsixContents(packagePath, marker);
        var unsigned = marker["unsignedDevelopment"]?.GetValue<bool>() ?? false;
        if (!unsigned)
        {
            var psignTool = PsignTool.Require(psignToolPath);
            var publisher = marker["publisher"]?.GetValue<string>()
                ?? throw new PublisherException("Publisher ownership marker has no publisher.");
            PsignTool.Verify(psignTool, packagePath, publisher, TrustedAnchors(trustedCertificateAuthorities));
        }
        return new PublisherVerifyResult(true, "published WinGet v2 static source", inventory.Count, marker["buildId"]?.GetValue<string>());
    }

    public static PublisherVerifyResult Publish(PublisherPublishOptions options) =>
        Publish(options, afterCopy: null);

    /// <summary>
    /// Publishes with a test hook invoked after feed artifacts are copied and before the staged
    /// package is verified and promoted; it receives the destination and staged package paths.
    /// </summary>
    internal static PublisherVerifyResult Publish(PublisherPublishOptions options, Action<string, string>? afterCopy)
    {
        var state = Path.GetFullPath(options.StateDirectory);
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DestinationDirectory));
        PublisherWorkspace.EnsureDisjoint(state, destination, "state", "destination");
        PublisherWorkspace.EnsureSafeExistingPath(state);
        if (!Directory.Exists(state))
            throw new PublisherException($"Publisher state does not exist: {state}");
        ValidateIdentity(options.PackageName, options.Publisher);
        var version = ParsePackageVersion(options.PackageVersion);
        if (options.UnsignedDevelopment == (options.PfxPath is not null))
            throw new PublisherException("Choose exactly one: --unsigned-development or --pfx with a signing password environment variable.");
        if (options.PfxPath is not null && string.IsNullOrWhiteSpace(options.PasswordEnvironmentVariable))
            throw new PublisherException("Signing requires --password-env naming the environment variable containing the PFX password.");
        if (options.UnsignedDevelopment && options.PasswordEnvironmentVariable is not null)
            throw new PublisherException("--unsigned-development cannot be combined with --password-env.");
        if (options.TimestampUrl is not null
            && (options.UnsignedDevelopment
                || !Uri.TryCreate(options.TimestampUrl, UriKind.Absolute, out var timestampUri)
                || (timestampUri.Scheme != Uri.UriSchemeHttps && timestampUri.Scheme != Uri.UriSchemeHttp)
                || options.TimestampUrl.Any(char.IsWhiteSpace)))
            throw new PublisherException("--timestamp-url requires signed publication and an HTTP(S) URL.");
        using (var existingState = PublisherWorkspace.OpenState(state, create: false))
        {
        }
        using var writerLock = PublisherWorkspace.AcquireLock(state);
        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new PublisherException("Publication destination must not be a filesystem root.");
        PublisherWorkspace.EnsureSafeExistingPath(destinationParent);
        Directory.CreateDirectory(destinationParent);
        var destinationLockPath = Path.Combine(destinationParent, Path.GetFileName(destination) + ".pinget-publisher.lock");
        if (File.Exists(destinationLockPath) && (File.GetAttributes(destinationLockPath) & FileAttributes.ReparsePoint) != 0)
            throw new PublisherException("Destination lock cannot be a reparse point.");
        using var destinationLock = new FileStream(destinationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var trustedAnchors = TrustedAnchors(options.TrustedCertificateAuthorities);
        var psignTool = options.UnsignedDevelopment ? null : PsignTool.Require(options.PsignToolPath);

        var workspace = new PublisherWorkspace(state);
        var buildId = options.BuildId ?? workspace.Status().LastBuildId
            ?? throw new PublisherException("Publisher state has no successful build to publish.");
        _ = workspace.Verify(buildId);

        PublisherWorkspace.EnsureSafeExistingPath(destination);
        Directory.CreateDirectory(destination);
        PublisherWorkspace.EnsureSafeExistingPath(destination);
        var markerPath = Path.Combine(destination, MarkerName);
        var marker = ExistingDestinationMarker(destination, markerPath);
        if (marker is not null)
            _ = VerifyDestination(destination, options.PsignToolPath, trustedAnchors);
        var oldBuildId = marker?["buildId"]?.GetValue<string>();
        var oldVersionText = marker?["packageVersion"]?.GetValue<string>();
        if (marker is not null)
        {
            var existingIdentity = marker["packageName"]?.GetValue<string>();
            var existingPublisher = marker["publisher"]?.GetValue<string>();
            if (!string.Equals(existingIdentity, options.PackageName, StringComparison.Ordinal)
                || !string.Equals(existingPublisher, options.Publisher, StringComparison.Ordinal))
                throw new PublisherException("The destination is already owned by a different source package identity or publisher.");
            if (oldVersionText is null || !Version.TryParse(oldVersionText, out var oldVersion))
                throw new PublisherException("Existing publisher marker has an invalid source package version.");
            var comparison = version.CompareTo(oldVersion);
            if (comparison < 0)
                throw new PublisherException($"Source package version {version} must be greater than published version {oldVersion}.");
            if (comparison == 0)
            {
                if (!string.Equals(oldBuildId, buildId, StringComparison.Ordinal)
                    || (marker["unsignedDevelopment"]?.GetValue<bool>() ?? false) != options.UnsignedDevelopment)
                    throw new PublisherException("The source package version has already been used for different build content or signing mode.");
                var existing = VerifyDestination(destination, options.PsignToolPath, trustedAnchors);
                PublisherWorkspace.RecordPublished(state, buildId);
                return existing;
            }
        }

        var buildPath = Path.Combine(state, "builds", buildId);
        var stagingRoot = Path.Combine(state, $".publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        var createdArtifacts = new List<string>();
        var createdDirectories = new List<string>();
        var promoted = false;
        try
        {
            var stagedPackage = Path.Combine(stagingRoot, "source2.msix");
            File.WriteAllBytes(stagedPackage, MsixPackageWriter.Write(
            [
                ("AppxManifest.xml", AppxManifest(options, version)),
                ("Assets/StoreLogo.png", LogoPng),
                ("Assets/Logo150.png", LogoPng),
                ("Assets/Logo44.png", LogoPng),
                ("Public/index.db", File.ReadAllBytes(Path.Combine(buildPath, "Public", "index.db"))),
            ]));
            if (!options.UnsignedDevelopment)
            {
                var password = Environment.GetEnvironmentVariable(options.PasswordEnvironmentVariable!);
                if (string.IsNullOrEmpty(password))
                    throw new PublisherException($"Signing password environment variable '{options.PasswordEnvironmentVariable}' is not set or empty.");
                if (!File.Exists(Path.GetFullPath(options.PfxPath!)))
                    throw new PublisherException($"PFX file does not exist: {Path.GetFullPath(options.PfxPath!)}");
                PsignTool.Sign(psignTool!, stagedPackage, Path.GetFullPath(options.PfxPath!), password, options.TimestampUrl);
                PsignTool.Verify(psignTool!, stagedPackage, options.Publisher, trustedAnchors);
            }

            var existingArtifacts = marker?["artifacts"]?.DeepClone() as JsonObject ?? new JsonObject();
            var snapshotArtifacts = ReadSnapshotArtifacts(buildPath);
            foreach (var item in snapshotArtifacts)
            {
                var artifactHash = item.Value?.GetValue<string>()
                    ?? throw new PublisherException($"Invalid artifact hash for '{item.Key}'.");
                var source = PublisherWorkspace.ResolveContained(Path.Combine(buildPath, "feed"), item.Key);
                var target = PublisherWorkspace.ResolveContained(destination, item.Key);
                PublisherWorkspace.EnsureSafeExistingPath(Path.GetDirectoryName(target)!);
                var targetDirectory = Path.GetDirectoryName(target)!;
                var missingDirectories = new Stack<string>();
                for (var directory = targetDirectory; !Directory.Exists(directory); directory = Path.GetDirectoryName(directory)!)
                    missingDirectories.Push(directory);
                while (missingDirectories.TryPop(out var missingDirectory))
                {
                    Directory.CreateDirectory(missingDirectory);
                    createdDirectories.Add(missingDirectory);
                }
                if (File.Exists(target))
                {
                    if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                        throw new PublisherException($"Reparse point is not allowed for published artifact: {item.Key}");
                    if (!string.Equals(HashFile(target), artifactHash, StringComparison.Ordinal))
                        throw new PublisherException($"Refusing to overwrite immutable published artifact with different bytes: {item.Key}");
                }
                else
                {
                    CopyAtomically(source, target, overwrite: false);
                    createdArtifacts.Add(target);
                    if (!string.Equals(HashFile(target), artifactHash, StringComparison.Ordinal))
                        throw new PublisherException($"Copied artifact hash mismatch: {item.Key}");
                }
                existingArtifacts[item.Key] = artifactHash;
            }

            afterCopy?.Invoke(destination, stagedPackage);
            var packageHash = HashFile(stagedPackage);
            existingArtifacts["source2.msix"] = packageHash;
            var updatedMarker = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["owner"] = "Pinget.Publisher",
                ["packageName"] = options.PackageName,
                ["publisher"] = options.Publisher,
                ["packageVersion"] = version.ToString(),
                ["buildId"] = buildId,
                ["indexHash"] = ReadSnapshotIndexHash(buildPath),
                ["unsignedDevelopment"] = options.UnsignedDevelopment,
                ["publishedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["artifacts"] = existingArtifacts,
            };
            var oldMarkerBytes = File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null;
            VerifyMsixContents(stagedPackage, updatedMarker);
            WriteJsonAtomically(markerPath, updatedMarker);
            try
            {
                CopyAtomically(stagedPackage, Path.Combine(destination, "source2.msix"));
            }
            catch
            {
                if (oldMarkerBytes is null)
                    File.Delete(markerPath);
                else
                    File.WriteAllBytes(markerPath, oldMarkerBytes);
                throw;
            }
            promoted = true;
            PublisherWorkspace.RecordPublished(state, buildId);
            return VerifyDestination(destination, options.PsignToolPath, trustedAnchors);
        }
        finally
        {
            if (!promoted)
            {
                foreach (var artifact in createdArtifacts.AsEnumerable().Reverse())
                    File.Delete(artifact);
                foreach (var directory in createdDirectories.AsEnumerable().Reverse())
                {
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
            }
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static JsonObject? ExistingDestinationMarker(string destination, string markerPath)
    {
        var entries = Directory.EnumerateFileSystemEntries(destination)
            .Where(path => !Path.GetFileName(path).Equals(MarkerName, StringComparison.Ordinal))
            .ToArray();
        if (!File.Exists(markerPath))
        {
            if (entries.Length > 0)
                throw new PublisherException($"Refusing to publish into a non-empty unowned destination: {destination}");
            return null;
        }
        var marker = ReadMarker(markerPath);
        if (marker["schemaVersion"]?.GetValue<int>() != 1)
            throw new PublisherException("Unsupported publisher ownership marker version.");
        if (!string.Equals(marker["owner"]?.GetValue<string>(), "Pinget.Publisher", StringComparison.Ordinal))
            throw new PublisherException("Destination marker is not a Pinget publisher ownership record.");
        return marker;
    }

    private static JsonObject ReadMarker(string path)
    {
        try
        {
            PublisherWorkspace.EnsureSafeExistingPath(path);
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new PublisherException($"Invalid publisher ownership marker: {path}");
        }
        catch (PublisherException) { throw; }
        catch (Exception ex) { throw new PublisherException($"Cannot read publisher ownership marker '{path}': {ex.Message}", ex); }
    }

    private static JsonObject ReadSnapshotArtifacts(string buildPath)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(buildPath, "snapshot.json"))) as JsonObject
            ?? throw new PublisherException("Invalid snapshot metadata.");
        return metadata["artifacts"] as JsonObject
            ?? throw new PublisherException("Snapshot artifact inventory is missing.");
    }

    private static string ReadSnapshotIndexHash(string buildPath)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(buildPath, "snapshot.json"))) as JsonObject
            ?? throw new PublisherException("Invalid snapshot metadata.");
        return metadata["indexHash"]?.GetValue<string>()
            ?? throw new PublisherException("Snapshot index hash is missing.");
    }

    private static void VerifyMsixContents(string packagePath, JsonObject marker)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var manifestEntry = archive.GetEntry("AppxManifest.xml")
                ?? throw new PublisherException("MSIX does not contain AppxManifest.xml.");
            var indexEntry = archive.GetEntry("Public/index.db")
                ?? throw new PublisherException("MSIX does not expose Public/index.db.");
            using (var indexStream = indexEntry.Open())
            {
                var actualIndexHash = Convert.ToHexString(SHA256.HashData(indexStream)).ToLowerInvariant();
                if (!string.Equals(actualIndexHash, marker["indexHash"]?.GetValue<string>(), StringComparison.Ordinal))
                    throw new PublisherException("MSIX contains a v2 index different from the published build.");
            }

            using var manifestStream = manifestEntry.Open();
            var xml = XDocument.Load(manifestStream);
            XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XNamespace uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
            var identity = xml.Root?.Element(foundation + "Identity")
                ?? throw new PublisherException("MSIX AppxManifest.xml has no Identity.");
            if (!string.Equals((string?)identity.Attribute("Name"), marker["packageName"]?.GetValue<string>(), StringComparison.Ordinal)
                || !string.Equals((string?)identity.Attribute("Publisher"), marker["publisher"]?.GetValue<string>(), StringComparison.Ordinal)
                || !string.Equals((string?)identity.Attribute("Version"), marker["packageVersion"]?.GetValue<string>(), StringComparison.Ordinal))
                throw new PublisherException("MSIX identity does not match the publisher ownership marker.");
            var extension = xml.Descendants(uap3 + "AppExtension").SingleOrDefault();
            if (!string.Equals((string?)extension?.Attribute("Name"), "com.microsoft.winget.source", StringComparison.Ordinal)
                || !string.Equals((string?)extension?.Attribute("PublicFolder"), "Public", StringComparison.Ordinal))
                throw new PublisherException("MSIX does not expose the expected WinGet preindexed source extension.");
        }
        catch (PublisherException) { throw; }
        catch (Exception ex) { throw new PublisherException($"Could not verify source MSIX contents: {ex.Message}", ex); }
    }

    private static void ValidateIdentity(string packageName, string publisher)
    {
        if (!PackageNamePattern().IsMatch(packageName))
            throw new PublisherException("Package identity name must contain only letters, digits, and periods.");
        if (string.IsNullOrWhiteSpace(publisher) || publisher.Any(char.IsControl) || publisher.Contains('<') || publisher.Contains('>')
            || publisher.Trim().Length != publisher.Length)
            throw new PublisherException("Publisher must be a non-empty certificate subject without control or XML delimiter characters or surrounding whitespace.");
    }

    private static Version ParsePackageVersion(string value)
    {
        var components = value.Split('.');
        if (components.Length != 4 || components.Any(part => !ushort.TryParse(part, out _))
            || !Version.TryParse(value, out var version) || version.Build < 0 || version.Revision < 0)
            throw new PublisherException("Source MSIX package version must have four numeric components (for example 1.0.0.0).");
        return version;
    }

    private static IReadOnlyList<string> TrustedAnchors(IReadOnlyList<string>? trustedCertificateAuthorities) =>
        trustedCertificateAuthorities?.Select(Path.GetFullPath).ToArray() ?? [];

    private static byte[] AppxManifest(PublisherPublishOptions options, Version version)
    {
        var name = System.Security.SecurityElement.Escape(options.PackageName);
        var publisher = System.Security.SecurityElement.Escape(options.Publisher);
        var versionText = version.ToString();
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
                     IgnorableNamespaces="uap uap3">
              <Identity Name="{name}" ProcessorArchitecture="neutral" Publisher="{publisher}" Version="{versionText}" />
              <Properties>
                <DisplayName>Pinget preindexed source</DisplayName>
                <PublisherDisplayName>Pinget source publisher</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>
              <Applications>
                <Application Id="SourceData">
                  <uap:VisualElements DisplayName="Pinget preindexed source" Square150x150Logo="Assets\Logo150.png" Square44x44Logo="Assets\Logo44.png" Description="Pinget WinGet v2 package source" BackgroundColor="#0078d7" AppListEntry="none">
                    <uap:DefaultTile />
                  </uap:VisualElements>
                  <Extensions>
                    <uap3:Extension Category="windows.appExtension">
                      <uap3:AppExtension Name="com.microsoft.winget.source" Id="{PackageExtensionId}" DisplayName="Pinget preindexed source" Description="Pinget WinGet v2 package source" PublicFolder="Public" />
                    </uap3:Extension>
                  </Extensions>
                </Application>
              </Applications>
              <Resources><Resource Language="und" /></Resources>
            </Package>
            """;
        return new UTF8Encoding(false).GetBytes(xml.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void CopyAtomically(string source, string destination, bool overwrite = true)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void WriteJsonAtomically(string path, JsonObject value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    [GeneratedRegex(@"^[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNamePattern();
}
