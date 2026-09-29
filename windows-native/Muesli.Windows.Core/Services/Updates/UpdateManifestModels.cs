using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

/// <summary>
/// Models for the release-side update-channel contract written by
/// <c>scripts/write-update-manifest.ps1</c> and verified by
/// <c>scripts/verify-update-manifest.ps1</c>. The app-side verifier reads exactly these fields;
/// keep the shape in step with the PowerShell contract.
/// </summary>
public sealed class UpdateManifest
{
    public int SchemaVersion { get; init; }
    public string Contract { get; init; } = "";
    public string Product { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Version { get; init; } = "";
    public string Runtime { get; init; } = "";
    public string? MinimumSupportedVersion { get; init; }
    public string? ReleaseNotes { get; init; }
    public UpdateManifestSigning Signing { get; init; } = new();
    public IReadOnlyList<UpdatePackage> Packages { get; init; } = [];
    public string? PortableContentDigest { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
}

public sealed class UpdateManifestSigning
{
    public string Mode { get; init; } = "";
    public string Status { get; init; } = "";
    public string Algorithm { get; init; } = "";
    public bool SignaturePresent { get; init; }
    public bool AuthenticodeRequired { get; init; }
    public bool AuthenticodeVerified { get; init; }
    public string PublisherPublicKeySha256 { get; init; } = "";
    public string PublisherCertificateThumbprint { get; init; } = "";
    public string PublisherSubject { get; init; } = "";
    public string DetachedSignatureFile { get; init; } = "";
}

public sealed class UpdatePackage
{
    public string Name { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Bytes { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed class UpdateDetachedSignature
{
    public int SchemaVersion { get; init; }
    public string Contract { get; init; } = "";
    public string ManifestFileName { get; init; } = "";
    public string ManifestSha256 { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Status { get; init; } = "";
    public string Algorithm { get; init; } = "";
    public string SignatureBase64 { get; init; } = "";
    public string PublicKeySpkiBase64 { get; init; } = "";
    public string PublisherPublicKeySha256 { get; init; } = "";
    public string PublisherCertificateThumbprint { get; init; } = "";
    public string PublisherSubject { get; init; } = "";
    public bool Authenticode { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
}

public static class UpdateManifestContract
{
    public const string ManifestContract = "muesli.windows.update-channel";
    public const string DetachedContract = "muesli.windows.update-channel.detached-signature";
    public const int SupportedSchemaVersion = 1;
    public const string Algorithm = "RSA-SHA256";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
