using System.Security.Cryptography;
using System.Text.Json;

namespace Muesli.Windows.Services;

/// <summary>
/// App-side verification of a signed update manifest. Mirrors
/// <c>scripts/verify-update-manifest.ps1</c>: it fails closed and never authorizes an installation
/// it cannot independently tie to a pinned publisher key. Fixture/dry-run and unsigned manifests
/// are parsed but never authorize a production update.
/// </summary>
public static class SignedUpdateVerifier
{
    public static UpdateVerificationResult Verify(
        byte[] manifestBytes,
        byte[] detachedSignatureBytes,
        string? pinnedPublisherPublicKeySha256)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(detachedSignatureBytes);

        UpdateManifest manifest;
        UpdateDetachedSignature signature;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, UpdateManifestContract.Json)
                ?? throw new JsonException("empty manifest");
            signature = JsonSerializer.Deserialize<UpdateDetachedSignature>(detachedSignatureBytes, UpdateManifestContract.Json)
                ?? throw new JsonException("empty signature record");
        }
        catch (JsonException)
        {
            return UpdateVerificationResult.Failure("malformed-manifest", "The update manifest could not be read.");
        }

        if (manifest.SchemaVersion != UpdateManifestContract.SupportedSchemaVersion ||
            !string.Equals(manifest.Contract, UpdateManifestContract.ManifestContract, StringComparison.Ordinal))
        {
            return UpdateVerificationResult.Failure("unsupported-contract", "The update manifest contract is not supported.");
        }

        if (signature.SchemaVersion != UpdateManifestContract.SupportedSchemaVersion ||
            !string.Equals(signature.Contract, UpdateManifestContract.DetachedContract, StringComparison.Ordinal))
        {
            return UpdateVerificationResult.Failure("unsupported-contract", "The detached signature contract is not supported.");
        }

        var manifestHash = HashHex(manifestBytes);
        if (!string.Equals(NormalizeHash(signature.ManifestSha256), manifestHash, StringComparison.Ordinal))
        {
            return UpdateVerificationResult.Failure("manifest-hash-mismatch", "The detached signature does not cover this manifest.");
        }

        var mode = (manifest.Signing.Mode ?? "").Trim().ToLowerInvariant();
        if (mode.Length == 0 || !string.Equals(mode, (signature.Mode ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals((manifest.Signing.Status ?? "").Trim(), (signature.Status ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return UpdateVerificationResult.Failure("signing-metadata-mismatch", "The manifest signing metadata disagrees with its signature record.");
        }

        if (!string.Equals(manifest.Signing.Algorithm, UpdateManifestContract.Algorithm, StringComparison.Ordinal) ||
            !string.Equals(signature.Algorithm, UpdateManifestContract.Algorithm, StringComparison.Ordinal))
        {
            return UpdateVerificationResult.Failure("unsupported-algorithm", "Only RSA-SHA256 update signatures are supported.");
        }

        if (mode == "unsigned")
        {
            return UpdateVerificationResult.Failure("unsigned-manifest", "An unsigned manifest can never authorize a Windows update.");
        }

        if (string.IsNullOrWhiteSpace(pinnedPublisherPublicKeySha256))
        {
            return UpdateVerificationResult.Failure(
                "missing-publisher-pin",
                "No publisher key is pinned, so the update cannot be trusted and is refused.");
        }

        if (!manifest.Signing.SignaturePresent ||
            string.IsNullOrWhiteSpace(signature.SignatureBase64) ||
            string.IsNullOrWhiteSpace(signature.PublicKeySpkiBase64))
        {
            return UpdateVerificationResult.Failure("missing-signature", "The update manifest is missing its RSA signature or public key.");
        }

        byte[] publicKey;
        byte[] signatureBytes;
        try
        {
            publicKey = Convert.FromBase64String(signature.PublicKeySpkiBase64);
            signatureBytes = Convert.FromBase64String(signature.SignatureBase64);
        }
        catch (FormatException)
        {
            return UpdateVerificationResult.Failure("malformed-signature", "The update signature was not valid base64.");
        }

        var embeddedKeyHash = PublisherKeyHash(publicKey);
        if (embeddedKeyHash is null)
        {
            return UpdateVerificationResult.Failure("malformed-signature", "The update signing key was not a valid RSA public key.");
        }

        if (!string.Equals(embeddedKeyHash, NormalizeHash(signature.PublisherPublicKeySha256), StringComparison.Ordinal) ||
            !string.Equals(embeddedKeyHash, NormalizeHash(manifest.Signing.PublisherPublicKeySha256), StringComparison.Ordinal))
        {
            return UpdateVerificationResult.Failure("publisher-key-mismatch", "The update publisher key does not match the manifest metadata.");
        }

        if (!string.Equals(embeddedKeyHash, NormalizeHash(pinnedPublisherPublicKeySha256), StringComparison.Ordinal))
        {
            return UpdateVerificationResult.Failure("publisher-pin-mismatch", "The update was not signed by the pinned publisher key.");
        }

        if (!string.Equals(
                (manifest.Signing.PublisherCertificateThumbprint ?? "").Trim(),
                (signature.PublisherCertificateThumbprint ?? "").Trim(),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                (manifest.Signing.PublisherSubject ?? "").Trim(),
                (signature.PublisherSubject ?? "").Trim(),
                StringComparison.Ordinal))
        {
            return UpdateVerificationResult.Failure("publisher-metadata-mismatch", "The manifest publisher metadata disagrees with its signature record.");
        }

        using (var rsa = RSA.Create())
        {
            try
            {
                rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            }
            catch (CryptographicException)
            {
                return UpdateVerificationResult.Failure("malformed-signature", "The update signing key was not a valid RSA public key.");
            }

            if (!rsa.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                return UpdateVerificationResult.Failure("signature-invalid", "The update manifest signature did not verify.");
            }
        }

        if (mode == "production" && (!manifest.Signing.AuthenticodeVerified || !signature.Authenticode))
        {
            return UpdateVerificationResult.Failure("authenticode-required", "A production update manifest must be Authenticode-verified.");
        }

        return new UpdateVerificationResult(true, null, null, manifest, embeddedKeyHash);
    }

    /// <summary>Publisher key pin shared with <c>Get-MuesliPublicKeySha256</c>: lowercase SHA-256 of the SPKI bytes.</summary>
    public static string? PublisherKeyHash(byte[] subjectPublicKeyInfo) =>
        subjectPublicKeyInfo.Length == 0 ? null : HashHex(subjectPublicKeyInfo);

    public static string HashHex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Streams the file so a large update package is never fully buffered in memory.</summary>
    public static string HashFileHex(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string NormalizeHash(string? value)
    {
        var candidate = (value ?? "").Trim().ToLowerInvariant();
        return candidate.Length == 64 && candidate.All(Uri.IsHexDigit) ? candidate : candidate;
    }
}

public sealed record UpdateVerificationResult(
    bool IsVerified,
    string? FailureCode,
    string? FailureMessage,
    UpdateManifest? Manifest,
    string? PublisherPublicKeySha256)
{
    public static UpdateVerificationResult Failure(string code, string message) =>
        new(false, code, message, null, null);
}

/// <summary>
/// Three-part release version comparison for downgrade and minimum-supported-version checks.
/// </summary>
public static class UpdateVersion
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(candidate, out var parsed))
        {
            return false;
        }

        // Compare on Major.Minor.Build only, so assembly "0.3.0.0" equals release "0.3.0".
        version = new Version(
            Math.Max(0, parsed.Major),
            Math.Max(0, parsed.Minor),
            Math.Max(0, parsed.Build));
        return true;
    }

    public static int Compare(string? left, string? right)
    {
        if (!TryParse(left, out var a) || !TryParse(right, out var b))
        {
            return 0;
        }

        return a.CompareTo(b);
    }
}
